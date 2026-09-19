using Omrina.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Omrina.Desktop;

/// <summary>
/// Cached template page hosted by <see cref="MainWindow"/>.
/// File picking, printing and navigation are supplied by the host so this page
/// never needs a native window handle.
/// </summary>
public sealed partial class TemplatePage : Page
{
    private const double PreviewScale = 2;

    private readonly Func<AnswerSheetLayout, Task<SvgSaveResult>>? _saveSvgAsync;
    private readonly Func<AnswerSheetLayout, Task>? _printAsync;
    private readonly Func<bool>? _isPrintBusy;
    private AnswerSheetLayout? _layout;
    private bool _previewMatchesInputs;
    private bool _printUiLocked;

    public TemplatePage()
        : this(null, null, null)
    {
    }

    public TemplatePage(
        Func<AnswerSheetLayout, Task<SvgSaveResult>>? saveSvgAsync,
        Func<AnswerSheetLayout, Task>? printAsync,
        Func<bool>? isPrintBusy)
    {
        InitializeComponent();
        _saveSvgAsync = saveSvgAsync;
        _printAsync = printAsync;
        _isPrintBusy = isPrintBusy;
        SaveSvgButton.IsEnabled = false;
        PrintButton.IsEnabled = false;
        ImportButton.IsEnabled = false;
        GenerateLayout();
    }

    /// <summary>Raised when the current layout should be used on the capture page.</summary>
    public event Action<AnswerSheetLayout>? CaptureRequested;

    public AnswerSheetLayout? CurrentLayout => _layout;

    public bool HasValidPreview => _layout is not null && _previewMatchesInputs;

    public void ReportPlatformStatus(string message)
    {
        SetStatus(message);
    }

    private void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        GenerateLayout();
    }

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        MarkPreviewOutdated();
    }

    private void NumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        MarkPreviewOutdated();
    }

    private void GenerateLayout()
    {
        GenerateButton.IsEnabled = false;
        TemplateStatusText.Text = "正在生成模板预览。";

        try
        {
            var questionCount = ReadInteger(QuestionCountBox, "题数");
            var optionsPerQuestion = ReadInteger(OptionsPerQuestionBox, "每题选项数");
            var layout = AnswerSheetLayout.Create(TitleBox.Text, questionCount, optionsPerQuestion);

            RenderLayout(layout);
            _layout = layout;
            _previewMatchesInputs = true;
            SaveSvgButton.IsEnabled = _saveSvgAsync is not null;
            PrintButton.IsEnabled = _printAsync is not null && !IsPrintBusy();
            ImportButton.IsEnabled = true;
            SetStatus($"已生成 {layout.QuestionCount} 题、每题 {layout.OptionsPerQuestion} 个选项的 A4 模板。编号：{layout.TemplateNumber}。");
        }
        catch (Exception exception)
        {
            _layout = null;
            _previewMatchesInputs = false;
            SaveSvgButton.IsEnabled = false;
            PrintButton.IsEnabled = false;
            ImportButton.IsEnabled = false;
            PreviewCanvas.Children.Clear();
            var rootCause = exception.GetBaseException();
            SetStatus($"模板生成失败：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。请检查标题、题数和选项数。");
        }
        finally
        {
            GenerateButton.IsEnabled = true;
        }
    }

    private async void SaveSvgButton_Click(object sender, RoutedEventArgs e)
    {
        if (_layout is null || !_previewMatchesInputs || _saveSvgAsync is null)
        {
            SetStatus("参数已变更或文件保存不可用，请先重新生成预览后再保存。");
            return;
        }

        var layout = _layout;
        GenerateButton.IsEnabled = false;
        SaveSvgButton.IsEnabled = false;
        try
        {
            SetStatus("正在选择 SVG 保存位置。");
            var result = await _saveSvgAsync(layout);
            SetStatus(result.Cancelled
                ? "已取消保存 SVG。"
                : $"SVG 已保存：{result.Path ?? "已选择文件"}");
        }
        catch (Exception exception)
        {
            var rootCause = exception.GetBaseException();
            SetStatus($"保存 SVG 失败：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。请重新选择位置后再试。");
        }
        finally
        {
            GenerateButton.IsEnabled = true;
            SaveSvgButton.IsEnabled = ReferenceEquals(_layout, layout) && _previewMatchesInputs;
        }
    }

    private async void PrintButton_Click(object sender, RoutedEventArgs e)
    {
        if (_layout is null || !_previewMatchesInputs || _printAsync is null)
        {
            SetStatus("参数已变更或系统打印不可用，请先生成预览后再试。");
            return;
        }

        var layout = _layout;
        _printUiLocked = true;
        SetControlsEnabled(false);
        try
        {
            await _printAsync(layout);
        }
        catch (Exception exception)
        {
            var rootCause = exception.GetBaseException();
            SetStatus($"无法打开系统打印：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。请检查打印机后重试。");
        }
        finally
        {
            if (ReferenceEquals(_layout, layout) && _previewMatchesInputs && !IsPrintBusy())
            {
                _printUiLocked = false;
                SetControlsEnabled(true);
            }
        }
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_layout is null || !_previewMatchesInputs)
        {
            SetStatus("参数已变更，请先重新生成预览后再导入图像。");
            return;
        }

        CaptureRequested?.Invoke(_layout);
    }

    private void RenderLayout(AnswerSheetLayout layout)
    {
        TemplateRenderer.Render(layout, PreviewCanvas, PreviewScale, showPageOutline: true);
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

    private void MarkPreviewOutdated()
    {
        if (_layout is null || !_previewMatchesInputs)
        {
            return;
        }

        _previewMatchesInputs = false;
        SaveSvgButton.IsEnabled = false;
        PrintButton.IsEnabled = false;
        ImportButton.IsEnabled = false;
        SetStatus("参数已变更，请重新生成预览。");
    }

    private void SetControlsEnabled(bool enabled)
    {
        TitleBox.IsEnabled = enabled;
        QuestionCountBox.IsEnabled = enabled;
        OptionsPerQuestionBox.IsEnabled = enabled;
        GenerateButton.IsEnabled = enabled;
        SaveSvgButton.IsEnabled = enabled && _saveSvgAsync is not null && _layout is not null && _previewMatchesInputs;
        PrintButton.IsEnabled = enabled
            && _printAsync is not null
            && _layout is not null
            && _previewMatchesInputs
            && !IsPrintBusy();
        ImportButton.IsEnabled = enabled && _layout is not null && _previewMatchesInputs;
    }

    private void SetStatus(string message)
    {
        TemplateStatusText.Text = message;
        if (_printUiLocked && !IsPrintBusy())
        {
            _printUiLocked = false;
            SetControlsEnabled(true);
        }
    }

    private bool IsPrintBusy() => _isPrintBusy?.Invoke() == true;
}

public sealed record SvgSaveResult(bool Cancelled, string? Path);
