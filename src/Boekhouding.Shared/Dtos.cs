namespace Boekhouding.Shared;

// ---------- Producten & voorraad ----------

public record ProductDto(
    int Id,
    string Sku,
    string Title,
    string? Description,
    string? Vendor,
    string? ProductType,
    string? Tags,
    string? Barcode,
    int? WeightGrams,
    decimal Price,
    decimal? CompareAtPrice,
    string Status,
    long? ShopifyProductId,
    int StockMargin,
    int StockInvoice,
    int StockTotal,
    decimal StockValue)
{
    public bool TrackStock { get; init; } = true;
    public bool RequiresShipping { get; init; } = true;
    /// <summary>Resultaat in Shopify na aanmaken/bijwerken (null = niet gevraagd).</summary>
    public string? ShopifyMessage { get; init; }
}

public record UpdateProductRequest(
    string Title,
    string? Description,
    string? Vendor,
    string? ProductType,
    string? Tags,
    string? Barcode,
    int? WeightGrams,
    decimal Price,
    decimal? CompareAtPrice,
    string Status,
    bool TrackStock = true,
    bool RequiresShipping = true);

/// <summary>Vrij product, los van een aankoop (bv. mystery pack, single, bundel).</summary>
public class CreateProductRequest
{
    /// <summary>Leeg = automatisch.</summary>
    public string? Sku { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Vendor { get; set; }
    public string? ProductType { get; set; }
    public string? Tags { get; set; }
    public string? Barcode { get; set; }
    public int? WeightGrams { get; set; }
    public decimal Price { get; set; }
    public decimal? CompareAtPrice { get; set; }
    public string Status { get; set; } = "draft";
    public bool TrackStock { get; set; } = true;
    /// <summary>false = digitaal product (geen verzending).</summary>
    public bool RequiresShipping { get; set; } = true;
    /// <summary>Meteen aanmaken in Shopify.</summary>
    public bool PushToShopify { get; set; }
}

public record StockLotDto(
    int Id,
    int ProductId,
    string Sku,
    string ProductTitle,
    VatRegime Regime,
    DateTime PurchaseDate,
    string Source,
    decimal UnitCost,
    int QuantityIn,
    int QuantityRemaining,
    int QuantitySold)
{
    /// <summary>Aankoopwaarde van de hele partij.</summary>
    public decimal TotalCost => UnitCost * QuantityIn;
}

// ---------- Aankopen ----------

/// <summary>Eén artikellijn bij invoer (margegoed of factuurgoed). Klasse i.p.v. record zodat een WPF-grid ze kan bewerken.</summary>
public class PurchaseLineRequest
{
    /// <summary>Bestaande SKU → voorraad wordt bij dat product gezet. Leeg of nieuw → nieuw product.</summary>
    public string? Sku { get; set; }
    public string Title { get; set; } = "";
    public int Quantity { get; set; } = 1;
    /// <summary>Aankoopprijs per stuk. Margegoed: betaald bedrag. Factuurgoed: excl. btw.</summary>
    public decimal UnitCost { get; set; }
    /// <summary>Verkoopprijs incl. btw (Shopify "price").</summary>
    public decimal? SalePrice { get; set; }
    public string? Vendor { get; set; }
    public string? ProductType { get; set; }
    public string? Tags { get; set; }
    public string? Barcode { get; set; }
    public string? Description { get; set; }
    public int? WeightGrams { get; set; }
}

public class CreateMarginPurchaseRequest
{
    public DateTime Date { get; set; } = DateTime.Today;
    public string SellerName { get; set; } = "";
    public string? SellerAddress { get; set; }
    public string? SellerEmail { get; set; }
    public string? Notes { get; set; }
    public List<PurchaseLineRequest> Lines { get; set; } = [];
    /// <summary>Nieuwe producten in Shopify aanmaken (als concept) en Shopify-voorraad verhogen.</summary>
    public bool SyncToShopify { get; set; }
}

public class CreateInvoicePurchaseRequest
{
    public string SupplierName { get; set; } = "";
    public string? SupplierVatNumber { get; set; }
    public string Country { get; set; } = "BE";
    public string InvoiceNumber { get; set; } = "";
    public DateTime InvoiceDate { get; set; } = DateTime.Today;
    public PurchaseVatType VatType { get; set; } = PurchaseVatType.Belgian21;
    public decimal NetAmount { get; set; }
    public decimal VatAmount { get; set; }
    public string? Description { get; set; }
    /// <summary>Mag leeg zijn (bv. factuur voor verzendmateriaal zonder voorraad).</summary>
    public List<PurchaseLineRequest> Lines { get; set; } = [];
    public bool SyncToShopify { get; set; }
}

public record PurchaseAgreementDto(
    int Id,
    string Number,
    DateTime Date,
    string SellerName,
    string? SellerAddress,
    string? SellerEmail,
    bool AddressMissing,
    int ItemCount,
    decimal Total)
{
    /// <summary>Wat er in Shopify gebeurde na het opslaan (of waarom niet).</summary>
    public List<string> ShopifyMessages { get; init; } = [];
}

public record UpdateSellerRequest(string SellerName, string? SellerAddress, string? SellerEmail);

public record PurchaseInvoiceDto(
    int Id,
    string Source,
    string SupplierName,
    string? SupplierVatNumber,
    string Country,
    string InvoiceNumber,
    DateTime InvoiceDate,
    PurchaseVatType VatType,
    decimal NetAmount,
    decimal VatAmount,
    decimal GrossAmount,
    string? FileName,
    bool HasFile,
    int LineCount,
    string? Description)
{
    public List<string> ShopifyMessages { get; init; } = [];
}

// ---------- Verkopen ----------

public record SalesOrderDto(
    int Id,
    SalesChannel Channel,
    string OrderNumber,
    DateTime OrderDate,
    string CustomerName,
    decimal TotalIncl,
    decimal ShippingIncl,
    decimal MarginRevenue,
    decimal InvoiceRevenue,
    decimal UnallocatedRevenue,
    decimal VatDue,
    string FinancialStatus,
    bool HasStockWarning,
    string Items);

// ---------- Dashboard & rapport ----------

public record DashboardDto(
    int Year,
    int Month,
    decimal RevenueTotal,
    decimal RevenueShopify,
    decimal RevenueTikTok,
    decimal RevenueWoo,
    int OrderCount,
    decimal VatBalance,
    decimal MarginProfit,
    decimal StockValueMargin,
    decimal StockValueInvoice,
    int StockWarnings,
    int AgreementsMissingAddress,
    DateTime? LastSync,
    bool UsesDummyData,
    List<SalesOrderDto> RecentOrders,
    List<PurchaseInvoiceDto> RecentInvoices);

public record ChannelRevenueDto(SalesChannel Channel, int Orders, decimal RevenueIncl);

public class MonthlyReportDto
{
    public int Year { get; init; }
    public int Month { get; init; }
    public DateTime From { get; init; }
    /// <summary>Einde van de periode (exclusief).</summary>
    public DateTime To { get; init; }
    /// <summary>Bv. "november 2026", "Q4 2026", "2026".</summary>
    public string PeriodLabel { get; init; } = "";

