using Microsoft.UI.Xaml;

namespace Omrina.Desktop;

public partial class App : Application
{
    private MainWindow? _window;
    private bool _healthServerStarted;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window ??= new MainWindow();
        _window.Activate();
        if (!_healthServerStarted)
        {
            _healthServerStarted = true;
            _ = _window.StartHealthServerAsync();
        }
    }
}
