using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Boekhouding.Api.Integrations;

/// <summary>
/// Echte mailbox via IMAP/SMTP (werkt met o.a. Microsoft 365, Gmail met app-wachtwoord, Combell, one.com).
/// Leest de mails van de laatste 14 dagen; dubbels worden in de database tegengehouden op Message-Id,
/// zodat er niets in de mailbox gewijzigd hoeft te worden.
/// </summary>
public class MailKitMailboxClient(IOptions<MailOptions> options) : IMailboxClient
{
    private MailOptions O => options.Value;

    public async Task<IReadOnlyList<IncomingMail>> FetchNewAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(O.ImapHost)) throw new InvalidOperationException("ComplaintsMailbox:ImapHost is niet ingesteld.");
        using var imap = new ImapClient();
        await imap.ConnectAsync(O.ImapHost, O.ImapPort, SecureSocketOptions.Auto, ct);
        await imap.AuthenticateAsync(O.Username ?? O.Address, O.Password ?? "", ct);
        await imap.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        var result = new List<IncomingMail>();
        foreach (var uid in await imap.Inbox.SearchAsync(SearchQuery.DeliveredAfter(DateTime.Today.AddDays(-14)), ct))
        {
            var msg = await imap.Inbox.GetMessageAsync(uid, ct);
            var from = msg.From.Mailboxes.FirstOrDefault();
            if (from is null || from.Address.Equals(O.Address, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(new IncomingMail(
                msg.MessageId ?? $"uid-{uid.Id}@{O.ImapHost}",
                msg.InReplyTo,
                from.Address,
                string.IsNullOrWhiteSpace(from.Name) ? from.Address : from.Name,
                msg.Subject ?? "(geen onderwerp)",
                msg.TextBody ?? HtmlToText(msg.HtmlBody),
                msg.Date.LocalDateTime));
        }
        await imap.DisconnectAsync(true, ct);
        return result;
    }

    public async Task<string> SendAsync(string toEmail, string toName, string subject, string body, string? inReplyTo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(O.SmtpHost)) throw new InvalidOperationException("ComplaintsMailbox:SmtpHost is niet ingesteld.");
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(O.DisplayName, O.Address));
        msg.To.Add(new MailboxAddress(toName, toEmail));
        msg.Subject = subject;
        if (inReplyTo is not null)
        {
            msg.InReplyTo = inReplyTo;
            msg.References.Add(inReplyTo);
        }
        msg.Body = new TextPart("plain") { Text = body };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(O.SmtpHost, O.SmtpPort, SecureSocketOptions.StartTlsWhenAvailable, ct);
        await smtp.AuthenticateAsync(O.Username ?? O.Address, O.Password ?? "", ct);
        await smtp.SendAsync(msg, ct);
        await smtp.DisconnectAsync(true, ct);
        return msg.MessageId ?? "";
    }

    private static string HtmlToText(string? html) =>
        html is null ? "" : System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ")).Trim();
}

/// <summary>Testklachten die verwijzen naar dummy-orders van een paar dagen geleden.</summary>
public class DummyMailboxClient : IMailboxClient
{
    private static readonly (string Subject, string Body)[] Templates =
    [
        ("Pakket nog niet ontvangen – bestelling {nr}", "Hallo,\n\nIk heb bestelling {nr} een tijd geleden geplaatst maar nog steeds niets ontvangen. Kunnen jullie eens kijken?\n\nGroetjes,\n{name}"),
        ("Kaart beschadigd ({nr})", "Hey,\n\nIn mijn order {nr} zat een kaart met een kreuk in de hoek. Is daar iets aan te doen?\n\n{name}"),
        ("Verkeerd artikel ontvangen in {nr}", "Goeiedag,\n\nIk kreeg een ander product dan ik besteld had in {nr}. Hoe kan ik dit omruilen?\n\nMvg,\n{name}"),
        ("Vraag over mijn spaarorders", "Hallo,\n\nIk heb al een paar orders gespaard tijdens de streams. Kunnen die samen verzonden worden en wat kost dat?\n\nBedankt!\n{name}"),
    ];

    public async Task<IReadOnlyList<IncomingMail>> FetchNewAsync(CancellationToken ct)
    {
        var shopify = new DummyShopifyClient();
        var list = new List<IncomingMail>();
        for (var d = 10; d >= 0; d--)
        {
            var day = DateTime.Today.AddDays(-d);
            var rng = new Random(DateOnly.FromDateTime(day).DayNumber * 13);
            if (rng.NextDouble() > 0.6) continue;
            var orders = (await shopify.GetOrdersSinceAsync(day.AddDays(-4), ct)).Where(o => o.OrderDate < day.AddDays(-3)).ToList();
            if (orders.Count == 0) continue;
            var order = orders[rng.Next(orders.Count)];
            var (subject, body) = Templates[rng.Next(Templates.Length)];
            var received = day.AddHours(rng.Next(8, 22)).AddMinutes(rng.Next(60));
            if (received > DateTime.Now) continue;
            list.Add(new IncomingMail($"<dummy-{day:yyyyMMdd}@example.com>", null, order.CustomerEmail ?? "", order.CustomerName,
                subject.Replace("{nr}", order.OrderNumber),
                body.Replace("{nr}", order.OrderNumber).Replace("{name}", order.CustomerName.Split(' ')[0]),
                received));
        }
        return list;
    }

    public Task<string> SendAsync(string toEmail, string toName, string subject, string body, string? inReplyTo, CancellationToken ct) =>
        Task.FromResult($"<dummy-reply-{Guid.NewGuid():N}@example.com>");
}
