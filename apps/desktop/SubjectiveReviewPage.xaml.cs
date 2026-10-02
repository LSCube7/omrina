using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Omrina.Core;
using Omrina.Platform;
using Windows.Storage.Streams;

namespace Omrina.Desktop;

public sealed record SubjectiveCapturePickerItem(LocalSubjectiveCaptureSummary Summary)
{
    public string DisplayText
    {
        get
        {
            var regionSummary = Summary.SubjectiveQuestionCount > 0
                ? $"{Summary.SubjectiveQuestionCount} 个主观题区域"
                : "无主观题区域";
            return $"{Summary.TemplateTitle} · {regionSummary} · {Summary.PixelWidth}×{Summary.PixelHeight} · "
                + $"{Summary.CreatedAtUtc.ToLocalTime():g} · {ShortId(Summary.CaptureId)}";
        }
    }

    private static string ShortId(string value) => value.Length <= 8 ? value : value[..8];
}

public sealed record SubjectiveReviewPickerItem(LocalSubjectiveReviewSummary Summary)
{
    public string DisplayText
    {
        get
        {
            var state = Summary.IsFinal
                ? $"已确认 · {Summary.FinalSubtotal?.ToString(CultureInfo.CurrentCulture) ?? "—"} 分"
                : "未完成";
            var id = Summary.ReviewId.ToString("N");
            return $"{Summary.CreatedAtUtc.ToLocalTime():g} · {Summary.QuestionCount} 题 · v{Summary.Version} · {state} · {id[..8]}";
        }
    }
}

public sealed record SubjectiveQuestionPickerItem(SubjectiveGradingQuestion Question)
{
    public string DisplayText
    {
        get
        {
            var state = Question.Status switch
            {
                SubjectiveReviewStatus.Unreviewed => "未批阅",
                SubjectiveReviewStatus.Draft => "草稿",
                SubjectiveReviewStatus.Confirmed => "已确认",
                _ => "状态无效"
            };
            var score = Question.Score?.ToString(CultureInfo.CurrentCulture) ?? "—";
            return $"第 {Question.QuestionNumber} 题 · {state} · {score}/{Question.MaximumScore.ToString(CultureInfo.CurrentCulture)} 分 · "
                + $"{Question.Region.Width}×{Question.Region.Height} 像素";
        }
    }
}

/// <summary>Cached local page for defining and grading human-marked image regions.</summary>
public sealed partial class SubjectiveReviewPage : Page
{
    private const int ReviewPageSize = 50;
    private readonly LocalSubjectiveReviewService? _service;
    private readonly Func<string, string, string, CancellationToken, Task<FileSaveResult>>? _saveTextAsync;
    private readonly ObservableCollection<SubjectiveCapturePickerItem> _captureItems = [];
    private readonly ObservableCollection<SubjectiveReviewPickerItem> _reviewItems = [];
    private readonly ObservableCollection<SubjectiveQuestionPickerItem> _questionItems = [];
    private readonly AsyncSelectionGeneration _captureImageGeneration = new();
    private readonly AsyncSelectionGeneration _questionImageGeneration = new();
    private readonly AsyncSelectionGeneration _captureListGeneration = new();
    private readonly AsyncSelectionGeneration _reviewListGeneration = new();
    private readonly AsyncSelectionGeneration _documentGeneration = new();
    private readonly CancellationTokenSource _pageLifetime = new();
    private readonly HashSet<Task> _activeOperations = [];
    private readonly List<LocalSubjectiveReviewDiagnostic> _captureDiagnostics = [];
    private readonly List<LocalSubjectiveReviewDiagnostic> _reviewDiagnostics = [];
    private LocalSubjectiveCaptureSummary? _selectedCapture;
    private LocalSubjectiveReviewDocument? _document;
    private SubjectiveGradingQuestion? _selectedQuestion;
    private string? _captureCursor;
    private string? _reviewCursor;
    private CancellationTokenSource? _captureListCancellation;
    private CancellationTokenSource? _reviewListCancellation;
    private CancellationTokenSource? _documentCancellation;
    private CancellationTokenSource? _captureImageCancellation;
    private CancellationTokenSource? _questionImageCancellation;
    private CancellationTokenSource? _mutationCancellation;
    private CancellationTokenSource? _exportCancellation;
    private int _activeOperationCount;
    private bool _hasLoadedOnNavigation;
    private bool _isClosed;
    private bool _isCaptureImageAvailable;
    private bool _isUpdatingCapturePicker;
    private bool _isUpdatingReviewPicker;
    private bool _isUpdatingQuestionList;
    private bool _isLoadingCaptureList;
    private bool _isLoadingReviewList;
    private bool _isCreatingReview;
    private bool _isMutatingReview;
    private bool _isReadingReview;
    private bool _isExporting;
    private Task? _shutdownTask;

    public SubjectiveReviewPage()
        : this(null, null)
    {
    }

    public SubjectiveReviewPage(
        LocalSubjectiveReviewService? service,
        Func<string, string, string, CancellationToken, Task<FileSaveResult>>? saveTextAsync)
    {
        InitializeComponent();
        _service = service;
        _saveTextAsync = saveTextAsync;
        CapturePicker.ItemsSource = _captureItems;
        CapturePicker.DisplayMemberPath = nameof(SubjectiveCapturePickerItem.DisplayText);
        ReviewPicker.ItemsSource = _reviewItems;
        ReviewPicker.DisplayMemberPath = nameof(SubjectiveReviewPickerItem.DisplayText);
        SubjectiveQuestionList.ItemsSource = _questionItems;
        UpdateActionStates();
        if (_service is null)
        {
            SetInfo(
                "主观题批阅不可用",
                "本地批阅服务尚未连接。请重启应用后重试。错误代码：LOCAL_REVIEW_SERVICE_UNAVAILABLE",
                InfoBarSeverity.Error);
        }
    }

    /// <summary>Called by MainWindow only after the user navigates to this page.</summary>
    public async Task ActivateAsync()
    {
        if (_isClosed || _service is null || _hasLoadedOnNavigation)
        {
            return;
        }

        _hasLoadedOnNavigation = true;
        await Task.WhenAll(
            RunTrackedOperationAsync(
                "采集记录加载失败",
                () => LoadCapturePageAsync(append: false),
                () => !_isClosed),
            RunTrackedOperationAsync(
                "批阅记录加载失败",
                () => LoadReviewPageAsync(append: false),
                () => !_isClosed));
    }

    public Task CancelAndWaitAsync() => _shutdownTask ??= CancelAndWaitCoreAsync();