    // Verkopen margegoederen
    public decimal MarginSalesRevenue { get; init; }
    public decimal MarginSalesCost { get; init; }
    public decimal MarginProfit { get; init; }
    /// <summary>Som van negatieve marges: telt niet mee (geen btw-recuperatie bij margeregeling per stuk).</summary>
    public decimal MarginLossesIgnored { get; init; }
    public decimal MarginVat { get; init; }

    // Verkopen factuurgoederen
    public decimal InvoiceSalesRevenueIncl { get; init; }
    public decimal InvoiceSalesRevenueExcl { get; init; }
    public decimal InvoiceSalesVat { get; init; }

    // Verkopen zonder gekende voorraad (behandeld als normaal 21%)
    public decimal UnallocatedRevenueIncl { get; init; }
    public decimal UnallocatedVat { get; init; }

    // Verzendkosten aangerekend aan klant
    public decimal ShippingRevenueIncl { get; init; }
    public decimal ShippingVat { get; init; }

    public decimal TotalRevenueIncl { get; init; }
    public List<ChannelRevenueDto> PerChannel { get; init; } = [];

    // Aankopen
    public decimal PurchasesBelgianNet { get; init; }
    public decimal PurchasesBelgianVat { get; init; }
    public decimal PurchasesIntraCommunityNet { get; init; }
    public decimal IntraCommunityVatReverseCharged { get; init; }
    public decimal PurchasesExemptNet { get; init; }
    public decimal MarginPurchasesTotal { get; init; }

    // Saldo
    public decimal VatDueTotal { get; init; }
    public decimal VatDeductibleTotal { get; init; }
    /// <summary>Positief = te betalen, negatief = terug te vorderen.</summary>
    public decimal VatBalance { get; init; }

    public List<string> Warnings { get; init; } = [];
}

public record SyncResultDto(DateTime At, string Mode, int NewOrders, int NewInvoices, int UnallocatedLines, List<string> Messages);

public record SyncStatusDto(DateTime? LastShopifySync, DateTime? LastBillitSync, bool UsesDummyData, int IntervalMinutes);
