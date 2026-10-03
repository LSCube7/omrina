using Omrina.Core;
using Omrina.Platform;
using Omrina.Scanning;
using Omrina.Server;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Omrina.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly LoopbackHealthServer _healthServer;
    private readonly DesktopLocalAgentOperations _agentOperations;
    private readonly CaptureStore _captureStore = new();
    private readonly IScannerService _scannerService;
    private readonly IAnswerSheetRecognitionService _recognitionService;
    private readonly IFileDialogService _fileDialogService;
    private readonly TemplatePrintController? _printController;
    private readonly StatusPage _statusPage;
    private readonly SchoolExamStore _examStore;
    private readonly SchoolExamPage _examPage;
    private readonly ExamAnswerKeyPage _answerKeyPage;
    private readonly TemplatePage _templatePage;
    private readonly CapturePage _capturePage;
    private readonly RecognitionPage _recognitionPage;
    private readonly SubjectiveReviewPage _subjectiveReviewPage;
    private readonly SettingsPage _settingsPage;
    private readonly AboutPage _aboutPage;
    private bool _isClosed;
    private SchoolSheetDefinition? _activeExam;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ConfigureSystemBackdrop();

        _statusPage = new StatusPage();
        _examStore = new SchoolExamStore(_captureStore.RootDirectory);
        _examPage = new SchoolExamPage(_examStore.ReadCatalog);
        _answerKeyPage = new ExamAnswerKeyPage(_examStore.ReadAnswerKey, _examStore.SaveAnswerKey);
        _aboutPage = new AboutPage();
        _fileDialogService = DesktopPlatformFactory.CreateFileDialogs(this);
        _examPage.ConfigureTemplateBundleTransfer(ExportSchoolTemplateBundleAsync, ImportSchoolTemplateBundleAsync);
        _scannerService = new SerializedScannerService(
            DesktopPlatformFactory.CreateScanner(_captureStore.RootDirectory));
        _recognitionService = new SkiaAnswerSheetRecognitionService();
        _subjectiveReviewPage = new SubjectiveReviewPage(
            new LocalSubjectiveReviewService(_captureStore),
            SaveTextAsync);
        _agentOperations = new DesktopLocalAgentOperations(
            _captureStore,
            _scannerService,
            _recognitionService,
            DesktopPlatformFactory.DeleteTemporaryScanFile);
        _healthServer = new LoopbackHealthServer(_agentOperations);
        _healthServer.GrantRevoked += _agentOperations.ForgetGrant;
        _settingsPage = new SettingsPage(_healthServer, DispatcherQueue);
        _printController = CreatePrintController();
        _templatePage = new TemplatePage(
            SaveSvgAsync,
            _printController is null ? null : PrintAsync,
            () => _printController?.IsBusy == true,
            _printController is null ? null : PrintDocumentAsync);
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
        _templatePage.SchoolDocumentGenerated += TemplatePage_SchoolDocumentGenerated;
        _capturePage.RecognitionRequested += CapturePage_RecognitionRequested;
        _capturePage.LayoutSelectionRequested += () => SelectNavigationItem("template");
        _capturePage.CaptureSelected += SetRecognitionCapture;
        _answerKeyPage.AnswersSaved += () =>
        {
            if (_recognitionPage.CurrentCapture is { } capture) SetSharedAnswerKey(capture);
        };
        _examPage.NewExamRequested += CreateNewExam;
        _examPage.ExamSelected += OpenExam;
        _examPage.NavigationRequested += SelectNavigationItem;

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

        NavigationFrame.Content = _examPage;
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

    private async void AppNavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        switch (tag)
        {
            case "exams":
                _examPage.Refresh();
                NavigateTo(_examPage, "考试");
                break;
            case "answer-key":
                NavigateTo(_answerKeyPage, "答案与评分");
                break;
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
                    SetRecognitionCapture(latestCapture);
                }

                NavigateTo(_recognitionPage, "识别 / 复核");
                break;
            }
            case "subjective-review":
                NavigateTo(_subjectiveReviewPage, "主观题批阅");
                await _subjectiveReviewPage.ActivateAsync();
                break;
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

    }

    private void CreateNewExam()
    {
        if (!CanChangeExam()) return;
        if (!_subjectiveReviewPage.CanChangeExam(null))
        {
            SelectNavigationItem("subjective-review");
            return;
        }
        var definition = new SchoolSheetDefinition
        {
            ExamId = Guid.NewGuid().ToString("N"),
            LayoutDocumentId = Guid.NewGuid().ToString("N"),
            Title = "新考试",
            Questions = [
                new(1, SchoolQuestionType.Choice, 2, "", ["", "", "", ""]),
                new(2, SchoolQuestionType.Choice, 2, "", ["", "", "", ""]),
                new(3, SchoolQuestionType.Choice, 2, "", ["", "", "", ""]),
                new(4, SchoolQuestionType.Choice, 2, "", ["", "", "", ""]),
                new(5, SchoolQuestionType.Subjective, 10, SubjectiveHeightMm: 60),
                new(6, SchoolQuestionType.Subjective, 10, SubjectiveHeightMm: 60)
            ]
        };
        _templatePage.LoadSchoolDefinition(definition, isSaved: false);
        SelectNavigationItem("template");
    }

    private void OpenExam(SchoolSheetDefinition definition)
    {
        try
        {
            if (!CanChangeExam()) return;
            if (!_subjectiveReviewPage.CanChangeExam(definition.ExamId))
            {
                SelectNavigationItem("subjective-review");
                return;
            }
            _templatePage.LoadSchoolDefinition(definition);
            if (_templatePage.CurrentDocument is { } document)
                TemplatePage_SchoolDocumentGenerated(definition, document);
            SelectNavigationItem("template");
        }
        catch (Exception exception)
        {
            _statusPage.SetConnectionStatus($"考试无法打开，请刷新列表后重试。调试信息：{exception.GetType().Name}（0x{exception.HResult:X8}）。");
            SelectNavigationItem("status");
        }
    }

    private void TemplatePage_SchoolDocumentGenerated(SchoolSheetDefinition definition, SchoolAnswerSheet document)
    {
        var contextChanged = _activeExam is null || _activeExam.ExamId != definition.ExamId
            || _activeExam.LayoutDocumentId != definition.LayoutDocumentId || _activeExam.Version != definition.Version;
        if (contextChanged)
        {
            if (!CanChangeExam()) throw new InvalidOperationException("请先保存当前考试的标准答案，再切换考试。");
        }
        if (!_subjectiveReviewPage.CanChangeExam(definition.ExamId))
            throw new InvalidOperationException("请先保存或取消当前主观题批阅的修改，再切换考试。");
        var canonical = _examStore.SaveDefinition(definition);
        _answerKeyPage.SetExam(canonical);
        if (contextChanged) _recognitionPage.ClearCapture();
        _activeExam = canonical;
        _examPage.SetCurrentExam(canonical);
        _capturePage.SetDocument(document);
        RefreshExamCaptures();
    }

    private bool CanChangeExam()
    {
        if (_answerKeyPage.CanChangeExam()) return true;
        SelectNavigationItem("answer-key");
        return false;
    }

    private void RefreshExamCaptures()
    {
        if (_activeExam is null) return;
        var items = new List<CaptureRecord>();
        string? cursor = null;
        do
        {
            var page = _captureStore.LoadPage(cursor: cursor);
            if (page.Diagnostics.Count > 0)
            {
                _statusPage.SetConnectionStatus($"答卷历史中有 {page.Diagnostics.Count} 条记录无法读取，请检查本地数据。错误代码：{page.Diagnostics[0].Code}。");
            }
            items.AddRange(page.Items.Where(capture => capture.TemplateLayout?.SchoolMetadata is { } metadata
                && metadata.ExamId == _activeExam.ExamId
                && metadata.LayoutDocumentId == _activeExam.LayoutDocumentId
                && metadata.Version == _activeExam.Version));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        _capturePage.SetCaptures(items);
        if (!_subjectiveReviewPage.SetExam(_activeExam.ExamId, items.Select(item => item.Manifest.CaptureId).ToArray()))
        {
            _statusPage.SetConnectionStatus("主观题批阅中有未保存的修改，请保存或取消后再切换考试。");
        }
    }

    private void TemplatePage_CaptureRequested(AnswerSheetLayout layout)
    {
        _capturePage.SetLayout(layout);
        SelectNavigationItem("capture");
    }

    private void CapturePage_RecognitionRequested(object? sender, CaptureRecord capture)
    {
        SetRecognitionCapture(capture);
        SelectNavigationItem("recognition");
    }

    private void SetRecognitionCapture(CaptureRecord capture)
    {
        _recognitionPage.SetCapture(capture);
        SetSharedAnswerKey(capture);
    }

    private void SetSharedAnswerKey(CaptureRecord capture)
    {
        if (capture.TemplateLayout?.SchoolDefinition is { } definition)
        {
            try
            {
                _recognitionPage.SetExamAnswerKey(definition.ExamId, _examStore.ReadAnswerKey(definition));
            }
            catch (Exception exception)
            {
                _recognitionPage.SetExamAnswerKey(definition.ExamId, null);
                _statusPage.SetConnectionStatus($"标准答案读取失败，请检查考试版本后重试。调试信息：{exception.GetType().Name}（0x{exception.HResult:X8}）。");
            }
        }
    }

    private void SelectNavigationItem(string tag)
    {
        foreach (var item in AppNavigationView.MenuItems.Concat(AppNavigationView.FooterMenuItems).OfType<NavigationViewItem>())
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

    private async Task PrintDocumentAsync(IReadOnlyList<AnswerSheetLayout> pages)
    {
        if (_printController is null)
        {
            throw new InvalidOperationException("系统打印服务不可用。");
        }
        await _printController.RequestPrintAsync(pages);
    }

    private async Task<SvgSaveResult> SaveSvgAsync(AnswerSheetLayout layout)
    {
        var result = await _fileDialogService.SaveTextAsync(
            "omrina-template",
            ".svg",
            layout.ToSvg());
        return new SvgSaveResult(result.Cancelled, result.Path);
    }

    private async Task<FileSaveResult> ExportSchoolTemplateBundleAsync(SchoolSheetDefinition definition)
    {
        var content = await Task.Run(() => _examStore.ExportTemplateBundle(definition));
        return await _fileDialogService.SaveTextAsync("omrina-school-template", ".json", content);
    }

    private async Task<SchoolSheetDefinition?> ImportSchoolTemplateBundleAsync()
    {
        var content = await _fileDialogService.PickTemplateBundleJsonAsync();
        return content is null ? null : await Task.Run(() => _examStore.ImportTemplateBundle(content));
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
        var key = AnswerKey.Create(layout, PageAnswers(layout, answerKey));
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
            key = AnswerKey.Create(layout, PageAnswers(layout, answerKey));
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
        if (request.Layout.Questions.Count > 0 && request.Layout.Questions.Select(question => question.Number).All(
                questionNumber => request.AnswerKey.TryGetValue(questionNumber, out var answer)
                    && !string.IsNullOrWhiteSpace(answer)))
        {
            var answerValues = request.Layout.Questions.Select(question => question.Number)
                .ToDictionary(questionNumber => questionNumber, questionNumber => request.AnswerKey[questionNumber]!);
            answerKey = AnswerKey.Create(request.Layout, answerValues);
        }

        var scoring = ScoringEngine.Score(request.Review, answerKey);
        var coreFormat = format == RecognitionExportFormat.Json
            ? Omrina.Core.ResultExportFormat.Json
            : Omrina.Core.ResultExportFormat.Csv;
        return Task.FromResult<string?>(ResultExporter.Export(scoring, coreFormat, indented: true));
    }

    private static IReadOnlyDictionary<int, string> PageAnswers(AnswerSheetLayout layout, IReadOnlyDictionary<int, string> answers)
        => layout.Questions.ToDictionary(question => question.Number, question => answers.TryGetValue(question.Number, out var answer)
            ? answer : throw new ArgumentException($"标准答案缺少第 {question.Number} 题。"));

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

    private async Task<CaptureRecord> PersistCaptureAsync(
        AnswerSheetLayout layout,
        IInputImageFile file,
        CaptureSourceType sourceType,
        CancellationToken cancellationToken)
    {
        var capture = await _captureStore.ImportAsync(layout, file, sourceType, cancellationToken);
        try { RefreshExamCaptures(); }
        catch (Exception exception)
        {
            _statusPage.SetConnectionStatus($"答卷已保存，但历史列表刷新失败，请重新打开考试。调试信息：{exception.GetType().Name}（0x{exception.HResult:X8}）。");
        }
        return capture;
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        try
        {
            await Task.WhenAll(
                _capturePage.CancelAndWaitAsync(),
                _recognitionPage.CancelAndWaitAsync(),
                _subjectiveReviewPage.CancelAndWaitAsync());
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
            _healthServer.GrantRevoked -= _agentOperations.ForgetGrant;
            await _scannerService.DisposeAsync();
        }
    }
}
