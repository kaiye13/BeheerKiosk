using System.Globalization;
using System.Text.Json;
using Boekhouding.Api.Data;
using Boekhouding.Api.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Boekhouding.Api.Services;

/// <summary>
/// Producten tussen de boekhouding en Shopify:
/// - ophalen: elke Shopify-variant wordt één product (SKU) in de boekhouding;
/// - pushen: nieuw product aanmaken in Shopify (standaard als concept) of bestaand bijwerken;
/// - voorraad: bij een nieuwe aankoop de beschikbare voorraad in Shopify verhogen.
/// </summary>
public class ShopifyProductService(
    AppDbContext db,
    ShopifyTokenProvider shopify,
    IOptions<IntegrationOptions> options,
    ILogger<ShopifyProductService> log)
{
    private static string? _locationId;

    /// <summary>Echte Shopify-koppeling actief (niet in dummy-modus en sleutels ingevuld).</summary>
    public bool IsEnabled =>
        !options.Value.UseDummyData
        && !string.IsNullOrWhiteSpace(options.Value.ShopifyShopDomain)
        && (!string.IsNullOrWhiteSpace(options.Value.ShopifyAccessToken)
            || (!string.IsNullOrWhiteSpace(options.Value.ShopifyClientId) && !string.IsNullOrWhiteSpace(options.Value.ShopifyClientSecret)));

    // ---------- Ophalen ----------

    private const string ProductsQuery = """
        query Products($cursor: String) {
          products(first: 25, after: $cursor) {
            pageInfo { hasNextPage endCursor }
            nodes {
              id title descriptionHtml vendor productType tags status
              variants(first: 30) {
                nodes {
                  id title sku barcode price compareAtPrice
                  inventoryItem { id requiresShipping measurement { weight { value unit } } }
                }
              }
            }
          }
        }
        """;

    /// <summary>Haalt alle producten op en werkt de boekhouding bij. Geeft het aantal nieuwe/gewijzigde producten terug.</summary>
    public async Task<int> ImportAsync(CancellationToken ct = default)
    {
        var all = await db.Products.ToListAsync(ct);
        var byVariant = all.Where(p => p.ShopifyVariantId is not null).ToDictionary(p => p.ShopifyVariantId!.Value);
        var bySku = all.GroupBy(p => p.Sku, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var changed = 0;
        string? cursor = null;
        do
        {
            var data = await shopify.GraphQlAsync(ProductsQuery, new { cursor }, ct);
            var products = data.GetProperty("products");
            foreach (var p in products.GetProperty("nodes").EnumerateArray())
            {
                var variants = p.GetProperty("variants").GetProperty("nodes").EnumerateArray().ToList();
                foreach (var v in variants)
                {
                    var variantId = ShopifyTokenProvider.IdOf(v.GetProperty("id").GetString()!);
                    var sku = ShopifyClient.Str(v, "sku")?.Trim();
                    var product = byVariant.GetValueOrDefault(variantId)
                                  ?? (sku is { Length: > 0 } && bySku.TryGetValue(sku, out var s) && s.ShopifyVariantId is null ? s : null);
                    if (product is null)
                    {
                        product = new Product { Sku = UniqueSku(sku, variantId, bySku) };
                        db.Products.Add(product);
                        bySku[product.Sku] = product;
                    }
                    else if (sku is { Length: > 0 } && !sku.Equals(product.Sku, StringComparison.OrdinalIgnoreCase) && !bySku.ContainsKey(sku))
                    {
                        // SKU in Shopify aangepast: overnemen.
                        bySku.Remove(product.Sku);
                        product.Sku = sku;
                        bySku[sku] = product;
                    }
                    byVariant[variantId] = product;

                    var variantTitle = ShopifyClient.Str(v, "title");
                    var before = Fingerprint(product);
                    product.Title = variants.Count > 1 && variantTitle is not null && variantTitle != "Default Title"
                        ? $"{ShopifyClient.Str(p, "title")} – {variantTitle}" : ShopifyClient.Str(p, "title") ?? product.Title;
                    product.Description = ShopifyClient.Str(p, "descriptionHtml");
                    product.Vendor = ShopifyClient.Str(p, "vendor");
                    product.ProductType = ShopifyClient.Str(p, "productType");
                    product.Tags = string.Join(", ", p.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
                    product.Status = (ShopifyClient.Str(p, "status") ?? "draft").ToLowerInvariant();
                    product.Price = Dec(ShopifyClient.Str(v, "price")) ?? 0m;
                    product.CompareAtPrice = Dec(ShopifyClient.Str(v, "compareAtPrice"));
                    product.Barcode = ShopifyClient.Str(v, "barcode");
                    product.WeightGrams = Grams(ShopifyClient.Obj(ShopifyClient.Obj(ShopifyClient.Obj(v, "inventoryItem"), "measurement"), "weight"));
                    product.ShopifyProductId = ShopifyTokenProvider.IdOf(p.GetProperty("id").GetString()!);
                    product.ShopifyVariantId = variantId;
                    if (ShopifyClient.Obj(v, "inventoryItem") is { ValueKind: JsonValueKind.Object } invItem && invItem.TryGetProperty("requiresShipping", out var rs) && rs.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        product.RequiresShipping = rs.GetBoolean();
                        if (!product.RequiresShipping) product.TrackStock = false;
                    }
                    product.ShopifyInventoryItemId = ShopifyClient.Str(ShopifyClient.Obj(v, "inventoryItem"), "id") is { } inv ? ShopifyTokenProvider.IdOf(inv) : null;
                    product.ShopifySyncedAt = DateTime.Now;
                    if (before != Fingerprint(product)) changed++;
                }
            }
            var page = products.GetProperty("pageInfo");
            cursor = page.GetProperty("hasNextPage").GetBoolean() ? page.GetProperty("endCursor").GetString() : null;
        } while (cursor is not null);

        await db.SaveChangesAsync(ct);
        return changed;
    }

    private static string Fingerprint(Product p) =>
        string.Join('|', p.Sku, p.Title, p.Description, p.Vendor, p.ProductType, p.Tags, p.Status, p.Price, p.CompareAtPrice, p.Barcode, p.WeightGrams, p.ShopifyVariantId);

    private static string UniqueSku(string? sku, long variantId, Dictionary<string, Product> bySku) =>
        sku is { Length: > 0 } && !bySku.ContainsKey(sku) ? sku : $"SHOP-{variantId}";

    private static decimal? Dec(string? s) => s is null ? null : decimal.Parse(s, CultureInfo.InvariantCulture);

    private static int? Grams(JsonElement weight)
    {
        if (weight.ValueKind != JsonValueKind.Object || !weight.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Number) return null;
        var value = v.GetDouble();
        var g = ShopifyClient.Str(weight, "unit") switch
        {
            "KILOGRAMS" => value * 1000,
            "POUNDS" => value * 453.592,
            "OUNCES" => value * 28.3495,
            _ => value,
        };
        return g > 0 ? (int)Math.Round(g) : null;
    }

    // ---------- Pushen ----------

    /// <summary>
    /// Maakt het product aan in Shopify (als het nog niet gekoppeld is) of werkt het bij.
    /// Bij aanmaken krijgt het in Shopify meteen de voorraad die de boekhouding al heeft.
    /// Geeft het aantal stuks terug dat aan de Shopify-voorraad toegevoegd werd.
    /// </summary>
    public async Task<int> PushAsync(Product p, CancellationToken ct = default)
    {
        var created = p.ShopifyProductId is null;
        if (created) await CreateAsync(p, ct);
        else await UpdateAsync(p, ct);
        p.ShopifySyncedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);

        if (!created || !p.TrackStock || !options.Value.ShopifyAdjustInventory) return 0;
        var stock = await db.StockLots.Where(l => l.ProductId == p.Id).SumAsync(l => l.QuantityRemaining, ct);
        if (stock > 0) await AdjustInventoryAsync(p, stock, ct);
        return stock;
    }

    private static string Money(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    private static string ShopifyStatus(string status) => status.ToLowerInvariant() switch { "active" => "ACTIVE", "archived" => "ARCHIVED", _ => "DRAFT" };
    private static string[] TagList(string? tags) => (tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Digitaal product: geen verzending, geen voorraad, geen gewicht.
    private object InventoryItem(Product p) => p.RequiresShipping && p.WeightGrams is { } w
        ? new { sku = p.Sku, tracked = p.TrackStock, requiresShipping = true, measurement = new { weight = new { value = (double)w, unit = "GRAMS" } } }
        : new { sku = p.Sku, tracked = p.TrackStock, requiresShipping = p.RequiresShipping };

    private async Task CreateAsync(Product p, CancellationToken ct)
    {
        const string mutation = """
            mutation Create($input: ProductSetInput!) {
              productSet(input: $input, synchronous: true) {
                product { id variants(first: 1) { nodes { id inventoryItem { id } } } }
                userErrors { field message }
              }
            }
            """;
        var input = new Dictionary<string, object?>
        {
            ["title"] = p.Title,
            ["descriptionHtml"] = p.Description ?? "",
            ["vendor"] = p.Vendor,
            ["productType"] = p.ProductType,
            ["tags"] = TagList(p.Tags),
            ["status"] = ShopifyStatus(p.Status),
            ["productOptions"] = new[] { new { name = "Title", position = 1, values = new[] { new { name = "Default Title" } } } },
            ["variants"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["optionValues"] = new[] { new { optionName = "Title", name = "Default Title" } },
                    ["price"] = Money(p.Price),
                    ["compareAtPrice"] = p.CompareAtPrice is { } c ? Money(c) : null,
                    ["barcode"] = p.Barcode,
                    ["inventoryItem"] = InventoryItem(p),
                },
            },
        };
        var data = await shopify.GraphQlAsync(mutation, new { input }, ct);
        var payload = shopify.Payload(data, "productSet", "product aanmaken");
        var product = payload.GetProperty("product");
        var variant = product.GetProperty("variants").GetProperty("nodes")[0];
        p.ShopifyProductId = ShopifyTokenProvider.IdOf(product.GetProperty("id").GetString()!);
        p.ShopifyVariantId = ShopifyTokenProvider.IdOf(variant.GetProperty("id").GetString()!);
        p.ShopifyInventoryItemId = ShopifyTokenProvider.IdOf(variant.GetProperty("inventoryItem").GetProperty("id").GetString()!);
        log.LogInformation("Product {Sku} aangemaakt in Shopify ({Id})", p.Sku, p.ShopifyProductId);
    }

    private async Task UpdateAsync(Product p, CancellationToken ct)
    {
        // Bewust geen productSet: dat zou andere varianten van hetzelfde Shopify-product verwijderen.
        const string updateProduct = """
            mutation Update($product: ProductUpdateInput!) {
              productUpdate(product: $product) { product { id } userErrors { field message } }
            }
            """;
        const string updateVariant = """
            mutation UpdateVariant($productId: ID!, $variants: [ProductVariantsBulkInput!]!) {
              productVariantsBulkUpdate(productId: $productId, variants: $variants) { productVariants { id } userErrors { field message } }
            }
            """;
        var productGid = $"gid://shopify/Product/{p.ShopifyProductId}";
        var isMultiVariant = p.Title.Contains(" – ") && await db.Products.CountAsync(x => x.ShopifyProductId == p.ShopifyProductId, ct) > 1;

        var product = new Dictionary<string, object?>
        {
            ["id"] = productGid,
            ["descriptionHtml"] = p.Description ?? "",
            ["vendor"] = p.Vendor,
            ["productType"] = p.ProductType,
            ["tags"] = TagList(p.Tags),
            ["status"] = ShopifyStatus(p.Status),
        };
        // Bij producten met meerdere varianten is de titel hier "Product – Variant": productnaam dan niet overschrijven.
        if (!isMultiVariant) product["title"] = p.Title;
        shopify.Payload(await shopify.GraphQlAsync(updateProduct, new { product }, ct), "productUpdate", "product bijwerken");

        if (p.ShopifyVariantId is { } variantId)
        {
            var variant = new Dictionary<string, object?>
            {
                ["id"] = $"gid://shopify/ProductVariant/{variantId}",
                ["price"] = Money(p.Price),
                ["compareAtPrice"] = p.CompareAtPrice is { } c ? Money(c) : null,
                ["barcode"] = p.Barcode,
                ["inventoryItem"] = InventoryItem(p),
            };
            shopify.Payload(await shopify.GraphQlAsync(updateVariant, new { productId = productGid, variants = new[] { variant } }, ct),
                "productVariantsBulkUpdate", "variant bijwerken");
        }
        log.LogInformation("Product {Sku} bijgewerkt in Shopify", p.Sku);
    }

    /// <summary>
    /// Enkel de status in Shopify aanpassen (niets anders overschrijven). De status geldt in Shopify voor het hele product,
    /// dus ook andere varianten van hetzelfde Shopify-product krijgen ze in de boekhouding.
    /// </summary>
    public async Task SetStatusAsync(Product p, CancellationToken ct = default)
    {
        if (p.ShopifyProductId is not { } productId) return;
        const string mutation = """
            mutation Status($product: ProductUpdateInput!) {
              productUpdate(product: $product) { product { id } userErrors { field message } }
            }
            """;
        shopify.Payload(await shopify.GraphQlAsync(mutation,
            new { product = new { id = $"gid://shopify/Product/{productId}", status = ShopifyStatus(p.Status) } }, ct),
            "productUpdate", "status aanpassen");
        foreach (var sibling in await db.Products.Where(x => x.ShopifyProductId == productId && x.Id != p.Id).ToListAsync(ct))
            sibling.Status = p.Status;
        p.ShopifySyncedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);
    }

    // ---------- Voorraad ----------

    /// <summary>Verhoogt (of verlaagt) de beschikbare voorraad in Shopify met <paramref name="delta"/> stuks.</summary>
    public async Task AdjustInventoryAsync(Product p, int delta, CancellationToken ct = default)
    {
        if (delta == 0) return;
        if (p.ShopifyInventoryItemId is not { } itemId) throw new InvalidOperationException($"{p.Sku} is niet gekoppeld aan Shopify.");
        var locationId = await LocationIdAsync(ct);
        const string mutation = """
            mutation Adjust($input: InventoryAdjustQuantitiesInput!) {
              inventoryAdjustQuantities(input: $input) { userErrors { field message } }
            }
            """;
        var input = new
        {
            reason = "received",
            name = "available",
            changes = new[] { new { delta, inventoryItemId = $"gid://shopify/InventoryItem/{itemId}", locationId } },
        };
        shopify.Payload(await shopify.GraphQlAsync(mutation, new { input }, ct), "inventoryAdjustQuantities", "voorraad aanpassen");
        log.LogInformation("Shopify-voorraad {Sku} {Delta:+0;-0}", p.Sku, delta);
    }

    private async Task<string> LocationIdAsync(CancellationToken ct)
    {
        if (_locationId is not null) return _locationId;
        var data = await shopify.GraphQlAsync("{ locations(first: 20) { nodes { id name isActive } } }", null, ct);
        var locations = shopify.Payload(data, "locations", "locaties ophalen").GetProperty("nodes").EnumerateArray().Where(l => l.GetProperty("isActive").GetBoolean()).ToList();
        var wanted = options.Value.ShopifyLocationName;
        var location = (string.IsNullOrWhiteSpace(wanted)
                           ? locations.FirstOrDefault()
                           : locations.FirstOrDefault(l => string.Equals(l.GetProperty("name").GetString(), wanted, StringComparison.OrdinalIgnoreCase)));
        if (location.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(wanted) ? "Geen actieve Shopify-locatie gevonden." : $"Shopify-locatie '{wanted}' niet gevonden.");
        return _locationId = location.GetProperty("id").GetString()!;
    }

    /// <summary>
    /// Na een aankoop: nieuwe producten aanmaken in Shopify (als concept) en de voorraad verhogen met de ingekochte aantallen.
    /// Fouten breken de aankoop niet af; ze worden als melding teruggegeven.
    /// </summary>
    public async Task<List<string>> SyncAfterPurchaseAsync(IEnumerable<StockLot> lots, CancellationToken ct = default)
    {
        var messages = new List<string>();
        if (!IsEnabled) return messages;
        foreach (var group in lots.GroupBy(l => l.Product))
        {
            var p = group.Key;
            var qty = group.Sum(l => l.QuantityIn);
            try
            {
                if (p.ShopifyProductId is null)
                {
                    // Nieuw in Shopify: krijgt meteen de volledige voorraad uit de boekhouding (incl. deze aankoop).
                    var added = await PushAsync(p, ct);
                    messages.Add($"{p.Sku}: aangemaakt in Shopify ({p.Status}){(added > 0 ? $", voorraad {added}" : "")}.");
                    continue;
                }
                if (options.Value.ShopifyAdjustInventory) await AdjustInventoryAsync(p, qty, ct);
                messages.Add(options.Value.ShopifyAdjustInventory ? $"{p.Sku}: Shopify-voorraad +{qty}." : $"{p.Sku}: al in Shopify.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                messages.Add($"⚠ {p.Sku}: Shopify niet bijgewerkt – {ex.Message}");
            }
        }
        return messages;
    }
}
