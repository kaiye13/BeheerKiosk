using System.Globalization;
using Boekhouding.Api.Data;
using Boekhouding.Shared;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace Boekhouding.Api.Services;

/// <summary>Maandexport voor de boekhouder: overzicht + detailbladen.</summary>
public class ExcelExporter(AppDbContext db, ReportService reports)
{
    private const string Money = "#,##0.00 €";

    public Task<byte[]> ExportMonthAsync(int year, int month, CancellationToken ct = default) =>
        ExportAsync(ReportService.Range(year, month).From, ReportService.Range(year, month).To, ct);

    /// <summary>Export voor een periode [from, to): maand, kwartaal, jaar, ...</summary>
    public async Task<byte[]> ExportAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var report = await reports.BuildAsync(from, to, ct);
        var orders = await reports.GetOrdersAsync(from, to, ct);
        var invoices = await reports.GetInvoicesAsync(from, to, ct);
        var agreements = await reports.GetAgreementsAsync(from, to, ct);
        var lots = await db.StockLots.Include(l => l.Product).Where(l => l.QuantityRemaining > 0).ToListAsync(ct);

        using var wb = new XLWorkbook();
        WriteSummary(wb.AddWorksheet("Overzicht"), report);
        WriteSales(wb.AddWorksheet("Verkopen"), orders);
        WritePurchases(wb.AddWorksheet("Aankoopfacturen"), invoices);
        WriteMarginPurchases(wb.AddWorksheet("Margegoederen aankoop"), agreements);
        WriteStock(wb.AddWorksheet("Voorraad"), lots);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void WriteSummary(IXLWorksheet ws, MonthlyReportDto r)
    {
        ws.Cell(1, 1).Value = $"Btw-afrekening {r.PeriodLabel}";
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(16);

        var row = 3;
        void Section(string title)
        {
            row++;
            ws.Cell(row, 1).Value = title;
            ws.Range(row, 1, row, 2).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#E8EEF7"));
            row++;
        }
        void Line(string label, decimal value, bool bold = false)
        {
            ws.Cell(row, 1).Value = label;
            ws.Cell(row, 2).Value = value;
            ws.Cell(row, 2).Style.NumberFormat.Format = Money;
            if (bold) ws.Range(row, 1, row, 2).Style.Font.SetBold();
            row++;
        }

        Section("Omzet per kanaal (incl. btw)");
        foreach (var c in r.PerChannel) Line($"{c.Channel.Label()} ({c.Orders} orders)", c.RevenueIncl);
        Line("Totaal omzet", r.TotalRevenueIncl, true);

        Section("Margegoederen (btw op winstmarge, 21/121)");
        Line("Verkoopprijs margegoederen", r.MarginSalesRevenue);
        Line("Aankoopprijs verkochte margegoederen", r.MarginSalesCost);
        Line("Winstmarge (enkel positieve marges)", r.MarginProfit);
        Line("Niet-verrekenbaar verlies", r.MarginLossesIgnored);
        Line("Btw verschuldigd op marge", r.MarginVat, true);

        Section("Factuurgoederen (normaal regime 21%)");
        Line("Omzet incl. btw", r.InvoiceSalesRevenueIncl);
        Line("Omzet excl. btw", r.InvoiceSalesRevenueExcl);
        Line("Btw verschuldigd", r.InvoiceSalesVat, true);

        Section("Overige verkopen");
        Line("Verkopen zonder voorraadkoppeling (incl.)", r.UnallocatedRevenueIncl);
        Line("Btw hierop (21%)", r.UnallocatedVat);
        Line("Verzendkosten aangerekend (incl.)", r.ShippingRevenueIncl);
        Line("Btw op verzendkosten (21%)", r.ShippingVat);

        Section("Aankopen");
        Line("Belgische facturen excl. btw", r.PurchasesBelgianNet);
        Line("Aftrekbare btw Belgische facturen", r.PurchasesBelgianVat, true);
        Line("Intracommunautaire aankopen (0%)", r.PurchasesIntraCommunityNet);
        Line("Verlegde btw (verschuldigd én aftrekbaar)", r.IntraCommunityVatReverseCharged);
        Line("Facturen 0% / vrijgesteld", r.PurchasesExemptNet);
        Line("Aankoop margegoederen (geen btw)", r.MarginPurchasesTotal);

        Section("Saldo");
        Line("Totaal btw verschuldigd", r.VatDueTotal);
        Line("Totaal btw aftrekbaar", r.VatDeductibleTotal);
        Line(r.VatBalance >= 0 ? "TE BETALEN" : "TERUG TE VORDEREN", Math.Abs(r.VatBalance), true);

        if (r.Warnings.Count > 0)
        {
            Section("Aandachtspunten");
            foreach (var w in r.Warnings) ws.Cell(row++, 1).Value = "⚠ " + w;
        }

        row += 2;
        ws.Cell(row, 1).Value = "Indicatieve berekening – laten nakijken door de boekhouder vóór de btw-aangifte.";
        ws.Cell(row, 1).Style.Font.SetItalic();
        ws.Column(1).Width = 55;
        ws.Column(2).Width = 18;
    }

