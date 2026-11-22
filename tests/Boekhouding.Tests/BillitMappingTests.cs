using System.Text.Json;
using Boekhouding.Api.Integrations;
using Boekhouding.Shared;

namespace Boekhouding.Tests;

/// <summary>Vertaling van Billit-documenten (GET /v1/orders/{id}) naar aankoopfacturen.</summary>
public class BillitMappingTests
{
    private static ExternalInvoice Map(string type, string country, decimal excl, decimal vat, string? number = "F-2026-118") =>
        BillitClient.Map(JsonDocument.Parse($$"""
            {
              "OrderID": 4711,
              "OrderType": "{{type}}",
              "OrderDirection": "Cost",
              {{(number is null ? "" : $"\"OrderNumber\": \"{number}\",")}}
              "OrderTitle": "Voorraad november",
              "OrderDate": "2026-11-03T00:00:00",
              "TotalExcl": {{excl.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
              "TotalVAT": {{vat.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
              "OrderPDF": { "FileID": "abc-123", "FileName": "factuur.pdf" },
              "Supplier": { "Name": "Kartomania Sp. z o.o.", "VATNumber": "PL5272981234", "CountryCode": "{{country}}" }
            }
            """).RootElement, "billit-4711");

    [Fact]
    public void Belgian_invoice_with_vat_is_deductible()
    {
        var i = Map("Invoice", "BE", 100m, 21m);
        Assert.Equal(PurchaseVatType.Belgian21, i.VatType);
        Assert.Equal((100m, 21m), (i.NetAmount, i.VatAmount));
        Assert.Equal("F-2026-118", i.InvoiceNumber);
        Assert.Equal(new DateTime(2026, 11, 3), i.InvoiceDate);
        Assert.Equal("abc-123", i.PdfFileId);
        Assert.Equal("billit-4711", i.ExternalId);
    }

    [Theory]
    [InlineData("PL")]
    [InlineData("NL")]
    [InlineData("AT")]
    public void Zero_vat_from_other_eu_country_is_intra_community(string country) =>
        Assert.Equal(PurchaseVatType.IntraCommunity0, Map("Invoice", country, 500m, 0m).VatType);

    [Fact]
    public void Zero_vat_from_belgium_or_outside_eu_is_exempt()
    {
        Assert.Equal(PurchaseVatType.Exempt0, Map("Invoice", "BE", 50m, 0m).VatType);
        Assert.Equal(PurchaseVatType.Exempt0, Map("Invoice", "US", 50m, 0m).VatType);
    }

    [Fact]
    public void Credit_note_is_negative()
    {
        var i = Map("CreditNote", "BE", 40m, 8.4m);
        Assert.Equal((-40m, -8.4m), (i.NetAmount, i.VatAmount));
        Assert.StartsWith("Creditnota", i.Description);
    }

    [Fact]
    public void Empty_order_number_falls_back_to_title() =>
        Assert.Equal("Voorraad november", Map("Invoice", "BE", 10m, 2.1m, number: "").InvoiceNumber);

    [Fact]
    public void Missing_order_number_falls_back_to_title()
    {
        Assert.Equal("Voorraad november", Map("Invoice", "BE", 10m, 2.1m, number: null).InvoiceNumber);
    }
}
