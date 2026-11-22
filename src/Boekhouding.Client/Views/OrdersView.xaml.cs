using System.Windows.Controls;
using Boekhouding.Client.Services;
using Boekhouding.Shared;

namespace Boekhouding.Client.Views;

public partial class OrdersView : UserControl
{
    private List<SalesOrderDto> _orders = [];

    public OrdersView()
    {
        InitializeComponent();
        ChannelFilter.Items.Add(new ComboBoxItem { Content = "Alle kanalen" });
        foreach (var c in Enum.GetValues<SalesChannel>()) ChannelFilter.Items.Add(new ComboBoxItem { Content = c.Label(), Tag = c });
        ChannelFilter.SelectedIndex = 0;
        Loaded += async (_, _) => await LoadAsync();
        Period.Changed += async (_, _) => await LoadAsync();
    }

    private Task LoadAsync() => Ui.RunAsync(null, async () =>
    {
        _orders = await ApiClient.Current.GetOrdersAsync(Period.From, Period.To);
        ApplyFilter();
    });

    private void Filter_Changed(object sender, System.Windows.RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (Grid is null || Search is null || MinAmount is null || MaxAmount is null) return;
        IEnumerable<SalesOrderDto> q = _orders;
        if ((ChannelFilter.SelectedItem as ComboBoxItem)?.Tag is SalesChannel c) q = q.Where(o => o.Channel == c);
        var term = Search.Text.Trim();
        if (term.Length > 0)
            q = q.Where(o => o.CustomerName.Contains(term, StringComparison.OrdinalIgnoreCase)
                             || o.OrderNumber.Contains(term, StringComparison.OrdinalIgnoreCase)
                             || o.Items.Contains(term, StringComparison.OrdinalIgnoreCase));
        if (MinAmount.Text.Trim().Length > 0 && Ui.TryParseDecimal(MinAmount.Text, out var min)) q = q.Where(o => o.TotalIncl >= min);
        if (MaxAmount.Text.Trim().Length > 0 && Ui.TryParseDecimal(MaxAmount.Text, out var max)) q = q.Where(o => o.TotalIncl <= max);
        var list = q.ToList();
        Grid.ItemsSource = list;
        Summary.Text = $"{Period.Label} · {list.Count} orders · omzet {Ui.Money(list.Sum(o => o.TotalIncl))} · btw {Ui.Money(list.Sum(o => o.VatDue))}"
                       + (list.Count(o => o.HasStockWarning) is > 0 and var w ? $" · {w} met voorraadwaarschuwing" : "");
    }

    private async void Order_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        await Ui.ShowOrderAsync(((System.Windows.Controls.DataGrid)sender).SelectedItem, System.Windows.Window.GetWindow(this));
}
