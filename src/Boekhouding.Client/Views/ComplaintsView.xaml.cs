using System.Windows;
using System.Windows.Controls;
using Boekhouding.Client.Services;
using Boekhouding.Shared;

namespace Boekhouding.Client.Views;

public partial class ComplaintsView : UserControl
{
    private record OrderOption(int? Id, string Label);

    private List<ComplaintDto> _all = [];
    private ComplaintDetailDto? _detail;

    public ComplaintsView()
    {
        InitializeComponent();
        foreach (var s in Enum.GetValues<ComplaintStatus>())
        {
            StatusBox.Items.Add(new ComboBoxItem { Content = s.Label(), Tag = s });
            AfterSendBox.Items.Add(new ComboBoxItem { Content = "daarna: " + s.Label(), Tag = s });
        }
        AfterSendBox.SelectedIndex = (int)ComplaintStatus.WaitingForCustomer;
        Loaded += async (_, _) => await LoadAsync();
    }

    private Task LoadAsync(int? selectId = null) => Ui.RunAsync(null, async () =>
    {
        _all = await ApiClient.Current.GetComplaintsAsync();
        ApplyFilter();
        selectId ??= _detail?.Complaint.Id;
        if (selectId is { } id) List.SelectedItem = (List.ItemsSource as List<ComplaintDto>)?.FirstOrDefault(c => c.Id == id);
    });

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (List is null) return;
        List.ItemsSource = ShowResolved.IsChecked == true ? _all : _all.Where(c => c.Status != ComplaintStatus.Resolved).ToList();
        Summary.Text = $"{_all.Count(c => c.Status == ComplaintStatus.New)} nieuw · {_all.Count(c => c.Status == ComplaintStatus.InProgress)} in behandeling · " +
                       $"{_all.Count(c => c.Status == ComplaintStatus.WaitingForCustomer)} wacht op klant · {_all.Count(c => c.Status == ComplaintStatus.Resolved)} opgelost";
    }

    private async void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not ComplaintDto c) return;
        await Ui.RunAsync(null, async () => ShowDetail(await ApiClient.Current.GetComplaintAsync(c.Id)));
    }

    private void ShowDetail(ComplaintDetailDto d)
    {
        if (_detail?.Complaint.Id != d.Complaint.Id) ReplyBox.Clear();
        _detail = d;
        Detail.Visibility = Visibility.Visible;
        var c = d.Complaint;
        DetailSubject.Text = c.Subject;
        DetailFrom.Text = $"{c.FromName} <{c.FromEmail}> · ontvangen {c.ReceivedAt:dd/MM/yyyy HH:mm}";
        StatusBox.SelectedItem = StatusBox.Items.Cast<ComboBoxItem>().First(i => (ComplaintStatus)i.Tag == c.Status);

        var options = new List<OrderOption> { new(null, "(geen order)") };
        options.AddRange(d.CustomerOrders.Select(o => new OrderOption(o.Id, $"{o.OrderNumber} · {o.OrderDate:dd/MM} · {Ui.Money(o.TotalIncl)}")));
        if (d.Order is { } linked && options.All(o => o.Id != linked.Id))
            options.Add(new OrderOption(linked.Id, $"{linked.OrderNumber} · {linked.OrderDate:dd/MM} · {Ui.Money(linked.TotalIncl)}"));
        OrderBox.ItemsSource = options;
        OrderBox.SelectedItem = options.First(o => o.Id == c.OrderId);

        OrderCard.Visibility = d.Order is null ? Visibility.Collapsed : Visibility.Visible;
        if (d.Order is { } order)
            OrderInfo.Text = $"{order.OrderNumber} via {order.Channel.Label()} op {order.OrderDate:dd/MM/yyyy HH:mm} · {Ui.Money(order.TotalIncl)}\n" +
                             $"{order.Items}\nStatus: {order.Stage.Label()} · {order.ShippingKind.Label()}" +
                             (order.TrackingNumber is null ? "" : $" · {order.TrackingCarrier} {order.TrackingNumber} ({order.TrackingStatus})");

        Thread.ItemsSource = d.Messages;
        ThreadScroll.ScrollToEnd();
        ReplyHint.Text = $"Antwoord gaat naar {c.FromEmail}";
        if (ReplyBox.Text.Length == 0) ReplyBox.Text = $"Hallo {c.FromName.Split(' ')[0]},\n\n\n\nMet vriendelijke groeten,\nShip & Ripper";
    }

    private async void SaveMeta_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is null) return;
        var status = (ComplaintStatus)((ComboBoxItem)StatusBox.SelectedItem).Tag;
        var orderId = (OrderBox.SelectedItem as OrderOption)?.Id;
        await Ui.RunAsync(SaveMeta, async () =>
        {
            await ApiClient.Current.UpdateComplaintAsync(_detail.Complaint.Id, new UpdateComplaintRequest(status, orderId));
            await LoadAsync();
        });
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is null) return;
        var body = ReplyBox.Text.Trim();
        if (body.Length == 0) { Ui.Info("Het antwoord is leeg."); return; }
        if (!Ui.Confirm($"Antwoord versturen naar {_detail.Complaint.FromEmail}?")) return;
        var after = (ComplaintStatus)((ComboBoxItem)AfterSendBox.SelectedItem).Tag;
        await Ui.RunAsync(SendButton, async () =>
        {
            var d = await ApiClient.Current.ReplyComplaintAsync(_detail.Complaint.Id, new ReplyRequest(body, after));
            ReplyBox.Clear();
            if (d.Messages.LastOrDefault() is { SendError: { } err }) Ui.Info($"Antwoord bewaard maar NIET verstuurd:\n{err}");
            await LoadAsync(d.Complaint.Id);
        });
    }

    private async void CheckMail_Click(object sender, RoutedEventArgs e) => await Ui.RunAsync(CheckMailButton, async () =>
    {
        await ApiClient.Current.CheckMailAsync();
        await LoadAsync();
    });
}
