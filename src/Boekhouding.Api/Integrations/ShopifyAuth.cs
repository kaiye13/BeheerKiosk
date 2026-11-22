using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Boekhouding.Api.Integrations;

/// <summary>
/// Levert een Shopify Admin API-token.
/// - Vaste token (shpat_…, oude "custom app" uit de Shopify-admin): wordt rechtstreeks gebruikt.
/// - Anders client ID + client secret (shpss_…) van een Dev Dashboard-app: "client credentials grant",
///   token is 24 uur geldig en wordt automatisch vernieuwd. Werkt enkel als app en winkel in dezelfde
///   Shopify-organisatie zitten en de app op de winkel geïnstalleerd is.
/// </summary>
public class ShopifyTokenProvider(HttpClient http, IOptions<IntegrationOptions> options)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTime _expiresAt;

    public string? GrantedScopes { get; private set; }

    private IntegrationOptions O => options.Value;

    public string ShopDomain =>
        (O.ShopifyShopDomain ?? "").Replace("https://", "").Replace("http://", "").Trim('/') is { Length: > 0 } d
            ? d
            : throw new InvalidOperationException("Integrations:ShopifyShopDomain is niet ingesteld (bv. mptfwi-9e.myshopify.com).");

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(O.ShopifyAccessToken)) return O.ShopifyAccessToken;
        if (string.IsNullOrWhiteSpace(O.ShopifyClientId) || string.IsNullOrWhiteSpace(O.ShopifyClientSecret))
            throw new InvalidOperationException("Vul ShopifyClientId + ShopifyClientSecret (of een ShopifyAccessToken) in.");

        await _lock.WaitAsync(ct);
        try
        {
            // 60 s marge zodat een token niet halverwege een sync vervalt.
            if (_token is not null && DateTime.UtcNow < _expiresAt.AddSeconds(-60)) return _token;

            using var res = await http.PostAsync($"https://{ShopDomain}/admin/oauth/access_token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = O.ShopifyClientId!,
                    ["client_secret"] = O.ShopifyClientSecret!,
                }), ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    body.Contains("app_not_installed") ? "Shopify: de app is (nog) niet geïnstalleerd op de winkel. Installeer ze via de Dev Dashboard."
                    : body.Contains("shop_not_permitted") ? "Shopify: de app en de winkel zitten niet in dezelfde Shopify-organisatie."
                    : body.Contains("invalid_client") ? "Shopify: Client ID of Client secret klopt niet."
                    : $"Shopify-token aanvragen mislukt ({(int)res.StatusCode}): {ShortError(body)}");

            var token = JsonSerializer.Deserialize<TokenResponse>(body)!;
            _token = token.AccessToken;
            _expiresAt = DateTime.UtcNow.AddSeconds(token.ExpiresIn);
            GrantedScopes = token.Scope;
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Fouten bij de laatste aanroep die het resultaat niet blokkeerden (bv. afgeschermde klantgegevens).</summary>
    public string? LastPartialErrors { get; private set; }

    /// <summary>
    /// GraphQL Admin API-aanroep; geeft het "data"-object terug.
    /// Bij rate limiting (THROTTLED / 429) wordt even gewacht en opnieuw geprobeerd.
    /// Gedeeltelijke fouten (data + errors) worden onthouden in <see cref="LastPartialErrors"/> i.p.v. alles te laten falen.
    /// </summary>
    public async Task<JsonElement> GraphQlAsync(string query, object? variables, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{ShopDomain}/admin/api/{O.ShopifyApiVersion}/graphql.json")
            {
                Content = JsonContent.Create(new { query, variables }),
            };
            req.Headers.Add("X-Shopify-Access-Token", await GetTokenAsync(ct));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            var throttled = (int)res.StatusCode == 429 || body.Contains("\"THROTTLED\"");
            if (throttled && attempt < 6)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                continue;
            }
            if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Shopify API-fout ({(int)res.StatusCode}): {ShortError(body)}");

            using var doc = JsonDocument.Parse(body);
            var hasData = doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object;
            LastPartialErrors = null;
            if (doc.RootElement.TryGetProperty("errors", out var errors))
            {
                var messages = string.Join(" | ", errors.EnumerateArray()
                    .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : e.ToString()).Distinct());
                if (!hasData) throw new InvalidOperationException($"Shopify GraphQL-fout: {messages}");
                LastPartialErrors = messages;
            }
            return data.Clone();
        }
    }

    /// <summary>
    /// Haalt het resultaat van een mutation op. Is het leeg (Shopify weigerde, bv. ontbrekend recht), dan een fout met
    /// de echte reden van Shopify in plaats van een vage JSON-fout.
    /// </summary>
    public JsonElement Payload(JsonElement data, string field, string action)
    {
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(field, out var p) && p.ValueKind == JsonValueKind.Object)
        {
            ThrowOnUserErrors(p, action);
            return p;
        }
        var reason = LastPartialErrors ?? "geen antwoord van Shopify";
        if (reason.Contains("access denied", StringComparison.OrdinalIgnoreCase) || reason.Contains("not approved", StringComparison.OrdinalIgnoreCase))
        {
            // Rechten zijn misschien net aangepast: volgende aanroep een nieuwe token (met de nieuwe scopes) vragen.
            _token = null;
            reason += " → de app mist hiervoor een recht (scope) in de Shopify Dev Dashboard.";
        }
        throw new InvalidOperationException($"Shopify {action}: {reason}");
    }

    /// <summary>Rechten die de koppeling nodig heeft.</summary>
    public static readonly string[] RequiredScopes =
    [
        "read_orders", "read_all_orders", "read_customers", "read_products", "write_products",
        "read_fulfillments", "read_inventory", "write_inventory", "read_locations",
    ];

    /// <summary>Ontbrekende rechten t.o.v. <see cref="RequiredScopes"/> (write_x geeft ook read_x).</summary>
    public IReadOnlyList<string> MissingScopes()
    {
        var granted = (GrantedScopes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        return RequiredScopes.Where(s => !granted.Contains(s) && !(s.StartsWith("read_") && granted.Contains("write_" + s[5..]))).ToList();
    }

    /// <summary>Gooit een leesbare fout als een mutation userErrors teruggeeft.</summary>
    public static void ThrowOnUserErrors(JsonElement payload, string action)
    {
        if (payload.TryGetProperty("userErrors", out var errs) && errs.GetArrayLength() > 0)
            throw new InvalidOperationException($"Shopify {action}: " + string.Join("; ", errs.EnumerateArray().Select(e => e.GetProperty("message").GetString())));
    }

    /// <summary>"gid://shopify/Product/123" → 123</summary>
    public static long IdOf(string gid) => long.Parse(gid[(gid.LastIndexOf('/') + 1)..]);

    /// <summary>Shopify geeft fouten soms als volledige HTML-pagina terug: enkel de kern tonen.</summary>
    private static string ShortError(string body)
    {
        var m = System.Text.RegularExpressions.Regex.Match(body, @"Oauth error [^<]+");
        if (m.Success) return m.Value.Trim();
        return body.Length > 300 ? body[..300] + "…" : body;
    }

    private record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
