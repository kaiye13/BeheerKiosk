using System.Windows;
using System.Windows.Controls;

namespace Boekhouding.Client.Views;

/// <summary>
/// Periodekeuze: dag, week, maand, kwartaal, jaar of alles, met ‹ › om naar de vorige/volgende periode te gaan.
/// <see cref="From"/> is inclusief, <see cref="To"/> exclusief.
/// </summary>
public class PeriodPicker : StackPanel
{
    public enum Mode { Day, Week, Month, Quarter, Year, All }

    private readonly ComboBox _mode = new() { Width = 100, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _prev = new() { Content = "‹", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 4, 0), ToolTip = "Vorige periode" };
    private readonly Button _next = new() { Content = "›", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Volgende periode" };
    private readonly TextBlock _label = new() { MinWidth = 150, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
    private readonly DatePicker _date = new() { Width = 130, Visibility = Visibility.Collapsed };
    private DateTime _anchor = DateTime.Today;
    private bool _suppress;

    public event EventHandler? Changed;

    public DateTime From { get; private set; }
    public DateTime To { get; private set; }
    public Mode Current => (Mode)((ComboBoxItem)_mode.SelectedItem).Tag;
    public string Label => _label.Text;

    /// <param name="modes">Welke keuzes getoond worden (bv. enkel maand/kwartaal/jaar voor de btw-export).</param>
    public PeriodPicker(Mode initial = Mode.Month, params Mode[] modes)
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        if (modes.Length == 0) modes = Enum.GetValues<Mode>();
        foreach (var m in modes)
            _mode.Items.Add(new ComboBoxItem { Content = ModeName(m), Tag = m });
        _mode.SelectedItem = _mode.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (Mode)i.Tag == initial) ?? _mode.Items[0];

        _mode.SelectionChanged += (_, _) => Update(raise: true);
        _prev.Click += (_, _) => Shift(-1);
        _next.Click += (_, _) => Shift(+1);
        _date.SelectedDateChanged += (_, _) =>
        {
            if (_suppress || _date.SelectedDate is not { } d) return;
            _anchor = d;
            Update(raise: true);
        };

        Children.Add(_mode);
        Children.Add(_prev);
        Children.Add(_label);
        Children.Add(_date);
        Children.Add(_next);
        Update(raise: false);
    }

    public PeriodPicker() : this(Mode.Month) { }

    /// <summary>Startkeuze vanuit XAML, bv. InitialMode="All".</summary>
    public Mode InitialMode
    {
        get => Current;
        set => _mode.SelectedItem = _mode.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (Mode)i.Tag == value) ?? _mode.SelectedItem;
    }

    private static string ModeName(Mode m) => m switch
    {
        Mode.Day => "Dag",
        Mode.Week => "Week",
        Mode.Month => "Maand",
        Mode.Quarter => "Kwartaal",
        Mode.Year => "Jaar",
        _ => "Alles",
    };

    private void Shift(int step)
    {
        _anchor = Current switch
        {
            Mode.Day => _anchor.AddDays(step),
            Mode.Week => _anchor.AddDays(7 * step),
            Mode.Month => _anchor.AddMonths(step),
            Mode.Quarter => _anchor.AddMonths(3 * step),
            Mode.Year => _anchor.AddYears(step),
            _ => _anchor,
        };
        Update(raise: true);
    }

    private void Update(bool raise)
    {
        var nl = Ui.Nl;
        var d = _anchor.Date;
        (From, To, _label.Text) = Current switch
        {
            Mode.Day => (d, d.AddDays(1), d.ToString("ddd d MMM yyyy", nl)),
            Mode.Week => WeekOf(d),
            Mode.Month => (new DateTime(d.Year, d.Month, 1), new DateTime(d.Year, d.Month, 1).AddMonths(1), d.ToString("MMMM yyyy", nl)),
            Mode.Quarter => Quarter(d),
            Mode.Year => (new DateTime(d.Year, 1, 1), new DateTime(d.Year + 1, 1, 1), d.Year.ToString()),
            _ => (new DateTime(2000, 1, 1), new DateTime(2100, 1, 1), "Alle periodes"),
        };

        // Bij "Dag" kun je ook een datum kiezen in de kalender.
        var isDay = Current == Mode.Day;
        _date.Visibility = isDay ? Visibility.Visible : Visibility.Collapsed;
        _label.Visibility = isDay ? Visibility.Collapsed : Visibility.Visible;
        _prev.IsEnabled = _next.IsEnabled = Current != Mode.All;
        _suppress = true;
        _date.SelectedDate = d;
        _suppress = false;

        if (raise) Changed?.Invoke(this, EventArgs.Empty);
    }

    private static (DateTime, DateTime, string) WeekOf(DateTime d)
    {
        var monday = d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
        var week = System.Globalization.ISOWeek.GetWeekOfYear(monday);
        return (monday, monday.AddDays(7), $"week {week} ({monday:dd/MM} – {monday.AddDays(6):dd/MM})");
    }

    private static (DateTime, DateTime, string) Quarter(DateTime d)
    {
        var q = (d.Month - 1) / 3;
        var start = new DateTime(d.Year, q * 3 + 1, 1);
        return (start, start.AddMonths(3), $"Q{q + 1} {d.Year} ({start.ToString("MMM", Ui.Nl)}–{start.AddMonths(2).ToString("MMM", Ui.Nl)})");
    }

    /// <summary>Valt de datum binnen de gekozen periode?</summary>
    public bool Contains(DateTime date) => date >= From && date < To;
}
