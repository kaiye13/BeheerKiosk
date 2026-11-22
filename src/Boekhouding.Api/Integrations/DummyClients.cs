using Boekhouding.Shared;

namespace Boekhouding.Api.Integrations;

/// <summary>
/// Genereert per dag een vaste set orders (zelfde datum = zelfde orders), zodat herhaald synchroniseren
/// geen dubbels maakt en er in de loop van de dag "nieuwe" orders bijkomen.
/// Donderdag en zondag zijn livestreamdagen met meer orders, vooral via TikTok Shop.
/// </summary>
public class DummyShopifyClient : IShopifyClient
{
    public Task<IReadOnlyList<ExternalOrder>> GetOrdersSinceAsync(DateTime since, CancellationToken ct)
    {
        var now = DateTime.Now;
        var orders = new List<ExternalOrder>();
        for (var day = DateOnly.FromDateTime(since); day <= DateOnly.FromDateTime(now); day = day.AddDays(1))
        {
            var rng = new Random(day.DayNumber * 7919);
            var liveDay = day.DayOfWeek is DayOfWeek.Thursday or DayOfWeek.Sunday;
            var count = liveDay ? rng.Next(8, 16) : rng.Next(1, 6);

            for (var i = 0; i < count; i++)
            {
                var tiktok = rng.NextDouble() < (liveDay ? 0.55 : 0.25);
                var hour = liveDay && rng.NextDouble() < 0.7 ? rng.Next(20, 23) : rng.Next(9, 23);
                var date = day.ToDateTime(new TimeOnly(hour, rng.Next(0, 60)));
                var order = DummyOrder(rng, tiktok, date, $"gid://shopify/Order/{day.DayNumber}{i:00}",
                    $"#{(day.DayNumber - 739000) * 20 + i}", DummyCatalog.CustomerNames, now);
                if (date > now || date < since) continue;
                orders.Add(order);
            }
        }

        // Livestream-simulatie: de laatste 3 uur komt er
        // gemiddeld elke 8 minuten een order, vooral van een kleine groep vaste kijkers (zodat herhaalde kopers zichtbaar zijn).
        var liveFrom = new[] { since, now.AddHours(-3) }.Max();
        var startMinute = (long)(liveFrom - DateTime.UnixEpoch).TotalMinutes;
        var endMinute = (long)(now - DateTime.UnixEpoch).TotalMinutes;
        for (var m = startMinute; m <= endMinute; m++)
        {
            var rng = new Random((int)(m % int.MaxValue) * 31 + 7);
            if (rng.NextDouble() > 0.12) continue;
            var date = DateTime.UnixEpoch.AddMinutes(m);
            orders.Add(DummyOrder(rng, rng.NextDouble() < 0.6, date, $"gid://shopify/Order/live-{m}", $"#L{m % 100000:00000}",
                DummyCatalog.CustomerNames[..8], now));
        }
        return Task.FromResult<IReadOnlyList<ExternalOrder>>(orders);
    }

    private static ExternalOrder DummyOrder(Random rng, bool tiktok, DateTime date, string id, string number, string[] customers, DateTime now)
    {
        var lines = DummyLines(rng);
        var goods = lines.Sum(l => l.UnitPriceIncl * l.Quantity);
        var customer = customers[rng.Next(customers.Length)];
        // Spaarders verzamelen orders en laten later in één keer verzenden.
        var saver = rng.NextDouble() < 0.35;
        var (method, shipping, kind) = saver ? ("Sparen – later verzenden", 0m, ShippingKind.Saver)
            : goods >= 50 ? ("Gratis verzending vanaf €50", 0m, ShippingKind.Free)
            : rng.NextDouble() < 0.6 ? ("bpost thuislevering", 4.95m, ShippingKind.Paid)
            : ("bpost Pick-up punt", 6.95m, ShippingKind.Paid);

        // Verzonden na 1 dag, geleverd na 3 dagen. Spaarorders worden pas na 2 weken samen verstuurd.
        var age = now - date;
        var shipAfter = kind == ShippingKind.Saver ? TimeSpan.FromDays(14) : TimeSpan.FromDays(1);
        ExternalFulfillment? fulfillment = null;
        if (age > shipAfter)
        {
            var tracking = $"32321{rng.Next(10000000, 99999999)}BE";
            var delivered = age > shipAfter + TimeSpan.FromDays(2);
            fulfillment = new ExternalFulfillment("bpost", tracking,
                $"https://track.bpost.cloud/btr/web/#/search?itemCode={tracking}",
                delivered ? "Delivered" : "In transit", date + shipAfter, delivered ? date + shipAfter + TimeSpan.FromDays(2) : null);
        }

        return new ExternalOrder(
            tiktok ? SalesChannel.TikTokShop : SalesChannel.Shopify,
            id, number, date, customer,
            customer.ToLowerInvariant().Replace(' ', '.') + "@example.com",
            shipping, "paid", lines, method, fulfillment);
    }

