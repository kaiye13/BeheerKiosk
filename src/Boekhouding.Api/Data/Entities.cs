using Boekhouding.Shared;

namespace Boekhouding.Api.Data;

/// <summary>Product zoals in Shopify (standaardvelden) + koppeling naar voorraad.</summary>
public class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Vendor { get; set; }
    public string? ProductType { get; set; }
    public string? Tags { get; set; }
    public string? Barcode { get; set; }
    public int? WeightGrams { get; set; }
    /// <summary>Verkoopprijs incl. btw.</summary>
    public decimal Price { get; set; }
    public decimal? CompareAtPrice { get; set; }
    /// <summary>Shopify-status: active / draft / archived.</summary>
    public string Status { get; set; } = "draft";
    /// <summary>
    /// false = geen voorraad bijhouden (bv. mystery pack, dienst): verkopen geven geen waarschuwing
    /// "zonder voorraad" en worden belast aan 21% op de volledige prijs.
    /// </summary>
    public bool TrackStock { get; set; } = true;
    /// <summary>false = digitaal product: geen verzending (Shopify vraagt geen verzendadres).</summary>
    public bool RequiresShipping { get; set; } = true;
    public long? ShopifyProductId { get; set; }
    /// <summary>Elke Shopify-variant is één product in de boekhouding (eigen SKU, eigen voorraad).</summary>
    public long? ShopifyVariantId { get; set; }
    public long? ShopifyInventoryItemId { get; set; }
    public DateTime? ShopifySyncedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public List<StockLot> Lots { get; set; } = [];
}

/// <summary>
/// Een partij voorraad van één product, aangekocht onder één btw-regime.
/// Verkopen worden aan partijen toegewezen (margegoederen eerst).
/// </summary>
public class StockLot
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public VatRegime Regime { get; set; }
    /// <summary>Margegoed: betaalde prijs per stuk. Factuurgoed: prijs per stuk excl. btw.</summary>
    public decimal UnitCost { get; set; }
    public int QuantityIn { get; set; }
    public int QuantityRemaining { get; set; }
    public DateTime PurchaseDate { get; set; }
    public int? PurchaseInvoiceId { get; set; }
    public PurchaseInvoice? PurchaseInvoice { get; set; }
    public int? PurchaseAgreementId { get; set; }
    public PurchaseAgreement? PurchaseAgreement { get; set; }
}

public class PurchaseInvoice
{
    public int Id { get; set; }
    /// <summary>"Manueel" of "Billit".</summary>
    public string Source { get; set; } = "Manueel";
    public string? ExternalId { get; set; }
    public string SupplierName { get; set; } = "";
    public string? SupplierVatNumber { get; set; }
    public string Country { get; set; } = "BE";
    public string InvoiceNumber { get; set; } = "";
    public DateTime InvoiceDate { get; set; }
    public PurchaseVatType VatType { get; set; }
    public decimal NetAmount { get; set; }
    public decimal VatAmount { get; set; }
    public string? Description { get; set; }
    public string? FileName { get; set; }
    public string? StoredFileName { get; set; }
    public List<StockLot> Lots { get; set; } = [];
}

/// <summary>Verkoopovereenkomst / aankoopborderel voor margegoederen gekocht van een particulier.</summary>
public class PurchaseAgreement
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public DateTime Date { get; set; }
    public string SellerName { get; set; } = "";
    public string? SellerAddress { get; set; }
    public string? SellerEmail { get; set; }
    public string? Notes { get; set; }
    public List<StockLot> Lots { get; set; } = [];
}

public class SalesOrder
{
    public int Id { get; set; }
    public SalesChannel Channel { get; set; }
    /// <summary>Id in het bronsysteem (Shopify GID / Woo id). Uniek per kanaal → geen dubbele import.</summary>
    public string ExternalId { get; set; } = "";
    public string OrderNumber { get; set; } = "";
    public DateTime OrderDate { get; set; }
    public string CustomerName { get; set; } = "";
    public string? CustomerEmail { get; set; }
    /// <summary>Aangerekende verzendkosten incl. btw.</summary>
    public decimal ShippingIncl { get; set; }
    public decimal TotalIncl { get; set; }
    public string FinancialStatus { get; set; } = "paid";
    /// <summary>Historische order (oude WooCommerce-shop): telt mee in omzet, maar neemt geen huidige voorraad af.</summary>
    public bool IsHistorical { get; set; }
    /// <summary>Naam van de verzendmethode in Shopify (bv. "bpost thuislevering", "Sparen – later verzenden").</summary>
    public string? ShippingMethod { get; set; }
    public ShippingKind ShippingKind { get; set; }

