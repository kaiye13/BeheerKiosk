using Boekhouding.Api.Data;
using Boekhouding.Api.Integrations;
using Boekhouding.Api.Services;
using Boekhouding.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Boekhouding.Tests;

public sealed class StreamAndComplaintTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDbContext _db;
    private readonly FakeMailbox _mail = new();
    private readonly SyncService _sync;
    private readonly StreamService _streams;
    private readonly ComplaintService _complaints;

    public StreamAndComplaintTests()
    {
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        var activity = new ShopActivityService(_db);
        _complaints = new ComplaintService(_db, _mail, NullLogger<ComplaintService>.Instance);
        _sync = new SyncService(_db, new DummyShopifyClient(), new NotConfiguredTikTokShopClient(), new DummyWooCommerceClient(), new DummyBillitClient(), new StockAllocator(_db),
            Options.Create(new IntegrationOptions()), Options.Create(new StreamOptions()), _complaints, activity,
            TestServices.ShopifyProducts(_db), TestServices.Paths, NullLogger<SyncService>.Instance);
        _streams = new StreamService(_db, new DummyShopifyClient(), activity, _sync, Options.Create(new StreamOptions()),
            Options.Create(new IntegrationOptions()), NullLogger<StreamService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private async Task OrderAsync(string id, string customer, string? method, decimal shipping, DateTime? at = null, string? email = null)
    {
        await _sync.ImportOrdersAsync([new ExternalOrder(SalesChannel.TikTokShop, id, "#" + id, at ?? DateTime.Now.AddMinutes(-5), customer,
            email ?? customer.ToLowerInvariant().Replace(' ', '.') + "@example.com", shipping, "paid",
            [new ExternalOrderLine("X", "Booster", 2, 6m)], method)], historical: false);
        await _db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("Sparen – later verzenden", 0, ShippingKind.Saver)]
    [InlineData("Spaarpakket", 0, ShippingKind.Saver)]
    [InlineData("bpost thuislevering", 4.95, ShippingKind.Paid)]
    [InlineData("Gratis verzending vanaf €50", 0, ShippingKind.Free)]
    [InlineData(null, 0, ShippingKind.Free)]
    public void Shipping_is_classified_from_method_and_cost(string? method, decimal cost, ShippingKind expected) =>
        Assert.Equal(expected, SyncService.ClassifyShipping(method, cost, "spaar,sparen"));

    [Fact]
    public async Task Saver_orders_start_in_saving_stage()
    {
        await OrderAsync("1001", "Jens Peeters", "Sparen – later verzenden", 0);
        Assert.Equal(FulfillmentStage.Saving, (await _db.SalesOrders.SingleAsync()).Stage);
    }

    [Fact]
    public async Task Label_name_matches_customer_ignoring_case_and_at_sign()
    {
        await OrderAsync("1001", "Jens Peeters", "bpost thuislevering", 4.95m);
        await OrderAsync("1002", "Jens Peeters", "Sparen – later verzenden", 0);
        await OrderAsync("1003", "Lotte Janssens", "bpost thuislevering", 4.95m);

        var info = await _streams.LabelInfoAsync("@jens peeters");

        Assert.True(info.Matched);
        Assert.Equal("Jens Peeters", info.CustomerName);
        Assert.Equal(2, info.Orders);
        Assert.Equal(ShippingKind.Paid, info.Kind);
        Assert.Equal("#1001 #1002", info.OrderNumbers);
    }

    [Fact]
    public async Task Ambiguous_partial_name_is_not_matched()
    {
        await OrderAsync("1001", "Jan Peeters", "bpost", 4.95m);
        await OrderAsync("1002", "Jan Maes", "bpost", 4.95m);

        var info = await _streams.LabelInfoAsync("Jan");

        Assert.False(info.Matched);
        Assert.Empty(info.OrderIds);
    }

    [Fact]
    public async Task Label_from_queue_uses_the_order_itself_even_from_an_earlier_day()
    {
        // Spaarder van 3 dagen geleden, met een voornaam die twee klanten delen: op naam niet te vinden.
        await _sync.ImportOrdersAsync([
            new ExternalOrder(SalesChannel.Shopify, "gid://shopify/Order/555", "#KIOSK1300", DateTime.Now.AddDays(-3), "Jan Peeters", null, 0m,
                "paid", [new ExternalOrderLine("X", "Booster", 2, 6m)], "Live openen op TikTok (geen retour)"),
            new ExternalOrder(SalesChannel.Shopify, "gid://shopify/Order/556", "#KIOSK1301", DateTime.Now, "Jan Maes", null, 4.95m,
                "paid", [new ExternalOrderLine("X", "Booster", 1, 6m)], "bpost"),
        ], historical: false);
        await _db.SaveChangesAsync();

        // Op naam: vindt de verkeerde Jan (van vandaag, verzending betaald).
        Assert.Equal(ShippingKind.Paid, (await _streams.LabelInfoAsync("Jan")).Kind);

        var info = await _streams.LabelInfoAsync("Jan", "shopify:555");
        Assert.True(info.Matched);
        Assert.Equal(ShippingKind.Saver, info.Kind);
        Assert.Equal("#KIOSK1300", info.OrderNumbers);
        Assert.Equal(ShippingKind.Saver, (await _streams.LabelInfoAsync("Jan", "shopify:555#1794170000000")).Kind);   // herdruk
    }

    [Fact]
    public async Task Orders_from_before_go_live_still_show_in_follow_up_and_stream()
    {
        await _sync.ImportOrdersAsync([new ExternalOrder(SalesChannel.TikTokShop, "pre", "#900", DateTime.Now.AddHours(-1), "Jens Peeters", null, 0m,
            "paid", [new ExternalOrderLine("X", "Booster", 1, 6m)], "Live openen op TikTok (geen retour)")], historical: false,
            historicalBefore: DateTime.Now);
        await _db.SaveChangesAsync();

        Assert.True((await _db.SalesOrders.SingleAsync()).IsHistorical);
        Assert.Single(await FollowUp.ListAsync(_db, 30, default));
        Assert.Equal(1, (await _streams.DashboardAsync(null)).OrderCount);
    }

    [Fact]
    public async Task Marking_label_printed_moves_new_orders_to_ripped()
    {
        await OrderAsync("1001", "Jens Peeters", "bpost", 4.95m);
        var id = (await _db.SalesOrders.SingleAsync()).Id;

        await _streams.MarkLabelPrintedAsync([id]);

        var o = await _db.SalesOrders.SingleAsync();
        Assert.NotNull(o.LabelPrintedAt);
        Assert.Equal(FulfillmentStage.Ripped, o.Stage);
    }

    [Fact]
    public async Task Stream_dashboard_counts_only_orders_since_start()
    {
        await OrderAsync("old", "Jens Peeters", "bpost", 4.95m, DateTime.Now.AddHours(-2));
        await _streams.StartAsync(new StartStreamRequest("Test", 2000));
        await OrderAsync("new", "Lotte Janssens", "Sparen", 0, DateTime.Now.AddSeconds(1));

        var d = await _streams.DashboardAsync(null);

        Assert.True(d.Stream!.IsActive);
        Assert.Equal(1, d.OrderCount);
        Assert.Equal(1, d.SaverOrders);
        Assert.Equal("Lotte Janssens", d.Customers.Single().CustomerName);
        Assert.Equal(ShippingKind.Saver, d.Customers.Single().Kind);
    }

    [Fact]
    public async Task Complaint_is_linked_to_order_number_in_subject_and_replies_thread()
    {
        await OrderAsync("18181", "Emma Jacobs", "bpost", 4.95m);
        _mail.Inbox.Add(new IncomingMail("<m1@x>", null, "emma.jacobs@example.com", "Emma Jacobs", "Kaart beschadigd #18181", "Kreuk in de kaart", DateTime.Now.AddHours(-1)));
        Assert.Equal(1, await _complaints.ImportAsync());

        var complaint = await _db.Complaints.Include(c => c.SalesOrder).SingleAsync();
        Assert.Equal("#18181", complaint.SalesOrder!.OrderNumber);

        var detail = await _complaints.ReplyAsync(complaint.Id, new ReplyRequest("Sorry, we sturen een nieuwe.", null));
        Assert.Equal(ComplaintStatus.WaitingForCustomer, detail!.Complaint.Status);
        Assert.Equal("<m1@x>", _mail.Sent.Single().InReplyTo);

        // Antwoord van de klant komt in dezelfde klacht, status gaat terug naar "in behandeling".
        _mail.Inbox.Add(new IncomingMail("<m2@x>", _mail.Sent.Single().MessageId, "emma.jacobs@example.com", "Emma Jacobs", "Re: Kaart beschadigd #18181", "Top, bedankt!", DateTime.Now));
        Assert.Equal(1, await _complaints.ImportAsync());
        Assert.Equal(0, await _complaints.ImportAsync()); // geen dubbels

        var thread = await _db.Complaints.Include(c => c.Messages).SingleAsync();
        Assert.Equal(3, thread.Messages.Count);
        Assert.Equal(ComplaintStatus.InProgress, thread.Status);
    }

    [Fact]
    public async Task Complaint_without_number_links_to_latest_order_of_sender()
    {
        await OrderAsync("2001", "Noah Willems", "bpost", 4.95m, DateTime.Now.AddDays(-3));
        await OrderAsync("2002", "Noah Willems", "bpost", 4.95m, DateTime.Now.AddDays(-1));
        _mail.Inbox.Add(new IncomingMail("<m3@x>", null, "noah.willems@example.com", "Noah", "Vraag", "Waar blijft mijn pakket?", DateTime.Now));

        await _complaints.ImportAsync();

        Assert.Equal("#2002", (await _db.Complaints.Include(c => c.SalesOrder).SingleAsync()).SalesOrder!.OrderNumber);
    }

    [Fact]
    public async Task Failed_send_keeps_reply_with_error()
    {
        await OrderAsync("3001", "Lina Claes", "bpost", 4.95m);
        _mail.Inbox.Add(new IncomingMail("<m4@x>", null, "lina.claes@example.com", "Lina", "Probleem #3001", "...", DateTime.Now));
        await _complaints.ImportAsync();
        _mail.FailSending = true;

        var detail = await _complaints.ReplyAsync((await _db.Complaints.SingleAsync()).Id, new ReplyRequest("Hallo", null));

        Assert.Equal("SMTP down", detail!.Messages.Last().SendError);
    }

    private sealed class FakeMailbox : IMailboxClient
    {
        public List<IncomingMail> Inbox { get; } = [];
        public List<(string To, string? InReplyTo, string MessageId)> Sent { get; } = [];
        public bool FailSending { get; set; }

        public Task<IReadOnlyList<IncomingMail>> FetchNewAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IncomingMail>>(Inbox.ToList());

        public Task<string> SendAsync(string toEmail, string toName, string subject, string body, string? inReplyTo, CancellationToken ct)
        {
            if (FailSending) throw new InvalidOperationException("SMTP down");
            var id = $"<sent-{Sent.Count}@x>";
            Sent.Add((toEmail, inReplyTo, id));
            return Task.FromResult(id);
        }
    }
}
