using System.Globalization;
using System.Text.Json;
using Boekhouding.Shared;
using Microsoft.Extensions.Options;

namespace Boekhouding.Api.Integrations;

/// <summary>
/// Billit REST API: inkomende documenten (aankoopfacturen en creditnota's, OrderDirection 'Cost') + hun PDF.
/// Authenticatie met header "ApiKey" (en optioneel "PartyID" als het account meerdere bedrijven heeft).
/// Documentatie: https://docs.billit.be
/// </summary>
public class BillitClient(HttpClient http, IOptions<IntegrationOptions> options, ILogger<BillitClient> log) : IBillitClient
{
    private static readonly HashSet<string> EuCountries =
    [
        "AT", "BE", "BG", "CY", "CZ", "DE", "DK", "EE", "ES", "FI", "FR", "GR", "EL", "HR", "HU", "IE", "IT",
        "LT", "LU", "LV", "MT", "NL", "PL", "PT", "RO", "SE", "SI", "SK",
    ];

    private IntegrationOptions O => options.Value;
    private string BaseUrl => (O.BillitBaseUrl ?? "https://api.billit.be").TrimEnd('/');

    public async Task<IReadOnlyList<ExternalInvoice>> GetIncomingInvoicesSinceAsync(DateTime since, ISet<string>? skipIds, CancellationToken ct)
    {
        var filter = $"OrderDirection eq 'Cost' and LastModified ge DateTime'{since:yyyy-MM-ddTHH:mm:ss}'";
        var url = $"{BaseUrl}/v1/orders?$filter={Uri.EscapeDataString(filter)}&$orderby=LastModified";
        var result = new List<ExternalInvoice>();

        while (url is not null)
        {
            using var doc = await GetJsonAsync(url, ct);
            foreach (var item in doc.RootElement.GetProperty("Items").EnumerateArray())
            {
                var type = Str(item, "OrderType");
                if (type is not ("Invoice" or "CreditNote")) continue;
                var id = $"billit-{Num(item, "OrderID")}";
                if (skipIds?.Contains(id) == true) continue;

                // Detail nodig voor leverancier (btw-nummer, land), factuurnummer en PDF.
                using var detail = await GetJsonAsync($"{BaseUrl}/v1/orders/{Num(item, "OrderID")}", ct);
                result.Add(Map(detail.RootElement, id));
            }
            url = Str(doc.RootElement, "NextPageLink") is { Length: > 0 } next
                ? (next.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? next : BaseUrl + next)
                : null;
        }
        return result;
    }

    internal static ExternalInvoice Map(JsonElement o, string id)
    {
        var supplier = Obj(o, "Supplier") is { ValueKind: JsonValueKind.Object } s ? s : Obj(o, "CounterParty");
        var country = (Str(supplier, "CountryCode") ?? "BE").ToUpperInvariant();
        var net = Dec(o, "TotalExcl");
        var vat = Dec(o, "TotalVAT");
        var creditNote = Str(o, "OrderType") == "CreditNote";
        if (creditNote)
        {
            // Creditnota: altijd negatief in de boekhouding (vermindert kost en aftrekbare btw).
            net = -Math.Abs(net);
            vat = -Math.Abs(vat);
        }

        var vatType = vat != 0 ? PurchaseVatType.Belgian21
            : country != "BE" && EuCountries.Contains(country) ? PurchaseVatType.IntraCommunity0
            : PurchaseVatType.Exempt0;

        static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var number = NonEmpty(Str(o, "OrderNumber")) ?? NonEmpty(Str(o, "OrderTitle")) ?? $"Billit {Num(o, "OrderID")}";
        var date = Str(o, "OrderDate") is { } d ? DateTime.Parse(d, CultureInfo.InvariantCulture).Date : DateTime.Today;
        var description = (creditNote ? "Creditnota" : "Factuur") + " via Billit"
                          + (Str(o, "OrderTitle") is { Length: > 0 } title && title != number ? $" – {title}" : "");

        return new ExternalInvoice(
            id,
            Str(supplier, "Name") ?? Str(supplier, "DisplayName") ?? "Onbekende leverancier",
            Str(supplier, "VATNumber"),
            country,
            number,
            date,
            vatType,
            net,
            vat,
            description,
            Str(Obj(o, "OrderPDF"), "FileID"));
    }

    public async Task<(string FileName, byte[] Content)?> DownloadFileAsync(string fileId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{BaseUrl}/v1/files/{Uri.EscapeDataString(fileId)}", ct);
        var content = Str(doc.RootElement, "FileContent");
        if (string.IsNullOrEmpty(content)) return null;
        return (Str(doc.RootElement, "FileName") ?? $"{fileId}.pdf", Convert.FromBase64String(content));
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(O.BillitApiKey)) throw new InvalidOperationException("Integrations:BillitApiKey is niet ingesteld.");
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("ApiKey", O.BillitApiKey);
            if (!string.IsNullOrWhiteSpace(O.BillitPartyId)) req.Headers.Add("PartyID", O.BillitPartyId);
            req.Headers.Add("Accept", "application/json");
            using var res = await http.SendAsync(req, ct);
            if ((int)res.StatusCode == 429 && attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                continue;
            }
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("Billit {Status} op {Url}", (int)res.StatusCode, url);
                throw new InvalidOperationException((int)res.StatusCode switch
                {
                    401 or 403 => "Billit weigert de API-sleutel (verkeerd, verlopen of geen API-toegang).",
                    _ => $"Billit-fout ({(int)res.StatusCode}): {(body.Length > 200 ? body[..200] + "…" : body)}",
                });
            }
            return JsonDocument.Parse(body);
        }
    }

    // ---------- JSON-hulpjes ----------

    private static JsonElement Obj(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    private static string? Str(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Num(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) ? (v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString() ?? "") : "";

    private static decimal Dec(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
}
