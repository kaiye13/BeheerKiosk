using System.Text.RegularExpressions;
using Boekhouding.Api.Data;
using Boekhouding.Api.Integrations;
using Boekhouding.Shared;
using Microsoft.EntityFrameworkCore;

namespace Boekhouding.Api.Services;

/// <summary>Klachten uit de eigen mailbox: inlezen, koppelen aan een order en beantwoorden.</summary>
public partial class ComplaintService(AppDbContext db, IMailboxClient mailbox, ILogger<ComplaintService> log)
{
    [GeneratedRegex(@"#\s?([A-Z]?\d{4,6})", RegexOptions.IgnoreCase)]
    private static partial Regex OrderNumberRegex();

    /// <summary>Haalt mails op. Antwoorden van de klant komen in de bestaande klacht, nieuwe mails worden een nieuwe klacht.</summary>
    public async Task<int> ImportAsync(CancellationToken ct = default)
    {
        var mails = await mailbox.FetchNewAsync(ct);
        var ids = mails.Select(m => m.MessageId).ToList();
        var known = (await db.ComplaintMessages.Where(m => m.MessageId != null && ids.Contains(m.MessageId)).Select(m => m.MessageId!).ToListAsync(ct))
            .Concat(await db.Complaints.Where(c => ids.Contains(c.ExternalMessageId)).Select(c => c.ExternalMessageId).ToListAsync(ct))
            .ToHashSet();

        var count = 0;
        foreach (var mail in mails.OrderBy(m => m.Date))
        {
            if (!known.Add(mail.MessageId)) continue;
            var message = new ComplaintMessage { Outgoing = false, MessageId = mail.MessageId, Body = mail.Body.Trim(), At = mail.Date };

            var existing = await FindThreadAsync(mail, ct);
            if (existing is not null)
            {
                existing.Messages.Add(message);
                if (existing.Status is ComplaintStatus.WaitingForCustomer or ComplaintStatus.Resolved)
                    existing.Status = ComplaintStatus.InProgress;
            }
            else
            {
                var complaint = new Complaint
                {
                    ExternalMessageId = mail.MessageId,
                    FromEmail = mail.FromEmail,
                    FromName = mail.FromName,
                    Subject = mail.Subject,
                    ReceivedAt = mail.Date,
                    SalesOrder = await MatchOrderAsync(mail, ct),
                };
                complaint.Messages.Add(message);
                db.Complaints.Add(complaint);
            }
            count++;
        }
        await db.SaveChangesAsync(ct);
        return count;
    }

    private async Task<Complaint?> FindThreadAsync(IncomingMail mail, CancellationToken ct)
    {
        if (mail.InReplyTo is { } replyTo)
        {
            var byReply = await db.Complaints.Include(c => c.Messages)
                .FirstOrDefaultAsync(c => c.ExternalMessageId == replyTo || c.Messages.Any(m => m.MessageId == replyTo), ct);
            if (byReply is not null) return byReply;
        }
        // "Re: <onderwerp>" van dezelfde afzender op een open klacht.
        var subject = Regex.Replace(mail.Subject, @"^(re|antw|aw|fw|fwd)\s*:\s*", "", RegexOptions.IgnoreCase).Trim();
        if (subject == mail.Subject) return null;
        return await db.Complaints.Include(c => c.Messages)
            .Where(c => c.FromEmail == mail.FromEmail && c.Subject == subject && c.Status != ComplaintStatus.Resolved)
            .OrderByDescending(c => c.ReceivedAt).FirstOrDefaultAsync(ct);
    }

    /// <summary>Ordernummer uit onderwerp/tekst (#18181), anders de laatste order van dit e-mailadres.</summary>
    private async Task<SalesOrder?> MatchOrderAsync(IncomingMail mail, CancellationToken ct)
    {
        var match = OrderNumberRegex().Match(mail.Subject + " " + mail.Body);
        if (match.Success)
        {
            var number = "#" + match.Groups[1].Value.ToUpperInvariant();
            var byNumber = await db.SalesOrders.FirstOrDefaultAsync(o => o.OrderNumber == number, ct);
            if (byNumber is not null) return byNumber;
        }
        if (string.IsNullOrWhiteSpace(mail.FromEmail)) return null;
        return await db.SalesOrders.Where(o => o.CustomerEmail == mail.FromEmail)
            .OrderByDescending(o => o.OrderDate).FirstOrDefaultAsync(ct);
    }

