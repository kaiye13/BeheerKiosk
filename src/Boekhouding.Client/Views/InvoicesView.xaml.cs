using System.IO;
using System.Windows;
using System.Windows.Controls;
using Boekhouding.Client.Services;
using Boekhouding.Shared;
using Microsoft.Win32;

namespace Boekhouding.Client.Views;

public partial class InvoicesView : UserControl
{
    private List<PurchaseInvoiceDto> _all = [];
    private bool _ready;

    public InvoicesView()
    {
        InitializeComponent();
        VatFilter.Items.Add(new ComboBoxItem { Content = "Alle btw-types" });
        foreach (var t in Enum.GetValues<PurchaseVatType>()) VatFilter.Items.Add(new ComboBoxItem { Content = t.Label(), Tag = t });
        SourceFilter.Items.Add(new ComboBoxItem { Content = "Alle" });
        SourceFilter.Items.Add(new ComboBoxItem { Content = "Billit", Tag = "Billit" });
        SourceFilter.Items.Add(new ComboBoxItem { Content = "Manueel", Tag = "Manueel" });
        VatFilter.SelectedIndex = SourceFilter.SelectedIndex = 0;

        Period.Changed += (_, _) => ApplyFilter();
        VatFilter.SelectionChanged += (_, _) => ApplyFilter();
        SourceFilter.SelectionChanged += (_, _) => ApplyFilter();
        SupplierFilter.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(ApplyFilter);
        SupplierFilter.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ApplyFilter()));
        MinAmount.TextChanged += (_, _) => ApplyFilter();
        MaxAmount.TextChanged += (_, _) => ApplyFilter();
        Loaded += async (_, _) => await LoadAsync();
    }

    private Task LoadAsync() => Ui.RunAsync(null, async () =>
    {
        _all = await ApiClient.Current.GetInvoicesAsync();
        var typed = SupplierFilter.Text;
        SupplierFilter.ItemsSource = _all.Select(i => i.SupplierName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
        SupplierFilter.Text = typed;
        _ready = true;
        ApplyFilter();
    });

    private void ApplyFilter()
    {
        if (!_ready) return;
        IEnumerable<PurchaseInvoiceDto> q = _all.Where(i => Period.Contains(i.InvoiceDate));

        var supplier = SupplierFilter.Text.Trim();
        if (supplier.Length > 0)
            q = q.Where(i => i.SupplierName.Contains(supplier, StringComparison.OrdinalIgnoreCase)
                             || (i.SupplierVatNumber?.Contains(supplier, StringComparison.OrdinalIgnoreCase) ?? false)
                             || i.InvoiceNumber.Contains(supplier, StringComparison.OrdinalIgnoreCase));
        if (Ui.TryParseDecimal(MinAmount.Text, out var min) && MinAmount.Text.Trim().Length > 0) q = q.Where(i => i.GrossAmount >= min);
        if (Ui.TryParseDecimal(MaxAmount.Text, out var max) && MaxAmount.Text.Trim().Length > 0) q = q.Where(i => i.GrossAmount <= max);
        if ((VatFilter.SelectedItem as ComboBoxItem)?.Tag is PurchaseVatType t) q = q.Where(i => i.VatType == t);
        if ((SourceFilter.SelectedItem as ComboBoxItem)?.Tag is string s) q = q.Where(i => i.Source == s);

        var list = q.ToList();
        Grid.ItemsSource = list;
        Totals.Text = $"{list.Count} facturen · {Period.Label}   |   excl. btw {Ui.Money(list.Sum(i => i.NetAmount))}   ·   " +
                      $"btw {Ui.Money(list.Sum(i => i.VatAmount))}   ·   incl. {Ui.Money(list.Sum(i => i.GrossAmount))}" +
                      (list.Any(i => i.VatType == PurchaseVatType.IntraCommunity0)
                          ? $"   ·   waarvan intracommunautair {Ui.Money(list.Where(i => i.VatType == PurchaseVatType.IntraCommunity0).Sum(i => i.NetAmount))}"
                          : "");
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _ready = false;
        SupplierFilter.Text = MinAmount.Text = MaxAmount.Text = "";
        VatFilter.SelectedIndex = SourceFilter.SelectedIndex = 0;
        _ready = true;
        ApplyFilter();
    }

    private PurchaseInvoiceDto? Selected
    {
        get
        {
            if (Grid.SelectedItem is PurchaseInvoiceDto i) return i;
            Ui.Info("Selecteer eerst een factuur.");
            return null;
        }
    }

    private async void Attach_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } inv) return;
        var dlg = new OpenFileDialog { Filter = "Facturen (*.pdf;*.png;*.jpg;*.jpeg)|*.pdf;*.png;*.jpg;*.jpeg|Alle bestanden|*.*" };
        if (dlg.ShowDialog() != true) return;
        await Ui.RunAsync(AttachButton, async () =>
        {
            await ApiClient.Current.UploadInvoiceFileAsync(inv.Id, dlg.FileName);
            await LoadAsync();
        });
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } inv) return;
        if (!inv.HasFile) { Ui.Info("Aan deze factuur is nog geen bestand gekoppeld."); return; }
        await Ui.RunAsync(OpenButton, async () =>
        {
            var bytes = await ApiClient.Current.DownloadInvoiceFileAsync(inv.Id);
            var path = Path.Combine(Path.GetTempPath(), "ShipRipperBoekhouding", $"{inv.Id}_{inv.FileName}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
            Ui.Open(path);
        });
    }

    private void New_Click(object sender, RoutedEventArgs e) => MainWindow.Instance.Navigate("newpurchase", "invoice");
}
