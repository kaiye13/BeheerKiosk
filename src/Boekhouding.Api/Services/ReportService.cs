using Boekhouding.Api.Data;
using Boekhouding.Shared;
using Microsoft.EntityFrameworkCore;

namespace Boekhouding.Api.Services;

/// <summary>
/// Maandafrekening: omzet per regime, verschuldigde btw, aftrekbare btw en saldo.
/// Bedragen worden in geheugen opgeteld (SQLite kan niet rekenen met decimal-kolommen).
/// </summary>
public class ReportService(AppDbContext db)
{
    private static readonly string[] IgnoredStatuses = ["refunded", "voided", "cancelled"];

    public Task<List<SalesOrder>> GetOrdersAsync(int year, int month, CancellationToken ct = default) =>
        GetOrdersAsync(Range(year, month).From, Range(year, month).To, ct);

    /// <summary>Orders met datum in [from, to).</summary>
    public async Task<List<SalesOrder>> GetOrdersAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        // Producten één keer laden (enkele tientallen) i.p.v. per verkoopregel opnieuw (met lange omschrijving):
        // EF koppelt ze vanzelf aan de regels. Scheelt op Azure SQL seconden bij een jaaroverzicht.
        await db.Products.LoadAsync(ct);
        return await db.SalesOrders
            .Include(o => o.Lines).ThenInclude(l => l.Allocations)
            .Where(o => o.OrderDate >= from && o.OrderDate < to)
            .OrderBy(o => o.OrderDate)
            .AsSplitQuery()
            .ToListAsync(ct);
    }

    public async Task<List<PurchaseInvoice>> GetInvoicesAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        return await db.PurchaseInvoices.Include(i => i.Lots)
            .Where(i => i.InvoiceDate >= from && i.InvoiceDate < to)
            .OrderBy(i => i.InvoiceDate)
            .ToListAsync(ct);
    }

    public async Task<List<PurchaseAgreement>> GetAgreementsAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        return await db.PurchaseAgreements.Include(a => a.Lots).ThenInclude(l => l.Product)
            .Where(a => a.Date >= from && a.Date < to)
            .OrderBy(a => a.Date)
            .ToListAsync(ct);
    }

    public Task<MonthlyReportDto> BuildMonthlyAsync(int year, int month, CancellationToken ct = default) =>
        BuildAsync(Range(year, month).From, Range(year, month).To, ct);

    /// <summary>Afrekening voor een willekeurige periode [from, to): dag, maand, kwartaal of jaar.</summary>
    public async Task<MonthlyReportDto> BuildAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var orders = (await GetOrdersAsync(from, to, ct))
            .Where(o => !IgnoredStatuses.Contains(o.FinancialStatus))
            .ToList();
        var invoices = await GetInvoicesAsync(from, to, ct);
        var agreements = await GetAgreementsAsync(from, to, ct);
        return Calculate(from, to, orders, invoices, agreements);
    }

    public static MonthlyReportDto Calculate(DateTime from, DateTime to, List<SalesOrder> orders,
        List<PurchaseInvoice> invoices, List<PurchaseAgreement> agreements)
    {
        decimal marginRevenue = 0, marginCost = 0, marginProfit = 0, marginLosses = 0, marginVat = 0;
        decimal invoiceRevenue = 0, invoiceVat = 0, unallocatedRevenue = 0, unallocatedVat = 0;
        var unallocatedLines = 0;

        foreach (var line in orders.SelectMany(o => o.Lines))
        {
            foreach (var a in line.Allocations)
            {
                var revenue = a.UnitPriceIncl * a.Quantity;
                if (a.Regime == VatRegime.Margin)
                {
                    var cost = a.UnitCost * a.Quantity;
                    marginRevenue += revenue;
                    marginCost += cost;
                    if (revenue >= cost) marginProfit += revenue - cost;
                    else marginLosses += cost - revenue;
                    marginVat += a.VatAmount;
                }
                else
                {
                    invoiceRevenue += revenue;
                    invoiceVat += a.VatAmount;
                }
            }
            if (line.UnallocatedQuantity > 0)
            {
                var revenue = line.UnitPriceIncl * line.UnallocatedQuantity;
                unallocatedRevenue += revenue;
                unallocatedVat += Vat.FromInclusive(revenue);
                if (!line.SalesOrder.IsHistorical && line.Product?.TrackStock != false) unallocatedLines++;
            }
        }

        var shippingRevenue = orders.Sum(o => o.ShippingIncl);
        var shippingVat = orders.Sum(o => Vat.FromInclusive(o.ShippingIncl));

        var belgian = invoices.Where(i => i.VatType == PurchaseVatType.Belgian21).ToList();
        var intra = invoices.Where(i => i.VatType == PurchaseVatType.IntraCommunity0).ToList();
        var intraNet = intra.Sum(i => i.NetAmount);
        var intraVat = intra.Sum(i => Vat.OnExclusive(i.NetAmount));
        var belgianVat = belgian.Sum(i => i.VatAmount);

        var vatDue = marginVat + invoiceVat + unallocatedVat + shippingVat + intraVat;
        var vatDeductible = belgianVat + intraVat;

        var warnings = new List<string>();
        if (unallocatedLines > 0)
            warnings.Add($"{unallocatedLines} orderlijnen verkocht zonder gekende voorraad → behandeld als normaal 21% op de volledige prijs. Voer de aankoop in om dit te corrigeren.");
        var missingAddress = agreements.Count(a => string.IsNullOrWhiteSpace(a.SellerAddress));
        if (missingAddress > 0)
            warnings.Add($"{missingAddress} verkoopovereenkomst(en) zonder adres van de verkoper. Vul aan vóór de aangifte.");
        if (marginLosses > 0)
            warnings.Add($"Verlies op margegoederen ({marginLosses:N2} €) is niet verrekenbaar onder de margeregeling per stuk.");
        if (orders.Any(o => o.IsHistorical))
            warnings.Add("Bevat historische WooCommerce-orders: btw berekend als normaal 21% (geen voorraadkoppeling).");

        return new MonthlyReportDto
        {
            Year = from.Year,
            Month = from.Month,
            From = from,
            To = to,
            PeriodLabel = PeriodLabel(from, to),
            MarginSalesRevenue = marginRevenue,
            MarginSalesCost = marginCost,
            MarginProfit = marginProfit,
            MarginLossesIgnored = marginLosses,
            MarginVat = marginVat,
            InvoiceSalesRevenueIncl = invoiceRevenue,
            InvoiceSalesRevenueExcl = invoiceRevenue - invoiceVat,
            InvoiceSalesVat = invoiceVat,
            UnallocatedRevenueIncl = unallocatedRevenue,
            UnallocatedVat = unallocatedVat,
            ShippingRevenueIncl = shippingRevenue,
            ShippingVat = shippingVat,
            TotalRevenueIncl = orders.Sum(o => o.TotalIncl),
            PerChannel = orders.GroupBy(o => o.Channel)
                .Select(g => new ChannelRevenueDto(g.Key, g.Count(), g.Sum(o => o.TotalIncl)))
                .OrderBy(c => c.Channel).ToList(),
            PurchasesBelgianNet = belgian.Sum(i => i.NetAmount),
            PurchasesBelgianVat = belgianVat,
            PurchasesIntraCommunityNet = intraNet,
            IntraCommunityVatReverseCharged = intraVat,
            PurchasesExemptNet = invoices.Where(i => i.VatType == PurchaseVatType.Exempt0).Sum(i => i.NetAmount),
            MarginPurchasesTotal = agreements.SelectMany(a => a.Lots).Sum(l => l.QuantityIn * l.UnitCost),
            VatDueTotal = vatDue,
            VatDeductibleTotal = vatDeductible,
            VatBalance = vatDue - vatDeductible,
            Warnings = warnings,
        };
    }

    /// <summary>"november 2026", "Q4 2026", "2026", "8 november 2026" of "01/10/2026 – 15/10/2026".</summary>
    public static string PeriodLabel(DateTime from, DateTime to)
    {
        var nl = System.Globalization.CultureInfo.GetCultureInfo("nl-BE");
        if (to == from.AddDays(1)) return from.ToString("d MMMM yyyy", nl);
        if (from.Day == 1 && to == from.AddMonths(1)) return from.ToString("MMMM yyyy", nl);
        if (from.Day == 1 && (from.Month - 1) % 3 == 0 && to == from.AddMonths(3)) return $"Q{(from.Month - 1) / 3 + 1} {from.Year}";
        if (from.Day == 1 && from.Month == 1 && to == from.AddYears(1)) return from.Year.ToString();
        return $"{from:dd/MM/yyyy} – {to.AddDays(-1):dd/MM/yyyy}";
    }

    public static (DateTime From, DateTime To) Range(int year, int month)
    {
        var from = new DateTime(year, month, 1);
        return (from, from.AddMonths(1));
    }
}