    // ---- Opvolging ----
    public FulfillmentStage Stage { get; set; } = FulfillmentStage.New;
    public string? TrackingCarrier { get; set; }
    public string? TrackingNumber { get; set; }
    public string? TrackingUrl { get; set; }
    /// <summary>Status zoals de vervoerder/Shopify die meldt (bv. "In transit", "Delivered").</summary>
    public string? TrackingStatus { get; set; }
    public DateTime? ShippedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? LabelPrintedAt { get; set; }
    public string? InternalNotes { get; set; }

    public List<SalesOrderLine> Lines { get; set; } = [];
}

/// <summary>Een livestream: alle orders tussen start en einde tellen mee voor het doel.</summary>
public class LiveStream
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public decimal Goal { get; set; }
}

public class Complaint
{
    public int Id { get; set; }
    /// <summary>Message-Id van de eerste mail (voorkomt dubbele import).</summary>
    public string ExternalMessageId { get; set; } = "";
    public string FromEmail { get; set; } = "";
    public string FromName { get; set; } = "";
    public string Subject { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
    public ComplaintStatus Status { get; set; } = ComplaintStatus.New;
    public int? SalesOrderId { get; set; }
    public SalesOrder? SalesOrder { get; set; }
    public List<ComplaintMessage> Messages { get; set; } = [];
}

public class ComplaintMessage
{
    public int Id { get; set; }
    public int ComplaintId { get; set; }
    public Complaint Complaint { get; set; } = null!;
    public bool Outgoing { get; set; }
    public string? MessageId { get; set; }
    public string Body { get; set; } = "";
    public DateTime At { get; set; }
    /// <summary>Uitgaand bericht dat (nog) niet verstuurd kon worden.</summary>
    public string? SendError { get; set; }
}

/// <summary>Bezoeker van de webshop, opgebouwd uit Shopify Customer Events (pixel).</summary>
public class ShopSession
{
    public int Id { get; set; }
    /// <summary>Shopify clientId (anoniem per browser).</summary>
    public string ClientId { get; set; } = "";
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public int PageViews { get; set; }
    public ShopStage Stage { get; set; }
    public decimal CartValue { get; set; }
    public int CartItems { get; set; }
    public string? LastProduct { get; set; }
}

public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public class SalesOrderLine
{
    public int Id { get; set; }
    public int SalesOrderId { get; set; }
    public SalesOrder SalesOrder { get; set; } = null!;
    public int? ProductId { get; set; }
    public Product? Product { get; set; }
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";
    public int Quantity { get; set; }
    /// <summary>Verkoopprijs per stuk incl. btw, na korting.</summary>
    public decimal UnitPriceIncl { get; set; }
    /// <summary>Stuks waarvoor geen voorraad gevonden werd. Die worden als normaal 21% behandeld.</summary>
    public int UnallocatedQuantity { get; set; }
    public List<SaleAllocation> Allocations { get; set; } = [];
}

/// <summary>Koppeling van verkochte stuks aan een voorraadpartij, met de btw die daaruit volgt.</summary>
public class SaleAllocation
{
    public int Id { get; set; }
    public int SalesOrderLineId { get; set; }
    public SalesOrderLine SalesOrderLine { get; set; } = null!;
    public int StockLotId { get; set; }
    public StockLot StockLot { get; set; } = null!;
    public int Quantity { get; set; }
    public VatRegime Regime { get; set; }
    public decimal UnitCost { get; set; }
    public decimal UnitPriceIncl { get; set; }
    /// <summary>Verschuldigde btw voor deze toewijzing (alle stuks samen).</summary>
    public decimal VatAmount { get; set; }
}

public class SyncState
{
    public string Key { get; set; } = "";
    public DateTime LastRun { get; set; }
}

/// <summary>
/// Printopdracht voor een label, bv. vanuit de rip-wachtrij ("iemand komt in de wachtrij").
/// De boekhoud-app op de pc met de labelprinter haalt openstaande opdrachten op en print ze.
/// </summary>
public class LabelJob
{
    public int Id { get; set; }
    /// <summary>Waar de opdracht vandaan komt, bv. "tiktok", "shopify", "test".</summary>
    public string Source { get; set; } = "";
    /// <summary>Uniek per bron (bv. TikTok-ordernummer): voorkomt dubbele labels als de rip-tool herstart.</summary>
    public string Reference { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Item { get; set; }
    public int? SalesOrderId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public string? ClaimedBy { get; set; }
    public DateTime? PrintedAt { get; set; }
    public string? Error { get; set; }
}