    public async Task<List<ComplaintDto>> ListAsync(CancellationToken ct = default) =>
        (await db.Complaints.Include(c => c.Messages).Include(c => c.SalesOrder).ToListAsync(ct))
        .Select(ToDto)
        .OrderBy(c => c.Status == ComplaintStatus.Resolved)
        .ThenByDescending(c => c.LastActivity)
        .ToList();

    public async Task<ComplaintDetailDto?> GetAsync(int id, CancellationToken ct = default)
    {
        var c = await db.Complaints.Include(x => x.Messages).Include(x => x.SalesOrder).ThenInclude(o => o!.Lines)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return null;
        var customerOrders = await db.SalesOrders.Include(o => o.Lines)
            .Where(o => o.CustomerEmail == c.FromEmail && c.FromEmail != "")
            .OrderByDescending(o => o.OrderDate).Take(20).ToListAsync(ct);
        var openCounts = await FollowUp.OpenComplaintCountsAsync(db, ct);
        return new ComplaintDetailDto(
            ToDto(c),
            c.Messages.OrderBy(m => m.At).Select(m => new ComplaintMessageDto(m.Id, m.Outgoing, m.Body, m.At, m.SendError)).ToList(),
            c.SalesOrder is null ? null : FollowUp.ToDto(c.SalesOrder, openCounts),
            customerOrders.Select(o => FollowUp.ToDto(o, openCounts)).ToList());
    }

    public async Task<ComplaintDetailDto?> ReplyAsync(int id, ReplyRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Body)) throw new ValidationException("Antwoord is leeg.");
        var c = await db.Complaints.Include(x => x.Messages).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return null;
        if (string.IsNullOrWhiteSpace(c.FromEmail)) throw new ValidationException("Deze klacht heeft geen e-mailadres om op te antwoorden.");

        var lastIncoming = c.Messages.Where(m => !m.Outgoing).OrderByDescending(m => m.At).FirstOrDefault();
        var message = new ComplaintMessage { Outgoing = true, Body = req.Body.Trim(), At = DateTime.Now };
        try
        {
            message.MessageId = await mailbox.SendAsync(c.FromEmail, c.FromName,
                c.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? c.Subject : "Re: " + c.Subject,
                req.Body, lastIncoming?.MessageId ?? c.ExternalMessageId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Antwoord wel bewaren, zodat de tekst niet verloren gaat; fout tonen in de app.
            log.LogWarning(ex, "Antwoord op klacht {Id} niet verstuurd", id);
            message.SendError = ex.Message;
        }
        c.Messages.Add(message);
        c.Status = req.NewStatus ?? ComplaintStatus.WaitingForCustomer;
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<bool> UpdateAsync(int id, UpdateComplaintRequest req, CancellationToken ct = default)
    {
        var c = await db.Complaints.FindAsync([id], ct);
        if (c is null) return false;
        if (req.OrderId is { } orderId && !await db.SalesOrders.AnyAsync(o => o.Id == orderId, ct))
            throw new ValidationException("Order niet gevonden.");
        c.Status = req.Status;
        c.SalesOrderId = req.OrderId;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static ComplaintDto ToDto(Complaint c)
    {
        var first = c.Messages.OrderBy(m => m.At).FirstOrDefault();
        var preview = (first?.Body ?? "").ReplaceLineEndings(" ");
        return new ComplaintDto(c.Id, c.FromName, c.FromEmail, c.Subject, c.ReceivedAt,
            c.Messages.Count == 0 ? c.ReceivedAt : c.Messages.Max(m => m.At),
            c.Status, c.SalesOrderId, c.SalesOrder?.OrderNumber, c.Messages.Count,
            preview.Length > 140 ? preview[..140] + "…" : preview);
    }
}
