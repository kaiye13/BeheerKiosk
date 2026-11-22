using Boekhouding.Api.Data;
using Boekhouding.Api.Integrations;
using Boekhouding.Api.Services;
using Boekhouding.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Boekhouding.Tests;

public sealed class TikTokDuplicateTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDbContext _db;

    public TikTokDuplicateTests()
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

    private sealed class NoShopify : IShopifyClient
    {
        public Task<IReadOnlyList<ExternalOrder>> GetOrdersSinceAsync(DateTime since, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExternalOrder>>([]);
        public Task<IReadOnlyList<AbandonedCheckoutDto>> GetAbandonedCheckoutsAsync(DateTime since, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AbandonedCheckoutDto>>([]);
    }

    private sealed class FakeTikTok(params ExternalOrder[] orders) : ITikTokShopClient
    {
        public bool IsConfigured => true;
        public Task<IReadOnlyList<ExternalOrder>> GetOrdersSinceAsync(DateTime since, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExternalOrder>>(orders);
    }

    private static ExternalOrder Order(string id, string number, DateTime at, string name, decimal price) =>
        new(SalesChannel.TikTokShop, id, number, at, name, null, 0m, "paid", [new ExternalOrderLine("X", "Booster", 1, price)], "TikTok");

    [Fact]
    public async Task Copy_via_shopify_of_an_existing_tiktok_order_is_removed_other_orders_stay()
    {
        var at = DateTime.Now.AddHours(-2);
        var tiktokOrder = Order("577000000000000001", "TT 577000000000000001", at, "Elien Borms", 20m);
        var sync = new SyncService(_db, new NoShopify(), new FakeTikTok(tiktokOrder), new DummyWooCommerceClient(), new DummyBillitClient(),
            new StockAllocator(_db), Options.Create(new IntegrationOptions { UseDummyData = false, TikTokSyncIntervalSeconds = 5 }),
            Options.Create(new StreamOptions()), new ComplaintService(_db, new DummyMailboxClient(), NullLogger<ComplaintService>.Instance),
            new ShopActivityService(_db), TestServices.ShopifyProducts(_db), TestServices.Paths, NullLogger<SyncService>.Instance);

        // Al rechtstreeks binnen + kopie via Shopify (5 min later) + een andere klant via Shopify.
        await sync.ImportOrdersAsync([
            tiktokOrder,
            Order("gid://shopify/Order/1", "#KIOSK1391", at.AddMinutes(5), "elien borms", 18m),
            Order("gid://shopify/Order/2", "#KIOSK1390", at.AddMinutes(10), "Beyza Kaya", 15.99m),
        ], historical: false);
        await _db.SaveChangesAsync();

        await sync.RunOrdersAsync();

        var numbers = await _db.SalesOrders.Select(o => o.OrderNumber).OrderBy(n => n).ToListAsync();
        Assert.Equal(["#KIOSK1390", "TT 577000000000000001"], numbers);
        Assert.Equal(2, await _db.SalesOrderLines.CountAsync());
    }
}
