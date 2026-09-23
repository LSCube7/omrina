using Omrina.Core;
using Omrina.Scanning;
using Omrina.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Omrina.Desktop;

/// <summary>
/// Cached capture page hosted by <see cref="MainWindow"/>.
/// Native file selection, temporary-file wrapping and scanner lifetime are
/// supplied by the host/platform boundary.
/// </summary>
public sealed partial class CapturePage : Page
{
    private readonly IScannerService? _scannerService;
    private readonly Func<CancellationToken, Task<IInputImageFile?>>? _pickImageAsync;
    private readonly Func<string, CancellationToken, Task<IInputImageFile>>? _openScanImageAsync;
    private readonly Func<
        AnswerSheetLayout,
        IInputImageFile,
        CaptureSourceType,
        CancellationToken,
        Task<CaptureRecord>>? _persistCaptureAsync;
    private readonly Action<string>? _deleteTemporaryScanFile;
    private readonly ObservableCollection<CaptureScannerDeviceItem> _scannerDevices = [];
    private AnswerSheetLayout? _layout;
    private bool _templateMatchesInputs;
    private CancellationTokenSource? _importCancellation;
    private CancellationTokenSource? _enumerationCancellation;
    private CancellationTokenSource? _scanCancellation;
    private bool _enumerationInProgress;
    private bool _scanInProgress;
    private TaskCompletionSource<bool>? _activeOperationCompletion;
    private bool _isClosed;
    private bool _updatingLayoutInputs;
    private CaptureRecord? _latestCapture;

    public CapturePage()
        : this(null, null, null, null, null, null)
    {
    }

    public CapturePage(
        IScannerService? scannerService,
        Func<CancellationToken, Task<IInputImageFile?>>? pickImageAsync,
        Func<string, CancellationToken, Task<IInputImageFile>>? openScanImageAsync,
        Func<
            AnswerSheetLayout,
            IInputImageFile,
            CaptureSourceType,
            CancellationToken,
            Task<CaptureRecord>>? persistCaptureAsync,
        Action<string>? deleteTemporaryScanFile,
        AnswerSheetLayout? initialLayout = null)
    {
        InitializeComponent();
        _scannerService = scannerService;
        _pickImageAsync = pickImageAsync;
        _openScanImageAsync = openScanImageAsync;
        _persistCaptureAsync = persistCaptureAsync;
        _deleteTemporaryScanFile = deleteTemporaryScanFile;
        ScannerList.ItemsSource = _scannerDevices;

        if (initialLayout is not null)
        {
            TitleBox.Text = initialLayout.Title;
            QuestionCountBox.Value = initialLayout.QuestionCount;
            OptionsPerQuestionBox.Value = initialLayout.OptionsPerQuestion;
            SetLayout(initialLayout);
        }
        else
        {
            UpdateImportButtonState();
            UpdateScannerAvailability();
        }
    }

    public AnswerSheetLayout? CurrentLayout => _layout;

    public CaptureRecord? LatestCapture => _latestCapture;

    /// <summary>Raised after a capture has been persisted successfully.</summary>
    public event EventHandler<CaptureRecord>? CaptureSaved;

    /// <summary>Raised when the user asks to open the latest capture in recognition/review.</summary>
    public event EventHandler<CaptureRecord>? RecognitionRequested;

    public bool IsBusy => _importCancellation is not null || _enumerationInProgress || _scanInProgress;

    public Task WaitForIdleAsync() => _activeOperationCompletion?.Task ?? Task.CompletedTask;

    public void SetLayout(AnswerSheetLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
        _templateMatchesInputs = true;
        _updatingLayoutInputs = true;
        try
        {
            TitleBox.Text = layout.Title;
            QuestionCountBox.Value = layout.QuestionCount;
            OptionsPerQuestionBox.Value = layout.OptionsPerQuestion;
        }
        finally
        {
            _updatingLayoutInputs = false;
        }
        var template = CaptureTemplateReference.FromLayout(layout);
        TemplateIdText.Text = $"模板 ID：{template.TemplateId}";
        CaptureStatusText.Text = $"模板参数已确认：{layout.QuestionCount} 题、每题 {layout.OptionsPerQuestion} 个选项。";
        UpdateImportButtonState();
        UpdateScanCaptureButtonState();
    }

    /// <summary>Restores a previously committed local record for same-window review.</summary>
    public void SetExistingCapture(CaptureRecord capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        _latestCapture = capture;
        OpenRecognitionButton.Visibility = Visibility.Visible;
        CaptureStatusText.Text =
            $"已找到最近采集记录：{capture.Manifest.CreatedAtUtc.ToLocalTime():g}，"
            + $"已关联模板 {capture.Manifest.TemplateId}。可直接打开识别 / 复核。";
    }

    /// <summary>Shows a local history error without falling back to another record.</summary>
    public void SetExistingCaptureLoadError(string code, string message)
    {
        _latestCapture = null;
        OpenRecognitionButton.Visibility = Visibility.Collapsed;
        CaptureStatusText.Text = $"最近采集记录无法打开：{message} 错误代码：{code}。";
    }

