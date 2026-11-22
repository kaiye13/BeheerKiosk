using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Boekhouding.Client.Services;
using Boekhouding.Shared;
using Microsoft.Web.WebView2.Core;

namespace Boekhouding.Client.Views;

public partial class StreamView : UserControl
{
    // Snel verversen: de server haalt tijdens een stream elke 2 s nieuwe orders op.
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _loading;
    private StreamDashboardDto? _last;

    public StreamView()
    {
        InitializeComponent();
        _timer.Tick += async (_, _) => await LoadAsync();
        Loaded += async (_, _) =>
        {
            _timer.Start();
            await LoadAsync();
            await InitRipToolAsync();
        };
        // Labels uit de rip-wachtrij (achtergrondtaak) ook hier in de statusbalk tonen.
        Action<string, bool> onLabel = (text, error) => Dispatcher.Invoke(() => SetStatus(text, error));
        LabelService.StatusChanged += onLabel;
        // Naam van "Nu rippen" klaarzetten naast "Print label" (als deze pc niet automatisch print).
        Action<string, string> onName = (name, reference) => Dispatcher.Invoke(() =>
        {
            // Geen focus wegnemen: Spatie/N in de rip-wachtrij moeten blijven werken.
            ManualName.Text = name;
            _queuedName = name;
            _queuedReference = reference;
            _ = ShowQueuedShippingAsync(name, reference);
            SetStatus($"Nu rippen: {name} — klik op 🖨 Print label (of Enter) om het label te printen.", error: false);
        });
        LabelQueueWorker.NameArrived += onName;
        Unloaded += (_, _) =>
        {
            LabelService.StatusChanged -= onLabel;
            LabelQueueWorker.NameArrived -= onName;
            _timer.Stop();
            RipTool.Dispose();
        };
    }

