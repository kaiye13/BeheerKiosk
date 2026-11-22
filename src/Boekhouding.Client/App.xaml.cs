using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using Boekhouding.Client.Services;

namespace Boekhouding.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Belgische notatie: komma als decimaalteken, € 1.234,56, dd/mm/jjjj.
        var culture = CultureInfo.GetCultureInfo("nl-BE");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        // Ook de UI-thread zelf (die al draait), anders worden bv. dagnamen in het Engels getoond.
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));

        DispatcherUnhandledException += (_, args) =>
        {
            Ui.ShowError(args.Exception);
            args.Handled = true;
        };

        base.OnStartup(e);
        new MainWindow().Show();
    }
}
