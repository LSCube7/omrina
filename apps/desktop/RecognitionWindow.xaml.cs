using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Omrina.Core;

namespace Omrina.Desktop;

/// <summary>Inputs passed from the capture context to the platform recognizer.</summary>
public sealed record RecognitionCaptureContext(
    CaptureRecord Capture,
    AnswerSheetLayout Layout,
    string ImagePath);

/// <summary>Progress reported by the local recognition adapter.</summary>
public sealed record RecognitionProgressUpdate(
    string Stage,
    int Completed,
    int Total,
    double? Fraction = null);

public delegate Task<RecognitionResult> LocalRecognitionRunner(
    RecognitionCaptureContext context,
    IProgress<RecognitionProgressUpdate>? progress,
    CancellationToken cancellationToken);

/// <summary>UI-neutral score data. The workflow adapter maps its immutable result to this view.</summary>
public sealed record RecognitionScoreQuestionView(
    int QuestionNumber,
    string OriginalAnswer,
    string FinalAnswer,
    string ExpectedAnswer,
    double? Score,
    bool NeedsReview);

public sealed record RecognitionScoreView(
    double TotalScore,
    double MaxScore,
    IReadOnlyList<RecognitionScoreQuestionView> Questions,
    string Summary,
    ScoringResult? Source = null);

public delegate Task<RecognitionScoreView?> LocalScoreRunner(
    AnswerSheetLayout layout,
    ManualReviewSession review,
    IReadOnlyDictionary<int, string> answerKey,
    CancellationToken cancellationToken);

public sealed record ManualReviewEdit(
    int QuestionNumber,
    string OriginalAnswer,
    string CorrectedAnswer,
    string Reason,
    string Reviewer,
    DateTimeOffset TimestampUtc);

public sealed record ManualReviewAuditView(
    int QuestionNumber,
    string OriginalAnswer,
    string CorrectedAnswer,
    string Reason,
    string Reviewer,
    DateTimeOffset TimestampUtc);

public sealed record RecognitionReviewView(
    RecognitionResult Result,
    ManualReviewSession Session,
    IReadOnlyList<ManualReviewAuditView> Audits,
    RecognitionScoreView? Score = null);

public delegate Task<RecognitionReviewView?> LocalReviewRunner(
    AnswerSheetLayout layout,
    ManualReviewSession session,
    IReadOnlyDictionary<int, string>? answerKey,
    IReadOnlyList<ManualReviewEdit> edits,
    CancellationToken cancellationToken);

public enum RecognitionExportFormat
{
    Json,
    Csv
}

public sealed record RecognitionExportRequest(
    CaptureRecord Capture,
    AnswerSheetLayout Layout,
    ManualReviewSession Review,
    RecognitionResult Result,
    IReadOnlyDictionary<int, string?> AnswerKey,
    RecognitionScoreView? Score,
    IReadOnlyList<ManualReviewAuditView> Audits,
    ScoringResult? ScoreResult);

public delegate Task<string?> LocalExportRunner(
    RecognitionExportRequest request,
    RecognitionExportFormat format,
    CancellationToken cancellationToken);

/// <summary>Editable display row that keeps machine output separate from user changes.</summary>
public sealed class RecognitionQuestionRow : INotifyPropertyChanged
{
    private string _expectedAnswer = "未设置";
    private string _reviewChoice;
    private string _reviewReason = string.Empty;

    public RecognitionQuestionRow(QuestionRecognitionResult result, int optionsPerQuestion)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        if (optionsPerQuestion is < AnswerSheetLayout.MinOptionsPerQuestion or > AnswerSheetLayout.MaxOptionsPerQuestion)
        {
            throw new ArgumentOutOfRangeException(nameof(optionsPerQuestion));
        }

        var labels = Enumerable.Range(0, optionsPerQuestion)
            .Select(index => ((char)('A' + index)).ToString())
            .ToArray();
        AnswerChoices = new[] { "未设置" }.Concat(labels).ToArray();
        ReviewChoices = new[] { "保持原识别", "清空" }.Concat(labels).ToArray();
        _expectedAnswer = AnswerChoices[0];
        _reviewChoice = ReviewChoices[0];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public QuestionRecognitionResult Result { get; }

