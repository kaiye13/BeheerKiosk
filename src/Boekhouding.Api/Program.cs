using Boekhouding.Api.Data;
using Boekhouding.Api.Integrations;
using Boekhouding.Api.Services;
using Boekhouding.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Als Windows-service is de werkmap System32; config staat naast de exe.
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Services.AddWindowsService(o => o.ServiceName = "ShipRipperBoekhouding");

// Azure App Service: de poort komt van Azure (Linux: PORT / ASPNETCORE_HTTP_PORTS, meestal 8080).
// "Urls" uit appsettings.json (http://0.0.0.0:5080, voor de lokale server) mag die niet overschrijven,
// anders antwoordt de app nooit en geeft Azure 503. Op Windows (IIS in-process) heeft dit geen effect.
if (Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") is not null)
{
    var port = Environment.GetEnvironmentVariable("PORT") ?? Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080";
    builder.WebHost.UseUrls($"http://+:{port.Split(';')[0]}");
}

// Datamap; "%HOME%\data\…" werkt zo op Windows én Linux (backslashes worden de juiste scheidingstekens).
var dataDir = Environment.ExpandEnvironmentVariables(builder.Configuration["Storage:DataDirectory"] ?? @"%ProgramData%\ShipRipperBoekhouding");
if (!OperatingSystem.IsWindows()) dataDir = dataDir.Replace('\\', '/');
var uploadDir = Path.Combine(dataDir, "uploads");
Directory.CreateDirectory(uploadDir);

builder.Services.AddSingleton(new AppPaths(dataDir, uploadDir));
// Database: met ConnectionStrings:Default (Azure: Verbindingsreeksen → "Default", type SQLAzure) Azure SQL,
// anders het SQLite-bestand in de datamap.
var sqlConnection = builder.Configuration.GetConnectionString("Default");
var sqlitePath = Path.Combine(dataDir, "boekhouding.db");
if (!string.IsNullOrWhiteSpace(sqlConnection))
    builder.Services.AddDbContext<AppDbContext, SqlServerDbContext>(o =>
        o.UseSqlServer(sqlConnection, sql => sql.EnableRetryOnFailure()));
else
    builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={sqlitePath}"));

// Eenmalig: bestaande SQLite-database overzetten naar Azure SQL en stoppen.
//   ShipRipperServer.exe --import-sqlite "C:\ProgramData\ShipRipperBoekhouding\boekhouding.db"
if (args.SkipWhile(a => a != "--import-sqlite").Skip(1).FirstOrDefault() is { } importFrom)
{
    if (string.IsNullOrWhiteSpace(sqlConnection))
        throw new InvalidOperationException("Zet eerst ConnectionStrings:Default (de Azure SQL-verbindingsreeks).");
    await SqliteImport.RunAsync(importFrom, sqlConnection);
    return;
}
builder.Services.Configure<IntegrationOptions>(builder.Configuration.GetSection(IntegrationOptions.Section));
builder.Services.Configure<CompanyOptions>(builder.Configuration.GetSection(CompanyOptions.Section));

var useDummy = builder.Configuration.GetValue($"{IntegrationOptions.Section}:{nameof(IntegrationOptions.UseDummyData)}", true);
// Dummy-modus: alles gesimuleerd. Productie: echte Shopify; wat nog niet ingesteld is levert niets op
// (nooit testgegevens in de echte boekhouding).
if (useDummy)
{
    builder.Services.AddSingleton<IShopifyClient, DummyShopifyClient>();
    builder.Services.AddSingleton<IWooCommerceClient, DummyWooCommerceClient>();
    builder.Services.AddSingleton<IBillitClient, DummyBillitClient>();
}
else
{
    builder.Services.AddSingleton<IShopifyClient, ShopifyClient>();
    builder.Services.AddSingleton<IWooCommerceClient, NotConfiguredWooCommerceClient>();
    if (string.IsNullOrWhiteSpace(builder.Configuration[$"{IntegrationOptions.Section}:{nameof(IntegrationOptions.BillitApiKey)}"]))
        builder.Services.AddSingleton<IBillitClient, NotConfiguredBillitClient>();
    else
        builder.Services.AddHttpClient<IBillitClient, BillitClient>(c => c.Timeout = TimeSpan.FromSeconds(60));
}

// TikTok Shop rechtstreeks (één instantie: tokens en winkels worden hergebruikt).
if (!useDummy && !string.IsNullOrWhiteSpace(builder.Configuration[$"{IntegrationOptions.Section}:{nameof(IntegrationOptions.TikTokAppKey)}"]))
{
    builder.Services.AddHttpClient("tiktok", c => c.Timeout = TimeSpan.FromSeconds(30));
    builder.Services.AddSingleton<ITikTokShopClient>(sp => new TikTokShopClient(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("tiktok"),
        sp.GetRequiredService<IOptions<IntegrationOptions>>(),
        sp.GetRequiredService<AppPaths>(),
        sp.GetRequiredService<ILogger<TikTokShopClient>>()));
}
else
    builder.Services.AddSingleton<ITikTokShopClient, NotConfiguredTikTokShopClient>();

// Eén gedeelde instantie, zodat de Shopify-token (24 u geldig) hergebruikt wordt.
builder.Services.AddHttpClient("shopify", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton(sp => new ShopifyTokenProvider(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("shopify"),
    sp.GetRequiredService<IOptions<IntegrationOptions>>()));

builder.Services.Configure<MailOptions>(builder.Configuration.GetSection(MailOptions.Section));
builder.Services.Configure<StreamOptions>(builder.Configuration.GetSection(StreamOptions.Section));
// Klachtenmailbox: echte IMAP/SMTP zodra een server ingesteld is, ook als de rest nog dummy is.
if (!string.IsNullOrWhiteSpace(builder.Configuration[$"{MailOptions.Section}:{nameof(MailOptions.ImapHost)}"]))
    builder.Services.AddSingleton<IMailboxClient, MailKitMailboxClient>();
else if (useDummy)
    builder.Services.AddSingleton<IMailboxClient, DummyMailboxClient>();
else
    builder.Services.AddSingleton<IMailboxClient, NotConfiguredMailboxClient>();

builder.Services.AddScoped<StockAllocator>();
builder.Services.AddScoped<PurchaseService>();
builder.Services.AddScoped<SyncService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<ExcelExporter>();
builder.Services.AddScoped<ComplaintService>();
builder.Services.AddScoped<ShopActivityService>();
builder.Services.AddScoped<StreamService>();
builder.Services.AddScoped<ShopifyProductService>();
builder.Services.AddHostedService<SyncBackgroundService>();

var app = builder.Build();

// Database klaarzetten. Lukt dat niet (bv. Azure SQL-firewall), dan start de server tóch, zodat
// /api/health de reden toont in plaats van dat Azure enkel "503" geeft.
string? databaseError = null;
var databaseKind = string.IsNullOrWhiteSpace(sqlConnection) ? "SQLite" : "Azure SQL";
try
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await SchemaGuard.EnsureAsync(db, useDummy, app.Logger).WaitAsync(TimeSpan.FromSeconds(75));
    if (useDummy) await DummySeeder.SeedAsync(db, scope.ServiceProvider.GetRequiredService<PurchaseService>());
}
catch (Exception ex)
{
    var messages = new List<string>();
    for (var e = ex; e is not null; e = e.InnerException) messages.Add(e.Message);
    databaseError = ex is TimeoutException
        ? $"{databaseKind} antwoordt niet binnen 75 s (firewall of netwerk?)."
        : string.Join(" → ", messages.Distinct());
    app.Logger.LogError(ex, "Database niet klaar bij het opstarten ({Kind})", databaseKind);
}

// Fouten uit validatie → 400 met leesbare melding voor de client.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (ValidationException ex) { await Results.Problem(ex.Message, statusCode: 400).ExecuteAsync(ctx); }
});

