using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Omrina.Core;
using System.Collections.ObjectModel;

namespace Omrina.Desktop;

public sealed record SchoolExamListItem(SchoolSheetDefinition Definition)
{
    public string Name => $"{Definition.Title} · 第 {Definition.Version} 版";
    public string Summary => $"{(Definition.Paper == SchoolPaper.A4Portrait ? "A4 纵向" : $"A3 横向 · {Definition.Columns} 栏")} · {Definition.Questions.Count} 道题";
}

public sealed partial class SchoolExamPage : Page
{
    private readonly Func<IReadOnlyList<SchoolSheetDefinition>> _readDefinitions;
    private readonly ObservableCollection<SchoolExamListItem> _items = [];
    private bool _refreshing;

    public SchoolExamPage(Func<IReadOnlyList<SchoolSheetDefinition>> readDefinitions)
    {
        InitializeComponent();
        _readDefinitions = readDefinitions;
        ExamList.ItemsSource = _items;
        Refresh();
    }

    public event Action? NewExamRequested;
    public event Action<SchoolSheetDefinition>? ExamSelected;
    public event Action<string>? NavigationRequested;

    public void SetCurrentExam(SchoolSheetDefinition definition)
    {
        CurrentExamText.Text = $"当前考试：{definition.Title} · 第 {definition.Version} 版";
    }

    public void Refresh()
    {
        _refreshing = true;
        try
        {
            var definitions = _readDefinitions();
            _items.Clear();
            foreach (var definition in definitions)
            {
                _items.Add(new SchoolExamListItem(definition));
            }
            ExamStatusText.Text = _items.Count == 0
                ? "暂无已保存的考试。生成答题卡后会保存对应版本。"
                : $"已保存 {_items.Count} 个答题卡版本。选中后可继续准备或阅卷。";
        }
        catch (Exception exception)
        {
            ExamStatusText.Text = $"考试资料读取失败，请检查本地资料后重试。调试信息：{exception.GetType().Name}（0x{exception.HResult:X8}）。";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ExamList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_refreshing && ExamList.SelectedItem is SchoolExamListItem item)
        {
            ExamSelected?.Invoke(item.Definition);
            // A blocked switch must still allow the same exam to be selected again.
            _refreshing = true;
            try { ExamList.SelectedItem = null; }
            finally { _refreshing = false; }
        }
    }

    private void NewExam_Click(object sender, RoutedEventArgs args) => NewExamRequested?.Invoke();
    private void Refresh_Click(object sender, RoutedEventArgs args) => Refresh();
    private void Template_Click(object sender, RoutedEventArgs args) => NavigationRequested?.Invoke("template");
    private void AnswerKey_Click(object sender, RoutedEventArgs args) => NavigationRequested?.Invoke("answer-key");
    private void Capture_Click(object sender, RoutedEventArgs args) => NavigationRequested?.Invoke("capture");
}