    public string QuestionLabel => $"第 {Result.QuestionNumber} 题";

    public string StateText => Result.State switch
    {
        QuestionMarkState.Single => "单选",
        QuestionMarkState.Blank => "空白",
        QuestionMarkState.Multiple => "多选",
        QuestionMarkState.Uncertain => "不确定",
        _ => "未知状态"
    };

    public string SelectedText
    {
        get
        {
            var selected = Result.Options
                .Where(option => option.State == OptionMarkState.Selected)
                .Select(option => option.OptionLabel)
                .ToArray();
            var selectedText = selected.Length == 0 ? "未选" : string.Join("、", selected);
            var optionStates = Result.Options
                .OrderBy(option => option.OptionIndex)
                .Select(option => $"{option.OptionLabel}{FormatOptionState(option.State)}")
                .ToArray();
            return $"识别答案：{selectedText}\n选项状态：{string.Join("；", optionStates)}";
        }
    }

    public string ConfidenceText =>
        $"置信指标：{Result.Confidence.ToString("P0", CultureInfo.CurrentCulture)}（启发式）";

    public string ReviewText => NeedsReview
        ? "需要复核：请对照原图确认空白、多选或低置信结果。"
        : "可直接复核。";

    public bool NeedsReview => Result.State is not QuestionMarkState.Single
        || Result.Confidence < 0.75
        || Result.Options.Any(option => option.State == OptionMarkState.Uncertain);

    public IReadOnlyList<string> AnswerChoices { get; }

    public string ExpectedAnswer
    {
        get => _expectedAnswer;
        set => SetProperty(ref _expectedAnswer, value);
    }

    public IReadOnlyList<string> ReviewChoices { get; }

    public string ReviewChoice
    {
        get => _reviewChoice;
        set
        {
            if (SetProperty(ref _reviewChoice, value))
            {
                OnPropertyChanged(nameof(CorrectedAnswer));
            }
        }
    }

    public string ReviewReason
    {
        get => _reviewReason;
        set => SetProperty(ref _reviewReason, value);
    }

    public string OriginalAnswer => GetOriginalAnswer(Result);

    public string CorrectedAnswer => ReviewChoice switch
    {
        "保持原识别" => OriginalAnswer,
        "清空" => "",
        _ => ReviewChoice
    };

    public static string GetOriginalAnswer(QuestionRecognitionResult result)
    {
        var selected = result.Options
            .Where(option => option.State == OptionMarkState.Selected)
            .Select(option => option.OptionLabel)
            .ToArray();
        return selected.Length == 0 ? "" : string.Join("", selected);
    }

    public void ClearPendingReview()
    {
        ReviewChoice = ReviewChoices[0];
        ReviewReason = string.Empty;
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string FormatOptionState(OptionMarkState state) => state switch
    {
        OptionMarkState.Selected => "已选",
        OptionMarkState.Unselected => "未选",
        OptionMarkState.Uncertain => "不确定",
        _ => "未知"
    };
}

/// <summary>
/// Cached recognition/review page hosted by <see cref="MainWindow"/>.
/// The page owns only UI state; engine and workflow implementations are injected
/// as delegates so Core contracts stay independent from Uno and Desktop types.
/// </summary>
public sealed partial class RecognitionPage : Page
{
    private readonly LocalRecognitionRunner? _recognitionRunner;
    private readonly LocalScoreRunner? _scoreRunner;
    private readonly LocalReviewRunner? _reviewRunner;
    private readonly LocalExportRunner? _exportRunner;
    private readonly Func<
        string,
        string,
        string,
        CancellationToken,
        Task<FileSaveResult>>? _saveTextAsync;
    private readonly ObservableCollection<RecognitionQuestionRow> _questionRows = [];
    private readonly List<ManualReviewAuditView> _auditTrail = [];
    private CaptureRecord? _capture;
    private AnswerSheetLayout? _layout;
    private RecognitionResult? _recognitionResult;
    private ManualReviewSession? _reviewSession;
    private RecognitionScoreView? _score;
    private CancellationTokenSource? _recognitionCancellation;
    private CancellationTokenSource? _scoreCancellation;
    private CancellationTokenSource? _reviewCancellation;
    private TaskCompletionSource<bool>? _idleCompletion;
    private int _activeOperations;
    private bool _isClosed;