// Eenvoudige gedeelde sleutel voor het lokale netwerk (header X-Api-Key, of ?key= voor links in de browser).
var apiKey = app.Configuration["Security:ApiKey"];
app.Use(async (ctx, next) =>
{
    if (!string.IsNullOrEmpty(apiKey) && ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Path != "/api/health")
    {
        var given = ctx.Request.Headers["X-Api-Key"].FirstOrDefault() ?? ctx.Request.Query["key"].FirstOrDefault();
        if (given != apiKey)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsync("Ongeldige of ontbrekende API-sleutel.");
            return;
        }
    }
    await next();
});

var api = app.MapGroup("/api");

api.MapGet("/health", (IOptions<IntegrationOptions> o) => new
{
    status = databaseError is null ? "ok" : "fout",
    database = databaseKind,
    databaseError,
    dummy = o.Value.UseDummyData,
    time = DateTime.Now,
});

// ---------- Producten & voorraad ----------

api.MapGet("/products", async (AppDbContext db) =>
    (await db.Products.Include(p => p.Lots).OrderBy(p => p.Title).ToListAsync()).Select(p => p.ToDto()));

api.MapGet("/products/{id:int}", async (int id, AppDbContext db) =>
    await db.Products.Include(p => p.Lots).FirstOrDefaultAsync(p => p.Id == id) is { } p ? Results.Ok(p.ToDto()) : Results.NotFound());

