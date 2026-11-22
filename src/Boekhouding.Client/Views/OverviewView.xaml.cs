using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Boekhouding.Client.Services;
using Boekhouding.Shared;

namespace Boekhouding.Client.Views;

public partial class OverviewView : UserControl
{
    private List<StockLotDto> _lots = [];

    public OverviewView()
    {
        InitializeComponent();
        Period.Changed += (_, _) => ApplyFilter();
        SupplierFilter.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(ApplyFilter);
        SupplierFilter.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ApplyFilter()));
        MinAmount.TextChanged += (_, _) => ApplyFilter();
        MaxAmount.TextChanged += (_, _) => ApplyFilter();
        Loaded += async (_, _) => await Ui.RunAsync(null, async () =>
        {
            _lots = await ApiClient.Current.GetStockLotsAsync();
            SupplierFilter.ItemsSource = _lots.Select(l => l.Source).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
            BuildTotals();
            ApplyFilter();
        });
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        SupplierFilter.Text = MinAmount.Text = MaxAmount.Text = "";
        Search.Text = "";
        ApplyFilter();
    }

    private void BuildTotals()
    {
        Totals.Children.Clear();
        var margin = _lots.Where(l => l.Regime == VatRegime.Margin).ToList();
        var invoice = _lots.Where(l => l.Regime == VatRegime.Invoice).ToList();
        Card("Margegoederen in voorraad", $"{margin.Sum(l => l.QuantityRemaining)} st.", Ui.Money(margin.Sum(l => l.QuantityRemaining * l.UnitCost)) + " aankoopwaarde");
        Card("Factuurgoederen in voorraad", $"{invoice.Sum(l => l.QuantityRemaining)} st.", Ui.Money(invoice.Sum(l => l.QuantityRemaining * l.UnitCost)) + " excl. btw");
        Card("Verkocht uit marge", $"{margin.Sum(l => l.QuantitySold)} st.", "sinds start");
        Card("Verkocht uit factuur", $"{invoice.Sum(l => l.QuantitySold)} st.", "sinds start");
    }

    private void Card(string title, string value, string sub)
    {
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = title, Foreground = (Brush)FindResource("Muted"), FontSize = 12 });
        p.Children.Add(new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 2) });
        p.Children.Add(new TextBlock { Text = sub, Foreground = (Brush)FindResource("Muted"), FontSize = 11 });
        Totals.Children.Add(new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(6), Child = p });
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (Grid is null || OnlyInStock is null || Period is null || SelectionTotals is null) return;
        IEnumerable<StockLotDto> q = _lots.Where(l => Period.Contains(l.PurchaseDate));
        var supplier = SupplierFilter.Text.Trim();
        if (supplier.Length > 0) q = q.Where(l => l.Source.Contains(supplier, StringComparison.OrdinalIgnoreCase));
        if (MinAmount.Text.Trim().Length > 0 && Ui.TryParseDecimal(MinAmount.Text, out var min)) q = q.Where(l => l.TotalCost >= min);
        if (MaxAmount.Text.Trim().Length > 0 && Ui.TryParseDecimal(MaxAmount.Text, out var max)) q = q.Where(l => l.TotalCost <= max);
        if (RegimeFilter.SelectedIndex == 1) q = q.Where(l => l.Regime == VatRegime.Margin);
        if (RegimeFilter.SelectedIndex == 2) q = q.Where(l => l.Regime == VatRegime.Invoice);
        if (OnlyInStock.IsChecked == true) q = q.Where(l => l.QuantityRemaining > 0);
        var s = Search.Text.Trim();
        if (s.Length > 0)
            q = q.Where(l => l.Sku.Contains(s, StringComparison.OrdinalIgnoreCase)
                             || l.ProductTitle.Contains(s, StringComparison.OrdinalIgnoreCase)
                             || l.Source.Contains(s, StringComparison.OrdinalIgnoreCase));
        var list = q.ToList();
        Grid.ItemsSource = list;
        SelectionTotals.Text = $"{list.Count} partijen · {Period.Label}   |   ingekocht {list.Sum(l => l.QuantityIn)} st. voor {Ui.Money(list.Sum(l => l.TotalCost))}" +
                               $"   ·   verkocht {list.Sum(l => l.QuantitySold)} st.   ·   resterend {list.Sum(l => l.QuantityRemaining)} st. " +
                               $"({Ui.Money(list.Sum(l => l.QuantityRemaining * l.UnitCost))} aankoopwaarde)";
    }
}
