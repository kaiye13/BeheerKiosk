namespace Boekhouding.Shared;

// ---------- Livestream ----------

public record StreamDto(int Id, string Title, DateTime StartedAt, DateTime? EndedAt, decimal Goal, bool IsActive);

public record StartStreamRequest(string Title, decimal Goal);

public record StreamOrderDto(
    int OrderId,
    DateTime OrderDate,
    string OrderNumber,
    SalesChannel Channel,
    string CustomerName,
    string CustomerKey,
    string Items,
    int ItemCount,
    decimal TotalIncl,
    decimal ShippingIncl,
    ShippingKind ShippingKind,
    string? ShippingMethod,
    FulfillmentStage Stage,
    bool LabelPrinted);

public record StreamCustomerDto(
    string CustomerKey,
    string CustomerName,
    string? Email,
    int Orders,
    int Items,
    decimal Total,
    int PaidShippingOrders,
    int SaverOrders,
    ShippingKind Kind,
    int OpenSaverOrders,
    decimal OpenSaverValue,
    int TikTokOrders,
    int ShopifyOrders);

public record TopProductDto(string Sku, string Title, int Quantity, decimal Revenue);

public record ShopSessionDto(
    string ClientId,
    string? CustomerName,
    string? CustomerEmail,
    DateTime FirstSeen,
    DateTime LastSeen,
    int MinutesInShop,
    int PageViews,
    ShopStage Stage,
    decimal CartValue,
    int CartItems,
    string? LastProduct);

public record AbandonedCheckoutDto(string Id, DateTime CreatedAt, string? CustomerName, string? Email, decimal Total, string Items, string? RecoveryUrl);

public record StreamDashboardDto(
    StreamDto? Stream,
    DateTime From,
    decimal Goal,
    decimal Revenue,
    decimal RevenueShopify,
    decimal RevenueTikTok,
    int OrderCount,
    int CustomerCount,
    decimal AverageOrder,
    int PaidShippingOrders,
    int SaverOrders,
    int FreeShippingOrders,
    decimal ShippingCollected,
    int LabelsPending,
    int NewComplaints,
    DateTime? LastSync,
    bool UsesDummyData,
    List<StreamOrderDto> Orders,
    List<StreamCustomerDto> Customers,
    List<TopProductDto> TopProducts,
    List<ShopSessionDto> LiveShoppers,
    List<AbandonedCheckoutDto> AbandonedCheckouts)
{
    /// <summary>Laatste event van de Shopify-pixel ("Nu in de shop"). Null = pixel (nog) niet gekoppeld.</summary>
    public DateTime? LastPixelEvent { get; init; }
}

/// <summary>Info voor het label nadat "done rippen" geklikt is: de naam uit de rip-tool gekoppeld aan de orders.</summary>
public record LabelInfoDto(
    string Name,
    bool Matched,
    string? CustomerName,
    int Orders,
    int Items,
    ShippingKind? Kind,
    string OrderNumbers,
    List<int> OrderIds,
    int OpenSaverOrders);

public record MarkLabelPrintedRequest(List<int> OrderIds);

// ---------- Opvolging ----------

public record FollowUpOrderDto(
    int Id,
    SalesChannel Channel,
    string OrderNumber,
    DateTime OrderDate,
    string CustomerName,
    string? CustomerEmail,
    string Items,
    decimal TotalIncl,
    ShippingKind ShippingKind,
    string? ShippingMethod,
    FulfillmentStage Stage,
    string? TrackingCarrier,
    string? TrackingNumber,
    string? TrackingUrl,
    string? TrackingStatus,
    DateTime? ShippedAt,
    DateTime? DeliveredAt,
    DateTime? LabelPrintedAt,
    string? InternalNotes,
    int OpenComplaints);

public record UpdateFollowUpRequest(FulfillmentStage Stage, string? TrackingCarrier, string? TrackingNumber, string? InternalNotes);

// ---------- Klachten ----------

public record ComplaintDto(
    int Id,
    string FromName,
    string FromEmail,
    string Subject,
    DateTime ReceivedAt,
    DateTime LastActivity,
    ComplaintStatus Status,
    int? OrderId,
    string? OrderNumber,
    int MessageCount,
    string Preview);

public record ComplaintMessageDto(int Id, bool Outgoing, string Body, DateTime At, string? SendError);

public record ComplaintDetailDto(
    ComplaintDto Complaint,
    List<ComplaintMessageDto> Messages,
    FollowUpOrderDto? Order,
    List<FollowUpOrderDto> CustomerOrders);

public record ReplyRequest(string Body, ComplaintStatus? NewStatus);

public record UpdateComplaintRequest(ComplaintStatus Status, int? OrderId);

// ---------- Detail van één order ----------

public record OrderLineDetailDto(
    string Sku,
    string Title,
    int Quantity,
    decimal UnitPriceIncl,
    decimal TotalIncl,
    string StockSource,
    decimal Vat);

public record OrderDetailDto(
    int Id,
    SalesChannel Channel,
    string OrderNumber,
    DateTime OrderDate,
    string CustomerName,
    string? CustomerEmail,
    string? ShippingMethod,
    ShippingKind ShippingKind,
    decimal ShippingIncl,
    decimal TotalIncl,
    decimal VatDue,
    string FinancialStatus,
    FulfillmentStage Stage,
    string? TrackingCarrier,
    string? TrackingNumber,
    string? TrackingUrl,
    DateTime? LabelPrintedAt,
    string? InternalNotes,
    List<OrderLineDetailDto> Lines);

// ---------- Rip-wachtrij ↔ boekhouding ----------

public record CreateLabelJobRequest(string Name, string Source, string Reference, int? SalesOrderId = null, string? Item = null);

public record LabelJobDto(int Id, string Name, string Source, string Reference, int? SalesOrderId, string? Item, DateTime CreatedAt);

public record CompleteLabelJobRequest(string? Error);

/// <summary>Shopify-order (webshop) voor de rip-wachtrij.</summary>
public record QueueOrderDto(
    int Id,
    string OrderNumber,
    string CustomerName,
    string FirstName,
    string Items,
    string FirstItem,
    long CreatedUnix,
    ShippingKind ShippingKind,
    string? ShippingMethod);
