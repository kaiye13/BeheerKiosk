using System.Windows;
using System.Windows.Controls;

namespace Boekhouding.Client.Views;

/// <summary>Keuze van maand + jaar. Vuurt <see cref="Changed"/> bij elke wijziging.</summary>
public class MonthPicker : StackPanel
{
    private readonly ComboBox _month = new() { Width = 120, Margin = new Thickness(0, 0, 6, 0) };
    private readonly ComboBox _year = new() { Width = 80 };

    public event EventHandler? Changed;

    public int Month => _month.SelectedIndex + 1;
    public int Year => (int)_year.SelectedItem;

    public MonthPicker()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        foreach (var name in Ui.Nl.DateTimeFormat.MonthNames.Take(12)) _month.Items.Add(name);
        for (var y = DateTime.Today.Year; y >= 2023; y--) _year.Items.Add(y);
        _month.SelectedIndex = DateTime.Today.Month - 1;
        _year.SelectedItem = DateTime.Today.Year;
        _month.SelectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _year.SelectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        Children.Add(_month);
        Children.Add(_year);
    }
}
