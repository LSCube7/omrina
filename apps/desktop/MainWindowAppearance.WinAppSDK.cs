using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Media;

namespace Omrina.Desktop;

public sealed partial class MainWindow
{
    private void ConfigureSystemBackdrop()
    {
        if (!MicaController.IsSupported())
        {
            return;
        }

        SystemBackdrop = new MicaBackdrop();
        RootLayout.Background = new SolidColorBrush(Colors.Transparent);
        AppTitleBar.Background = new SolidColorBrush(Colors.Transparent);
    }
}