api.MapPut("/products/{id:int}", async (int id, bool? pushToShopify, UpdateProductRequest req, AppDbContext db, ShopifyProductService shopify) =>
{
    var p = await db.Products.Include(x => x.Lots).FirstOrDefaultAsync(x => x.Id == id);
    if (p is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(req.Title)) throw new ValidationException("Titel is verplicht.");
    (p.Title, p.Description, p.Vendor, p.ProductType, p.Tags, p.Barcode, p.WeightGrams, p.Price, p.CompareAtPrice, p.Status) =
        (req.Title, req.Description, req.Vendor, req.ProductType, req.Tags, req.Barcode, req.WeightGrams, req.Price, req.CompareAtPrice, req.Status);
    p.TrackStock = req.TrackStock;
    p.RequiresShipping = req.RequiresShipping;
    if (!p.RequiresShipping) p.TrackStock = false; // digitaal: geen voorraad
    await db.SaveChangesAsync();
    if (pushToShopify == true && shopify.IsEnabled && p.ShopifyProductId is not null)
    {
        try { await shopify.PushAsync(p); }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            return Results.Problem($"Opgeslagen in de boekhouding, maar Shopify is niet bijgewerkt: {ex.Message}", statusCode: 502);
        }
    }
    return Results.Ok(p.ToDto());
});

// Vrij product (los van een aankoop): mystery packs, singles, bundels, ...
api.MapPost("/products", async (CreateProductRequest req, AppDbContext db, ShopifyProductService shopify) =>
{
    if (string.IsNullOrWhiteSpace(req.Title)) throw new ValidationException("Titel is verplicht.");
    if (req.Price < 0) throw new ValidationException("Prijs mag niet negatief zijn.");
    var sku = string.IsNullOrWhiteSpace(req.Sku) ? PurchaseService.GenerateSku() : req.Sku.Trim();
    if (await db.Products.AnyAsync(p => p.Sku == sku)) throw new ValidationException($"SKU '{sku}' bestaat al.");

    var p = new Product
    {
        Sku = sku, Title = req.Title.Trim(), Description = req.Description, Vendor = req.Vendor, ProductType = req.ProductType,
        Tags = req.Tags, Barcode = req.Barcode, WeightGrams = req.WeightGrams, Price = req.Price, CompareAtPrice = req.CompareAtPrice,
        Status = string.IsNullOrWhiteSpace(req.Status) ? "draft" : req.Status,
        RequiresShipping = req.RequiresShipping, TrackStock = req.RequiresShipping && req.TrackStock,
    };
    db.Products.Add(p);
    await db.SaveChangesAsync();

    string? shopifyMessage = null;
    if (req.PushToShopify)
    {
        if (!shopify.IsEnabled) shopifyMessage = "Niet naar Shopify gestuurd: Shopify is niet actief (dummy-modus of geen sleutels).";
        else
        {
            try
            {
                await shopify.PushAsync(p);
                shopifyMessage = $"Aangemaakt in Shopify ({p.Status}).";
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                shopifyMessage = $"⚠ Opgeslagen in de boekhouding, maar niet in Shopify: {ex.Message}";
            }
        }
    }
    await db.Entry(p).Collection(x => x.Lots).LoadAsync();
    return Results.Ok(p.ToDto() with { ShopifyMessage = shopifyMessage });
});

