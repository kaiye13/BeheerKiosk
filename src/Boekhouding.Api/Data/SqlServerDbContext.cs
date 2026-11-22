using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Boekhouding.Api.Data;

/// <summary>
/// Zelfde database als <see cref="AppDbContext"/>, maar voor (Azure) SQL Server. Heeft eigen migraties
/// (Data/MigrationsSqlServer), want die van SQLite gebruiken SQLite-types.
/// </summary>
public class SqlServerDbContext(DbContextOptions<SqlServerDbContext> options) : AppDbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        // Bedragen: 4 decimalen (aankoopprijs per stuk, marges); SQLite had geen vaste precisie.
        c.Properties<decimal>().HavePrecision(18, 4);
        c.Properties<decimal?>().HavePrecision(18, 4);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        // SQL Server kan geen index op nvarchar(max): tekstvelden met een (unieke) index of sleutel krijgen een maximumlengte.
        b.Entity<Product>().Property(p => p.Sku).HasMaxLength(200);
        b.Entity<PurchaseAgreement>().Property(a => a.Number).HasMaxLength(100);
        b.Entity<SalesOrder>().Property(o => o.ExternalId).HasMaxLength(200);
        b.Entity<SyncState>().Property(s => s.Key).HasMaxLength(200);
        b.Entity<AppSetting>().Property(s => s.Key).HasMaxLength(200);
        b.Entity<Complaint>().Property(c => c.ExternalMessageId).HasMaxLength(450);
        b.Entity<ShopSession>().Property(s => s.ClientId).HasMaxLength(200);
        b.Entity<LabelJob>().Property(j => j.Source).HasMaxLength(100);
        b.Entity<LabelJob>().Property(j => j.Reference).HasMaxLength(300);
    }
}

/// <summary>Enkel voor "dotnet ef migrations add --context SqlServerDbContext" (zonder echte server).</summary>
public class SqlServerDesignTimeFactory : IDesignTimeDbContextFactory<SqlServerDbContext>
{
    public SqlServerDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqlServerDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=design-time;Trusted_Connection=True").Options);
}
