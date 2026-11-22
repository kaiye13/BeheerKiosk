using System.Globalization;
using System.Net;
using System.Text;
using Boekhouding.Api.Data;

namespace Boekhouding.Api.Services;

public class CompanyOptions
{
    public const string Section = "Company";
    public string Name { get; set; } = "Ship & Ripper";
    public string Address { get; set; } = "Straat 1, 1000 Brussel";
    public string VatNumber { get; set; } = "BE0000.000.000";
    public string? Email { get; set; }
}

/// <summary>Printbare verkoopovereenkomst (aankoopborderel) voor margegoederen, als HTML → afdrukken of opslaan als PDF.</summary>
public static class AgreementDocument
{
    private static readonly CultureInfo Nl = CultureInfo.GetCultureInfo("nl-BE");

    public static string Render(PurchaseAgreement a, CompanyOptions company)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var total = a.Lots.Sum(l => l.QuantityIn * l.UnitCost);
        var rows = new StringBuilder();
        foreach (var l in a.Lots)
            rows.Append($"<tr><td>{E(l.Product.Title)}</td><td>{E(l.Product.Sku)}</td><td class=n>{l.QuantityIn}</td>" +
                        $"<td class=n>{l.UnitCost.ToString("C", Nl)}</td><td class=n>{(l.UnitCost * l.QuantityIn).ToString("C", Nl)}</td></tr>");

        var addressMissing = string.IsNullOrWhiteSpace(a.SellerAddress);
        var warning = addressMissing
            ? "<div class=warn>Adres van de verkoper ontbreekt. Vul dit aan in de applicatie vóór ondertekening.</div>"
            : "";

        return $$"""
        <!doctype html>
        <html lang="nl"><head><meta charset="utf-8"><title>{{E(a.Number)}} – Verkoopovereenkomst</title>
        <style>
          body { font-family: Segoe UI, Arial, sans-serif; color:#1b1f24; max-width: 800px; margin: 32px auto; padding: 0 24px; }
          h1 { font-size: 22px; margin-bottom: 4px; } .sub { color:#555; margin-top:0; }
          .parties { display:flex; gap:24px; margin:24px 0; } .parties div { flex:1; border:1px solid #ccd; border-radius:6px; padding:12px 16px; }
          .parties h3 { margin:0 0 6px; font-size:13px; text-transform:uppercase; color:#556; letter-spacing:.04em; }
          table { width:100%; border-collapse:collapse; margin:16px 0; } th,td { border-bottom:1px solid #dde; padding:8px 6px; text-align:left; }
          th { background:#f1f4f9; font-size:13px; } .n { text-align:right; } tfoot td { font-weight:600; border-top:2px solid #889; }
          .clause { font-size:13px; line-height:1.5; } .sign { display:flex; gap:48px; margin-top:48px; } .sign div { flex:1; border-top:1px solid #333; padding-top:6px; font-size:13px; }
          .warn { background:#fdecea; color:#8a1c12; border:1px solid #f5c2bd; padding:10px 14px; border-radius:6px; margin:16px 0; font-weight:600; }
          @media print { .noprint { display:none; } body { margin:0; } }
        </style></head><body>
        <button class="noprint" onclick="window.print()" style="float:right;padding:8px 14px">Afdrukken / PDF</button>
        <h1>Verkoopovereenkomst tweedehandsgoederen</h1>
        <p class="sub">Aankoopborderel nr. <b>{{E(a.Number)}}</b> – datum {{a.Date.ToString("d MMMM yyyy", Nl)}}</p>
        {{warning}}
        <div class="parties">
          <div><h3>Verkoper (particulier)</h3><b>{{E(a.SellerName)}}</b><br>{{(addressMissing ? "<i>adres ontbreekt</i>" : E(a.SellerAddress).Replace("\n", "<br>"))}}<br>{{E(a.SellerEmail)}}</div>
          <div><h3>Koper</h3><b>{{E(company.Name)}}</b><br>{{E(company.Address)}}<br>Btw: {{E(company.VatNumber)}}</div>
        </div>
        <table>
          <thead><tr><th>Omschrijving</th><th>Ref.</th><th class=n>Aantal</th><th class=n>Prijs/st</th><th class=n>Totaal</th></tr></thead>
          <tbody>{{rows}}</tbody>
          <tfoot><tr><td colspan=4>Totaal betaald</td><td class=n>{{total.ToString("C", Nl)}}</td></tr></tfoot>
        </table>
        <p class="clause">De verkoper verklaart als particulier te handelen, eigenaar te zijn van bovenstaande goederen en deze vrij van
        lasten over te dragen aan de koper voor de vermelde prijs. Op deze verkoop is geen btw verschuldigd. De koper neemt de goederen op
        onder de bijzondere regeling voor de winstmarge (tweedehandsgoederen).</p>
        {{(string.IsNullOrWhiteSpace(a.Notes) ? "" : $"<p class=clause><b>Opmerkingen:</b> {E(a.Notes)}</p>")}}
        <div class="sign"><div>Handtekening verkoper</div><div>Handtekening koper</div></div>
        </body></html>
        """;
    }
}
