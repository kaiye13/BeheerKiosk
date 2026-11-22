using Boekhouding.Api.Data;
using Boekhouding.Api.Integrations;
using Boekhouding.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Boekhouding.Api.Services;

/// <summary>Livestream: doel, wie wat besteld heeft, verzenders vs. spaarders, labels.</summary>
public class StreamService(
    AppDbContext db,
    IShopifyClient shopify,
    ShopActivityService activity,
    SyncService sync,
    IOptions<StreamOptions> options,
    IOptions<IntegrationOptions> integration,
    ILogger<StreamService> log)
{
    public static StreamDto ToDto(LiveStream s) => new(s.Id, s.Title, s.StartedAt, s.EndedAt, s.Goal, s.EndedAt is null);

    public Task<LiveStream?> GetActiveAsync(CancellationToken ct = default) =>
        db.Streams.Where(s => s.EndedAt == null).OrderByDescending(s => s.StartedAt).FirstOrDefaultAsync(ct);

    public async Task<LiveStream> StartAsync(StartStreamRequest req, CancellationToken ct = default)
    {
        if (await GetActiveAsync(ct) is { } running) throw new ValidationException($"Er loopt al een stream sinds {running.StartedAt:HH:mm}.");
        var stream = new LiveStream
        {
            Title = string.IsNullOrWhiteSpace(req.Title) ? $"Stream {DateTime.Now:dd/MM HH:mm}" : req.Title.Trim(),
            Goal = req.Goal > 0 ? req.Goal : options.Value.DefaultGoal,
            StartedAt = DateTime.Now,
        };
        db.Streams.Add(stream);
        await db.SaveChangesAsync(ct);
        return stream;
    }

    public async Task<LiveStream?> StopAsync(CancellationToken ct = default)
    {
        var s = await GetActiveAsync(ct);
        if (s is null) return null;
        s.EndedAt = DateTime.Now;
        await db.SaveChangesAsync(ct);
        return s;
    }

    /// <summary>Periode van de stream (of vandaag als er geen stream loopt).</summary>
    private async Task<(LiveStream? Stream, DateTime From, DateTime To)> WindowAsync(int? streamId, CancellationToken ct)
    {
        var stream = streamId is { } id ? await db.Streams.FindAsync([id], ct) : await GetActiveAsync(ct);
        // Lopende stream of "vandaag": geen bovengrens, zodat orders met een klok die iets voorloopt niet wegvallen.
        return stream is null ? (null, DateTime.Today, DateTime.MaxValue) : (stream, stream.StartedAt, stream.EndedAt ?? DateTime.MaxValue);
    }

    private Task<List<SalesOrder>> OrdersAsync(DateTime from, DateTime to, CancellationToken ct) =>
        db.SalesOrders.Include(o => o.Lines)
            .Where(o => o.Channel != SalesChannel.WooCommerce && o.OrderDate >= from && o.OrderDate <= to)
            .OrderByDescending(o => o.OrderDate)
            .AsSplitQuery()
            .ToListAsync(ct);

    public async Task<StreamDashboardDto> DashboardAsync(int? streamId, CancellationToken ct = default)
    {
        var (stream, from, to) = await WindowAsync(streamId, ct);
        var orders = await OrdersAsync(from, to, ct);
        var openSavers = (await db.SalesOrders.Where(o => o.ShippingKind == ShippingKind.Saver && o.Stage == FulfillmentStage.Saving)
                .ToListAsync(ct))
            .GroupBy(o => Labels.CustomerKey(o.CustomerEmail, o.CustomerName))
            .ToDictionary(g => g.Key, g => (Count: g.Count(), Value: g.Sum(o => o.TotalIncl)));

        var customers = orders
            .GroupBy(o => Labels.CustomerKey(o.CustomerEmail, o.CustomerName))
            .Select(g =>
            {
                var paid = g.Count(o => o.ShippingKind == ShippingKind.Paid);
                var saver = g.Count(o => o.ShippingKind == ShippingKind.Saver);
                var open = openSavers.GetValueOrDefault(g.Key);
                return new StreamCustomerDto(g.Key, g.First().CustomerName, g.First().CustomerEmail, g.Count(),
                    g.Sum(o => o.Lines.Sum(l => l.Quantity)), g.Sum(o => o.TotalIncl), paid, saver,
                    paid > 0 ? ShippingKind.Paid : saver > 0 ? ShippingKind.Saver : ShippingKind.Free,
                    open.Count, open.Value,
                    g.Count(o => o.Channel == SalesChannel.TikTokShop), g.Count(o => o.Channel == SalesChannel.Shopify));
            })
            .OrderByDescending(c => c.Total)
            .ToList();

        var top = orders.SelectMany(o => o.Lines)
            .GroupBy(l => l.Sku)
            .Select(g => new TopProductDto(g.Key, g.First().Title, g.Sum(l => l.Quantity), g.Sum(l => l.Quantity * l.UnitPriceIncl)))
            .OrderByDescending(p => p.Quantity).Take(8).ToList();

        List<AbandonedCheckoutDto> abandoned = [];
        try
        {
            // Mandjes die tijdens de stream (of de laatste 24 uur) niet afgerekend werden.
            var since = new[] { from, DateTime.Now.AddHours(-24) }.Min();
            abandoned = (await shopify.GetAbandonedCheckoutsAsync(since, ct)).OrderByDescending(a => a.CreatedAt).Take(20).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug(ex, "Verlaten checkouts niet beschikbaar");
        }

        var lastPixel = await db.ShopSessions.OrderByDescending(s => s.LastSeen).Select(s => (DateTime?)s.LastSeen).FirstOrDefaultAsync(ct);
        var revenue = orders.Sum(o => o.TotalIncl);
        return new StreamDashboardDto(
            stream is null ? null : ToDto(stream),
            from,
            stream?.Goal ?? options.Value.DefaultGoal,
            revenue,
            orders.Where(o => o.Channel == SalesChannel.Shopify).Sum(o => o.TotalIncl),
            orders.Where(o => o.Channel == SalesChannel.TikTokShop).Sum(o => o.TotalIncl),
            orders.Count,
            customers.Count,
            orders.Count == 0 ? 0 : Vat.Round(revenue / orders.Count),
            orders.Count(o => o.ShippingKind == ShippingKind.Paid),
            orders.Count(o => o.ShippingKind == ShippingKind.Saver),
            orders.Count(o => o.ShippingKind == ShippingKind.Free),
            orders.Sum(o => o.ShippingIncl),
            orders.Count(o => o.LabelPrintedAt == null),
            await db.Complaints.CountAsync(c => c.Status == ComplaintStatus.New, ct),
            (await sync.GetStatusAsync(ct)).LastShopifySync,
            integration.Value.UseDummyData,
            orders.Select(o => new StreamOrderDto(o.Id, o.OrderDate, o.OrderNumber, o.Channel, o.CustomerName,
                Labels.CustomerKey(o.CustomerEmail, o.CustomerName),
                string.Join(", ", o.Lines.Select(l => $"{l.Quantity}× {l.Title}")),
                o.Lines.Sum(l => l.Quantity), o.TotalIncl, o.ShippingIncl, o.ShippingKind, o.ShippingMethod, o.Stage,
                o.LabelPrintedAt is not null)).ToList(),
            customers,
            top,
            await activity.GetActiveAsync(ct),
            abandoned)
        {
            LastPixelEvent = lastPixel,
        };
    }

    /// <summary>
    /// Koppelt de naam uit de rip-tool aan de orders van deze stream. Eerst exacte naam, dan e-mail,
    /// dan "naam bevat" (TikTok-namen zijn vaak verkort).
    /// </summary>
    /// <param name="reference">Bestelling uit de rip-wachtrij ("shopify:&lt;id&gt;" of "tiktok:&lt;id&gt;"): dan wordt die
    /// bestelling zelf gebruikt (ook als ze van een vorige dag is), samen met de andere orders van die klant in deze stream.</param>
    public async Task<LabelInfoDto> LabelInfoAsync(string name, string? reference = null, CancellationToken ct = default)
    {
        var clean = (name ?? "").Trim().TrimStart('@');
        var (_, from, to) = await WindowAsync(null, ct);
        var orders = await OrdersAsync(from.AddHours(-12), to, ct);

        static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var n = Norm(clean);

        var byReference = await FindByReferenceAsync(reference, ct);
        if (byReference is not null)
        {
            var customer = Labels.CustomerKey(byReference.CustomerEmail, byReference.CustomerName);
            var same = orders.Where(o => o.Id != byReference.Id && Labels.CustomerKey(o.CustomerEmail, o.CustomerName) == customer);
            return await InfoAsync(clean, [byReference, .. same], ct);
        }

        List<SalesOrder> matches = n.Length == 0 ? [] :
            orders.Where(o => Norm(o.CustomerName) == n).ToList() is { Count: > 0 } exact ? exact
            : orders.Where(o => o.CustomerEmail is { } e && Norm(e.Split('@')[0]) == n).ToList() is { Count: > 0 } byMail ? byMail
            : n.Length >= 3 ? orders.Where(o => Norm(o.CustomerName).Contains(n) || n.Contains(Norm(o.CustomerName))).ToList()
            : [];

        // Meerdere klanten gevonden met "bevat": te onzeker, niets koppelen.
        if (matches.Select(o => Labels.CustomerKey(o.CustomerEmail, o.CustomerName)).Distinct().Count() > 1)
            matches = [];

        if (matches.Count == 0)
            return new LabelInfoDto(clean, false, null, 0, 0, null, "", [], 0);
        return await InfoAsync(clean, matches, ct);
    }

    /// <summary>Bestelling van de rip-wachtrij terugvinden: Shopify-id (gid://shopify/Order/…) of TikTok-ordernummer.</summary>
    private async Task<SalesOrder?> FindByReferenceAsync(string? reference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Split(':', 2) is not [var source, var raw]) return null;
        var id = raw.Split('#')[0].Trim();   // "#…" = herdruk-kenmerk van de wachtrij
        if (id.Length == 0) return null;
        var query = db.SalesOrders.Include(o => o.Lines);
        return source.ToLowerInvariant() switch
        {
            "shopify" => await query.FirstOrDefaultAsync(o => o.ExternalId == "gid://shopify/Order/" + id, ct),
            "tiktok" => await query.FirstOrDefaultAsync(o => o.Channel == SalesChannel.TikTokShop && o.ExternalId == id, ct),
            _ => null,
        };
    }

    private async Task<LabelInfoDto> InfoAsync(string clean, List<SalesOrder> matches, CancellationToken ct)
    {
        var first = matches[0];
        var key = Labels.CustomerKey(first.CustomerEmail, first.CustomerName);
        var openSavers = await db.SalesOrders.CountAsync(o => o.ShippingKind == ShippingKind.Saver && o.Stage == FulfillmentStage.Saving
                                                              && (o.CustomerEmail == first.CustomerEmail || o.CustomerName == first.CustomerName), ct);
        var kind = matches.Any(o => o.ShippingKind == ShippingKind.Paid) ? ShippingKind.Paid
            : matches.Any(o => o.ShippingKind == ShippingKind.Saver) ? ShippingKind.Saver : ShippingKind.Free;
        return new LabelInfoDto(clean, true, first.CustomerName, matches.Count, matches.Sum(o => o.Lines.Sum(l => l.Quantity)), kind,
            string.Join(" ", matches.OrderBy(o => o.OrderDate).Select(o => o.OrderNumber)), matches.Select(o => o.Id).ToList(), openSavers);
    }

    public async Task MarkLabelPrintedAsync(IEnumerable<int> orderIds, CancellationToken ct = default)
    {
        var ids = orderIds.ToList();
        foreach (var o in await db.SalesOrders.Where(o => ids.Contains(o.Id)).ToListAsync(ct))
        {
            o.LabelPrintedAt = DateTime.Now;
            if (o.Stage == FulfillmentStage.New) o.Stage = FulfillmentStage.Ripped;
        }
        await db.SaveChangesAsync(ct);
    }
}
