using Boekhouding.Api.Data;
using Boekhouding.Api.Integrations;
using Boekhouding.Shared;
using Microsoft.EntityFrameworkCore;

namespace Boekhouding.Api.Services;

/// <summary>Event zoals de Shopify Customer Events-pixel het doorstuurt (zie shopify-pixel.js).</summary>
public class PixelEvent
{
    public string ClientId { get; set; } = "";
    /// <summary>Shopify-eventnaam: page_viewed, product_viewed, product_added_to_cart, checkout_started, checkout_completed.</summary>
    public string Name { get; set; } = "";
    public DateTime? Timestamp { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public decimal? CartValue { get; set; }
    public int? CartItems { get; set; }
    public string? ProductTitle { get; set; }
}

/// <summary>Houdt per bezoeker bij waar die zit (rondkijken → mandje → betalen) en hoelang.</summary>
public class ShopActivityService(AppDbContext db)
{
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(30);
    private static long _lastSimulatedSlot;

    public async Task ApplyAsync(PixelEvent e, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(e.ClientId)) return;
        var at = e.Timestamp?.ToLocalTime() ?? DateTime.Now;
        var s = db.ShopSessions.Local.FirstOrDefault(x => x.ClientId == e.ClientId)
                ?? await db.ShopSessions.FirstOrDefaultAsync(x => x.ClientId == e.ClientId, ct);
        var isNew = s is null;
        if (s is null) db.ShopSessions.Add(s = new ShopSession { ClientId = e.ClientId });
        if (isNew || (s.Stage == ShopStage.Paid && at - s.LastSeen > ActiveWindow) || at - s.LastSeen > TimeSpan.FromHours(2))
        {
            // Nieuw bezoek (of eerder bezoek afgesloten): opnieuw beginnen.
            s.FirstSeen = at;
            s.PageViews = 0;
            s.Stage = ShopStage.Browsing;
            s.CartItems = 0;
            s.CartValue = 0;
        }

        s.LastSeen = at;
        if (!string.IsNullOrWhiteSpace(e.CustomerName)) s.CustomerName = e.CustomerName;
        if (!string.IsNullOrWhiteSpace(e.CustomerEmail)) s.CustomerEmail = e.CustomerEmail;
        if (e.CartValue is { } v) s.CartValue = v;
        if (e.CartItems is { } n) s.CartItems = n;
        if (!string.IsNullOrWhiteSpace(e.ProductTitle)) s.LastProduct = e.ProductTitle;

        var stage = e.Name switch
        {
            "product_added_to_cart" => ShopStage.Cart,
            "checkout_started" or "checkout_contact_info_submitted" or "payment_info_submitted" => ShopStage.Checkout,
            "checkout_completed" => ShopStage.Paid,
            _ => s.Stage,
        };
        if (stage > s.Stage) s.Stage = stage;
        if (e.Name == "page_viewed") s.PageViews++;
    }

    public async Task<List<ShopSessionDto>> GetActiveAsync(CancellationToken ct = default)
    {
        var from = DateTime.Now - ActiveWindow;
        return (await db.ShopSessions.Where(s => s.LastSeen >= from).ToListAsync(ct))
            .OrderByDescending(s => s.Stage == ShopStage.Paid ? -1 : (int)s.Stage)
            .ThenByDescending(s => s.CartValue)
            .Select(s => new ShopSessionDto(s.ClientId, s.CustomerName, s.CustomerEmail, s.FirstSeen, s.LastSeen,
                (int)Math.Round((s.LastSeen - s.FirstSeen).TotalMinutes), s.PageViews, s.Stage, s.CartValue, s.CartItems, s.LastProduct))
            .ToList();
    }

    /// <summary>Dummy-modus: elke 2 minuten een realistische momentopname van bezoekers in de shop.</summary>
    public async Task SimulateAsync(CancellationToken ct = default)
    {
        var slot = (long)(DateTime.Now - DateTime.UnixEpoch).TotalMinutes / 2;
        if (Interlocked.Exchange(ref _lastSimulatedSlot, slot) == slot) return;
        for (var k = 0; k < 14; k++)
        {
            var rng = new Random((int)(slot % 100000) * 101 + k);
            if (rng.NextDouble() < 0.45) continue;
            var known = k < 9; // aangemelde klant → naam gekend
            var name = known ? DummyCatalog.CustomerNames[k] : null;
            var item = DummyCatalog.Items[rng.Next(DummyCatalog.Items.Length)];
            var roll = rng.NextDouble();
            var evt = roll < 0.4 ? "page_viewed" : roll < 0.6 ? "product_viewed" : roll < 0.85 ? "product_added_to_cart" : roll < 0.97 ? "checkout_started" : "checkout_completed";
            var items = rng.Next(1, 5);
            await ApplyAsync(new PixelEvent
            {
                ClientId = $"dummy-{k}",
                Name = evt,
                CustomerName = name,
                CustomerEmail = name?.ToLowerInvariant().Replace(' ', '.') + (known ? "@example.com" : null),
                ProductTitle = item.Title,
                CartItems = evt is "page_viewed" or "product_viewed" ? null : items,
                CartValue = evt is "page_viewed" or "product_viewed" ? null : Vat.Round(item.Price * items),
            }, ct);
        }
        await db.SaveChangesAsync(ct);
    }
}
