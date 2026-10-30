using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace GUI;

public partial class App : Application
{
    static App()
    {
        // WPF normally uses the Windows number format. Force en-US bindings so a decimal point in values such as 1500.5 is interpreted consistently with the protocol.
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(
                XmlLanguage.GetLanguage(CultureInfo.GetCultureInfo("en-US").IetfLanguageTag)));
    }
}