    public async Task CancelAndWaitAsync()
    {
        _isClosed = true;
        _importCancellation?.Cancel();
        _enumerationCancellation?.Cancel();
        _scanCancellation?.Cancel();
        var idle = WaitForIdleAsync();
        await idle.ConfigureAwait(true);
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
        if (IsBusy)
        {
            return;
        }

        GenerateTemplateButton.IsEnabled = false;
        CaptureStatusText.Text = "正在确认模板参数。";

        try
        {
            var questionCount = ReadInteger(QuestionCountBox, "题数");
            var optionsPerQuestion = ReadInteger(OptionsPerQuestionBox, "每题选项数");
            var layout = AnswerSheetLayout.Create(TitleBox.Text, questionCount, optionsPerQuestion);
            SetLayout(layout);
        }
        catch (Exception exception)
        {
            _layout = null;
            _templateMatchesInputs = false;
            TemplateIdText.Text = "模板参数无效。";
            CaptureStatusText.Text = FormatFailure("模板参数确认失败", exception, "请检查标题、题数和选项数后重试。");
            UpdateImportButtonState();
            UpdateScanCaptureButtonState();
        }
        finally
        {
            GenerateTemplateButton.IsEnabled = !_isClosed;
        }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_layout is null || !_templateMatchesInputs || _pickImageAsync is null || _persistCaptureAsync is null)
        {
            CaptureStatusText.Text = "请先确认有效的模板参数。";
            UpdateImportButtonState();
            return;
        }

