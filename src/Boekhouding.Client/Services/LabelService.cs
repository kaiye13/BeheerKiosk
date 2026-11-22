using System.Windows.Threading;
using Boekhouding.Shared;

namespace Boekhouding.Client.Services;

/// <summary>
/// Labels printen voor een klant: naam opzoeken bij de orders van de stream, label printen, orders op "label geprint" zetten.
/// Wordt gebruikt door de rip-tool in het stream-dashboard én door de achtergrondtaak voor de rip-wachtrij.
/// </summary>
public static class LabelService
{
    /// <summary>Meldingen voor de statusbalk van het stream-dashboard (tekst, is fout).</summary>
    public static event Action<string, bool>? StatusChanged;

    public static void Report(string text, bool error) => StatusChanged?.Invoke(text, error);

    /// <summary>Print het label; geeft een korte omschrijving terug. Gooit een fout als printen mislukt.</summary>
    /// <param name="reference">Bestelling uit de rip-wachtrij ("shopify:…"/"tiktok:…"): verzending/spaarder komt dan
    /// uit die bestelling zelf, en de naam op het label blijft zoals de wachtrij hem gaf (voornaam).</param>
    public static async Task<string> PrintForNameAsync(string name, string? source = null, string? reference = null)
    {
        var info = await ApiClient.Current.GetLabelInfoAsync(name, reference);
        var label = new LabelContent(
            reference is not null ? name : info.CustomerName ?? info.Name,
            info.Matched
                ? $"{info.Orders} order{(info.Orders == 1 ? "" : "s")} · {info.Items} art. · {info.OrderNumbers}"
                : $"{DateTime.Now:dd/MM HH:mm} · geen order gevonden",
            info.Kind switch
            {
                ShippingKind.Paid => "VERZENDING BETAALD",
                ShippingKind.Saver => "SPAARDER" + (info.OpenSaverOrders > 1 ? $" · {info.OpenSaverOrders} open orders" : ""),
                ShippingKind.Free => "GRATIS VERZENDING",
                _ => "CONTROLEER VERZENDING",
            });
        var method = LabelPrinter.Print(label, ApiClient.Current.Settings);
        if (info.Matched) await ApiClient.Current.MarkLabelPrintedAsync(info.OrderIds);

        var text = $"🖨 {DateTime.Now:HH:mm:ss} label geprint voor {label.Name} ({label.Shipping.ToLowerInvariant()}) via {method}"
                   + (source is null ? "" : $" · uit {source}")
                   + (info.Matched ? "" : " · ⚠ niet gekoppeld aan een order");
        Report(text, error: !info.Matched);
        return text;
    }
}

/// <summary>
/// Achtergrondtaak (enkel op de pc die "labels van de rip-wachtrij printen" aan heeft):
/// haalt elke 2 s nieuwe printopdrachten op bij de server, claimt ze (zodat geen andere pc dubbel print) en print.
/// </summary>
public class LabelQueueWorker
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _busy;

    public LabelQueueWorker() => _timer.Tick += async (_, _) => await TickAsync();

    public void Start() => _timer.Start();

    /// <summary>
    /// Naam van "Nu rippen" als deze pc niet automatisch print: het streamscherm zet ze klaar
    /// in het vakje naast "Print label". Enkel opgehaald zolang het streamscherm open is.
    /// </summary>
    public static event Action<string, string>? NameArrived;   // (naam, bestelling-referentie)

    private async Task TickAsync()
    {
        var autoPrint = ApiClient.Current.Settings.PrintQueueLabels;
        if (_busy || (!autoPrint && NameArrived is null)) return;
        _busy = true;
        try
        {
            foreach (var job in await ApiClient.Current.GetPendingLabelJobsAsync())
            {
                if (!await ApiClient.Current.ClaimLabelJobAsync(job.Id, Environment.MachineName)) continue;
                if (!autoPrint)
                {
                    NameArrived?.Invoke(job.Name, job.Reference);
                    await ApiClient.Current.CompleteLabelJobAsync(job.Id, null);
                    continue;
                }
                try
                {
                    await LabelService.PrintForNameAsync(job.Name, job.Source == "test" ? "test" : $"wachtrij ({job.Source})",
                        job.Source is "shopify" or "tiktok" ? job.Reference : null);
                    await ApiClient.Current.CompleteLabelJobAsync(job.Id, null);
                }
                catch (Exception ex)
                {
                    LabelService.Report($"Label voor {job.Name} (wachtrij) niet geprint: {ex.Message}", error: true);
                    await ApiClient.Current.CompleteLabelJobAsync(job.Id, ex.Message);
                }
            }
        }
        catch (ApiException)
        {
            // Server even niet bereikbaar: volgende tik opnieuw.
        }
        finally
        {
            _busy = false;
        }
    }
}
