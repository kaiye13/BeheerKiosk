using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Boekhouding.Client.Services;
using Boekhouding.Shared;
using Microsoft.Win32;

namespace Boekhouding.Client.Views;

public partial class ExportView : UserControl
{
    public ExportView()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
        Period.Changed += async (_, _) => await LoadAsync();
    }

    private Task LoadAsync() => Ui.RunAsync(null, async () =>
    {
        var r = await ApiClient.Current.GetReportAsync(Period.From, Period.To);
        Warnings.ItemsSource = r.Warnings.Select(w => "⚠ " + w).ToList();

        var pay = r.VatBalance >= 0;
        BalanceLabel.Text = pay ? "Btw te betalen" : "Btw terug te vorderen";
        BalanceSub.Text = $"Verschuldigd {Ui.Money(r.VatDueTotal)} − aftrekbaar {Ui.Money(r.VatDeductibleTotal)}";
        BalanceValue.Text = Ui.Money(Math.Abs(r.VatBalance));
        BalanceValue.Foreground = new SolidColorBrush(pay ? Color.FromRgb(0xB9, 0x1C, 0x1C) : Color.FromRgb(0x05, 0x96, 0x69));

        Sections.Children.Clear();
        Section("Omzet per kanaal (incl. btw)",
            r.PerChannel.Select(c => ($"{c.Channel.Label()} ({c.Orders} orders)", c.RevenueIncl, false))
                .Append(("Totaal", r.TotalRevenueIncl, true)).ToArray());
        Section("Margegoederen – btw op de winst (21/121)",
            ("Verkoopprijs", r.MarginSalesRevenue, false),
            ("Aankoopprijs", r.MarginSalesCost, false),
            ("Winstmarge", r.MarginProfit, false),
            ("Niet-verrekenbaar verlies", r.MarginLossesIgnored, false),
            ("Btw verschuldigd", r.MarginVat, true));
        Section("Factuurgoederen – normaal 21%",
            ("Omzet incl. btw", r.InvoiceSalesRevenueIncl, false),
            ("Omzet excl. btw", r.InvoiceSalesRevenueExcl, false),
            ("Btw verschuldigd", r.InvoiceSalesVat, true));
        Section("Overige verkopen",
            ("Zonder voorraadkoppeling (incl.)", r.UnallocatedRevenueIncl, false),
            ("Btw hierop", r.UnallocatedVat, false),
            ("Verzendkosten klant (incl.)", r.ShippingRevenueIncl, false),
            ("Btw op verzending", r.ShippingVat, false));
        Section("Aankopen",
            ("Belgische facturen excl. btw", r.PurchasesBelgianNet, false),
            ("Aftrekbare btw", r.PurchasesBelgianVat, true),
            ("Intracommunautair (0%)", r.PurchasesIntraCommunityNet, false),
            ("Verlegde btw (verschuldigd én aftrekbaar)", r.IntraCommunityVatReverseCharged, false),
            ("0% / vrijgesteld", r.PurchasesExemptNet, false),
            ("Margegoederen aangekocht", r.MarginPurchasesTotal, false));
        Section("Btw-saldo",
            ("Totaal verschuldigd", r.VatDueTotal, false),
            ("Totaal aftrekbaar", r.VatDeductibleTotal, false),
            (pay ? "Te betalen" : "Terug te vorderen", Math.Abs(r.VatBalance), true));
    });

    private void Section(string title, params (string Label, decimal Value, bool Bold)[] rows)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("SectionTitle") });
        foreach (var (label, value, bold) in rows)
        {
            var line = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var v = new TextBlock { Text = Ui.Money(value), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            DockPanel.SetDock(v, Dock.Right);
            line.Children.Add(v);
            line.Children.Add(new TextBlock
            {
                Text = label, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = bold ? (Brush)FindResource("Text") : (Brush)FindResource("Muted"),
            });
            panel.Children.Add(line);
        }
        Sections.Children.Add(new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(6), Child = panel });
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            FileName = $"btw-afrekening-{Period.Label.Split(" (")[0].Replace(' ', '-').Replace('/', '-')}.xlsx",
            Filter = "Excel-bestand (*.xlsx)|*.xlsx",
        };
        if (dlg.ShowDialog() != true) return;
        await Ui.RunAsync(ExportButton, async () =>
        {
            await File.WriteAllBytesAsync(dlg.FileName, await ApiClient.Current.ExportAsync(Period.From, Period.To));
            if (Ui.Confirm("Export opgeslagen. Nu openen in Excel?")) Ui.Open(dlg.FileName);
        });
    }
}
