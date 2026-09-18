using AnswerSheet.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAPS2.Images.Gdi;
using NAPS2.Scan;
using System.Globalization;
using System.Collections.ObjectModel;
using Windows.Storage.Pickers;
using Windows.Storage;

namespace AnswerSheet.Desktop;

/// <summary>
/// Local M1 capture window. A caller may provide a generated layout so the image
/// can be associated with the same template that was previewed elsewhere.
/// </summary>
public sealed partial class CaptureWindow : Window
{
    private readonly CaptureStore _captureStore;
    private readonly ScanningContext _scanningContext;
    private readonly ScanController _scanController;
    private readonly ObservableCollection<CaptureScannerDeviceItem> _scannerDevices = [];
    private AnswerSheetLayout? _layout;
    private bool _templateMatchesInputs;
    private CancellationTokenSource? _importCancellation;
    private CancellationTokenSource? _scanCancellation;
    private bool _enumerationInProgress;
    private bool _scanInProgress;
    private bool _scanningContextDisposed;
    private bool _isClosed;

    public CaptureWindow()
        : this(null, null)
    {
    }

    public CaptureWindow(AnswerSheetLayout layout)
        : this(layout, null)
    {
    }

    public CaptureWindow(AnswerSheetLayout? layout, CaptureStore? captureStore)
    {
        InitializeComponent();
        _captureStore = captureStore ?? new CaptureStore();
        _scanningContext = new ScanningContext(new GdiImageContext());
        _scanningContext.SetUpWin32Worker();
        _scanController = new ScanController(_scanningContext);
        ScannerList.ItemsSource = _scannerDevices;
        Closed += CaptureWindow_Closed;

        if (layout is null)
        {
            UpdateImportButtonState();
            return;
        }

        TitleBox.Text = layout.Title;
        QuestionCountBox.Value = layout.QuestionCount;
        OptionsPerQuestionBox.Value = layout.OptionsPerQuestion;
        SetLayout(layout);
    }

