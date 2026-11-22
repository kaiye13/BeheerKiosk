using Microsoft.EntityFrameworkCore;

namespace Boekhouding.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockLot> StockLots => Set<StockLot>();
    public DbSet<PurchaseInvoice> PurchaseInvoices => Set<PurchaseInvoice>();
    public DbSet<PurchaseAgreement> PurchaseAgreements => Set<PurchaseAgreement>();
    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<SalesOrderLine> SalesOrderLines => Set<SalesOrderLine>();
    public DbSet<SaleAllocation> SaleAllocations => Set<SaleAllocation>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();
    public DbSet<LiveStream> Streams => Set<LiveStream>();
    public DbSet<Complaint> Complaints => Set<Complaint>();
    public DbSet<ComplaintMessage> ComplaintMessages => Set<ComplaintMessage>();
    public DbSet<ShopSession> ShopSessions => Set<ShopSession>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<LabelJob> LabelJobs => Set<LabelJob>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Product>().HasIndex(p => p.Sku).IsUnique();
        b.Entity<Product>().HasIndex(p => p.ShopifyVariantId);
        b.Entity<PurchaseAgreement>().HasIndex(a => a.Number).IsUnique();
        b.Entity<SalesOrder>().HasIndex(o => new { o.Channel, o.ExternalId }).IsUnique();
        b.Entity<SyncState>().HasKey(s => s.Key);
        b.Entity<AppSetting>().HasKey(s => s.Key);
        b.Entity<Complaint>().HasIndex(c => c.ExternalMessageId).IsUnique();
        b.Entity<ShopSession>().HasIndex(s => s.ClientId).IsUnique();
        b.Entity<SalesOrder>().HasIndex(o => o.OrderDate);
        b.Entity<LabelJob>().HasIndex(j => new { j.Source, j.Reference }).IsUnique();

        b.Entity<SaleAllocation>()
            .HasOne(a => a.StockLot).WithMany()
            .OnDelete(DeleteBehavior.Restrict);
    }
}
