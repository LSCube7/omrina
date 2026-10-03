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
    private readonly ObservableCollection<SchoolQuestionGroupItem> _groups = [];
    private bool _isInitializing = true;
    private bool _loadingQuestion;
    private bool _loadingGroup;
    private bool _normalizingBubbleSize;
    private bool _previewMatchesInputs;
    private bool _busy;
    private bool _isRegistered;
    private string _documentId = Guid.NewGuid().ToString("N");
    private AnswerSheetLayout? _layout;
    private SchoolQuestionItem? _editingItem;
    private string? _editingGroupId;
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
        GroupList.ItemsSource = _groups;
        var defaultGroupId = Guid.NewGuid().ToString("N");
        _groups.Add(new(defaultGroupId, "选择题组"));
        for (var number = 1; number <= 20; number++)
            _questions.Add(new(new(number, SchoolQuestionType.Choice, Options: new[] { "", "", "", "" }), defaultGroupId));
        _isInitializing = false;
        GroupList.SelectedIndex = 0;
        QuestionList.SelectedIndex = 0;
        UpdateActions();
        TemplateStatusText.Text = "设置题目后生成预览。";
    }

    public void LoadSchoolDefinition(SchoolSheetDefinition definition, bool isSaved = true)
    {
        ArgumentNullException.ThrowIfNull(definition);
        SchoolAnswerSheet document;
        try
        {
            document = SchoolAnswerSheet.Create(definition);
            definition = document.Pages[0].SchoolDefinition!;
        }
        catch (Exception error)
        {
            _isRegistered = false;
            _busy = false;
            _previewMatchesInputs = false;
            _layout = null;
            CurrentSchoolDefinition = null;
            CurrentDocument = null;
            PagePicker.ItemsSource = null;
            PreviewCanvas.Children.Clear();
            SummaryText.Text = "";
            TemplateStatusText.Text = $"版式无法打开：{error.Message}";
            UpdateActions();
            return;
        }
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
            LayoutOrderBox.SelectedIndex = definition.LayoutOrder == SchoolLayoutOrder.Separated ? 1 : 0;
            BubbleWidthBox.Maximum = definition.BubbleShape == BubbleShape.Rectangle ? double.PositiveInfinity : 2;
            BubbleWidthBox.Value = definition.BubbleWidthMm; BubbleHeightBox.Value = definition.BubbleHeightMm;
            _editingItem = null;
            _groups.Clear(); foreach (var group in definition.Groups) _groups.Add(new(group.Id, group.Title));
            var questionGroups = definition.Groups.SelectMany(group => group.QuestionNumbers.Select(number => (number, group.Id)))
                .ToDictionary(pair => pair.number, pair => pair.Id);
            _questions.Clear(); foreach (var question in definition.Questions) _questions.Add(new(question, questionGroups[question.Number]));
        }
        finally { _isInitializing = false; }
        _editingGroupId = null;
        GroupList.SelectedIndex = _groups.Count > 0 ? 0 : -1;
        QuestionList.SelectedIndex = 0;
        DisplayDocument(definition, document);
        TemplateStatusText.Text = isSaved ? "已打开已保存的版式，可查看或另存为新版本。" : "预览已生成。";
        UpdateActions();
    }

    public void ReportPlatformStatus(string message) { TemplateStatusText.Text = message; if (_isPrintBusy?.Invoke() != true) _busy = false; UpdateActions(); }
    private void InputChanged(object sender, TextChangedEventArgs e) => MarkOutdated();
    private void NumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (!_isInitializing && !_normalizingBubbleSize && (ReferenceEquals(sender, BubbleWidthBox) || ReferenceEquals(sender, BubbleHeightBox)) && double.IsFinite(sender.Value))
        {
            var scaled = sender.Value * 10;
            var tenths = Math.Round(scaled, MidpointRounding.AwayFromZero);
            if (double.IsFinite(scaled) && tenths is >= 1 and <= 9007199254740991d && Math.Abs(scaled - tenths) <= 1e-9)
            {
                var normalized = tenths / 10;
                if (sender.Value != normalized)
                {
                    _normalizingBubbleSize = true;
                    try { sender.Value = normalized; }
                    finally { _normalizingBubbleSize = false; }
                }
            }
        }
        MarkOutdated();
    }
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
        GroupList.IsEnabled = !_busy; GroupTitleBox.IsEnabled = !_busy; LayoutOrderBox.IsEnabled = !_busy;
        AddQuestionButton.IsEnabled = !_busy; RemoveQuestionButton.IsEnabled = !_busy && QuestionList.SelectedItem is SchoolQuestionItem;
        AddGroupButton.IsEnabled = !_busy;
        RemoveGroupButton.IsEnabled = !_busy && GroupList.SelectedItem is SchoolQuestionGroupItem group && !_questions.Any(item => item.GroupId == group.Id);
        MoveGroupUpButton.IsEnabled = !_busy && GroupList.SelectedIndex > 0;
        MoveGroupDownButton.IsEnabled = !_busy && GroupList.SelectedIndex >= 0 && GroupList.SelectedIndex < _groups.Count - 1;
        ApplyGroupTitleButton.IsEnabled = !_busy && _editingGroupId is not null;
        GenerateButton.IsEnabled = !_busy;
        SaveSvgButton.IsEnabled = ready && _saveSvgAsync is not null;
        PrintButton.IsEnabled = ready && _isRegistered && (_printDocumentAsync is not null || _printAsync is not null) && _isPrintBusy?.Invoke() != true;
        ImportButton.IsEnabled = ready && _isRegistered; PagePicker.IsEnabled = !_busy;
        ColumnsBox.IsEnabled = !_busy && PaperBox.SelectedIndex == 1;
        RepeatIdentityBox.IsEnabled = !_busy && DuplexBox.IsChecked == true;
        BubbleWidthBox.Maximum = ShapeBox.SelectedIndex == 1 ? double.PositiveInfinity : 2;
        QuestionGroupBox.IsEnabled = !_busy && QuestionList.SelectedItem is SchoolQuestionItem;
        HeightBox.IsEnabled = !_busy && GetDraftQuestionType() == SchoolQuestionType.Subjective;
        MoveQuestionUpButton.IsEnabled = !_busy && CanMoveSelectedQuestion(-1);
        MoveQuestionDownButton.IsEnabled = !_busy && CanMoveSelectedQuestion(1);
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
            RefreshQuestionGroups(question.Type, item.GroupId, item);
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
        var questionType = QuestionTypeBox.SelectedIndex == 1 ? SchoolQuestionType.Subjective : SchoolQuestionType.Choice;
        var selectedGroup = QuestionGroupBox.SelectedItem as SchoolQuestionGroupItem
            ?? throw new ArgumentException("请选择所属题组；如果没有合适的题组，请先新建空组。");
        if (_questions.Any(other => !ReferenceEquals(other, item) && other.GroupId == selectedGroup.Id && other.Question.Type != questionType))
            throw new ArgumentException("同一题组只能包含相同题型的题目，请选择其他题组或新建空组。");
        var definition = new SchoolQuestionDefinition(number, questionType,
            (decimal)score, BodyBox.Text, questionType == SchoolQuestionType.Choice ? options : null, HeightBox.Value);
        var index = _questions.IndexOf(item); _loadingQuestion = true;
        try
        {
            _questions[index] = new(definition, selectedGroup.Id);
            _editingItem = _questions[index];
            RefreshQuestionGroups(questionType, selectedGroup.Id, _editingItem);
            if (selectUpdated) QuestionList.SelectedIndex = index;
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
        try { if (_editingGroupId is not null) CommitGroupTitle(_editingGroupId); if (_editingItem is not null) ApplyQuestion(); }
        catch (Exception error) when (error is ArgumentException or OverflowException) { TemplateStatusText.Text = error.Message; return; }
        var type = QuestionTypeBox.SelectedIndex == 1 ? SchoolQuestionType.Subjective : SchoolQuestionType.Choice;
        var selectedGroup = GroupList.SelectedItem as SchoolQuestionGroupItem;
        var targetGroup = selectedGroup is not null && IsGroupCompatible(selectedGroup, type)
            ? selectedGroup
            : _groups.FirstOrDefault(group => IsGroupCompatible(group, type));
        if (targetGroup is null)
        {
            var prefix = type == SchoolQuestionType.Choice ? "选择题组" : "解答题组";
            targetGroup = new(Guid.NewGuid().ToString("N"), CreateUniqueGroupTitle(prefix));
            _groups.Add(targetGroup);
        }
        _questions.Add(new(new(_questions.Count == 0 ? 1 : _questions.Max(item => item.Question.Number) + 1, type,
            Options: type == SchoolQuestionType.Choice ? new[] { "", "", "", "" } : null), targetGroup.Id));
        QuestionList.SelectedIndex = _questions.Count - 1; MarkOutdated();
    }
    private void RemoveQuestion_Click(object sender, RoutedEventArgs e)
    {
        if (QuestionList.SelectedItem is not SchoolQuestionItem item) return;
        _questions.Remove(item);
        if (ReferenceEquals(_editingItem, item)) _editingItem = null;
        QuestionList.SelectedIndex = _questions.Count > 0 ? 0 : -1;
        MarkOutdated();
    }

    private void GroupTitleChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitializing && !_loadingGroup) MarkOutdated();
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _loadingGroup) return;
        var requested = GroupList.SelectedItem as SchoolQuestionGroupItem;
        var requestedId = requested?.Id;
        if (_editingGroupId is { } previousId && previousId != requestedId)
        {
            try { CommitGroupTitle(previousId); }
            catch (ArgumentException error)
            {
                _loadingGroup = true;
                try { GroupList.SelectedItem = _groups.FirstOrDefault(group => group.Id == previousId); }
                finally { _loadingGroup = false; }
                TemplateStatusText.Text = error.Message;
                return;
            }
        }
        _editingGroupId = requestedId;
        _loadingGroup = true;
        try { GroupTitleBox.Text = requestedId is null ? "" : _groups.First(group => group.Id == requestedId).Title; }
        finally { _loadingGroup = false; }
        UpdateActions();
    }

    private SchoolQuestionGroupItem CommitGroupTitle(string groupId)
    {
        var index = _groups.ToList().FindIndex(group => group.Id == groupId);
        if (index < 0) throw new ArgumentException("所选题组已不存在。");
        var title = GroupTitleBox.Text.Trim();
        if (title.Length == 0) throw new ArgumentException("题组名称不能为空。");
        if (_groups.Any(group => group.Id != groupId && string.Equals(group.Title, title, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("题组名称不能重复。");
        var updated = _groups[index] with { Title = title };
        if (!string.Equals(_groups[index].Title, title, StringComparison.Ordinal))
        {
            var selectedListGroupId = (GroupList.SelectedItem as SchoolQuestionGroupItem)?.Id;
            _groups[index] = updated;
            if (selectedListGroupId == groupId)
            {
                _loadingGroup = true;
                try { GroupList.SelectedItem = updated; GroupTitleBox.Text = title; }
                finally { _loadingGroup = false; }
            }
            var selectedId = QuestionGroupBox.SelectedItem is SchoolQuestionGroupItem selectedGroup ? selectedGroup.Id : _editingItem?.GroupId;
            RefreshQuestionGroups(GetDraftQuestionType(), selectedId, _editingItem);
        }
        return updated;
    }

    private void ApplyGroupTitle_Click(object sender, RoutedEventArgs e)
    {
        if (_editingGroupId is not { } groupId) return;
        try
        {
            var updated = CommitGroupTitle(groupId);
            _loadingGroup = true;
            try { GroupList.SelectedItem = updated; GroupTitleBox.Text = updated.Title; }
            finally { _loadingGroup = false; }
            MarkOutdated();
        }
        catch (ArgumentException error) { TemplateStatusText.Text = error.Message; }
    }

    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        try { if (_editingGroupId is { } currentId) CommitGroupTitle(currentId); }
        catch (ArgumentException error) { TemplateStatusText.Text = error.Message; return; }
        var group = new SchoolQuestionGroupItem(Guid.NewGuid().ToString("N"), CreateUniqueGroupTitle("新题组"));
        _groups.Add(group);
        _editingGroupId = group.Id;
        _loadingGroup = true;
        try { GroupList.SelectedItem = group; GroupTitleBox.Text = group.Title; }
        finally { _loadingGroup = false; }
        RefreshQuestionGroups(GetDraftQuestionType(), group.Id, _editingItem);
        MarkOutdated();
        UpdateActions();
    }

    private void RemoveGroup_Click(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not SchoolQuestionGroupItem group || _questions.Any(item => item.GroupId == group.Id)) return;
        var index = GroupList.SelectedIndex;
        _groups.Remove(group);
        var next = _groups.Count == 0 ? null : _groups[Math.Min(index, _groups.Count - 1)];
        _editingGroupId = next?.Id;
        _loadingGroup = true;
        try { GroupList.SelectedItem = next; GroupTitleBox.Text = next?.Title ?? ""; }
        finally { _loadingGroup = false; }
        RefreshQuestionGroups(GetDraftQuestionType(), QuestionList.SelectedItem is SchoolQuestionItem item ? item.GroupId : null, _editingItem);
        MarkOutdated();
        UpdateActions();
    }

    private void MoveGroupUp_Click(object sender, RoutedEventArgs e) => MoveSelectedGroup(-1);
    private void MoveGroupDown_Click(object sender, RoutedEventArgs e) => MoveSelectedGroup(1);

    private string CreateUniqueGroupTitle(string prefix)
    {
        var suffix = 1;
        var candidate = prefix;
        while (_groups.Any(group => string.Equals(group.Title, candidate, StringComparison.OrdinalIgnoreCase)))
            candidate = $"{prefix} {++suffix}";
        return candidate;
    }

    private void MoveSelectedGroup(int offset)
    {
        var index = GroupList.SelectedIndex;
        var targetIndex = index + offset;
        if (index < 0 || targetIndex < 0 || targetIndex >= _groups.Count) return;
        var selected = _groups[index];
        _groups.Move(index, targetIndex);
        _loadingGroup = true;
        try { GroupList.SelectedItem = selected; }
        finally { _loadingGroup = false; }
        RefreshQuestionGroups(GetDraftQuestionType(), QuestionGroupBox.SelectedItem is SchoolQuestionGroupItem questionGroup ? questionGroup.Id : _editingItem?.GroupId, _editingItem);
        MarkOutdated();
        UpdateActions();
    }

    private void QuestionType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _loadingQuestion) return;
        RefreshQuestionGroups(GetDraftQuestionType(), QuestionGroupBox.SelectedItem is SchoolQuestionGroupItem group ? group.Id : null, _editingItem);
        MarkOutdated();
        UpdateActions();
    }

    private void QuestionGroup_SelectionChanged(object sender, SelectionChangedEventArgs e) => MarkOutdated();

    private SchoolQuestionType GetDraftQuestionType() => QuestionTypeBox.SelectedIndex == 1 ? SchoolQuestionType.Subjective : SchoolQuestionType.Choice;

    private bool IsGroupCompatible(SchoolQuestionGroupItem group, SchoolQuestionType type) => _questions
        .Where(item => item.GroupId == group.Id).All(item => item.Question.Type == type);

    private void RefreshQuestionGroups(SchoolQuestionType type, string? selectedGroupId, SchoolQuestionItem? editingItem)
    {
        var wasLoading = _loadingQuestion;
        _loadingQuestion = true;
        try
        {
            var compatibleGroups = _groups.Where(group => _questions
                .Where(item => item.GroupId == group.Id && !ReferenceEquals(item, editingItem))
                .All(item => item.Question.Type == type)).ToArray();
            QuestionGroupBox.ItemsSource = compatibleGroups;
            QuestionGroupBox.SelectedItem = compatibleGroups.FirstOrDefault(group => group.Id == selectedGroupId);
        }
        finally { _loadingQuestion = wasLoading; }
    }

    private bool CanMoveSelectedQuestion(int offset)
    {
        if (QuestionList.SelectedItem is not SchoolQuestionItem selected) return false;
        var current = _editingItem is not null && _questions.Contains(_editingItem) ? _editingItem : selected;
        var groupId = current.GroupId;
        var currentIndex = _questions.IndexOf(current);
        var groupIndices = _questions.Select((item, index) => (item, index))
            .Where(entry => entry.item.GroupId == groupId).Select(entry => entry.index).ToArray();
        var position = Array.IndexOf(groupIndices, currentIndex);
        return position + offset >= 0 && position + offset < groupIndices.Length;
    }

    private void MoveQuestionUp_Click(object sender, RoutedEventArgs e) => MoveSelectedQuestion(-1);
    private void MoveQuestionDown_Click(object sender, RoutedEventArgs e) => MoveSelectedQuestion(1);
    private void MoveSelectedQuestion(int offset)
    {
        if (QuestionList.SelectedItem is not SchoolQuestionItem selected) return;
        try { ApplyQuestion(); }
        catch (Exception error) when (error is ArgumentException or OverflowException) { TemplateStatusText.Text = error.Message; return; }
        var currentIndex = _questions.IndexOf(_editingItem ?? selected);
        var groupId = _editingItem?.GroupId ?? selected.GroupId;
        var groupIndices = _questions.Select((item, index) => (item, index))
            .Where(entry => entry.item.GroupId == groupId).Select(entry => entry.index).ToArray();
        var position = Array.IndexOf(groupIndices, currentIndex);
        if (position + offset < 0 || position + offset >= groupIndices.Length) return;
        var targetIndex = groupIndices[position + offset];
        var currentItem = _questions[currentIndex];
        var targetItem = _questions[targetIndex];
        _loadingQuestion = true;
        try
        {
            _questions[currentIndex] = targetItem;
            _questions[targetIndex] = currentItem;
            _editingItem = currentItem;
            QuestionList.SelectedItem = currentItem;
        }
        finally { _loadingQuestion = false; }
        MarkOutdated();
        UpdateActions();
    }
    private void GenerateButton_Click(object sender, RoutedEventArgs e) => GenerateLayout(true);
    private void GenerateLayout(bool save)
    {
        _busy = true; _previewMatchesInputs = false; UpdateActions();
        TemplateStatusText.Text = "正在排版答题卡。";
        try
        {
            if (save)
            {
                if (_editingGroupId is { } groupId) CommitGroupTitle(groupId);
                if (_editingItem is not null) ApplyQuestion();
            }
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
                Questions = _questions.Select(item => item.Question).ToArray(),
                Groups = BuildGroups(),
                LayoutOrder = LayoutOrderBox.SelectedIndex == 1 ? SchoolLayoutOrder.Separated : SchoolLayoutOrder.Mixed
            };
            var document = SchoolAnswerSheet.Create(definition);
            definition = document.Pages[0].SchoolDefinition!;
            DisplayDocument(definition, document);
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
    private void DisplayDocument(SchoolSheetDefinition definition, SchoolAnswerSheet document)
    {
        CurrentSchoolDefinition = definition; CurrentDocument = document;
        PagePicker.ItemsSource = document.Pages.Select(page => $"第 {page.SchoolMetadata!.PageNumber} 页 · {(page.SchoolMetadata.Side == SchoolPageSide.Back ? "反面" : "正面")}").ToArray();
        PagePicker.SelectedIndex = 0; ShowPage(0);
        _previewMatchesInputs = true;
        SummaryText.Text = $"共 {definition.Questions.Count} 题、{definition.Groups.Count} 个题组 · 总分 {definition.Questions.Sum(question => question.MaximumScore)} · {document.Pages.Count} 页 · 版本 {definition.Version}";
    }

    private IReadOnlyList<SchoolQuestionGroupDefinition> BuildGroups()
    {
        if (_groups.Count == 0) throw new ArgumentException("请至少创建一个题组并分配题目。");
        var groups = new List<SchoolQuestionGroupDefinition>(_groups.Count);
        foreach (var group in _groups)
        {
            var numbers = _questions.Where(item => item.GroupId == group.Id).Select(item => item.Question.Number).ToArray();
            if (numbers.Length == 0) throw new ArgumentException($"题组“{group.Title}”尚无题目，请先分配题目或删除该空组。");
            groups.Add(new(group.Id, group.Title.Trim(), numbers));
        }
        if (_questions.Any(item => !_groups.Any(group => group.Id == item.GroupId)))
            throw new ArgumentException("有题目尚未分配到题组。");
        return groups;
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
public sealed record SchoolQuestionItem(SchoolQuestionDefinition Question, string GroupId)
{
    public string DisplayText => $"第 {Question.Number} 题 · {(Question.Type == SchoolQuestionType.Choice ? "选择题" : "解答题")} · {Question.MaximumScore} 分";
}

public sealed record SchoolQuestionGroupItem(string Id, string Title)
{
    public string DisplayText => Title;
}
