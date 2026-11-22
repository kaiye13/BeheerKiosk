using Boekhouding.Shared;

namespace Boekhouding.Api.Integrations;

public record ExternalOrderLine(string Sku, string Title, int Quantity, decimal UnitPriceIncl, long? VariantId = null);

public record ExternalOrder(
    SalesChannel Channel,
    string ExternalId,
    string OrderNumber,
    DateTime OrderDate,
    string CustomerName,
    string? CustomerEmail,
    decimal ShippingIncl,
    string FinancialStatus,
    List<ExternalOrderLine> Lines,
    string? ShippingMethod = null,
    ExternalFulfillment? Fulfillment = null);

/// <summary>Verzendinfo uit Shopify (fulfillment + tracking van de vervoerder).</summary>
public record ExternalFulfillment(string? Carrier, string? TrackingNumber, string? TrackingUrl, string? Status, DateTime? ShippedAt, DateTime? DeliveredAt);

public record IncomingMail(string MessageId, string? InReplyTo, string FromEmail, string FromName, string Subject, string Body, DateTime Date);

/// <summary>Eigen mailbox voor klachten (bv. klachten@jouwdomein.be): IMAP om te lezen, SMTP om te antwoorden.</summary>
public interface IMailboxClient
{
    Task<IReadOnlyList<IncomingMail>> FetchNewAsync(CancellationToken ct);
    /// <summary>Verstuurt een mail en geeft de Message-Id terug.</summary>
    Task<string> SendAsync(string toEmail, string toName, string subject, string body, string? inReplyTo, CancellationToken ct);
    bool IsConfigured => true;
}

public class MailOptions
{
    public const string Section = "ComplaintsMailbox";
    public string Address { get; set; } = "klachten@example.com";
    public string DisplayName { get; set; } = "Ship & Ripper klantendienst";
    public string? ImapHost { get; set; }
    public int ImapPort { get; set; } = 993;
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public class StreamOptions
{
    public const string Section = "Stream";
    public decimal DefaultGoal { get; set; } = 2000m;
    /// <summary>Verzendmethodes waarvan de naam dit bevat, tellen als "sparen".</summary>
    public string SaverShippingKeyword { get; set; } = "live openen,livestream,spaar,sparen";
    /// <summary>Sync-interval tijdens een actieve stream.</summary>
    public int LiveSyncIntervalSeconds { get; set; } = 2;
    /// <summary>Hoe vaak nieuwe orders opgehaald worden buiten een stream.</summary>
    public int OrderSyncIntervalSeconds { get; set; } = 10;
    /// <summary>Sleutel die de Shopify-pixel meestuurt (endpoint is publiek bereikbaar).</summary>
    public string PixelKey { get; set; } = "verander-mij-pixel";
    /// <summary>Publiek https-adres van de server (bv. https://boekhouding.tcg-kiosk.be). Leeg = het adres waarmee de app verbindt.</summary>
    public string? PublicUrl { get; set; }
}

public record ExternalInvoice(
    string ExternalId,
    string SupplierName,
    string? SupplierVatNumber,
    string Country,
    string InvoiceNumber,
    DateTime InvoiceDate,
    PurchaseVatType VatType,
    decimal NetAmount,
    decimal VatAmount,
    string? Description,
    string? PdfFileId = null);

/// <summary>Shopify Admin API. TikTok Shop-orders komen via het Shopify-verkoopkanaal binnen en worden hier als <see cref="SalesChannel.TikTokShop"/> teruggegeven.</summary>
public interface IShopifyClient
{
    Task<IReadOnlyList<ExternalOrder>> GetOrdersSinceAsync(DateTime since, CancellationToken ct);
    /// <summary>Verlaten checkouts (Admin API "abandonedCheckouts"): mandje + e-mail, niet betaald.</summary>
    Task<IReadOnlyList<AbandonedCheckoutDto>> GetAbandonedCheckoutsAsync(DateTime since, CancellationToken ct);
}

/// <summary>Oude WooCommerce-webshop: eenmalige import van historische omzet.</summary>
public interface IWooCommerceClient
{
    Task<IReadOnlyList<ExternalOrder>> GetAllOrdersAsync(CancellationToken ct);
    bool IsConfigured => true;
}

/// <summary>Billit: inkomende (aankoop)facturen.</summary>
public interface IBillitClient
{
    /// <param name="skipIds">Al gekende documenten: niet opnieuw in detail ophalen.</param>
    Task<IReadOnlyList<ExternalInvoice>> GetIncomingInvoicesSinceAsync(DateTime since, ISet<string>? skipIds, CancellationToken ct);
    /// <summary>PDF van een document (null als er geen is).</summary>
    Task<(string FileName, byte[] Content)?> DownloadFileAsync(string fileId, CancellationToken ct) =>
        Task.FromResult<(string FileName, byte[] Content)?>(null);
    /// <summary>PDF-bestand (FileID) van een al geïmporteerd document, om het opnieuw op te halen.</summary>
    Task<string?> GetPdfFileIdAsync(string externalId, CancellationToken ct) => Task.FromResult<string?>(null);
    bool IsConfigured => true;
}

public class IntegrationOptions
{
    public const string Section = "Integrations";

