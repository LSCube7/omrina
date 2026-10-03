using Omrina.Core;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Omrina.Desktop;

public sealed partial class TemplatePage : Page
{
    private readonly Func<AnswerSheetLayout, Task<SvgSaveResult>>? _saveSvgAsync;
    private readonly Func<AnswerSheetLayout, Task>? _printAsync;
    private readonly Func<IReadOnlyList<AnswerSheetLayout>, Task>? _printDocumentAsync;
    private readonly Func<bool>? _isPrintBusy;
    private readonly ObservableCollection<SchoolQuestionItem> _questions = [];
    private bool _isInitializing = true;
    private bool _loadingQuestion;
    private bool _previewMatchesInputs;
    private bool _busy;
    private bool _isRegistered;
    private string _documentId = Guid.NewGuid().ToString("N");
    private AnswerSheetLayout? _layout;
    private SchoolQuestionItem? _editingItem;
    public SchoolSheetDefinition? CurrentSchoolDefinition { get; private set; }
    public SchoolAnswerSheet? CurrentDocument { get; private set; }
    public AnswerSheetLayout? CurrentLayout => _layout;
    public bool HasValidPreview => _layout is not null && _previewMatchesInputs;
    public event Action<AnswerSheetLayout>? CaptureRequested;
    public event Action<SchoolSheetDefinition, SchoolAnswerSheet>? SchoolDocumentGenerated;

    public TemplatePage() : this(null, null, null) { }
    public TemplatePage(Func<AnswerSheetLayout, Task<SvgSaveResult>>? saveSvgAsync,
        Func<AnswerSheetLayout, Task>? printAsync, Func<bool>? isPrintBusy,
        Func<IReadOnlyList<AnswerSheetLayout>, Task>? printDocumentAsync = null)
    {
        InitializeComponent();
        _saveSvgAsync = saveSvgAsync;
        _printAsync = printAsync;
        _isPrintBusy = isPrintBusy;
        _printDocumentAsync = printDocumentAsync;
        ExamIdBox.Text = Guid.NewGuid().ToString("N");
        QuestionList.ItemsSource = _questions;
        for (var number = 1; number <= 20; number++)
            _questions.Add(new(new(number, SchoolQuestionType.Choice, Options: new[] { "", "", "", "" })));
        _isInitializing = false;
        QuestionList.SelectedIndex = 0;
        UpdateActions();
        TemplateStatusText.Text = "设置题目后生成预览。";
    }

