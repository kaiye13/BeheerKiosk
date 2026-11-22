using Microsoft.EntityFrameworkCore;

namespace Boekhouding.Api.Data;

/// <summary>
/// Brengt de database bij het opstarten op de laatste versie via EF-migraties (Data/Migrations).
/// Een database van vóór de migraties (testversie, aangemaakt met EnsureCreated) wordt in dummy-modus
/// opnieuw opgebouwd; met echte data weigert de server te starten in plaats van gegevens te verliezen.
/// </summary>
public static class SchemaGuard
{
    public static async Task EnsureAsync(AppDbContext db, bool dummy, ILogger log)
    {
        var (tables, hasHistory) = await InspectAsync(db);
        if (tables > 0 && !hasHistory)
        {
            if (!dummy)
                throw new InvalidOperationException(
                    "De database is aangemaakt door een testversie van de app en bevat waarschijnlijk dummy-data. " +
                    "Stop de server, verwijder of hernoem boekhouding.db in de datamap en start opnieuw.");
            log.LogWarning("Testdatabase van vóór de migraties: wordt opnieuw opgebouwd met dummy-data.");
            await db.Database.EnsureDeletedAsync();
        }

        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count > 0)
        {
            log.LogInformation("Database bijwerken: {Migrations}", string.Join(", ", pending));
            await db.Database.MigrateAsync();
        }

        await GuardDataModeAsync(db, dummy, log);
    }

    private const string DataModeKey = "DataMode";

    /// <summary>
    /// Een database onthoudt of ze testdata of echte data bevat. Wisselen van modus op dezelfde database
    /// wordt geweigerd, zodat dummy-producten/-aankopen nooit tussen echte Shopify-orders terechtkomen (en omgekeerd).
    /// </summary>
    private static async Task GuardDataModeAsync(AppDbContext db, bool dummy, ILogger log)
    {
        var wanted = dummy ? "dummy" : "live";
        var setting = await db.AppSettings.FindAsync(DataModeKey);
        if (setting is null)
        {
            // Bestaande database zonder markering: als er al producten in staan, kunnen we niet weten welke data het is.
            if (!dummy && await db.Products.AnyAsync())
                throw new InvalidOperationException(
                    "De database bevat al gegevens, maar het is niet zeker of dat echte data is. " +
                    "Stop de server, verwijder of hernoem boekhouding.db in de datamap en start opnieuw.");
            db.AppSettings.Add(new AppSetting { Key = DataModeKey, Value = wanted });
            await db.SaveChangesAsync();
            log.LogInformation("Database gemarkeerd als {Mode}", wanted);
            return;
        }
        if (setting.Value == wanted) return;

        throw new InvalidOperationException(setting.Value == "dummy"
            ? "Deze database bevat TESTDATA (dummy-modus), maar UseDummyData staat nu op false. " +
              "Stop de server, verwijder of hernoem boekhouding.db in de datamap en start opnieuw voor een lege, echte database."
            : "Deze database bevat ECHTE data, maar UseDummyData staat op true. Zet UseDummyData terug op false, " +
              "of gebruik een andere datamap (Storage:DataDirectory) om te testen.");
    }

    private static async Task<(long Tables, bool HasHistory)> InspectAsync(AppDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            async Task<long> Scalar(string sql)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                return Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }
            var tables = await Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory'");
            var history = await Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'");
            return (tables, history > 0);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
