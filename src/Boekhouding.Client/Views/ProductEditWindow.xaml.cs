using System.Windows;
using System.Windows.Controls;
using Boekhouding.Client.Services;
using Boekhouding.Shared;

namespace Boekhouding.Client.Views;

/// <summary>Bestaand product bewerken, of (zonder product) een vrij product aanmaken.</summary>
public partial class ProductEditWindow : Window
{
    private readonly ProductDto? _product;

    public ProductEditWindow(ProductDto? product = null)
    {
        _product = product;
        InitializeComponent();
        if (product is null)
        {
            Title = "Nieuw product";
            Header.Text = "Nieuw product (vrij, zonder aankoop)";
            SkuPanel.Visibility = Visibility.Visible;
            PriceBox.Text = "";
            StatusBox.SelectedIndex = 1; // concept
            ShopifyBox.Content = "Meteen in Shopify aanmaken";
            ShopifyBox.Visibility = Visibility.Visible;
            SaveButton.Content = "Aanmaken";
            return;
        }

        Header.Text = $"{product.Sku} – voorraad {product.StockTotal} ({product.StockMargin} marge, {product.StockInvoice} factuur)";
        TitleBox.Text = product.Title;
        DescBox.Text = product.Description;
        VendorBox.Text = product.Vendor;
        TypeBox.Text = product.ProductType;
        TagsBox.Text = product.Tags;
        BarcodeBox.Text = product.Barcode;
        PriceBox.Text = product.Price.ToString("0.00", Ui.Nl);
        CompareBox.Text = product.CompareAtPrice?.ToString("0.00", Ui.Nl);
        WeightBox.Text = product.WeightGrams?.ToString();
        StatusBox.SelectedItem = StatusBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Content == product.Status)
                                 ?? StatusBox.Items[1];
        TrackStockBox.IsChecked = product.TrackStock;
        DigitalBox.IsChecked = !product.RequiresShipping;
        ShopifyBox.Visibility = product.ShopifyProductId is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Digitaal: geen voorraad en geen gewicht.</summary>
    private void Digital_Changed(object sender, RoutedEventArgs e)
    {
        if (TrackStockBox is null || WeightBox is null) return;
        var digital = DigitalBox.IsChecked == true;
        if (digital) TrackStockBox.IsChecked = false;
        TrackStockBox.IsEnabled = !digital;
        WeightBox.IsEnabled = !digital;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text)) { Ui.Info("Vul een titel in."); return; }
        if (!Ui.TryParseDecimal(PriceBox.Text, out var price)) { Ui.Info("Ongeldige prijs."); return; }
        decimal? compare = null;
        if (!string.IsNullOrWhiteSpace(CompareBox.Text))
        {
            if (!Ui.TryParseDecimal(CompareBox.Text, out var c)) { Ui.Info("Ongeldige vergelijkingsprijs."); return; }
            compare = c;
        }
        int? weight = int.TryParse(WeightBox.Text, out var w) ? w : null;
        var status = (string)((ComboBoxItem)StatusBox.SelectedItem).Content;
        var digital = DigitalBox.IsChecked == true;
        var trackStock = !digital && TrackStockBox.IsChecked == true;

        if (_product is null)
        {
            var create = new CreateProductRequest
            {
                Sku = SkuBox.Text, Title = TitleBox.Text.Trim(), Description = DescBox.Text, Vendor = VendorBox.Text,
                ProductType = TypeBox.Text, Tags = TagsBox.Text, Barcode = BarcodeBox.Text, WeightGrams = digital ? null : weight,
                Price = price, CompareAtPrice = compare, Status = status, TrackStock = trackStock, RequiresShipping = !digital,
                PushToShopify = ShopifyBox.IsChecked == true,
            };
            await Ui.RunAsync(SaveButton, async () =>
            {
                var created = await ApiClient.Current.CreateProductAsync(create);
                Ui.Info($"Product {created.Sku} aangemaakt." + (created.ShopifyMessage is { } m ? $"\n\nShopify: {m}" : ""));
                DialogResult = true;
            });
            return;
        }

        var req = new UpdateProductRequest(TitleBox.Text.Trim(), DescBox.Text, VendorBox.Text, TypeBox.Text, TagsBox.Text,
            BarcodeBox.Text, digital ? null : weight, price, compare, status, trackStock, RequiresShipping: !digital);
        await Ui.RunAsync(SaveButton, async () =>
        {
            await ApiClient.Current.UpdateProductAsync(_product.Id, req, pushToShopify: _product.ShopifyProductId is not null && ShopifyBox.IsChecked == true);
            DialogResult = true;
        });
    }
}