    private static void WriteSales(IXLWorksheet ws, List<SalesOrder> orders)
    {
        string[] headers = ["Datum", "Order", "Kanaal", "Klant", "SKU", "Artikel", "Aantal", "Prijs/st incl.", "Regime",
            "Aankoop/st", "Omzet incl.", "Btw"];
        WriteHeader(ws, headers);
        var row = 2;
        foreach (var o in orders)
        {
            foreach (var l in o.Lines)
            {
                foreach (var a in l.Allocations)
                    row = SalesRow(ws, row, o, l, a.Quantity, a.Regime.Label(), a.UnitCost, a.VatAmount);
                if (l.UnallocatedQuantity > 0)
                    row = SalesRow(ws, row, o, l, l.UnallocatedQuantity, o.IsHistorical ? "Historisch 21%" : "Zonder voorraad 21%",
                        null, Vat.FromInclusive(l.UnitPriceIncl * l.UnallocatedQuantity));
            }
            if (o.ShippingIncl > 0)
            {
                ws.Cell(row, 1).Value = o.OrderDate;
                ws.Cell(row, 2).Value = o.OrderNumber;
                ws.Cell(row, 3).Value = o.Channel.Label();
                ws.Cell(row, 4).Value = o.CustomerName;
                ws.Cell(row, 6).Value = "Verzendkosten";
                ws.Cell(row, 9).Value = "Verzending 21%";
                ws.Cell(row, 11).Value = o.ShippingIncl;
                ws.Cell(row, 12).Value = Vat.FromInclusive(o.ShippingIncl);
                row++;
            }
        }
        Finish(ws, row, moneyCols: [8, 10, 11, 12], dateCol: 1);
    }

    private static int SalesRow(IXLWorksheet ws, int row, SalesOrder o, SalesOrderLine l, int qty, string regime, decimal? unitCost, decimal vat)
    {
        ws.Cell(row, 1).Value = o.OrderDate;
        ws.Cell(row, 2).Value = o.OrderNumber;
        ws.Cell(row, 3).Value = o.Channel.Label();
        ws.Cell(row, 4).Value = o.CustomerName;
        ws.Cell(row, 5).Value = l.Sku;
        ws.Cell(row, 6).Value = l.Title;
        ws.Cell(row, 7).Value = qty;
        ws.Cell(row, 8).Value = l.UnitPriceIncl;
        ws.Cell(row, 9).Value = regime;
        if (unitCost is { } c) ws.Cell(row, 10).Value = c;
        ws.Cell(row, 11).Value = l.UnitPriceIncl * qty;
        ws.Cell(row, 12).Value = vat;
        return row + 1;
    }

