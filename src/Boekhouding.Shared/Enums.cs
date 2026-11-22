namespace Boekhouding.Shared;

/// <summary>Btw-regime waaronder een voorraadstuk is aangekocht.</summary>
public enum VatRegime
{
    /// <summary>Margegoed (tweedehands, aangekocht van particulier): btw enkel op de winstmarge.</summary>
    Margin = 0,
    /// <summary>Factuurgoed: normaal btw-regime, btw op de volledige verkoopprijs.</summary>
    Invoice = 1,
}

/// <summary>Btw-type van een aankoopfactuur.</summary>
public enum PurchaseVatType
{
    /// <summary>Belgische factuur met 21% btw (aftrekbaar).</summary>
    Belgian21 = 0,
    /// <summary>Intracommunautaire aankoop (bv. Polen) aan 0%: btw wordt verlegd naar ons.</summary>
    IntraCommunity0 = 1,
    /// <summary>Andere factuur zonder btw (vrijgesteld / buiten EU zonder invoerbtw op factuur).</summary>
    Exempt0 = 2,
}

public enum SalesChannel
{
    Shopify = 0,
    TikTokShop = 1,
    WooCommerce = 2,
}

/// <summary>Hoe de klant de verzending van deze order geregeld heeft.</summary>
public enum ShippingKind
{
    /// <summary>Klant betaalde verzendkosten (verzender).</summary>
    Paid = 0,
    /// <summary>Klant spaart orders op en laat later in één keer verzenden.</summary>
    Saver = 1,
    /// <summary>Gratis verzending (bv. boven drempelbedrag).</summary>
    Free = 2,
}

/// <summary>Interne opvolging van een order na de stream.</summary>
public enum FulfillmentStage
{
    New = 0,
    Ripped = 1,
    Packed = 2,
    Shipped = 3,
    Delivered = 4,
    /// <summary>Spaarorder: wacht tot klant verzending aanvraagt.</summary>
    Saving = 5,
    Problem = 6,
}

public enum ComplaintStatus
{
    New = 0,
    InProgress = 1,
    WaitingForCustomer = 2,
    Resolved = 3,
}

public enum ShopStage
{
    Browsing = 0,
    Cart = 1,
    Checkout = 2,
    Paid = 3,
}
