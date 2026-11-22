namespace Boekhouding.Api.Integrations;

// Koppelingen die (nog) niet ingesteld zijn in productie. Ze leveren niets op in plaats van dummy-data,
// zodat er nooit testgegevens in de echte boekhouding terechtkomen.
// - WooCommerce: REST API v3 (GET /wp-json/wc/v3/orders) of een CSV-import, zodra de gegevens er zijn.
// - Billit: REST API (header ApiKey + PartyID), zodra de sleutel er is.

public class NotConfiguredWooCommerceClient : IWooCommerceClient
{
    public bool IsConfigured => false;
    public Task<IReadOnlyList<ExternalOrder>> GetAllOrdersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ExternalOrder>>([]);
}

public class NotConfiguredBillitClient : IBillitClient
{
    public bool IsConfigured => false;
    public Task<IReadOnlyList<ExternalInvoice>> GetIncomingInvoicesSinceAsync(DateTime since, ISet<string>? skipIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ExternalInvoice>>([]);
}

public class NotConfiguredMailboxClient : IMailboxClient
{
    public bool IsConfigured => false;
    public Task<IReadOnlyList<IncomingMail>> FetchNewAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IncomingMail>>([]);
    public Task<string> SendAsync(string toEmail, string toName, string subject, string body, string? inReplyTo, CancellationToken ct) =>
        throw new InvalidOperationException("De klachtenmailbox is nog niet ingesteld (ComplaintsMailbox in appsettings.json).");
}
