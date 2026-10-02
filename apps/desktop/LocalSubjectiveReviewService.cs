using System.Text.Json;
using Omrina.Core;
using Omrina.Platform;
using Omrina.Protocol;
using Omrina.Server;

namespace Omrina.Desktop;

public sealed class LocalSubjectiveReviewException : Exception
{
    public LocalSubjectiveReviewException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record LocalSubjectiveReviewSummary(
    Guid ReviewId,
    string CaptureId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long Version,
    int QuestionCount,
    bool IsFinal,
    decimal? FinalSubtotal);

public sealed record LocalSubjectiveCaptureSummary(
    string CaptureId,
    string TemplateTitle,
    DateTimeOffset CreatedAtUtc,
    string SourceType,
    uint PixelWidth,
    uint PixelHeight,
    int QuestionCount);

public sealed record LocalSubjectiveReviewDiagnostic(string ResourceId, string Code, string Message);

public sealed record LocalSubjectiveReviewPage(
    IReadOnlyList<LocalSubjectiveReviewSummary> Items,
    IReadOnlyList<LocalSubjectiveReviewDiagnostic> Diagnostics,
    string? NextCursor);

public sealed record LocalSubjectiveCapturePage(
    IReadOnlyList<LocalSubjectiveCaptureSummary> Items,
    IReadOnlyList<LocalSubjectiveReviewDiagnostic> Diagnostics,
    string? NextCursor);

public sealed record LocalSubjectiveReviewDocument(
    Guid ReviewId,
    SubjectiveGradingSnapshot Snapshot,
    LocalSubjectiveCaptureSummary? Capture);

public sealed record LocalSubjectiveReviewExport(string Format, string Content);

/// <summary>
/// Trusted, local-only access to saved captures and subjective reviews. It never accepts paths,
/// grant IDs, or credentials from the caller and keeps file work off the UI thread.
/// </summary>
public sealed class LocalSubjectiveReviewService
{
    private const int MaximumExportEnvelopeBytes = 1024 * 1024;
    private readonly CaptureStore _captureStore;
    private readonly SubjectiveReviewStore _reviewStore;

    public LocalSubjectiveReviewService(
        CaptureStore captureStore,
        SubjectiveReviewStore? reviewStore = null)
    {
        _captureStore = captureStore ?? throw new ArgumentNullException(nameof(captureStore));
        _reviewStore = reviewStore ?? new SubjectiveReviewStore(captureStore.RootDirectory);
    }

