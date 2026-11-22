# Ship & Ripper – Boekhouding

Boekhoudapplicatie voor Shopify + TikTok Shop (via Shopify), de oude WooCommerce-shop en Billit.
Momenteel draait alles op **dummy-data**: er zijn nog geen API-sleutels nodig.

## Opbouw

```
[Server-pc, altijd aan]   ShipRipperServer.exe   (Windows-service, poort 5080)
                          ├── SQLite-database + geüploade facturen in %ProgramData%\ShipRipperBoekhouding
                          └── haalt elke 10 min orders (Shopify/TikTok) en Billit-facturen op
[Elke pc]                 ShipRipperBoekhouding.exe   → praat met de server over het lokale netwerk
```

| Map | Inhoud |
|---|---|
| `src/Boekhouding.Shared` | DTO's, enums en btw-rekenregels (`Vat.cs`) |
| `src/Boekhouding.Api` | ASP.NET Core API, database, sync, rapporten, Excel-export |
| `src/Boekhouding.Client` | WPF-app (de .exe voor de gebruikers) |
| `tests/Boekhouding.Tests` | Tests voor btw, voorraadtoewijzing en maandrapport |

## Bouwen

Nodig: .NET 10 SDK (enkel op de pc waar je bouwt; de exe's hebben niets nodig).

```powershell
.\publish.ps1
```

Resultaat: `dist\Server\` en `dist\Client\ShipRipperBoekhouding.exe`.

> Staat de projectmap diep genest (pad > 260 tekens), dan faalt de build. Zet de map dan dichter bij de schijfroot, bv. `C:\dev\ShipRipperBoekhouding`.

## Installeren

**Server-pc** (één keer):
1. Kopieer de map `dist\Server` naar bv. `C:\ShipRipper\Server`.
2. Pas `appsettings.json` aan: **`Security:ApiKey`** (verzin een lange sleutel) en de bedrijfsgegevens onder `Company` (komen op de verkoopovereenkomst).
3. PowerShell als administrator → `.\install-server.ps1`. Het script toont het adres voor de andere pc's.
4. Stel energiebeheer in op "nooit slapen" en **maak dagelijks een back-up** van `%ProgramData%\ShipRipperBoekhouding`.

**Andere pc's:** kopieer `ShipRipperBoekhouding.exe`, start hem, en vul onder *Instellingen* het serveradres (bv. `http://192.168.1.50:5080`) en de API-sleutel in.
Windows SmartScreen waarschuwt de eerste keer ("Onbekende uitgever") omdat de exe niet digitaal ondertekend is → *Meer info → Toch uitvoeren*.

## Btw-logica

| Situatie | Btw |
|---|---|
| Verkoop margegoed | (verkoop − aankoop) × 21/121, per stuk. Verlies = 0 en niet verrekenbaar |
| Verkoop factuurgoed | verkoopprijs × 21/121 |
| Verkoop zonder gekende voorraad | als factuurgoed (21% op alles) + waarschuwing |
| Verzendkosten klant | × 21/121 |
| Aankoop BE 21% | btw aftrekbaar |
| Aankoop intracommunautair 0% (bv. Polen) | 21% verlegd: verschuldigd én aftrekbaar (netto 0) |
| Aankoop margegoed van particulier | geen btw |

**Toewijzing:** een verkoop neemt eerst voorraad uit margegoederen (oudste eerst), daarna uit factuurgoederen.
Wordt er verkocht vóór de aankoop is ingevoerd, dan wordt de verkoop bij invoer van die voorraad alsnog gekoppeld (enkel orders vanaf de maand van aankoop, zodat afgesloten maanden niet wijzigen).

⚠ Dit is een hulpmiddel. Laat de logica en de eerste exports nakijken door de boekhouder, o.a. voor margegoederen met identieke producten en de roosters van de btw-aangifte.

## Stream-functies

| Pagina | Wat |
|---|---|
| **Live stream** | Doel als ringdiagram (Shopify + TikTok), wie wat bestelt, verzending betaald / spaarder / gratis, klanten met open spaarorders, best verkochte producten, wie nu in de shop zit, verlaten checkouts, rip-tool met labelprint. Ververst elke 10 s; de server synchroniseert elke 30 s zolang er een stream loopt. |
| **Orderopvolging** | Per order: waar besteld, status (nieuw → geript → ingepakt → verzonden → geleverd, spaarorder, probleem), vervoerder + tracking (link naar bpost/PostNL/DPD/GLS/DHL), notities, open klachten. |
| **Klachten** | Mails uit de eigen klachtenmailbox, automatisch gekoppeld aan de order (ordernummer in de mail, anders laatste order van dat e-mailadres). Antwoorden vanuit de app; antwoorden van de klant komen in hetzelfde gesprek. |

**Spaarders vs. verzenders** wordt bepaald uit de verzendmethode in Shopify: bevat de naam een woord uit `Stream:SaverShippingKeyword` (standaard `spaar,sparen`) → spaarder. Anders betaald (verzendkost > 0) of gratis.

### Rip-tool + Brother-labelprinter
1. *Instellingen → Rip-tool*: adres van de localhost-pagina, de tekst op de knop ("done rippen") en de CSS-selectors van het blok per klant en van de naam (vind je met F12 → Inspect).
2. *Instellingen → Labelprinter*: kies de Brother-printer en het labelformaat (standaard 62×29 mm, DK-11209). Met *Voorbeeld* zie je het label, met *Testlabel printen* print je er één.
3. Klik je in de rip-tool op "done rippen", dan leest de app de naam, zoekt de orders van die klant in de stream en print een label met naam, ordernummers en **VERZENDING BETAALD / SPAARDER**. De orders krijgen status "Geript / label".
4. Er komt ook een knop **🖨 Label** rechtsonder in de rip-tool (selecteer een naam en klik), en onderaan kun je een naam typen.

De rip-tool zelf hoeft niet aangepast te worden: de app voegt het luister-script en de knop toe via de ingebedde browser (Microsoft Edge WebView2, standaard aanwezig op Windows 10/11).
Eigen opmaak? Maak een sjabloon in **P-touch Editor** met tekstobjecten `Naam` en `Info`, kies het .lbx-bestand bij Instellingen en installeer de **Brother b-PAC Client Component (64-bit)**.

### Klachtenmailbox
Vul in `appsettings.json` → `ComplaintsMailbox` het adres en de IMAP/SMTP-gegevens in (bv. Microsoft 365: `outlook.office365.com`:993 / `smtp.office365.com`:587; Gmail: met app-wachtwoord). Zolang `ImapHost` leeg is, komen er testklachten binnen.
De server leest de mails van de laatste 14 dagen en wijzigt niets in de mailbox (geen verwijderen of verplaatsen).

### "Nu in de shop" (Shopify)
- **Verlaten checkouts** komen uit de Shopify Admin API (scope `read_orders`).
- **Live bezoekers** (rondkijken → mandje → betalen, hoelang al in de shop, naam als ze aangemeld zijn) komen van een **Shopify Customer Events-pixel**: zie [shopify/customer-events-pixel.js](shopify/customer-events-pixel.js). De pixel stuurt events naar `/pixel/events` op de server, dus dat pad moet via https bereikbaar zijn (bv. Cloudflare Tunnel). Shopify past zelf de cookietoestemming van de klant toe; zonder toestemming worden er geen events verstuurd.

## Shopify (live)

Verbinding via een app in de **Shopify Dev Dashboard**: `ShopifyClientId` + `ShopifyClientSecret` in de instellingen van de server. De server vraagt zelf een token aan (24 u geldig, automatisch vernieuwd).
De app moet **geïnstalleerd** zijn op de winkel (`mptfwi-9e.myshopify.com`) en in dezelfde Shopify-organisatie zitten, met deze rechten:
`read_orders, read_all_orders, read_customers, read_products, write_products, read_fulfillments, read_inventory, write_inventory, read_locations`.
Test de verbinding (alleen lezen): `GET /api/integrations/shopify/check` met de API-sleutel van de server.

| Wat | Hoe |
|---|---|
| Orders webshop + TikTok Shop | Elke sync (10 min, 30 s tijdens een stream). TikTok herkend aan het verkoopkanaal. Verzendmethode "Live openen…" = spaarder, "Express" = verzending betaald. Tracking uit de fulfillments. Geannuleerd/terugbetaald → voorraad terug. |
| Producten ophalen | Automatisch om de 6 uur, of knop *Ophalen uit Shopify*. Elke variant = één product (SKU). |
| Product aanmaken / bijwerken | Knop *Naar Shopify* bij Producten, of het vinkje *Ook in Shopify aanpassen* bij bewerken. Nieuwe producten komen als **concept**. |
| Voorraad | Bij een aankoop met het vinkje *Shopify bijwerken*: nieuwe producten aangemaakt (concept) en beschikbare voorraad verhoogd met de ingekochte aantallen (`Integrations:ShopifyAdjustInventory`, locatie: `ShopifyLocationName` of de eerste actieve). Verkopen verlagen de voorraad in Shopify zoals altijd. |
| Verlaten checkouts | Op het stream-dashboard. |

**Ingebruikname:** orders van vóór de eerste echte sync (of `Integrations:GoLiveDate`) tellen mee in de omzet, maar nemen geen voorraad af. Orders worden de eerste keer opgehaald vanaf 1 januari van dit jaar (`ShopifyImportFrom`).

## Database

SQLite-bestand `boekhouding.db` in de datamap; geen aparte databaseserver nodig. Het schema wordt bij het starten automatisch bijgewerkt via EF-migraties (`src/Boekhouding.Api/Data/Migrations`).
Een database van een testversie (dummy-data, van vóór de migraties) wordt in productie geweigerd: verwijder dan `boekhouding.db` en start opnieuw.
Nieuwe migratie na een modelwijziging (ontwikkelaar): `dotnet tool run dotnet-ef migrations add <Naam> --project src/Boekhouding.Api --output-dir Data/Migrations`.

## Echte koppelingen activeren (later)

1. Vul de klassen in `src/Boekhouding.Api/Integrations/LiveClients.cs` in (Shopify GraphQL Admin API, Billit API, WooCommerce REST of CSV).
2. Zet in `appsettings.json` van de server de sleutels en `Integrations:UseDummyData` op `false`.
3. **Begin met een lege database**: stop de service en verwijder `boekhouding.db` (de dummy-data mag niet in de echte boekhouding komen).

## Nog niet gebouwd

- Terugbetalingen en retouren (orders met status refunded/cancelled worden nu genegeerd in het rapport, niet teruggedraaid in voorraad).
- Producten terugpushen naar Shopify.
- Databasemigraties: het schema wordt nu aangemaakt met `EnsureCreated`. In dummy-modus wordt de testdatabase bij een schemawijziging automatisch opnieuw opgebouwd; met echte data weigert de server te starten. Vóór echt gebruik overschakelen op EF-migraties.
- Puntensysteem (bewust overgeslagen).

## Ontwikkelen

```powershell
$env:Storage__DataDirectory = "C:\temp\boekhouding-dev"   # testdata apart houden
dotnet run --project src/Boekhouding.Api
dotnet run --project src/Boekhouding.Client
dotnet test
```
