using Boekhouding.Api.Data;
using Boekhouding.Shared;
using Microsoft.EntityFrameworkCore;

namespace Boekhouding.Api.Services;

public class ValidationException(string message) : Exception(message);

/// <summary>Invoer van nieuwe voorraad: margegoederen (met verkoopovereenkomst) of factuurgoederen.</summary>
public class PurchaseService(AppDbContext db, StockAllocator allocator)
{
    public async Task<PurchaseAgreement> CreateMarginPurchaseAsync(CreateMarginPurchaseRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.SellerName)) throw new ValidationException("Naam van de verkoper is verplicht.");
        ValidateLines(req.Lines, required: true);

        var agreement = new PurchaseAgreement
        {
            Number = await NextAgreementNumberAsync(req.Date.Year, ct),
            Date = req.Date.Date,
            SellerName = req.SellerName.Trim(),
            SellerAddress = string.IsNullOrWhiteSpace(req.SellerAddress) ? null : req.SellerAddress.Trim(),
            SellerEmail = string.IsNullOrWhiteSpace(req.SellerEmail) ? null : req.SellerEmail.Trim(),
            Notes = req.Notes,
        };
        db.PurchaseAgreements.Add(agreement);

        var products = await AddLotsAsync(req.Lines, VatRegime.Margin, req.Date, lot => agreement.Lots.Add(lot), ct);
        await db.SaveChangesAsync(ct);
        await AllocatePendingAsync(products, req.Date, ct);
        return agreement;
    }

    public async Task<PurchaseInvoice> CreateInvoicePurchaseAsync(CreateInvoicePurchaseRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.SupplierName)) throw new ValidationException("Leverancier is verplicht.");
        if (string.IsNullOrWhiteSpace(req.InvoiceNumber)) throw new ValidationException("Factuurnummer is verplicht.");
        if (req.NetAmount < 0 || req.VatAmount < 0) throw new ValidationException("Bedragen mogen niet negatief zijn.");
        ValidateLines(req.Lines, required: false);

        var invoice = new PurchaseInvoice
        {
            Source = "Manueel",
            SupplierName = req.SupplierName.Trim(),
            SupplierVatNumber = req.SupplierVatNumber,
            Country = string.IsNullOrWhiteSpace(req.Country) ? "BE" : req.Country.Trim().ToUpperInvariant(),
            InvoiceNumber = req.InvoiceNumber.Trim(),
            InvoiceDate = req.InvoiceDate.Date,
            VatType = req.VatType,
            NetAmount = req.NetAmount,
            VatAmount = req.VatType == PurchaseVatType.Belgian21 ? req.VatAmount : 0m,
            Description = req.Description,
        };
        db.PurchaseInvoices.Add(invoice);

        var products = await AddLotsAsync(req.Lines, VatRegime.Invoice, req.InvoiceDate, lot => invoice.Lots.Add(lot), ct);
        await db.SaveChangesAsync(ct);
        await AllocatePendingAsync(products, req.InvoiceDate, ct);
        return invoice;
    }

    private static void ValidateLines(List<PurchaseLineRequest> lines, bool required)
    {
        if (required && lines.Count == 0) throw new ValidationException("Voeg minstens één artikel toe.");
        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l.Title) && string.IsNullOrWhiteSpace(l.Sku))
                throw new ValidationException("Elk artikel heeft een naam of bestaande SKU nodig.");
            if (l.Quantity <= 0) throw new ValidationException($"Aantal moet groter dan 0 zijn ({l.Title}).");
            if (l.UnitCost < 0) throw new ValidationException($"Aankoopprijs mag niet negatief zijn ({l.Title}).");
        }
    }

    private async Task<List<Product>> AddLotsAsync(List<PurchaseLineRequest> lines, VatRegime regime, DateTime date,
        Action<StockLot> attach, CancellationToken ct)
    {
        var touched = new List<Product>();
        foreach (var line in lines)
        {
            var product = await ResolveProductAsync(line, ct);
            product.TrackStock = true; // ingekocht → voorraad bijhouden
            var lot = new StockLot
            {
                Product = product,
                Regime = regime,
                UnitCost = line.UnitCost,
                QuantityIn = line.Quantity,
                QuantityRemaining = line.Quantity,
                PurchaseDate = date.Date,
            };
            db.StockLots.Add(lot);
            attach(lot);
            if (!touched.Contains(product)) touched.Add(product);
        }
        return touched;
    }

    private async Task<Product> ResolveProductAsync(PurchaseLineRequest line, CancellationToken ct)
    {
        var sku = line.Sku?.Trim();
        if (!string.IsNullOrEmpty(sku))
        {
            var existing = db.Products.Local.FirstOrDefault(p => p.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase))
                           ?? await db.Products.FirstOrDefaultAsync(p => p.Sku == sku, ct);
            if (existing is not null) return existing;
        }

        var product = new Product
        {
            Sku = string.IsNullOrEmpty(sku) ? GenerateSku() : sku,
            Title = string.IsNullOrWhiteSpace(line.Title) ? sku! : line.Title.Trim(),
            Description = line.Description,
            Vendor = line.Vendor,
            ProductType = line.ProductType,
            Tags = line.Tags,
            Barcode = line.Barcode,
            WeightGrams = line.WeightGrams,
            Price = line.SalePrice ?? 0m,
            Status = "draft",
        };
        db.Products.Add(product);
        return product;
    }

    public static string GenerateSku() => $"SR-{DateTime.Now:yyMMdd}-{Random.Shared.Next(10000, 99999)}";

    private async Task<string> NextAgreementNumberAsync(int year, CancellationToken ct)
    {
        var prefix = $"VO-{year}-";
        var numbers = await db.PurchaseAgreements.Where(a => a.Number.StartsWith(prefix)).Select(a => a.Number).ToListAsync(ct);
        numbers.AddRange(db.PurchaseAgreements.Local.Select(a => a.Number).Where(n => n.StartsWith(prefix)));
        var next = numbers.Select(n => int.TryParse(n[prefix.Length..], out var x) ? x : 0).DefaultIfEmpty(0).Max() + 1;
        return $"{prefix}{next:0000}";
    }

    private async Task AllocatePendingAsync(List<Product> products, DateTime purchaseDate, CancellationToken ct)
    {
        var any = 0;
        foreach (var p in products)
            any += await allocator.AllocatePendingAsync(p, purchaseDate, ct);
        if (any > 0) await db.SaveChangesAsync(ct);
    }
}
