using Microsoft.UI.Xaml;
using NAPS2.Images.Gdi;
using NAPS2.Scan;
using System.Collections.ObjectModel;

namespace AnswerSheet.Desktop;

public sealed partial class MainWindow : Window
{
    private const double WideLayoutBreakpoint = 720;
    private readonly LoopbackHealthServer _healthServer = new();
    private readonly ScanningContext _scanningContext;
    private readonly ScanController _scanController;
    private readonly ObservableCollection<ScannerDeviceItem> _devices = [];
    private TemplateWindow? _templateWindow;
    private bool _enumerationInProgress;
    private bool _scanInProgress;
    private bool _isClosed;
    private bool _scanningContextDisposed;
    private CancellationTokenSource? _scanCancellation;

    public MainWindow()
    {
        InitializeComponent();

        _scanningContext = new ScanningContext(new GdiImageContext());
        _scanningContext.SetUpWin32Worker();
        _scanController = new ScanController(_scanningContext);

        DeviceList.ItemsSource = _devices;
        Closed += MainWindow_Closed;
    }

    private void RootLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var useWideLayout = e.NewSize.Width >= WideLayoutBreakpoint;
        DeviceActions.Orientation = useWideLayout
            ? Microsoft.UI.Xaml.Controls.Orientation.Horizontal
            : Microsoft.UI.Xaml.Controls.Orientation.Vertical;
        Microsoft.UI.Xaml.Controls.Grid.SetRow(DeviceActions, useWideLayout ? 0 : 1);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(DeviceActions, useWideLayout ? 1 : 0);
        Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(DeviceActions, useWideLayout ? 1 : 2);
        Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(DeviceHeading, useWideLayout ? 1 : 2);
        DeviceActions.HorizontalAlignment = useWideLayout
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Stretch;
    }

    private void TemplateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_templateWindow is not null)
        {
            _templateWindow.Activate();
            return;
        }

        _templateWindow = new TemplateWindow();
        _templateWindow.Closed += (_, _) => _templateWindow = null;
        _templateWindow.Activate();
    }

    public async Task StartHealthServerAsync()
    {
        try
        {
            await _healthServer.StartAsync();
            if (!_isClosed)
            {
                ConnectionStatusText.Text = $"本地连接服务已就绪： http://127.0.0.1:{LoopbackHealthServer.Port}/health";
            }
        }
        catch (Exception exception)
        {
            if (!_isClosed)
            {
                var rootCause = exception.GetBaseException();
                ConnectionStatusText.Text = $"本地连接服务启动失败：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。设备枚举仍可使用。";
            }
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        LoadingRing.Visibility = Visibility.Visible;
        LoadingRing.IsActive = true;
        _devices.Clear();
        DeviceList.SelectedItem = null;
        ScanButton.IsEnabled = false;
        _enumerationInProgress = true;
        var failures = new List<string>();

        try
        {
            await EnumerateDriverAsync(Driver.Wia, failures);
            await EnumerateDriverAsync(Driver.Twain, failures);

            if (!_isClosed)
            {
                var result = _devices.Count == 0
                    ? "未发现 WIA 或 TWAIN 扫描设备。请确认设备已连接并安装驱动后重试。"
                    : $"已发现 {_devices.Count} 个设备入口。";
                DeviceStatusText.Text = failures.Count == 0
                    ? result
                    : $"{result} 枚举异常：{string.Join("；", failures)}。请检查相应驱动后重试。";
            }
        }
        finally
        {
            _enumerationInProgress = false;
            if (_isClosed)
            {
                DisposeScanningContext();
            }
            else
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                RefreshButton.IsEnabled = true;
                UpdateScanButtonState();
            }
        }
    }

    private async Task EnumerateDriverAsync(Driver driver, ICollection<string> failures)
    {
        if (_isClosed)
        {
            return;
        }

        try
        {
            var devices = await _scanController.GetDeviceList(driver);
            if (!_isClosed)
            {
                AddDevices(devices);
            }
        }
        catch (Exception exception)
        {
            failures.Add($"{driver}：{exception.GetType().Name}（0x{exception.HResult:X8}）");
        }
    }

    private void AddDevices(IEnumerable<ScanDevice> devices)
    {
        foreach (var device in devices)
        {
            _devices.Add(new ScannerDeviceItem(device));
        }
    }

    private void DeviceList_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        UpdateScanButtonState();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not ScannerDeviceItem selectedDevice || _scanInProgress)
        {
            return;
        }

        _scanInProgress = true;
        _scanCancellation = new CancellationTokenSource();
        RefreshButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        CancelScanButton.IsEnabled = true;
        DeviceStatusText.Text = $"正在使用 {selectedDevice.Name} 扫描测试纸（平板、A4、300 DPI）。";

        try
        {
            var outputPath = await TestScanService.ScanFirstPageAsync(
                _scanController,
                selectedDevice.Device,
                _scanCancellation.Token);
            if (!_isClosed)
            {
                DeviceStatusText.Text = $"测试扫描完成，已保存首张 PNG：{outputPath}";
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isClosed)
            {
                DeviceStatusText.Text = "测试扫描已取消。";
            }
        }
        catch (Exception exception)
        {
            if (!_isClosed)
            {
                var rootCause = exception.GetBaseException();
                DeviceStatusText.Text = $"测试扫描失败：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。请检查设备与测试纸后重试。";
            }
        }
        finally
        {
            _scanCancellation.Dispose();
            _scanCancellation = null;
            _scanInProgress = false;
            if (_isClosed)
            {
                if (!_enumerationInProgress)
                {
                    DisposeScanningContext();
                }
            }
            else
            {
                RefreshButton.IsEnabled = true;
                CancelScanButton.IsEnabled = false;
                UpdateScanButtonState();
            }
        }
    }

    private void CancelScanButton_Click(object sender, RoutedEventArgs e)
    {
        _scanCancellation?.Cancel();
    }

    private void UpdateScanButtonState()
    {
        ScanButton.IsEnabled = !_isClosed
            && !_enumerationInProgress
            && !_scanInProgress
            && DeviceList.SelectedItem is ScannerDeviceItem;
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        _scanCancellation?.Cancel();
        if (!_enumerationInProgress && !_scanInProgress)
        {
            DisposeScanningContext();
        }

        try
        {
            await _healthServer.StopAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"本地连接服务停止失败：{exception.GetType().Name}（0x{exception.HResult:X8}）");
        }
    }

    private void DisposeScanningContext()
    {
        if (_scanningContextDisposed)
        {
            return;
        }

        _scanningContext.Dispose();
        _scanningContextDisposed = true;
    }
}

public sealed record ScannerDeviceItem(ScanDevice Device)
{
    public string Driver => Device.Driver.ToString().ToUpperInvariant();

    public string Name => Device.Name;
}