    private static void WritePurchases(IXLWorksheet ws, List<PurchaseInvoice> invoices)
    {
        WriteHeader(ws, ["Datum", "Bron", "Leverancier", "Btw-nr", "Land", "Factuurnr", "Btw-type", "Excl. btw", "Btw", "Verlegde btw", "Incl. btw", "Omschrijving"]);
        var row = 2;
        foreach (var i in invoices)
        {
            ws.Cell(row, 1).Value = i.InvoiceDate;
            ws.Cell(row, 2).Value = i.Source;
            ws.Cell(row, 3).Value = i.SupplierName;
            ws.Cell(row, 4).Value = i.SupplierVatNumber;
            ws.Cell(row, 5).Value = i.Country;
            ws.Cell(row, 6).Value = i.InvoiceNumber;
            ws.Cell(row, 7).Value = i.VatType.Label();
            ws.Cell(row, 8).Value = i.NetAmount;
            ws.Cell(row, 9).Value = i.VatAmount;
            ws.Cell(row, 10).Value = i.VatType == PurchaseVatType.IntraCommunity0 ? Vat.OnExclusive(i.NetAmount) : 0m;
            ws.Cell(row, 11).Value = i.NetAmount + i.VatAmount;
            ws.Cell(row, 12).Value = i.Description;
            row++;
        }
        Finish(ws, row, moneyCols: [8, 9, 10, 11], dateCol: 1);
    }

    private static void WriteMarginPurchases(IXLWorksheet ws, List<PurchaseAgreement> agreements)
    {
        WriteHeader(ws, ["Datum", "Overeenkomst", "Verkoper", "Adres", "SKU", "Artikel", "Aantal", "Prijs/st", "Totaal"]);
        var row = 2;
        foreach (var a in agreements)
        foreach (var l in a.Lots)
        {
            ws.Cell(row, 1).Value = a.Date;
            ws.Cell(row, 2).Value = a.Number;
            ws.Cell(row, 3).Value = a.SellerName;
            ws.Cell(row, 4).Value = a.SellerAddress ?? "ONTBREEKT";
            ws.Cell(row, 5).Value = l.Product.Sku;
            ws.Cell(row, 6).Value = l.Product.Title;
            ws.Cell(row, 7).Value = l.QuantityIn;
            ws.Cell(row, 8).Value = l.UnitCost;
            ws.Cell(row, 9).Value = l.UnitCost * l.QuantityIn;
            row++;
        }
        Finish(ws, row, moneyCols: [8, 9], dateCol: 1);
    }

    private static void WriteStock(IXLWorksheet ws, List<StockLot> lots)
    {
        WriteHeader(ws, ["SKU", "Artikel", "Regime", "Aankoopdatum", "In voorraad", "Aankoop/st", "Waarde"]);
        var row = 2;
        foreach (var l in lots.OrderBy(l => l.Product.Sku).ThenBy(l => l.Regime))
        {
            ws.Cell(row, 1).Value = l.Product.Sku;
            ws.Cell(row, 2).Value = l.Product.Title;
            ws.Cell(row, 3).Value = l.Regime.Label();
            ws.Cell(row, 4).Value = l.PurchaseDate;
            ws.Cell(row, 5).Value = l.QuantityRemaining;
            ws.Cell(row, 6).Value = l.UnitCost;
            ws.Cell(row, 7).Value = l.UnitCost * l.QuantityRemaining;
            row++;
        }
        Finish(ws, row, moneyCols: [6, 7], dateCol: 4);
    }

    private static void WriteHeader(IXLWorksheet ws, string[] headers)
    {
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        ws.Row(1).Style.Font.SetBold();
        ws.Row(1).Style.Fill.SetBackgroundColor(XLColor.FromHtml("#E8EEF7"));
        ws.SheetView.FreezeRows(1);
    }

    private static void Finish(IXLWorksheet ws, int nextRow, int[] moneyCols, int dateCol)
    {
        var last = Math.Max(2, nextRow - 1);
        foreach (var c in moneyCols) ws.Range(2, c, last, c).Style.NumberFormat.Format = Money;
        ws.Range(2, dateCol, last, dateCol).Style.DateFormat.Format = "dd/mm/yyyy";
        if (nextRow > 2) ws.Range(1, 1, last, ws.LastColumnUsed()!.ColumnNumber()).SetAutoFilter();
        ws.Columns().AdjustToContents(1, Math.Min(last, 500));
    }
}
