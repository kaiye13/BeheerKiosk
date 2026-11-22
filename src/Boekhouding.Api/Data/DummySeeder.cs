using Boekhouding.Api.Integrations;
using Boekhouding.Api.Services;
using Boekhouding.Shared;
using Microsoft.EntityFrameworkCore;

namespace Boekhouding.Api.Data;

/// <summary>Vult een lege database met testvoorraad (factuur- en margegoederen). Orders komen daarna via de dummy-sync.</summary>
public static class DummySeeder
{
    public static async Task SeedAsync(AppDbContext db, PurchaseService purchases, CancellationToken ct = default)
    {
        if (await db.Products.AnyAsync(ct)) return;

        foreach (var item in DummyCatalog.Items)
        {
            db.Products.Add(new Product
            {
                Sku = item.Sku, Title = item.Title, Vendor = item.Vendor, ProductType = item.ProductType,
                Price = item.Price, WeightGrams = item.WeightGrams, Status = "active",
                Tags = item.Vendor.ToLowerInvariant() + ", tcg",
                ShopifyProductId = 8_000_000_000 + Array.IndexOf(DummyCatalog.Items, item),
            });
        }
        await db.SaveChangesAsync(ct);

        var start = DateTime.Today.AddDays(-100);

        // Distributeur België (21%)
        await purchases.CreateInvoicePurchaseAsync(Invoice("Cardmaster Distributie BV", "BE0789456123", "BE", "CM-2026-0412",
            start, PurchaseVatType.Belgian21,
            ("PKM-SV-BP", 300, 3.40m), ("PKM-151-ETB", 60, 41.00m), ("PKM-PE-BUNDLE", 130, 24.50m),
            ("PKM-CHAR-UPC", 70, 105.00m), ("ACC-TOPLOAD-25", 300, 1.60m), ("ACC-PENNY-100", 300, 0.70m)), ct);

        // Distributeur Polen (intracommunautair 0%)
        await purchases.CreateInvoicePurchaseAsync(Invoice("Kartomania Sp. z o.o.", "PL5272981234", "PL", "FV/2026/07/118",
            start.AddDays(5), PurchaseVatType.IntraCommunity0,
            ("OP-OP09-BOX", 130, 82.00m), ("OP-OP09-BP", 400, 3.30m), ("YGO-RA03-BOX", 130, 46.00m), ("LORC-BP", 400, 3.60m)), ct);

        // Bijbestelling vorige maand
        await purchases.CreateInvoicePurchaseAsync(Invoice("Cardmaster Distributie BV", "BE0789456123", "BE", "CM-2026-0588",
            DateTime.Today.AddDays(-40), PurchaseVatType.Belgian21,
            ("PKM-SV-BP", 200, 3.40m), ("PKM-151-ETB", 30, 41.00m)), ct);

        // Margegoederen gekocht van particulieren
        await purchases.CreateMarginPurchaseAsync(Margin("Pieter Van den Broeck", "Kerkstraat 12\n9000 Gent", start.AddDays(2),
            ("PKM-VINTAGE-LOT", 120, 9.00m), ("PKM-SINGLE-MIX", 250, 2.50m)), ct);
        await purchases.CreateMarginPurchaseAsync(Margin("Sofie Claeys", "Lindelaan 4\n2000 Antwerpen", start.AddDays(20),
            ("PKM-151-ETB", 15, 38.00m), ("PKM-SV-BP", 80, 2.50m)), ct);
        await purchases.CreateMarginPurchaseAsync(Margin("Kevin Dumont", "Rue de la Gare 7\n4000 Liège", DateTime.Today.AddDays(-35),
            ("PKM-CHAR-UPC", 10, 95.00m), ("PKM-SINGLE-MIX", 200, 3.00m)), ct);
        // Bewust zonder adres: toont de waarschuwing in de app
        await purchases.CreateMarginPurchaseAsync(Margin("Tom (via Marktplaats)", null, DateTime.Today.AddDays(-12),
            ("PKM-VINTAGE-LOT", 60, 11.00m), ("PKM-SV-BP", 50, 2.80m)), ct);
    }

    private static CreateInvoicePurchaseRequest Invoice(string supplier, string vatNr, string country, string number,
        DateTime date, PurchaseVatType type, params (string Sku, int Qty, decimal Cost)[] lines)
    {
        var net = lines.Sum(l => l.Qty * l.Cost);
        return new CreateInvoicePurchaseRequest
        {
            SupplierName = supplier, SupplierVatNumber = vatNr, Country = country, InvoiceNumber = number,
            InvoiceDate = date, VatType = type, NetAmount = net,
            VatAmount = type == PurchaseVatType.Belgian21 ? Vat.OnExclusive(net) : 0m,
            Description = "Voorraad TCG",
            Lines = lines.Select(l => new PurchaseLineRequest { Sku = l.Sku, Title = l.Sku, Quantity = l.Qty, UnitCost = l.Cost }).ToList(),
        };
    }

    private static CreateMarginPurchaseRequest Margin(string seller, string? address, DateTime date,
        params (string Sku, int Qty, decimal Cost)[] lines) => new()
    {
        SellerName = seller, SellerAddress = address, Date = date,
        Lines = lines.Select(l => new PurchaseLineRequest { Sku = l.Sku, Title = l.Sku, Quantity = l.Qty, UnitCost = l.Cost }).ToList(),
    };
}