    /// <summary>Lists records by GUID-N ordinal keyset order, not by creation time.</summary>
    public async Task<LocalSubjectiveReviewPage> ListReviewsAsync(
        int pageSize = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await Task.Run(
                () => _reviewStore.ListTrustedPage(pageSize, cursor, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return new LocalSubjectiveReviewPage(
                page.Items.Select(ToSummary).ToArray(),
                page.Diagnostics.Select(ToDiagnostic).ToArray(),
                page.NextCursor);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SubjectiveReviewStoreException exception)
        {
            throw ToServiceException(exception);
        }
    }

    /// <summary>Lists only manifest-validated captures, in GUID-N ordinal keyset order.</summary>
    public async Task<LocalSubjectiveCapturePage> ListCapturesAsync(
        int pageSize = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await Task.Run(
                () => _captureStore.LoadPage(pageSize, cursor, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return new LocalSubjectiveCapturePage(
                page.Items.Select(ToSummary).ToArray(),
                page.Diagnostics
                    .Select(diagnostic => new LocalSubjectiveReviewDiagnostic(
                        diagnostic.ResourceId,
                        diagnostic.Code,
                        diagnostic.Message))
                    .ToArray(),
                page.NextCursor);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CaptureException exception)
        {
            throw ToServiceException(exception);
        }
    }

    public async Task<LocalSubjectiveReviewDocument> ReadAsync(
        Guid reviewId,
        CancellationToken cancellationToken = default)
    {
        var record = await LoadReviewAsync(reviewId, cancellationToken).ConfigureAwait(false);
        var captureSummary = await TryLoadCaptureSummaryAsync(record.Snapshot.CaptureId, cancellationToken)
            .ConfigureAwait(false);
        return ToDocument(record, captureSummary);
    }

    public async Task<LocalSubjectiveReviewDocument> CreateAsync(
        string captureId,
        IReadOnlyList<SubjectiveRegionDefinition> regions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regions);
        CaptureRecord? capture;
        try
        {
            capture = await Task.Run(
                () => _captureStore.LoadById(captureId, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CaptureException exception)
        {
            throw ToServiceException(exception);
        }

        if (capture is null)
        {
            throw new LocalSubjectiveReviewException("CAPTURE_NOT_FOUND", "找不到可用于批阅的本地采集记录。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        SubjectiveGradingSnapshot snapshot;
        var reviewId = Guid.NewGuid();
        try
        {
            snapshot = SubjectiveGradingSnapshot.Start(
                capture.Manifest.CaptureId,
                checked((int)capture.Manifest.PixelWidth),
                checked((int)capture.Manifest.PixelHeight),
                regions);
            EnsureStoredResponseSize(reviewId, snapshot);
        }
        catch (LocalSubjectiveReviewException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new LocalSubjectiveReviewException(
                "INVALID_SUBJECTIVE_REVIEW",
                "题目区域或满分设置无效，请检查后重试。");
        }

        try
        {
            var record = await _reviewStore.CreateAsync(
                reviewId,
                SubjectiveReviewStore.DesktopLocalOwner,
                snapshot,
                cancellationToken).ConfigureAwait(false);
            return ToDocument(record, ToSummary(capture));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SubjectiveReviewStoreException exception)
        {
            throw ToServiceException(exception);
        }
    }

    public async Task<LocalSubjectiveReviewDocument> ApplyEditsAsync(
        Guid reviewId,
        long expectedVersion,
        string reviewer,
        IReadOnlyList<SubjectiveGradeEdit> edits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var captureSummary = await TryLoadCaptureSummaryAsyncForReviewAsync(reviewId, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var record = await _reviewStore.ApplyTrustedAsync(
                reviewId,
                expectedVersion,
                current =>
                {
                    var next = current.ApplyEdits(edits, reviewer, expectedVersion);
                    EnsureStoredResponseSize(reviewId, next);
                    return next;
                },
                cancellationToken).ConfigureAwait(false);
            return ToDocument(record, captureSummary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SubjectiveReviewStoreException exception)
        {
            throw ToServiceException(exception);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            if (exception is InvalidOperationException
                && exception.Message.Contains("不能超过 1 MiB", StringComparison.Ordinal))
            {
                throw TooLarge();
            }

            throw new LocalSubjectiveReviewException(
                "INVALID_SUBJECTIVE_GRADE",
                "评分修改无效，请检查状态、分值和评语后重试。");
        }
    }

    public async Task<LocalSubjectiveImage> ReadQuestionImageAsync(
        Guid reviewId,
        Guid questionId,
        CancellationToken cancellationToken = default)
    {
        var record = await LoadReviewAsync(reviewId, cancellationToken).ConfigureAwait(false);
        var question = record.Snapshot.Questions.FirstOrDefault(item => item.QuestionId == questionId)
            ?? throw new LocalSubjectiveReviewException("SUBJECTIVE_QUESTION_NOT_FOUND", "找不到此批阅记录中的题目区域。");

        CaptureRecord? capture;
        try
        {
            capture = await Task.Run(
                () => _captureStore.LoadById(record.Snapshot.CaptureId, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CaptureException exception)
        {
            throw ToServiceException(exception);
        }

        if (capture is null)
        {
            throw new LocalSubjectiveReviewException("CAPTURE_NOT_FOUND", "原始采集图像已不可用，批阅记录仍可读取和导出。");
        }

        if (capture.Manifest.PixelWidth != record.Snapshot.ImageWidth
            || capture.Manifest.PixelHeight != record.Snapshot.ImageHeight)
        {
            throw new LocalSubjectiveReviewException("SUBJECTIVE_IMAGE_INVALID", "原图尺寸与批阅记录不一致，无法读取区域。");
        }

        try
        {
            var file = await Task.Run(
                () => _captureStore.GetInputFile(record.Snapshot.CaptureId, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            var png = await SubjectiveImageCropper.CropToPngAsync(
                file,
                question.Region,
                record.Snapshot.ImageWidth,
                record.Snapshot.ImageHeight,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new LocalSubjectiveImage(png);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SubjectiveImageCropException exception)
        {
            throw new LocalSubjectiveReviewException(exception.Code, exception.Message);
        }
        catch (ImageDecodeException)
        {
            throw new LocalSubjectiveReviewException("SUBJECTIVE_IMAGE_INVALID", "答题区域图像无法读取，请检查原图后重试。");
        }
        catch (CaptureException exception)
        {
            throw ToServiceException(exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LocalSubjectiveReviewException("SUBJECTIVE_IMAGE_READ_FAILED", "答题区域读取失败，请稍后重试。");
        }
    }

    public async Task<IInputImageFile> ReadCaptureImageAsync(
        string captureId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run<IInputImageFile>(
                () => _captureStore.GetInputFile(captureId, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CaptureException exception)
        {
            throw ToServiceException(exception);
        }
    }

    public async Task<LocalSubjectiveReviewExport> ExportAsync(
        Guid reviewId,
        string format,
        CancellationToken cancellationToken = default)
    {
        if (format is not "json" and not "csv")
        {
            throw new LocalSubjectiveReviewException("INVALID_EXPORT_FORMAT", "导出格式请选择 JSON 或 CSV。");
        }

        var record = await LoadReviewAsync(reviewId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var content = format == "json"
            ? SubjectiveReviewExporter.ExportJson(record.ReviewId, record.Snapshot)
            : SubjectiveReviewExporter.ExportCsv(record.Snapshot);
        var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(new { format, content }, AgentJson.Options);
        if (envelopeBytes.Length > MaximumExportEnvelopeBytes)
        {
            throw TooLarge();
        }

        return new LocalSubjectiveReviewExport(format, content);
    }

    private async Task<SubjectiveReviewRecord> LoadReviewAsync(
        Guid reviewId,
        CancellationToken cancellationToken)
    {
        if (reviewId == Guid.Empty)
        {
            throw new LocalSubjectiveReviewException("SUBJECTIVE_REVIEW_NOT_FOUND", "找不到本地批阅记录。");
        }

        try
        {
            return await Task.Run(
                () => _reviewStore.LoadTrusted(reviewId)
                    ?? throw new LocalSubjectiveReviewException("SUBJECTIVE_REVIEW_NOT_FOUND", "找不到本地批阅记录。"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SubjectiveReviewStoreException exception)
        {
            throw ToServiceException(exception);
        }
    }

    private async Task<LocalSubjectiveCaptureSummary?> TryLoadCaptureSummaryAsync(
        string captureId,
        CancellationToken cancellationToken)
    {
        try
        {
            var capture = await Task.Run(
                () => _captureStore.LoadById(captureId, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return capture is null ? null : ToSummary(capture);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CaptureException)
        {
            // A missing or damaged original image must not hide the saved grades and history.
            return null;
        }
    }

    private async Task<LocalSubjectiveCaptureSummary?> TryLoadCaptureSummaryAsyncForReviewAsync(
        Guid reviewId,
        CancellationToken cancellationToken)
    {
        var record = await LoadReviewAsync(reviewId, cancellationToken).ConfigureAwait(false);
        return await TryLoadCaptureSummaryAsync(record.Snapshot.CaptureId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static LocalSubjectiveReviewSummary ToSummary(SubjectiveReviewRecord record)
    {
        var snapshot = record.Snapshot;
        return new LocalSubjectiveReviewSummary(
            record.ReviewId,
            snapshot.CaptureId,
            snapshot.CreatedAtUtc,
            snapshot.UpdatedAtUtc,
            snapshot.Version,
            snapshot.Questions.Count,
            snapshot.IsFinal,
            snapshot.FinalSubtotal);
    }

    private static LocalSubjectiveCaptureSummary ToSummary(CaptureRecord record)
    {
        var manifest = record.Manifest;
        return new LocalSubjectiveCaptureSummary(
            manifest.CaptureId,
            manifest.TemplateTitle,
            manifest.CreatedAtUtc,
            manifest.SourceType,
            manifest.PixelWidth,
            manifest.PixelHeight,
            manifest.QuestionCount);
    }

    private static LocalSubjectiveReviewDocument ToDocument(
        SubjectiveReviewRecord record,
        LocalSubjectiveCaptureSummary? capture)
    {
        return new LocalSubjectiveReviewDocument(record.ReviewId, record.Snapshot, capture);
    }

    private static LocalSubjectiveReviewDiagnostic ToDiagnostic(SubjectiveReviewStoreDiagnostic diagnostic)
    {
        return new LocalSubjectiveReviewDiagnostic(diagnostic.ResourceId, diagnostic.Code, diagnostic.Message);
    }

    private static void EnsureStoredResponseSize(Guid reviewId, SubjectiveGradingSnapshot snapshot)
    {
        var responseBytes = JsonSerializer.SerializeToUtf8Bytes(
            SubjectiveReviewExporter.ToWireDocument(reviewId, snapshot),
            AgentJson.Options);
        if (responseBytes.Length > SubjectiveReviewStore.MaximumStoredDocumentBytes)
        {
            throw TooLarge();
        }
    }

    private static LocalSubjectiveReviewException ToServiceException(SubjectiveReviewStoreException exception)
    {
        return new LocalSubjectiveReviewException(exception.Code, exception.Message);
    }

    private static LocalSubjectiveReviewException ToServiceException(CaptureException exception)
    {
        return new LocalSubjectiveReviewException(exception.Code, exception.Message);
    }

    private static LocalSubjectiveReviewException TooLarge() =>
        new("SUBJECTIVE_REVIEW_TOO_LARGE", "批阅记录过大，请减少区域或历史内容后重试。");
}
