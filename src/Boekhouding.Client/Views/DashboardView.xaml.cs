using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Boekhouding.Client.Services;

namespace Boekhouding.Client.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
        Month.Changed += async (_, _) => await LoadAsync();
    }

    private Task LoadAsync() => Ui.RunAsync(null, async () =>
    {
        var d = await ApiClient.Current.GetDashboardAsync(Month.Year, Month.Month);
        DummyBanner.Visibility = d.UsesDummyData ? Visibility.Visible : Visibility.Collapsed;
        LastSyncText.Text = d.LastSync is { } t ? $"Laatste synchronisatie: {t:dd/MM/yyyy HH:mm}" : "Nog niet gesynchroniseerd";

        var warnings = new List<string>();
        if (d.StockWarnings > 0)
            warnings.Add($"⚠ {d.StockWarnings} orderlijnen verkocht zonder gekende voorraad (worden belast aan 21% op de volledige prijs). Voer de aankoop in via 'Nieuwe invoer'.");
        if (d.AgreementsMissingAddress > 0)
            warnings.Add($"⚠ {d.AgreementsMissingAddress} verkoopovereenkomst(en) zonder adres van de verkoper.");
        Warnings.ItemsSource = warnings;

        Kpis.Children.Clear();
        AddKpi("Omzet deze maand", Ui.Money(d.RevenueTotal), $"{d.OrderCount} orders");
        AddKpi("Shopify", Ui.Money(d.RevenueShopify), "webshop");
        AddKpi("TikTok Shop", Ui.Money(d.RevenueTikTok), "via Shopify");
        AddKpi(d.VatBalance >= 0 ? "Btw te betalen" : "Btw terug te krijgen", Ui.Money(Math.Abs(d.VatBalance)), "saldo deze maand",
            d.VatBalance >= 0 ? "#B91C1C" : "#059669");
        AddKpi("Winst margegoederen", Ui.Money(d.MarginProfit), "verkoop − aankoop", "#059669");
        AddKpi("Voorraad marge", Ui.Money(d.StockValueMargin), "aankoopwaarde");
        AddKpi("Voorraad factuur", Ui.Money(d.StockValueInvoice), "aankoopwaarde excl. btw");
        AddKpi("Oude webshop", Ui.Money(d.RevenueWoo), "WooCommerce deze maand");

        OrdersGrid.ItemsSource = d.RecentOrders;
        InvoicesGrid.ItemsSource = d.RecentInvoices;
    });

    private void AddKpi(string title, string value, string sub, string? color = null)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Foreground = (Brush)FindResource("Muted"), FontSize = 12 });
        panel.Children.Add(new TextBlock
        {
            Text = value, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 2),
            Foreground = color is null ? (Brush)FindResource("Text") : (Brush)new BrushConverter().ConvertFrom(color)!,
        });
        panel.Children.Add(new TextBlock { Text = sub, Foreground = (Brush)FindResource("Muted"), FontSize = 11 });
        Kpis.Children.Add(new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(6), Child = panel });
    }

    private async void Sync_Click(object sender, RoutedEventArgs e) => await Ui.RunAsync(SyncButton, async () =>
    {
        var r = await ApiClient.Current.SyncAsync();
        Ui.Info(string.Join("\n", r.Messages));
        await LoadAsync();
    });

    private void Upload_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.Navigate("newpurchase", "invoice");

    private async void Order_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        await Ui.ShowOrderAsync(((System.Windows.Controls.DataGrid)sender).SelectedItem, System.Windows.Window.GetWindow(this));
}
