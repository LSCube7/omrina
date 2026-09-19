using Omrina.Core;
using Omrina.Platform;
using Omrina.Scanning;
using Omrina.Server;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Omrina.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly LoopbackHealthServer _healthServer = new();
    private readonly CaptureStore _captureStore = new();
    private readonly IScannerService _scannerService;
    private readonly IFileDialogService _fileDialogService;
    private readonly TemplatePrintController? _printController;
    private readonly StatusPage _statusPage;
    private readonly TemplatePage _templatePage;
    private readonly CapturePage _capturePage;
    private readonly SettingsPage _settingsPage;
    private readonly AboutPage _aboutPage;
    private bool _isClosed;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _statusPage = new StatusPage();
        _settingsPage = new SettingsPage();
        _aboutPage = new AboutPage();
        _fileDialogService = DesktopPlatformFactory.CreateFileDialogs(this);
        _scannerService = DesktopPlatformFactory.CreateScanner(_captureStore.RootDirectory);
        _printController = CreatePrintController();
        _templatePage = new TemplatePage(
            SaveSvgAsync,
            _printController is null ? null : PrintAsync,
            () => _printController?.IsBusy == true);
        _capturePage = new CapturePage(
            _scannerService,
            PickImageAsync,
            OpenScanImageAsync,
            PersistCaptureAsync,
            DesktopPlatformFactory.DeleteTemporaryScanFile);
        _templatePage.CaptureRequested += TemplatePage_CaptureRequested;

        NavigationFrame.Content = _statusPage;
        AppNavigationView.SelectedItem = AppNavigationView.MenuItems[0];
        Closed += MainWindow_Closed;
    }

    public async Task StartHealthServerAsync()
    {
        try
        {
            await _healthServer.StartAsync();
            if (!_isClosed)
            {
                _statusPage.SetConnectionStatus(
                    $"本地连接服务已就绪： http://127.0.0.1:{LoopbackHealthServer.Port}/health");
            }
        }
        catch (Exception exception)
        {
            if (!_isClosed)
            {
                var rootCause = exception.GetBaseException();
                _statusPage.SetConnectionStatus(
                    $"本地连接服务启动失败：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。模板和采集仍可使用。");
            }
        }
    }

    private void AppNavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        switch (tag)
        {
            case "status":
                NavigateTo(_statusPage, "状态");
                break;
            case "template":
                NavigateTo(_templatePage, "模板");
                break;
            case "capture":
                NavigateTo(_capturePage, "采集");
                break;
            case "settings":
                NavigateTo(_settingsPage, "设置");
                break;
            case "about":
                NavigateTo(_aboutPage, "关于");
                break;
        }
    }

    private void NavigateTo(Page page, string title)
    {
        if (!ReferenceEquals(NavigationFrame.Content, page))
        {
            NavigationFrame.Content = page;
        }

        TitleBarPageText.Text = title;
    }

    private void TemplatePage_CaptureRequested(AnswerSheetLayout layout)
    {
        _capturePage.SetLayout(layout);
        SelectNavigationItem("capture");
    }

    private void SelectNavigationItem(string tag)
    {
        foreach (var item in AppNavigationView.MenuItems.OfType<NavigationViewItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                AppNavigationView.SelectedItem = item;
                return;
            }
        }
    }

    private TemplatePrintController? CreatePrintController()
    {
        try
        {
            return new TemplatePrintController(
                this,
                message => _templatePage?.ReportPlatformStatus(message));
        }
        catch (Exception exception)
        {
            var rootCause = exception.GetBaseException();
            _statusPage?.SetConnectionStatus(
                $"系统打印不可用：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。模板仍可保存和导入。");
            return null;
        }
    }

    private async Task PrintAsync(AnswerSheetLayout layout)
    {
        if (_printController is null)
        {
            throw new InvalidOperationException("系统打印服务不可用。");
        }

        await _printController.RequestPrintAsync(layout);
    }

    private async Task<SvgSaveResult> SaveSvgAsync(AnswerSheetLayout layout)
    {
        var result = await _fileDialogService.SaveTextAsync(
            "omrina-template",
            ".svg",
            layout.ToSvg());
        return new SvgSaveResult(result.Cancelled, result.Path);
    }

    private async Task<IInputImageFile?> PickImageAsync(CancellationToken cancellationToken)
    {
        return await _fileDialogService.PickImageAsync(cancellationToken);
    }

    private static Task<IInputImageFile> OpenScanImageAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IInputImageFile>(new LocalInputImageFile(path));
    }

    private Task<CaptureRecord> PersistCaptureAsync(
        AnswerSheetLayout layout,
        IInputImageFile file,
        CaptureSourceType sourceType,
        CancellationToken cancellationToken)
    {
        return _captureStore.ImportAsync(layout, file, sourceType, cancellationToken);
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        try
        {
            await _capturePage.CancelAndWaitAsync();
            if (_printController is not null)
            {
                await _printController.WaitForIdleAsync();
            }
            await _healthServer.StopAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"OMRINA 主窗口关闭清理失败：{exception.GetType().Name}（0x{exception.HResult:X8}）。");
        }
        finally
        {
            _printController?.Dispose();
            await _scannerService.DisposeAsync();
        }
    }
}
