using Microsoft.UI.Xaml.Controls;

namespace Omrina.Desktop;

public sealed partial class StatusPage : Page
{
    public StatusPage()
    {
        InitializeComponent();
    }

    public void SetConnectionStatus(string message)
    {
        ConnectionStatusText.Text = message;
    }
}
