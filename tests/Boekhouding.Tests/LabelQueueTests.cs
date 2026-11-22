using Boekhouding.Api.Data;
using Boekhouding.Api.Integrations;
using Boekhouding.Api.Services;
using Boekhouding.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Boekhouding.Tests;

public sealed class LabelQueueTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDbContext _db;

    public LabelQueueTests()
    {
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task Same_order_twice_gives_one_label()
    {
        var (first, dup1) = await LabelQueue.CreateAsync(_db, new CreateLabelJobRequest("Jens Peeters", "tiktok", "576123"));
        var (second, dup2) = await LabelQueue.CreateAsync(_db, new CreateLabelJobRequest("Jens Peeters", "TikTok", "576123"));

        Assert.False(dup1);
        Assert.True(dup2);
        Assert.Equal(first.Id, second.Id);
        Assert.Single(await LabelQueue.PendingAsync(_db));
    }

    [Fact]
    public async Task Only_one_pc_can_claim_and_printed_jobs_disappear()
    {
        var (job, _) = await LabelQueue.CreateAsync(_db, new CreateLabelJobRequest("Lotte", "shopify", "12"));

        Assert.True(await LabelQueue.ClaimAsync(_db, job.Id, "PC-1"));
        Assert.False(await LabelQueue.ClaimAsync(_db, job.Id, "PC-2"));
        Assert.Empty(await LabelQueue.PendingAsync(_db)); // geclaimd → niet meer uitgedeeld

        await LabelQueue.CompleteAsync(_db, job.Id, null);
        _db.ChangeTracker.Clear();
        Assert.NotNull((await _db.LabelJobs.SingleAsync()).PrintedAt);
    }

    [Fact]
    public async Task Shopify_queue_lists_only_live_open_webshop_orders_since_start()
    {
        var sync = new SyncService(_db, new DummyShopifyClient(), new NotConfiguredTikTokShopClient(), new DummyWooCommerceClient(), new DummyBillitClient(), new StockAllocator(_db),
            Options.Create(new IntegrationOptions()), Options.Create(new StreamOptions()),
            new ComplaintService(_db, new DummyMailboxClient(), NullLogger<ComplaintService>.Instance),
            new ShopActivityService(_db), TestServices.ShopifyProducts(_db), TestServices.Paths, NullLogger<SyncService>.Instance);
        var now = DateTime.Now;
        ExternalOrder Order(string id, SalesChannel ch, string method, DateTime at) =>
            new(ch, id, "#" + id, at, "Jens Peeters", null, 0m, "paid", [new ExternalOrderLine("X", "Booster", 2, 6m)], method);

        await sync.ImportOrdersAsync([
            Order("live", SalesChannel.Shopify, "Live openen op TikTok (geen retour)", now.AddMinutes(-5)),
            Order("express", SalesChannel.Shopify, "Express", now.AddMinutes(-4)),
            Order("tiktok", SalesChannel.TikTokShop, "Shipped by Seller", now.AddMinutes(-3)),
            Order("old", SalesChannel.Shopify, "Live openen op TikTok (geen retour)", now.AddHours(-3)),
        ], historical: false);
        await _db.SaveChangesAsync();

        var live = await LabelQueue.ShopifyOrdersAsync(_db, now.AddHours(-1), onlyLive: true);
        var all = await LabelQueue.ShopifyOrdersAsync(_db, now.AddHours(-1), onlyLive: false);

        Assert.Equal("#live", Assert.Single(live).OrderNumber);
        Assert.Equal("Jens", live[0].FirstName);
        Assert.Equal(["#live", "#express"], all.Select(o => o.OrderNumber));   // TikTok niet: die haalt de rip-tool zelf
    }
}