    private void GenerateTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        GenerateLayout();
    }

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        MarkTemplateOutdated();
    }

    private void NumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        MarkTemplateOutdated();
    }

    private void GenerateLayout()
    {
        GenerateTemplateButton.IsEnabled = false;
        CaptureStatusText.Text = "正在确认模板参数。";

        try
        {
            var questionCount = ReadInteger(QuestionCountBox, "题数");
            var optionsPerQuestion = ReadInteger(OptionsPerQuestionBox, "每题选项数");
            var layout = AnswerSheetLayout.Create(TitleBox.Text, questionCount, optionsPerQuestion);
            SetLayout(layout);
            CaptureStatusText.Text = $"模板参数已确认：{layout.QuestionCount} 题、每题 {layout.OptionsPerQuestion} 个选项。";
        }
        catch (Exception exception)
        {
            _layout = null;
            _templateMatchesInputs = false;
            TemplateIdText.Text = "模板参数无效。";
            CaptureStatusText.Text = FormatFailure("模板参数确认失败", exception, "请检查标题、题数和选项数后重试。");
            UpdateImportButtonState();
        }
        finally
        {
            GenerateTemplateButton.IsEnabled = !_isClosed && _importCancellation is null;
        }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_layout is null || !_templateMatchesInputs || _importCancellation is not null)
        {
            CaptureStatusText.Text = "请先确认有效的模板参数。";
            UpdateImportButtonState();
            return;
        }

        var layout = _layout;
        using var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation;
        SetBusy(true);

        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this));

            CaptureStatusText.Text = "请选择要导入的 PNG 或 JPEG 图像。";
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                CaptureStatusText.Text = "已取消图像导入。";
                return;
            }

            CaptureStatusText.Text = "正在验证图像并保存原图。";
            var capture = await _captureStore.ImportAsync(
                layout,
                file,
                CaptureSourceType.Import,
                cancellation.Token);
            CaptureStatusText.Text =
                $"图像导入成功：{capture.Manifest.PixelWidth}×{capture.Manifest.PixelHeight}，"
                + $"已关联模板 {capture.Manifest.TemplateId}。";
        }
        catch (OperationCanceledException)
        {
            if (!_isClosed)
            {
                CaptureStatusText.Text = "图像导入已取消，未留下采集记录。";
            }
        }
        catch (CaptureException exception)
        {
            CaptureStatusText.Text =
                $"图像导入失败：{exception.Message} 错误代码：{exception.Code}。请重新选择后重试。";
        }
        catch (Exception exception)
        {
            CaptureStatusText.Text = FormatFailure(
                "图像导入失败",
                exception,
                "请重新选择文件后重试。");
        }
        finally
        {
            _importCancellation = null;
            SetBusy(false);
            if (_isClosed && !_enumerationInProgress && !_scanInProgress)
            {
                DisposeScanningContext();
            }
        }
    }

    private void CancelImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_importCancellation is null)
        {
            return;
        }

        CaptureStatusText.Text = "正在取消图像导入。";
        _importCancellation.Cancel();
    }

    private async void RefreshScannerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_enumerationInProgress || _scanInProgress || _importCancellation is not null)
        {
            return;
        }

        _enumerationInProgress = true;
        _scannerDevices.Clear();
        ScannerList.SelectedItem = null;
        RefreshScannerButton.IsEnabled = false;
        ScanCaptureButton.IsEnabled = false;
        CaptureStatusText.Text = "正在刷新 WIA 与 TWAIN 扫描设备。";
        var failures = new List<string>();

        try
        {
            await EnumerateDriverAsync(Driver.Wia, failures);
            await EnumerateDriverAsync(Driver.Twain, failures);

            if (!_isClosed)
            {
                var result = _scannerDevices.Count == 0
                    ? "未发现 WIA 或 TWAIN 扫描设备，请连接设备并重试。"
                    : $"已发现 {_scannerDevices.Count} 个扫描设备入口。";
                CaptureStatusText.Text = failures.Count == 0
                    ? result
                    : $"{result} 枚举异常：{string.Join("；", failures)}。请检查相应驱动后重试。";
            }
        }
        finally
        {
            _enumerationInProgress = false;
            if (_isClosed)
            {
                if (!_scanInProgress)
                {
                    DisposeScanningContext();
                }
            }
            else
            {
                RefreshScannerButton.IsEnabled = true;
                UpdateScanCaptureButtonState();
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
                foreach (var device in devices)
                {
                    _scannerDevices.Add(new CaptureScannerDeviceItem(device));
                }
            }
        }
        catch (Exception exception)
        {
            failures.Add($"{driver}：{exception.GetType().Name}（0x{exception.HResult:X8}）");
        }
    }

    private void ScannerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateScanCaptureButtonState();
    }

    private async void ScanCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_layout is null
            || !_templateMatchesInputs
            || _scanInProgress
            || _enumerationInProgress
            || _importCancellation is not null
            || ScannerList.SelectedItem is not CaptureScannerDeviceItem selectedDevice)
        {
            CaptureStatusText.Text = "请先确认模板参数并选择扫描设备。";
            UpdateScanCaptureButtonState();
            return;
        }

        var layout = _layout;
        int dpi;
        try
        {
            dpi = ReadSelectedResolution();
        }
        catch (Exception exception)
        {
            CaptureStatusText.Text = FormatFailure(
                "扫描参数无效",
                exception,
                "请选择 150、300 或 600 DPI 后重试。");
            return;
        }
        _scanInProgress = true;
        _scanCancellation = new CancellationTokenSource();
        SetScanBusy(true);
        SetBusy(true);
        CaptureStatusText.Text =
            $"正在使用 {selectedDevice.Name} 扫描首张答题纸（平板、A4、{dpi} DPI）。";

        string? temporaryPath = null;
        try
        {
            temporaryPath = await CaptureScanService.ScanFirstPageAsync(
                _scanController,
                selectedDevice.Device,
                dpi,
                _captureStore.RootDirectory,
                _scanCancellation.Token);

            var scanFile = await StorageFile.GetFileFromPathAsync(temporaryPath);
            var capture = await _captureStore.ImportAsync(
                layout,
                scanFile,
                CaptureSourceType.Scan,
                _scanCancellation.Token);
            CaptureStatusText.Text =
                $"扫描并保存成功：{capture.Manifest.PixelWidth}×{capture.Manifest.PixelHeight}，"
                + $"已关联模板 {capture.Manifest.TemplateId}。";
        }
        catch (OperationCanceledException)
        {
            if (!_isClosed)
            {
                CaptureStatusText.Text = "扫描或保存已取消，未留下采集记录。";
            }
        }
        catch (CaptureException exception)
        {
            CaptureStatusText.Text =
                $"扫描保存失败：{exception.Message} 错误代码：{exception.Code}。请重试。";
        }
        catch (Exception exception)
        {
            CaptureStatusText.Text = FormatFailure(
                "扫描保存失败",
                exception,
                "请检查设备、测试纸和分辨率后重试。");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                CaptureScanService.DeleteTemporaryFile(temporaryPath);
            }

            _scanCancellation?.Dispose();
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
                SetBusy(false);
                SetScanBusy(false);
            }
        }
    }

    private void CancelScanCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is null)
        {
            return;
        }

        CaptureStatusText.Text = "正在取消扫描和保存。";
        _scanCancellation.Cancel();
    }

    private void CaptureWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        _importCancellation?.Cancel();
        _scanCancellation?.Cancel();
        if (!_enumerationInProgress && !_scanInProgress)
        {
            DisposeScanningContext();
        }
    }

    private void SetLayout(AnswerSheetLayout layout)
    {
        _layout = layout;
        _templateMatchesInputs = true;
        var template = CaptureTemplateReference.FromLayout(layout);
        TemplateIdText.Text = $"模板 ID：{template.TemplateId}";
        UpdateImportButtonState();
    }

    private void MarkTemplateOutdated()
    {
        if (_layout is null || !_templateMatchesInputs)
        {
            return;
        }

        _layout = null;
        _templateMatchesInputs = false;
        TemplateIdText.Text = "参数已变更，请重新确认模板参数。";
        CaptureStatusText.Text = "参数已变更，请重新确认模板参数。";
        UpdateImportButtonState();
    }

    private void SetBusy(bool busy)
    {
        GenerateTemplateButton.IsEnabled = !busy && !_isClosed;
        TitleBox.IsEnabled = !busy && !_isClosed;
        QuestionCountBox.IsEnabled = !busy && !_isClosed;
        OptionsPerQuestionBox.IsEnabled = !busy && !_isClosed;
        ImportButton.IsEnabled = !busy && !_isClosed && _layout is not null && _templateMatchesInputs;
        CancelImportButton.IsEnabled = busy && !_isClosed;
        RefreshScannerButton.IsEnabled = !busy && !_isClosed && !_enumerationInProgress && !_scanInProgress;
        ResolutionComboBox.IsEnabled = !busy && !_isClosed && !_scanInProgress;
        UpdateScanCaptureButtonState();
    }

    private void UpdateImportButtonState()
    {
        if (_importCancellation is not null)
        {
            return;
        }

        ImportButton.IsEnabled = !_isClosed && _layout is not null && _templateMatchesInputs;
    }

    private static int ReadInteger(NumberBox numberBox, string name)
    {
        if (double.IsNaN(numberBox.Value)
            || double.IsInfinity(numberBox.Value)
            || numberBox.Value != Math.Truncate(numberBox.Value))
        {
            throw new ArgumentException($"{name}必须是整数。", name);
        }

        return checked((int)numberBox.Value);
    }

    private int ReadSelectedResolution()
    {
        if (ResolutionComboBox.SelectedItem is ComboBoxItem { Tag: string tag }
            && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dpi)
            && dpi is 150 or 300 or 600)
        {
            return dpi;
        }

        throw new InvalidOperationException("请选择 150、300 或 600 DPI。");
    }

    private void SetScanBusy(bool busy)
    {
        RefreshScannerButton.IsEnabled = !busy && !_isClosed && !_enumerationInProgress && _importCancellation is null;
        ResolutionComboBox.IsEnabled = !busy && !_isClosed && _importCancellation is null;
        ScannerList.IsEnabled = !busy && !_isClosed && _importCancellation is null;
        CancelScanCaptureButton.IsEnabled = busy && !_isClosed;
        UpdateScanCaptureButtonState();
    }

    private void UpdateScanCaptureButtonState()
    {
        ScanCaptureButton.IsEnabled = !_isClosed
            && !_enumerationInProgress
            && !_scanInProgress
            && _importCancellation is null
            && _layout is not null
            && _templateMatchesInputs
            && ScannerList.SelectedItem is CaptureScannerDeviceItem;
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

    private static string FormatFailure(string operation, Exception exception, string nextStep)
    {
        var rootCause = exception.GetBaseException();
        return $"{operation}：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。{nextStep}";
    }
}

public sealed record CaptureScannerDeviceItem(ScanDevice Device)
{
    public string Driver => Device.Driver.ToString().ToUpperInvariant();

    public string Name => Device.Name;
}
