using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Boekhouding.Api.Data;
using Boekhouding.Shared;
using Microsoft.Extensions.Options;

namespace Boekhouding.Api.Integrations;

/// <summary>Orders rechtstreeks uit TikTok Shop (alle geautoriseerde winkels, bv. BE en NL).</summary>
public interface ITikTokShopClient
{
    bool IsConfigured { get; }
    /// <summary>Orders die sinds <paramref name="since"/> aangemaakt of gewijzigd zijn (niet-betaalde niet).</summary>
    Task<IReadOnlyList<ExternalOrder>> GetOrdersSinceAsync(DateTime since, CancellationToken ct);
}

public class NotConfiguredTikTokShopClient : ITikTokShopClient
{
    public bool IsConfigured => false;
    public Task<IReadOnlyList<ExternalOrder>> GetOrdersSinceAsync(DateTime since, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ExternalOrder>>([]);
}

/// <summary>
/// TikTok Shop Open API (versie 202309): ondertekende aanvragen (HMAC-SHA256), tokens uit een eigen bestand
/// (Integrations:TikTokTokensFile, standaard tiktok-tokens.json in de datamap) die automatisch vernieuwd worden.
/// Het tokens-bestand is een kopie van dat van de rip-wachtrij: elke app vernieuwt zijn eigen kopie.
/// </summary>
public class TikTokShopClient(HttpClient http, IOptions<IntegrationOptions> options, AppPaths paths, ILogger<TikTokShopClient> log)
    : ITikTokShopClient
{
    private const string Base = "https://open-api.tiktokglobalshop.com";
    private const string AuthBase = "https://auth.tiktok-shops.com";
    private const int ExpiredToken = 105002;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private JsonObject? _tokens;
    private List<(string Name, string Cipher)>? _shops;

    private IntegrationOptions O => options.Value;
    private string AppKey => (O.TikTokAppKey ?? "").Trim();
    private string AppSecret => (O.TikTokAppSecret ?? "").Trim();
    private string TokensFile => string.IsNullOrWhiteSpace(O.TikTokTokensFile)
        ? Path.Combine(paths.DataDirectory, "tiktok-tokens.json")
        : Environment.ExpandEnvironmentVariables(O.TikTokTokensFile);

    public bool IsConfigured => AppKey.Length > 0 && AppSecret.Length > 0 && EnsureTokensFile();

    /// <summary>
    /// Nieuwe pc: staat tiktok-tokens.json naast de exe maar nog niet in de datamap, dan wordt het één keer overgenomen
    /// (daarna vernieuwt de server zijn eigen kopie in de datamap).
    /// </summary>
    private bool EnsureTokensFile()
    {
        if (File.Exists(TokensFile)) return true;
        var besideExe = Path.Combine(AppContext.BaseDirectory, "tiktok-tokens.json");
        if (!File.Exists(besideExe)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TokensFile)!);
            File.Copy(besideExe, TokensFile);
            log.LogInformation("TikTok-tokens overgenomen van {Source}", besideExe);
            return true;
        }
        catch (IOException)
        {
            return File.Exists(TokensFile);
        }
    }

    public async Task<IReadOnlyList<ExternalOrder>> GetOrdersSinceAsync(DateTime since, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _shops ??= await GetShopsAsync(ct);
            var result = new List<ExternalOrder>();
            var sinceUnix = new DateTimeOffset(since).ToUnixTimeSeconds();
            foreach (var shop in _shops)
            {
                var pageToken = "";
                var seen = new HashSet<string>();
                do
                {
                    var query = new Dictionary<string, string> { ["page_size"] = "50", ["sort_field"] = "update_time", ["sort_order"] = "ASC" };
                    if (pageToken.Length > 0) query["page_token"] = pageToken;
                    var data = await RequestAsync("/order/202309/orders/search", query, new { update_time_ge = sinceUnix }, shop.Cipher, ct);
                    if (data.TryGetProperty("orders", out var orders))
                        foreach (var o in orders.EnumerateArray())
                            if (MapOrder(o, shop.Name) is { } mapped) result.Add(mapped);
                    seen.Add(pageToken);
                    pageToken = data.TryGetProperty("next_page_token", out var next) ? next.GetString() ?? "" : "";
                } while (pageToken.Length > 0 && !seen.Contains(pageToken));
            }
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---------- Orders omzetten ----------

    internal static ExternalOrder? MapOrder(JsonElement o, string shopName)
    {
        var status = Str(o, "status") ?? "";
        if (status == "UNPAID") return null;                     // nog niet betaald: (nog) geen verkoop
        var paidTime = Long(o, "paid_time");
        if (paidTime is null && status != "CANCELLED") return null;

        // TikTok geeft één regel per stuk: groeperen per SKU. Prijs = wat de klant betaalt + korting die
        // TikTok zelf draagt (die krijgt de verkoper uitbetaald), dus originele prijs − korting van de verkoper.
        var lines = new List<ExternalOrderLine>();
        if (o.TryGetProperty("line_items", out var items))
        {
            foreach (var g in items.EnumerateArray().GroupBy(li => Str(li, "sku_id") ?? Str(li, "product_name") ?? ""))
            {
                var first = g.First();
                var sku = Str(first, "seller_sku") is { Length: > 0 } s ? s : $"TT-{Str(first, "sku_id")}";
                var title = string.Join(" – ", new[] { Str(first, "product_name"), Str(first, "sku_name") }
                    .Where(t => !string.IsNullOrWhiteSpace(t) && t != "Default"));
                var unit = g.Average(li => (Money(li, "original_price") ?? Money(li, "sale_price") ?? 0m) - (Money(li, "seller_discount") ?? 0m));
                lines.Add(new ExternalOrderLine(sku, title, g.Count(), Math.Round(unit, 2)));
            }
        }

        var payment = o.TryGetProperty("payment", out var p) ? p : default;
        var shipping = (Money(payment, "original_shipping_fee") is { } orig ? orig - (Money(payment, "shipping_fee_seller_discount") ?? 0m) : Money(payment, "shipping_fee")) ?? 0m;

        var recipient = o.TryGetProperty("recipient_address", out var r) ? r : default;
        var name = Str(recipient, "name") is { Length: > 0 } n ? n : "TikTok-klant";
        var email = Str(o, "buyer_email");

        var created = DateTimeOffset.FromUnixTimeSeconds(paidTime ?? Long(o, "create_time") ?? 0).LocalDateTime;
        var financial = status == "CANCELLED" ? "cancelled" : "paid";

        var tracking = Str(o, "tracking_number");
        var fulfillmentStatus = status switch
        {
            "IN_TRANSIT" or "AWAITING_COLLECTION" => "IN_TRANSIT",
            "DELIVERED" or "COMPLETED" => "DELIVERED",
            _ => null,
        };
        var fulfillment = tracking is null && fulfillmentStatus is null ? null : new ExternalFulfillment(
            Str(o, "shipping_provider"), tracking, null, fulfillmentStatus,
            Long(o, "collection_time") is { } c ? DateTimeOffset.FromUnixTimeSeconds(c).LocalDateTime : null,
            Long(o, "delivery_time") is { } d ? DateTimeOffset.FromUnixTimeSeconds(d).LocalDateTime : null);

        var method = Str(o, "delivery_option_name") ?? Str(o, "shipping_type");
        return new ExternalOrder(SalesChannel.TikTokShop, Str(o, "id")!, $"TT {Str(o, "id")}", created, name, email,
            shipping, financial, lines, method is null ? $"TikTok ({shopName})" : $"{method} (TikTok {shopName})", fulfillment);
    }

    // ---------- API ----------

    private async Task<List<(string, string)>> GetShopsAsync(CancellationToken ct)
    {
        var data = await RequestAsync("/authorization/202309/shops", new(), null, null, ct);
        var shops = data.GetProperty("shops").EnumerateArray()
            .Select(s => (Str(s, "name") ?? Str(s, "id") ?? "?", Str(s, "cipher") ?? "")).ToList();
        if (shops.Count == 0) throw new InvalidOperationException("TikTok: geen geautoriseerde winkels gevonden.");
        log.LogInformation("TikTok Shop: {Count} winkel(s): {Names}", shops.Count, string.Join(", ", shops.Select(s => s.Item1)));
        return shops;
    }

    private async Task<JsonElement> RequestAsync(string path, Dictionary<string, string> query, object? body, string? shopCipher,
        CancellationToken ct, bool retried = false)
    {
        var tokens = await TokensAsync(ct);
        var parameters = new Dictionary<string, string>(query)
        {
            ["app_key"] = AppKey,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["version"] = "202309",
        };
        if (shopCipher is not null) parameters["shop_cipher"] = shopCipher;
        var bodyJson = body is null ? "" : JsonSerializer.Serialize(body);
        parameters["sign"] = Sign(path, parameters, bodyJson);

        var url = $"{Base}{path}?{string.Join("&", parameters.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"))}";
        using var req = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
        req.Headers.Add("x-tts-access-token", tokens["access_token"]!.GetValue<string>());
        if (body is not null) req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text.Length > 0 ? text : "{}");
        var code = doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : -1;
        if (code == ExpiredToken && !retried)
        {
            await RefreshAsync(ct);
            return await RequestAsync(path, query, body, shopCipher, ct, retried: true);
        }
        if (code != 0)
        {
            var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : $"HTTP {(int)res.StatusCode}";
            throw new InvalidOperationException($"TikTok {path}: {code} {message}");
        }
        return doc.RootElement.GetProperty("data").Clone();
    }

    private string Sign(string path, Dictionary<string, string> parameters, string body)
    {
        var sb = new StringBuilder(AppSecret).Append(path);
        foreach (var (k, v) in parameters.Where(kv => kv.Key is not "sign" and not "access_token").OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(k).Append(v);
        sb.Append(body).Append(AppSecret);
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(AppSecret), Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    // ---------- Tokens ----------

    private async Task<JsonObject> TokensAsync(CancellationToken ct)
    {
        if (_tokens is null)
        {
            if (!File.Exists(TokensFile)) throw new InvalidOperationException($"TikTok-tokens niet gevonden: {TokensFile}");
            var root = JsonNode.Parse(await File.ReadAllTextAsync(TokensFile, ct))!.AsObject();
            _tokens = (root["data"] as JsonObject) ?? root;
        }
        var expires = _tokens["access_token_expire_in"]?.GetValue<long>() ?? 0;
        if (expires - DateTimeOffset.UtcNow.ToUnixTimeSeconds() < 300) await RefreshAsync(ct);
        return _tokens;
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var refresh = _tokens?["refresh_token"]?.GetValue<string>() ?? throw new InvalidOperationException("TikTok: geen refresh token.");
        var url = $"{AuthBase}/api/v2/token/refresh?app_key={Uri.EscapeDataString(AppKey)}&app_secret={Uri.EscapeDataString(AppSecret)}" +
                  $"&refresh_token={Uri.EscapeDataString(refresh)}&grant_type=refresh_token";
        var root = JsonNode.Parse(await http.GetStringAsync(url, ct))!.AsObject();
        if (root["code"]?.GetValue<long>() != 0 || root["data"] is not JsonObject data)
            throw new InvalidOperationException($"TikTok-token vernieuwen mislukt: {root["code"]} {root["message"]}");
        _tokens = (JsonObject)data.DeepClone();
        await File.WriteAllTextAsync(TokensFile, new JsonObject { ["data"] = data.DeepClone() }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
        log.LogInformation("TikTok-token vernieuwd");
    }

    // ---------- Hulp ----------

    private static string? Str(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;

    private static long? Long(JsonElement e, string p) =>
        Str(e, p) is { } s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) && l > 0 ? l : null;

    private static decimal? Money(JsonElement e, string p) =>
        Str(e, p) is { Length: > 0 } s && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
}
