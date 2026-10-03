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
    private readonly Func<SchoolExamCatalog> _readCatalog;
    private readonly ObservableCollection<SchoolExamListItem> _items = [];
    private Func<SchoolSheetDefinition, Task<FileSaveResult>>? _exportBundleAsync;
    private Func<Task<SchoolSheetDefinition?>>? _importBundleAsync;
    private SchoolSheetDefinition? _currentExam;
    private bool _refreshing;
    private bool _transferInProgress;

    public SchoolExamPage(Func<SchoolExamCatalog> readCatalog)
    {
        InitializeComponent();
        _readCatalog = readCatalog ?? throw new ArgumentNullException(nameof(readCatalog));
        ExamList.ItemsSource = _items;
        Refresh();
    }

    public event Action? NewExamRequested;
    public event Action<SchoolSheetDefinition>? ExamSelected;
    public event Action<string>? NavigationRequested;

    public void ConfigureTemplateBundleTransfer(
        Func<SchoolSheetDefinition, Task<FileSaveResult>> exportBundleAsync,
        Func<Task<SchoolSheetDefinition?>> importBundleAsync)
    {
        _exportBundleAsync = exportBundleAsync ?? throw new ArgumentNullException(nameof(exportBundleAsync));
        _importBundleAsync = importBundleAsync ?? throw new ArgumentNullException(nameof(importBundleAsync));
        UpdateTransferButtons();
    }

    public void SetCurrentExam(SchoolSheetDefinition definition)
    {
        _currentExam = definition ?? throw new ArgumentNullException(nameof(definition));
        CurrentExamText.Text = $"当前考试：{definition.Title} · 第 {definition.Version} 版";
        UpdateTransferButtons();
    }

    public void Refresh()
    {
        _refreshing = true;
        try
        {
            var catalog = _readCatalog();
            _items.Clear();
            foreach (var definition in catalog.Definitions)
            {
                _items.Add(new SchoolExamListItem(definition));
            }
            var summary = _items.Count == 0
                ? "暂无已保存的考试。生成答题卡后会保存对应版本。"
                : $"已保存 {_items.Count} 个答题卡版本。选中后可继续准备或阅卷。";
            if (catalog.Diagnostics.Count == 0)
            {
                ExamStatusText.Text = summary;
            }
            else
            {
                var details = string.Join("；", catalog.Diagnostics.Take(3)
                    .Select(diagnostic => $"{diagnostic.DocumentName}：{diagnostic.ErrorType}"));
                ExamStatusText.Text = $"{summary} 另有 {catalog.Diagnostics.Count} 份本地资料无法读取，原文件已保留。调试信息：模块 exam-catalog；{details}。";
            }
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

    private async void ExportBundle_Click(object sender, RoutedEventArgs args)
    {
        if (_transferInProgress || _currentExam is not { } definition || _exportBundleAsync is null)
        {
            return;
        }

        _transferInProgress = true;
        UpdateTransferButtons();
        try
        {
            var result = await _exportBundleAsync(definition);
            ExamStatusText.Text = result.Cancelled
                ? "已取消导出。"
                : "考试资料包已导出到所选位置。可将该文件带到另一台电脑离线导入。";
        }
        catch (Exception exception)
        {
            ExamStatusText.Text = $"考试资料包导出失败，请重试并确认目标位置可写。调试信息：模块 exam-bundle-export，错误类型 {exception.GetType().Name}，代码 0x{exception.HResult:X8}。";
        }
        finally
        {
            _transferInProgress = false;
            UpdateTransferButtons();
        }
    }

    private async void ImportBundle_Click(object sender, RoutedEventArgs args)
    {
        if (_transferInProgress || _importBundleAsync is null)
        {
            return;
        }

        _transferInProgress = true;
        UpdateTransferButtons();
        try
        {
            var imported = await _importBundleAsync();
            if (imported is null)
            {
                ExamStatusText.Text = "已取消导入。";
                return;
            }

            Refresh();
            ExamStatusText.Text = $"已导入“{imported.Title}”第 {imported.Version} 版资料包。选择列表中的考试后即可使用。";
        }
        catch (Exception exception)
        {
            ExamStatusText.Text = $"考试资料包导入失败。请确认文件有效，并检查是否与本机已有版本冲突。调试信息：模块 exam-bundle-import，错误类型 {exception.GetType().Name}，代码 0x{exception.HResult:X8}。";
        }
        finally
        {
            _transferInProgress = false;
            UpdateTransferButtons();
        }
    }

    private void UpdateTransferButtons()
    {
        ImportBundleButton.IsEnabled = !_transferInProgress && _importBundleAsync is not null;
        ExportBundleButton.IsEnabled = !_transferInProgress && _exportBundleAsync is not null && _currentExam is not null;
    }
}