    public RecognitionPage()
        : this(null, null, null, null, null)
    {
    }

    public RecognitionPage(
        LocalRecognitionRunner? recognitionRunner,
        LocalScoreRunner? scoreRunner,
        LocalReviewRunner? reviewRunner,
        LocalExportRunner? exportRunner,
        Func<string, string, string, CancellationToken, Task<FileSaveResult>>? saveTextAsync)
    {
        InitializeComponent();
        _recognitionRunner = recognitionRunner;
        _scoreRunner = scoreRunner;
        _reviewRunner = reviewRunner;
        _exportRunner = exportRunner;
        _saveTextAsync = saveTextAsync;
        QuestionList.ItemsSource = _questionRows;
        RunRecognitionButton.IsEnabled = false;
        CancelRecognitionButton.IsEnabled = false;
        RecognitionResultsBorder.Visibility = Visibility.Collapsed;
        WorkflowBorder.Visibility = Visibility.Collapsed;
        ExportJsonButton.IsEnabled = false;
        ExportCsvButton.IsEnabled = false;
    }

    public CaptureRecord? CurrentCapture => _capture;

    public AnswerSheetLayout? CurrentLayout => _layout;

    public RecognitionResult? CurrentRecognitionResult => _recognitionResult;

    public Task WaitForIdleAsync() => _idleCompletion?.Task ?? Task.CompletedTask;

    /// <summary>Loads and validates the manifest-associated layout and original image.</summary>
    public void SetCapture(CaptureRecord capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        _recognitionCancellation?.Cancel();
        _scoreCancellation?.Cancel();
        _reviewCancellation?.Cancel();
        _capture = capture;
        _layout = null;
        _recognitionResult = null;
        _reviewSession = null;
        _score = null;
        _auditTrail.Clear();
        _questionRows.Clear();
        RecognitionResultsBorder.Visibility = Visibility.Collapsed;
        WorkflowBorder.Visibility = Visibility.Collapsed;
        ExportJsonButton.IsEnabled = false;
        ExportCsvButton.IsEnabled = false;
        ReviewAuditText.Text = string.Empty;
        WorkflowStatusText.Text = "请先完成识别。";
        RecognitionProgressBar.Visibility = Visibility.Collapsed;
        RecognitionProgressText.Text = "等待识别。";

        TemplateAssociationText.Text =
            $"{capture.Manifest.TemplateTitle} · {capture.Manifest.QuestionCount} 题 · "
            + $"每题 {capture.Manifest.OptionsPerQuestion} 项 · {capture.Manifest.TemplateId}";
        ImageMetadataText.Text =
            $"{capture.Manifest.PixelWidth}×{capture.Manifest.PixelHeight}，"
            + $"{capture.Manifest.ImageExtension.ToUpperInvariant()}，{capture.Manifest.SourceType}";
        ImagePathText.Text = capture.ImageFilePath;

        if (!TryResolveAssociatedLayout(capture, out var layout, out var associationMessage))
        {
            AssociationStatusText.Text = associationMessage;
            RunRecognitionButton.IsEnabled = false;
            SetInfo("模板关联无效", associationMessage, InfoBarSeverity.Error);
            OriginalImage.Source = null;
            return;
        }

        _layout = layout;
        AssociationStatusText.Text = "已验证 manifest、模板 schema、TemplateId 和原图路径。";
        SetInfo("已载入采集记录", "识别将使用 manifest 关联的模板；不会从像素或短编号猜测模板。", InfoBarSeverity.Informational);
        LoadOriginalImage(capture.ImageFilePath);
        RunRecognitionButton.IsEnabled = _recognitionRunner is not null && !_isClosed;
        if (_recognitionRunner is null)
        {
            RecognitionProgressText.Text = "本地识别适配器尚未连接。";
        }
    }