    // ---------- Dashboard ----------

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var d = _last = await ApiClient.Current.GetStreamDashboardAsync();
            Render(d);
        }
        catch (ApiException ex)
        {
            HeaderSub.Text = "⚠ " + ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Render(StreamDashboardDto d)
    {
        var live = d.Stream is { IsActive: true };
        LiveDot.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        StartPanel.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        HeaderTitle.Text = d.Stream?.Title ?? "Live stream";
        var updated = $"bijgewerkt {DateTime.Now:HH:mm:ss}" + (d.UsesDummyData ? " · dummy-data" : "");
        HeaderSub.Text = live
            ? $"Gestart om {d.Stream!.StartedAt:HH:mm} · {Duration(DateTime.Now - d.Stream.StartedAt)} bezig · {updated}"
            : $"Geen stream actief: cijfers van vandaag. Start een stream om vanaf dat moment te tellen. · {updated}";

        Donut.SetValues(d.RevenueShopify, d.RevenueTikTok, d.Goal);
        LegendShopify.Text = Ui.Money(d.RevenueShopify);
        LegendTikTok.Text = Ui.Money(d.RevenueTikTok);
        LegendRemaining.Text = d.Revenue >= d.Goal ? "Doel bereikt!" : $"Nog {Ui.Money(d.Goal - d.Revenue)} te gaan";

        Kpis.Children.Clear();
        Kpi("Orders", d.OrderCount.ToString(), $"{d.CustomerCount} klanten");
        Kpi("Gemiddelde order", Ui.Money(d.AverageOrder), "incl. btw");
        Kpi("Verzending betaald", d.PaidShippingOrders.ToString(), $"{Ui.Money(d.ShippingCollected)} ontvangen", "#166534");
        Kpi("Spaarders", d.SaverOrders.ToString(), "orders zonder verzending", "#92400E");
        Kpi("Gratis verzending", d.FreeShippingOrders.ToString(), "boven drempel", "#3730A3");
        Kpi("Labels te printen", d.LabelsPending.ToString(), "nog geen label", d.LabelsPending > 0 ? "#B91C1C" : null);
        Kpi("Nu in de shop", d.LastPixelEvent is null && !d.UsesDummyData ? "–" : d.LiveShoppers.Count.ToString(),
            $"{d.LiveShoppers.Count(s => s.Stage == ShopStage.Cart)} mandje · {d.LiveShoppers.Count(s => s.Stage == ShopStage.Checkout)} betalen");
        Kpi("Nieuwe klachten", d.NewComplaints.ToString(), "klik om te openen", d.NewComplaints > 0 ? "#B91C1C" : null,
            () => MainWindow.Instance.Navigate("complaints"));

        SetIfChanged(TopProducts, d.TopProducts);
        SetIfChanged(OrdersGrid, d.Orders);
        OrdersSummary.Text = $"{d.Orders.Count} orders · {Ui.Money(d.Revenue)}";
        SetIfChanged(CustomersGrid, d.Customers);
        SetIfChanged(ShoppersGrid, d.LiveShoppers);
        SetIfChanged(AbandonedGrid, d.AbandonedCheckouts);
        var pixelLinked = d.LastPixelEvent is not null || d.UsesDummyData;
        ShoppersHint.Visibility = pixelLinked ? Visibility.Collapsed : Visibility.Visible;
        ShoppersTab.Header = pixelLinked ? $"Nu in de shop ({d.LiveShoppers.Count})" : "Nu in de shop (niet gekoppeld)";
        AbandonedTab.Header = $"Verlaten checkouts ({d.AbandonedCheckouts.Count})";
    }

    private async void PixelCode_Click(object sender, RoutedEventArgs e) => await Ui.RunAsync(PixelButton, async () =>
    {
        var p = await ApiClient.Current.GetPixelCodeAsync();
        Clipboard.SetText(p.Code);
        var steps = "De pixelcode staat op je klembord.\n\n" +
                    "In Shopify:\n" +
                    "1. Instellingen → Klantevenementen → Aangepaste pixel toevoegen (naam: Ship & Ripper live).\n" +
                    "2. Wis de voorbeeldcode en plak (Ctrl+V).\n" +
                    "3. Klantprivacy → Toestemming: kies \"Analyse\". Gegevensverkoop: \"komt niet in aanmerking als verkoop\".\n" +
                    "4. Opslaan, daarna Verbinden.\n\n" +
                    $"De pixel stuurt naar: {p.Server}";
        Ui.Info(p.Warnings.Count == 0 ? steps : steps + "\n\n⚠ " + string.Join("\n⚠ ", p.Warnings));
    });

    private void Abandoned_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (AbandonedGrid.SelectedItem is AbandonedCheckoutDto { RecoveryUrl: { } url }) Ui.Open(url);
    }

    /// <summary>
    /// Lijst enkel vervangen als de inhoud veranderd is: anders springt ze elke 2 s naar boven
    /// en kan een klik op "Details" net tijdens het verversen wegvallen.
    /// </summary>
    private static void SetIfChanged<T>(ItemsControl list, List<T> items)
    {
        var fingerprint = JsonSerializer.Serialize(items);
        if (list.Tag as string == fingerprint) return;
        list.Tag = fingerprint;
        list.ItemsSource = items;
    }

    private static string Duration(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}u {t.Minutes:00}m" : $"{t.Minutes}m";

    private void Kpi(string title, string value, string sub, string? color = null, Action? onClick = null)
    {
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = title, Foreground = (Brush)FindResource("Muted"), FontSize = 12 });
        p.Children.Add(new TextBlock
        {
            Text = value, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0),
            Foreground = color is null ? (Brush)FindResource("Text") : (Brush)new BrushConverter().ConvertFrom(color)!,
        });
        p.Children.Add(new TextBlock { Text = sub, Foreground = (Brush)FindResource("Muted"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        var card = new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(4), Padding = new Thickness(12, 10, 12, 10), Child = p };
        if (onClick is not null)
        {
            card.Cursor = Cursors.Hand;
            card.MouseLeftButtonUp += (_, _) => onClick();
        }
        Kpis.Children.Add(card);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.TryParseDecimal(GoalBox.Text, out var goal) || goal <= 0) { Ui.Info("Vul een geldig doelbedrag in."); return; }
        await Ui.RunAsync(StartButton, async () =>
        {
            await ApiClient.Current.StartStreamAsync(new StartStreamRequest(TitleBox.Text, goal));
            await LoadAsync();
        });
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm("Stream stoppen? De cijfers van deze stream blijven bewaard.")) return;
        await Ui.RunAsync(StopButton, async () =>
        {
            await ApiClient.Current.StopStreamAsync();
            await LoadAsync();
        });
    }

    // ---------- Rip-tool + labels ----------

    private string RipToolUrl
    {
        get
        {
            var s = ApiClient.Current.Settings;
            return string.IsNullOrWhiteSpace(s.RipToolUrl) ? ClientSettings.DefaultRipToolUrl : s.RipToolUrl;
        }
    }

    private async Task InitRipToolAsync()
    {
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShipRipperBoekhouding", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await RipTool.EnsureCoreWebView2Async(env);
            await RipTool.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(RipToolScript.Build(ApiClient.Current.Settings));
            RipTool.CoreWebView2.WebMessageReceived += OnRipToolMessage;
            RipTool.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess) HideRipToolError();
                else ShowRipToolError($"De rip-wachtrij is niet bereikbaar op {RipToolUrl}.\n\n" +
                                      "Start ze op deze pc (in de map van de rip-tool: npm start) en klik daarna op ⟳.");
            };
            RipTool.CoreWebView2.Navigate(RipToolUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowRipToolError("Microsoft Edge WebView2 Runtime ontbreekt. Installeer die via https://go.microsoft.com/fwlink/p/?LinkId=2124703 en herstart de app.");
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or InvalidOperationException)
        {
            ShowRipToolError($"Rip-tool kon niet geladen worden ({RipToolUrl}): {ex.Message}");
        }
    }

    private void ShowRipToolError(string text)
    {
        RipTool.Visibility = Visibility.Collapsed;
        RipToolError.Text = text;
        RipToolError.Visibility = Visibility.Visible;
    }

    private void HideRipToolError()
    {
        RipTool.Visibility = Visibility.Visible;
        RipToolError.Visibility = Visibility.Collapsed;
    }

    // Opnieuw het ingestelde adres laden (ook na een foutpagina, of als de wachtrij net gestart is).
    private void Reload_Click(object sender, RoutedEventArgs e) => RipTool.CoreWebView2?.Navigate(RipToolUrl);

    private async void OnRipToolMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var name = doc.RootElement.GetProperty("name").GetString()?.Trim() ?? "";
            var manual = doc.RootElement.GetProperty("source").GetString() == "manual";
            if (!manual && !ApiClient.Current.Settings.AutoPrintOnDone) return;
            if (name.Length == 0)
            {
                SetStatus("Geen naam gevonden in de rip-tool. Controleer de selectors in Instellingen, of typ de naam onderaan.", error: true);
                return;
            }
            await PrintLabelAsync(name);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or ArgumentException)
        {
            SetStatus("Onverwacht bericht van de rip-tool: " + ex.Message, error: true);
        }
    }

    private async void ManualPrint_Click(object sender, RoutedEventArgs e)
    {
        var name = ManualName.Text.Trim();
        if (name.Length == 0) { Ui.Info("Typ eerst de naam van de klant."); return; }
        // Naam uit de wachtrij niet aangepast: de bestelling zelf bepaalt verzending/spaarder.
        var reference = name == _queuedName ? _queuedReference : null;
        await PrintLabelAsync(name, reference);
        ManualName.Clear();
        _queuedName = _queuedReference = null;
    }

    private void ManualName_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        ManualPrint_Click(sender, e);
    }

    private string? _queuedName, _queuedReference;

    /// <summary>Al vóór het printen tonen wat er onderaan het label komt (verzending betaald / spaarder / …).</summary>
    private async Task ShowQueuedShippingAsync(string name, string reference)
    {
        try
        {
            var source = reference.Split(':')[0];
            var info = await ApiClient.Current.GetLabelInfoAsync(name, source is "shopify" or "tiktok" ? reference : null);
            if (_queuedName != name) return;   // intussen al een volgende
            var shipping = !info.Matched ? "⚠ geen bestelling gevonden: controleer verzending" : info.Kind switch
            {
                Boekhouding.Shared.ShippingKind.Paid => "VERZENDING BETAALD",
                Boekhouding.Shared.ShippingKind.Saver => "SPAARDER" + (info.OpenSaverOrders > 1 ? $" ({info.OpenSaverOrders} open orders)" : ""),
                Boekhouding.Shared.ShippingKind.Free => "GRATIS VERZENDING",
                _ => "controleer verzending",
            };
            SetStatus($"Nu rippen: {name} · {shipping}{(info.Matched ? $" · {info.OrderNumbers}" : "")} — klik op 🖨 Print label.", error: !info.Matched);
        }
        catch (Exception)
        {
            // server even niet bereikbaar: de melding "klik op Print label" blijft staan
        }
    }

    private async Task PrintLabelAsync(string name, string? reference = null)
    {
        try
        {
            var source = reference?.Split(':')[0];
            await LabelService.PrintForNameAsync(name, null, source is "shopify" or "tiktok" ? reference : null); // meldt zelf in de statusbalk
            await LoadAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Label voor {name} niet geprint: {ex.Message}", error: true);
        }
    }

    private void SetStatus(string text, bool error)
    {
        PrintStatusBar.Visibility = Visibility.Visible;
        PrintStatusBar.Background = new SolidColorBrush(error ? Color.FromRgb(0xFE, 0xF3, 0xC7) : Color.FromRgb(0xEC, 0xFD, 0xF5));
        PrintStatus.Foreground = new SolidColorBrush(error ? Color.FromRgb(0x92, 0x40, 0x0E) : Color.FromRgb(0x06, 0x5F, 0x46));
        PrintStatus.Text = text;
    }

    private async void Order_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        await Ui.ShowOrderAsync(((System.Windows.Controls.DataGrid)sender).SelectedItem, System.Windows.Window.GetWindow(this));
}