    public void LoadSchoolDefinition(SchoolSheetDefinition definition, bool isSaved = true)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _isRegistered = isSaved;
        _isInitializing = true;
        try
        {
            _documentId = definition.LayoutDocumentId;
            ExamIdBox.Text = definition.ExamId; TitleBox.Text = definition.Title; VersionBox.Value = definition.Version;
            PaperBox.SelectedIndex = definition.Paper == SchoolPaper.A3Landscape ? 1 : 0;
            ColumnsBox.SelectedIndex = definition.Columns == 3 ? 1 : 0;
            ModeBox.SelectedIndex = definition.Mode == SchoolContentMode.WithQuestions ? 1 : 0;
            DuplexBox.IsChecked = definition.Duplex; RepeatIdentityBox.IsChecked = definition.RepeatBackIdentity;
            IdentityBox.SelectedIndex = definition.CandidateIdentity.Mode == CandidateIdentityMode.Marking ? 1 : 0;
            DigitsBox.Value = definition.CandidateIdentity.Digits; CandidateIdBox.Text = definition.CandidateIdentity.CandidateId ?? "";
            ShapeBox.SelectedIndex = definition.BubbleShape == BubbleShape.Rectangle ? 1 : 0;
            LabelBox.SelectedIndex = definition.LabelPlacement == LabelPlacement.Inside ? 1 : 0;
            BubbleWidthBox.Value = definition.BubbleWidthMm; BubbleHeightBox.Value = definition.BubbleHeightMm;
            _editingItem = null;
            _questions.Clear(); foreach (var question in definition.Questions) _questions.Add(new(question));
        }
        finally { _isInitializing = false; }
        QuestionList.SelectedIndex = 0;
        GenerateLayout(false);
    }

    public void ReportPlatformStatus(string message) { TemplateStatusText.Text = message; if (_isPrintBusy?.Invoke() != true) _busy = false; UpdateActions(); }
    private void InputChanged(object sender, TextChangedEventArgs e) => MarkOutdated();
    private void NumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs e) => MarkOutdated();
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) => MarkOutdated();
    private void CheckChanged(object sender, RoutedEventArgs e) => MarkOutdated();
    private void MarkOutdated()
    {
        if (_isInitializing || _loadingQuestion) return;
        _previewMatchesInputs = false;
        TemplateStatusText.Text = "参数已变更，请重新生成并保存版式。";
        UpdateActions();
    }
    private void UpdateActions()
    {
        if (_isInitializing) return;
        var ready = !_busy && HasValidPreview;
        SettingsPanel.IsEnabled = !_busy; QuestionPanel.IsEnabled = !_busy; QuestionList.IsEnabled = !_busy;
        AddQuestionButton.IsEnabled = !_busy; RemoveQuestionButton.IsEnabled = !_busy && QuestionList.SelectedItem is SchoolQuestionItem;
        GenerateButton.IsEnabled = !_busy;
        SaveSvgButton.IsEnabled = ready && _saveSvgAsync is not null;
        PrintButton.IsEnabled = ready && _isRegistered && (_printDocumentAsync is not null || _printAsync is not null) && _isPrintBusy?.Invoke() != true;
        ImportButton.IsEnabled = ready && _isRegistered; PagePicker.IsEnabled = !_busy;
        ColumnsBox.IsEnabled = !_busy && PaperBox.SelectedIndex == 1;
        RepeatIdentityBox.IsEnabled = !_busy && DuplexBox.IsChecked == true;
    }
    private void QuestionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _loadingQuestion || QuestionList.SelectedItem is not SchoolQuestionItem item) return;
        if (_editingItem is not null && _questions.Contains(_editingItem) && !ReferenceEquals(_editingItem, item))
        {
            try { ApplyQuestion(_editingItem, false); }
            catch (Exception error) when (error is ArgumentException or OverflowException)
            {
                _loadingQuestion = true;
                try { QuestionList.SelectedItem = _editingItem; }
                finally { _loadingQuestion = false; }
                TemplateStatusText.Text = $"请先修正当前题目：{error.Message}";
                return;
            }
        }
        _editingItem = item;
        _loadingQuestion = true;
        try
        {
            var question = item.Question;
            QuestionNumberBox.Value = question.Number; QuestionTypeBox.SelectedIndex = question.Type == SchoolQuestionType.Subjective ? 1 : 0;
            ScoreBox.Value = (double)question.MaximumScore; BodyBox.Text = question.Body;
            OptionCountBox.Value = question.Options?.Count ?? 4; OptionsBox.Text = string.Join("\n", question.Options ?? Array.Empty<string>());
            HeightBox.Value = question.SubjectiveHeightMm;
        }
        finally { _loadingQuestion = false; }
        UpdateActions();
    }
    private static int ReadInteger(NumberBox box, string label)
    {
        if (!double.IsFinite(box.Value) || box.Value != Math.Truncate(box.Value)) throw new ArgumentException($"{label}必须是整数。");
        return checked((int)box.Value);
    }
    private void ApplyQuestion(SchoolQuestionItem? editingItem = null, bool selectUpdated = true)
    {
        var item = editingItem ?? QuestionList.SelectedItem as SchoolQuestionItem;
        if (item is null) return;
        var number = ReadInteger(QuestionNumberBox, "题号");
        if (number < 1 || _questions.Any(other => !ReferenceEquals(other, item) && other.Question.Number == number)) throw new ArgumentException("题号必须为正整数且不能重复。");
        var score = ScoreBox.Value;
        if (!double.IsFinite(score) || score <= 0 || score > 1000000) throw new ArgumentException("满分必须大于零且不超过 1,000,000。");
        var count = ReadInteger(OptionCountBox, "选项数");
        if (count is < 2 or > 6) throw new ArgumentException("选项数须为 2–6。");
        var lines = OptionsBox.Text.Replace("\r", "").Split('\n');
        if (lines.Length > count && lines.Skip(count).Any(line => line.Length > 0)) throw new ArgumentException("选项正文行数超过选项数，请调整后重试。");
        var options = Enumerable.Range(0, count).Select(index => index < lines.Length ? lines[index] : "").ToArray();
        var definition = new SchoolQuestionDefinition(number, QuestionTypeBox.SelectedIndex == 1 ? SchoolQuestionType.Subjective : SchoolQuestionType.Choice,
            (decimal)score, BodyBox.Text, options, HeightBox.Value);
        var index = _questions.IndexOf(item); _loadingQuestion = true;
        try
        {
            _questions[index] = new(definition);
            if (selectUpdated) { QuestionList.SelectedIndex = index; _editingItem = _questions[index]; }
        }
        finally { _loadingQuestion = false; }
    }
    private void ApplyQuestion_Click(object sender, RoutedEventArgs e)
    {
        try { ApplyQuestion(); MarkOutdated(); } catch (Exception error) when (error is ArgumentException or OverflowException) { TemplateStatusText.Text = error.Message; }
    }
    private void AddQuestion_Click(object sender, RoutedEventArgs e)
    {
        if (_questions.Count >= 500) { TemplateStatusText.Text = "最多支持 500 道题。"; return; }
        _questions.Add(new(new(_questions.Count == 0 ? 1 : _questions.Max(item => item.Question.Number) + 1, SchoolQuestionType.Choice, Options: new[] { "", "", "", "" })));
        QuestionList.SelectedIndex = _questions.Count - 1; MarkOutdated();
    }
    private void RemoveQuestion_Click(object sender, RoutedEventArgs e)
    { if (QuestionList.SelectedItem is SchoolQuestionItem item) { _questions.Remove(item); QuestionList.SelectedIndex = _questions.Count > 0 ? 0 : -1; MarkOutdated(); } }
    private void GenerateButton_Click(object sender, RoutedEventArgs e) => GenerateLayout(true);
    private void GenerateLayout(bool save)
    {
        _busy = true; _previewMatchesInputs = false; UpdateActions();
        TemplateStatusText.Text = "正在排版答题卡。";
        try
        {
            if (save) ApplyQuestion();
            var definition = new SchoolSheetDefinition
            {
                ExamId = ExamIdBox.Text.Trim(), LayoutDocumentId = _documentId, Version = ReadInteger(VersionBox, "版本"), Title = TitleBox.Text.Trim(),
                Paper = PaperBox.SelectedIndex == 1 ? SchoolPaper.A3Landscape : SchoolPaper.A4Portrait,
                Columns = PaperBox.SelectedIndex == 1 ? ColumnsBox.SelectedIndex + 2 : 1,
                Mode = ModeBox.SelectedIndex == 1 ? SchoolContentMode.WithQuestions : SchoolContentMode.AnswerOnly,
                Duplex = DuplexBox.IsChecked == true, RepeatBackIdentity = RepeatIdentityBox.IsChecked == true,
                CandidateIdentity = new(IdentityBox.SelectedIndex == 1 ? CandidateIdentityMode.Marking : CandidateIdentityMode.Barcode, ReadInteger(DigitsBox, "考号位数"), string.IsNullOrWhiteSpace(CandidateIdBox.Text) ? null : CandidateIdBox.Text.Trim()),
                BubbleShape = ShapeBox.SelectedIndex == 1 ? BubbleShape.Rectangle : BubbleShape.Circle,
                LabelPlacement = LabelBox.SelectedIndex == 1 ? LabelPlacement.Inside : LabelPlacement.Outside,
                BubbleWidthMm = BubbleWidthBox.Value, BubbleHeightMm = BubbleHeightBox.Value,
                Questions = _questions.Select(item => item.Question).ToArray()
            };
            var document = SchoolAnswerSheet.Create(definition);
            CurrentSchoolDefinition = definition; CurrentDocument = document;
            PagePicker.ItemsSource = document.Pages.Select(page => $"第 {page.SchoolMetadata!.PageNumber} 页 · {(page.SchoolMetadata.Side == SchoolPageSide.Back ? "反面" : "正面")}").ToArray();
            PagePicker.SelectedIndex = 0; ShowPage(0);
            _previewMatchesInputs = true;
            SummaryText.Text = $"共 {definition.Questions.Count} 题 · 总分 {definition.Questions.Sum(question => question.MaximumScore)} · {document.Pages.Count} 页 · 版本 {definition.Version}";
            if (save)
            {
                _isRegistered = false;
                SchoolDocumentGenerated?.Invoke(definition, document);
                _isRegistered = true;
            }
            TemplateStatusText.Text = _isRegistered ? "预览与已保存版式一致，可打印整份或采集选中页。" : "预览已生成。请点击生成并保存版式，保存后才能打印和采集。";
        }
        catch (Exception error)
        {
            _previewMatchesInputs = false; _layout = null; PreviewCanvas.Children.Clear();
            TemplateStatusText.Text = $"版式生成或保存失败：{error.Message}。请调整后重试。";
        }
        finally { _busy = false; UpdateActions(); }
    }
    private void ShowPage(int index)
    {
        if (CurrentDocument is null || index < 0 || index >= CurrentDocument.Pages.Count) return;
        _layout = CurrentDocument.Pages[index]; TemplateRenderer.Render(_layout, PreviewCanvas, 2, true);
    }
    private void PagePicker_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_isInitializing) ShowPage(PagePicker.SelectedIndex); }
    private void EditorGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isInitializing) return;
        var narrow = e.NewSize.Width < 800;
        EditorGrid.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(340);
        EditorGrid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(PreviewPanel, narrow ? 0 : 1); Grid.SetRow(PreviewPanel, narrow ? 1 : 0);
    }
    private async void SaveSvgButton_Click(object sender, RoutedEventArgs e)
    {
        if (!HasValidPreview || _saveSvgAsync is null || _layout is null) return;
        _busy = true; UpdateActions();
        try { var result = await _saveSvgAsync(_layout); TemplateStatusText.Text = result.Cancelled ? "已取消保存。" : "当前页 SVG 已保存。"; }
        catch (Exception error) { TemplateStatusText.Text = $"保存失败：{error.GetBaseException().GetType().Name}。请重试。"; }
        finally { _busy = false; UpdateActions(); }
    }
    private async void PrintButton_Click(object sender, RoutedEventArgs e)
    {
        if (!HasValidPreview || !_isRegistered || CurrentDocument is null || _layout is null) return;
        _busy = true; UpdateActions();
        try { if (_printDocumentAsync is not null) await _printDocumentAsync(CurrentDocument.Pages); else if (_printAsync is not null) await _printAsync(_layout); }
        catch (Exception error) { TemplateStatusText.Text = $"系统打印无法打开：{error.GetBaseException().GetType().Name}。请检查平台和打印机。"; }
        finally { _busy = _isPrintBusy?.Invoke() == true; UpdateActions(); }
    }
    private void ImportButton_Click(object sender, RoutedEventArgs e) { if (HasValidPreview && _isRegistered && _layout is not null) CaptureRequested?.Invoke(_layout); }
}
public sealed record SvgSaveResult(bool Cancelled, string? Path);
public sealed record SchoolQuestionItem(SchoolQuestionDefinition Question)
{
    public string DisplayText => $"第 {Question.Number} 题 · {(Question.Type == SchoolQuestionType.Choice ? "选择题" : "解答题")} · {Question.MaximumScore} 分";
}
