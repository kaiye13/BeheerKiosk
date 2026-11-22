using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Boekhouding.Client.Services;
using Boekhouding.Client.Views;

namespace Boekhouding.Client;

public partial class MainWindow : Window
{
    public static MainWindow Instance { get; private set; } = null!;
    private object? _pendingArg;
    private readonly LabelQueueWorker _labelQueue = new();

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();
        // "Details ›" in eender welke orderlijst opent het ordervenster.
        // Labels van de rip-wachtrij printen (enkel als dat in Instellingen aangezet is op deze pc).
        _labelQueue.Start();
        CommandBindings.Add(new CommandBinding(Ui.ShowOrderCommand, async (_, e) => await Ui.ShowOrderAsync(e.Parameter, this)));
        Loaded += async (_, _) => await CheckConnectionAsync(askSettingsIfDown: true);
    }

    /// <summary>Naar een andere pagina gaan, optioneel met een argument (bv. welk tabblad).</summary>
    public void Navigate(string key, object? arg = null)
    {
        _pendingArg = arg;
        var button = Nav.Children.OfType<RadioButton>().First(b => (string)b.Tag == key);
        if (button.IsChecked == true) Show(key);
        else button.IsChecked = true;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (Host is null) return; // tijdens InitializeComponent
        Show((string)((RadioButton)sender).Tag);
    }

    private void Show(string key)
    {
        var arg = _pendingArg;
        _pendingArg = null;
        Host.Content = key switch
        {
            "stream" => new StreamView(),
            "followup" => new FollowUpView(),
            "complaints" => new ComplaintsView(),
            "dashboard" => new DashboardView(),
            "orders" => new OrdersView(),
            "products" => new ProductsView(),
            "overview" => new OverviewView(),
            "newpurchase" => new NewPurchaseView(arg as string),
            "agreements" => new AgreementsView(),
            "invoices" => new InvoicesView(),
            "export" => new ExportView(),
            _ => Host.Content,
        };
    }

    public async Task CheckConnectionAsync(bool askSettingsIfDown = false)
    {
        var api = ApiClient.Current;
        ServerText.Text = api.Settings.ServerUrl;
        StatusText.Text = "Verbinden…";
        var ok = await api.PingAsync();
        StatusDot.Fill = ok ? new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)) : new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        StatusText.Text = ok ? "Verbonden met server" : "Server niet bereikbaar";
        if (ok) Show(CurrentKey());
        else if (askSettingsIfDown) OpenSettings();
    }

    private string CurrentKey() => (string)Nav.Children.OfType<RadioButton>().First(b => b.IsChecked == true).Tag;

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private async void OpenSettings()
    {
        var dlg = new SettingsWindow { Owner = this };
        if (dlg.ShowDialog() == true) await CheckConnectionAsync();
    }
}
