using System.Windows;
using Boekhouding.Client.Services;
using Boekhouding.Shared;

namespace Boekhouding.Client.Views;

/// <summary>Wat iemand precies besteld heeft: alle artikelen, verzending, status en tracking.</summary>
public partial class OrderDetailWindow : Window
{
    private readonly OrderDetailDto _order;
    public int OrderId => _order.Id;

    public OrderDetailWindow(OrderDetailDto o)
    {
        _order = o;
        InitializeComponent();
        Title = $"Bestelling {o.OrderNumber} – {o.CustomerName}";
        OrderTitle.Text = $"Bestelling {o.OrderNumber}";
        OrderSub.Text = $"{o.OrderDate:dddd d MMMM yyyy, HH:mm} · {o.Lines.Sum(l => l.Quantity)} artikelen" +
                        (o.FinancialStatus is "paid" ? "" : $" · betaalstatus: {o.FinancialStatus}");
        ChannelBadge.Content = o;
        ShippingBadge.Content = o;

        Customer.Text = o.CustomerName;
        CustomerEmail.Text = o.CustomerEmail ?? "";
        ShippingMethod.Text = o.ShippingMethod ?? o.ShippingKind.Label();
        ShippingCost.Text = o.ShippingIncl > 0 ? $"Verzendkost betaald: {Ui.Money(o.ShippingIncl)}" : "Geen verzendkost op deze order";

        Stage.Text = o.Stage.Label() + (o.LabelPrintedAt is { } l ? $" · label {l:dd/MM HH:mm}" : "");
        Tracking.Text = o.TrackingNumber is null ? "Nog geen tracking" : $"{o.TrackingCarrier} {o.TrackingNumber}";
        TrackingLink.Visibility = o.TrackingUrl is null ? Visibility.Collapsed : Visibility.Visible;

        Lines.ItemsSource = o.Lines;
        Totals.Text = $"Artikelen {Ui.Money(o.Lines.Sum(x => x.TotalIncl))} + verzending {Ui.Money(o.ShippingIncl)} · btw {Ui.Money(o.VatDue)}";
        Total.Text = Ui.Money(o.TotalIncl);
        if (!string.IsNullOrWhiteSpace(o.InternalNotes))
        {
            Notes.Text = "Notitie: " + o.InternalNotes;
            Notes.Visibility = Visibility.Visible;
        }
    }

    private void Tracking_Click(object sender, RoutedEventArgs e)
    {
        if (_order.TrackingUrl is { } url) Ui.Open(url);
    }
}
