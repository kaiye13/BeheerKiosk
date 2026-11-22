using System.Globalization;
using System.Text.Json;
using Boekhouding.Shared;

namespace Boekhouding.Api.Integrations;

/// <summary>
/// Echte Shopify-koppeling (GraphQL Admin API): orders van de webshop én TikTok Shop (TikTok is als
/// verkoopkanaal aan Shopify gekoppeld), met verzendmethode, tracking en verlaten checkouts.
/// </summary>
public class ShopifyClient(ShopifyTokenProvider shopify, ILogger<ShopifyClient> log) : IShopifyClient
{
    // Kosten per pagina binnen de Shopify-limiet houden: 10 orders × 40 lijnen.
    private const string OrdersQuery = """
        query Orders($cursor: String, $q: String!) {
          orders(first: 10, after: $cursor, query: $q, sortKey: UPDATED_AT) {
            pageInfo { hasNextPage endCursor }
            nodes {
              id
              name
              createdAt
              cancelledAt
              displayFinancialStatus
              sourceName
              channelInformation { channelDefinition { channelName handle } app { title } }
              email
              customer { displayName email }
              shippingAddress { name }
              shippingLine { title }
              totalShippingPriceSet { shopMoney { amount } }
              lineItems(first: 40) {
                nodes {
                  sku
                  name
                  currentQuantity
                  variant { id }
                  discountedUnitPriceAfterAllDiscountsSet { shopMoney { amount } }
                }
              }
              fulfillments(first: 5) {
                createdAt
                deliveredAt
                displayStatus
                trackingInfo(first: 1) { company number url }
              }
            }
          }
        }
        """;

    public async Task<IReadOnlyList<ExternalOrder>> GetOrdersSinceAsync(DateTime since, CancellationToken ct)
    {
        var result = new List<ExternalOrder>();
        var filter = $"updated_at:>='{since.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}'";
        string? cursor = null;
        do
        {
            var data = await shopify.GraphQlAsync(OrdersQuery, new { cursor, q = filter }, ct);
            if (shopify.LastPartialErrors is { } partial)
                log.LogWarning("Shopify orders deels zonder gegevens: {Errors}", partial);
            var orders = data.GetProperty("orders");
            foreach (var node in orders.GetProperty("nodes").EnumerateArray())
                result.Add(MapOrder(node));
            var page = orders.GetProperty("pageInfo");
            cursor = page.GetProperty("hasNextPage").GetBoolean() ? page.GetProperty("endCursor").GetString() : null;
        } while (cursor is not null);
        return result;
    }

    internal static ExternalOrder MapOrder(JsonElement o)
    {
        var customer = Obj(o, "customer");
        var name = Str(customer, "displayName") ?? Str(Obj(o, "shippingAddress"), "name") ?? "Onbekende klant";
        var email = Str(customer, "email") ?? Str(o, "email");

        var lines = new List<ExternalOrderLine>();
        foreach (var l in o.GetProperty("lineItems").GetProperty("nodes").EnumerateArray())
        {
            var qty = l.GetProperty("currentQuantity").GetInt32();
            if (qty <= 0) continue; // volledig terugbetaald of verwijderd
            var variant = Obj(l, "variant");
            var variantId = Str(variant, "id") is { } vid ? ShopifyTokenProvider.IdOf(vid) : (long?)null;
            var sku = Str(l, "sku");
            lines.Add(new ExternalOrderLine(
                string.IsNullOrWhiteSpace(sku) ? (variantId is { } v ? $"SHOP-{v}" : "ZONDER-SKU") : sku,
                Str(l, "name") ?? "",
                qty,
                Money(l, "discountedUnitPriceAfterAllDiscountsSet"),
                variantId));
        }

        return new ExternalOrder(
            IsTikTok(o) ? SalesChannel.TikTokShop : SalesChannel.Shopify,
            o.GetProperty("id").GetString()!,
            Str(o, "name") ?? "",
            DateTimeOffset.Parse(o.GetProperty("createdAt").GetString()!, CultureInfo.InvariantCulture).LocalDateTime,
            name,
            email,
            Money(o, "totalShippingPriceSet"),
            Str(o, "cancelledAt") is not null ? "cancelled" : (Str(o, "displayFinancialStatus") ?? "paid").ToLowerInvariant(),
            lines,
            Str(Obj(o, "shippingLine"), "title"),
            MapFulfillment(o));
    }