// Actief / inactief zetten (active = zichtbaar in de winkel, draft = inactief, archived = gearchiveerd).
// Gekoppeld aan Shopify → daar mee aangepast. De geschiedenis in de boekhouding blijft altijd bewaard.
api.MapPost("/products/{id:int}/status", async (int id, string value, AppDbContext db, ShopifyProductService shopify) =>
{
    if (value is not ("active" or "draft" or "archived")) throw new ValidationException("Status moet active, draft of archived zijn.");
    var p = await db.Products.Include(x => x.Lots).FirstOrDefaultAsync(x => x.Id == id);
    if (p is null) return Results.NotFound();
    if (value == "active" && p.Price <= 0) throw new ValidationException("Vul eerst een verkoopprijs in voor je het product actief zet.");
    p.Status = value;
    await db.SaveChangesAsync();
    string? message = null;
    if (p.ShopifyProductId is not null && shopify.IsEnabled)
    {
        try
        {
            await shopify.SetStatusAsync(p);
            message = "Ook in Shopify aangepast.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            message = $"⚠ Aangepast in de boekhouding, maar niet in Shopify: {ex.Message}";
        }
    }
    return Results.Ok(p.ToDto() with { ShopifyMessage = message });
});

// ---------- Shopify-producten ----------

api.MapPost("/shopify/products/import", async (ShopifyProductService shopify) =>
{
    if (!shopify.IsEnabled) throw new ValidationException("Shopify is niet actief (dummy-modus of geen sleutels ingesteld).");
    try { return Results.Ok(new { changed = await shopify.ImportAsync() }); }
    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
});

// Product aanmaken in Shopify (als het nog niet gekoppeld is) of bijwerken.
api.MapPost("/products/{id:int}/shopify", async (int id, AppDbContext db, ShopifyProductService shopify) =>
{
    if (!shopify.IsEnabled) throw new ValidationException("Shopify is niet actief (dummy-modus of geen sleutels ingesteld).");
    var p = await db.Products.Include(x => x.Lots).FirstOrDefaultAsync(x => x.Id == id);
    if (p is null) return Results.NotFound();
    if (p.Price <= 0) throw new ValidationException("Vul eerst een verkoopprijs in.");
    int added;
    try { added = await shopify.PushAsync(p); }
    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
    return Results.Ok(p.ToDto() with { ShopifyMessage = added > 0 ? $"In Shopify gezet met voorraad {added}." : "Shopify bijgewerkt." });
});

api.MapGet("/stock-lots", async (AppDbContext db) =>
    (await db.StockLots.Include(l => l.Product).Include(l => l.PurchaseAgreement).Include(l => l.PurchaseInvoice)
        .OrderByDescending(l => l.PurchaseDate).ToListAsync()).Select(l => l.ToDto()));

// ---------- Aankopen ----------

api.MapPost("/purchases/margin", async (CreateMarginPurchaseRequest req, PurchaseService svc, ShopifyProductService shopify) =>
{
    var agreement = await svc.CreateMarginPurchaseAsync(req);
    var messages = req.SyncToShopify ? await shopify.SyncAfterPurchaseAsync(agreement.Lots) : [];
    return Results.Ok(agreement.ToDto() with { ShopifyMessages = messages });
});

