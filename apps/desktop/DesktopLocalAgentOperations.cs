using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Omrina.Core;
using Omrina.Platform;
using Omrina.Protocol;
using Omrina.Scanning;
using Omrina.Server;

namespace Omrina.Desktop;

/// <summary>
/// Adapts the app-owned capture, scanner and M2 recognition workflow to the
/// grant-scoped local agent API. No UI types or caller-provided local paths
/// cross this boundary.
/// </summary>
public sealed class DesktopLocalAgentOperations : ILocalAgentOperations, ILocalAgentSubjectiveImages
{
    private const int MaximumUploadBytes = 20 * 1024 * 1024;
    private const int MaximumResultBytes = 1024 * 1024;
    private const int MaximumRevokedGrantMarkers = 4096;
    private static readonly TimeSpan RevokedGrantMarkerLifetime = TimeSpan.FromHours(8) + TimeSpan.FromMinutes(5);

    private readonly CaptureStore _captureStore;
    private readonly IScannerService _scannerService;
    private readonly IAnswerSheetRecognitionService _recognitionService;
    private readonly Action<string> _deleteTemporaryScanFile;
    private readonly SubjectiveReviewStore _subjectiveReviewStore;
    private readonly AnswerSheetLayout _defaultTemplate;
    private readonly object _grantResourcesGate = new();
    private readonly Dictionary<string, GrantResources> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _revokedGrantIds = new(StringComparer.Ordinal);

    public DesktopLocalAgentOperations(
        CaptureStore captureStore,
        IScannerService scannerService,
        IAnswerSheetRecognitionService recognitionService,
        Action<string> deleteTemporaryScanFile)
    {
        _captureStore = captureStore ?? throw new ArgumentNullException(nameof(captureStore));
        _scannerService = scannerService ?? throw new ArgumentNullException(nameof(scannerService));
        _recognitionService = recognitionService ?? throw new ArgumentNullException(nameof(recognitionService));
        _deleteTemporaryScanFile = deleteTemporaryScanFile
            ?? throw new ArgumentNullException(nameof(deleteTemporaryScanFile));
        _subjectiveReviewStore = new SubjectiveReviewStore(_captureStore.RootDirectory);
        _defaultTemplate = AnswerSheetLayout.Create("OMRINA 默认答题纸", 10, 4);
    }

    /// <summary>Releases all in-memory references after a grant is revoked or expires.</summary>
    public void ForgetGrant(string grantId)
    {
        if (string.IsNullOrWhiteSpace(grantId))
        {
            return;
        }

        GrantResources? resources;
        lock (_grantResourcesGate)
        {
            _revokedGrantIds[grantId] = DateTimeOffset.UtcNow + RevokedGrantMarkerLifetime;
            PruneRevokedGrantMarkers(DateTimeOffset.UtcNow);
            _resources.Remove(grantId, out resources);
        }

        resources?.Revoke();
    }

