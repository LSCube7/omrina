using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Omrina.Core;
using System.Collections.ObjectModel;

namespace Omrina.Desktop;

public sealed class ExamAnswerKeyRow
{
    public required int Number { get; init; }
    public required decimal MaximumScore { get; init; }
    public required IReadOnlyList<string> Options { get; init; }
    public string? Answer { get; set; }
    public string Label => $"第 {Number} 题 · {MaximumScore:0.##} 分";
}

public sealed partial class ExamAnswerKeyPage : Page
{
    private readonly Func<SchoolSheetDefinition, IReadOnlyDictionary<int, string>?> _read;
    private readonly Action<SchoolSheetDefinition, IReadOnlyDictionary<int, string>> _save;
    private readonly ObservableCollection<ExamAnswerKeyRow> _rows = [];
    private SchoolSheetDefinition? _definition;
    private IReadOnlyDictionary<int, string> _savedAnswers = new Dictionary<int, string>();
    private bool _loadFailed;
    public event Action? AnswersSaved;

    public ExamAnswerKeyPage(
        Func<SchoolSheetDefinition, IReadOnlyDictionary<int, string>?> read,
        Action<SchoolSheetDefinition, IReadOnlyDictionary<int, string>> save)
    {
        InitializeComponent();
        _read = read;
        _save = save;
        AnswersList.ItemsSource = _rows;
    }

    public void SetExam(SchoolSheetDefinition definition)
    {
        if (!_loadFailed && _definition is { } previous && previous.ExamId == definition.ExamId
            && previous.LayoutDocumentId == definition.LayoutDocumentId && previous.Version == definition.Version)
            return;
        if (!CanChangeExam()) throw new InvalidOperationException("请先保存当前考试的标准答案，再切换考试。");
        _definition = definition;
        _rows.Clear();
        SaveButton.IsEnabled = false;
        ExamNameText.Text = $"{definition.Title} · 第 {definition.Version} 版";
        try
        {
            var saved = _read(definition);
            _savedAnswers = saved ?? new Dictionary<int, string>();
            foreach (var question in definition.Questions.Where(question => question.Type == SchoolQuestionType.Choice))
            {
                var options = Enumerable.Range(0, question.Options!.Count)
                    .Select(index => ((char)('A' + index)).ToString()).ToArray();
                _rows.Add(new ExamAnswerKeyRow
                {
                    Number = question.Number,
                    MaximumScore = question.MaximumScore,
                    Options = options,
                    Answer = saved?.GetValueOrDefault(question.Number)
                });
            }
            SaveButton.IsEnabled = true;
            _loadFailed = false;
            StatusText.Text = _rows.Count == 0 ? "本场考试没有选择题，可直接批阅主观题。"
                : saved is null ? "请选择每道题的标准答案后保存。" : "已载入保存的标准答案。";
        }
        catch (Exception exception)
        {
            _loadFailed = true;
            StatusText.Text = $"标准答案读取失败，请重新选择已保存的考试后重试。调试信息：{exception.GetType().Name}（0x{exception.HResult:X8}）。";
        }
    }

    public bool CanChangeExam()
    {
        var changed = _rows.Any(row => row.Answer != _savedAnswers.GetValueOrDefault(row.Number));
        if (changed) StatusText.Text = "标准答案有未保存的修改，请先保存或撤销修改，再切换考试或答题卡版本。";
        return !changed;
    }

    private void Discard_Click(object sender, RoutedEventArgs args)
    {
        foreach (var row in _rows) row.Answer = _savedAnswers.GetValueOrDefault(row.Number);
        AnswersList.ItemsSource = null;
        AnswersList.ItemsSource = _rows;
        StatusText.Text = "已恢复上次保存的答案选择。";
    }

    private void Save_Click(object sender, RoutedEventArgs args)
    {
        if (_definition is null) return;
        if (_rows.Any(row => row.Answer is null || !row.Options.Contains(row.Answer)))
        {
            StatusText.Text = "请为每道选择题选择一个标准答案。";
            return;
        }
        try
        {
            var answers = _rows.ToDictionary(row => row.Number, row => row.Answer!);
            _save(_definition, answers);
            _savedAnswers = answers;
            StatusText.Text = "标准答案已保存，后续学生答卷将使用这套答案。";
            AnswersSaved?.Invoke();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"标准答案保存失败，请检查考试版本后重试。调试信息：{exception.GetType().Name}（0x{exception.HResult:X8}）。";
        }
    }
}
