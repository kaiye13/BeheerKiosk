using System.Text.Json;
using Boekhouding.Api.Integrations;
using Boekhouding.Shared;

namespace Boekhouding.Tests;

public class TikTokMappingTests
{
    private static JsonElement Order(string status, long? paid = 1794160000) => JsonDocument.Parse($$"""
    {
      "id": "577000111222333444", "status": "{{status}}", "create_time": 1794159900 {{(paid is null ? "" : $", \"paid_time\": {paid}")}},
      "recipient_address": { "name": "Jan Peeters" }, "buyer_email": "x@scs.tiktok.com",
      "delivery_option_name": "Standard shipping", "tracking_number": "DPD123", "shipping_provider": "DPD",
      "payment": { "original_shipping_fee": "4.95", "shipping_fee_seller_discount": "0", "shipping_fee": "0" },
      "line_items": [
        { "sku_id": "1", "seller_sku": "ETB-151", "product_name": "Elite Trainer Box", "sku_name": "Default", "original_price": "59.99", "sale_price": "54.99", "seller_discount": "0", "platform_discount": "5" },
        { "sku_id": "1", "seller_sku": "ETB-151", "product_name": "Elite Trainer Box", "sku_name": "Default", "original_price": "59.99", "sale_price": "54.99", "seller_discount": "0", "platform_discount": "5" },
        { "sku_id": "2", "seller_sku": "", "product_name": "Booster", "sku_name": "Engels", "original_price": "6.00", "sale_price": "5.00", "seller_discount": "1.00" }
      ]
    }
    """).RootElement;

    [Fact]
    public void Paid_order_groups_units_and_counts_platform_discount_as_revenue()
    {
        var o = TikTokShopClient.MapOrder(Order("AWAITING_SHIPMENT"), "TCG KIOSK (BE)")!;
        Assert.Equal(SalesChannel.TikTokShop, o.Channel);
        Assert.Equal("577000111222333444", o.ExternalId);
        Assert.Equal("paid", o.FinancialStatus);
        Assert.Equal("Jan Peeters", o.CustomerName);
        Assert.Equal(4.95m, o.ShippingIncl);
        Assert.Equal(2, o.Lines.Count);
        var etb = o.Lines.Single(l => l.Sku == "ETB-151");
        Assert.Equal(2, etb.Quantity);
        Assert.Equal(59.99m, etb.UnitPriceIncl);            // klant 54,99 + 5 korting van TikTok
        var booster = o.Lines.Single(l => l.Sku == "TT-2");  // geen eigen SKU → TT-<sku_id>
        Assert.Equal(5.00m, booster.UnitPriceIncl);          // korting van de verkoper gaat eraf
        Assert.Equal("Booster – Engels", booster.Title);
        Assert.Equal("DPD123", o.Fulfillment!.TrackingNumber);
    }

    [Fact]
    public void Unpaid_order_is_skipped_and_cancelled_is_marked()
    {
        Assert.Null(TikTokShopClient.MapOrder(Order("UNPAID", paid: null), "BE"));
        Assert.Equal("cancelled", TikTokShopClient.MapOrder(Order("CANCELLED"), "BE")!.FinancialStatus);
    }
}