    public Task<JsonElement> GetTemplatesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ToJsonElement(new[] { ToTemplateSummary(_defaultTemplate) }));
    }

    public async Task<JsonElement> GetDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = await _scannerService.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            return ToJsonElement(devices.Select(device => new
            {
                deviceId = device.Id,
                device.Name,
                device.Driver
            }).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogSafeException("device-list", exception);
            throw new LocalOperationException("SCANNER_UNAVAILABLE", "扫描设备暂时不可用，请检查设备后重试。");
        }
    }

    public async Task<JsonElement> RunAsync(
        TaskOperationRequest request,
        Stream? image,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.GrantId))
        {
            throw new LocalOperationException("INVALID_GRANT", "当前本地授权无效，请重新配对后重试。");
        }

        var parameters = RequireObject(request.Parameters);
        RejectUnknownOrPathProperties(parameters, GetAllowedParameterNames(request.Operation));
        if (request.Operation != TaskOperation.Upload && image is not null)
        {
            throw new LocalOperationException("UNEXPECTED_IMAGE", "此操作不接受图像数据。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var grantResources = GetGrantResources(request.GrantId);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            grantResources.RevocationToken);
        var operationCancellationToken = linkedCancellation.Token;

        try
        {
            if (request.Operation == TaskOperation.Template)
            {
                return CreateTemplate(grantResources, parameters, operationCancellationToken);
            }

            return request.Operation switch
            {
                TaskOperation.Upload => await UploadAsync(grantResources!, parameters, image, operationCancellationToken)
                    .ConfigureAwait(false),
                TaskOperation.Scan => await ScanAsync(grantResources!, parameters, operationCancellationToken)
                    .ConfigureAwait(false),
                TaskOperation.Recognize => await RecognizeAsync(grantResources!, parameters, operationCancellationToken)
                    .ConfigureAwait(false),
                TaskOperation.Score => Score(grantResources!, parameters, operationCancellationToken),
                TaskOperation.Review => Review(grantResources!, parameters, operationCancellationToken),
                TaskOperation.Export => Export(grantResources!, parameters, operationCancellationToken),
                TaskOperation.SubjectiveCreate => await CreateSubjectiveReviewAsync(
                    request.GrantId,
                    grantResources,
                    parameters,
                    operationCancellationToken).ConfigureAwait(false),
                TaskOperation.SubjectiveRead => await ReadSubjectiveReviewAsync(
                    request.GrantId,
                    grantResources,
                    parameters,
                    operationCancellationToken).ConfigureAwait(false),
                TaskOperation.SubjectiveGrade => await GradeSubjectiveReviewAsync(
                    request.GrantId,
                    grantResources,
                    parameters,
                    operationCancellationToken).ConfigureAwait(false),
                TaskOperation.SubjectiveExport => await ExportSubjectiveReviewAsync(
                    request.GrantId,
                    grantResources,
                    parameters,
                    operationCancellationToken).ConfigureAwait(false),
                _ => throw new LocalOperationException("UNSUPPORTED_OPERATION", "当前操作暂不支持。")
            };
        }
        catch (LocalOperationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CaptureException exception)
        {
            throw new LocalOperationException(exception.Code, exception.Message);
        }
        catch (Exception exception)
        {
            LogSafeException("task", exception);
            throw new LocalOperationException("LOCAL_OPERATION_FAILED", "本地操作失败，请检查输入后重试。");
        }
    }

    private JsonElement CreateTemplate(
        GrantResources grantResources,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (parameters.TryGetProperty("schoolDefinition", out var schoolInput))
        {
            if (parameters.EnumerateObject().Count() != 1)
                throw new LocalOperationException("INVALID_PARAMETERS", "学校答题纸定义不能与旧版模板参数混用。");
            var definition = ReadSchoolDefinition(schoolInput);
            var pages = SchoolAnswerSheet.Create(definition).Pages;
            var result = ToJsonElement(new
            {
                documentId = definition.LayoutDocumentId,
                examId = definition.ExamId,
                version = definition.Version,
                pages = pages.Select(ToTemplateSummary).ToArray()
            });
            // Match LocalAgentHost's UTF-8 result bound before publishing any page resource.
            if (Encoding.UTF8.GetByteCount(result.GetRawText()) > MaximumResultBytes)
                throw new LocalOperationException("TEMPLATE_DOCUMENT_TOO_LARGE", "答题纸页面和预览内容过大，请减少页数或题目正文后重试。");
            cancellationToken.ThrowIfCancellationRequested();
            grantResources.AddTemplates(pages);
            return result;
        }
        var title = ReadString(parameters, "title");
        var questionCount = ReadPositiveInt(parameters, "questionCount");
        var optionsPerQuestion = ReadPositiveInt(parameters, "optionsPerQuestion");
        var subjectiveRegions = ReadTemplateSubjectiveRegions(parameters);
        AnswerSheetLayout layout;
        try
        {
            layout = subjectiveRegions.Length == 0
                ? AnswerSheetLayout.Create(title, questionCount, optionsPerQuestion)
                : AnswerSheetLayout.Create(title, questionCount, optionsPerQuestion, subjectiveRegions);
        }
        catch (Exception exception)
        {
            LogSafeException("template", exception);
            throw new LocalOperationException("INVALID_TEMPLATE", "模板参数无效，请检查标题、题数和选项数后重试。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        grantResources.AddTemplate(layout);
        return ToJsonElement(ToTemplateSummary(layout));
    }

    private async Task<JsonElement> UploadAsync(
        GrantResources grantResources,
        JsonElement parameters,
        Stream? image,
        CancellationToken cancellationToken)
    {
        if (image is null)
        {
            throw new LocalOperationException("IMAGE_REQUIRED", "请提供 PNG 或 JPEG 原图后重试。");
        }

        var layout = FindTemplate(grantResources, ReadString(parameters, "templateId"));
        var fileName = ValidateFileName(ReadString(parameters, "fileName"));
        var file = await BufferUploadAsync(image, fileName, cancellationToken).ConfigureAwait(false);
        var capture = await _captureStore.ImportAsync(
            layout,
            file,
            CaptureSourceType.Import,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        grantResources.AddCapture(capture.Manifest.CaptureId, new CaptureResource(capture, layout));
        return ToCaptureSummary(capture);
    }

    private async Task<JsonElement> ScanAsync(
        GrantResources grantResources,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var layout = FindTemplate(grantResources, ReadString(parameters, "templateId"));
        var deviceId = ReadString(parameters, "deviceId");
        var dpi = ReadPositiveInt(parameters, "dpi");
        if (dpi is not (150 or 300 or 600))
        {
            throw new LocalOperationException("INVALID_SCAN_OPTIONS", "分辨率请选择 150、300 或 600 DPI。");
        }

        string? temporaryImagePath = null;
        try
        {
            var devices = await _scannerService.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            var device = devices.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, deviceId, StringComparison.Ordinal));
            if (device is null)
            {
                throw new LocalOperationException("SCANNER_NOT_FOUND", "扫描设备已失效，请刷新设备列表后重试。");
            }

            var scan = await _scannerService.ScanAsync(
                device,
                new ScanOptions(dpi, Flatbed: true, PageSize: layout.WidthMm > 210 ? "A3" : "A4"),
                cancellationToken).ConfigureAwait(false);
            temporaryImagePath = ValidateScannerOutputPath(scan.ImagePath);
            var file = new LocalInputImageFile(temporaryImagePath);
            var capture = await _captureStore.ImportAsync(
                layout,
                file,
                CaptureSourceType.Scan,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            grantResources.AddCapture(capture.Manifest.CaptureId, new CaptureResource(capture, layout));
            return ToJsonElement(new
            {
                captureId = capture.Manifest.CaptureId,
                templateId = capture.Manifest.TemplateId,
                createdAt = capture.Manifest.CreatedAtUtc,
                sourceType = capture.Manifest.SourceType,
                imageWidth = capture.Manifest.PixelWidth,
                imageHeight = capture.Manifest.PixelHeight,
                byteLength = capture.Manifest.ByteLength,
                dpi = scan.Dpi,
                pageSize = scan.PageSize,
                flatbed = scan.Flatbed,
                schoolMetadata = ToSchoolMetadata(capture.Manifest.SchoolMetadata),
                candidateId = capture.Manifest.CandidateId,
                identityStatus = capture.Manifest.IdentityStatus
            });
        }
        catch (LocalOperationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CaptureException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogSafeException("scan", exception);
            throw new LocalOperationException("SCAN_FAILED", "扫描失败，请检查设备并重试。");
        }
        finally
        {
            if (temporaryImagePath is not null)
            {
                _deleteTemporaryScanFile(temporaryImagePath);
            }
        }
    }

    private async Task<JsonElement> RecognizeAsync(
        GrantResources grantResources,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var captureId = ReadString(parameters, "captureId");
        var capture = FindCapture(grantResources, captureId);
        var recognition = await _recognitionService.RecognizeAsync(
            capture.Layout,
            new LocalInputImageFile(capture.Record.ImageFilePath),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new ResultResource(capture.Layout, recognition);
        var resultId = Guid.NewGuid().ToString("N");
        grantResources.AddResult(resultId, result);
        lock (result.SyncRoot)
        {
            return BuildResultSummary(resultId, result);
        }
    }

    private JsonElement Score(GrantResources grantResources, JsonElement parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resultId = ReadString(parameters, "resultId");
        var result = FindResult(grantResources, resultId);
        var answerKey = ReadAnswerKey(parameters, result.Layout);
        var pointsPerQuestion = ReadOptionalPositiveDecimal(parameters, "pointsPerQuestion") ?? 1m;
        lock (result.SyncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scoring = ScoringEngine.Score(
                result.Review,
                answerKey,
                new ScoringOptions(pointsPerQuestion));
            var nextVersion = checked(result.Version + 1);
            cancellationToken.ThrowIfCancellationRequested();
            result.AnswerKey = answerKey;
            result.Scoring = scoring;
            result.Version = nextVersion;
            return BuildResultSummary(resultId, result);
        }
    }

    private JsonElement Review(GrantResources grantResources, JsonElement parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resultId = ReadString(parameters, "resultId");
        var expectedVersion = ReadPositiveInt(parameters, "expectedVersion");
        var reviewer = ReadString(parameters, "reviewer");
        var result = FindResult(grantResources, resultId);
        var edits = ReadReviewEdits(parameters, result.Layout);
        var updatedAnswerKey = parameters.TryGetProperty("answerKey", out _)
            ? ReadOptionalAnswerKey(parameters, result.Layout)
            : null;

        lock (result.SyncRoot)
        {
            if (result.Version != expectedVersion)
            {
                throw new LocalOperationException(
                    "VERSION_CONFLICT",
                    "识别结果已更新，请重新读取后再提交复核。");
            }

            if (!result.Review.CanReview)
            {
                throw new LocalOperationException("RECOGNITION_REJECTED", "已拒绝的识别结果不能进行人工复核。");
            }

            var nextReview = result.Review;
            foreach (var edit in edits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    nextReview = string.IsNullOrEmpty(edit.Answer)
                        ? nextReview.ConfirmBlank(edit.QuestionNumber, reviewer, edit.Reason, edit.TimestampUtc)
                        : nextReview.SetAnswer(edit.QuestionNumber, edit.Answer, reviewer, edit.Reason, edit.TimestampUtc);
                }
                catch (ArgumentException)
                {
                    throw new LocalOperationException("INVALID_REVIEW", "复核答案、审核人或原因无效，请检查后重试。");
                }
            }

            var nextAnswerKey = updatedAnswerKey ?? result.AnswerKey;
            var nextScoring = ScoringEngine.Score(
                nextReview,
                nextAnswerKey,
                result.Scoring.Options);
            var nextVersion = checked(result.Version + 1);
            cancellationToken.ThrowIfCancellationRequested();
            result.Review = nextReview;
            result.AnswerKey = nextAnswerKey;
            result.Scoring = nextScoring;
            result.Version = nextVersion;
            return BuildResultSummary(resultId, result);
        }
    }

    private JsonElement Export(GrantResources grantResources, JsonElement parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resultId = ReadString(parameters, "resultId");
        var formatValue = ReadString(parameters, "format");
        var format = formatValue.ToLowerInvariant() switch
        {
            "json" => ResultExportFormat.Json,
            "csv" => ResultExportFormat.Csv,
            _ => throw new LocalOperationException("INVALID_EXPORT_FORMAT", "导出格式请选择 JSON 或 CSV。")
        };
        var result = FindResult(grantResources, resultId);
        lock (result.SyncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = ResultExporter.Export(result.Scoring, format, indented: true);
            cancellationToken.ThrowIfCancellationRequested();
            return ToJsonElement(new { format = formatValue.ToLowerInvariant(), content });
        }
    }

    private async Task<JsonElement> CreateSubjectiveReviewAsync(
        string grantId,
        GrantResources grantResources,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        await grantResources.SubjectiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captureGuid = ReadGuid(parameters, "captureId");
            var capture = FindCapture(grantResources, captureGuid.ToString("N"));
            var captureRecord = capture.Record;
            var imageWidth = checked((int)captureRecord.Manifest.PixelWidth);
            var imageHeight = checked((int)captureRecord.Manifest.PixelHeight);
            var layout = captureRecord.TemplateLayout;
            if (layout is null
                || !string.Equals(layout.TemplateId, captureRecord.Manifest.TemplateId, StringComparison.Ordinal)
                || layout.SchemaVersion != captureRecord.Manifest.TemplateSchemaVersion
                || !string.Equals(capture.Layout.TemplateId, layout.TemplateId, StringComparison.Ordinal))
            {
                throw new LocalOperationException(
                    "CAPTURE_TEMPLATE_INVALID",
                    "采集记录缺少有效的关联模板，无法创建主观题批阅记录。");
            }

            SubjectiveReviewTemplateMapping templateMapping;
            try
            {
                templateMapping = await SubjectiveCaptureTemplateMapper.LocateAndMapAsync(
                    layout,
                    new LocalInputImageFile(captureRecord.ImageFilePath),
                    imageWidth,
                    imageHeight,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SubjectiveCaptureMappingException exception)
            {
                throw new LocalOperationException(exception.Code, exception.Message);
            }
            catch (ImageDecodeException)
            {
                throw new LocalOperationException(
                    "SUBJECTIVE_IMAGE_INVALID",
                    "原始采集图像无法读取，未创建批阅记录。");
            }

            var definitions = templateMapping.Regions.Select(region =>
            {
                var mapped = region.ToMapped(imageWidth, imageHeight);
                return SubjectiveRegionDefinition.Create(
                    mapped.QuestionId,
                    mapped.QuestionNumber,
                    mapped.BoundingRectangle,
                    mapped.MaximumScore,
                    imageWidth,
                    imageHeight);
            }).ToArray();

            SubjectiveGradingSnapshot snapshot;
            try
            {
                snapshot = SubjectiveGradingSnapshot.Start(
                    captureRecord.Manifest.CaptureId,
                    imageWidth,
                    imageHeight,
                    definitions);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
            {
                throw new LocalOperationException("INVALID_SUBJECTIVE_REVIEW", "题目区域或满分设置无效，请检查后重试。");
            }

            var reviewId = Guid.NewGuid();
            var response = CreateCheckedSubjectiveDocument(reviewId, snapshot);
            await SaveNewSubjectiveReviewAsync(
                reviewId,
                grantId,
                snapshot,
                templateMapping,
                cancellationToken).ConfigureAwait(false);
            return response;
        }
        finally
        {
            grantResources.SubjectiveGate.Release();
        }
    }

    private async Task<JsonElement> ReadSubjectiveReviewAsync(
        string grantId,
        GrantResources grantResources,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var reviewId = ReadGuid(parameters, "reviewId");
        await grantResources.SubjectiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = FindSubjectiveReview(grantResources, reviewId, grantId);
            return CreateCheckedSubjectiveDocument(record.ReviewId, record.Snapshot);
        }
        finally
        {
            grantResources.SubjectiveGate.Release();
        }
    }

    private async Task<JsonElement> GradeSubjectiveReviewAsync(
        string grantId,
        GrantResources grantResources,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var reviewId = ReadGuid(parameters, "reviewId");
        var expectedVersion = ReadNonNegativeInt64(parameters, "expectedVersion");
        var reviewer = ReadString(parameters, "reviewer");
        var edits = ReadSubjectiveGradeEdits(parameters);

        await grantResources.SubjectiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = FindSubjectiveReview(grantResources, reviewId, grantId);
            if (record.Snapshot.History.Count >= SubjectiveGradingSnapshot.MaximumHistoryBatchCount)
            {
                throw new LocalOperationException("SUBJECTIVE_HISTORY_LIMIT", "批阅历史已达到上限，无法继续修改。");
            }

            SubjectiveReviewRecord committed;
            try
            {
                committed = await _subjectiveReviewStore.ApplyForGrantAsync(
                    reviewId,
                    grantId,
                    expectedVersion,
                    current =>
                    {
                        if (current.History.Count >= SubjectiveGradingSnapshot.MaximumHistoryBatchCount)
                        {
                            throw new LocalOperationException(
                                "SUBJECTIVE_HISTORY_LIMIT",
                                "批阅历史已达到上限，无法继续修改。");
                        }

                        var next = current.ApplyEdits(edits, reviewer, expectedVersion);
                        _ = CreateCheckedSubjectiveDocument(reviewId, next);
                        return next;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("不能超过 1 MiB", StringComparison.Ordinal))
            {
                throw SubjectiveReviewTooLarge();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
            {
                throw new LocalOperationException("INVALID_SUBJECTIVE_GRADE", "评分修改无效，请检查状态、分值和评语后重试。");
            }
            catch (SubjectiveReviewStoreException exception)
            {
                throw new LocalOperationException(exception.Code, exception.Message);
            }

            return CreateCheckedSubjectiveDocument(reviewId, committed.Snapshot);
        }
        finally
        {
            grantResources.SubjectiveGate.Release();
        }
    }

    private async Task<JsonElement> ExportSubjectiveReviewAsync(
        string grantId,
        GrantResources grantResources,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var reviewId = ReadGuid(parameters, "reviewId");
        var format = ReadString(parameters, "format");
        if (format is not "json" and not "csv")
        {
            throw new LocalOperationException("INVALID_EXPORT_FORMAT", "导出格式请选择 JSON 或 CSV。");
        }

        await grantResources.SubjectiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = FindSubjectiveReview(grantResources, reviewId, grantId);
            var content = format == "json"
                ? SubjectiveReviewExporter.ExportJson(record.ReviewId, record.Snapshot)
                : SubjectiveReviewExporter.ExportCsv(record.Snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            return EnsureSubjectiveResultSize(ToJsonElement(new { format, content }));
        }
        finally
        {
            grantResources.SubjectiveGate.Release();
        }
    }

    public async Task<LocalSubjectiveImage> ReadSubjectiveImageAsync(
        string grantId,
        Guid reviewId,
        Guid questionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(grantId) || reviewId == Guid.Empty || questionId == Guid.Empty)
        {
            throw new LocalOperationException("NOT_FOUND", "找不到此授权下的答题区域。");
        }

        var grantResources = GetGrantResources(grantId);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            grantResources.RevocationToken);
        var operationCancellationToken = linkedCancellation.Token;
        CaptureRecord captureRecord;
        SubjectiveGradingQuestion question;
        SubjectiveReviewTemplateMapping? templateMapping;

        await grantResources.SubjectiveGate.WaitAsync(operationCancellationToken).ConfigureAwait(false);
        try
        {
            operationCancellationToken.ThrowIfCancellationRequested();
            var record = FindSubjectiveReview(grantResources, reviewId, grantId);
            templateMapping = record.TemplateMapping;
            question = record.Snapshot.Questions.FirstOrDefault(candidate => candidate.QuestionId == questionId)
                ?? throw new LocalOperationException("SUBJECTIVE_QUESTION_NOT_FOUND", "找不到此授权下的答题区域。");
            if (!grantResources.TryGetCapture(record.Snapshot.CaptureId, out var capture))
            {
                throw new LocalOperationException("NOT_FOUND", "找不到此授权下的原始采集图像。");
            }

            captureRecord = capture.Record;
        }
        finally
        {
            grantResources.SubjectiveGate.Release();
        }

        try
        {
            var file = new LocalInputImageFile(captureRecord.ImageFilePath);
            byte[] png;
            if (templateMapping is { } mapping)
            {
                if (captureRecord.TemplateLayout is null
                    || !string.Equals(captureRecord.TemplateLayout.ToJson(), mapping.TemplateJson, StringComparison.Ordinal))
                {
                    throw new LocalOperationException(
                        "CAPTURE_TEMPLATE_MISMATCH",
                        "原图关联的模板与批阅记录不一致，无法读取题目区域。");
                }

                var regionProvenance = mapping.Regions.FirstOrDefault(region => region.QuestionId == question.QuestionId)
                    ?? throw new LocalOperationException(
                        "SUBJECTIVE_IMAGE_INVALID",
                        "批阅记录缺少该题的模板区域映射。");
                png = await SubjectiveImageCropper.RectifyToPngAsync(
                    file,
                    regionProvenance.ToMapped(
                        checked((int)captureRecord.Manifest.PixelWidth),
                        checked((int)captureRecord.Manifest.PixelHeight)),
                    checked((int)captureRecord.Manifest.PixelWidth),
                    checked((int)captureRecord.Manifest.PixelHeight),
                    operationCancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Keep image access for already-saved pixel-defined documents.
                png = await SubjectiveImageCropper.CropToPngAsync(
                    file,
                    question.Region,
                    checked((int)captureRecord.Manifest.PixelWidth),
                    checked((int)captureRecord.Manifest.PixelHeight),
                    operationCancellationToken).ConfigureAwait(false);
            }

            operationCancellationToken.ThrowIfCancellationRequested();
            return new LocalSubjectiveImage(png);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SubjectiveImageCropException exception)
        {
            throw new LocalOperationException(exception.Code, exception.Message);
        }
        catch (ImageDecodeException exception)
        {
            LogSafeException("subjective-image", exception);
            throw new LocalOperationException("SUBJECTIVE_IMAGE_INVALID", "答题区域图像无法读取，请重新采集后重试。");
        }
        catch (FileNotFoundException)
        {
            throw new LocalOperationException("SUBJECTIVE_IMAGE_NOT_FOUND", "找不到此授权下的原始采集图像。");
        }
        catch (Exception exception)
        {
            LogSafeException("subjective-image", exception);
            throw new LocalOperationException("SUBJECTIVE_IMAGE_READ_FAILED", "答题区域读取失败，请稍后重试。");
        }
    }

    private async Task SaveNewSubjectiveReviewAsync(
        Guid reviewId,
        string grantId,
        SubjectiveGradingSnapshot snapshot,
        SubjectiveReviewTemplateMapping? templateMapping,
        CancellationToken cancellationToken)
    {
        try
        {
            await _subjectiveReviewStore.CreateAsync(
                    reviewId,
                    grantId,
                    snapshot,
                    cancellationToken,
                    templateMapping)
                .ConfigureAwait(false);
        }
        catch (SubjectiveReviewStoreException exception)
        {
            throw new LocalOperationException(exception.Code, exception.Message);
        }
    }

    private SubjectiveReviewRecord FindSubjectiveReview(
        GrantResources grantResources,
        Guid reviewId,
        string grantId)
    {
        SubjectiveReviewRecord? record;
        try
        {
            record = _subjectiveReviewStore.LoadForGrant(reviewId, grantId);
        }
        catch (SubjectiveReviewStoreException exception)
        {
            throw new LocalOperationException(exception.Code, exception.Message);
        }

        if (record is null || !grantResources.TryGetCapture(record.Snapshot.CaptureId, out _))
        {
            throw new LocalOperationException("SUBJECTIVE_REVIEW_NOT_FOUND", "找不到此授权下的主观题批阅记录。");
        }

        return record;
    }

    private static JsonElement CreateCheckedSubjectiveDocument(Guid reviewId, SubjectiveGradingSnapshot snapshot)
    {
        var result = ToJsonElement(SubjectiveReviewExporter.ToWireDocument(reviewId, snapshot));
        if (Encoding.UTF8.GetByteCount(result.GetRawText()) > SubjectiveReviewStore.MaximumStoredDocumentBytes)
        {
            throw SubjectiveReviewTooLarge();
        }

        return result;
    }

    private static JsonElement EnsureSubjectiveResultSize(JsonElement result)
    {
        if (Encoding.UTF8.GetByteCount(result.GetRawText()) > MaximumResultBytes)
        {
            throw SubjectiveReviewTooLarge();
        }

        return result;
    }

    private static LocalOperationException SubjectiveReviewTooLarge() =>
        new("SUBJECTIVE_REVIEW_TOO_LARGE", "批阅记录过大，请减少区域或历史内容后重试。");

    private JsonElement BuildResultSummary(string resultId, ResultResource result)
    {
        var exported = ResultExporter.ToJson(result.Scoring);
        using var document = JsonDocument.Parse(exported);
        return ToJsonElement(new
        {
            resultId,
            version = result.Version,
            schoolMetadata = ToSchoolMetadata(result.Layout.SchoolMetadata),
            candidateId = result.Recognition.CandidateIdentity is { RequiresReview: false } identity ? identity.CandidateId : null,
            identityStatus = result.Layout.SchemaVersion == 3 ? (result.Recognition.CandidateIdentity is { RequiresReview: false, CandidateId: not null } ? "Identified" : "RequireAssociation") : null,
            result = document.RootElement.Clone()
        });
    }

    private static CaptureResource FindCapture(GrantResources resources, string captureId)
    {
        if (resources.TryGetCapture(captureId, out var capture))
        {
            return capture;
        }

        throw new LocalOperationException("CAPTURE_NOT_FOUND", "找不到此授权下的采集记录，请重新上传或扫描。");
    }

    private static ResultResource FindResult(GrantResources resources, string resultId)
    {
        if (resources.TryGetResult(resultId, out var result))
        {
            return result;
        }

        throw new LocalOperationException("RESULT_NOT_FOUND", "找不到此授权下的识别结果，请重新识别。");
    }

    private static AnswerSheetLayout FindTemplate(GrantResources resources, string templateId)
    {
        if (resources.TryGetTemplate(templateId, out var layout))
        {
            return layout;
        }

        throw new LocalOperationException("TEMPLATE_NOT_FOUND", "模板已失效，请重新获取或创建模板后重试。");
    }

    private GrantResources GetGrantResources(string grantId)
    {
        lock (_grantResourcesGate)
        {
            var now = DateTimeOffset.UtcNow;
            PruneRevokedGrantMarkers(now);
            if (_revokedGrantIds.ContainsKey(grantId))
            {
                throw new LocalOperationException("INVALID_GRANT", "当前本地授权已撤销或过期，请重新配对后重试。");
            }

            if (!_resources.TryGetValue(grantId, out var resources))
            {
                resources = new GrantResources(_defaultTemplate);
                _resources.Add(grantId, resources);
            }

            return resources;
        }
    }

    private void PruneRevokedGrantMarkers(DateTimeOffset now)
    {
        foreach (var expiredGrantId in _revokedGrantIds
            .Where(pair => pair.Value <= now)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _revokedGrantIds.Remove(expiredGrantId);
        }

        if (_revokedGrantIds.Count <= MaximumRevokedGrantMarkers)
        {
            return;
        }

        foreach (var oldestGrantId in _revokedGrantIds
            .OrderBy(pair => pair.Value)
            .Take(_revokedGrantIds.Count - MaximumRevokedGrantMarkers)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _revokedGrantIds.Remove(oldestGrantId);
        }
    }

    private string ValidateScannerOutputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new LocalOperationException("SCAN_OUTPUT_INVALID", "扫描仪没有返回有效图像。");
        }

        var pendingRoot = Path.GetFullPath(Path.Combine(_captureStore.RootDirectory, "pending-scans"));
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(pendingRoot, fullPath);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new LocalOperationException("SCAN_OUTPUT_INVALID", "扫描仪返回的图像位置无效，未访问该文件。");
        }

        return fullPath;
    }

    private static object ToTemplateSummary(AnswerSheetLayout layout)
    {
        var summary = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["templateId"] = layout.TemplateId,
            ["schemaVersion"] = layout.SchemaVersion,
            ["paper"] = layout.SchoolDefinition?.Paper.ToString() ?? "A4Portrait",
            ["side"] = layout.SchoolMetadata?.Side.ToString() ?? "Front",
            ["pageIndex"] = layout.SchoolPageIndex,
            ["widthMm"] = layout.WidthMm,
            ["heightMm"] = layout.HeightMm,
            ["schoolMetadata"] = ToSchoolMetadata(layout.SchoolMetadata),
            ["title"] = layout.Title,
            ["questionCount"] = layout.QuestionCount,
            ["optionsPerQuestion"] = layout.OptionsPerQuestion,
            ["templateNumber"] = layout.TemplateNumber,
            ["subjectiveRegions"] = layout.SubjectiveRegions.Select(region => new
            {
                questionId = region.QuestionId,
                questionNumber = region.QuestionNumber,
                maxScore = region.MaximumScore,
                rectangleMm = new
                {
                    x = region.Rectangle.X,
                    y = region.Rectangle.Y,
                    width = region.Rectangle.Width,
                    height = region.Rectangle.Height
                }
            }).ToArray(),
            ["svg"] = layout.ToSvg()
        };

        if (layout.SchoolDefinition is not null)
        {
            summary["schoolGroups"] = layout.SchoolGroups.Select(group => new
            {
                groupId = group.GroupId,
                title = group.Title,
                questionNumbers = group.QuestionNumbers,
                rectangleMm = new
                {
                    x = group.RectangleMm.X,
                    y = group.RectangleMm.Y,
                    width = group.RectangleMm.Width,
                    height = group.RectangleMm.Height
                }
            }).ToArray();
        }

        return summary;
    }

    private static object? ToSchoolMetadata(SchoolPageMetadata? metadata) => metadata is null ? null : new
    {
        metadata.ExamId,
        metadata.LayoutDocumentId,
        metadata.Version,
        metadata.PageNumber,
        side = metadata.Side.ToString(),
        metadata.TemplateId
    };

    private static JsonElement ToCaptureSummary(CaptureRecord capture) => ToJsonElement(new
    {
        captureId = capture.Manifest.CaptureId,
        templateId = capture.Manifest.TemplateId,
        createdAt = capture.Manifest.CreatedAtUtc,
        sourceType = capture.Manifest.SourceType,
        imageWidth = capture.Manifest.PixelWidth,
        imageHeight = capture.Manifest.PixelHeight,
        byteLength = capture.Manifest.ByteLength,
        schoolMetadata = ToSchoolMetadata(capture.Manifest.SchoolMetadata),
        candidateId = capture.Manifest.CandidateId,
        identityStatus = capture.Manifest.IdentityStatus
    });

    private static JsonElement ToJsonElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, AgentJson.Options);

    private static JsonElement RequireObject(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            throw new LocalOperationException("INVALID_PARAMETERS", "操作参数格式无效。");
        }

        return parameters;
    }

    private static IReadOnlySet<string> GetAllowedParameterNames(TaskOperation operation)
    {
        return operation switch
        {
            TaskOperation.Template => Names("title", "questionCount", "optionsPerQuestion", "subjectiveRegions", "schoolDefinition"),
            TaskOperation.Upload => Names("templateId", "fileName"),
            TaskOperation.Scan => Names("templateId", "deviceId", "dpi"),
            TaskOperation.Recognize => Names("captureId"),
            TaskOperation.Score => Names("resultId", "answerKey", "pointsPerQuestion"),
            TaskOperation.Review => Names("resultId", "expectedVersion", "reviewer", "edits", "answerKey"),
            TaskOperation.Export => Names("resultId", "format"),
            TaskOperation.SubjectiveCreate => Names("captureId"),
            TaskOperation.SubjectiveRead => Names("reviewId"),
            TaskOperation.SubjectiveGrade => Names("reviewId", "expectedVersion", "reviewer", "edits"),
            TaskOperation.SubjectiveExport => Names("reviewId", "format"),
            _ => new HashSet<string>(StringComparer.Ordinal)
        };
    }

    private static IReadOnlySet<string> Names(params string[] names) => new HashSet<string>(names, StringComparer.Ordinal);

    private static void RejectUnknownOrPathProperties(JsonElement parameters, IReadOnlySet<string> allowedNames)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in parameters.EnumerateObject())
        {
            if (!seen.Add(property.Name) || !allowedNames.Contains(property.Name)
                || property.Name.Contains("path", StringComparison.OrdinalIgnoreCase))
            {
                throw new LocalOperationException("INVALID_PARAMETERS", "操作参数包含不受支持的字段。");
            }
        }
    }

    private static SchoolSheetDefinition ReadSchoolDefinition(JsonElement value)
    {
        RejectUnknownOrPathProperties(RequireObject(value), Names("examId", "layoutDocumentId", "version", "title", "paper", "mode", "columns", "bubbleShape", "labelPlacement", "bubbleWidthMm", "bubbleHeightMm", "candidateIdentity", "duplex", "repeatBackIdentity", "groups", "layoutOrder", "questions"));
        if (value.TryGetProperty("candidateIdentity", out var identity))
            RejectUnknownOrPathProperties(RequireObject(identity), Names("mode", "digits", "candidateId"));
        if (value.TryGetProperty("groups", out var groups))
        {
            if (groups.ValueKind != JsonValueKind.Array)
                throw new LocalOperationException("INVALID_TEMPLATE", "题目组定义格式无效。");
            foreach (var group in groups.EnumerateArray())
                RejectUnknownOrPathProperties(RequireObject(group), Names("id", "title", "questionNumbers"));
        }
        if (!value.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
            throw new LocalOperationException("INVALID_TEMPLATE", "请提供答题纸题目结构。");
        foreach (var question in questions.EnumerateArray())
            RejectUnknownOrPathProperties(RequireObject(question), Names("number", "type", "maximumScore", "body", "options", "subjectiveHeightMm"));
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
                TypeInfoResolver = new DefaultJsonTypeInfoResolver
                {
                    Modifiers = { info =>
                    {
                        // Requests may omit fields with published defaults. Persisted snapshots remain strict.
                        if (info.Type == typeof(SchoolSheetDefinition) || info.Type == typeof(SchoolQuestionDefinition) || info.Type == typeof(SchoolCandidateIdentity))
                            foreach (var property in info.Properties) property.IsRequired = false;
                    } }
                }
            };
            var definition = value.Deserialize<SchoolSheetDefinition>(options) ?? throw new JsonException();
            return SchoolAnswerSheet.Create(definition).Pages[0].SchoolDefinition!;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new LocalOperationException("INVALID_TEMPLATE", "答题纸定义无效，请检查考试标识、纸张、题目和填涂框尺寸。");
        }
    }

    private static string ReadString(JsonElement parameters, string name)
    {
        if (!parameters.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new LocalOperationException("INVALID_PARAMETERS", $"参数 {name} 缺失或无效。");
        }

        return value.GetString()!.Trim();
    }

    private static int ReadPositiveInt(JsonElement parameters, string name)
    {
        if (!parameters.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var number)
            || number <= 0)
        {
            throw new LocalOperationException("INVALID_PARAMETERS", $"参数 {name} 缺失或无效。");
        }

        return number;
    }

    private static decimal? ReadOptionalPositiveDecimal(JsonElement parameters, string name)
    {
        if (!parameters.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDecimal(out var number)
            || number <= 0m)
        {
            throw new LocalOperationException("INVALID_PARAMETERS", $"参数 {name} 必须大于零。");
        }

        return number;
    }

    private static string ValidateFileName(string fileName)
    {
        if (fileName.Length > 255
            || fileName.Contains('/')
            || fileName.Contains('\\')
            || Path.IsPathRooted(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || !(fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)))
        {
            throw new LocalOperationException("INVALID_IMAGE_NAME", "文件名必须是 PNG 或 JPEG 文件名，不能包含文件夹路径。");
        }

        return fileName;
    }

    private static async Task<BufferedInputImageFile> BufferUploadAsync(
        Stream source,
        string fileName,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumUploadBytes)
            {
                throw new LocalOperationException("UPLOAD_TOO_LARGE", "上传图像不能超过 20 MiB。");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return new BufferedInputImageFile(fileName, buffer.ToArray());
    }

    private static AnswerKey ReadAnswerKey(JsonElement parameters, AnswerSheetLayout layout)
    {
        if (!parameters.TryGetProperty("answerKey", out var answers)
            || answers.ValueKind != JsonValueKind.Object)
        {
            throw new LocalOperationException("INVALID_ANSWER_KEY", "请提供完整的标准答案。");
        }

        var values = new Dictionary<int, string>();
        foreach (var property in answers.EnumerateObject())
        {
            if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var questionNumber)
                || property.Value.ValueKind != JsonValueKind.String
                || !values.TryAdd(questionNumber, property.Value.GetString() ?? string.Empty))
            {
                throw new LocalOperationException("INVALID_ANSWER_KEY", "标准答案格式无效，请检查题号和选项。");
            }
        }

        try
        {
            if (layout.SchoolDefinition is {} definition)
            {
                var allChoice = definition.Questions.Where(question => question.Type == SchoolQuestionType.Choice).ToArray();
                if (values.Count == allChoice.Length && allChoice.All(question => values.ContainsKey(question.Number)))
                {
                    if (allChoice.Any(question => values[question.Number] is not { Length: 1 } answer
                        || answer[0] < 'A' || answer[0] >= 'A' + question.Options!.Count))
                        throw new ArgumentException("考试标准答案包含无效选项。");
                    values = layout.Questions.ToDictionary(question => question.Number, question => values[question.Number]);
                }
            }
            return AnswerKey.Create(layout, values);
        }
        catch (Exception exception)
        {
            LogSafeException("answer-key", exception);
            throw new LocalOperationException("INVALID_ANSWER_KEY", "请为每道题提供有效的标准答案选项。");
        }
    }

    private static void LogSafeException(string module, Exception exception)
    {
        System.Diagnostics.Debug.WriteLine(
            $"Desktop local agent {module} failed: {exception.GetType().Name} (0x{exception.HResult:X8}).");
    }

    private static AnswerKey? ReadOptionalAnswerKey(JsonElement parameters, AnswerSheetLayout layout)
    {
        if (!parameters.TryGetProperty("answerKey", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ReadAnswerKey(parameters, layout);
    }

    private static Guid ReadGuid(JsonElement parameters, string name)
    {
        var value = ReadString(parameters, name);
        if (!Guid.TryParse(value, out var parsed) || parsed == Guid.Empty)
        {
            throw new LocalOperationException("INVALID_PARAMETERS", $"参数 {name} 缺失或无效。");
        }

        return parsed;
    }

    private static long ReadNonNegativeInt64(JsonElement parameters, string name)
    {
        if (!parameters.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var parsed)
            || parsed < 0)
        {
            throw new LocalOperationException("INVALID_PARAMETERS", $"参数 {name} 缺失或无效。");
        }

        return parsed;
    }

    private static TemplateSubjectiveRegion[] ReadTemplateSubjectiveRegions(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("subjectiveRegions", out var regions))
        {
            return [];
        }

        if (regions.ValueKind != JsonValueKind.Array
            || regions.GetArrayLength() > SubjectiveGradingSnapshot.MaximumQuestionCount)
        {
            throw new LocalOperationException("INVALID_TEMPLATE", "主观题区域最多可定义 64 道，请检查模板参数。");
        }

        var definitions = new List<TemplateSubjectiveRegion>(regions.GetArrayLength());
        foreach (var region in regions.EnumerateArray())
        {
            RequireExactProperties(region, "questionNumber", "maxScore", "rectangleMm");
            if (!region.TryGetProperty("rectangleMm", out var rectangle))
            {
                throw new LocalOperationException("INVALID_TEMPLATE", "主观题区域缺少毫米坐标。");
            }

            RequireExactProperties(rectangle, "x", "y", "width", "height");
            try
            {
                definitions.Add(TemplateSubjectiveRegion.Create(
                    ReadRequiredInt32(region, "questionNumber"),
                    ReadRequiredDecimal(region, "maxScore"),
                    new RectMm(
                        ReadRequiredFiniteDouble(rectangle, "x"),
                        ReadRequiredFiniteDouble(rectangle, "y"),
                        ReadRequiredFiniteDouble(rectangle, "width"),
                        ReadRequiredFiniteDouble(rectangle, "height"))));
            }
            catch (ArgumentException)
            {
                throw new LocalOperationException("INVALID_TEMPLATE", "主观题题号、毫米区域或满分超出允许范围。");
            }
        }

        return definitions.ToArray();
    }

    private static double ReadRequiredFiniteDouble(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetDouble(out var parsed)
            || !double.IsFinite(parsed))
        {
            throw new LocalOperationException("INVALID_TEMPLATE", $"参数 {name} 必须是有限的毫米数。");
        }

        return parsed;
    }

    private static SubjectiveGradeEdit[] ReadSubjectiveGradeEdits(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("edits", out var edits)
            || edits.ValueKind != JsonValueKind.Array
            || edits.GetArrayLength() is < 1 or > SubjectiveGradingSnapshot.MaximumQuestionCount)
        {
            throw new LocalOperationException("INVALID_SUBJECTIVE_GRADE", "请提供一条或多条有效的评分修改。");
        }

        var parsed = new List<SubjectiveGradeEdit>(edits.GetArrayLength());
        foreach (var edit in edits.EnumerateArray())
        {
            RequireExactProperties(edit, "questionId", "status", "score", "comment");
            var questionIdText = ReadRequiredString(edit, "questionId");
            if (!Guid.TryParse(questionIdText, out var questionId) || questionId == Guid.Empty)
            {
                throw new LocalOperationException("INVALID_SUBJECTIVE_GRADE", "评分修改中的题目 ID 无效。");
            }

            var status = ReadRequiredString(edit, "status") switch
            {
                "ungraded" => SubjectiveReviewStatus.Unreviewed,
                "draft" => SubjectiveReviewStatus.Draft,
                "confirmed" => SubjectiveReviewStatus.Confirmed,
                _ => throw new LocalOperationException("INVALID_SUBJECTIVE_GRADE", "评分状态无效。")
            };

            if (!edit.TryGetProperty("score", out var scoreValue))
            {
                throw new LocalOperationException("INVALID_SUBJECTIVE_GRADE", "评分修改缺少分值字段。");
            }

            decimal? score = null;
            if (scoreValue.ValueKind == JsonValueKind.Number)
            {
                if (!scoreValue.TryGetDecimal(out var parsedScore))
                {
                    throw new LocalOperationException("INVALID_SUBJECTIVE_GRADE", "分值格式无效。");
                }

                score = parsedScore;
            }
            else if (scoreValue.ValueKind != JsonValueKind.Null)
            {
                throw new LocalOperationException("INVALID_SUBJECTIVE_GRADE", "分值必须为数字或空值。");
            }

            if (!edit.TryGetProperty("comment", out var commentValue)
                || (commentValue.ValueKind != JsonValueKind.String && commentValue.ValueKind != JsonValueKind.Null))
            {
                throw new LocalOperationException("INVALID_SUBJECTIVE_GRADE", "评语必须是文本或空值。");
            }

            var comment = commentValue.ValueKind == JsonValueKind.String ? commentValue.GetString() : null;
            if (string.IsNullOrEmpty(comment))
            {
                comment = null;
            }

            parsed.Add(new SubjectiveGradeEdit(questionId, status, score, comment));
        }

        return parsed.ToArray();
    }

    private static void RequireExactProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new LocalOperationException("INVALID_PARAMETERS", "操作参数包含缺失或不受支持的字段。");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
            {
                throw new LocalOperationException("INVALID_PARAMETERS", "操作参数包含重复或不受支持的字段。");
            }
        }

        if (names.Any(name => !seen.Contains(name)))
        {
            throw new LocalOperationException("INVALID_PARAMETERS", "操作参数缺少必要字段。");
        }
    }

    private static string ReadRequiredString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new LocalOperationException("INVALID_PARAMETERS", $"参数 {name} 缺失或无效。");
        }

        return property.GetString()!;
    }

    private static int ReadRequiredInt32(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var parsed))
        {
            throw new LocalOperationException("INVALID_PARAMETERS", $"参数 {name} 缺失或无效。");
        }

        return parsed;
    }

    private static decimal ReadRequiredDecimal(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetDecimal(out var parsed))
        {
            throw new LocalOperationException("INVALID_PARAMETERS", $"参数 {name} 缺失或无效。");
        }

        return parsed;
    }

    private static IReadOnlyList<ReviewEdit> ReadReviewEdits(JsonElement parameters, AnswerSheetLayout layout)
    {
        if (!parameters.TryGetProperty("edits", out var edits)
            || edits.ValueKind != JsonValueKind.Array
            || edits.GetArrayLength() == 0
            || edits.GetArrayLength() > layout.QuestionCount)
        {
            throw new LocalOperationException("INVALID_REVIEW", "请提供一条或多条有效的复核修改。");
        }

        var parsed = new List<ReviewEdit>(edits.GetArrayLength());
        var questionNumbers = new HashSet<int>();
        foreach (var edit in edits.EnumerateArray())
        {
            if (edit.ValueKind != JsonValueKind.Object
                || edit.EnumerateObject().Any(property => property.Name is not "questionNumber" and not "answer" and not "reason" and not "timestampUtc"))
            {
                throw new LocalOperationException("INVALID_REVIEW", "复核修改包含无效字段。");
            }

            if (!edit.TryGetProperty("questionNumber", out var questionValue)
                || questionValue.ValueKind != JsonValueKind.Number
                || !questionValue.TryGetInt32(out var questionNumber)
                || questionNumber <= 0
                || !layout.Questions.Any(question => question.Number == questionNumber)
                || !questionNumbers.Add(questionNumber))
            {
                throw new LocalOperationException("INVALID_REVIEW", "复核题号无效或重复。");
            }

            if (!edit.TryGetProperty("answer", out _))
            {
                throw new LocalOperationException("INVALID_REVIEW", "复核修改缺少答案字段。");
            }

            string? answer = null;
            if (edit.TryGetProperty("answer", out var answerValue))
            {
                if (answerValue.ValueKind == JsonValueKind.String)
                {
                    answer = answerValue.GetString();
                }
                else if (answerValue.ValueKind != JsonValueKind.Null)
                {
                    throw new LocalOperationException("INVALID_REVIEW", "复核答案必须是选项文本或空值。");
                }
            }

            string? reason = null;
            if (edit.TryGetProperty("reason", out var reasonValue))
            {
                if (reasonValue.ValueKind != JsonValueKind.String)
                {
                    throw new LocalOperationException("INVALID_REVIEW", "复核原因必须是文本。");
                }

                reason = reasonValue.GetString();
            }
            DateTimeOffset? timestampUtc = null;
            if (edit.TryGetProperty("timestampUtc", out var timestampValue))
            {
                if (timestampValue.ValueKind != JsonValueKind.String
                    || !DateTimeOffset.TryParse(
                        timestampValue.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var parsedTimestamp))
                {
                    throw new LocalOperationException("INVALID_REVIEW", "复核时间格式无效。");
                }

                timestampUtc = parsedTimestamp;
            }

            parsed.Add(new ReviewEdit(questionNumber, answer, reason, timestampUtc));
        }

        return parsed;
    }

    private sealed class GrantResources
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _revocation = new();
        private readonly Dictionary<string, AnswerSheetLayout> _templates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CaptureResource> _captures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ResultResource> _results = new(StringComparer.Ordinal);
        private bool _isRevoked;

        public GrantResources(AnswerSheetLayout defaultTemplate)
        {
            _templates.Add(defaultTemplate.TemplateId, defaultTemplate);
        }

        public SemaphoreSlim SubjectiveGate { get; } = new(1, 1);

        public CancellationToken RevocationToken => _revocation.Token;

        public void AddTemplate(AnswerSheetLayout layout)
        {
            lock (_gate)
            {
                ThrowIfRevoked();
                _templates[layout.TemplateId] = layout;
            }
        }

        public void AddTemplates(IReadOnlyList<AnswerSheetLayout> pages)
        {
            lock (_gate)
            {
                ThrowIfRevoked();
                foreach (var page in pages) _templates[page.TemplateId] = page;
            }
        }

        public bool TryGetTemplate(string templateId, out AnswerSheetLayout layout)
        {
            lock (_gate)
            {
                if (!_isRevoked && _templates.TryGetValue(templateId, out layout!))
                {
                    return true;
                }

                layout = null!;
                return false;
            }
        }

        public void AddCapture(string captureId, CaptureResource capture)
        {
            lock (_gate)
            {
                ThrowIfRevoked();
                if (!_captures.TryAdd(captureId, capture))
                {
                    throw new LocalOperationException("CAPTURE_ID_CONFLICT", "采集记录 ID 冲突，请重试。");
                }
            }
        }

        public bool TryGetCapture(string captureId, out CaptureResource capture)
        {
            lock (_gate)
            {
                if (!_isRevoked && _captures.TryGetValue(captureId, out capture!))
                {
                    return true;
                }

                capture = null!;
                return false;
            }
        }

        public void AddResult(string resultId, ResultResource result)
        {
            lock (_gate)
            {
                ThrowIfRevoked();
                if (!_results.TryAdd(resultId, result))
                {
                    throw new LocalOperationException("RESULT_ID_CONFLICT", "识别结果 ID 冲突，请重新识别。");
                }
            }
        }

        public bool TryGetResult(string resultId, out ResultResource result)
        {
            lock (_gate)
            {
                if (!_isRevoked && _results.TryGetValue(resultId, out result!))
                {
                    return true;
                }

                result = null!;
                return false;
            }
        }

        public void Revoke()
        {
            lock (_gate)
            {
                if (_isRevoked)
                {
                    return;
                }

                _isRevoked = true;
            }

            _ = CancelRevocationAsync(_revocation);
        }

        private void ThrowIfRevoked()
        {
            if (_isRevoked)
            {
                throw new LocalOperationException("INVALID_GRANT", "当前本地授权已撤销或过期，请重新配对后重试。");
            }
        }

        private static async Task CancelRevocationAsync(CancellationTokenSource revocation)
        {
            try
            {
                await revocation.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // A disposed source has no remaining operations to cancel.
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Desktop agent grant cancellation callback failed: {exception.GetType().Name} (0x{exception.HResult:X8}).");
            }
        }
    }

    private sealed record CaptureResource(CaptureRecord Record, AnswerSheetLayout Layout);

    private sealed class ResultResource
    {
        public ResultResource(AnswerSheetLayout layout, RecognitionResult recognition)
        {
            Layout = layout;
            Recognition = recognition;
            Review = ManualReviewSession.Start(recognition);
            Scoring = ScoringEngine.Score(Review);
        }

        public object SyncRoot { get; } = new();

        public AnswerSheetLayout Layout { get; }

        public RecognitionResult Recognition { get; }

        public ManualReviewSession Review { get; set; }

        public AnswerKey? AnswerKey { get; set; }

        public ScoringResult Scoring { get; set; }

        public int Version { get; set; } = 1;
    }

    private sealed record ReviewEdit(
        int QuestionNumber,
        string? Answer,
        string? Reason,
        DateTimeOffset? TimestampUtc);

    private sealed class BufferedInputImageFile(string name, byte[] content) : IInputImageFile
    {
        public string Name { get; } = name;

        public ulong Length { get; } = checked((ulong)content.LongLength);

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false));
        }
    }
}