        var layout = _layout;
        var cancellation = BeginImportOperation();
        try
        {
            CaptureStatusText.Text = "请选择要导入的 PNG 或 JPEG 图像。";
            var file = await _pickImageAsync(cancellation.Token);
            if (file is null)
            {
                CaptureStatusText.Text = "已取消图像导入，未留下采集记录。";
                return;
            }

            CaptureStatusText.Text = "正在验证图像并保存原图。";
            var capture = await _persistCaptureAsync(
                layout,
                file,
                CaptureSourceType.Import,
                cancellation.Token);
            SetLatestCapture(capture);
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
            CaptureStatusText.Text = FormatFailure("图像导入失败", exception, "请重新选择文件后重试。");
        }
        finally
        {
            EndImportOperation(cancellation);
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
        if (_scannerService is null || _enumerationInProgress || _scanInProgress || _importCancellation is not null)
        {
            CaptureStatusText.Text = "当前平台暂未提供扫描设备服务。";
            return;
        }

        _enumerationInProgress = true;
        _enumerationCancellation = new CancellationTokenSource();
        BeginOperation();
        _scannerDevices.Clear();
        ScannerList.SelectedItem = null;
        RefreshScannerButton.IsEnabled = false;
        ScanCaptureButton.IsEnabled = false;
        CaptureStatusText.Text = "正在刷新扫描设备。";
        try
        {
            var devices = await _scannerService.GetDevicesAsync(_enumerationCancellation.Token);
            foreach (var device in devices)
            {
                _scannerDevices.Add(new CaptureScannerDeviceItem(device));
            }

            if (!_isClosed)
            {
                CaptureStatusText.Text = _scannerDevices.Count == 0
                    ? "未发现扫描设备，请连接设备并重试。"
                    : $"已发现 {_scannerDevices.Count} 个扫描设备入口。";
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isClosed)
            {
                CaptureStatusText.Text = "刷新扫描设备已取消。";
            }
        }
        catch (Exception exception)
        {
            CaptureStatusText.Text = FormatFailure("扫描设备刷新失败", exception, "请检查设备与驱动后重试。");
        }
        finally
        {
            _enumerationCancellation.Dispose();
            _enumerationCancellation = null;
            _enumerationInProgress = false;
            EndOperation();
            if (!_isClosed)
            {
                RefreshScannerButton.IsEnabled = true;
                UpdateScanCaptureButtonState();
            }
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
            || _scannerService is null
            || _openScanImageAsync is null
            || _persistCaptureAsync is null
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
            CaptureStatusText.Text = FormatFailure("扫描参数无效", exception, "请选择 150、300 或 600 DPI 后重试。");
            return;
        }

        _scanInProgress = true;
        _scanCancellation = new CancellationTokenSource();
        BeginOperation();
        SetScanBusy(true);
        SetBusy(true);
        CaptureStatusText.Text =
            $"正在使用 {selectedDevice.Name} 扫描首张答题纸（平板、A4、{dpi} DPI）。";

        string? temporaryPath = null;
        try
        {
            var scan = await _scannerService.ScanAsync(
                selectedDevice.Device,
                new ScanOptions(dpi),
                _scanCancellation.Token);
            temporaryPath = scan.ImagePath;
            var scanFile = await _openScanImageAsync(temporaryPath, _scanCancellation.Token);
            var capture = await _persistCaptureAsync(
                layout,
                scanFile,
                CaptureSourceType.Scan,
                _scanCancellation.Token);
            SetLatestCapture(capture);
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
            CaptureStatusText.Text = $"扫描保存失败：{exception.Message} 错误代码：{exception.Code}。请重试。";
        }
        catch (Exception exception)
        {
            CaptureStatusText.Text = FormatFailure("扫描保存失败", exception, "请检查设备、测试纸和分辨率后重试。");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                _deleteTemporaryScanFile?.Invoke(temporaryPath);
            }

            _scanCancellation?.Dispose();
            _scanCancellation = null;
            _scanInProgress = false;
            EndOperation();
            if (!_isClosed)
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

    private void SetLayoutFromTemplate(AnswerSheetLayout layout)
    {
        SetLayout(layout);
    }

    private void MarkTemplateOutdated()
    {
        if (_updatingLayoutInputs || _layout is null || !_templateMatchesInputs)
        {
            return;
        }

        _layout = null;
        _templateMatchesInputs = false;
        TemplateIdText.Text = "参数已变更，请重新确认模板参数。";
        CaptureStatusText.Text = "参数已变更，请重新确认模板参数。";
        UpdateImportButtonState();
        UpdateScanCaptureButtonState();
    }

    private void SetBusy(bool busy)
    {
        GenerateTemplateButton.IsEnabled = !busy && !_isClosed;
        TitleBox.IsEnabled = !busy && !_isClosed;
        QuestionCountBox.IsEnabled = !busy && !_isClosed;
        OptionsPerQuestionBox.IsEnabled = !busy && !_isClosed;
        ImportButton.IsEnabled = !busy && !_isClosed && _layout is not null && _templateMatchesInputs;
        CancelImportButton.IsEnabled = busy && _importCancellation is not null && !_isClosed;
        RefreshScannerButton.IsEnabled = !busy
            && !_isClosed
            && _scannerService is not null
            && !_enumerationInProgress
            && !_scanInProgress;
        ResolutionComboBox.IsEnabled = !busy && !_isClosed && !_scanInProgress;
        UpdateScanCaptureButtonState();
    }

    private void UpdateImportButtonState()
    {
        if (_importCancellation is not null)
        {
            return;
        }

        ImportButton.IsEnabled = !_isClosed
            && _pickImageAsync is not null
            && _persistCaptureAsync is not null
            && _layout is not null
            && _templateMatchesInputs;
    }

    private void SetScanBusy(bool busy)
    {
        RefreshScannerButton.IsEnabled = !busy
            && !_isClosed
            && _scannerService is not null
            && !_enumerationInProgress
            && _importCancellation is null;
        ResolutionComboBox.IsEnabled = !busy && !_isClosed && _importCancellation is null;
        ScannerList.IsEnabled = !busy && !_isClosed && _importCancellation is null;
        CancelScanCaptureButton.IsEnabled = busy && !_isClosed;
        UpdateScanCaptureButtonState();
    }

    private void UpdateScanCaptureButtonState()
    {
        ScanCaptureButton.IsEnabled = !_isClosed
            && _scannerService is not null
            && !_enumerationInProgress
            && !_scanInProgress
            && _importCancellation is null
            && _layout is not null
            && _templateMatchesInputs
            && _openScanImageAsync is not null
            && _persistCaptureAsync is not null
            && ScannerList.SelectedItem is CaptureScannerDeviceItem;
    }

    private void UpdateScannerAvailability()
    {
        var available = _scannerService is not null;
        RefreshScannerButton.IsEnabled = available && !_isClosed;
        ScannerList.IsEnabled = available && !_isClosed;
        if (!available)
        {
            CaptureStatusText.Text = "当前平台暂未提供扫描设备服务；你仍可导入图像。";
        }
    }

    private CancellationTokenSource BeginImportOperation()
    {
        var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation;
        BeginOperation();
        SetBusy(true);
        return cancellation;
    }

    private void EndImportOperation(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_importCancellation, cancellation))
        {
            _importCancellation = null;
        }

        cancellation.Dispose();
        EndOperation();
        if (!_isClosed)
        {
            SetBusy(false);
            UpdateImportButtonState();
        }
    }

    private void BeginOperation()
    {
        _activeOperationCompletion ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void EndOperation()
    {
        if (_importCancellation is null && !_enumerationInProgress && !_scanInProgress)
        {
            _activeOperationCompletion?.TrySetResult(true);
            _activeOperationCompletion = null;
        }
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

    private static string FormatFailure(string operation, Exception exception, string nextStep)
    {
        var rootCause = exception.GetBaseException();
        return $"{operation}：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。{nextStep}";
    }

    private void SetLatestCapture(CaptureRecord capture)
    {
        _latestCapture = capture;
        OpenRecognitionButton.Visibility = Visibility.Visible;
        CaptureSaved?.Invoke(this, capture);
    }

    private void OpenRecognitionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_latestCapture is not null)
        {
            RecognitionRequested?.Invoke(this, _latestCapture);
        }
    }
}

public sealed record CaptureScannerDeviceItem(ScannerDevice Device)
{
    public string Driver => Device.Driver.ToUpperInvariant();

    public string Name => Device.Name;
}
