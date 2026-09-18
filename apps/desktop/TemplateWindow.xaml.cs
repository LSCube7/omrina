using AnswerSheet.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AnswerSheet.Desktop;

public sealed partial class TemplateWindow : Window
{
    private const double PreviewScale = 2;
    private AnswerSheetLayout? _layout;
    private TemplatePrintController? _printController;
    private CaptureWindow? _captureWindow;
    private bool _previewMatchesInputs;
    private bool _printUiLocked;

    public TemplateWindow()
    {
        InitializeComponent();
        SaveSvgButton.IsEnabled = false;
        PrintButton.IsEnabled = false;
        ImportButton.IsEnabled = false;
        try
        {
            _printController = new TemplatePrintController(this, SetStatus);
        }
        catch (Exception exception)
        {
            var rootCause = exception.GetBaseException();
            SetStatus($"系统打印不可用：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。");
        }

        Closed += TemplateWindow_Closed;
        GenerateLayout();
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
            SaveSvgButton.IsEnabled = true;
            PrintButton.IsEnabled = _printController is not null && !_printController.IsBusy;
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
        if (_layout is null || !_previewMatchesInputs)
        {
            TemplateStatusText.Text = "参数已变更，请先重新生成预览后再保存。";
            return;
        }

        var layout = _layout;
        GenerateButton.IsEnabled = false;
        SaveSvgButton.IsEnabled = false;
        try
        {
            TemplateStatusText.Text = "正在选择 SVG 保存位置。";
            var picker = new FileSavePicker
            {
                SuggestedFileName = "answersheet-template"
            };
            picker.FileTypeChoices.Add("SVG 图像", [".svg"]);
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this));

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                TemplateStatusText.Text = "已取消保存 SVG。";
                return;
            }

            TemplateStatusText.Text = "正在保存 SVG。";
            await FileIO.WriteTextAsync(file, layout.ToSvg(), Windows.Storage.Streams.UnicodeEncoding.Utf8);
            TemplateStatusText.Text = $"SVG 已保存：{file.Path}";
        }
        catch (Exception exception)
        {
            var rootCause = exception.GetBaseException();
            TemplateStatusText.Text = $"保存 SVG 失败：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。请重新选择位置后再试。";
        }
        finally
        {
            GenerateButton.IsEnabled = true;
            SaveSvgButton.IsEnabled = ReferenceEquals(_layout, layout) && _previewMatchesInputs;
        }
    }

    private async void PrintButton_Click(object sender, RoutedEventArgs e)
    {
        if (_layout is null || !_previewMatchesInputs || _printController is null)
        {
            SetStatus("参数已变更或系统打印不可用，请先生成预览后再试。");
            return;
        }

        var layout = _layout;
        _printUiLocked = true;
        SetControlsEnabled(false);
        try
        {
            await _printController.RequestPrintAsync(layout);
        }
        catch (Exception exception)
        {
            var rootCause = exception.GetBaseException();
            SetStatus($"无法打开系统打印：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。请检查打印机后重试。");
        }
        finally
        {
            if (ReferenceEquals(_layout, layout)
                && _previewMatchesInputs
                && (_printController is null || !_printController.IsBusy))
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

        if (_captureWindow is not null)
        {
            _captureWindow.Activate();
            return;
        }

        var captureWindow = new CaptureWindow(_layout);
        _captureWindow = captureWindow;
        captureWindow.Closed += CaptureWindow_Closed;
        captureWindow.Activate();
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

    private void TemplateWindow_Closed(object sender, WindowEventArgs args)
    {
        _printController?.Dispose();
        _printController = null;
        _captureWindow?.Close();
        _captureWindow = null;
    }

    private void SetControlsEnabled(bool enabled)
    {
        TitleBox.IsEnabled = enabled;
        QuestionCountBox.IsEnabled = enabled;
        OptionsPerQuestionBox.IsEnabled = enabled;
        GenerateButton.IsEnabled = enabled;
        SaveSvgButton.IsEnabled = enabled && _layout is not null && _previewMatchesInputs;
        PrintButton.IsEnabled = enabled
            && _layout is not null
            && _previewMatchesInputs
            && _printController is not null
            && !_printController.IsBusy;
        ImportButton.IsEnabled = enabled && _layout is not null && _previewMatchesInputs;
    }

    private void SetStatus(string message)
    {
        TemplateStatusText.Text = message;
        if (_printUiLocked && (_printController is null || !_printController.IsBusy))
        {
            _printUiLocked = false;
            SetControlsEnabled(true);
        }
    }

    private void CaptureWindow_Closed(object sender, WindowEventArgs args)
    {
        if (ReferenceEquals(_captureWindow, sender))
        {
            _captureWindow = null;
        }
    }
}