api.MapPost("/purchases/invoice", async (CreateInvoicePurchaseRequest req, PurchaseService svc, ShopifyProductService shopify) =>
{
    var invoice = await svc.CreateInvoicePurchaseAsync(req);
    var messages = req.SyncToShopify ? await shopify.SyncAfterPurchaseAsync(invoice.Lots) : [];
    return Results.Ok(invoice.ToDto() with { ShopifyMessages = messages });
});

api.MapGet("/purchase-agreements", async (AppDbContext db) =>
    (await db.PurchaseAgreements.Include(a => a.Lots).OrderByDescending(a => a.Date).ThenByDescending(a => a.Id).ToListAsync())
        .Select(a => a.ToDto()));

api.MapPut("/purchase-agreements/{id:int}/seller", async (int id, UpdateSellerRequest req, AppDbContext db) =>
{
    var a = await db.PurchaseAgreements.Include(x => x.Lots).FirstOrDefaultAsync(x => x.Id == id);
    if (a is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(req.SellerName)) throw new ValidationException("Naam van de verkoper is verplicht.");
    a.SellerName = req.SellerName.Trim();
    a.SellerAddress = string.IsNullOrWhiteSpace(req.SellerAddress) ? null : req.SellerAddress.Trim();
    a.SellerEmail = string.IsNullOrWhiteSpace(req.SellerEmail) ? null : req.SellerEmail.Trim();
    await db.SaveChangesAsync();
    return Results.Ok(a.ToDto());
});

api.MapGet("/purchase-agreements/{id:int}/document", async (int id, AppDbContext db, IOptions<CompanyOptions> company) =>
    await db.PurchaseAgreements.Include(a => a.Lots).ThenInclude(l => l.Product).FirstOrDefaultAsync(a => a.Id == id) is { } a
        ? Results.Content(AgreementDocument.Render(a, company.Value), "text/html; charset=utf-8")
        : Results.NotFound());

api.MapGet("/purchase-invoices", async (AppDbContext db) =>
    (await db.PurchaseInvoices.Include(i => i.Lots).OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.Id).ToListAsync())
        .Select(i => i.ToDto()));

api.MapPost("/purchase-invoices/{id:int}/file", async (int id, IFormFile file, AppDbContext db) =>
{
    var inv = await db.PurchaseInvoices.Include(i => i.Lots).FirstOrDefaultAsync(i => i.Id == id);
    if (inv is null) return Results.NotFound();
    var stored = $"{id}_{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}";
    await using (var fs = File.Create(Path.Combine(uploadDir, stored)))
        await file.CopyToAsync(fs);
    if (inv.StoredFileName is { } old) File.Delete(Path.Combine(uploadDir, old));
    inv.FileName = Path.GetFileName(file.FileName);
    inv.StoredFileName = stored;
    await db.SaveChangesAsync();
    return Results.Ok(inv.ToDto());
}).DisableAntiforgery();

api.MapGet("/purchase-invoices/{id:int}/file", async (int id, AppDbContext db) =>
{
    var inv = await db.PurchaseInvoices.FindAsync(id);
    if (inv?.StoredFileName is null) return Results.NotFound();
    var path = Path.Combine(uploadDir, inv.StoredFileName);
    return File.Exists(path) ? Results.File(path, fileDownloadName: inv.FileName) : Results.NotFound();
});

// ---------- Verkopen, dashboard, rapporten ----------

api.MapGet("/orders/{id:int}", async (int id, AppDbContext db) =>
    await db.SalesOrders.Include(o => o.Lines).ThenInclude(l => l.Allocations)
        .Include(o => o.Lines).ThenInclude(l => l.Product)
        .AsSplitQuery().FirstOrDefaultAsync(o => o.Id == id) is { } o ? Results.Ok(o.ToDetailDto()) : Results.NotFound());

// Periode via year+month, of via from+to (to = exclusief), bv. een dag, kwartaal of jaar.
api.MapGet("/orders", async (int? year, int? month, DateTime? from, DateTime? to, ReportService reports) =>
{
    var (f, t) = Period(year, month, from, to);
    return (await reports.GetOrdersAsync(f, t)).OrderByDescending(o => o.OrderDate).Select(o => o.ToDto());
});

