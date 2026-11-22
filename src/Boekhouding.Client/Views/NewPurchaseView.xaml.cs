using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Boekhouding.Client.Services;
using Boekhouding.Shared;
using Microsoft.Win32;

namespace Boekhouding.Client.Views;

public partial class NewPurchaseView : UserControl
{
    private record ProductOption(ProductDto Product)
    {
        public string Label => $"{Product.Sku} – {Product.Title}";
    }

    private readonly ObservableCollection<PurchaseLineRequest> _marginLines = [];
    private readonly ObservableCollection<PurchaseLineRequest> _invoiceLines = [];

    public NewPurchaseView(string? tab = null)
    {
        InitializeComponent();
        MarginLines.ItemsSource = _marginLines;
        InvoiceLines.ItemsSource = _invoiceLines;
        _marginLines.CollectionChanged += (_, _) => UpdateTotals();
        _invoiceLines.CollectionChanged += (_, _) => UpdateTotals();

        foreach (var t in Enum.GetValues<PurchaseVatType>())
            VatType.Items.Add(new ComboBoxItem { Content = t.Label(), Tag = t });
        VatType.SelectedIndex = 0;

        ResetMargin();
        ResetInvoice();
        if (tab == "invoice") Tabs.SelectedItem = InvoiceTab;

        // Adressuggesties terwijl je typt: eerder gebruikte adressen + echte adressen (OpenStreetMap).
        AddressAutocomplete.Attach(SellerAddress, () => _knownAddresses);

        Loaded += async (_, _) => await Ui.RunAsync(null, async () =>
        {
            var options = (await ApiClient.Current.GetProductsAsync()).Select(p => new ProductOption(p)).ToList();
            MarginExisting.ItemsSource = options;
            InvoiceExisting.ItemsSource = options;
            _knownAddresses = (await ApiClient.Current.GetAgreementsAsync())
                .Select(a => a.SellerAddress ?? "").Where(a => a.Length > 0).ToList();
        });
    }

    private List<string> _knownAddresses = [];

    // ---------- Gedeeld ----------

    private void Lines_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
        Dispatcher.BeginInvoke(UpdateTotals, DispatcherPriority.Background);

    private void UpdateTotals()
    {
        var m = _marginLines.Sum(l => l.Quantity * l.UnitCost);
        MarginTotal.Text = $"Totaal te betalen aan verkoper: {Ui.Money(m)}  ({_marginLines.Sum(l => l.Quantity)} stuks)";

        var i = _invoiceLines.Sum(l => l.Quantity * l.UnitCost);
        var net = Ui.TryParseDecimal(NetAmount.Text, out var n) ? n : 0m;
        InvoiceTotal.Text = _invoiceLines.Count == 0
            ? "Geen artikelen: factuur wordt enkel als kost geboekt."
            : $"Artikelen: {Ui.Money(i)} excl. btw" + (net > 0 && Math.Abs(net - i) > 0.01m ? $"  ·  ⚠ verschil met factuurtotaal {Ui.Money(net - i)}" : "");
    }

    private static void AddExisting(ComboBox combo, ObservableCollection<PurchaseLineRequest> lines)
    {
        if (combo.SelectedItem is not ProductOption { Product: var p }) { Ui.Info("Kies eerst een product in de lijst."); return; }
        lines.Add(new PurchaseLineRequest
        {
            Sku = p.Sku, Title = p.Title, Quantity = 1, SalePrice = p.Price,
            Vendor = p.Vendor, ProductType = p.ProductType, Tags = p.Tags, Barcode = p.Barcode,
        });
    }

    /// <summary>Lopende celbewerking afronden en lege rijen weglaten.</summary>
    private static List<PurchaseLineRequest> CollectLines(DataGrid grid, IEnumerable<PurchaseLineRequest> lines)
    {
        grid.CommitEdit(DataGridEditingUnit.Cell, true);
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        return lines.Where(l => !string.IsNullOrWhiteSpace(l.Title) || !string.IsNullOrWhiteSpace(l.Sku)).ToList();
    }

    // ---------- Margegoederen ----------

