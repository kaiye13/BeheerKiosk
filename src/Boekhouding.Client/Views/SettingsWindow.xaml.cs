using System.Windows;
using Boekhouding.Client.Services;
using Microsoft.Win32;

namespace Boekhouding.Client.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        var s = ApiClient.Current.Settings;
        UrlBox.Text = s.ServerUrl;
        KeyBox.Password = s.ApiKey;

        foreach (var p in LabelPrinter.InstalledPrinters()) PrinterBox.Items.Add(p);
        PrinterBox.Text = s.PrinterName;
        WidthBox.Text = s.LabelWidthMm.ToString(Ui.Nl);
        HeightBox.Text = s.LabelHeightMm.ToString(Ui.Nl);
        TemplateBox.Text = s.LabelTemplatePath;
        NameObjectBox.Text = s.TemplateNameObject;
        InfoObjectBox.Text = s.TemplateInfoObject;

        RipUrlBox.Text = s.RipToolUrl;
        DoneTextBox.Text = s.DoneButtonText;
        DoneSelectorBox.Text = s.DoneButtonSelector;
        ContainerBox.Text = s.ContainerSelector;
        NameSelectorBox.Text = s.NameSelector;
        AutoPrintBox.IsChecked = s.AutoPrintOnDone;
        PrintQueueBox.IsChecked = s.PrintQueueLabels;
        InitRolls(s.LabelWidthMm, s.LabelHeightMm);
        ShowPreview(s.LabelWidthMm, s.LabelHeightMm);
    }

    /// <summary>Brother DK-rollen voor de QL-800 (max. 62 mm breed). Doorlopende rollen: lengte zelf te kiezen.</summary>
    private record Roll(string Name, double Width, double Height, bool Continuous = false)
    {
        public override string ToString() => Name;
    }

    private static readonly Roll[] Rolls =
    [
        new("DK-11209 · 29 × 62 mm (klein adreslabel)", 62, 29),
        new("DK-11201 · 29 × 90 mm (standaard adreslabel)", 90, 29),
        new("DK-11208 · 38 × 90 mm (groot adreslabel)", 90, 38),
        new("DK-11202 · 62 × 100 mm (verzendlabel)", 100, 62),
        new("DK-22205 · doorlopend 62 mm (lengte zelf kiezen)", 62, 40, Continuous: true),
        new("DK-22251 · doorlopend 62 mm zwart/rood (lengte zelf kiezen)", 62, 40, Continuous: true),
        new("Eigen formaat", 0, 0),
    ];

    private bool _settingRoll;

    private void InitRolls(double width, double height)
    {
        _settingRoll = true;
        foreach (var r in Rolls) RollBox.Items.Add(r);
        // Huidige afmetingen herkennen (in eender welke richting), anders "Eigen formaat".
        var (a, b) = (Math.Max(width, height), Math.Min(width, height));
        RollBox.SelectedItem = Rolls.FirstOrDefault(r => !r.Continuous && r.Width > 0
                                                         && Math.Abs(Math.Max(r.Width, r.Height) - a) < 1 && Math.Abs(Math.Min(r.Width, r.Height) - b) < 1)
                               ?? Rolls[^1];
        _settingRoll = false;
    }

    private void Roll_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_settingRoll || RollBox.SelectedItem is not Roll { Width: > 0 } r) return;
        WidthBox.Text = r.Width.ToString(Ui.Nl);
        HeightBox.Text = r.Height.ToString(Ui.Nl);
        if (r.Continuous)
            Ui.Info("Doorlopende rol: de breedte is 62 mm. De lengte van het label kies je zelf bij Labelhoogte (nu 40 mm).\n\nKies in de Brother-driver ook de doorlopende rol, en doe eerst een testlabel.");
        ShowPreview(r.Width, r.Height);
    }

    private static readonly LabelContent Sample = new("Testklant Jansen", "2 orders · 5 art. · #L12345 #L12346", "VERZENDING BETAALD");

    private void ShowPreview(double widthMm, double heightMm)
    {
        static double Dip(double mm) => mm / 25.4 * 96;
        PreviewBox.Child = LabelPrinter.BuildVisual(Sample, Dip(Math.Max(widthMm, heightMm)), Dip(Math.Min(widthMm, heightMm)));
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (Ui.TryParseDecimal(WidthBox.Text, out var w) && Ui.TryParseDecimal(HeightBox.Text, out var h) && w > 0 && h > 0)
            ShowPreview((double)w, (double)h);
    }

    private ClientSettings? Read()
    {
        var url = UrlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            TestResult.Text = "Ongeldig serveradres. Voorbeeld: http://192.168.1.50:5080";
            return null;
        }
        if (RipUrlBox.Text.Trim() is { Length: > 0 } rip && !Uri.TryCreate(rip, UriKind.Absolute, out _))
        {
            Ui.Info("Ongeldig adres voor de rip-tool. Voorbeeld: http://localhost:3000");
            return null;
        }
        if (!Ui.TryParseDecimal(WidthBox.Text, out var w) || !Ui.TryParseDecimal(HeightBox.Text, out var h) || w <= 0 || h <= 0)
        {
            Ui.Info("Ongeldige labelafmetingen.");
            return null;
        }
        return new ClientSettings
        {
            ServerUrl = url,
            ApiKey = KeyBox.Password,
            PrinterName = PrinterBox.Text.Trim(),
            LabelWidthMm = (double)w,
            LabelHeightMm = (double)h,
            LabelTemplatePath = TemplateBox.Text.Trim(),
            TemplateNameObject = NameObjectBox.Text.Trim(),
            TemplateInfoObject = InfoObjectBox.Text.Trim(),
            RipToolUrl = RipUrlBox.Text.Trim(),
            DoneButtonText = DoneTextBox.Text.Trim(),
            DoneButtonSelector = DoneSelectorBox.Text.Trim(),
            ContainerSelector = ContainerBox.Text.Trim(),
            NameSelector = NameSelectorBox.Text.Trim(),
            AutoPrintOnDone = AutoPrintBox.IsChecked == true,
            PrintQueueLabels = PrintQueueBox.IsChecked == true,
        };
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (Read() is not { } s) return;
        TestResult.Text = "Bezig…";
        var client = new ApiClient(s);
        if (!await client.PingAsync()) { TestResult.Text = "✗ Server niet bereikbaar."; return; }
        try
        {
            await client.GetSyncStatusAsync();
            TestResult.Text = "✓ Verbonden en API-sleutel klopt.";
        }
        catch (ApiException ex) { TestResult.Text = "✗ " + ex.Message; }
    }

    private void BrowseTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "P-touch Editor-sjabloon (*.lbx)|*.lbx|Alle bestanden|*.*" };
        if (dlg.ShowDialog() == true) TemplateBox.Text = dlg.FileName;
    }

    private void TestPrint_Click(object sender, RoutedEventArgs e)
    {
        if (Read() is not { } s) return;
        try
        {
            var method = LabelPrinter.Print(Sample, s);
            Ui.Info($"Testlabel verstuurd via {method}.");
        }
        catch (Exception ex)
        {
            Ui.Info("Printen mislukt: " + ex.Message);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Read() is not { } s) return;
        s.Save();
        ApiClient.Reconfigure(s);
        DialogResult = true;
    }
}