    private async Task CancelAndWaitCoreAsync()
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;
        _pageLifetime.Cancel();
        CancelRequest(_captureListCancellation);
        CancelRequest(_reviewListCancellation);
        CancelRequest(_documentCancellation);
        CancelRequest(_captureImageCancellation);
        CancelRequest(_questionImageCancellation);
        CancelRequest(_mutationCancellation);
        CancelRequest(_exportCancellation);
        _captureImageGeneration.Invalidate();
        _questionImageGeneration.Invalidate();

        var activeOperations = _activeOperations.ToArray();
        if (activeOperations.Length > 0)
        {
            await Task.WhenAll(activeOperations).ConfigureAwait(true);
        }

        DisposeRequest(ref _captureListCancellation);
        DisposeRequest(ref _reviewListCancellation);
        DisposeRequest(ref _documentCancellation);
        DisposeRequest(ref _captureImageCancellation);
        DisposeRequest(ref _questionImageCancellation);
        DisposeRequest(ref _mutationCancellation);
        DisposeRequest(ref _exportCancellation);
        _pageLifetime.Dispose();
    }

    private async void ReloadCapturesButton_Click(object sender, RoutedEventArgs e) =>
        await RunTrackedOperationAsync(
            "采集记录加载失败",
            () => LoadCapturePageAsync(append: false),
            () => !_isClosed);

    private async void LoadMoreCapturesButton_Click(object sender, RoutedEventArgs e) =>
        await RunTrackedOperationAsync(
            "采集记录加载失败",
            () => LoadCapturePageAsync(append: true),
            () => !_isClosed);

    private async void ReloadReviewsButton_Click(object sender, RoutedEventArgs e) =>
        await RunTrackedOperationAsync(
            "批阅记录加载失败",
            () => LoadReviewPageAsync(append: false),
            () => !_isClosed);

    private async void LoadMoreReviewsButton_Click(object sender, RoutedEventArgs e) =>
        await RunTrackedOperationAsync(
            "批阅记录加载失败",
            () => LoadReviewPageAsync(append: true),
            () => !_isClosed);

    private void CapturePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingCapturePicker)
        {
            return;
        }

        _selectedCapture = (CapturePicker.SelectedItem as SubjectiveCapturePickerItem)?.Summary;
        if (_selectedCapture is null)
        {
            _captureImageGeneration.Invalidate();
            CancelRequest(_captureImageCancellation);
            OriginalCaptureImage.Source = null;
            OriginalCaptureSummaryText.Text = "选择采集记录后显示图像信息。";
            CaptureTemplateStatusText.Text = "选择采集记录后检查关联的主观题区域。";
            _isCaptureImageAvailable = false;
            UpdateActionStates();
            return;
        }

        var capture = _selectedCapture;
        UpdateCaptureTemplateStatus(capture);
        _ = RunTrackedOperationAsync(
            "原图预览失败",
            () => LoadCaptureImageAsync(capture),
            () => !_isClosed && string.Equals(
                _selectedCapture?.CaptureId,
                capture.CaptureId,
                StringComparison.OrdinalIgnoreCase));
        UpdateActionStates();
    }

    private void ReviewPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingReviewPicker || ReviewPicker.SelectedItem is not SubjectiveReviewPickerItem item)
        {
            return;
        }

        _ = RunTrackedOperationAsync(
            "批阅记录读取失败",
            () => LoadReviewAsync(item.Summary.ReviewId, preserveEditorInput: false),
            () => !_isClosed
                && ReviewPicker.SelectedItem is SubjectiveReviewPickerItem selected
                && selected.Summary.ReviewId == item.Summary.ReviewId);
    }

    private async void CreateReviewButton_Click(object sender, RoutedEventArgs e) =>
        await RunTrackedOperationAsync(
            "创建批阅记录失败",
            CreateReviewAsync,
            () => !_isClosed
                && _selectedCapture is
                {
                    SubjectiveQuestionCount: > 0 and <= SubjectiveGradingSnapshot.MaximumQuestionCount
                });

    private void SubjectiveQuestionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingQuestionList)
        {
            return;
        }

        if (SubjectiveQuestionList.SelectedItem is not SubjectiveQuestionPickerItem item)
        {
            _selectedQuestion = null;
            _questionImageGeneration.Invalidate();
            CancelRequest(_questionImageCancellation);
            QuestionRegionImage.Source = null;
            QuestionImageStatusText.Text = "选择一道题目后显示该区域图像。";
            UpdateGradeActions();
            return;
        }

        SelectQuestion(item.Question, preserveEditorInput: false);
    }

    private void QuestionScoreBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateGradeActions();

    private void QuestionCommentBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateGradeActions();

    private void ReviewerBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateGradeActions();

    private async void SaveDraftButton_Click(object sender, RoutedEventArgs e) =>
        await RunTrackedOperationAsync(
            "草稿保存失败",
            () => ApplyGradeAsync(SubjectiveReviewStatus.Draft),
            () => !_isClosed && _document is not null);

    private async void ConfirmScoreButton_Click(object sender, RoutedEventArgs e) =>
        await RunTrackedOperationAsync(
            "确认分数失败",
            () => ApplyGradeAsync(SubjectiveReviewStatus.Confirmed),
            () => !_isClosed && _document is not null);

    private async void ResetQuestionButton_Click(object sender, RoutedEventArgs e) =>
        await RunTrackedOperationAsync(
            "重置题目失败",
            () => ApplyGradeAsync(SubjectiveReviewStatus.Unreviewed),
            () => !_isClosed && _document is not null);

    private async void ReloadReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || _isCreatingReview || _isMutatingReview || _isReadingReview)
        {
            return;
        }

        var reviewId = _document.ReviewId;
        await RunTrackedOperationAsync(
            "重新读取批阅记录失败",
            () => LoadReviewAsync(reviewId, preserveEditorInput: true),
            () => !_isClosed && _document?.ReviewId == reviewId);
    }

    private async void ExportReviewJsonButton_Click(object sender, RoutedEventArgs e) =>
        await RunTrackedOperationAsync(
            "JSON 导出失败",
            () => ExportReviewAsync("json", ".json"),
            () => !_isClosed && _document is not null);

    private async void ExportReviewCsvButton_Click(object sender, RoutedEventArgs e) =>
        await RunTrackedOperationAsync(
            "CSV 导出失败",
            () => ExportReviewAsync("csv", ".csv"),
            () => !_isClosed && _document is not null);

    private async Task LoadCapturePageAsync(bool append)
    {
        if (_service is null)
        {
            return;
        }

        var cancellation = StartRequest(ref _captureListCancellation);
        var generation = _captureListGeneration.Begin();
        _isLoadingCaptureList = true;
        var cursor = append ? _captureCursor : null;
        CaptureListStatusText.Text = append ? "正在继续加载采集记录。" : "正在加载采集记录。";
        UpdateActionStates();
        try
        {
            var page = await _service.ListCapturesAsync(ReviewPageSize, cursor, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentCapturePage(generation))
            {
                return;
            }

            var selectedCaptureId = _selectedCapture?.CaptureId;
            _isUpdatingCapturePicker = true;
            try
            {
                if (!append)
                {
                    _captureItems.Clear();
                }

                foreach (var summary in page.Items)
                {
                    if (!_captureItems.Any(item => string.Equals(
                            item.Summary.CaptureId,
                            summary.CaptureId,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        _captureItems.Add(new SubjectiveCapturePickerItem(summary));
                    }
                }

                _captureCursor = page.NextCursor;
                var selectedItem = selectedCaptureId is null
                    ? null
                    : _captureItems.FirstOrDefault(item => string.Equals(
                        item.Summary.CaptureId,
                        selectedCaptureId,
                        StringComparison.OrdinalIgnoreCase));
                if (selectedItem is not null)
                {
                    CapturePicker.SelectedItem = selectedItem;
                }
                else if (!append && selectedCaptureId is not null)
                {
                    CapturePicker.SelectedItem = null;
                    _selectedCapture = null;
                    _captureImageGeneration.Invalidate();
                    CancelRequest(_captureImageCancellation);
                    OriginalCaptureImage.Source = null;
                    OriginalCaptureSummaryText.Text = "所选采集记录已不在当前列表中，请重新选择。";
                    CaptureTemplateStatusText.Text = "所选采集记录不在当前列表中。";
                    _isCaptureImageAvailable = false;
                }
            }
            finally
            {
                _isUpdatingCapturePicker = false;
            }

            UpdateDiagnostics(_captureDiagnostics, page.Diagnostics, append);
            SetCaptureDiagnostics(_captureDiagnostics);
            if (_captureItems.Count == 0 && _captureDiagnostics.Count == 0)
            {
                CaptureListStatusText.Text = page.NextCursor is null
                    ? "没有可用于主观题批阅的采集记录。请先到采集页导入或扫描原图。"
                    : "此页没有可用记录；可以继续加载下一页。";
            }
            else if (_captureDiagnostics.Count > 0)
            {
                CaptureListStatusText.Text = $"已载入 {_captureItems.Count} 条采集记录；有 {_captureDiagnostics.Count} 条记录无法读取，请查看下方诊断信息。";
            }
            else
            {
                CaptureListStatusText.Text = $"已载入 {_captureItems.Count} 条采集记录。";
            }

            if (_selectedCapture is null && _captureItems.Count > 0)
            {
                CapturePicker.SelectedItem = _captureItems[0];
            }
        }
        finally
        {
            FinishRequest(ref _captureListCancellation, cancellation);
            if (IsCurrentCapturePage(generation))
            {
                _isLoadingCaptureList = false;
                UpdateActionStates();
            }
        }
    }

    private async Task LoadReviewPageAsync(bool append)
    {
        if (_service is null)
        {
            return;
        }

        var cancellation = StartRequest(ref _reviewListCancellation);
        var generation = _reviewListGeneration.Begin();
        _isLoadingReviewList = true;
        var cursor = append ? _reviewCursor : null;
        ReviewListStatusText.Text = append ? "正在继续加载批阅记录。" : "正在加载批阅记录。";
        UpdateActionStates();
        try
        {
            var page = await _service.ListReviewsAsync(ReviewPageSize, cursor, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_reviewListGeneration.IsCurrent(generation))
            {
                return;
            }

            var selectedReviewId = (ReviewPicker.SelectedItem as SubjectiveReviewPickerItem)?.Summary.ReviewId;
            _isUpdatingReviewPicker = true;
            try
            {
                if (!append)
                {
                    _reviewItems.Clear();
                }

                foreach (var summary in page.Items)
                {
                    UpsertReviewItem(summary, select: false);
                }

                _reviewCursor = page.NextCursor;
                var selectedItem = selectedReviewId is null
                    ? null
                    : _reviewItems.FirstOrDefault(item => item.Summary.ReviewId == selectedReviewId.Value);
                if (selectedItem is not null)
                {
                    ReviewPicker.SelectedItem = selectedItem;
                }
            }
            finally
            {
                _isUpdatingReviewPicker = false;
            }

            UpdateDiagnostics(_reviewDiagnostics, page.Diagnostics, append);
            SetReviewDiagnostics(_reviewDiagnostics);
            if (_reviewItems.Count == 0 && _reviewDiagnostics.Count == 0)
            {
                ReviewListStatusText.Text = page.NextCursor is null
                    ? "没有已保存的主观题批阅记录。选择采集记录并定义区域后，可以创建第一条。"
                    : "此页没有可用记录；可以继续加载下一页。";
            }
            else if (_reviewDiagnostics.Count > 0)
            {
                ReviewListStatusText.Text = $"已载入 {_reviewItems.Count} 条批阅记录；有 {_reviewDiagnostics.Count} 条记录无法读取，请查看下方诊断信息。";
            }
            else
            {
                ReviewListStatusText.Text = $"已载入 {_reviewItems.Count} 条批阅记录。";
            }
        }
        finally
        {
            FinishRequest(ref _reviewListCancellation, cancellation);
            if (_reviewListGeneration.IsCurrent(generation))
            {
                _isLoadingReviewList = false;
                UpdateActionStates();
            }
        }
    }

    private async Task LoadCaptureImageAsync(LocalSubjectiveCaptureSummary capture)
    {
        if (_service is null)
        {
            return;
        }

        var generation = _captureImageGeneration.Begin();
        var cancellation = StartRequest(ref _captureImageCancellation);
        OriginalCaptureImage.Source = null;
        _isCaptureImageAvailable = false;
        OriginalCaptureSummaryText.Text = FormatCaptureSummary(capture) + " · 正在打开原图预览。";
        UpdateActionStates();

        try
        {
            var input = await _service.ReadCaptureImageAsync(capture.CaptureId, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentCaptureImage(generation, capture.CaptureId))
            {
                return;
            }

            if (input is not LocalInputImageFile localInput)
            {
                throw new LocalSubjectiveReviewException(
                    "CAPTURE_IMAGE_PREVIEW_UNAVAILABLE",
                    "这条采集记录的原图无法在本机预览；仍可查看已有批阅记录。请重新载入原图后重试。");
            }

            var uri = new Uri(Path.GetFullPath(localInput.Path), UriKind.Absolute);
            if (!File.Exists(localInput.Path))
            {
                throw new LocalSubjectiveReviewException(
                    "CAPTURE_IMAGE_NOT_FOUND",
                    "原图文件已不可用；仍可查看和导出已有批阅记录。请重新采集后重试。");
            }

            var bitmap = new BitmapImage(uri);
            bitmap.ImageOpened += (_, _) =>
            {
                if (!IsCurrentCaptureImage(generation, capture.CaptureId))
                {
                    return;
                }

                _isCaptureImageAvailable = true;
                OriginalCaptureSummaryText.Text = FormatCaptureSummary(capture) + " · 原图已载入。";
                UpdateActionStates();
            };
            bitmap.ImageFailed += (_, _) =>
            {
                if (!IsCurrentCaptureImage(generation, capture.CaptureId))
                {
                    return;
                }

                OriginalCaptureImage.Source = null;
                _isCaptureImageAvailable = false;
                OriginalCaptureSummaryText.Text = FormatCaptureSummary(capture) + " · 原图预览失败，仍可查看已有批阅记录。";
                UpdateActionStates();
                SetInfo(
                    "原图预览失败",
                    "无法显示所选采集记录的原图；已有批阅记录仍可读取和导出。错误代码：CAPTURE_IMAGE_PREVIEW_FAILED",
                    InfoBarSeverity.Warning);
            };
            OriginalCaptureImage.Source = bitmap;
        }
        catch (LocalSubjectiveReviewException)
        {
            if (IsCurrentCaptureImage(generation, capture.CaptureId))
            {
                OriginalCaptureImage.Source = null;
                _isCaptureImageAvailable = false;
                OriginalCaptureSummaryText.Text = FormatCaptureSummary(capture)
                    + " · 原图不可用；已有批阅记录仍可读取和导出。";
                UpdateActionStates();
            }

            throw;
        }
        finally
        {
            FinishRequest(ref _captureImageCancellation, cancellation);
        }
    }

    private async Task LoadQuestionImageAsync(SubjectiveGradingQuestion question)
    {
        if (_service is null || _document is null)
        {
            return;
        }

        var reviewId = _document.ReviewId;
        var generation = _questionImageGeneration.Begin();
        var cancellation = StartRequest(ref _questionImageCancellation);
        QuestionRegionImage.Source = null;
        QuestionImageStatusText.Text = "正在读取所选题目的区域图像。";
        try
        {
            var image = await _service.ReadQuestionImageAsync(reviewId, question.QuestionId, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentQuestionImage(generation, reviewId, question.QuestionId))
            {
                return;
            }

            var bitmap = await CreateBitmapFromPngAsync(image.Bytes, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentQuestionImage(generation, reviewId, question.QuestionId))
            {
                return;
            }

            QuestionRegionImage.Source = bitmap;
            QuestionImageStatusText.Text =
                $"第 {question.QuestionNumber} 题 · 区域 {question.Region.Width}×{question.Region.Height} 像素。";
        }
        catch (LocalSubjectiveReviewException exception)
        {
            if (IsCurrentQuestionImage(generation, reviewId, question.QuestionId))
            {
                QuestionRegionImage.Source = null;
                QuestionImageStatusText.Text =
                    $"题目区域图像不可用；已保存分数和批阅历史仍可查看。错误代码：{exception.Code}";
            }

            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (IsCurrentQuestionImage(generation, reviewId, question.QuestionId))
            {
                QuestionRegionImage.Source = null;
                QuestionImageStatusText.Text = "题目区域图像无法显示；已保存分数和批阅历史仍可查看。错误代码：SUBJECTIVE_IMAGE_PREVIEW_FAILED";
            }

            throw;
        }
        finally
        {
            FinishRequest(ref _questionImageCancellation, cancellation);
        }
    }

    private async Task CreateReviewAsync()
    {
        if (_isCreatingReview || _isMutatingReview || _isReadingReview)
        {
            return;
        }

        if (_service is null
            || _selectedCapture is not
            {
                SubjectiveQuestionCount: > 0 and <= SubjectiveGradingSnapshot.MaximumQuestionCount
            } capture
            || !_isCaptureImageAvailable)
        {
            ShowInfo("请选择有主观题区域且原图可用的采集记录。区域应在模板页定义。", InfoBarSeverity.Warning);
            return;
        }

        _isCreatingReview = true;
        UpdateActionStates();
        var captureId = capture.CaptureId;
        var cancellation = StartRequest(ref _mutationCancellation);
        try
        {
            var document = await _service.CreateAsync(captureId, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_isClosed || !string.Equals(_selectedCapture?.CaptureId, captureId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            UpdateReviewSummaryItem(document, select: true);
            SetDocument(document, preserveEditorInput: null);
            SetInfo("批阅记录已创建", $"已按关联模板载入 {document.Snapshot.Questions.Count} 道主观题，可以开始批阅。", InfoBarSeverity.Success);
        }
        finally
        {
            FinishRequest(ref _mutationCancellation, cancellation);
            _isCreatingReview = false;
            UpdateActionStates();
        }
    }

    private async Task LoadReviewAsync(Guid reviewId, bool preserveEditorInput)
    {
        if (_service is null)
        {
            return;
        }

        var generation = _documentGeneration.Begin();
        var cancellation = StartRequest(ref _documentCancellation);
        _isReadingReview = true;
        _questionImageGeneration.Invalidate();
        CancelRequest(_questionImageCancellation);
        QuestionRegionImage.Source = null;
        QuestionImageStatusText.Text = "正在读取批阅记录。";
        if (!preserveEditorInput)
        {
            _document = null;
            _selectedQuestion = null;
            _questionItems.Clear();
            ReviewEditorBorder.Visibility = Visibility.Collapsed;
        }

        UpdateActionStates();

        try
        {
            var document = await _service.ReadAsync(reviewId, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_documentGeneration.IsCurrent(generation)
                || (ReviewPicker.SelectedItem as SubjectiveReviewPickerItem)?.Summary.ReviewId != reviewId)
            {
                return;
            }

            var editorInput = preserveEditorInput ? CaptureEditorInput() : null;
            SetDocument(document, editorInput);
            UpdateReviewSummaryItem(document, select: false);
            if (document.Capture is not null)
            {
                AddOrSelectCapture(document.Capture);
                _ = RunTrackedOperationAsync(
                    "原图预览失败",
                    () => LoadCaptureImageAsync(document.Capture),
                    () => !_isClosed && _selectedCapture?.CaptureId == document.Capture!.CaptureId);
            }
            else
            {
                _isUpdatingCapturePicker = true;
                try
                {
                    CapturePicker.SelectedItem = null;
                }
                finally
                {
                    _isUpdatingCapturePicker = false;
                }

                _selectedCapture = null;
                _isCaptureImageAvailable = false;
                _captureImageGeneration.Invalidate();
                CancelRequest(_captureImageCancellation);
                OriginalCaptureImage.Source = null;
                OriginalCaptureSummaryText.Text = "原始采集图像已不可用；已保存分数、批阅历史和导出仍可使用。";
                CaptureTemplateStatusText.Text = "原始图像不可用；已有批阅记录仍可读取和导出。";
                UpdateActionStates();
                SetInfo(
                    "原图已不可用",
                    "该批阅记录仍可读取和导出，但无法创建新的批阅记录或查看题目图像。错误代码：CAPTURE_NOT_FOUND",
                    InfoBarSeverity.Warning);
            }
        }
        finally
        {
            FinishRequest(ref _documentCancellation, cancellation);
            if (_documentGeneration.IsCurrent(generation))
            {
                _isReadingReview = false;
                UpdateActionStates();
            }
        }
    }

    private async Task ApplyGradeAsync(SubjectiveReviewStatus status)
    {
        if (_service is null
            || _document is null
            || _selectedQuestion is null
            || _isCreatingReview
            || _isMutatingReview
            || _isReadingReview)
        {
            return;
        }

        var reviewer = ReviewerBox.Text.Trim();
        if (reviewer.Length is < 1 or > SubjectiveGradingSnapshot.MaximumReviewerLength)
        {
            ShowInfo("请输入批阅者，最多 64 个字符。", InfoBarSeverity.Warning);
            ReviewerBox.Focus(FocusState.Programmatic);
            return;
        }

        decimal? score = null;
        string? comment = null;
        if (status != SubjectiveReviewStatus.Unreviewed)
        {
            if (!SubjectiveReviewInputParser.TryParseScore(
                    QuestionScoreBox.Text,
                    _selectedQuestion.MaximumScore,
                    CultureInfo.CurrentCulture,
                    out var parsedScore))
            {
                ShowInfo("得分必须在 0 到本题满分之间；小数请使用当前语言的分隔符。", InfoBarSeverity.Warning);
                QuestionScoreBox.Focus(FocusState.Programmatic);
                return;
            }

            score = parsedScore;
            comment = NormalizeComment(QuestionCommentBox.Text);
            if (comment?.Length > SubjectiveGradingSnapshot.MaximumCommentLength)
            {
                ShowInfo("评语不能超过 256 个字符。", InfoBarSeverity.Warning);
                QuestionCommentBox.Focus(FocusState.Programmatic);
                return;
            }
        }

        if (status == SubjectiveReviewStatus.Confirmed
            && !SubjectiveReviewInputParser.CanConfirmDraft(
                _selectedQuestion.Status,
                _selectedQuestion.Score,
                _selectedQuestion.Comment,
                score!.Value,
                comment))
        {
            ShowInfo("确认前请先保存为草稿；确认时的分数和评语必须与已保存草稿一致。", InfoBarSeverity.Warning);
            UpdateGradeActions();
            return;
        }

        var currentDocument = _document;
        var selectedQuestion = _selectedQuestion;
        var questionId = selectedQuestion.QuestionId;
        var questionNumber = selectedQuestion.QuestionNumber;
        _isMutatingReview = true;
        var cancellation = StartRequest(ref _mutationCancellation);
        var edit = new SubjectiveGradeEdit(questionId, status, score, comment);
        UpdateActionStates();
        try
        {
            var updated = await _service.ApplyEditsAsync(
                currentDocument.ReviewId,
                currentDocument.Snapshot.Version,
                reviewer,
                [edit],
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_isClosed || _document?.ReviewId != currentDocument.ReviewId)
            {
                return;
            }

            var selectedQuestionId = _selectedQuestion?.QuestionId;
            var editorInput = CaptureEditorInput();
            SetDocument(updated, editorInput, selectedQuestionId);
            UpdateReviewSummaryItem(updated, select: false);
            var saved = updated.Snapshot.GetQuestion(questionNumber);
            var savedState = saved.Status switch
            {
                SubjectiveReviewStatus.Draft => "草稿已保存。",
                SubjectiveReviewStatus.Confirmed => "分数已确认。",
                SubjectiveReviewStatus.Unreviewed => "题目已重置为未批阅。",
                _ => "题目已更新。"
            };
            if (_selectedQuestion?.QuestionId == questionId)
            {
                GradeActionStatusText.Text = savedState;
            }

            SetInfo("批阅记录已保存", $"第 {questionNumber} 题：{savedState}", InfoBarSeverity.Success);
        }
        finally
        {
            FinishRequest(ref _mutationCancellation, cancellation);
            _isMutatingReview = false;
            UpdateActionStates();
        }
    }

    private async Task ExportReviewAsync(string format, string extension)
    {
        if (_service is null || _document is null || _saveTextAsync is null || _isExporting)
        {
            ShowInfo("导出功能暂不可用。错误代码：EXPORT_UNAVAILABLE", InfoBarSeverity.Warning);
            return;
        }

        var reviewId = _document.ReviewId;
        _isExporting = true;
        UpdateActionStates();
        var cancellation = StartRequest(ref _exportCancellation);
        ReviewExportStatusText.Text = $"正在准备 {format.ToUpperInvariant()} 导出。";
        try
        {
            var export = await _service.ExportAsync(reviewId, format, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_isClosed || _document?.ReviewId != reviewId)
            {
                return;
            }

            var fileName = $"omrina-subjective-review-{reviewId.ToString("N")[..8]}";
            var result = await _saveTextAsync(fileName, extension, export.Content, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_isClosed || _document?.ReviewId != reviewId)
            {
                return;
            }

            if (result.Cancelled)
            {
                ReviewExportStatusText.Text = "已取消导出。";
                return;
            }

            ReviewExportStatusText.Text = $"{format.ToUpperInvariant()} 文件已保存。";
            SetInfo("导出已保存", "文件已通过应用的保存对话框保存。", InfoBarSeverity.Success);
        }
        finally
        {
            FinishRequest(ref _exportCancellation, cancellation);
            _isExporting = false;
            UpdateActionStates();
        }
    }

    private void SetDocument(
        LocalSubjectiveReviewDocument document,
        EditorInput? preserveEditorInput,
        Guid? selectedQuestionId = null)
    {
        var previousQuestionId = selectedQuestionId
            ?? preserveEditorInput?.QuestionId
            ?? _selectedQuestion?.QuestionId;
        _document = document;
        _selectedQuestion = null;
        _isUpdatingQuestionList = true;
        try
        {
            _questionItems.Clear();
            foreach (var question in document.Snapshot.Questions)
            {
                _questionItems.Add(new SubjectiveQuestionPickerItem(question));
            }

            var selected = previousQuestionId is Guid questionId
                ? _questionItems.FirstOrDefault(item => item.Question.QuestionId == questionId)
                : null;
            selected ??= _questionItems.FirstOrDefault();
            SubjectiveQuestionList.SelectedItem = selected;
            _selectedQuestion = selected?.Question;
        }
        finally
        {
            _isUpdatingQuestionList = false;
        }

        ReviewEditorBorder.Visibility = Visibility.Visible;
        ReviewVersionText.Text = $"批阅记录版本：{document.Snapshot.Version} · 共 {document.Snapshot.Questions.Count} 题。";
        FinalSubtotalText.Text = document.Snapshot.IsFinal && document.Snapshot.FinalSubtotal is decimal subtotal
            ? $"所有题目均已确认 · 主观题小计 {subtotal.ToString(CultureInfo.CurrentCulture)} 分。"
            : "尚未全部确认，暂不显示最终主观题小计。";
        SelectedReviewSummaryText.Text = FormatReviewSummary(document.ReviewId, document.Snapshot);
        ReviewHistoryText.Text = FormatReviewHistory(document.Snapshot);

        if (preserveEditorInput is not null)
        {
            QuestionScoreBox.Text = preserveEditorInput.ScoreText;
            QuestionCommentBox.Text = preserveEditorInput.CommentText;
            ReviewerBox.Text = preserveEditorInput.ReviewerText;
            SelectedQuestionText.Text = _selectedQuestion is null
                ? "请选择一道题目。"
                : FormatSelectedQuestion(_selectedQuestion);
            RequestQuestionImage(_selectedQuestion);
        }
        else
        {
            PopulateEditor(_selectedQuestion);
            RequestQuestionImage(_selectedQuestion);
        }

        UpdateActionStates();
    }

    private void SelectQuestion(SubjectiveGradingQuestion question, bool preserveEditorInput)
    {
        _selectedQuestion = question;
        SelectedQuestionText.Text = FormatSelectedQuestion(question);
        if (!preserveEditorInput)
        {
            PopulateEditor(question);
        }

        RequestQuestionImage(question);
        UpdateGradeActions();
    }

    private void PopulateEditor(SubjectiveGradingQuestion? question)
    {
        if (question is null)
        {
            QuestionScoreBox.Text = string.Empty;
            QuestionCommentBox.Text = string.Empty;
            ReviewerBox.Text = string.Empty;
            SelectedQuestionText.Text = "请选择一道题目。";
            GradeActionStatusText.Text = "选择题目后填写得分、评语和批阅者。";
            return;
        }

        QuestionScoreBox.Text = question.Score?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        QuestionCommentBox.Text = question.Comment ?? string.Empty;
        ReviewerBox.Text = string.Empty;
        SelectedQuestionText.Text = FormatSelectedQuestion(question);
        GradeActionStatusText.Text = question.Status switch
        {
            SubjectiveReviewStatus.Unreviewed => "尚未批阅。填写得分、评语和批阅者后保存草稿。",
            SubjectiveReviewStatus.Draft => "草稿已保存。确认时必须保持草稿的分数和评语不变。",
            SubjectiveReviewStatus.Confirmed => "分数已确认。修改分数或评语前，请先保存为草稿。",
            _ => "题目状态无效。"
        };
    }

    private void RequestQuestionImage(SubjectiveGradingQuestion? question)
    {
        _questionImageGeneration.Invalidate();
        CancelRequest(_questionImageCancellation);
        QuestionRegionImage.Source = null;
        if (question is null || _document is null)
        {
            QuestionImageStatusText.Text = "选择一道题目后显示该区域图像。";
            UpdateGradeActions();
            return;
        }

        _ = RunTrackedOperationAsync(
            "题目区域图像读取失败",
            () => LoadQuestionImageAsync(question),
            () => !_isClosed && _document?.Snapshot.Questions.Any(item => item.QuestionId == question.QuestionId) == true
                && _selectedQuestion?.QuestionId == question.QuestionId);
    }

    private void UpdateActionStates()
    {
        var serviceAvailable = _service is not null && !_isClosed;
        ReloadCapturesButton.IsEnabled = serviceAvailable && !_isLoadingCaptureList;
        LoadMoreCapturesButton.IsEnabled = serviceAvailable && !_isLoadingCaptureList && _captureCursor is not null;
        ReloadReviewsButton.IsEnabled = serviceAvailable && !_isLoadingReviewList;
        LoadMoreReviewsButton.IsEnabled = serviceAvailable && !_isLoadingReviewList && _reviewCursor is not null;
        CreateReviewButton.IsEnabled = serviceAvailable
            && !_isCreatingReview
            && !_isMutatingReview
            && !_isReadingReview
            && _selectedCapture is { SubjectiveQuestionCount: > 0 and <= SubjectiveGradingSnapshot.MaximumQuestionCount }
            && _isCaptureImageAvailable;
        ExportReviewJsonButton.IsEnabled = serviceAvailable && !_isExporting && _document is not null && _saveTextAsync is not null;
        ExportReviewCsvButton.IsEnabled = serviceAvailable && !_isExporting && _document is not null && _saveTextAsync is not null;
        ReloadReviewButton.IsEnabled = serviceAvailable
            && !_isCreatingReview
            && !_isMutatingReview
            && !_isReadingReview
            && _document is not null;
        ReviewPicker.IsEnabled = serviceAvailable && !_isCreatingReview && !_isMutatingReview && !_isReadingReview;
        CapturePicker.IsEnabled = serviceAvailable && !_isCreatingReview;
        UpdateGradeActions();
    }

    private void UpdateGradeActions()
    {
        var question = _selectedQuestion;
        var canEdit = _service is not null
            && !_isClosed
            && !_isCreatingReview
            && !_isMutatingReview
            && !_isReadingReview
            && _document is not null
            && question is not null;
        var reviewer = ReviewerBox.Text.Trim();
        var validReviewer = reviewer.Length is >= 1 and <= SubjectiveGradingSnapshot.MaximumReviewerLength;
        var validScore = question is not null
            && SubjectiveReviewInputParser.TryParseScore(
                QuestionScoreBox.Text,
                question.MaximumScore,
                CultureInfo.CurrentCulture,
                out _);
        var normalizedComment = NormalizeComment(QuestionCommentBox.Text);
        var validComment = normalizedComment is null
            || normalizedComment.Length <= SubjectiveGradingSnapshot.MaximumCommentLength;
        var canSaveDraft = canEdit && validReviewer && validScore && validComment;
        if (canSaveDraft && question is not null
            && SubjectiveReviewInputParser.TryParseScore(
                QuestionScoreBox.Text,
                question.MaximumScore,
                CultureInfo.CurrentCulture,
                out var score))
        {
            canSaveDraft = question.Status != SubjectiveReviewStatus.Draft
                || question.Score != score
                || !string.Equals(question.Comment, normalizedComment, StringComparison.Ordinal);
        }

        SaveDraftButton.IsEnabled = canSaveDraft;
        var canConfirm = canEdit
            && validReviewer
            && validScore
            && validComment
            && question is not null
            && SubjectiveReviewInputParser.TryParseScore(
                QuestionScoreBox.Text,
                question.MaximumScore,
                CultureInfo.CurrentCulture,
                out var requestedScore)
            && SubjectiveReviewInputParser.CanConfirmDraft(
                question.Status,
                question.Score,
                question.Comment,
                requestedScore,
                normalizedComment);
        ConfirmScoreButton.IsEnabled = canConfirm;
        ResetQuestionButton.IsEnabled = canEdit
            && validReviewer
            && question is not null
            && question.Status != SubjectiveReviewStatus.Unreviewed;
        UpdatePagingButtons();
    }

    private void UpdatePagingButtons()
    {
        LoadMoreCapturesButton.IsEnabled = !_isClosed && !_isLoadingCaptureList && _captureCursor is not null;
        LoadMoreReviewsButton.IsEnabled = !_isClosed && !_isLoadingReviewList && _reviewCursor is not null;
    }

    private void SetCaptureDiagnostics(IReadOnlyList<LocalSubjectiveReviewDiagnostic> diagnostics)
    {
        SetDiagnostics(CaptureDiagnosticsText, diagnostics);
    }

    private void SetReviewDiagnostics(IReadOnlyList<LocalSubjectiveReviewDiagnostic> diagnostics)
    {
        SetDiagnostics(ReviewDiagnosticsText, diagnostics);
    }

    private static void SetDiagnostics(
        TextBlock target,
        IReadOnlyList<LocalSubjectiveReviewDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            target.Text = string.Empty;
            target.Visibility = Visibility.Collapsed;
            return;
        }

        var visible = diagnostics.Take(10)
            .Select(item => $"{item.ResourceId} · {item.Code}: {item.Message}")
            .ToList();
        if (diagnostics.Count > visible.Count)
        {
            visible.Add($"另有 {diagnostics.Count - visible.Count} 条诊断未显示。");
        }

        target.Text = string.Join(Environment.NewLine, visible);
        target.Visibility = Visibility.Visible;
    }

    private static void UpdateDiagnostics(
        List<LocalSubjectiveReviewDiagnostic> accumulated,
        IReadOnlyList<LocalSubjectiveReviewDiagnostic> incoming,
        bool append)
    {
        if (!append)
        {
            accumulated.Clear();
        }

        accumulated.AddRange(incoming);
    }

    private void UpsertReviewItem(LocalSubjectiveReviewSummary summary, bool select)
    {
        var newItem = new SubjectiveReviewPickerItem(summary);
        var index = -1;
        for (var candidate = 0; candidate < _reviewItems.Count; candidate++)
        {
            if (_reviewItems[candidate].Summary.ReviewId == summary.ReviewId)
            {
                index = candidate;
                break;
            }
        }

        var wasUpdatingPicker = _isUpdatingReviewPicker;
        _isUpdatingReviewPicker = true;
        try
        {
            if (index >= 0)
            {
                _reviewItems[index] = newItem;
            }
            else
            {
                _reviewItems.Add(newItem);
            }

            if (select)
            {
                ReviewPicker.SelectedItem = newItem;
            }
        }
        finally
        {
            _isUpdatingReviewPicker = wasUpdatingPicker;
        }

        UpdateActionStates();
    }

    private void UpdateReviewSummaryItem(LocalSubjectiveReviewDocument document, bool select)
    {
        var summary = new LocalSubjectiveReviewSummary(
            document.ReviewId,
            document.Snapshot.CaptureId,
            document.Snapshot.CreatedAtUtc,
            document.Snapshot.UpdatedAtUtc,
            document.Snapshot.Version,
            document.Snapshot.Questions.Count,
            document.Snapshot.IsFinal,
            document.Snapshot.FinalSubtotal);
        UpsertReviewItem(summary, select);
    }

    private void AddOrSelectCapture(LocalSubjectiveCaptureSummary summary)
    {
        var item = _captureItems.FirstOrDefault(existing => string.Equals(
            existing.Summary.CaptureId,
            summary.CaptureId,
            StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = new SubjectiveCapturePickerItem(summary);
            _captureItems.Add(item);
        }

        _isUpdatingCapturePicker = true;
        try
        {
            CapturePicker.SelectedItem = item;
        }
        finally
        {
            _isUpdatingCapturePicker = false;
        }

        _selectedCapture = summary;
        UpdateCaptureTemplateStatus(summary);
    }

    private void UpdateCaptureTemplateStatus(LocalSubjectiveCaptureSummary capture)
    {
        CaptureTemplateStatusText.Text = capture.SubjectiveQuestionCount switch
        {
            > 0 and <= SubjectiveGradingSnapshot.MaximumQuestionCount =>
                $"关联模板定义了 {capture.SubjectiveQuestionCount} 个主观题区域。载入原图后可按这些区域创建批阅记录。",
            > SubjectiveGradingSnapshot.MaximumQuestionCount =>
                "关联模板的主观题区域数量超出当前支持上限，无法创建批阅记录。错误代码：SUBJECTIVE_TEMPLATE_REGIONS_INVALID",
            _ =>
                "这条采集关联的模板没有主观题区域。请在“模板”页添加区域并重新采集；已有批阅记录仍可在下方重开。"
        };
    }

    private void ShowServiceFailure(string title, LocalSubjectiveReviewException exception)
    {
        if (exception.Code == "VERSION_CONFLICT")
        {
            ReloadReviewButton.IsEnabled = _document is not null && !_isClosed;
            SetInfo(
                "批阅记录已更新",
                $"{exception.Message} 你未提交的表单内容仍保留。请重新读取后检查最新记录。错误代码：{exception.Code}",
                InfoBarSeverity.Warning);
            return;
        }

        SetInfo(title, $"{exception.Message} 错误代码：{exception.Code}", InfoBarSeverity.Error);
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        SetInfo("主观题批阅", message, severity);
    }

    private void SetInfo(string title, string message, InfoBarSeverity severity)
    {
        if (_isClosed)
        {
            return;
        }

        SubjectiveInfoBar.Title = title;
        SubjectiveInfoBar.Message = message;
        SubjectiveInfoBar.Severity = severity;
        SubjectiveInfoBar.IsOpen = true;
    }

    private static string FormatCaptureSummary(LocalSubjectiveCaptureSummary capture)
    {
        var shortId = capture.CaptureId.Length <= 8 ? capture.CaptureId : capture.CaptureId[..8];
        return $"{capture.TemplateTitle} · {capture.PixelWidth}×{capture.PixelHeight} 像素 · "
            + $"{capture.CreatedAtUtc.ToLocalTime():g} · {FormatCaptureSource(capture.SourceType)} · {shortId}";
    }

    private static string FormatCaptureSource(string sourceType) => sourceType switch
    {
        "import" => "导入",
        "scan" => "扫描",
        _ => "来源未识别"
    };

    private static string FormatReviewSummary(Guid reviewId, SubjectiveGradingSnapshot snapshot)
    {
        var state = snapshot.IsFinal
            ? $"已确认 · 小计 {snapshot.FinalSubtotal?.ToString(CultureInfo.CurrentCulture) ?? "—"} 分"
            : "未全部确认";
        return $"批阅记录 {reviewId.ToString("N")[..8]} · 采集记录 {ShortId(snapshot.CaptureId)} · "
            + $"v{snapshot.Version} · {snapshot.Questions.Count} 题 · {state}";
    }

    private static string FormatSelectedQuestion(SubjectiveGradingQuestion question)
    {
        var status = question.Status switch
        {
            SubjectiveReviewStatus.Unreviewed => "未批阅",
            SubjectiveReviewStatus.Draft => "草稿",
            SubjectiveReviewStatus.Confirmed => "已确认",
            _ => "状态无效"
        };
        var score = question.Score?.ToString(CultureInfo.CurrentCulture) ?? "未评分";
        return $"第 {question.QuestionNumber} 题 · {status} · {score}/{question.MaximumScore.ToString(CultureInfo.CurrentCulture)} 分 · "
            + $"X={question.Region.X}, Y={question.Region.Y}, {question.Region.Width}×{question.Region.Height} 像素";
    }

    private static string FormatReviewHistory(SubjectiveGradingSnapshot snapshot)
    {
        if (snapshot.History.Count == 0)
        {
            return "尚无批阅历史。";
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            snapshot.History.Select(batch =>
            {
                var lines = new List<string>
                {
                    $"v{batch.Version} · {batch.TimestampUtc.ToLocalTime():g} · 批阅者：{batch.Reviewer}"
                };
                foreach (var change in batch.Changes)
                {
                    var action = change.Action switch
                    {
                        SubjectiveGradingAction.SetDraft => "保存草稿",
                        SubjectiveGradingAction.Confirm => "确认分数",
                        SubjectiveGradingAction.Reset => "重置题目",
                        _ => "状态变更"
                    };
                    lines.Add(
                        $"第 {change.QuestionNumber} 题 · {action}："
                        + $"{FormatGradeState(change.Before)} → {FormatGradeState(change.After)}");
                }

                return string.Join(Environment.NewLine, lines);
            }));
    }

    private static string FormatGradeState(SubjectiveGradeState state)
    {
        var status = state.Status switch
        {
            SubjectiveReviewStatus.Unreviewed => "未批阅",
            SubjectiveReviewStatus.Draft => "草稿",
            SubjectiveReviewStatus.Confirmed => "已确认",
            _ => "状态无效"
        };
        var score = state.Score?.ToString(CultureInfo.CurrentCulture) ?? "无分数";
        var comment = state.Comment is null ? "无评语" : $"评语：{state.Comment}";
        return $"{status} · {score} 分 · {comment}";
    }

    private static string ShortId(string value) => value.Length <= 8 ? value : value[..8];

    private static string? NormalizeComment(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private EditorInput CaptureEditorInput() => new(
        _selectedQuestion?.QuestionId,
        QuestionScoreBox.Text,
        QuestionCommentBox.Text,
        ReviewerBox.Text);

    private static async Task<BitmapImage> CreateBitmapFromPngAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(stream.GetOutputStreamAt(0));
        writer.WriteBytes(imageBytes);
        await writer.StoreAsync();
        await writer.FlushAsync();

        cancellationToken.ThrowIfCancellationRequested();
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();
        return bitmap;
    }

    private bool IsCurrentCapturePage(long generation) => _captureListGeneration.IsCurrent(generation);

    private bool IsCurrentCaptureImage(long generation, string captureId) =>
        !_isClosed
        && _captureImageGeneration.IsCurrent(generation)
        && string.Equals(_selectedCapture?.CaptureId, captureId, StringComparison.OrdinalIgnoreCase);

    private bool IsCurrentQuestionImage(long generation, Guid reviewId, Guid questionId) =>
        !_isClosed
        && _questionImageGeneration.IsCurrent(generation)
        && _document?.ReviewId == reviewId
        && _selectedQuestion?.QuestionId == questionId;

    private CancellationTokenSource StartRequest(ref CancellationTokenSource? current)
    {
        current?.Cancel();
        var next = CancellationTokenSource.CreateLinkedTokenSource(_pageLifetime.Token);
        current = next;
        return next;
    }

    private static void FinishRequest(ref CancellationTokenSource? current, CancellationTokenSource request)
    {
        if (ReferenceEquals(current, request))
        {
            current = null;
        }

        request.Dispose();
    }

    private static void CancelRequest(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void DisposeRequest(ref CancellationTokenSource? cancellation)
    {
        cancellation?.Dispose();
        cancellation = null;
    }

    private async Task RunTrackedOperationAsync(
        string failureTitle,
        Func<Task> operation,
        Func<bool>? canReportFailure = null)
    {
        if (_isClosed)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trackedTask = completion.Task;
        _activeOperations.Add(trackedTask);
        _activeOperationCount++;
        SubjectiveProgressBar.Visibility = Visibility.Visible;
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            if (!_isClosed && (canReportFailure?.Invoke() ?? true))
            {
                SetInfo("操作已取消", "操作已取消，当前批阅内容仍保留。", InfoBarSeverity.Informational);
            }
        }
        catch (LocalSubjectiveReviewException exception)
        {
            if (!_isClosed && (canReportFailure?.Invoke() ?? true))
            {
                ShowServiceFailure(failureTitle, exception);
            }
        }
        catch (CaptureException exception)
        {
            if (!_isClosed && (canReportFailure?.Invoke() ?? true))
            {
                SetInfo(failureTitle, $"{exception.Message} 错误代码：{exception.Code}", InfoBarSeverity.Error);
            }
        }
        catch (Exception exception)
        {
            if (!_isClosed && (canReportFailure?.Invoke() ?? true))
            {
                var rootCause = exception.GetBaseException();
                SetInfo(
                    failureTitle,
                    $"操作未完成，请稍后重试。调试信息：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。",
                    InfoBarSeverity.Error);
            }
        }
        finally
        {
            _activeOperationCount = Math.Max(0, _activeOperationCount - 1);
            _activeOperations.Remove(trackedTask);
            if (_activeOperationCount == 0)
            {
                SubjectiveProgressBar.Visibility = Visibility.Collapsed;
            }

            completion.TrySetResult();
        }
    }

    private sealed record EditorInput(
        Guid? QuestionId,
        string ScoreText,
        string CommentText,
        string ReviewerText);
}
