using System.Windows;
using System.Windows.Controls;
using Boekhouding.Client.Services;
using Boekhouding.Shared;

namespace Boekhouding.Client.Views;

public partial class FollowUpView : UserControl
{
    private List<FollowUpOrderDto> _orders = [];
    private const int OpenOnly = -1;

    public FollowUpView()
    {
        InitializeComponent();
        StageFilter.Items.Add(new ComboBoxItem { Content = "Alles nog open", Tag = OpenOnly });
        StageFilter.Items.Add(new ComboBoxItem { Content = "Alle statussen" });
        foreach (var s in Enum.GetValues<FulfillmentStage>())
        {
            StageFilter.Items.Add(new ComboBoxItem { Content = s.Label(), Tag = s });
            StageBox.Items.Add(new ComboBoxItem { Content = s.Label(), Tag = s });
        }
        StageFilter.SelectedIndex = 0;
        ChannelFilter.Items.Add(new ComboBoxItem { Content = "Alle kanalen" });
        ChannelFilter.Items.Add(new ComboBoxItem { Content = "Website (Shopify)", Tag = SalesChannel.Shopify });
        ChannelFilter.Items.Add(new ComboBoxItem { Content = "TikTok Shop", Tag = SalesChannel.TikTokShop });
        ChannelFilter.SelectedIndex = 0;
        Loaded += async (_, _) => await LoadAsync();
    }

    private int Days => int.Parse((string)((ComboBoxItem)DaysFilter.SelectedItem).Tag);

    private Task LoadAsync(int? selectId = null) => Ui.RunAsync(null, async () =>
    {
        _orders = await ApiClient.Current.GetFollowUpAsync(Days);
        ApplyFilter();
        if (selectId is { } id) Grid.SelectedItem = (Grid.ItemsSource as List<FollowUpOrderDto>)?.FirstOrDefault(o => o.Id == id);
    });

    private async void Days_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await LoadAsync();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (Grid is null || ChannelFilter is null || Summary is null) return;
        IEnumerable<FollowUpOrderDto> q = _orders;
        switch ((StageFilter.SelectedItem as ComboBoxItem)?.Tag)
        {
            case OpenOnly: q = q.Where(o => o.Stage != FulfillmentStage.Delivered || o.OpenComplaints > 0); break;
            case FulfillmentStage s: q = q.Where(o => o.Stage == s); break;
        }
        if ((ChannelFilter.SelectedItem as ComboBoxItem)?.Tag is SalesChannel c) q = q.Where(o => o.Channel == c);
        var term = Search.Text.Trim();
        if (term.Length > 0)
            q = q.Where(o => o.CustomerName.Contains(term, StringComparison.OrdinalIgnoreCase)
                             || o.OrderNumber.Contains(term, StringComparison.OrdinalIgnoreCase)
                             || (o.TrackingNumber?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                             || (o.CustomerEmail?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        var list = q.ToList();
        Grid.ItemsSource = list;

        int Count(FulfillmentStage s) => _orders.Count(o => o.Stage == s);
        Summary.Text = $"{list.Count} getoond · {Count(FulfillmentStage.New) + Count(FulfillmentStage.Ripped) + Count(FulfillmentStage.Packed)} te verzenden · " +
                       $"{Count(FulfillmentStage.Shipped)} onderweg · {Count(FulfillmentStage.Saving)} spaarorders · " +
                       $"{Count(FulfillmentStage.Problem)} probleem · {_orders.Sum(o => o.OpenComplaints)} open klachten";
    }

    private FollowUpOrderDto? Selected => Grid.SelectedItem as FollowUpOrderDto;

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Editor.IsEnabled = Selected is not null;
        if (Selected is not { } o) return;
        EditorTitle.Text = $"{o.OrderNumber} – {o.CustomerName}";
        EditorInfo.Text = $"{o.Channel.Label()} · {o.OrderDate:dd/MM/yyyy HH:mm} · {Ui.Money(o.TotalIncl)}\n{o.Items}\n" +
                          $"{o.ShippingKind.Label()}{(o.ShippingMethod is null ? "" : $" ({o.ShippingMethod})")}" +
                          (o.LabelPrintedAt is { } l ? $"\nLabel geprint {l:dd/MM HH:mm}" : "") +
                          (o.CustomerEmail is null ? "" : $"\n{o.CustomerEmail}");
        StageBox.SelectedItem = StageBox.Items.Cast<ComboBoxItem>().First(i => (FulfillmentStage)i.Tag == o.Stage);
        CarrierBox.Text = o.TrackingCarrier ?? "";
        TrackingBox.Text = o.TrackingNumber ?? "";
        NotesBox.Text = o.InternalNotes ?? "";
        TrackingInfo.Text = o.TrackingStatus is null ? "" :
            $"Status vervoerder: {o.TrackingStatus}" + (o.ShippedAt is { } s ? $" · verzonden {s:dd/MM}" : "") + (o.DeliveredAt is { } d ? $" · geleverd {d:dd/MM}" : "");
        TrackButton.IsEnabled = o.TrackingUrl is not null;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } o) return;
        var req = new UpdateFollowUpRequest((FulfillmentStage)((ComboBoxItem)StageBox.SelectedItem).Tag, CarrierBox.Text, TrackingBox.Text, NotesBox.Text);
        await Ui.RunAsync(SaveButton, async () =>
        {
            await ApiClient.Current.UpdateFollowUpAsync(o.Id, req);
            await LoadAsync(o.Id);
        });
    }

    private void Track_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.TrackingUrl is { } url) Ui.Open(url);
    }

    private async void Order_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        await Ui.ShowOrderAsync(((System.Windows.Controls.DataGrid)sender).SelectedItem, System.Windows.Window.GetWindow(this));
}
