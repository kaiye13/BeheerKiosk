using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Boekhouding.Client.Services;
using Boekhouding.Shared;

namespace Boekhouding.Client;

public static class Ui
{
    public static void ShowError(Exception ex)
    {
        var message = ex is ApiException ? ex.Message : $"Onverwachte fout: {ex.Message}";
        MessageBox.Show(message, "Boekhouding", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public static void Info(string message) =>
        MessageBox.Show(message, "Boekhouding", MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(string message) =>
        MessageBox.Show(message, "Boekhouding", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>Voert een API-actie uit met foutmelding en schakelt het element tijdelijk uit.</summary>
    public static async Task RunAsync(UIElement? busy, Func<Task> action)
    {
        if (busy is not null) busy.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { ShowError(ex); }
        finally { if (busy is not null) busy.IsEnabled = true; }
    }

    /// <summary>Opent het ordervenster. Parameter: een order-regel uit eender welke lijst (stream, dashboard, verkopen, opvolging).</summary>
    public static readonly RoutedUICommand ShowOrderCommand = new("Bestelling bekijken", nameof(ShowOrderCommand), typeof(Ui));

    private static readonly HashSet<int> Opening = [];

    public static async Task ShowOrderAsync(object? row, Window owner)
    {
        int? id = row switch
        {
            StreamOrderDto s => s.OrderId,
            SalesOrderDto s => s.Id,
            FollowUpOrderDto f => f.Id,
            int i => i,
            _ => null,
        };
        if (id is null) return;
        // Al open (of wordt net geopend, bv. dubbelklik): niet nog eens openen.
        if (Application.Current.Windows.OfType<Views.OrderDetailWindow>().FirstOrDefault(w => w.OrderId == id) is { } open)
        {
            open.Activate();
            return;
        }
        if (!Opening.Add(id.Value)) return;
        try
        {
            await RunAsync(null, async () =>
            {
                var order = await ApiClient.Current.GetOrderAsync(id.Value);
                new Views.OrderDetailWindow(order) { Owner = owner }.Show();
            });
        }
        finally
        {
            Opening.Remove(id.Value);
        }
    }

    public static void Open(string pathOrUrl) =>
        Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });

    public static readonly CultureInfo Nl = CultureInfo.GetCultureInfo("nl-BE");
    public static string Money(decimal v) => v.ToString("C", Nl);

    /// <summary>
    /// Accepteert "5,99", "5.99", "1.234,56" en "€ 5,99". Een punt zonder komma is een decimaalteken
    /// (anders wordt "5.99" in nl-BE gelezen als 599).
    /// </summary>
    public static bool TryParseDecimal(string? text, out decimal value)
    {
        var s = (text ?? "").Replace("€", "").Replace(" ", "").Trim();
        if (s.Contains(','))
            return decimal.TryParse(s, NumberStyles.Number, Nl, out value);
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>Voor bewerkbare grids: toont 0,00 en leest tolerant (zie <see cref="Ui.TryParseDecimal"/>).</summary>
public class DecimalConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is decimal d ? d.ToString("0.00", Ui.Nl) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value as string;
        if (string.IsNullOrWhiteSpace(text))
            return Nullable.GetUnderlyingType(targetType) is not null ? null! : 0m;
        return Ui.TryParseDecimal(text, out var d) ? d : DependencyProperty.UnsetValue;
    }
}

/// <summary>Toont Nederlandse labels voor de enums uit Boekhouding.Shared.</summary>
public class EnumLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        SalesChannel c => c.Label(),
        PurchaseVatType t => t.Label(),
        VatRegime r => r.Label(),
        ShippingKind k => k.Label(),
        FulfillmentStage s => s.Label(),
        ComplaintStatus s => s.Label(),
        ShopStage s => s.Label(),
        _ => value?.ToString() ?? "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
