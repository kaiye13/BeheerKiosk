using Boekhouding.Api.Data;
using Boekhouding.Shared;
using Microsoft.EntityFrameworkCore;

namespace Boekhouding.Api.Services;

/// <summary>Orderopvolging na de stream: status, tracking en notities.</summary>
public static class FollowUp
{
    public static FollowUpOrderDto ToDto(SalesOrder o, IReadOnlyDictionary<int, int> openComplaints) => new(
        o.Id, o.Channel, o.OrderNumber, o.OrderDate, o.CustomerName, o.CustomerEmail,
        string.Join(", ", o.Lines.Select(l => $"{l.Quantity}× {l.Title}")),
        o.TotalIncl, o.ShippingKind, o.ShippingMethod, o.Stage,
        o.TrackingCarrier, o.TrackingNumber, o.TrackingUrl, o.TrackingStatus,
        o.ShippedAt, o.DeliveredAt, o.LabelPrintedAt, o.InternalNotes,
        openComplaints.GetValueOrDefault(o.Id));

    public static async Task<Dictionary<int, int>> OpenComplaintCountsAsync(AppDbContext db, CancellationToken ct) =>
        (await db.Complaints.Where(c => c.SalesOrderId != null && c.Status != ComplaintStatus.Resolved)
            .Select(c => c.SalesOrderId!.Value).ToListAsync(ct))
        .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());

    public static async Task<List<FollowUpOrderDto>> ListAsync(AppDbContext db, int days, CancellationToken ct)
    {
        var from = DateTime.Today.AddDays(-days);
        var orders = await db.SalesOrders.Include(o => o.Lines)
            // Ook Shopify-orders van vóór de ingebruikname: die moeten nog verzonden/opgevolgd worden. Enkel de oude webshop niet.
            .Where(o => o.Channel != SalesChannel.WooCommerce && o.OrderDate >= from)
            .OrderByDescending(o => o.OrderDate)
            .AsSplitQuery()
            .ToListAsync(ct);
        var open = await OpenComplaintCountsAsync(db, ct);
        return orders.Select(o => ToDto(o, open)).ToList();
    }

    public static async Task<FollowUpOrderDto?> UpdateAsync(AppDbContext db, int id, UpdateFollowUpRequest req, CancellationToken ct)
    {
        var o = await db.SalesOrders.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return null;
        o.Stage = req.Stage;
        o.TrackingCarrier = string.IsNullOrWhiteSpace(req.TrackingCarrier) ? o.TrackingCarrier : req.TrackingCarrier.Trim();
        if (!string.IsNullOrWhiteSpace(req.TrackingNumber) && req.TrackingNumber.Trim() != o.TrackingNumber)
        {
            o.TrackingNumber = req.TrackingNumber.Trim();
            o.TrackingUrl = TrackingUrl(o.TrackingCarrier, o.TrackingNumber);
        }
        o.InternalNotes = req.InternalNotes;
        if (req.Stage == FulfillmentStage.Shipped) o.ShippedAt ??= DateTime.Now;
        if (req.Stage == FulfillmentStage.Delivered) o.DeliveredAt ??= DateTime.Now;
        await db.SaveChangesAsync(ct);
        return ToDto(o, await OpenComplaintCountsAsync(db, ct));
    }

    /// <summary>Trackinglink voor de gangbare Belgische vervoerders.</summary>
    public static string? TrackingUrl(string? carrier, string number) => carrier?.Trim().ToLowerInvariant() switch
    {
        "bpost" => $"https://track.bpost.cloud/btr/web/#/search?itemCode={Uri.EscapeDataString(number)}",
        "postnl" => $"https://jouw.postnl.be/track-and-trace/{Uri.EscapeDataString(number)}-BE",
        "dpd" => $"https://www.dpdgroup.com/be/mydpd/my-parcels/track?parcelNumber={Uri.EscapeDataString(number)}",
        "gls" => $"https://gls-group.com/BE/nl/pakket-volgen?match={Uri.EscapeDataString(number)}",
        "dhl" => $"https://www.dhl.com/be-nl/home/tracking.html?tracking-id={Uri.EscapeDataString(number)}",
        _ => null,
    };
}
