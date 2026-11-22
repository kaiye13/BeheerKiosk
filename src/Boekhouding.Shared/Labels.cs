namespace Boekhouding.Shared;

public static class Labels
{
    public static string Label(this SalesChannel c) => c switch
    {
        SalesChannel.Shopify => "Shopify",
        SalesChannel.TikTokShop => "TikTok Shop",
        SalesChannel.WooCommerce => "WooCommerce (oud)",
        _ => c.ToString(),
    };

    public static string Label(this PurchaseVatType t) => t switch
    {
        PurchaseVatType.Belgian21 => "BE 21%",
        PurchaseVatType.IntraCommunity0 => "Intracommunautair 0% (verlegd)",
        PurchaseVatType.Exempt0 => "0% / vrijgesteld",
        _ => t.ToString(),
    };

    public static string Label(this VatRegime r) => r == VatRegime.Margin ? "Marge" : "Factuur";

    public static string Label(this ShippingKind k) => k switch
    {
        ShippingKind.Paid => "Verzending betaald",
        ShippingKind.Saver => "Spaarder",
        ShippingKind.Free => "Gratis verzending",
        _ => k.ToString(),
    };

    public static string Label(this FulfillmentStage s) => s switch
    {
        FulfillmentStage.New => "Nieuw",
        FulfillmentStage.Ripped => "Geript / label",
        FulfillmentStage.Packed => "Ingepakt",
        FulfillmentStage.Shipped => "Verzonden",
        FulfillmentStage.Delivered => "Geleverd",
        FulfillmentStage.Saving => "Spaarorder",
        FulfillmentStage.Problem => "Probleem",
        _ => s.ToString(),
    };

    public static string Label(this ComplaintStatus s) => s switch
    {
        ComplaintStatus.New => "Nieuw",
        ComplaintStatus.InProgress => "In behandeling",
        ComplaintStatus.WaitingForCustomer => "Wacht op klant",
        ComplaintStatus.Resolved => "Opgelost",
        _ => s.ToString(),
    };

    public static string Label(this ShopStage s) => s switch
    {
        ShopStage.Browsing => "Kijkt rond",
        ShopStage.Cart => "In winkelmandje",
        ShopStage.Checkout => "Aan het betalen",
        ShopStage.Paid => "Betaald",
        _ => s.ToString(),
    };

    /// <summary>Sleutel om orders van dezelfde klant samen te nemen (e-mail, anders naam).</summary>
    public static string CustomerKey(string? email, string name) =>
        (string.IsNullOrWhiteSpace(email) ? name : email).Trim().ToLowerInvariant();
}
