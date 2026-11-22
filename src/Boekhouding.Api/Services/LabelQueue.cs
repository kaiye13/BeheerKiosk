using Boekhouding.Api.Data;
using Boekhouding.Shared;
using Microsoft.EntityFrameworkCore;

namespace Boekhouding.Api.Services;

/// <summary>
/// Koppeling met de rip-wachtrij (Node-app met TikTok-overlay):
/// - printopdrachten ontvangen en uitdelen aan de pc met de labelprinter (zonder dubbels);
/// - Shopify-webshoporders aanleveren zodat ze ook in de wachtrij komen.
/// </summary>
public static class LabelQueue
{
    /// <summary>Een geclaimde opdracht die na deze tijd nog niet geprint is, mag opnieuw opgepikt worden (pc gecrasht, …).</summary>
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(2);

    private static long _lastQueuePollTicks;

    /// <summary>Laatste keer dat de rip-wachtrij Shopify-orders opvroeg (= de wachtrij draait, dus er wordt gestreamd).</summary>
    public static DateTime LastQueuePoll => new(Interlocked.Read(ref _lastQueuePollTicks));

    public static void MarkQueuePoll() => Interlocked.Exchange(ref _lastQueuePollTicks, DateTime.Now.Ticks);

    public static LabelJobDto ToDto(LabelJob j) => new(j.Id, j.Name, j.Source, j.Reference, j.SalesOrderId, j.Item, j.CreatedAt);

    /// <summary>Nieuwe opdracht; bestaat ze al (zelfde bron + referentie), dan wordt er niets opnieuw geprint.</summary>
    public static async Task<(LabelJob Job, bool Duplicate)> CreateAsync(AppDbContext db, CreateLabelJobRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) throw new ValidationException("Naam is verplicht.");
        var source = string.IsNullOrWhiteSpace(req.Source) ? "onbekend" : req.Source.Trim().ToLowerInvariant();
        var reference = string.IsNullOrWhiteSpace(req.Reference) ? Guid.NewGuid().ToString("N") : req.Reference.Trim();

        var existing = await db.LabelJobs.FirstOrDefaultAsync(j => j.Source == source && j.Reference == reference, ct);
        if (existing is not null) return (existing, true);

        var job = new LabelJob
        {
            Source = source,
            Reference = reference,
            Name = req.Name.Trim(),
            Item = req.Item,
            SalesOrderId = req.SalesOrderId,
            CreatedAt = DateTime.Now,
        };
        db.LabelJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return (job, false);
    }

    /// <summary>Nog te printen: niet geprint, niet (meer) geclaimd, en niet ouder dan 12 uur.</summary>
    public static async Task<List<LabelJobDto>> PendingAsync(AppDbContext db, CancellationToken ct = default)
    {
        var claimExpired = DateTime.Now - ClaimTimeout;
        var since = DateTime.Now.AddHours(-12);
        return (await db.LabelJobs
                .Where(j => j.PrintedAt == null && j.Error == null && j.CreatedAt >= since && (j.ClaimedAt == null || j.ClaimedAt < claimExpired))
                .OrderBy(j => j.CreatedAt).ThenBy(j => j.Id)
                .Take(20)
                .ToListAsync(ct))
            .Select(ToDto).ToList();
    }

    /// <summary>Atomair claimen: maar één pc mag een opdracht printen.</summary>
    public static async Task<bool> ClaimAsync(AppDbContext db, int id, string by, CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var claimExpired = now - ClaimTimeout;
        var rows = await db.LabelJobs
            .Where(j => j.Id == id && j.PrintedAt == null && (j.ClaimedAt == null || j.ClaimedAt < claimExpired))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.ClaimedAt, now).SetProperty(j => j.ClaimedBy, by), ct);
        return rows == 1;
    }

    public static async Task<bool> CompleteAsync(AppDbContext db, int id, string? error, CancellationToken ct = default)
    {
        var job = await db.LabelJobs.FindAsync([id], ct);
        if (job is null) return false;
        if (string.IsNullOrWhiteSpace(error)) job.PrintedAt = DateTime.Now;
        else job.Error = error.Length > 500 ? error[..500] : error;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Webshoporders (Shopify, niet TikTok: die haalt de rip-tool zelf) vanaf <paramref name="since"/>.
    /// <paramref name="onlyLive"/>: enkel orders met verzendmethode "Live openen …" (spaarders), die live geopend worden.
    /// </summary>
    public static async Task<List<QueueOrderDto>> ShopifyOrdersAsync(AppDbContext db, DateTime since, bool onlyLive, CancellationToken ct = default)
    {
        MarkQueuePoll();
        var orders = await db.SalesOrders.Include(o => o.Lines)
            .Where(o => o.Channel == SalesChannel.Shopify && o.OrderDate >= since)
            .OrderBy(o => o.OrderDate)
            .ToListAsync(ct);
        return orders
            .Where(o => !SyncService.CancelledStatuses.Contains(o.FinancialStatus))
            .Where(o => !onlyLive || o.ShippingKind == ShippingKind.Saver)
            .Select(o => new QueueOrderDto(
                o.Id, o.OrderNumber, o.CustomerName,
                o.CustomerName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? o.CustomerName,
                string.Join(", ", o.Lines.Select(l => $"{l.Quantity}× {l.Title}")),
                o.Lines.FirstOrDefault()?.Title ?? "",
                new DateTimeOffset(o.OrderDate).ToUnixTimeSeconds(),
                o.ShippingKind, o.ShippingMethod))
            .ToList();
    }
}