    public Task<IReadOnlyList<AbandonedCheckoutDto>> GetAbandonedCheckoutsAsync(DateTime since, CancellationToken ct)
    {
        var now = DateTime.Now;
        var list = new List<AbandonedCheckoutDto>();
        var hourStart = (long)(since - DateTime.UnixEpoch).TotalHours;
        for (var h = hourStart; h <= (long)(now - DateTime.UnixEpoch).TotalHours; h++)
        {
            var rng = new Random((int)(h % int.MaxValue) * 17 + 3);
            if (rng.NextDouble() > 0.5) continue;
            var created = DateTime.UnixEpoch.AddHours(h).AddMinutes(rng.Next(60));
            if (created > now || created < since) continue;
            var name = DummyCatalog.CustomerNames[rng.Next(DummyCatalog.CustomerNames.Length)];
            var lines = DummyLines(rng);
            list.Add(new AbandonedCheckoutDto($"chk-{h}", created, name, name.ToLowerInvariant().Replace(' ', '.') + "@example.com",
                lines.Sum(l => l.Quantity * l.UnitPriceIncl), string.Join(", ", lines.Select(l => $"{l.Quantity}× {l.Title}")),
                $"https://shop.example.com/checkouts/recover/{h}"));
        }
        return Task.FromResult<IReadOnlyList<AbandonedCheckoutDto>>(list);
    }

    internal static List<ExternalOrderLine> DummyLines(Random rng)
    {
        var lines = new List<ExternalOrderLine>();
        var lineCount = rng.Next(1, 4);
        for (var l = 0; l < lineCount; l++)
        {
            // ~3% onbekende SKU, om de waarschuwing "verkocht zonder voorraad" te tonen.
            if (rng.NextDouble() < 0.03)
            {
                lines.Add(new ExternalOrderLine($"ONBEKEND-{rng.Next(100, 999)}", "Custom bundel (geen SKU in boekhouding)", 1, 19.99m));
                continue;
            }
            var item = DummyCatalog.Items[rng.Next(DummyCatalog.Items.Length)];
            var qty = item.Price < 10 ? rng.Next(1, 6) : 1;
            var price = rng.NextDouble() < 0.15 ? Vat.Round(item.Price * 0.9m) : item.Price;
            lines.Add(new ExternalOrderLine(item.Sku, item.Title, qty, price));
        }
        return lines;
    }
}

/// <summary>Historische orders van de oude WooCommerce-shop (jan 2024 – mrt 2025).</summary>
public class DummyWooCommerceClient : IWooCommerceClient
{
    public Task<IReadOnlyList<ExternalOrder>> GetAllOrdersAsync(CancellationToken ct)
    {
        var rng = new Random(2024);
        var orders = new List<ExternalOrder>();
        var n = 1;
        for (var month = new DateTime(2024, 1, 1); month < new DateTime(2025, 4, 1); month = month.AddMonths(1))
        {
            var count = rng.Next(10, 25);
            for (var i = 0; i < count; i++)
            {
                var date = month.AddDays(rng.Next(0, DateTime.DaysInMonth(month.Year, month.Month))).AddHours(rng.Next(9, 23));
                var customer = DummyCatalog.CustomerNames[rng.Next(DummyCatalog.CustomerNames.Length)];
                orders.Add(new ExternalOrder(
                    SalesChannel.WooCommerce, $"woo-{n}", $"WC-{1000 + n}", date, customer, null,
                    rng.NextDouble() < 0.5 ? 4.95m : 0m, "completed", DummyShopifyClient.DummyLines(rng)));
                n++;
            }
        }
        return Task.FromResult<IReadOnlyList<ExternalOrder>>(orders);
    }
}

/// <summary>Inkomende kostenfacturen zoals ze uit Billit zouden komen.</summary>
public class DummyBillitClient : IBillitClient
{
    public Task<IReadOnlyList<ExternalInvoice>> GetIncomingInvoicesSinceAsync(DateTime since, ISet<string>? skipIds, CancellationToken ct)
    {
        var list = new List<ExternalInvoice>();
        var today = DateTime.Today;
        for (var month = new DateTime(since.Year, since.Month, 1); month <= today; month = month.AddMonths(1))
        {
            var rng = new Random(month.Year * 100 + month.Month);
            var key = $"{month:yyyyMM}";

            Add(list, since, today, new ExternalInvoice($"billit-{key}-bpost", "bpost NV", "BE0214596464", "BE",
                $"BP{key}{rng.Next(100, 999)}", month.AddDays(27), PurchaseVatType.Belgian21,
                Vat.Round(180m + (decimal)rng.NextDouble() * 220m), 0m, "Verzendlabels"));

            Add(list, since, today, new ExternalInvoice($"billit-{key}-verpak", "Verpakkingen Online BV", "BE0700123456", "BE",
                $"VO-{key}-{rng.Next(10, 99)}", month.AddDays(9), PurchaseVatType.Belgian21,
                Vat.Round(60m + (decimal)rng.NextDouble() * 90m), 0m, "Dozen, bubbelenveloppen, tape"));

            Add(list, since, today, new ExternalInvoice($"billit-{key}-shopify", "Shopify International Ltd", "IE9787234G", "IE",
                $"SH-{key}", month.AddDays(1), PurchaseVatType.IntraCommunity0, 79m, 0m, "Shopify abonnement (dienst, btw verlegd)"));
        }
        return Task.FromResult<IReadOnlyList<ExternalInvoice>>(list);
    }

    private static void Add(List<ExternalInvoice> list, DateTime since, DateTime today, ExternalInvoice inv)
    {
        if (inv.InvoiceDate < since.Date || inv.InvoiceDate > today) return;
        var vat = inv.VatType == PurchaseVatType.Belgian21 ? Vat.OnExclusive(inv.NetAmount) : 0m;
        list.Add(inv with { VatAmount = vat });
    }
}
