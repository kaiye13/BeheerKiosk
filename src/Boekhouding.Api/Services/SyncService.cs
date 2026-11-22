using Boekhouding.Api.Data;
using Boekhouding.Api.Integrations;
using Boekhouding.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Boekhouding.Api.Services;

/// <summary>Haalt orders (Shopify + TikTok via Shopify, eenmalig WooCommerce) en Billit-facturen op.</summary>
public class SyncService(
    AppDbContext db,
    IShopifyClient shopify,
    ITikTokShopClient tiktok,
    IWooCommerceClient woo,
    IBillitClient billit,
    StockAllocator allocator,
    IOptions<IntegrationOptions> options,
    IOptions<StreamOptions> streamOptions,
    ComplaintService complaints,
    ShopActivityService activity,
    ShopifyProductService products,
    AppPaths paths,
    ILogger<SyncService> log)
{
    public const string ShopifyKey = "shopify";
    public const string BillitKey = "billit";
    public const string WooKey = "woocommerce";
    public const string ProductsKey = "shopify-products";
    public const string GoLiveKey = "go-live";
    public const string TikTokKey = "tiktok";
    private static DateTime _lastTikTokAttempt = DateTime.MinValue;
    /// <summary>Enkel voor tests: TikTok meteen weer laten ophalen.</summary>
    internal static void ResetTikTokThrottle() => _lastTikTokAttempt = DateTime.MinValue;

    // Achtergrondsync en de knop "Synchroniseer nu" mogen niet tegelijk lopen.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _reclassified;

    public async Task<SyncResultDto> RunAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var messages = new List<string>();
            if (!_reclassified)
            {
                var changed = await ReclassifyShippingAsync(ct);
                _reclassified = true;
                if (changed > 0) messages.Add($"{changed} orders kregen een ander verzendtype (instelling SaverShippingKeyword).");
            }
            var now = DateTime.Now;
            int newOrders = 0, newInvoices = 0;

            if (woo.IsConfigured && await GetLastRunAsync(WooKey, ct) is null)
            {
                var wooCount = await TryAsync(messages, "WooCommerce", async () =>
                    await ImportOrdersAsync(await woo.GetAllOrdersAsync(ct), historical: true, ct));
                if (wooCount is not null)
                {
                    await SetLastRunAsync(WooKey, now, ct);
                    messages.Add($"WooCommerce: {wooCount} historische orders geïmporteerd.");
                }
            }

            // Producten uit Shopify: bij de eerste sync en daarna om de 6 uur (prijzen, titels, nieuwe producten).
            if (!options.Value.UseDummyData && (await GetLastRunAsync(ProductsKey, ct) is not { } lastProducts || now - lastProducts > TimeSpan.FromHours(6)))
            {
                var imported = await TryAsync(messages, "Shopify-producten", async () => await products.ImportAsync(ct));
                if (imported is not null)
                {
                    await SetLastRunAsync(ProductsKey, now, ct);
                    await db.SaveChangesAsync(ct);
                    if (imported > 0) messages.Add($"Shopify: {imported} producten nieuw of bijgewerkt.");
                }
            }

            // Volledige sync: orders met 1 dag overlap, als vangnet voor wijzigingen die de snelle ophaling miste.
            newOrders = await SyncOrdersAsync(TimeSpan.FromDays(1), messages, ct) ?? 0;

            if (billit.IsConfigured)
            {
                var billitSince = (await GetLastRunAsync(BillitKey, ct))?.AddDays(-7)
                                  ?? options.Value.BillitImportFrom
                                  ?? (options.Value.UseDummyData ? DateTime.Today.AddMonths(-6) : new DateTime(DateTime.Today.Year, 1, 1));
                var invoiceCount = await TryAsync(messages, "Billit", async () =>
                    await ImportInvoicesAsync(billitSince, ct));
                if (invoiceCount is not null)
                {
                    newInvoices = invoiceCount.Value;
                    await SetLastRunAsync(BillitKey, now, ct);
                }
                var repaired = await TryAsync(messages, "Billit-PDF's", async () => await RestoreMissingPdfsAsync(ct));
                if (repaired > 0) messages.Add($"{repaired} factuur-PDF('s) opnieuw opgehaald bij Billit.");
            }

            await db.SaveChangesAsync(ct);

            var newComplaints = await TryAsync(messages, "Klachtenmailbox", async () => await complaints.ImportAsync(ct));
            if (newComplaints > 0) messages.Add($"{newComplaints} nieuwe klacht(en)/antwoord(en) in de mailbox.");
            if (options.Value.UseDummyData) await activity.SimulateAsync(ct);

            var unallocated = await db.SalesOrderLines.CountAsync(l => l.UnallocatedQuantity > 0 && !l.SalesOrder.IsHistorical && (l.Product == null || l.Product.TrackStock), ct);
            messages.Insert(0, $"{newOrders} nieuwe orders, {newInvoices} nieuwe Billit-facturen.");
            log.LogInformation("Sync klaar: {Orders} orders, {Invoices} facturen", newOrders, newInvoices);
            return new SyncResultDto(now, options.Value.UseDummyData ? "Dummy" : "Live", newOrders, newInvoices, unallocated, messages);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static readonly string[] CancelledStatuses = ["refunded", "voided", "cancelled"];

    /// <param name="historicalBefore">Orders vóór deze datum: omzet wel, voorraad niet.</param>
    private static readonly SemaphoreSlim OrdersGate = new(1, 1);

    /// <summary>
    /// Snelle ophaling: enkel Shopify-orders die sinds de vorige keer gewijzigd zijn (met 2 minuten overlap).
    /// Draait elke paar seconden, los van Billit/producten/mailbox, zodat nieuwe orders bijna meteen in de app staan.
    /// </summary>
    public async Task<int> RunOrdersAsync(CancellationToken ct = default)
    {
        var messages = new List<string>();
        var count = await SyncOrdersAsync(TimeSpan.FromMinutes(2), messages, ct);
        if (options.Value.UseDummyData) await activity.SimulateAsync(ct);
        if (count is null) log.LogWarning("Snelle order-sync: {Messages}", string.Join(" | ", messages));
        return count ?? 0;
    }

    private async Task<int?> SyncOrdersAsync(TimeSpan overlap, List<string> messages, CancellationToken ct)
    {
        await OrdersGate.WaitAsync(ct);
        try
        {
            var fetchStart = DateTime.Now;
            // Orders van vóór de ingebruikname tellen mee in de omzet, maar nemen geen voorraad af
            // (die voorraad werd toen nog niet in deze app bijgehouden).
            var goLive = await GoLiveAsync(fetchStart, ct);
            var since = (await GetLastRunAsync(ShopifyKey, ct))?.Subtract(overlap)
                        ?? options.Value.ShopifyImportFrom
                        ?? (options.Value.UseDummyData ? DateTime.Today.AddDays(-90) : new DateTime(DateTime.Today.Year, 1, 1));
            var count = await TryAsync(messages, "Shopify", async () =>
            {
                var orders = await shopify.GetOrdersSinceAsync(since, ct);
                // TikTok rechtstreeks gekoppeld: TikTok-orders die ook via Shopify binnenkomen overslaan (anders dubbel).
                if (tiktok.IsConfigured) orders = orders.Where(o => o.Channel != SalesChannel.TikTokShop).ToList();
                return await ImportOrdersAsync(orders, historical: false, ct, goLive);
            });
            if (count is not null) await SetLastRunAsync(ShopifyKey, fetchStart, ct);
            await db.SaveChangesAsync(ct);

            if (tiktok.IsConfigured && fetchStart - _lastTikTokAttempt >= TimeSpan.FromSeconds(Math.Max(5, options.Value.TikTokSyncIntervalSeconds)))
            {
                _lastTikTokAttempt = fetchStart;
                var ttSince = (await GetLastRunAsync(TikTokKey, ct))?.AddMinutes(-10)
                              ?? options.Value.TikTokImportFrom ?? options.Value.ShopifyImportFrom ?? new DateTime(DateTime.Today.Year, 1, 1);
                var ttCount = await TryAsync(messages, "TikTok Shop", async () =>
                {
                    var orders = await tiktok.GetOrdersSinceAsync(ttSince, ct);
                    await RelinkShopifyTikTokOrdersAsync(orders, ct);
                    return await ImportOrdersAsync(orders, historical: false, ct, goLive);
                });
                if (ttCount is not null)
                {
                    await SetLastRunAsync(TikTokKey, fetchStart, ct);
                    if (ttCount > 0) log.LogInformation("TikTok Shop: {Count} nieuwe orders", ttCount);
                }
                await db.SaveChangesAsync(ct);
                count = (count ?? 0) + (ttCount ?? 0);
            }
            return count;
        }
        finally
        {
            OrdersGate.Release();
        }
    }

    /// <summary>
    /// TikTok-orders die eerder via Shopify binnenkwamen (Shopify-id) koppelen aan hun TikTok-order, zodat ze niet
    /// dubbel geteld worden: zelfde moment (± 6 u) en zelfde klantnaam of zelfde bedrag. Voorraadtoewijzing blijft.
    /// </summary>
    private async Task RelinkShopifyTikTokOrdersAsync(IReadOnlyList<ExternalOrder> orders, CancellationToken ct)
    {
        var viaShopify = await db.SalesOrders
            .Where(o => o.Channel == SalesChannel.TikTokShop && o.ExternalId.StartsWith("gid://shopify/"))
            .ToListAsync(ct);
        if (viaShopify.Count == 0) return;
        var ids = orders.Select(o => o.ExternalId).ToList();
        var known = (await db.SalesOrders.Where(o => o.Channel == SalesChannel.TikTokShop && ids.Contains(o.ExternalId))
            .Select(o => o.ExternalId).ToListAsync(ct)).ToHashSet();
        static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetter).ToArray());

        foreach (var tt in orders.Where(o => !known.Contains(o.ExternalId)).OrderBy(o => o.OrderDate))
        {
            var total = tt.Lines.Sum(l => l.UnitPriceIncl * l.Quantity) + tt.ShippingIncl;
            var match = viaShopify
                .Where(s => Math.Abs((s.OrderDate - tt.OrderDate).TotalHours) <= 6
                            && (Norm(s.CustomerName) == Norm(tt.CustomerName) || Math.Abs(s.TotalIncl - total) <= 0.10m))
                .OrderBy(s => Math.Abs((s.OrderDate - tt.OrderDate).TotalMinutes))
                .FirstOrDefault();
            if (match is null) continue;
            log.LogInformation("TikTok-order {Tt} gekoppeld aan Shopify-order {Shopify}", tt.ExternalId, match.OrderNumber);
            match.ExternalId = tt.ExternalId;
            match.OrderNumber = $"{tt.OrderNumber} ({match.OrderNumber})";
            viaShopify.Remove(match);
        }

        // TikTok-order staat er al (rechtstreeks binnengekomen) én er kwam nog een kopie via Shopify
        // (bv. toen TikTok even niet gekoppeld was): de kopie weg, anders telt die verkoop dubbel.
        // Strenger dan koppelen: zelfde klant, en binnen 30 minuten óf zelfde bedrag binnen 6 uur
        // (Shopify noteert het bestelmoment, TikTok het betaalmoment: dat kan een uur of meer schelen).
        foreach (var tt in orders.Where(o => known.Contains(o.ExternalId)))
        {
            var ttTotal = tt.Lines.Sum(l => l.UnitPriceIncl * l.Quantity) + tt.ShippingIncl;
            var twin = viaShopify
                .Where(s => Norm(s.CustomerName) == Norm(tt.CustomerName)
                            && (Math.Abs((s.OrderDate - tt.OrderDate).TotalMinutes) <= 30
                                || (Math.Abs((s.OrderDate - tt.OrderDate).TotalHours) <= 6 && Math.Abs(s.TotalIncl - ttTotal) <= 0.10m)))
                .OrderBy(s => Math.Abs((s.OrderDate - tt.OrderDate).TotalMinutes))
                .FirstOrDefault();
            if (twin is null) continue;
            log.LogWarning("Dubbele TikTok-order via Shopify verwijderd: {Shopify} (= TikTok {Tt})", twin.OrderNumber, tt.ExternalId);
            await ReleaseStockAsync(twin, ct);
            foreach (var c in await db.Complaints.Where(c => c.SalesOrderId == twin.Id).ToListAsync(ct)) c.SalesOrderId = null;
            db.SalesOrderLines.RemoveRange(await db.SalesOrderLines.Where(l => l.SalesOrderId == twin.Id).ToListAsync(ct));
            db.SalesOrders.Remove(twin);
            viaShopify.Remove(twin);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> ImportOrdersAsync(IEnumerable<ExternalOrder> orders, bool historical, CancellationToken ct = default,
        DateTime? historicalBefore = null)
    {
        var incoming = orders.ToList();
        var ids = incoming.Select(o => o.ExternalId).ToList();
        var existing = (await db.SalesOrders.Where(o => ids.Contains(o.ExternalId)).ToListAsync(ct))
            .ToDictionary(o => (o.Channel, o.ExternalId));
        var allProducts = await db.Products.ToListAsync(ct);
        var bySku = allProducts.GroupBy(p => p.Sku, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byVariant = allProducts.Where(p => p.ShopifyVariantId is not null).GroupBy(p => p.ShopifyVariantId!.Value).ToDictionary(g => g.Key, g => g.First());
        static string TitleKey(string t) => new(t.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var byTitle = allProducts.GroupBy(p => TitleKey(p.Title)).Where(g => g.Key.Length > 0 && g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First());

        var count = 0;
        // Chronologisch, zodat de oudste verkoop eerst voorraad krijgt.
        foreach (var ext in incoming.OrderBy(o => o.OrderDate))
        {
            if (existing.TryGetValue((ext.Channel, ext.ExternalId), out var known))
            {
                // Bestaande order: status en tracking bijwerken. Geannuleerd/terugbetaald → voorraad terug.
                var wasCancelled = CancelledStatuses.Contains(known.FinancialStatus);
                known.FinancialStatus = ext.FinancialStatus;
                if (!wasCancelled && CancelledStatuses.Contains(ext.FinancialStatus))
                    await ReleaseStockAsync(known, ct);
                ApplyFulfillment(known, ext.Fulfillment);
                continue;
            }

            var isHistorical = historical || (historicalBefore is { } before && ext.OrderDate < before);

            var order = new SalesOrder
            {
                Channel = ext.Channel,
                ExternalId = ext.ExternalId,
                OrderNumber = ext.OrderNumber,
                OrderDate = ext.OrderDate,
                CustomerName = ext.CustomerName,
                CustomerEmail = ext.CustomerEmail,
                ShippingIncl = ext.ShippingIncl,
                FinancialStatus = ext.FinancialStatus,
                IsHistorical = isHistorical,
                TotalIncl = ext.Lines.Sum(l => l.UnitPriceIncl * l.Quantity) + ext.ShippingIncl,
                ShippingMethod = ext.ShippingMethod,
                ShippingKind = ClassifyShipping(ext.ShippingMethod, ext.ShippingIncl, streamOptions.Value.SaverShippingKeyword),
            };
            // Oude webshop: afgehandeld. Shopify-orders van vóór de ingebruikname blijven wel op te volgen.
            order.Stage = ext.Channel == SalesChannel.WooCommerce ? FulfillmentStage.Delivered
                : order.ShippingKind == ShippingKind.Saver ? FulfillmentStage.Saving : FulfillmentStage.New;
            ApplyFulfillment(order, ext.Fulfillment);
            existing[(ext.Channel, ext.ExternalId)] = order;
            var cancelled = CancelledStatuses.Contains(ext.FinancialStatus);
            foreach (var extLine in ext.Lines)
            {
                var product = extLine.VariantId is { } vid && byVariant.TryGetValue(vid, out var pv) ? pv
                    : bySku.GetValueOrDefault(extLine.Sku)
                      // TikTok kent geen eigen SKU's (TT-…): dan op productnaam koppelen, als die uniek is.
                      ?? (extLine.Sku.StartsWith("TT-") ? byTitle.GetValueOrDefault(TitleKey(extLine.Title)) : null);
                var line = new SalesOrderLine
                {
                    Product = product,
                    Sku = extLine.Sku,
                    Title = extLine.Title,
                    Quantity = extLine.Quantity,
                    UnitPriceIncl = extLine.UnitPriceIncl,
                    UnallocatedQuantity = extLine.Quantity,
                };
                order.Lines.Add(line);
                if (product is not null) line.ProductId = product.Id;
                if (!isHistorical && !cancelled && product is { TrackStock: true })
                    await allocator.AllocateAsync(line, ct);
            }
            db.SalesOrders.Add(order);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Verzendtype van bestaande orders opnieuw bepalen met de huidige instellingen (één keer per opstart),
    /// zodat een aangepast trefwoord voor spaarders ook voor oudere orders geldt.
    /// </summary>
    private async Task<int> ReclassifyShippingAsync(CancellationToken ct)
    {
        var changed = 0;
        foreach (var o in await db.SalesOrders.Where(o => o.Channel != SalesChannel.WooCommerce).ToListAsync(ct))
        {
            var kind = ClassifyShipping(o.ShippingMethod, o.ShippingIncl, streamOptions.Value.SaverShippingKeyword);
            if (kind == o.ShippingKind) continue;
            o.ShippingKind = kind;
            if (kind == ShippingKind.Saver && o.Stage == FulfillmentStage.New) o.Stage = FulfillmentStage.Saving;
            else if (kind != ShippingKind.Saver && o.Stage == FulfillmentStage.Saving) o.Stage = FulfillmentStage.New;
            changed++;
        }
        if (changed > 0) await db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>Geannuleerde/terugbetaalde order: toegewezen stuks gaan terug naar hun voorraadpartij.</summary>
    private async Task ReleaseStockAsync(SalesOrder order, CancellationToken ct)
    {
        var allocations = await db.SaleAllocations.Include(a => a.StockLot).Include(a => a.SalesOrderLine)
            .Where(a => a.SalesOrderLine.SalesOrderId == order.Id).ToListAsync(ct);
        foreach (var a in allocations)
        {
            a.StockLot.QuantityRemaining += a.Quantity;
            a.SalesOrderLine.UnallocatedQuantity = 0;
            db.SaleAllocations.Remove(a);
        }
        foreach (var line in await db.SalesOrderLines.Where(l => l.SalesOrderId == order.Id).ToListAsync(ct))
            line.UnallocatedQuantity = 0;
        log.LogInformation("Order {Order} geannuleerd/terugbetaald: {Count} toewijzingen teruggezet", order.OrderNumber, allocations.Count);
    }

    /// <summary>
    /// Datum vanaf wanneer de app de voorraad bijhoudt. Instelbaar (Integrations:GoLiveDate), anders het moment
    /// van de eerste echte synchronisatie. In dummy-modus niet van toepassing.
    /// </summary>
    private async Task<DateTime?> GoLiveAsync(DateTime now, CancellationToken ct)
    {
        if (options.Value.UseDummyData) return null;
        if (options.Value.GoLiveDate is { } configured) return configured;
        if (await GetLastRunAsync(GoLiveKey, ct) is { } stored) return stored;
        await SetLastRunAsync(GoLiveKey, now, ct);
        return now;
    }

    /// <param name="saverKeywords">Komma-gescheiden, bv. "live openen,spaar".</param>
    public static ShippingKind ClassifyShipping(string? method, decimal shippingIncl, string saverKeywords) =>
        method is not null && saverKeywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(k => method.Contains(k, StringComparison.OrdinalIgnoreCase)) ? ShippingKind.Saver
        : shippingIncl > 0 ? ShippingKind.Paid
        : ShippingKind.Free;

    /// <summary>Tracking overnemen en de interne status enkel vooruit zetten (een handmatig "Probleem" blijft staan).</summary>
    private static void ApplyFulfillment(SalesOrder order, ExternalFulfillment? f)
    {
        if (f is null) return;
        order.TrackingCarrier = f.Carrier ?? order.TrackingCarrier;
        order.TrackingNumber = f.TrackingNumber ?? order.TrackingNumber;
        order.TrackingUrl = f.TrackingUrl ?? order.TrackingUrl;
        order.TrackingStatus = f.Status ?? order.TrackingStatus;
        order.ShippedAt ??= f.ShippedAt;
        order.DeliveredAt ??= f.DeliveredAt;
        if (order.Stage == FulfillmentStage.Problem) return;
        var target = f.DeliveredAt is not null ? FulfillmentStage.Delivered : f.ShippedAt is not null ? FulfillmentStage.Shipped : order.Stage;
        if (Rank(target) > Rank(order.Stage)) order.Stage = target;
    }

    private static int Rank(FulfillmentStage s) => s switch
    {
        FulfillmentStage.Saving => 0,
        FulfillmentStage.New => 0,
        FulfillmentStage.Ripped => 1,
        FulfillmentStage.Packed => 2,
        FulfillmentStage.Shipped => 3,
        FulfillmentStage.Delivered => 4,
        _ => -1,
    };

    private async Task<int> ImportInvoicesAsync(DateTime since, CancellationToken ct)
    {
        var existing = (await db.PurchaseInvoices.Where(i => i.ExternalId != null).Select(i => i.ExternalId!).ToListAsync(ct)).ToHashSet();
        var invoices = await billit.GetIncomingInvoicesSinceAsync(since, existing, ct);
        var count = 0;
        foreach (var ext in invoices)
        {
            if (!existing.Add(ext.ExternalId)) continue;
            var invoice = new PurchaseInvoice
            {
                Source = "Billit",
                ExternalId = ext.ExternalId,
                SupplierName = ext.SupplierName,
                SupplierVatNumber = ext.SupplierVatNumber,
                Country = ext.Country,
                InvoiceNumber = ext.InvoiceNumber,
                InvoiceDate = ext.InvoiceDate,
                VatType = ext.VatType,
                NetAmount = ext.NetAmount,
                VatAmount = ext.VatAmount,
                Description = ext.Description,
            };
            await AttachPdfAsync(invoice, ext.PdfFileId, ct);
            db.PurchaseInvoices.Add(invoice);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Billit-facturen waarvan de PDF ontbreekt (bv. op een nieuwe server, of als het ophalen eerder mislukte)
    /// opnieuw bij Billit ophalen. Max. 100 per sync, zodat een grote achterstand gespreid gebeurt.
    /// </summary>
    private async Task<int> RestoreMissingPdfsAsync(CancellationToken ct)
    {
        var candidates = await db.PurchaseInvoices
            .Where(i => i.Source == "Billit" && i.ExternalId != null)
            .OrderByDescending(i => i.InvoiceDate)
            .ToListAsync(ct);
        var missing = candidates
            .Where(i => i.StoredFileName is null || !File.Exists(Path.Combine(paths.UploadDirectory, i.StoredFileName)))
            .Take(100)
            .ToList();
        var restored = 0;
        foreach (var invoice in missing)
        {
            var hadFile = invoice.StoredFileName;
            if (await billit.GetPdfFileIdAsync(invoice.ExternalId!, ct) is not { } fileId) continue;
            await AttachPdfAsync(invoice, fileId, ct);
            if (invoice.StoredFileName != hadFile) restored++;
        }
        if (restored > 0)
        {
            await db.SaveChangesAsync(ct);
            log.LogInformation("{Count} Billit-PDF's opnieuw opgehaald", restored);
        }
        return restored;
    }

    /// <summary>PDF van Billit ophalen en bewaren bij de factuur. Lukt dat niet, dan blijft de factuur zonder bestand.</summary>
    private async Task AttachPdfAsync(PurchaseInvoice invoice, string? fileId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(fileId)) return;
        try
        {
            if (await billit.DownloadFileAsync(fileId, ct) is not { } file) return;
            var extension = Path.GetExtension(file.FileName) is { Length: > 0 } ext ? ext : ".pdf";
            var stored = $"billit_{Guid.NewGuid():N}{extension}";
            await File.WriteAllBytesAsync(Path.Combine(paths.UploadDirectory, stored), file.Content, ct);
            invoice.FileName = Path.GetFileName(file.FileName);
            invoice.StoredFileName = stored;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "PDF van Billit-factuur {Number} niet opgehaald", invoice.InvoiceNumber);
        }
    }

    public async Task<SyncStatusDto> GetStatusAsync(CancellationToken ct = default) => new(
        await GetLastRunAsync(ShopifyKey, ct),
        await GetLastRunAsync(BillitKey, ct),
        options.Value.UseDummyData,
        options.Value.SyncIntervalMinutes);

    private async Task<T?> TryAsync<T>(List<string> messages, string source, Func<Task<T>> action) where T : struct
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Sync {Source} mislukt", source);
            messages.Add($"{source}: mislukt – {ex.Message}");
            return null;
        }
    }

    private async Task<DateTime?> GetLastRunAsync(string key, CancellationToken ct)
        => (await db.SyncStates.FindAsync([key], ct))?.LastRun;

    private async Task SetLastRunAsync(string key, DateTime at, CancellationToken ct)
    {
        var state = await db.SyncStates.FindAsync([key], ct);
        if (state is null) db.SyncStates.Add(new SyncState { Key = key, LastRun = at });
        else state.LastRun = at;
    }
}

/// <summary>
/// Twee ritmes:
/// - orders uit Shopify: elke paar seconden (Stream:LiveSyncIntervalSeconds tijdens een stream, anders Stream:OrderSyncIntervalSeconds);
/// - volledige sync (producten, Billit, mailbox, orders met ruime overlap): elke Integrations:SyncIntervalMinutes.
/// </summary>
public class SyncBackgroundService(
    IServiceScopeFactory scopes,
    IOptions<IntegrationOptions> options,
    IOptions<StreamOptions> streamOptions,
    ILogger<SyncBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var full = TimeSpan.FromMinutes(Math.Max(1, options.Value.SyncIntervalMinutes));
        var live = TimeSpan.FromSeconds(Math.Max(1, streamOptions.Value.LiveSyncIntervalSeconds));
        var normal = TimeSpan.FromSeconds(Math.Max(5, streamOptions.Value.OrderSyncIntervalSeconds));
        DateTime lastFull = DateTime.MinValue, lastOrders = DateTime.MinValue, lastStreamCheck = DateTime.MinValue;
        var streamActive = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var sync = scope.ServiceProvider.GetRequiredService<SyncService>();
                if (DateTime.Now - lastStreamCheck > TimeSpan.FromSeconds(10))
                {
                    streamActive = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Streams.AnyAsync(s => s.EndedAt == null, stoppingToken);
                    lastStreamCheck = DateTime.Now;
                }

                // Snel ophalen tijdens een stream: gestart in de app, of de rip-wachtrij vraagt (net nog) orders op.
                var queueRunning = DateTime.Now - LabelQueue.LastQueuePoll < TimeSpan.FromMinutes(1);

                if (DateTime.Now - lastFull >= full)
                {
                    lastFull = lastOrders = DateTime.Now;
                    await sync.RunAsync(stoppingToken);
                }
                else if (DateTime.Now - lastOrders >= (streamActive || queueRunning ? live : normal))
                {
                    lastOrders = DateTime.Now;
                    await sync.RunOrdersAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Automatische sync mislukt");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