api.MapGet("/dashboard", async (int year, int month, AppDbContext db, ReportService reports, SyncService sync) =>
{
    var orders = await reports.GetOrdersAsync(year, month);
    var report = await reports.BuildMonthlyAsync(year, month);
    var lots = await db.StockLots.Where(l => l.QuantityRemaining > 0).ToListAsync();
    var status = await sync.GetStatusAsync();
    var recentInvoices = await db.PurchaseInvoices.Include(i => i.Lots)
        .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.Id).Take(10).ToListAsync();

    decimal Revenue(SalesChannel c) => orders.Where(o => o.Channel == c).Sum(o => o.TotalIncl);
    return new DashboardDto(
        year, month,
        orders.Sum(o => o.TotalIncl),
        Revenue(SalesChannel.Shopify), Revenue(SalesChannel.TikTokShop), Revenue(SalesChannel.WooCommerce),
        orders.Count,
        report.VatBalance,
        report.MarginProfit,
        lots.Where(l => l.Regime == VatRegime.Margin).Sum(l => l.QuantityRemaining * l.UnitCost),
        lots.Where(l => l.Regime == VatRegime.Invoice).Sum(l => l.QuantityRemaining * l.UnitCost),
        await db.SalesOrderLines.CountAsync(l => l.UnallocatedQuantity > 0 && !l.SalesOrder.IsHistorical && (l.Product == null || l.Product.TrackStock)),
        await db.PurchaseAgreements.CountAsync(a => a.SellerAddress == null || a.SellerAddress == ""),
        status.LastShopifySync,
        status.UsesDummyData,
        orders.OrderByDescending(o => o.OrderDate).Take(15).Select(o => o.ToDto()).ToList(),
        recentInvoices.Select(i => i.ToDto()).ToList());
});

api.MapPost("/sync", async (SyncService sync) => await sync.RunAsync());
api.MapGet("/sync/status", async (SyncService sync) => await sync.GetStatusAsync());

api.MapGet("/reports/monthly", async (int year, int month, ReportService reports) => await reports.BuildMonthlyAsync(year, month));

api.MapGet("/reports/monthly/export", async (int year, int month, ExcelExporter exporter) =>
    Results.File(await exporter.ExportMonthAsync(year, month),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        $"btw-afrekening-{year}-{month:00}.xlsx"));

api.MapGet("/reports", async (DateTime from, DateTime to, ReportService reports) => await reports.BuildAsync(from, to));

api.MapGet("/reports/export", async (DateTime from, DateTime to, ExcelExporter exporter) =>
    Results.File(await exporter.ExportAsync(from, to),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        $"btw-afrekening-{ReportService.PeriodLabel(from, to).Replace(' ', '-').Replace('/', '-')}.xlsx"));

// ---------- Verbindingstest Shopify (alleen lezen) ----------

