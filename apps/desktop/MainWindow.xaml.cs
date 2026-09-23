using Omrina.Core;
using Omrina.Platform;
using Omrina.Scanning;
using Omrina.Server;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Omrina.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly LoopbackHealthServer _healthServer = new();
    private readonly CaptureStore _captureStore = new();
    private readonly IScannerService _scannerService;
    private readonly IAnswerSheetRecognitionService _recognitionService;
    private readonly IFileDialogService _fileDialogService;
    private readonly TemplatePrintController? _printController;
    private readonly StatusPage _statusPage;
    private readonly TemplatePage _templatePage;
    private readonly CapturePage _capturePage;
    private readonly RecognitionPage _recognitionPage;
    private readonly SettingsPage _settingsPage;
    private readonly AboutPage _aboutPage;
    private bool _isClosed;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _statusPage = new StatusPage();
        _settingsPage = new SettingsPage();
        _aboutPage = new AboutPage();
        _fileDialogService = DesktopPlatformFactory.CreateFileDialogs(this);
        _scannerService = DesktopPlatformFactory.CreateScanner(_captureStore.RootDirectory);
        _recognitionService = new SkiaAnswerSheetRecognitionService();
        _printController = CreatePrintController();
        _templatePage = new TemplatePage(
            SaveSvgAsync,
            _printController is null ? null : PrintAsync,
            () => _printController?.IsBusy == true);
        _capturePage = new CapturePage(
            _scannerService,
            PickImageAsync,
            OpenScanImageAsync,
            PersistCaptureAsync,
            DesktopPlatformFactory.DeleteTemporaryScanFile);
        _recognitionPage = new RecognitionPage(
            recognitionRunner: RecognizeCaptureAsync,
            scoreRunner: ScoreRecognitionAsync,
            reviewRunner: ApplyReviewAsync,
            exportRunner: ExportResultAsync,
            saveTextAsync: SaveTextAsync);
        _templatePage.CaptureRequested += TemplatePage_CaptureRequested;
        _capturePage.RecognitionRequested += CapturePage_RecognitionRequested;

        try
        {
            var latestCapture = _captureStore.LoadLatest();
            if (latestCapture is not null)
            {
                _capturePage.SetExistingCapture(latestCapture);
            }
        }
        catch (CaptureException exception)
        {
            _capturePage.SetExistingCaptureLoadError(exception.Code, exception.Message);
        }
        catch (Exception exception)
        {
            var rootCause = exception.GetBaseException();
            _capturePage.SetExistingCaptureLoadError(
                "CAPTURE_HISTORY_READ_FAILED",
                $"{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。");
        }

        NavigationFrame.Content = _statusPage;
        AppNavigationView.SelectedItem = AppNavigationView.MenuItems[0];
        Closed += MainWindow_Closed;
    }

    public async Task StartHealthServerAsync()
    {
        try
        {
            await _healthServer.StartAsync();
            if (!_isClosed)
            {
                _statusPage.SetConnectionStatus(
                    $"本地连接服务已就绪： http://127.0.0.1:{LoopbackHealthServer.Port}/health");
            }
        }
        catch (Exception exception)
        {
            if (!_isClosed)
            {
                var rootCause = exception.GetBaseException();
                _statusPage.SetConnectionStatus(
                    $"本地连接服务启动失败：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。模板和采集仍可使用。");
            }
        }
    }

    private void AppNavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        switch (tag)
        {
            case "status":
                NavigateTo(_statusPage, "状态");
                break;
            case "template":
                NavigateTo(_templatePage, "模板");
                break;
            case "capture":
                NavigateTo(_capturePage, "采集");
                break;
            case "recognition":
            {
                var latestCapture = _capturePage.LatestCapture;
                if (latestCapture is not null
                    && !ReferenceEquals(_recognitionPage.CurrentCapture, latestCapture))
                {
                    _recognitionPage.SetCapture(latestCapture);
                }

                NavigateTo(_recognitionPage, "识别 / 复核");
                break;
            }
            case "settings":
                NavigateTo(_settingsPage, "设置");
                break;
            case "about":
                NavigateTo(_aboutPage, "关于");
                break;
        }
    }

    private void NavigateTo(Page page, string title)
    {
        if (!ReferenceEquals(NavigationFrame.Content, page))
        {
            NavigationFrame.Content = page;
        }

        TitleBarPageText.Text = title;
    }

    private void TemplatePage_CaptureRequested(AnswerSheetLayout layout)
    {
        _capturePage.SetLayout(layout);
        SelectNavigationItem("capture");
    }

    private void CapturePage_RecognitionRequested(object? sender, CaptureRecord capture)
    {
        _recognitionPage.SetCapture(capture);
        SelectNavigationItem("recognition");
    }

    private void SelectNavigationItem(string tag)
    {
        foreach (var item in AppNavigationView.MenuItems.OfType<NavigationViewItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                AppNavigationView.SelectedItem = item;
                return;
            }
        }
    }

    private TemplatePrintController? CreatePrintController()
    {
        try
        {
            return new TemplatePrintController(
                this,
                message => _templatePage?.ReportPlatformStatus(message));
        }
        catch (Exception exception)
        {
            var rootCause = exception.GetBaseException();
            _statusPage?.SetConnectionStatus(
                $"系统打印不可用：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。模板仍可保存和导入。");
            return null;
        }
    }

    private async Task PrintAsync(AnswerSheetLayout layout)
    {
        if (_printController is null)
        {
            throw new InvalidOperationException("系统打印服务不可用。");
        }

        await _printController.RequestPrintAsync(layout);
    }

    private async Task<SvgSaveResult> SaveSvgAsync(AnswerSheetLayout layout)
    {
        var result = await _fileDialogService.SaveTextAsync(
            "omrina-template",
            ".svg",
            layout.ToSvg());
        return new SvgSaveResult(result.Cancelled, result.Path);
    }

    private Task<FileSaveResult> SaveTextAsync(
        string suggestedFileName,
        string extension,
        string content,
        CancellationToken cancellationToken)
    {
        return _fileDialogService.SaveTextAsync(
            suggestedFileName,
            extension,
            content,
            cancellationToken);
    }

    private async Task<RecognitionResult> RecognizeCaptureAsync(
        RecognitionCaptureContext context,
        IProgress<RecognitionProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new RecognitionProgressUpdate("正在解码原图并准备识别", 0, 0));
        var result = await _recognitionService.RecognizeAsync(
            context.Layout,
            new LocalInputImageFile(context.ImagePath),
            cancellationToken);
        progress?.Report(new RecognitionProgressUpdate(
            "识别结果已生成",
            result.Questions.Count,
            result.Questions.Count,
            1));
        return result;
    }

    private static Task<RecognitionScoreView?> ScoreRecognitionAsync(
        AnswerSheetLayout layout,
        ManualReviewSession review,
        IReadOnlyDictionary<int, string> answerKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = AnswerKey.Create(layout.QuestionCount, layout.OptionsPerQuestion, answerKey);
        var scoring = ScoringEngine.Score(review, key);
        return Task.FromResult<RecognitionScoreView?>(CreateScoreView(scoring));
    }

    private static Task<RecognitionReviewView?> ApplyReviewAsync(
        AnswerSheetLayout layout,
        ManualReviewSession session,
        IReadOnlyDictionary<int, string>? answerKey,
        IReadOnlyList<ManualReviewEdit> edits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var review = session;
        foreach (var edit in edits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            review = string.IsNullOrEmpty(edit.CorrectedAnswer)
                ? review.ConfirmBlank(
                    edit.QuestionNumber,
                    edit.Reviewer,
                    edit.Reason,
                    edit.TimestampUtc)
                : review.SetAnswer(
                    edit.QuestionNumber,
                    edit.CorrectedAnswer,
                    edit.Reviewer,
                    edit.Reason,
                    edit.TimestampUtc);
        }

        AnswerKey? key = null;
        if (answerKey is not null)
        {
            key = AnswerKey.Create(layout.QuestionCount, layout.OptionsPerQuestion, answerKey);
        }

        // Keep a score snapshot even when the user has not supplied an answer key;
        // ResultExporter then retains the immutable review history in recognition-only exports.
        var scoring = ScoringEngine.Score(review, key);
        var audits = review.History
            .Select(change => new ManualReviewAuditView(
                change.QuestionNumber,
                change.Before.AnswerLabel ?? string.Empty,
                change.After.AnswerLabel ?? string.Empty,
                change.Reason ?? string.Empty,
                change.Reviewer,
                change.Timestamp))
            .ToArray();
        return Task.FromResult<RecognitionReviewView?>(
            new RecognitionReviewView(review.OriginalRecognition, review, audits, CreateScoreView(scoring)));
    }

    private static Task<string?> ExportResultAsync(
        RecognitionExportRequest request,
        RecognitionExportFormat format,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AnswerKey? answerKey = null;
        if (Enumerable.Range(1, request.Layout.QuestionCount).All(
                questionNumber => request.AnswerKey.TryGetValue(questionNumber, out var answer)
                    && !string.IsNullOrWhiteSpace(answer)))
        {
            var answerValues = Enumerable.Range(1, request.Layout.QuestionCount)
                .ToDictionary(questionNumber => questionNumber, questionNumber => request.AnswerKey[questionNumber]!);
            answerKey = AnswerKey.Create(
                request.Layout.QuestionCount,
                request.Layout.OptionsPerQuestion,
                answerValues);
        }

        var scoring = ScoringEngine.Score(request.Review, answerKey);
        var coreFormat = format == RecognitionExportFormat.Json
            ? Omrina.Core.ResultExportFormat.Json
            : Omrina.Core.ResultExportFormat.Csv;
        return Task.FromResult<string?>(ResultExporter.Export(scoring, coreFormat, indented: true));
    }

    private static RecognitionScoreView CreateScoreView(ScoringResult scoring)
    {
        var originalAnswers = scoring.Review?.OriginalRecognition.Questions
            .ToDictionary(
                question => question.QuestionNumber,
                RecognitionQuestionRow.GetOriginalAnswer)
            ?? new Dictionary<int, string>();
        var questions = scoring.QuestionScores
            .OrderBy(question => question.QuestionNumber)
            .Select(question => new RecognitionScoreQuestionView(
                question.QuestionNumber,
                originalAnswers.TryGetValue(question.QuestionNumber, out var originalAnswer)
                    ? originalAnswer
                    : question.RecognizedAnswer ?? string.Empty,
                question.RecognizedAnswer ?? string.Empty,
                question.ExpectedAnswer ?? string.Empty,
                (double)question.EarnedPoints,
                question.RequiresReview))
            .ToArray();
        var summary = scoring.IsScored
            ? $"{scoring.TotalScore:0.##}/{scoring.MaximumScore:0.##} 分（{FormatScoringDisposition(scoring.Disposition)}）"
            : $"未评分：{FormatUnavailableReason(scoring.UnavailableReason)}";
        return new RecognitionScoreView(
            scoring.TotalScore is decimal total ? (double)total : 0,
            scoring.MaximumScore is decimal maximum ? (double)maximum : 0,
            questions,
            summary,
            scoring);
    }

    private static string FormatScoringDisposition(ScoringDisposition disposition) => disposition switch
    {
        ScoringDisposition.Final => "最终",
        ScoringDisposition.Provisional => "待复核",
        _ => "未评分"
    };

    private static string FormatUnavailableReason(ScoreUnavailableReason reason) => reason switch
    {
        ScoreUnavailableReason.AnswerKeyMissing => "尚未提供完整标准答案",
        ScoreUnavailableReason.RecognitionRejected => "识别结果已拒绝",
        _ => "未知原因"
    };

    private async Task<IInputImageFile?> PickImageAsync(CancellationToken cancellationToken)
    {
        return await _fileDialogService.PickImageAsync(cancellationToken);
    }

    private static Task<IInputImageFile> OpenScanImageAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IInputImageFile>(new LocalInputImageFile(path));
    }

    private Task<CaptureRecord> PersistCaptureAsync(
        AnswerSheetLayout layout,
        IInputImageFile file,
        CaptureSourceType sourceType,
        CancellationToken cancellationToken)
    {
        return _captureStore.ImportAsync(layout, file, sourceType, cancellationToken);
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        try
        {
            await _capturePage.CancelAndWaitAsync();
            await _recognitionPage.CancelAndWaitAsync();
            if (_printController is not null)
            {
                await _printController.WaitForIdleAsync();
            }
            await _healthServer.StopAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"OMRINA 主窗口关闭清理失败：{exception.GetType().Name}（0x{exception.HResult:X8}）。");
        }
        finally
        {
            _printController?.Dispose();
            await _scannerService.DisposeAsync();
        }
    }
}
