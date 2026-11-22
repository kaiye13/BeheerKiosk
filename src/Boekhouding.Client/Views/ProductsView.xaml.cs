using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Boekhouding.Client.Services;
using Boekhouding.Shared;

namespace Boekhouding.Client.Views;

public partial class ProductsView : UserControl
{
    private List<ProductDto> _products = [];

    public ProductsView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private Task LoadAsync() => Ui.RunAsync(null, async () =>
    {
        _products = await ApiClient.Current.GetProductsAsync();
        ApplyFilter();
    });

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var q = Search.Text.Trim();
        Grid.ItemsSource = q.Length == 0
            ? _products
            : _products.Where(p => new[] { p.Sku, p.Title, p.Vendor, p.ProductType, p.Tags }
                .Any(f => f?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)).ToList();
    }

    private async void Grid_DoubleClick(object sender, MouseButtonEventArgs e) => await EditSelectedAsync();

    private async void Edit_Click(object sender, RoutedEventArgs e) => await EditSelectedAsync();

    private async Task EditSelectedAsync()
    {
        if (Grid.SelectedItem is not ProductDto p) { Ui.Info("Selecteer eerst een product."); return; }
        if (new ProductEditWindow(p) { Owner = Window.GetWindow(this) }.ShowDialog() == true)
            await LoadAsync();
    }

    private async void Deactivate_Click(object sender, RoutedEventArgs e) => await SetStatusAsync("draft", DeactivateButton,
        p => $"'{p.Title}' op inactief zetten? Het verdwijnt uit de winkel" + (p.ShopifyProductId is null ? "." : " (in Shopify: concept)."));

    private async void Activate_Click(object sender, RoutedEventArgs e) => await SetStatusAsync("active", ActivateButton,
        p => $"'{p.Title}' actief zetten? Het wordt zichtbaar in de winkel aan {Ui.Money(p.Price)}.");

    private async Task SetStatusAsync(string status, UIElement button, Func<ProductDto, string> question)
    {
        if (Grid.SelectedItem is not ProductDto p) { Ui.Info("Selecteer eerst een product."); return; }
        if (p.Status == status) { Ui.Info($"'{p.Title}' staat al op {(status == "active" ? "actief" : "inactief")}."); return; }
        if (!Ui.Confirm(question(p))) return;
        await Ui.RunAsync(button, async () =>
        {
            var updated = await ApiClient.Current.SetProductStatusAsync(p.Id, status);
            await LoadAsync();
            if (updated.ShopifyMessage is { } m) Ui.Info(m);
        });
    }

    private void New_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.Navigate("newpurchase");

    private async void NewFree_Click(object sender, RoutedEventArgs e)
    {
        if (new ProductEditWindow { Owner = Window.GetWindow(this) }.ShowDialog() == true)
            await LoadAsync();
    }

    private async void Import_Click(object sender, RoutedEventArgs e) => await Ui.RunAsync(ImportButton, async () =>
    {
        var r = await ApiClient.Current.ImportShopifyProductsAsync();
        await LoadAsync();
        Ui.Info(r.Changed == 0 ? "Alles is al up-to-date met Shopify." : $"{r.Changed} producten nieuw of bijgewerkt vanuit Shopify.");
    });

    private async void Push_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not ProductDto p) { Ui.Info("Selecteer eerst een product."); return; }
        var question = p.ShopifyProductId is null
            ? $"'{p.Title}' als nieuw product in Shopify aanmaken (status: {p.Status})?"
            : $"'{p.Title}' in Shopify bijwerken met de gegevens uit de boekhouding (titel, prijs, merk, tags, status)?";
        if (!Ui.Confirm(question)) return;
        await Ui.RunAsync(PushButton, async () =>
        {
            var updated = await ApiClient.Current.PushProductToShopifyAsync(p.Id);
            await LoadAsync();
            Ui.Info(updated.ShopifyMessage ?? "Shopify is bijgewerkt.");
        });
    }
}
