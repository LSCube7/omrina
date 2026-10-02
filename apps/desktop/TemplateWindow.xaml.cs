using Omrina.Core;
using System.Collections.ObjectModel;
using System.Globalization;
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
    private const int MaximumSubjectiveRegionCount = 64;

    private readonly Func<AnswerSheetLayout, Task<SvgSaveResult>>? _saveSvgAsync;
    private readonly Func<AnswerSheetLayout, Task>? _printAsync;
    private readonly Func<bool>? _isPrintBusy;
    private readonly ObservableCollection<TemplateSubjectiveRegionPickerItem> _subjectiveRegionItems = [];
    private readonly List<TemplateSubjectiveRegion> _subjectiveRegions = [];
    private AnswerSheetLayout? _layout;
    private bool _previewMatchesInputs;
    private bool _printUiLocked;
    private bool _isInitializing = true;

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
        _isInitializing = false;
        _saveSvgAsync = saveSvgAsync;
        _printAsync = printAsync;
        _isPrintBusy = isPrintBusy;
        SubjectiveRegionList.ItemsSource = _subjectiveRegionItems;
        SaveSvgButton.IsEnabled = false;
        PrintButton.IsEnabled = false;
        ImportButton.IsEnabled = false;
        SubjectiveQuestionNumberBox.Text = GetSuggestedQuestionNumber();
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
        if (_isInitializing)
        {
            return;
        }

        if (sender == QuestionCountBox && _subjectiveRegions.Count == 0)
        {
            SubjectiveQuestionNumberBox.Text = GetSuggestedQuestionNumber();
        }

        MarkPreviewOutdated();
    }

    private void SubjectiveRegionInput_TextChanged(object sender, TextChangedEventArgs e) => MarkPreviewOutdated();

    private void AddSubjectiveRegionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_subjectiveRegions.Count >= MaximumSubjectiveRegionCount)
        {
            SetStatus($"每张答题纸最多定义 {MaximumSubjectiveRegionCount} 个主观题区域。");
            return;
        }

        if (!SubjectiveReviewInputParser.TryParseInteger(
                SubjectiveQuestionNumberBox.Text,
                1,
                int.MaxValue,
                out var questionNumber))
        {
            SetStatus("主观题题号必须是正整数。");
            SubjectiveQuestionNumberBox.Focus(FocusState.Programmatic);
            return;
        }

        if (!TryReadChoiceQuestionCount(out var choiceQuestionCount))
        {
            SetStatus("请先填写有效的选择题题数。");
            QuestionCountBox.Focus(FocusState.Programmatic);
            return;
        }

        if (questionNumber <= choiceQuestionCount)
        {
            SetStatus($"主观题题号必须大于选择题题数 {choiceQuestionCount}。");
            SubjectiveQuestionNumberBox.Focus(FocusState.Programmatic);
            return;
        }

        if (_subjectiveRegions.Any(region => region.QuestionNumber == questionNumber))
        {
            SetStatus("这个题号已经有区域，请换一个题号。");
            SubjectiveQuestionNumberBox.Focus(FocusState.Programmatic);
            return;
        }

        if (!SubjectiveReviewInputParser.TryParseMaximumScore(
                SubjectiveMaximumScoreBox.Text,
                CultureInfo.CurrentCulture,
                out var maximumScore))
        {
            SetStatus("满分必须大于 0 且不超过 1,000,000；小数请使用当前语言的分隔符。");
            SubjectiveMaximumScoreBox.Focus(FocusState.Programmatic);
            return;
        }

        if (!SubjectiveReviewInputParser.TryParseMillimetres(SubjectiveRegionXBox.Text, 10, 200, CultureInfo.CurrentCulture, out var x)
            || !SubjectiveReviewInputParser.TryParseMillimetres(SubjectiveRegionYBox.Text, 52, 275, CultureInfo.CurrentCulture, out var y)
            || !SubjectiveReviewInputParser.TryParseMillimetres(SubjectiveRegionWidthBox.Text, 40, 190, CultureInfo.CurrentCulture, out var width)
            || !SubjectiveReviewInputParser.TryParseMillimetres(SubjectiveRegionHeightBox.Text, 10, 223, CultureInfo.CurrentCulture, out var height))
        {
            SetStatus("区域坐标和尺寸必须是有效的毫米数；宽至少 40 毫米、高至少 10 毫米。");
            SubjectiveRegionXBox.Focus(FocusState.Programmatic);
            return;
        }

        try
        {
            var region = TemplateSubjectiveRegion.Create(
                questionNumber,
                maximumScore,
                new RectMm(x, y, width, height));
            _subjectiveRegions.Add(region);
            RefreshSubjectiveRegionList();
            SubjectiveQuestionNumberBox.Text = GetSuggestedQuestionNumber();
            MarkPreviewOutdated();
            SetStatus("主观题区域已添加。生成预览时会检查区域是否与选择题或其他区域重叠。");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            SetStatus($"主观题区域无效：{exception.Message}");
        }
    }

    private void RemoveSubjectiveRegionButton_Click(object sender, RoutedEventArgs e)
    {
        if (SubjectiveRegionList.SelectedItem is not TemplateSubjectiveRegionPickerItem selected)
        {
            return;
        }

        _subjectiveRegions.Remove(selected.Region);
        RefreshSubjectiveRegionList();
        MarkPreviewOutdated();
        SetStatus("已移除主观题区域，请重新生成预览。");
    }

    private void SubjectiveRegionList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSubjectiveRegionControls();

    private void GenerateLayout()
    {
        GenerateButton.IsEnabled = false;
        TemplateStatusText.Text = "正在生成模板预览。";

        try
        {
            var questionCount = ReadInteger(QuestionCountBox, "题数");
            var optionsPerQuestion = ReadInteger(OptionsPerQuestionBox, "每题选项数");
            var layout = AnswerSheetLayout.Create(
                TitleBox.Text,
                questionCount,
                optionsPerQuestion,
                _subjectiveRegions);

            RenderLayout(layout);
            _layout = layout;
            _previewMatchesInputs = true;
            SaveSvgButton.IsEnabled = _saveSvgAsync is not null;
            PrintButton.IsEnabled = _printAsync is not null && !IsPrintBusy();
            ImportButton.IsEnabled = true;
            var subjectiveSummary = layout.SubjectiveRegions.Count == 0
                ? "未添加主观题区域"
                : $"含 {layout.SubjectiveRegions.Count} 个主观题区域";
            SetStatus($"已生成 {layout.QuestionCount} 题、每题 {layout.OptionsPerQuestion} 个选项的 A4 模板，{subjectiveSummary}。编号：{layout.TemplateNumber}。");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            _layout = null;
            _previewMatchesInputs = false;
            SaveSvgButton.IsEnabled = false;
            PrintButton.IsEnabled = false;
            ImportButton.IsEnabled = false;
            PreviewCanvas.Children.Clear();
            SetStatus($"模板版式无效：{exception.Message} 请调整区域或选择题题数后重新生成。");
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
        if (!_previewMatchesInputs)
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
        SubjectiveQuestionNumberBox.IsEnabled = enabled;
        SubjectiveMaximumScoreBox.IsEnabled = enabled;
        SubjectiveRegionXBox.IsEnabled = enabled;
        SubjectiveRegionYBox.IsEnabled = enabled;
        SubjectiveRegionWidthBox.IsEnabled = enabled;
        SubjectiveRegionHeightBox.IsEnabled = enabled;
        AddSubjectiveRegionButton.IsEnabled = enabled;
        SubjectiveRegionList.IsEnabled = enabled;
        RemoveSubjectiveRegionButton.IsEnabled = enabled
            && SubjectiveRegionList.SelectedItem is TemplateSubjectiveRegionPickerItem;
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

    private bool TryReadChoiceQuestionCount(out int questionCount)
    {
        questionCount = default;
        if (double.IsNaN(QuestionCountBox.Value)
            || double.IsInfinity(QuestionCountBox.Value)
            || QuestionCountBox.Value != Math.Truncate(QuestionCountBox.Value)
            || QuestionCountBox.Value < 1
            || QuestionCountBox.Value > int.MaxValue)
        {
            return false;
        }

        questionCount = (int)QuestionCountBox.Value;
        return true;
    }

    private string GetSuggestedQuestionNumber()
    {
        var minimum = TryReadChoiceQuestionCount(out var count) ? count : 0;
        if (_subjectiveRegions.Count > 0)
        {
            minimum = Math.Max(minimum, _subjectiveRegions.Max(region => region.QuestionNumber));
        }

        return minimum == int.MaxValue
            ? string.Empty
            : (minimum + 1).ToString(CultureInfo.InvariantCulture);
    }

    private void RefreshSubjectiveRegionList()
    {
        _subjectiveRegionItems.Clear();
        foreach (var region in _subjectiveRegions.OrderBy(region => region.QuestionNumber))
        {
            _subjectiveRegionItems.Add(new TemplateSubjectiveRegionPickerItem(region));
        }

        SubjectiveRegionStatusText.Text = _subjectiveRegions.Count == 0
            ? "尚未添加主观题区域。"
            : $"已添加 {_subjectiveRegions.Count} 个区域；生成预览时会校验题号和区域冲突。";
        UpdateSubjectiveRegionControls();
    }

    private void UpdateSubjectiveRegionControls()
    {
        RemoveSubjectiveRegionButton.IsEnabled = !_printUiLocked
            && SubjectiveRegionList.SelectedItem is TemplateSubjectiveRegionPickerItem;
    }
}

public sealed record SvgSaveResult(bool Cancelled, string? Path);

public sealed record TemplateSubjectiveRegionPickerItem(TemplateSubjectiveRegion Region)
{
    public string DisplayText => string.Format(
        CultureInfo.CurrentCulture,
        "第 {0} 题 · 满分 {1} · X={2:0.##}、Y={3:0.##}、宽 {4:0.##}、高 {5:0.##} 毫米",
        Region.QuestionNumber,
        Region.MaximumScore,
        Region.Rectangle.X,
        Region.Rectangle.Y,
        Region.Rectangle.Width,
        Region.Rectangle.Height);
}