    /// <summary>Allows a host or test adapter to display an already computed result.</summary>
    public void SetRecognitionResult(RecognitionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_capture is null || _layout is null)
        {
            SetInfo("无法载入识别结果", "请先打开一条已验证的采集记录。", InfoBarSeverity.Error);
            return;
        }

        if (!string.Equals(result.TemplateId, _capture.Manifest.TemplateId, StringComparison.Ordinal)
            || result.TemplateSchemaVersion != _capture.Manifest.TemplateSchemaVersion)
        {
            SetInfo("识别结果关联不一致", "识别结果的模板身份或 schema 与采集 manifest 不一致，已拒绝显示。", InfoBarSeverity.Error);
            return;
        }

        var previousAnswerKey = _questionRows.ToDictionary(
            row => row.Result.QuestionNumber,
            row => row.ExpectedAnswer,
            EqualityComparer<int>.Default);
        _recognitionResult = result;
        _reviewSession = ManualReview.Start(result);
        _score = null;
        _auditTrail.Clear();
        ReviewAuditText.Text = string.Empty;
        _questionRows.Clear();
        foreach (var question in result.Questions.OrderBy(question => question.QuestionNumber))
        {
            var row = new RecognitionQuestionRow(question, _layout!.OptionsPerQuestion);
            if (previousAnswerKey.TryGetValue(question.QuestionNumber, out var expected))
            {
                row.ExpectedAnswer = expected;
            }

            _questionRows.Add(row);
        }

        RecognitionResultsBorder.Visibility = Visibility.Visible;
        WorkflowBorder.Visibility = Visibility.Visible;
        RecognitionSummaryText.Text = FormatSummary(result);
        RecognitionDiagnosticsText.Text = FormatDiagnostics(result.Diagnostics);
        WorkflowStatusText.Text = result.Status switch
        {
            RecognitionStatus.Accepted => "识别完成。请逐题设置标准答案后评分，或先应用人工修订。",
            RecognitionStatus.ReviewRequired => "识别完成但仍需复核；设置完整标准答案后可生成待复核评分。",
            _ => "当前识别结果已拒绝，不能评分；请检查原图和模板关联后重新识别。"
        };
        RecognitionProgressText.Text = "本地识别完成。";
        RecognitionProgressBar.Visibility = Visibility.Collapsed;
        SetInfo(
            result.Status == RecognitionStatus.Accepted ? "识别完成" : "识别需要复核",
            result.Status == RecognitionStatus.Accepted
                ? "题目结果已载入；置信指标是启发式质量指标，不代表准确率。"
                : "结果包含定位、方向或填涂诊断，请对照原图处理后再评分。",
            result.Status == RecognitionStatus.Accepted ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        UpdateWorkflowButtons();
    }

    public async Task CancelAndWaitAsync()
    {
        _isClosed = true;
        _recognitionCancellation?.Cancel();
        _scoreCancellation?.Cancel();
        _reviewCancellation?.Cancel();
        var idle = WaitForIdleAsync();
        await idle.ConfigureAwait(true);
    }

    private async void RunRecognitionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capture is null || _layout is null)
        {
            SetInfo("尚未选择采集记录", "请从采集页成功记录中打开识别和复核。", InfoBarSeverity.Warning);
            return;
        }

        if (_recognitionRunner is null)
        {
            SetInfo("本地识别不可用", "当前构建尚未提供本地识别适配器。请稍后重试。", InfoBarSeverity.Warning);
            return;
        }

        var activeCapture = _capture;
        var context = new RecognitionCaptureContext(activeCapture, _layout, activeCapture.ImageFilePath);
        _recognitionCancellation = new CancellationTokenSource();
        var cancellation = _recognitionCancellation;
        BeginOperation();
        SetRecognitionBusy(true);
        RecognitionProgressBar.Visibility = Visibility.Visible;
        RecognitionProgressBar.IsIndeterminate = true;
        RecognitionProgressText.Text = "正在准备本地识别。";

