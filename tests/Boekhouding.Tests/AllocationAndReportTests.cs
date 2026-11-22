using Boekhouding.Api.Data;
using Boekhouding.Api.Integrations;
using Boekhouding.Api.Services;
using Boekhouding.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Boekhouding.Tests;

public sealed class AllocationAndReportTests : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    private readonly AppDbContext _db;
    private readonly PurchaseService _purchases;
    private readonly SyncService _sync;

    public AllocationAndReportTests()
    {
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        var allocator = new StockAllocator(_db);
        _purchases = new PurchaseService(_db, allocator);
        _sync = new SyncService(_db, new DummyShopifyClient(), new NotConfiguredTikTokShopClient(), new DummyWooCommerceClient(), new DummyBillitClient(), allocator,
            Options.Create(new IntegrationOptions()), Options.Create(new StreamOptions()),
            new ComplaintService(_db, new DummyMailboxClient(), NullLogger<ComplaintService>.Instance),
            new ShopActivityService(_db), TestServices.ShopifyProducts(_db), TestServices.Paths, NullLogger<SyncService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    private static readonly DateTime Day = new(2026, 9, 1);

    private async Task StockAsync()
    {
        await _purchases.CreateInvoicePurchaseAsync(new CreateInvoicePurchaseRequest
        {
            SupplierName = "Leverancier", InvoiceNumber = "F1", InvoiceDate = Day, VatType = PurchaseVatType.Belgian21,
            NetAmount = 300m, VatAmount = 63m,
            Lines = [new PurchaseLineRequest { Sku = "BOX", Title = "Box", Quantity = 5, UnitCost = 60m, SalePrice = 121m }],
        });
        await _purchases.CreateMarginPurchaseAsync(new CreateMarginPurchaseRequest
        {
            SellerName = "Particulier", SellerAddress = "Straat 1, 9000 Gent", Date = Day.AddDays(3),
            Lines = [new PurchaseLineRequest { Sku = "BOX", Title = "Box", Quantity = 2, UnitCost = 80m }],
        });
    }

    private Task<int> SellAsync(string id, int qty, decimal price, DateTime date) =>
        _sync.ImportOrdersAsync([new ExternalOrder(SalesChannel.Shopify, id, id, date, "Klant", null, 0m, "paid",
            [new ExternalOrderLine("BOX", "Box", qty, price)])], historical: false);

    [Fact]
    public async Task Margin_goods_are_sold_first_even_if_invoice_stock_is_older()
    {
        await StockAsync();
        await SellAsync("o1", 3, 100m, Day.AddDays(5));
        await _db.SaveChangesAsync();

        var line = await _db.SalesOrderLines.Include(l => l.Allocations).SingleAsync();
        Assert.Equal(0, line.UnallocatedQuantity);
        var margin = line.Allocations.Single(a => a.Regime == VatRegime.Margin);
        var invoice = line.Allocations.Single(a => a.Regime == VatRegime.Invoice);
        Assert.Equal(2, margin.Quantity);
        Assert.Equal(1, invoice.Quantity);
        Assert.Equal(Vat.OnMargin(200m, 160m), margin.VatAmount);     // marge 40 → 6,94
        Assert.Equal(Vat.FromInclusive(100m), invoice.VatAmount);     // 17,36

        var lots = await _db.StockLots.ToListAsync();
        Assert.Equal(0, lots.Single(l => l.Regime == VatRegime.Margin).QuantityRemaining);
        Assert.Equal(4, lots.Single(l => l.Regime == VatRegime.Invoice).QuantityRemaining);
    }

    [Fact]
    public async Task Overselling_leaves_unallocated_quantity_taxed_at_21_percent()
    {
        await StockAsync();
        await SellAsync("o1", 9, 100m, Day.AddDays(5));
        await _db.SaveChangesAsync();

        var line = await _db.SalesOrderLines.SingleAsync();
        Assert.Equal(2, line.UnallocatedQuantity);

        var report = await new ReportService(_db).BuildMonthlyAsync(2026, 9);
        Assert.Equal(200m, report.UnallocatedRevenueIncl);
        Assert.Equal(Vat.FromInclusive(200m), report.UnallocatedVat);
        Assert.Contains(report.Warnings, w => w.Contains("zonder gekende voorraad"));
    }

    [Fact]
    public async Task New_stock_allocates_earlier_unallocated_sales_in_same_month()
    {
        await SellAsync("o1", 1, 100m, Day.AddDays(1));
        await _db.SaveChangesAsync();
        Assert.Equal(1, (await _db.SalesOrderLines.SingleAsync()).UnallocatedQuantity);

        await StockAsync();

        var line = await _db.SalesOrderLines.Include(l => l.Allocations).SingleAsync();
        Assert.Equal(0, line.UnallocatedQuantity);
        Assert.Single(line.Allocations);
    }

    [Fact]
    public async Task Orders_before_go_live_count_as_revenue_but_take_no_stock()
    {
        await StockAsync();
        await _sync.ImportOrdersAsync([new ExternalOrder(SalesChannel.Shopify, "old", "#1", Day.AddDays(5), "Klant", null, 0m, "paid",
            [new ExternalOrderLine("BOX", "Box", 2, 100m)])], historical: false, historicalBefore: Day.AddDays(10));
        await _db.SaveChangesAsync();

        var order = await _db.SalesOrders.Include(o => o.Lines).SingleAsync();
        Assert.True(order.IsHistorical);
        Assert.Equal(FulfillmentStage.New, order.Stage); // Shopify-order blijft op te volgen
        Assert.Equal(7, (await _db.StockLots.ToListAsync()).Sum(l => l.QuantityRemaining));
    }

    [Fact]
    public async Task Cancelled_order_gives_stock_back()
    {
        await StockAsync();
        await SellAsync("o1", 3, 100m, Day.AddDays(5));
        await _db.SaveChangesAsync();
        Assert.Equal(4, (await _db.StockLots.ToListAsync()).Sum(l => l.QuantityRemaining));

        await _sync.ImportOrdersAsync([new ExternalOrder(SalesChannel.Shopify, "o1", "o1", Day.AddDays(5), "Klant", null, 0m, "refunded",
            [new ExternalOrderLine("BOX", "Box", 3, 100m)])], historical: false);
        await _db.SaveChangesAsync();

        Assert.Equal(7, (await _db.StockLots.ToListAsync()).Sum(l => l.QuantityRemaining));
        Assert.Empty(await _db.SaleAllocations.ToListAsync());
        var report = await new ReportService(_db).BuildMonthlyAsync(2026, 9);
        Assert.Equal(0m, report.TotalRevenueIncl);
    }

    [Fact]
    public async Task Duplicate_orders_are_not_imported_twice()
    {
        await StockAsync();
        Assert.Equal(1, await SellAsync("o1", 1, 100m, Day.AddDays(5)));
        await _db.SaveChangesAsync();
        Assert.Equal(0, await SellAsync("o1", 1, 100m, Day.AddDays(5)));
    }

    [Fact]
    public async Task Monthly_report_balances_due_and_deductible_vat()
    {
        await StockAsync();
        await _purchases.CreateInvoicePurchaseAsync(new CreateInvoicePurchaseRequest
        {
            SupplierName = "Polen", InvoiceNumber = "PL1", Country = "PL", InvoiceDate = Day, VatType = PurchaseVatType.IntraCommunity0,
            NetAmount = 1000m,
        });
        await SellAsync("o1", 3, 100m, Day.AddDays(5));
        await _db.SaveChangesAsync();

        var r = await new ReportService(_db).BuildMonthlyAsync(2026, 9);

        Assert.Equal(200m, r.MarginSalesRevenue);
        Assert.Equal(160m, r.MarginSalesCost);
        Assert.Equal(40m, r.MarginProfit);
        Assert.Equal(160m, r.MarginPurchasesTotal);
        Assert.Equal(63m, r.PurchasesBelgianVat);
        Assert.Equal(210m, r.IntraCommunityVatReverseCharged);
        // Verlegde btw staat aan beide kanten → heft zichzelf op.
        Assert.Equal(r.MarginVat + r.InvoiceSalesVat + 210m, r.VatDueTotal);
        Assert.Equal(63m + 210m, r.VatDeductibleTotal);
        Assert.Equal(r.MarginVat + r.InvoiceSalesVat - 63m, r.VatBalance);
    }

    [Fact]
    public async Task Quarter_report_adds_up_its_months()
    {
        await StockAsync();
        await SellAsync("sep", 1, 100m, new DateTime(2026, 9, 20));
        await SellAsync("aug", 2, 100m, new DateTime(2026, 8, 5));
        await _db.SaveChangesAsync();
        var reports = new ReportService(_db);

        var quarter = await reports.BuildAsync(new DateTime(2026, 7, 1), new DateTime(2026, 10, 1));
        var aug = await reports.BuildMonthlyAsync(2026, 8);
        var sep = await reports.BuildMonthlyAsync(2026, 9);

        Assert.Equal("Q3 2026", quarter.PeriodLabel);
        Assert.Equal(aug.TotalRevenueIncl + sep.TotalRevenueIncl, quarter.TotalRevenueIncl);
        Assert.Equal(aug.VatBalance + sep.VatBalance, quarter.VatBalance);
    }

    [Theory]
    [InlineData("2026-11-01", "2026-12-01", "november 2026")]
    [InlineData("2026-10-01", "2027-01-01", "Q4 2026")]
    [InlineData("2026-01-01", "2027-01-01", "2026")]
    [InlineData("2026-11-08", "2026-11-09", "8 november 2026")]
    [InlineData("2026-11-02", "2026-11-09", "02/11/2026 – 08/11/2026")]
    public void Period_labels(string from, string to, string expected) =>
        Assert.Equal(expected, ReportService.PeriodLabel(DateTime.Parse(from), DateTime.Parse(to)));

    [Fact]
    public async Task Agreements_get_sequential_numbers_per_year()
    {
        await StockAsync();
        var second = await _purchases.CreateMarginPurchaseAsync(new CreateMarginPurchaseRequest
        {
            SellerName = "Iemand", Date = Day,
            Lines = [new PurchaseLineRequest { Title = "Nieuw lot", Quantity = 1, UnitCost = 5m }],
        });
        Assert.Equal("VO-2026-0002", second.Number);
        Assert.Null(second.SellerAddress);
    }
}
