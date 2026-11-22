using System.Text.Json;
using Boekhouding.Api.Data;
using Boekhouding.Api.Integrations;
using Boekhouding.Api.Services;
using Boekhouding.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Boekhouding.Tests;

public static class TestServices
{
    public static readonly AppPaths Paths = new(Path.GetTempPath(), Path.GetTempPath());

    /// <summary>Shopify-productservice in dummy-modus (doet niets naar buiten).</summary>
    public static ShopifyProductService ShopifyProducts(AppDbContext db) =>
        new(db, new ShopifyTokenProvider(new HttpClient(), Options.Create(new IntegrationOptions())),
            Options.Create(new IntegrationOptions()), NullLogger<ShopifyProductService>.Instance);
}

/// <summary>Vertaling van Shopify GraphQL-antwoorden naar orders in de boekhouding.</summary>
public class ShopifyMappingTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private const string TikTokOrder = """
        {
          "id": "gid://shopify/Order/6001",
          "name": "#1042",
          "createdAt": "2026-11-05T19:41:00Z",
          "cancelledAt": null,
          "displayFinancialStatus": "PAID",
          "sourceName": "4383523",
          "channelInformation": { "channelDefinition": { "channelName": "TikTok", "handle": "tiktok" }, "app": { "title": "TikTok" } },
          "email": "abc123@scs.tiktokw.eu",
          "customer": null,
          "shippingAddress": { "name": "Jens Peeters" },
          "shippingLine": { "title": "Live openen op TikTok (geen retour)" },
          "totalShippingPriceSet": { "shopMoney": { "amount": "0.0" } },
          "lineItems": { "nodes": [
            { "sku": "PKM-SV-BP", "name": "Scarlet & Violet Booster", "currentQuantity": 3, "variant": { "id": "gid://shopify/ProductVariant/77" },
              "discountedUnitPriceAfterAllDiscountsSet": { "shopMoney": { "amount": "5.99" } } },
            { "sku": "", "name": "Teruggestuurd item", "currentQuantity": 0, "variant": null,
              "discountedUnitPriceAfterAllDiscountsSet": { "shopMoney": { "amount": "9.99" } } }
          ] },
          "fulfillments": [
            { "createdAt": "2026-11-06T09:00:00Z", "deliveredAt": null, "displayStatus": "IN_TRANSIT",
              "trackingInfo": [ { "company": "bpost", "number": "323299", "url": "https://track.bpost.cloud/x" } ] }
          ]
        }
        """;

    [Fact]
    public void TikTok_order_is_mapped_with_shipping_name_and_tracking()
    {
        var o = ShopifyClient.MapOrder(Json(TikTokOrder));

        Assert.Equal(SalesChannel.TikTokShop, o.Channel);
        Assert.Equal("#1042", o.OrderNumber);
        Assert.Equal("Jens Peeters", o.CustomerName);            // geen klantobject → naam van verzendadres
        Assert.Equal("abc123@scs.tiktokw.eu", o.CustomerEmail);
        Assert.Equal("paid", o.FinancialStatus);
        Assert.Equal("Live openen op TikTok (geen retour)", o.ShippingMethod);
        var line = Assert.Single(o.Lines);                        // lijn met 0 stuks (teruggestuurd) valt weg
        Assert.Equal(("PKM-SV-BP", 3, 5.99m, 77L), (line.Sku, line.Quantity, line.UnitPriceIncl, line.VariantId!.Value));
        Assert.Equal("323299", o.Fulfillment!.TrackingNumber);
        Assert.Equal("In Transit", o.Fulfillment.Status);
        Assert.NotNull(o.Fulfillment.ShippedAt);
        Assert.Null(o.Fulfillment.DeliveredAt);
    }

    [Fact]
    public void Live_open_shipping_is_a_saver_and_express_is_paid()
    {
        const string keywords = "live openen,spaar,sparen";
        Assert.Equal(ShippingKind.Saver, SyncService.ClassifyShipping("Live openen op TikTok (geen retour)", 0, keywords));
        Assert.Equal(ShippingKind.Paid, SyncService.ClassifyShipping("Express", 9.99m, keywords));
    }

    [Fact]
    public void Webshop_order_without_tiktok_channel_is_shopify()
    {
        var json = TikTokOrder
            .Replace("\"channelName\": \"TikTok\", \"handle\": \"tiktok\"", "\"channelName\": \"Online Store\", \"handle\": \"online_store\"")
            .Replace("\"app\": { \"title\": \"TikTok\" }", "\"app\": { \"title\": \"Online Store\" }")
            .Replace("\"sourceName\": \"4383523\"", "\"sourceName\": \"web\"")
            .Replace("\"customer\": null", "\"customer\": { \"displayName\": \"Lotte Janssens\", \"email\": \"lotte@example.com\" }")
            .Replace("\"cancelledAt\": null", "\"cancelledAt\": \"2026-11-06T10:00:00Z\"");

        var o = ShopifyClient.MapOrder(Json(json));

        Assert.Equal(SalesChannel.Shopify, o.Channel);
        Assert.Equal("Lotte Janssens", o.CustomerName);
        Assert.Equal("cancelled", o.FinancialStatus);
    }
}