    /// <summary>true = dummy-data genereren i.p.v. echte API's aanspreken.</summary>
    public bool UseDummyData { get; set; } = true;
    public int SyncIntervalMinutes { get; set; } = 10;

    public string? ShopifyShopDomain { get; set; }
    /// <summary>Oude custom app (shpat_…). Leeg laten bij een Dev Dashboard-app.</summary>
    public string? ShopifyAccessToken { get; set; }
    /// <summary>Dev Dashboard-app: Client ID + Client secret (shpss_…). De server vraagt zelf een token aan.</summary>
    public string? ShopifyClientId { get; set; }
    public string? ShopifyClientSecret { get; set; }
    public string ShopifyApiVersion { get; set; } = "2026-07";
    /// <summary>Vanaf welke datum orders de eerste keer opgehaald worden (standaard 1 januari van dit jaar).</summary>
    public DateTime? ShopifyImportFrom { get; set; }
    /// <summary>Vanaf wanneer de app de voorraad bijhoudt. Leeg = moment van de eerste echte sync.</summary>
    public DateTime? GoLiveDate { get; set; }
    /// <summary>Vanaf welke datum Billit-documenten de eerste keer opgehaald worden (standaard 1 januari van dit jaar).</summary>
    public DateTime? BillitImportFrom { get; set; }
    /// <summary>Bij het invoeren van een aankoop de voorraad in Shopify mee verhogen.</summary>
    public bool ShopifyAdjustInventory { get; set; } = true;
    /// <summary>Naam van de Shopify-locatie voor voorraad. Leeg = eerste actieve locatie.</summary>
    public string? ShopifyLocationName { get; set; }
    public string? BillitBaseUrl { get; set; } = "https://api.billit.be";
    public string? BillitApiKey { get; set; }
    public string? BillitPartyId { get; set; }
    /// <summary>TikTok Shop Partner Center-app (APP_KEY / APP_SECRET). Leeg = TikTok enkel via Shopify.</summary>
    public string? TikTokAppKey { get; set; }
    public string? TikTokAppSecret { get; set; }
    /// <summary>Tokens (tokens.json van de TikTok-autorisatie). Leeg = tiktok-tokens.json in de datamap.</summary>
    public string? TikTokTokensFile { get; set; }
    /// <summary>Vanaf welke datum TikTok-orders de eerste keer opgehaald worden (standaard zoals Shopify).</summary>
    public DateTime? TikTokImportFrom { get; set; }
    /// <summary>Hoe vaak TikTok gevraagd wordt (seconden). TikTok heeft strengere limieten dan Shopify.</summary>
    public int TikTokSyncIntervalSeconds { get; set; } = 30;
    public string? WooBaseUrl { get; set; }
    public string? WooConsumerKey { get; set; }
    public string? WooConsumerSecret { get; set; }
}
