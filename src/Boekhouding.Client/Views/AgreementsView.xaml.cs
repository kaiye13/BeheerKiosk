using System.Windows;
using System.Windows.Controls;
using Boekhouding.Client.Services;
using Boekhouding.Shared;

namespace Boekhouding.Client.Views;

public partial class AgreementsView : UserControl
{
    private List<PurchaseAgreementDto> _all = [];
    private bool _ready;

    public AgreementsView()
    {
        InitializeComponent();
        AddressAutocomplete.Attach(AddressBox, () => _all.Select(a => a.SellerAddress ?? "").Where(a => a.Length > 0));

        Period.Changed += (_, _) => ApplyFilter();
        SellerFilter.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(ApplyFilter);
        SellerFilter.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ApplyFilter()));
        MinAmount.TextChanged += (_, _) => ApplyFilter();
        MaxAmount.TextChanged += (_, _) => ApplyFilter();
        OnlyMissing.Checked += (_, _) => ApplyFilter();
        OnlyMissing.Unchecked += (_, _) => ApplyFilter();
        Loaded += async (_, _) => await LoadAsync();
    }

    private Task LoadAsync(int? selectId = null) => Ui.RunAsync(null, async () =>
    {
        _all = await ApiClient.Current.GetAgreementsAsync();
        var typed = SellerFilter.Text;
        SellerFilter.ItemsSource = _all.Select(a => a.SellerName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
        SellerFilter.Text = typed;
        _ready = true;
        ApplyFilter();
        if (selectId is { } id) Grid.SelectedItem = (Grid.ItemsSource as IEnumerable<PurchaseAgreementDto>)?.FirstOrDefault(a => a.Id == id);
    });

    private void ApplyFilter()
    {
        if (!_ready) return;
        IEnumerable<PurchaseAgreementDto> q = _all.Where(a => Period.Contains(a.Date));
        var seller = SellerFilter.Text.Trim();
        if (seller.Length > 0)
            q = q.Where(a => a.SellerName.Contains(seller, StringComparison.OrdinalIgnoreCase)
                             || (a.SellerAddress?.Contains(seller, StringComparison.OrdinalIgnoreCase) ?? false)
                             || a.Number.Contains(seller, StringComparison.OrdinalIgnoreCase));
        if (MinAmount.Text.Trim().Length > 0 && Ui.TryParseDecimal(MinAmount.Text, out var min)) q = q.Where(a => a.Total >= min);
        if (MaxAmount.Text.Trim().Length > 0 && Ui.TryParseDecimal(MaxAmount.Text, out var max)) q = q.Where(a => a.Total <= max);
        if (OnlyMissing.IsChecked == true) q = q.Where(a => a.AddressMissing);

        var selected = Selected?.Id;
        var list = q.OrderByDescending(a => a.Date).ThenByDescending(a => a.Number).ToList();
        Grid.ItemsSource = list;
        if (selected is { } id) Grid.SelectedItem = list.FirstOrDefault(a => a.Id == id);
        Totals.Text = $"{list.Count} overeenkomsten · {Period.Label}   |   {list.Sum(a => a.ItemCount)} stuks   ·   totaal {Ui.Money(list.Sum(a => a.Total))}" +
                      (list.Count(a => a.AddressMissing) is > 0 and var missing ? $"   ·   ⚠ {missing} zonder adres" : "");
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _ready = false;
        SellerFilter.Text = MinAmount.Text = MaxAmount.Text = "";
        OnlyMissing.IsChecked = false;
        _ready = true;
        ApplyFilter();
    }

    private PurchaseAgreementDto? Selected => Grid.SelectedItem as PurchaseAgreementDto;

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Editor.IsEnabled = Selected is not null;
        if (Selected is not { } a) return;
        EditorTitle.Text = $"{a.Number} – {Ui.Money(a.Total)}";
        NameBox.Text = a.SellerName;
        AddressBox.Text = a.SellerAddress;
        EmailBox.Text = a.SellerEmail;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } a) return;
        await Ui.RunAsync(SaveButton, async () =>
        {
            await ApiClient.Current.UpdateSellerAsync(a.Id, new UpdateSellerRequest(NameBox.Text, AddressBox.Text, EmailBox.Text));
            await LoadAsync(a.Id);
        });
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } a) Ui.Open(ApiClient.Current.BrowserUrl($"api/purchase-agreements/{a.Id}/document"));
    }
}
