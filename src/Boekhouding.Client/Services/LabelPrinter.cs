using System.Printing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Boekhouding.Client.Services;

public record LabelContent(string Name, string Info, string Shipping);

/// <summary>
/// Print een verzendlabel op de Brother-printer.
/// 1) Met een P-touch Editor-sjabloon (.lbx): via Brother b-PAC (COM) — sjabloon bepaalt de opmaak.
/// 2) Zonder sjabloon: als gewone Windows-printopdracht op het labelformaat (werkt met elke Brother QL/PT-driver).
/// </summary>
public static class LabelPrinter
{
    /// <summary>
    /// Alle printers zoals Windows ze toont (zelfde bron als de printvensters: EnumPrinters).
    /// De WPF-opsomming (LocalPrintServer) wordt enkel als aanvulling gebruikt: die faalt volledig
    /// zodra één printer op de pc een probleem heeft (bv. een virtuele of half verwijderde printer).
    /// </summary>
    public static List<string> InstalledPrinters()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var n in EnumPrinterNames()) names.Add(n); }
        catch (Exception) { /* val terug op de WPF-opsomming */ }
        try
        {
            using var server = new LocalPrintServer();
            foreach (var q in server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]))
                names.Add(q.FullName);
        }
        catch (Exception) { /* sommige printers kunnen niet opgesomd worden: negeren */ }
        return names.ToList();
    }

    // ---- Win32 EnumPrinters (PRINTER_INFO_4: naam + server) ----
    private const int PRINTER_ENUM_LOCAL = 0x2, PRINTER_ENUM_CONNECTIONS = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PRINTER_INFO_4
    {
        public string pPrinterName;
        public string? pServerName;
        public uint Attributes;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumPrinters(int flags, string? name, int level, IntPtr buffer, int bufferSize, out int needed, out int returned);

    private static List<string> EnumPrinterNames()
    {
        const int flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;
        EnumPrinters(flags, null, 4, IntPtr.Zero, 0, out var needed, out _);
        if (needed <= 0) return [];
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!EnumPrinters(flags, null, 4, buffer, needed, out _, out var count)) return [];
            var size = Marshal.SizeOf<PRINTER_INFO_4>();
            return Enumerable.Range(0, count)
                .Select(i => Marshal.PtrToStructure<PRINTER_INFO_4>(buffer + i * size).pPrinterName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Printer openen op naam, ook gedeelde netwerkprinters (\\pc\printer).</summary>
    private static PrintQueue OpenQueue(string name)
    {
        if (name.StartsWith(@"\\"))
        {
            var slash = name.IndexOf('\\', 2);
            if (slash > 2) return new PrintQueue(new PrintServer(name[..slash]), name[(slash + 1)..]);
        }
        return new LocalPrintServer().GetPrintQueue(name);
    }

    /// <summary>Geeft terug welke methode gebruikt werd ("b-PAC" of "Windows").</summary>
    public static string Print(LabelContent label, ClientSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.LabelTemplatePath))
        {
            PrintWithBpac(label, s);
            return "b-PAC";
        }
        PrintWithWindows(label, s);
        return "Windows";
    }

    private static void PrintWithBpac(LabelContent label, ClientSettings s)
    {
        // b-PAC Client Component (64-bit) moet geïnstalleerd zijn: https://support.brother.com → b-PAC.
        var type = Type.GetTypeFromProgID("bpac.Document")
                   ?? throw new InvalidOperationException("Brother b-PAC is niet geïnstalleerd (64-bit Client Component nodig), of maak het sjabloonveld leeg om via Windows te printen.");
        if (!System.IO.File.Exists(s.LabelTemplatePath))
            throw new InvalidOperationException($"Sjabloon niet gevonden: {s.LabelTemplatePath}");

        dynamic doc = Activator.CreateInstance(type)!;
        try
        {
            if (!(bool)doc.Open(s.LabelTemplatePath)) throw new InvalidOperationException("Sjabloon kon niet geopend worden door b-PAC.");
            if (!string.IsNullOrWhiteSpace(s.PrinterName)) doc.SetPrinter(s.PrinterName, true);
            SetText(doc, s.TemplateNameObject, label.Name);
            SetText(doc, s.TemplateInfoObject, $"{label.Info}\n{label.Shipping}");
            doc.StartPrint("", 0);
            doc.PrintOut(1, 0);
            doc.EndPrint();
            doc.Close();
        }
        finally
        {
            Marshal.FinalReleaseComObject(doc);
        }
    }

    private static void SetText(dynamic doc, string objectName, string text)
    {
        if (string.IsNullOrWhiteSpace(objectName)) return;
        dynamic? obj = doc.GetObject(objectName);
        if (obj is not null) obj.Text = text;
    }

    private static void PrintWithWindows(LabelContent label, ClientSettings s)
    {
        var queue = FindQueue(s.PrinterName);
        var ticket = queue.DefaultPrintTicket.Clone();

        // Label liggend opgemaakt: breedte = lange zijde (62 mm), hoogte = korte zijde (29 mm).
        var w = MmToDip(Math.Max(s.LabelWidthMm, s.LabelHeightMm));
        var h = MmToDip(Math.Min(s.LabelWidthMm, s.LabelHeightMm));

        // Bij voorkeur het formaat dat de driver zelf aanbiedt (Brother: "29mm x 62mm" / DK-11209),
        // anders past de driver de pagina aan en wordt het label verschoven of verkleind.
        var media = DriverMediaSize(queue, s.LabelWidthMm, s.LabelHeightMm) ?? new PageMediaSize(w, h);
        ticket.PageMediaSize = media;
        // Is het driverformaat staand (smaller dan hoog), dan liggend printen zodat 62 mm de breedte is.
        ticket.PageOrientation = media.Width < media.Height ? PageOrientation.Landscape : PageOrientation.Portrait;

        var dlg = new PrintDialog { PrintQueue = queue, PrintTicket = ticket };
        var visual = BuildVisual(label, w, h);
        dlg.PrintVisual(visual, $"Label {label.Name}");
    }

    /// <summary>Formaat uit de driver dat (in eender welke richting) overeenkomt met het label, op 2 mm na.</summary>
    private static PageMediaSize? DriverMediaSize(PrintQueue queue, double widthMm, double heightMm)
    {
        try
        {
            var (a, b) = (Math.Max(widthMm, heightMm), Math.Min(widthMm, heightMm));
            return queue.GetPrintCapabilities().PageMediaSizeCapability
                .Where(m => m.Width is > 0 && m.Height is > 0)
                .FirstOrDefault(m =>
                {
                    var (mw, mh) = (m.Width!.Value * 25.4 / 96, m.Height!.Value * 25.4 / 96);
                    return Math.Abs(Math.Max(mw, mh) - a) <= 2 && Math.Abs(Math.Min(mw, mh) - b) <= 2;
                });
        }
        catch (PrintQueueException)
        {
            return null;
        }
    }

    /// <summary>
    /// Ingestelde printer, of (als er niets ingesteld is) automatisch een Brother/QL-labelprinter.
    /// Nooit stilzwijgend de standaardprinter: dat is vaak een kantoorprinter.
    /// </summary>
    private static PrintQueue FindQueue(string name)
    {
        var printers = InstalledPrinters();
        var seen = printers.Count == 0 ? "geen enkele printer" : string.Join(", ", printers);

        var wanted = !string.IsNullOrWhiteSpace(name)
            ? printers.FirstOrDefault(p => p.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) ?? name.Trim()
            : printers.FirstOrDefault(LooksLikeLabelPrinter)
              ?? throw new InvalidOperationException("Geen Brother-labelprinter gevonden. Installeer de driver van de QL-800 of kies de printer " +
                                                     $"in Instellingen → Labelprinter. De app ziet: {seen}.");
        try
        {
            return OpenQueue(wanted);
        }
        catch (Exception ex) when (ex is PrintSystemException or PrintQueueException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Printer '{wanted}' kan niet geopend worden ({ex.Message}). " +
                                                $"Kies de printer opnieuw in Instellingen → Labelprinter. De app ziet: {seen}.");
        }
    }

    public static bool LooksLikeLabelPrinter(string? name) =>
        name is not null && (name.Contains("Brother", StringComparison.OrdinalIgnoreCase)
                             || System.Text.RegularExpressions.Regex.IsMatch(name, @"\bQL-?\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    private static double MmToDip(double mm) => mm / 25.4 * 96;

    /// <summary>Opmaak zonder sjabloon: naam groot, daaronder orders en verzendstatus.</summary>
    public static FrameworkElement BuildVisual(LabelContent label, double w, double h)
    {
        var margin = MmToDip(1.5);
        var panel = new Grid { Width = w, Height = h, Background = Brushes.White };
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.6, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.8, GridUnitType.Star) });
        // Witruimte onderaan: de onderrand van een voorgesneden Brother-etiket wordt niet bedrukt,
        // anders valt de zwarte verzendbalk er half af.
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(MmToDip(3)) });

        UIElement Line(string text, FontWeight weight, int row, bool inverse = false)
        {
            var tb = new TextBlock { Text = text, FontFamily = new FontFamily("Segoe UI"), FontWeight = weight, Foreground = inverse ? Brushes.White : Brushes.Black };
            var box = new Viewbox { Child = tb, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left };
            tb.FontSize = row == 0 ? 40 : 20;
            // In de zwarte balk wat ruimte boven en onder de tekst, zodat letters niet tegen de rand plakken.
            var vertical = inverse ? MmToDip(0.6) : 0;
            var border = new Border { Child = box, Padding = new Thickness(margin, vertical, margin, vertical), Background = inverse ? Brushes.Black : Brushes.Transparent };
            Grid.SetRow(border, row);
            return border;
        }

        panel.Children.Add(Line(label.Name, FontWeights.Bold, 0));
        panel.Children.Add(Line(label.Info, FontWeights.Normal, 1));
        panel.Children.Add(Line(label.Shipping, FontWeights.Bold, 2, inverse: true));
        panel.Measure(new Size(w, h));
        panel.Arrange(new Rect(0, 0, w, h));
        panel.UpdateLayout();
        return panel;
    }
}