    private void Address_Changed(object sender, TextChangedEventArgs e)
    {
        if (AddressWarning is not null)
            AddressWarning.Visibility = string.IsNullOrWhiteSpace(SellerAddress.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddExistingMargin_Click(object sender, RoutedEventArgs e) => AddExisting(MarginExisting, _marginLines);

    private async void SaveMargin_Click(object sender, RoutedEventArgs e)
    {
        var req = new CreateMarginPurchaseRequest
        {
            Date = MarginDate.SelectedDate ?? DateTime.Today,
            SellerName = SellerName.Text,
            SellerAddress = SellerAddress.Text,
            SellerEmail = SellerEmail.Text,
            Notes = MarginNotes.Text,
            Lines = CollectLines(MarginLines, _marginLines),
            SyncToShopify = MarginShopify.IsChecked == true,
        };
        if (string.IsNullOrWhiteSpace(req.SellerAddress) &&
            !Ui.Confirm("Er is geen adres ingevuld. De overeenkomst wordt als onvolledig gemarkeerd.\n\nToch opslaan?"))
            return;

        await Ui.RunAsync(SaveMargin, async () =>
        {
            var a = await ApiClient.Current.CreateMarginPurchaseAsync(req);
            if (Ui.Confirm($"Opgeslagen als {a.Number} ({a.ItemCount} stuks, {Ui.Money(a.Total)}).{ShopifyText(a.ShopifyMessages)}\n\nVerkoopovereenkomst nu openen om af te drukken?"))
                Ui.Open(ApiClient.Current.BrowserUrl($"api/purchase-agreements/{a.Id}/document"));
            ResetMargin();
        });
    }

    private static string ShopifyText(List<string> messages) =>
        messages.Count == 0 ? "" : "\n\nShopify:\n" + string.Join("\n", messages);

    private void ResetMargin()
    {
        SellerName.Text = SellerAddress.Text = SellerEmail.Text = MarginNotes.Text = "";
        MarginDate.SelectedDate = DateTime.Today;
        _marginLines.Clear();
        UpdateTotals();
    }

    // ---------- Factuurgoederen ----------

    private PurchaseVatType SelectedVatType => (PurchaseVatType)((ComboBoxItem)VatType.SelectedItem).Tag;

    private void VatType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (VatAmount is null || VatType.SelectedItem is null) return;
        var type = SelectedVatType;
        VatAmount.IsEnabled = type == PurchaseVatType.Belgian21;
        if (type != PurchaseVatType.Belgian21) VatAmount.Text = "0,00";
        else RecalculateVat();
        VatTypeHint.Text = type switch
        {
            PurchaseVatType.Belgian21 => "Belgische leverancier: de btw op de factuur is aftrekbaar.",
            PurchaseVatType.IntraCommunity0 => "Leverancier in een ander EU-land (bv. Polen) met 0% en ons btw-nummer: btw wordt verlegd. 21% wordt in de afrekening zowel verschuldigd als aftrekbaar gezet.",
            _ => "Factuur zonder btw (vrijgesteld of buiten de EU). Opgelet: bij invoer van buiten de EU komt de btw via de douane/vervoerder.",
        };
    }

    private void NetAmount_LostFocus(object sender, RoutedEventArgs e)
    {
        RecalculateVat();
        UpdateTotals();
    }

    private void RecalculateVat()
    {
        if (SelectedVatType == PurchaseVatType.Belgian21 && Ui.TryParseDecimal(NetAmount.Text, out var net))
            VatAmount.Text = Vat.OnExclusive(net).ToString("0.00", Ui.Nl);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Facturen (*.pdf;*.png;*.jpg;*.jpeg)|*.pdf;*.png;*.jpg;*.jpeg|Alle bestanden|*.*" };
        if (dlg.ShowDialog() == true) FilePath.Text = dlg.FileName;
    }

    private void AddExistingInvoice_Click(object sender, RoutedEventArgs e) => AddExisting(InvoiceExisting, _invoiceLines);

    private async void SaveInvoice_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.TryParseDecimal(NetAmount.Text, out var net)) { Ui.Info("Vul het totaal excl. btw in."); return; }
        if (!Ui.TryParseDecimal(VatAmount.Text, out var vat)) vat = 0m;

        var req = new CreateInvoicePurchaseRequest
        {
            SupplierName = Supplier.Text,
            SupplierVatNumber = SupplierVat.Text,
            Country = Country.Text,
            InvoiceNumber = InvoiceNumber.Text,
            InvoiceDate = InvoiceDate.SelectedDate ?? DateTime.Today,
            VatType = SelectedVatType,
            NetAmount = net,
            VatAmount = vat,
            Description = InvoiceDescription.Text,
            Lines = CollectLines(InvoiceLines, _invoiceLines),
            SyncToShopify = InvoiceShopify.IsChecked == true,
        };
        var file = FilePath.Text;

        await Ui.RunAsync(SaveInvoice, async () =>
        {
            var inv = await ApiClient.Current.CreateInvoicePurchaseAsync(req);
            if (!string.IsNullOrEmpty(file)) await ApiClient.Current.UploadInvoiceFileAsync(inv.Id, file);
            Ui.Info($"Factuur {inv.InvoiceNumber} van {inv.SupplierName} opgeslagen" +
                    (inv.LineCount > 0 ? $" met {inv.LineCount} voorraadlijn(en)." : " (zonder voorraad).") + ShopifyText(inv.ShopifyMessages));
            ResetInvoice();
        });
    }

    private void ResetInvoice()
    {
        Supplier.Text = SupplierVat.Text = InvoiceNumber.Text = NetAmount.Text = InvoiceDescription.Text = FilePath.Text = "";
        VatAmount.Text = "0,00";
        Country.Text = "BE";
        InvoiceDate.SelectedDate = DateTime.Today;
        VatType.SelectedIndex = 0;
        _invoiceLines.Clear();
        UpdateTotals();
    }
}
