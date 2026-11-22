using System.IO;
using System.Text.Json;

namespace Boekhouding.Client.Services;

/// <summary>Instellingen per pc, in %AppData%\ShipRipperBoekhouding\client.json.</summary>
public class ClientSettings
{
    public string ServerUrl { get; set; } = "http://localhost:5080";
    public string ApiKey { get; set; } = "verander-mij-naar-iets-geheims";

    // ---- Rip-tool (localhost-pagina in het stream-dashboard) ----
    /// <summary>Leeg = de rip-wachtrij op deze pc (<see cref="DefaultRipToolUrl"/>).</summary>
    public string RipToolUrl { get; set; } = "";
    public const string DefaultRipToolUrl = "http://localhost:3000/dashboard.html";
    /// <summary>Tekst op de knop waarmee een rip afgerond wordt (hoofdletters maken niet uit).</summary>
    public string DoneButtonText { get; set; } = "done rippen";
    /// <summary>Optioneel: CSS-selector van de knop, als de tekst niet volstaat (bv. "button.done-rip").</summary>
    public string DoneButtonSelector { get; set; } = "";
    /// <summary>CSS-selector van het blok rond één klant/rip (bv. ".card"). Leeg = hele pagina.</summary>
    public string ContainerSelector { get; set; } = ".card";
    /// <summary>CSS-selector van de klantnaam binnen dat blok (bv. ".customer-name").</summary>
    public string NameSelector { get; set; } = ".customer-name";
    public bool AutoPrintOnDone { get; set; } = true;
    /// <summary>Deze pc print de labels die de rip-wachtrij doorstuurt (zet dit op één pc aan: die met de labelprinter).</summary>
    public bool PrintQueueLabels { get; set; }

    // ---- Labelprinter ----
    /// <summary>Windows-printernaam, bv. "Brother QL-820NWB". Leeg = standaardprinter.</summary>
    public string PrinterName { get; set; } = "";
    /// <summary>Optioneel: P-touch Editor-sjabloon (.lbx). Dan wordt via Brother b-PAC geprint.</summary>
    public string LabelTemplatePath { get; set; } = "";
    /// <summary>Objectnamen in het .lbx-sjabloon.</summary>
    public string TemplateNameObject { get; set; } = "Naam";
    public string TemplateInfoObject { get; set; } = "Info";
    public double LabelWidthMm { get; set; } = 62;
    public double LabelHeightMm { get; set; } = 29;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShipRipperBoekhouding", "client.json");

    public static ClientSettings Load()
    {
        var settings = new ClientSettings();
        try
        {
            if (File.Exists(FilePath))
                settings = JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(FilePath)) ?? settings;
        }
        catch (JsonException) { }

        // Voor testen naast een draaiende installatie: andere server via omgevingsvariabelen (wordt niet opgeslagen).
        if (Environment.GetEnvironmentVariable("SHIPRIPPER_SERVER_URL") is { Length: > 0 } url) settings.ServerUrl = url;
        if (Environment.GetEnvironmentVariable("SHIPRIPPER_API_KEY") is { Length: > 0 } key) settings.ApiKey = key;
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
