using Boekhouding.Api.Data;
using Boekhouding.Shared;

namespace Boekhouding.Api.Services;

public static class Mapping
{
    public static ProductDto ToDto(this Product p)
    {
        var margin = p.Lots.Where(l => l.Regime == VatRegime.Margin).Sum(l => l.QuantityRemaining);
        var invoice = p.Lots.Where(l => l.Regime == VatRegime.Invoice).Sum(l => l.QuantityRemaining);
        return new ProductDto(p.Id, p.Sku, p.Title, p.Description, p.Vendor, p.ProductType, p.Tags, p.Barcode,
            p.WeightGrams, p.Price, p.CompareAtPrice, p.Status, p.ShopifyProductId,
            margin, invoice, margin + invoice, p.Lots.Sum(l => l.QuantityRemaining * l.UnitCost)) { TrackStock = p.TrackStock, RequiresShipping = p.RequiresShipping };
    }

    public static StockLotDto ToDto(this StockLot l) => new(
        l.Id, l.ProductId, l.Product.Sku, l.Product.Title, l.Regime, l.PurchaseDate,
        l.PurchaseAgreement is { } a ? $"{a.Number} – {a.SellerName}"
            : l.PurchaseInvoice is { } i ? $"{i.SupplierName} – {i.InvoiceNumber}" : "",
        l.UnitCost, l.QuantityIn, l.QuantityRemaining, l.QuantityIn - l.QuantityRemaining);

    public static PurchaseAgreementDto ToDto(this PurchaseAgreement a) => new(
        a.Id, a.Number, a.Date, a.SellerName, a.SellerAddress, a.SellerEmail,
        string.IsNullOrWhiteSpace(a.SellerAddress),
        a.Lots.Sum(l => l.QuantityIn), a.Lots.Sum(l => l.QuantityIn * l.UnitCost));

    public static PurchaseInvoiceDto ToDto(this PurchaseInvoice i) => new(
        i.Id, i.Source, i.SupplierName, i.SupplierVatNumber, i.Country, i.InvoiceNumber, i.InvoiceDate, i.VatType,
        i.NetAmount, i.VatAmount, i.NetAmount + i.VatAmount, i.FileName, i.StoredFileName is not null, i.Lots.Count, i.Description);

    public static SalesOrderDto ToDto(this SalesOrder o)
    {
        var allocations = o.Lines.SelectMany(l => l.Allocations).ToList();
        var marginRevenue = allocations.Where(a => a.Regime == VatRegime.Margin).Sum(a => a.UnitPriceIncl * a.Quantity);
        var invoiceRevenue = allocations.Where(a => a.Regime == VatRegime.Invoice).Sum(a => a.UnitPriceIncl * a.Quantity);
        var unallocated = o.Lines.Sum(l => l.UnitPriceIncl * l.UnallocatedQuantity);
        var vat = allocations.Sum(a => a.VatAmount)
                  + o.Lines.Sum(l => Vat.FromInclusive(l.UnitPriceIncl * l.UnallocatedQuantity))
                  + Vat.FromInclusive(o.ShippingIncl);
        return new SalesOrderDto(o.Id, o.Channel, o.OrderNumber, o.OrderDate, o.CustomerName, o.TotalIncl, o.ShippingIncl,
            marginRevenue, invoiceRevenue, unallocated, vat, o.FinancialStatus,
            !o.IsHistorical && o.Lines.Any(l => l.UnallocatedQuantity > 0 && l.Product?.TrackStock != false),
            string.Join(", ", o.Lines.Select(l => $"{l.Quantity}× {l.Title}")));
    }

    /// <summary>Volledige order met per lijn de herkomst van de stuks (marge/factuur) en de btw.</summary>
    public static OrderDetailDto ToDetailDto(this SalesOrder o)
    {
        var lines = o.Lines.Select(l =>
        {
            var parts = l.Allocations.GroupBy(a => a.Regime).Select(g => $"{g.Sum(a => a.Quantity)}× {g.Key.Label().ToLowerInvariant()}").ToList();
            if (l.UnallocatedQuantity > 0)
                parts.Add(l.Product?.TrackStock == false ? "geen voorraad bijgehouden" : o.IsHistorical ? "van vóór de ingebruikname" : $"{l.UnallocatedQuantity}× zonder voorraad");
            var vat = l.Allocations.Sum(a => a.VatAmount) + Vat.FromInclusive(l.UnitPriceIncl * l.UnallocatedQuantity);
            return new OrderLineDetailDto(l.Sku, l.Title, l.Quantity, l.UnitPriceIncl, l.UnitPriceIncl * l.Quantity,
                parts.Count == 0 ? "–" : string.Join(", ", parts), vat);
        }).ToList();

        return new OrderDetailDto(o.Id, o.Channel, o.OrderNumber, o.OrderDate, o.CustomerName, o.CustomerEmail, o.ShippingMethod,
            o.ShippingKind, o.ShippingIncl, o.TotalIncl, lines.Sum(l => l.Vat) + Vat.FromInclusive(o.ShippingIncl), o.FinancialStatus,
            o.Stage, o.TrackingCarrier, o.TrackingNumber, o.TrackingUrl, o.LabelPrintedAt, o.InternalNotes, lines);
    }
}
