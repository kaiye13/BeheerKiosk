using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Boekhouding.Api.Data;

/// <summary>
/// Eenmalige overzetting van de bestaande SQLite-database (boekhouding.db) naar Azure SQL.
/// Alle tabellen worden met hun eigen ID's gekopieerd (zodat verkopen, voorraadpartijen en facturen
/// aan elkaar gekoppeld blijven). Weigert als de Azure SQL-database al gegevens bevat.
/// </summary>
public static class SqliteImport
{
    public static async Task RunAsync(string sqlitePath, string sqlConnection)
    {
        if (!File.Exists(sqlitePath)) throw new FileNotFoundException($"SQLite-database niet gevonden: {sqlitePath}");

        await using var source = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={sqlitePath};Mode=ReadOnly").Options);
        if ((await source.Database.GetPendingMigrationsAsync()).Any())
            throw new InvalidOperationException("De SQLite-database is niet bijgewerkt: start de lokale server één keer en probeer opnieuw.");

        await using var target = new SqlServerDbContext(new DbContextOptionsBuilder<SqlServerDbContext>()
            .UseSqlServer(sqlConnection).Options);
        Console.WriteLine("Azure SQL: tabellen aanmaken/bijwerken…");
        await target.Database.MigrateAsync();

        var types = InsertOrder(target.Model);
        foreach (var t in types)
        {
#pragma warning disable EF1002 // tabelnaam komt uit het EF-model, niet van een gebruiker
            var count = await target.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS [Value] FROM {Table(t)}").SingleAsync();
#pragma warning restore EF1002
            if (count > 0)
                throw new InvalidOperationException($"De Azure SQL-database bevat al gegevens ({t.GetTableName()}: {count} rijen). " +
                                                    "Overzetten gebeurt enkel naar een lege database.");
        }

        var connection = (SqlConnection)target.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        var total = 0;
        foreach (var t in types)
        {
            var rows = await LoadAsync(source, t);
            var table = ToDataTable(t, rows, source);
            using var bulk = new SqlBulkCopy(connection,
                SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.CheckConstraints, transaction)
            {
                DestinationTableName = Table(t),
                BatchSize = 1000,
                BulkCopyTimeout = 300,
            };
            foreach (DataColumn c in table.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
            await bulk.WriteToServerAsync(table);
            Console.WriteLine($"  {t.GetTableName(),-22} {table.Rows.Count,6} rijen");
            total += table.Rows.Count;
        }
        await transaction.CommitAsync();
        Console.WriteLine($"Klaar: {total} rijen overgezet naar Azure SQL.");
    }

    private static string Table(IEntityType t) =>
        t.GetSchema() is { } schema ? $"[{schema}].[{t.GetTableName()}]" : $"[{t.GetTableName()}]";

    /// <summary>Tabellen in een volgorde waarin elke verwijzing (foreign key) al bestaat.</summary>
    private static List<IEntityType> InsertOrder(IModel model)
    {
        var all = model.GetEntityTypes().Where(t => !t.IsOwned() && t.GetTableName() is not null).ToList();
        var ordered = new List<IEntityType>();
        while (ordered.Count < all.Count)
        {
            var next = all.Where(t => !ordered.Contains(t)
                                      && t.GetForeignKeys().All(fk => fk.PrincipalEntityType == t || ordered.Contains(fk.PrincipalEntityType)))
                .ToList();
            if (next.Count == 0) throw new InvalidOperationException("Kringverwijzing tussen tabellen: volgorde niet te bepalen.");
            ordered.AddRange(next);
        }
        return ordered;
    }

    private static async Task<List<object>> LoadAsync(DbContext source, IEntityType t)
    {
        var set = typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!.MakeGenericMethod(t.ClrType).Invoke(source, null)!;
        var list = new List<object>();
        await foreach (var e in (IAsyncEnumerable<object>)set) list.Add(e);
        // Zelfverwijzingen eerst de "ouders".
        return list.OrderBy(e => source.Entry(e).Property(t.FindPrimaryKey()!.Properties[0].Name).CurrentValue as IComparable).ToList();
    }

    private static DataTable ToDataTable(IEntityType t, List<object> rows, DbContext source)
    {
        var store = StoreObjectIdentifier.Table(t.GetTableName()!, t.GetSchema());
        var props = t.GetProperties().Where(p => p.GetColumnName(store) is not null).ToList();
        var table = new DataTable();
        foreach (var p in props)
        {
            var type = p.GetValueConverter()?.ProviderClrType ?? p.ClrType;
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type.IsEnum) type = Enum.GetUnderlyingType(type);
            table.Columns.Add(p.GetColumnName(store)!, type);
        }
        foreach (var e in rows)
        {
            var entry = source.Entry(e);
            var values = props.Select(p =>
            {
                var v = entry.Property(p.Name).CurrentValue;
                if (v is null) return DBNull.Value;
                if (p.GetValueConverter() is { } conv) v = conv.ConvertToProvider(v);
                if (v is Enum en) v = Convert.ChangeType(en, Enum.GetUnderlyingType(en.GetType()));
                return v ?? DBNull.Value;
            }).ToArray();
            table.Rows.Add(values);
        }
        return table;
    }
}