        try
        {
            var progress = new Progress<RecognitionProgressUpdate>(UpdateRecognitionProgress);
            var result = await _recognitionRunner(context, progress, cancellation.Token);
            if (!_isClosed && ReferenceEquals(_capture, activeCapture))
            {
                SetRecognitionResult(result);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isClosed)
            {
                RecognitionProgressText.Text = "识别已取消，未生成新的评分结果。";
                SetInfo("识别已取消", "你可以再次运行本地识别。", InfoBarSeverity.Informational);
            }
        }
        catch (Exception exception)
        {
            RecognitionProgressBar.Visibility = Visibility.Collapsed;
            RecognitionProgressText.Text = FormatFailure("本地识别失败", exception, "请检查原图和模板关联后重试。");
            SetInfo("本地识别失败", RecognitionProgressText.Text, InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_recognitionCancellation, cancellation))
            {
                _recognitionCancellation = null;
            }

            cancellation.Dispose();
            EndOperation();
            if (!_isClosed)
            {
                SetRecognitionBusy(false);
            }
        }
    }

    private void CancelRecognitionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recognitionCancellation is null)
        {
            return;
        }

        RecognitionProgressText.Text = "正在取消本地识别。";
        _recognitionCancellation.Cancel();
    }

    private async void ScoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recognitionResult is null || _scoreRunner is null)
        {
            WorkflowStatusText.Text = "评分服务尚未连接，请先完成识别并设置标准答案。";
            return;
        }

        if (!TryReadAnswerKey(out var answerKey, out var error))
        {
            WorkflowStatusText.Text = error;
            return;
        }

        _scoreCancellation = new CancellationTokenSource();
        var cancellation = _scoreCancellation;
        BeginOperation();
        ScoreButton.IsEnabled = false;
        UpdateWorkflowButtons();
        WorkflowStatusText.Text = "正在按用户提供的标准答案评分。";
        try
        {
            var score = await _scoreRunner(_layout!, _reviewSession!, answerKey, cancellation.Token);
            _score = score;
            WorkflowStatusText.Text = score is null
                ? "评分没有返回结果。"
                : $"评分完成：{score.Summary}";
            SetInfo(
                "评分完成",
                "评分使用的是你逐题设置的标准答案，未把本张填涂自动当作答案。"
                + (score?.Source?.IsProvisional == true ? " 当前结果仍有题目需要复核。" : string.Empty),
                score?.Source?.IsProvisional == true ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            WorkflowStatusText.Text = "评分已取消。";
        }
        catch (Exception exception)
        {
            WorkflowStatusText.Text = FormatFailure("评分失败", exception, "请检查标准答案后重试。");
            SetInfo("评分失败", WorkflowStatusText.Text, InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_scoreCancellation, cancellation))
            {
                _scoreCancellation = null;
            }

            cancellation.Dispose();
            EndOperation();
            if (!_isClosed)
            {
                UpdateWorkflowButtons();
            }
        }
    }

    private async void ApplyReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recognitionResult is null || _reviewRunner is null)
        {
            WorkflowStatusText.Text = "人工复核服务尚未连接。";
            return;
        }

        if (!TryReadManualEdits(out var edits, out var error))
        {
            WorkflowStatusText.Text = error;
            return;
        }

        _reviewCancellation = new CancellationTokenSource();
        var cancellation = _reviewCancellation;
        BeginOperation();
        ApplyReviewButton.IsEnabled = false;
        UpdateWorkflowButtons();
        WorkflowStatusText.Text = "正在保存人工修订和审计信息。";
        try
        {
            var hasAnswerKey = TryReadAnswerKey(out var answerKey, out _);
            var review = await _reviewRunner(
                _layout!,
                _reviewSession!,
                hasAnswerKey ? answerKey : null,
                edits,
                cancellation.Token);
            if (review is null)
            {
                WorkflowStatusText.Text = "人工修订没有返回结果。";
            }
            else
            {
                _reviewSession = review.Session;
                _auditTrail.Clear();
                _auditTrail.AddRange(review.Audits);
                _score = review.Score;
                foreach (var row in _questionRows)
                {
                    row.ClearPendingReview();
                }

                RenderAuditTrail();
                WorkflowStatusText.Text =
                    $"已应用 {_auditTrail.Count} 条人工修订；原识别结果仍保留在审计信息中。";
                SetInfo("人工修订已保存", "后续导出会同时保留原识别、修订结果和修订原因。", InfoBarSeverity.Success);
            }
        }
        catch (OperationCanceledException)
        {
            WorkflowStatusText.Text = "人工修订已取消，原识别结果未改变。";
        }
        catch (Exception exception)
        {
            WorkflowStatusText.Text = FormatFailure("人工修订失败", exception, "请检查修订原因后重试。");
            SetInfo("人工修订失败", WorkflowStatusText.Text, InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_reviewCancellation, cancellation))
            {
                _reviewCancellation = null;
            }

            cancellation.Dispose();
            EndOperation();
            if (!_isClosed)
            {
                UpdateWorkflowButtons();
            }
        }
    }

    private async void ExportJsonButton_Click(object sender, RoutedEventArgs e)
    {
        await ExportAsync(RecognitionExportFormat.Json);
    }

    private async void ExportCsvButton_Click(object sender, RoutedEventArgs e)
    {
        await ExportAsync(RecognitionExportFormat.Csv);
    }

    private async Task ExportAsync(RecognitionExportFormat format)
    {
        if (_capture is null
            || _layout is null
            || _recognitionResult is null
            || _reviewSession is null
            || _exportRunner is null
            || _saveTextAsync is null)
        {
            ExportStatusText.Text = "请先完成识别，并确认本地导出和文件保存服务可用。";
            return;
        }

        if (_questionRows.Any(row => !string.Equals(row.ReviewChoice, "保持原识别", StringComparison.Ordinal)))
        {
            ExportStatusText.Text = "请先应用或清除待提交的人工修订，再导出结果。";
            return;
        }

        try
        {
            var request = CreateExportRequest();
            var content = await _exportRunner(request, format, CancellationToken.None);
            if (content is null)
            {
                ExportStatusText.Text = "导出服务没有返回内容。";
                return;
            }

            var extension = format == RecognitionExportFormat.Json ? ".json" : ".csv";
            var saveResult = await _saveTextAsync(
                $"omrina-recognition-{_capture.Manifest.CaptureId[..Math.Min(12, _capture.Manifest.CaptureId.Length)]}",
                extension,
                content,
                CancellationToken.None);
            ExportStatusText.Text = saveResult.Cancelled
                ? "已取消导出，未写入文件。"
                : $"已导出 {format.ToString().ToUpperInvariant()}：{saveResult.Path ?? "已选择文件"}";
        }
        catch (Exception exception)
        {
            ExportStatusText.Text = FormatFailure("导出失败", exception, "请重新选择保存位置后重试。");
        }
    }

    private void ExpectedAnswer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_score is not null)
        {
            _score = null;
            WorkflowStatusText.Text = "标准答案已更改，请重新评分后再导出当前结果。";
        }

        UpdateWorkflowButtons();
    }

    private void ReviewChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateWorkflowButtons();
    }

    private void UpdateRecognitionProgress(RecognitionProgressUpdate update)
    {
        if (_isClosed)
        {
            return;
        }

        var total = Math.Max(0, update.Total);
        var completed = Math.Clamp(update.Completed, 0, Math.Max(total, update.Completed));
        RecognitionProgressText.Text = total > 0
            ? $"{update.Stage}（{completed}/{total}）"
            : update.Stage;
        if (update.Fraction is double fraction && double.IsFinite(fraction))
        {
            RecognitionProgressBar.IsIndeterminate = false;
            RecognitionProgressBar.Value = Math.Clamp(fraction * 100, 0, 100);
        }
        else
        {
            RecognitionProgressBar.IsIndeterminate = true;
        }
    }

    private void SetRecognitionBusy(bool busy)
    {
        RunRecognitionButton.IsEnabled = !busy && !_isClosed && _recognitionRunner is not null && _layout is not null;
        CancelRecognitionButton.IsEnabled = busy && !_isClosed;
        QuestionList.IsEnabled = !busy
            && !_isClosed
            && _scoreCancellation is null
            && _reviewCancellation is null;
    }

    private void UpdateWorkflowButtons()
    {
        var hasResult = _recognitionResult is not null;
        var hasPendingReviewEdits = _questionRows.Any(
            row => !string.Equals(row.ReviewChoice, "保持原识别", StringComparison.Ordinal));
        var workflowBusy = _scoreCancellation is not null || _reviewCancellation is not null;
        QuestionList.IsEnabled = !_isClosed && _recognitionCancellation is null && !workflowBusy;
        var answerKeyComplete = TryReadAnswerKey(out _, out _);
        ScoreButton.IsEnabled = !_isClosed
            && _recognitionCancellation is null
            && hasResult
            && _recognitionResult!.Status != RecognitionStatus.Rejected
            && !hasPendingReviewEdits
            && _reviewCancellation is null
            && answerKeyComplete
            && _scoreRunner is not null
            && _scoreCancellation is null;
        ApplyReviewButton.IsEnabled = !_isClosed
            && _recognitionCancellation is null
            && hasResult
            && _recognitionResult!.Status != RecognitionStatus.Rejected
            && _reviewRunner is not null
            && _scoreCancellation is null
            && _reviewCancellation is null
            && hasPendingReviewEdits;
        var canExport = !_isClosed
            && _recognitionCancellation is null
            && hasResult
            && _exportRunner is not null
            && _saveTextAsync is not null
            && !workflowBusy
            && !hasPendingReviewEdits;
        ExportJsonButton.IsEnabled = canExport;
        ExportCsvButton.IsEnabled = canExport;
    }

    private bool TryReadAnswerKey(
        out IReadOnlyDictionary<int, string> answerKey,
        out string error)
    {
        var values = new Dictionary<int, string>();
        if (_layout is null || _questionRows.Count != _layout.QuestionCount)
        {
            answerKey = values;
            error = "识别结果未覆盖模板的全部题目，不能评分。";
            return false;
        }

        foreach (var row in _questionRows.OrderBy(row => row.Result.QuestionNumber))
        {
            if (row.ExpectedAnswer is "未设置" or null || row.ExpectedAnswer.Length != 1)
            {
                answerKey = values;
                error = $"请设置第 {row.Result.QuestionNumber} 题的标准答案。";
                return false;
            }

            values[row.Result.QuestionNumber] = row.ExpectedAnswer;
        }

        answerKey = values;
        error = string.Empty;
        return true;
    }

    private bool TryReadManualEdits(out IReadOnlyList<ManualReviewEdit> edits, out string error)
    {
        var values = new List<ManualReviewEdit>();
        foreach (var row in _questionRows.Where(row => !string.Equals(row.ReviewChoice, "保持原识别", StringComparison.Ordinal)))
        {
            if (string.IsNullOrWhiteSpace(row.ReviewReason))
            {
                edits = values;
                error = $"请填写第 {row.Result.QuestionNumber} 题的人工修订原因。";
                return false;
            }

            values.Add(new ManualReviewEdit(
                row.Result.QuestionNumber,
                _reviewSession?.CurrentAnswers.TryGetValue(row.Result.QuestionNumber, out var currentAnswer) == true
                    ? currentAnswer.AnswerLabel ?? string.Empty
                    : row.OriginalAnswer,
                row.CorrectedAnswer,
                row.ReviewReason.Trim(),
                "本地用户",
                DateTimeOffset.UtcNow));
        }

        if (values.Count == 0)
        {
            edits = values;
            error = "请先选择需要修改的题目。";
            return false;
        }

        edits = values;
        error = string.Empty;
        return true;
    }

    private RecognitionExportRequest CreateExportRequest()
    {
        var answerKey = _questionRows.ToDictionary(
            row => row.Result.QuestionNumber,
            row => row.ExpectedAnswer is "未设置" ? null : row.ExpectedAnswer,
            EqualityComparer<int>.Default);
        return new RecognitionExportRequest(
            _capture!,
            _layout!,
            _reviewSession!,
            _recognitionResult!,
            answerKey,
            _score,
            _auditTrail.ToArray(),
            _score?.Source);
    }

    private void LoadOriginalImage(string path)
    {
        try
        {
            OriginalImage.Source = File.Exists(path)
                ? new BitmapImage(new Uri(Path.GetFullPath(path), UriKind.Absolute))
                : null;
        }
        catch (Exception exception)
        {
            OriginalImage.Source = null;
            SetInfo("原图预览不可用", FormatFailure("原图预览失败", exception, "仍可使用题目结果和本地文件进行复核。"), InfoBarSeverity.Warning);
        }
    }

    private static bool TryResolveAssociatedLayout(
        CaptureRecord capture,
        out AnswerSheetLayout? layout,
        out string message)
    {
        layout = null;
        var manifest = capture.Manifest;
        if (manifest.TemplateSchemaVersion != AnswerSheetLayout.TemplateSchemaVersion)
        {
            message = $"模板 schema {manifest.TemplateSchemaVersion} 与当前支持的 {AnswerSheetLayout.TemplateSchemaVersion} 不一致。";
            return false;
        }

        if (!File.Exists(capture.ImageFilePath))
        {
            message = "采集原图不存在，无法运行本地识别。";
            return false;
        }

        try
        {
            var resolved = AnswerSheetLayout.Create(
                manifest.TemplateTitle,
                manifest.QuestionCount,
                manifest.OptionsPerQuestion);
            if (!string.Equals(resolved.TemplateId, manifest.TemplateId, StringComparison.Ordinal))
            {
                message = "manifest 中的 TemplateId 与可重建布局不一致，已停止识别。";
                return false;
            }

            layout = resolved;
            message = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            message = $"关联布局无法重建：{exception.GetBaseException().GetType().Name}。";
            return false;
        }
    }

    private static string FormatSummary(RecognitionResult result)
    {
        var status = result.Status switch
        {
            RecognitionStatus.Accepted => "已接受",
            RecognitionStatus.ReviewRequired => "需要复核",
            RecognitionStatus.Rejected => "已拒绝",
            _ => "未知"
        };
        var orientation = result.Orientation switch
        {
            PageOrientation.Degrees0 => "0°",
            PageOrientation.Degrees90 => "90°",
            PageOrientation.Degrees180 => "180°",
            PageOrientation.Degrees270 => "270°",
            _ => "未知"
        };
        return $"状态：{status}；方向：{orientation}；题目：{result.Questions.Count}；"
            + $"置信指标：{result.Confidence.ToString("P0", CultureInfo.CurrentCulture)}（启发式，不是准确率）。";
    }

    private static string FormatDiagnostics(IReadOnlyList<RecognitionDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return "诊断：无。";
        }

        return "诊断：\n" + string.Join(
            "\n",
            diagnostics.Select(diagnostic => $"· [{diagnostic.Severity}] {diagnostic.Code}：{diagnostic.Message}"));
    }

    private void RenderAuditTrail()
    {
        ReviewAuditText.Text = _auditTrail.Count == 0
            ? string.Empty
            : "人工修订审计：\n" + string.Join(
                "\n",
                _auditTrail.Select(audit =>
                    $"· 第 {audit.QuestionNumber} 题：{DisplayAnswer(audit.OriginalAnswer)} → "
                    + $"{DisplayAnswer(audit.CorrectedAnswer)}；{audit.Reason}（{audit.Reviewer}，{audit.TimestampUtc:O}）"));
    }

    private void BeginOperation()
    {
        _activeOperations++;
        _idleCompletion ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void EndOperation()
    {
        _activeOperations = Math.Max(0, _activeOperations - 1);
        if (_activeOperations == 0)
        {
            _idleCompletion?.TrySetResult(true);
            _idleCompletion = null;
        }
    }

    private void SetInfo(string title, string message, InfoBarSeverity severity)
    {
        RecognitionInfoBar.Title = title;
        RecognitionInfoBar.Message = message;
        RecognitionInfoBar.Severity = severity;
        RecognitionInfoBar.IsOpen = true;
    }

    private static string DisplayAnswer(string answer) => string.IsNullOrEmpty(answer) ? "空白" : answer;

    private static string FormatFailure(string operation, Exception exception, string nextStep)
    {
        var rootCause = exception.GetBaseException();
        return $"{operation}：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。{nextStep}";
    }
}
