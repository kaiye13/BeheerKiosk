using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Boekhouding.Client.Services;

/// <summary>
/// Adressuggesties onder een adresvak terwijl je typt:
/// 1) adressen die al eerder gebruikt werden (verkoopovereenkomsten), 2) echte adressen uit OpenStreetMap (Photon, België/Nederland).
/// Klik (of ↓ + Enter) op een suggestie om ze in te vullen als "straat nr" + nieuwe regel + "postcode gemeente".
/// </summary>
public static class AddressAutocomplete
{
    private static readonly HttpClient Http = CreateHttp();
    // Benelux + rand, zodat suggesties niet uit de rest van de wereld komen.
    private const string Bbox = "2.4,49.4,7.3,53.7";

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ShipRipperBoekhouding/1.0");
        return http;
    }

    /// <param name="known">Eerder gebruikte adressen (wordt bij elke zoekopdracht opnieuw gevraagd).</param>
    public static void Attach(TextBox box, Func<IEnumerable<string>>? known = null)
    {
        var list = new ListBox { MaxHeight = 240, MinWidth = 260, BorderThickness = new Thickness(1) };
        var popup = new Popup
        {
            Child = list, PlacementTarget = box, Placement = PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true,
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        var applying = false;
        CancellationTokenSource? cts = null;

        void Choose()
        {
            if (list.SelectedItem is not Suggestion s) return;
            applying = true;
            box.Text = s.Value;
            box.CaretIndex = box.Text.Length;
            applying = false;
            popup.IsOpen = false;
            box.Focus();
        }

        box.TextChanged += (_, _) =>
        {
            if (applying || !box.IsKeyboardFocusWithin) return;
            timer.Stop();
            timer.Start();
        };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            var query = box.Text.Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (query.Length < 3) { popup.IsOpen = false; return; }

            cts?.Cancel();
            cts = new CancellationTokenSource();
            var token = cts.Token;

            var local = (known?.Invoke() ?? [])
                .Where(a => !string.IsNullOrWhiteSpace(a) && Flat(a).Contains(query, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(4)
                .Select(a => new Suggestion(a, "eerder gebruikt"));
            Show(local.ToList());

            try
            {
                var online = await SearchAsync(query, token);
                if (token.IsCancellationRequested) return;
                Show([.. local, .. online.Where(o => !local.Any(l => Same(l.Value, o.Value)))]);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Offline of dienst even niet bereikbaar: enkel de eerder gebruikte adressen.
            }
        };

        void Show(List<Suggestion> items)
        {
            list.ItemsSource = items;
            popup.Width = Math.Max(box.ActualWidth, 260);
            popup.IsOpen = items.Count > 0 && box.IsKeyboardFocusWithin;
        }

        box.PreviewKeyDown += (_, e) =>
        {
            if (!popup.IsOpen) return;
            if (e.Key == Key.Down)
            {
                list.SelectedIndex = Math.Min(list.SelectedIndex + 1, list.Items.Count - 1);
                list.ScrollIntoView(list.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == Key.Up && list.SelectedIndex > 0)
            {
                list.SelectedIndex--;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && list.SelectedIndex >= 0)
            {
                Choose();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                popup.IsOpen = false;
                e.Handled = true;
            }
        };
        list.PreviewMouseLeftButtonUp += (_, _) => Choose();
        box.LostKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is DependencyObject d && IsInside(d, list)) return;
            popup.IsOpen = false;
        };
    }

    private static bool IsInside(DependencyObject d, DependencyObject parent)
    {
        for (var cur = d; cur is not null; cur = System.Windows.Media.VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur))
            if (cur == parent) return true;
        return false;
    }

    private static string Flat(string s) => s.Replace("\r", " ").Replace("\n", " ");
    private static bool Same(string a, string b) => string.Equals(Flat(a).Replace(",", "").Trim(), Flat(b).Replace(",", "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Photon (OpenStreetMap): enkel adressen en straten, in België/Nederland.</summary>
    private static async Task<List<Suggestion>> SearchAsync(string query, CancellationToken ct)
    {
        var url = $"https://photon.komoot.io/api/?q={Uri.EscapeDataString(query)}&limit=8&bbox={Bbox}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        var result = new List<Suggestion>();
        foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var p = f.GetProperty("properties");
            string? Get(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var country = Get("countrycode");
            if (country is not ("BE" or "NL" or "LU" or "DE" or "FR")) continue;
            var street = Get("street") ?? (Get("type") == "street" ? Get("name") : null);
            var city = Get("city") ?? Get("town") ?? Get("village") ?? Get("district") ?? Get("county");
            if (street is null || city is null) continue;
            var line1 = string.Join(" ", new[] { street, Get("housenumber") }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var line2 = string.Join(" ", new[] { Get("postcode"), city }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var value = country == "BE" ? $"{line1}\n{line2}" : $"{line1}\n{line2}\n{Get("country")}";
            if (!result.Any(r => r.Value == value)) result.Add(new Suggestion(value, country == "BE" ? null : Get("country")));
        }
        return result.Take(6).ToList();
    }

    private sealed record Suggestion(string Value, string? Note)
    {
        public override string ToString() => Value.Replace("\r", "").Replace("\n", ", ") + (Note is null ? "" : $"   ({Note})");
    }
}