    /// <summary>TikTok Shop-orders herkennen aan het verkoopkanaal / de app die de order aanmaakte.</summary>
    internal static bool IsTikTok(JsonElement o)
    {
        var info = Obj(o, "channelInformation");
        var haystack = string.Join(" ",
            Str(Obj(info, "channelDefinition"), "channelName"),
            Str(Obj(info, "channelDefinition"), "handle"),
            Str(Obj(info, "app"), "title"),
            Str(o, "sourceName"));
        return haystack.Contains("tiktok", StringComparison.OrdinalIgnoreCase);
    }

    private static ExternalFulfillment? MapFulfillment(JsonElement o)
    {
        if (!o.TryGetProperty("fulfillments", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            return null;
        var f = list.EnumerateArray().Last();
        var tracking = f.TryGetProperty("trackingInfo", out var t) && t.GetArrayLength() > 0 ? t[0] : default;
        DateTime? Date(JsonElement e, string p) => Str(e, p) is { } s
            ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture).LocalDateTime : null;
        var status = Str(f, "displayStatus");
        return new ExternalFulfillment(
            Str(tracking, "company"),
            Str(tracking, "number"),
            Str(tracking, "url"),
            status is null ? null : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(status.Replace('_', ' ').ToLowerInvariant()),
            Date(f, "createdAt"),
            Date(f, "deliveredAt") ?? (status == "DELIVERED" ? Date(f, "createdAt") : null));
    }

    public async Task<IReadOnlyList<AbandonedCheckoutDto>> GetAbandonedCheckoutsAsync(DateTime since, CancellationToken ct)
    {
        const string query = """
            query Abandoned($q: String!) {
              abandonedCheckouts(first: 30, query: $q, reverse: true) {
                nodes {
                  id
                  createdAt
                  abandonedCheckoutUrl
                  totalPriceSet { shopMoney { amount } }
                  customer { displayName email }
                  lineItems(first: 10) { nodes { title quantity } }
                }
              }
            }
            """;
        var data = await shopify.GraphQlAsync(query, new { q = $"created_at:>='{since.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}'" }, ct);
        return data.GetProperty("abandonedCheckouts").GetProperty("nodes").EnumerateArray().Select(n =>
        {
            var c = Obj(n, "customer");
            var items = n.GetProperty("lineItems").GetProperty("nodes").EnumerateArray()
                .Select(l => $"{l.GetProperty("quantity").GetInt32()}× {Str(l, "title")}");
            return new AbandonedCheckoutDto(
                n.GetProperty("id").GetString()!,
                DateTimeOffset.Parse(n.GetProperty("createdAt").GetString()!, CultureInfo.InvariantCulture).LocalDateTime,
                Str(c, "displayName"), Str(c, "email"),
                Money(n, "totalPriceSet"),
                string.Join(", ", items),
                Str(n, "abandonedCheckoutUrl"));
        }).ToList();
    }

    // ---------- JSON-hulpjes (velden kunnen null of afgeschermd zijn) ----------

    internal static JsonElement Obj(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    internal static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>MoneyBag-veld (…Set { shopMoney { amount } }) → decimal.</summary>
    internal static decimal Money(JsonElement e, string prop)
    {
        var amount = Str(Obj(Obj(e, prop), "shopMoney"), "amount");
        return amount is null ? 0m : decimal.Parse(amount, CultureInfo.InvariantCulture);
    }
}