api.MapGet("/integrations/shopify/check", async (ShopifyTokenProvider shopify) =>
{
    try
    {
        var shop = await shopify.GraphQlAsync("{ shop { name myshopifyDomain currencyCode taxesIncluded } }", null, default);
        string? lastOrder = null, orderError = null, customerDataWarning = null;
        try
        {
            var orders = await shopify.GraphQlAsync("{ orders(first: 1, reverse: true) { nodes { name createdAt customer { displayName } } } }", null, default);
            customerDataWarning = shopify.LastPartialErrors;
            var node = orders.GetProperty("orders").GetProperty("nodes").EnumerateArray().FirstOrDefault();
            lastOrder = node.ValueKind == System.Text.Json.JsonValueKind.Object
                ? $"{node.GetProperty("name").GetString()} ({node.GetProperty("createdAt").GetString()})" : "(nog geen orders)";
        }
        catch (InvalidOperationException ex) { orderError = ex.Message; }

        return Results.Ok(new
        {
            ok = orderError is null,
            shop = shop.GetProperty("shop").GetProperty("name").GetString(),
            domain = shop.GetProperty("shop").GetProperty("myshopifyDomain").GetString(),
            currency = shop.GetProperty("shop").GetProperty("currencyCode").GetString(),
            pricesIncludeVat = shop.GetProperty("shop").GetProperty("taxesIncluded").GetBoolean(),
            grantedScopes = shopify.GrantedScopes,
            missingScopes = shopify.MissingScopes(),
            lastOrder,
            orderError,
            customerDataWarning,
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Ok(new { ok = false, error = ex.Message });
    }
});

// ---------- Livestream ----------

api.MapGet("/streams/current", async (StreamService s) => await s.GetActiveAsync() is { } st ? Results.Ok(StreamService.ToDto(st)) : Results.NoContent());
api.MapGet("/streams", async (AppDbContext db) =>
    (await db.Streams.OrderByDescending(s => s.StartedAt).Take(30).ToListAsync()).Select(StreamService.ToDto));
api.MapPost("/streams/start", async (StartStreamRequest req, StreamService s) => StreamService.ToDto(await s.StartAsync(req)));
api.MapPost("/streams/stop", async (StreamService s) => await s.StopAsync() is { } st ? Results.Ok(StreamService.ToDto(st)) : Results.NoContent());
api.MapGet("/streams/dashboard", async (int? streamId, StreamService s) => await s.DashboardAsync(streamId));
api.MapGet("/streams/label-info", async (string name, string? reference, StreamService s) => await s.LabelInfoAsync(name, reference));
api.MapPost("/orders/label-printed", async (MarkLabelPrintedRequest req, StreamService s) =>
{
    await s.MarkLabelPrintedAsync(req.OrderIds);
    return Results.NoContent();
});

// ---------- Rip-wachtrij: labels + Shopify-orders ----------

api.MapPost("/labels/jobs", async (CreateLabelJobRequest req, AppDbContext db) =>
{
    var (job, duplicate) = await LabelQueue.CreateAsync(db, req);
    return Results.Ok(new { id = job.Id, duplicate });
});
api.MapGet("/labels/jobs/pending", async (AppDbContext db) => await LabelQueue.PendingAsync(db));
api.MapPost("/labels/jobs/{id:int}/claim", async (int id, string? by, AppDbContext db) =>
    await LabelQueue.ClaimAsync(db, id, string.IsNullOrWhiteSpace(by) ? "onbekend" : by) ? Results.NoContent() : Results.Conflict());
api.MapPost("/labels/jobs/{id:int}/done", async (int id, CompleteLabelJobRequest req, AppDbContext db) =>
    await LabelQueue.CompleteAsync(db, id, req.Error) ? Results.NoContent() : Results.NotFound());

// since = unix-tijd (seconden), filter = live (standaard: enkel "Live openen"-orders) of all.
api.MapGet("/queue/shopify-orders", async (long since, string? filter, AppDbContext db) =>
    await LabelQueue.ShopifyOrdersAsync(db, DateTimeOffset.FromUnixTimeSeconds(since).LocalDateTime, onlyLive: filter != "all"));

// ---------- Opvolging ----------

api.MapGet("/follow-up", async (int? days, AppDbContext db) => await FollowUp.ListAsync(db, days ?? 30, default));
api.MapPut("/follow-up/{id:int}", async (int id, UpdateFollowUpRequest req, AppDbContext db) =>
    await FollowUp.UpdateAsync(db, id, req, default) is { } dto ? Results.Ok(dto) : Results.NotFound());

// ---------- Klachten ----------

api.MapGet("/complaints", async (ComplaintService c) => await c.ListAsync());
api.MapGet("/complaints/{id:int}", async (int id, ComplaintService c) => await c.GetAsync(id) is { } d ? Results.Ok(d) : Results.NotFound());
api.MapPost("/complaints/{id:int}/reply", async (int id, ReplyRequest req, ComplaintService c) =>
    await c.ReplyAsync(id, req) is { } d ? Results.Ok(d) : Results.NotFound());
api.MapPut("/complaints/{id:int}", async (int id, UpdateComplaintRequest req, ComplaintService c) =>
    await c.UpdateAsync(id, req) ? Results.NoContent() : Results.NotFound());
api.MapPost("/complaints/check-mail", async (ComplaintService c) => new { imported = await c.ImportAsync() });

// ---------- Winkelactiviteit (Shopify Customer Events-pixel) ----------
// Publiek endpoint (de pixel draait in de browser van de klant), beveiligd met Stream:PixelKey.
// Body als text/plain zodat de browser geen CORS-preflight doet.

app.MapMethods("/pixel/events", ["OPTIONS"], (HttpContext ctx) =>
{
    ctx.Response.Headers.AccessControlAllowOrigin = "*";
    ctx.Response.Headers.AccessControlAllowMethods = "POST";
    ctx.Response.Headers.AccessControlAllowHeaders = "Content-Type";
    return Results.NoContent();
});
app.MapPost("/pixel/events", async (HttpContext ctx, string key, IOptions<StreamOptions> o, ShopActivityService activity, AppDbContext db) =>
{
    ctx.Response.Headers.AccessControlAllowOrigin = "*";
    if (key != o.Value.PixelKey) return Results.Unauthorized();
    using var reader = new StreamReader(ctx.Request.Body);
    var json = await reader.ReadToEndAsync();
    var opts = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
    var events = json.TrimStart().StartsWith('[')
        ? System.Text.Json.JsonSerializer.Deserialize<List<PixelEvent>>(json, opts) ?? []
        : [System.Text.Json.JsonSerializer.Deserialize<PixelEvent>(json, opts)!];
    foreach (var e in events.Take(50)) await activity.ApplyAsync(e);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).DisableAntiforgery();

// Pixelcode voor Shopify met adres en sleutel ingevuld (voor de knop "Pixelcode kopiëren" in de app).
api.MapGet("/pixel/code", (HttpContext ctx, IOptions<StreamOptions> o) =>
{
    using var stream = typeof(Program).Assembly.GetManifestResourceStream("customer-events-pixel.js")!;
    var template = new StreamReader(stream).ReadToEnd();
    var server = (string.IsNullOrWhiteSpace(o.Value.PublicUrl) ? $"{ctx.Request.Scheme}://{ctx.Request.Host}" : o.Value.PublicUrl).TrimEnd('/');

    var warnings = new List<string>();
    var host = new Uri(server).Host;
    if (host is "localhost" or "127.0.0.1" || System.Net.IPAddress.TryParse(host, out _) || !server.StartsWith("https://"))
        warnings.Add($"Het adres {server} is niet publiek bereikbaar via https. De pixel werkt pas als de server online staat (bv. Azure) en Stream:PublicUrl ingevuld is.");
    if (o.Value.PixelKey is "verander-mij-pixel" or { Length: < 16 })
        warnings.Add("Stream:PixelKey is nog de voorbeeldwaarde of te kort. Kies een lange willekeurige sleutel en kopieer daarna de code opnieuw.");

    var code = System.Text.RegularExpressions.Regex.Replace(template, @"const SERVER = '[^']*';", _ => $"const SERVER = '{server}';");
    code = System.Text.RegularExpressions.Regex.Replace(code, @"const PIXEL_KEY = '[^']*';", _ => $"const PIXEL_KEY = '{o.Value.PixelKey}';");
    return Results.Ok(new { code, server, warnings });
});

// ---------- Demo rip-tool ----------
// Nabootsing van de lokale rip-pagina, om de iframe + "done rippen" → label te testen.
app.MapGet("/demo/rip", () => Results.Content(DemoRipPage.Html, "text/html; charset=utf-8"));

static (DateTime From, DateTime To) Period(int? year, int? month, DateTime? from, DateTime? to) =>
    from is { } f && to is { } t ? (f, t)
    : year is { } y && month is { } m ? ReportService.Range(y, m)
    : throw new ValidationException("Geef year+month of from+to mee.");

app.Run();

public partial class Program;
