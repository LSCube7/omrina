using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Omrina.Core;
using Omrina.Desktop;
using Omrina.M3.Desktop.Tests;
using Omrina.Platform;
using Omrina.Protocol;
using Omrina.Scanning;
using Omrina.Server;
using SkiaSharp;

if (args.Contains("--http-harness", StringComparer.Ordinal))
{
    return await RunHttpHarnessAsync();
}

try
{
    SubjectiveReviewInputRegression.Run();
    VerifyScannerDeviceIdentityMap();
    await VerifyDesktopAgentOperationsAsync();
    await VerifyActualSkiaRejectsSyntheticFixtureAsync();
    await VerifySubjectiveImageCropperAsync();
    Console.WriteLine("PASS: scanner identity/revocation, M3 workflow, M5 trusted/web subjective store workflows, parser boundaries, PNG/JPEG crop pixels, and actual Skia rejection without scoring.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL: {exception.GetType().Name}: {exception.Message}");
    return 1;
}

static void VerifyScannerDeviceIdentityMap()
{
    using var identityMap = new ScannerDeviceIdentityMap<string>();
    var initial = identityMap.ReplaceDevices(
        [("WIA", "native-a", "scanner A"), ("WIA", "native-b", "scanner B")],
        out var missingIdentityCount,
        out var duplicateIdentityCount);
    AssertEqual(0, missingIdentityCount, "first scanner enumeration should have stable identities");
    AssertEqual(0, duplicateIdentityCount, "first scanner enumeration should not contain duplicates");
    AssertEqual(2, initial.Count, "both native scanners should be visible");

    var scannerAId = initial.Single(entry => entry.NativeDevice == "scanner A").DeviceId;
    var scannerBId = initial.Single(entry => entry.NativeDevice == "scanner B").DeviceId;
    AssertEqual(72, scannerAId.Length, "public device ID should contain a prefix and a full SHA-256 HMAC");
    AssertTrue(!scannerAId.Contains("native-a", StringComparison.Ordinal), "public device IDs must not expose native IDs");

    var reordered = identityMap.ReplaceDevices(
        [("WIA", "native-b", "scanner B"), ("WIA", "native-a", "scanner A")],
        out _,
        out _);
    AssertEqual(scannerAId, reordered.Single(entry => entry.NativeDevice == "scanner A").DeviceId, "scanner A identity should survive list reordering");
    AssertEqual(scannerBId, reordered.Single(entry => entry.NativeDevice == "scanner B").DeviceId, "scanner B identity should survive list reordering");

    var onlyB = identityMap.ReplaceDevices(
        [("WIA", "native-b", "scanner B")],
        out _,
        out _);
    AssertEqual(1, onlyB.Count, "the disappeared scanner should be removed from the active set");
    AssertTrue(!identityMap.TryGetNativeDevice(scannerAId, out _), "the disappeared scanner's old ID must not resolve");
    AssertTrue(identityMap.TryGetNativeDevice(scannerBId, out var remainingDevice), "the remaining scanner ID should resolve");
    AssertEqual("scanner B", remainingDevice, "the remaining scanner ID must keep its own native mapping");

    var sameNativeIdAcrossDrivers = identityMap.ReplaceDevices(
        [("WIA", "shared-native-id", "WIA scanner"), ("TWAIN", "shared-native-id", "TWAIN scanner")],
        out _,
        out _);
    AssertEqual(2, sameNativeIdAcrossDrivers.Count, "the driver name should participate in identity hashing");
    AssertTrue(
        sameNativeIdAcrossDrivers[0].DeviceId != sameNativeIdAcrossDrivers[1].DeviceId,
        "the same native ID under different drivers should receive different public IDs");

    var filtered = identityMap.ReplaceDevices(
        [
            ("WIA", "duplicate-id", "first duplicate"),
            ("WIA", "duplicate-id", "second duplicate"),
            ("WIA", null, "missing native ID"),
            (" ", "missing-driver", "missing driver")
        ],
        out missingIdentityCount,
        out duplicateIdentityCount);
    AssertEqual(2, missingIdentityCount, "missing identities should be counted and skipped");
    AssertEqual(2, duplicateIdentityCount, "all candidates for a duplicated native identity should be counted and skipped");
    AssertEqual(0, filtered.Count, "ambiguous native identities should not leave any device mapped");

    var duplicateIdentityId = identityMap.ReplaceDevices(
        [("WIA", "same-native-id", "scanner probe")],
        out _,
        out _).Single().DeviceId;
    AssertDuplicateDevices([
        ("WIA", "same-native-id", "scanner A"),
        ("WIA", "same-native-id", "scanner B")
    ]);
    AssertDuplicateDevices([
        ("WIA", "same-native-id", "scanner B"),
        ("WIA", "same-native-id", "scanner A")
    ]);
    AssertDuplicateDevices([
        ("WIA", "same-native-id", "scanner A"),
        ("WIA", "same-native-id", "scanner B"),
        ("WIA", "same-native-id", "scanner C")
    ]);

    void AssertDuplicateDevices((string? Driver, string? NativeId, string NativeDevice)[] duplicateCandidates)
    {
        var ambiguous = identityMap.ReplaceDevices(duplicateCandidates, out _, out var skippedDuplicates);
        AssertEqual(0, ambiguous.Count, "all scanners sharing a duplicated native identity must remain unavailable");
        AssertEqual(duplicateCandidates.Length, skippedDuplicates, "every ambiguous candidate should be counted as skipped");
        AssertTrue(!identityMap.TryGetNativeDevice(duplicateIdentityId, out _), "ambiguous scanner IDs must not resolve after duplicate filtering");
    }

    using var nextSession = new ScannerDeviceIdentityMap<string>();
    var nextSessionEntry = nextSession.ReplaceDevices(
        [("WIA", "native-a", "scanner A")],
        out _,
        out _).Single();
    AssertTrue(scannerAId != nextSessionEntry.DeviceId, "public scanner IDs should be scoped to the current app session");
}

static async Task VerifyDesktopAgentOperationsAsync()
{
    var rootDirectory = Path.Combine(Path.GetTempPath(), $"omrina-m3-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(rootDirectory);
    try
    {
        var imageBytes = CreateSyntheticPng(40, 40);
        var scanner = new MockScannerService(rootDirectory, imageBytes);
        var recognition = new FixedRecognitionService();
        var store = new CaptureStore(rootDirectory);
        var operations = new DesktopLocalAgentOperations(
            store,
            scanner,
            recognition,
            path =>
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            });

        var staticTemplates = await operations.GetTemplatesAsync(CancellationToken.None);
        AssertEqual(JsonValueKind.Array, staticTemplates.ValueKind, "template catalog should be an array");
        AssertEqual(1, staticTemplates.GetArrayLength(), "catalog should expose one static default template");
        AssertEqual(
            "OMRINA 默认答题纸",
            staticTemplates[0].GetProperty("title").GetString(),
            "catalog must not expose a previous grant's custom title");
        AssertTrue(
            staticTemplates[0].GetProperty("svg").GetString()?.Contains("<svg", StringComparison.Ordinal) == true,
            "default template should include SVG content");

        var devices = await operations.GetDevicesAsync(CancellationToken.None);
        AssertEqual("mock:scanner", devices[0].GetProperty("deviceId").GetString(), "device list should expose the mock scanner ID");

        const string grantA = "grant-a";
        const string grantB = "grant-b";
        var customTemplate = await RunAsync(
            operations,
            grantA,
            TaskOperation.Template,
            new { title = "合成试卷", questionCount = 1, optionsPerQuestion = 2 });
        var customTemplateId = customTemplate.GetProperty("templateId").GetString()
            ?? throw new InvalidOperationException("template result omitted its ID");
        AssertTrue(
            customTemplate.GetProperty("svg").GetString()?.Contains(customTemplateId, StringComparison.Ordinal) == true,
            "custom template SVG should carry the generated template ID");
        AssertEqual(
            1,
            (await operations.GetTemplatesAsync(CancellationToken.None)).GetArrayLength(),
            "a created title must not enter the process-wide template catalog");

        await AssertOperationErrorAsync(
            () => RunAsync(
                operations,
                grantB,
                TaskOperation.Upload,
                new { templateId = customTemplateId, fileName = "synthetic.png" },
                new MemoryStream(imageBytes)),
            "TEMPLATE_NOT_FOUND");

        await AssertOperationErrorAsync(
            () => RunAsync(
                operations,
                grantA,
                TaskOperation.Upload,
                new { templateId = customTemplateId, fileName = "C:/private/synthetic.png" },
                new MemoryStream(imageBytes)),
            "INVALID_IMAGE_NAME");

        await AssertOperationErrorAsync(
            () => RunAsync(
                operations,
                grantA,
                TaskOperation.Recognize,
                new { captureId = "missing", imagePath = "C:/private/original.png" }),
            "INVALID_PARAMETERS");

        using var uploadBody = new MemoryStream(imageBytes, writable: false);
        var upload = await RunAsync(
            operations,
            grantA,
            TaskOperation.Upload,
            new { templateId = customTemplateId, fileName = "synthetic.png" },
            uploadBody);
        var uploadCaptureId = upload.GetProperty("captureId").GetString()
            ?? throw new InvalidOperationException("upload result omitted its capture ID");
        AssertTrue(!JsonSerializer.Serialize(upload).Contains("ImageFilePath", StringComparison.Ordinal), "capture result must not expose local paths");

        await AssertOperationErrorAsync(
            () => RunAsync(
                operations,
                grantB,
                TaskOperation.Recognize,
                new { captureId = uploadCaptureId }),
            "CAPTURE_NOT_FOUND");

        var defaultTemplateId = staticTemplates[0].GetProperty("templateId").GetString()
            ?? throw new InvalidOperationException("static template result omitted its ID");
        var scan = await RunAsync(
            operations,
            grantA,
            TaskOperation.Scan,
            new { templateId = defaultTemplateId, deviceId = "mock:scanner", dpi = 300 });
        var scanCaptureId = scan.GetProperty("captureId").GetString()
            ?? throw new InvalidOperationException("scan result omitted its capture ID");
        AssertEqual(1, scanner.ScanCount, "scan operation should call the mock scanner once");
        AssertEqual(300, scan.GetProperty("dpi").GetInt32(), "scan result should report the requested DPI");
        AssertTrue(scanner.LastOutputPath is not null && !File.Exists(scanner.LastOutputPath), "temporary scanner image should be deleted after persistence");
        AssertEqual("scan", store.LoadLatest()?.Manifest.SourceType, "scan output should be persisted as an original capture");

        var recognitionResult = await RunAsync(
            operations,
            grantA,
            TaskOperation.Recognize,
            new { captureId = uploadCaptureId });
        var resultId = recognitionResult.GetProperty("resultId").GetString()
            ?? throw new InvalidOperationException("recognition result omitted its ID");
        AssertEqual(1, recognitionResult.GetProperty("version").GetInt32(), "new recognition result should start at version 1");
        AssertEqual(1, recognition.RecognitionCount, "recognition should use the local recognition adapter");
        AssertTrue(
            recognitionResult.GetProperty("result").GetProperty("recognition").GetProperty("templateId").GetString() == customTemplateId,
            "recognition export should use the capture's associated template");

        await AssertOperationErrorAsync(
            () => RunAsync(
                operations,
                grantB,
                TaskOperation.Export,
                new { resultId, format = "json" }),
            "RESULT_NOT_FOUND");

        var scannedRecognition = await RunAsync(
            operations,
            grantA,
            TaskOperation.Recognize,
            new { captureId = scanCaptureId });
        AssertEqual(
            10,
            scannedRecognition.GetProperty("result").GetProperty("recognition").GetProperty("questions").GetArrayLength(),
            "simulated recognizer should return one answer for every question in the 10-question template");

        var score = await RunAsync(
            operations,
            grantA,
            TaskOperation.Score,
            new
            {
                resultId,
                answerKey = new Dictionary<int, string> { [1] = "A" },
                pointsPerQuestion = 2.5m
            });
        AssertEqual(2, score.GetProperty("version").GetInt32(), "scoring should advance the mutable result version");
        AssertEqual(
            2.5m,
            score.GetProperty("result").GetProperty("scoring").GetProperty("totalScore").GetDecimal(),
            "scoring should use the supplied answer key and points");

        var review = await RunAsync(
            operations,
            grantA,
            TaskOperation.Review,
            new
            {
                resultId,
                expectedVersion = 2,
                reviewer = "teacher",
                edits = new[]
                {
                    new
                    {
                        questionNumber = 1,
                        answer = "B",
                        reason = "复核修正",
                        timestampUtc = "2026-10-01T09:30:00Z"
                    }
                }
            });
        AssertEqual(3, review.GetProperty("version").GetInt32(), "manual review should advance the result version");
        AssertEqual(
            "复核修正",
            review.GetProperty("result").GetProperty("review").GetProperty("history")[0].GetProperty("reason").GetString(),
            "review reason should remain in the result audit");
        AssertEqual(
            0m,
            review.GetProperty("result").GetProperty("scoring").GetProperty("totalScore").GetDecimal(),
            "reviewed answer should be rescored against the stored answer key");

        await AssertOperationErrorAsync(
            () => RunAsync(
                operations,
                grantA,
                TaskOperation.Review,
                new
                {
                    resultId,
                    expectedVersion = 2,
                    reviewer = "teacher",
                    edits = new[] { new { questionNumber = 1, answer = "A", reason = "stale" } }
                }),
            "VERSION_CONFLICT");

        var export = await RunAsync(
            operations,
            grantA,
            TaskOperation.Export,
            new { resultId, format = "csv" });
        var csv = export.GetProperty("content").GetString() ?? string.Empty;
        AssertTrue(csv.Contains("teacher", StringComparison.Ordinal), "CSV export should include the reviewer");
        AssertTrue(csv.Contains("复核修正", StringComparison.Ordinal), "CSV export should include the review reason");
        AssertTrue(!csv.Contains(rootDirectory, StringComparison.Ordinal), "export should not contain the app-owned storage path");

        var jsonExport = await RunAsync(
            operations,
            grantA,
            TaskOperation.Export,
            new { resultId, format = "json" });
        using var exportedDocument = JsonDocument.Parse(jsonExport.GetProperty("content").GetString() ?? string.Empty);
        AssertEqual(
            "Final",
            exportedDocument.RootElement.GetProperty("scoring").GetProperty("disposition").GetString(),
            "JSON export should include the latest reviewed score");

        var webReviewId = await VerifySubjectiveReviewWorkflowAsync(
            operations,
            grantA,
            grantB,
            uploadCaptureId,
            rootDirectory);

        await VerifyLocalSubjectiveReviewServiceAsync(
            operations,
            grantA,
            grantB,
            uploadCaptureId,
            webReviewId,
            rootDirectory);

        operations.ForgetGrant(grantA);
        await AssertOperationErrorAsync(
            () => RunAsync(
                operations,
                grantA,
                TaskOperation.Recognize,
                new { captureId = uploadCaptureId }),
            "INVALID_GRANT");
    }
    finally
    {
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }
}

static async Task VerifyActualSkiaRejectsSyntheticFixtureAsync()
{
    var rootDirectory = Path.Combine(Path.GetTempPath(), $"omrina-m3-skia-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(rootDirectory);
    try
    {
        var imageBytes = CreateSyntheticPng(40, 40);
        var operations = new DesktopLocalAgentOperations(
            new CaptureStore(rootDirectory),
            new MockScannerService(rootDirectory, imageBytes),
            new SkiaAnswerSheetRecognitionService(),
            path =>
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            });
        var template = await RunAsync(
            operations,
            "skia-rejection-grant",
            TaskOperation.Template,
            new { title = "实际识别拒识测试", questionCount = 1, optionsPerQuestion = 2 });

        using var image = new MemoryStream(imageBytes, writable: false);
        var upload = await RunAsync(
            operations,
            "skia-rejection-grant",
            TaskOperation.Upload,
            new { templateId = template.GetProperty("templateId").GetString(), fileName = "synthetic.png" },
            image);
        var recognition = await RunAsync(
            operations,
            "skia-rejection-grant",
            TaskOperation.Recognize,
            new { captureId = upload.GetProperty("captureId").GetString() });

        AssertEqual(
            "Rejected",
            recognition.GetProperty("result").GetProperty("recognition").GetProperty("status").GetString(),
            "actual Skia recognition must reject the 40x40 synthetic fixture");

        var scoring = await RunAsync(
            operations,
            "skia-rejection-grant",
            TaskOperation.Score,
            new
            {
                resultId = recognition.GetProperty("resultId").GetString(),
                answerKey = new Dictionary<int, string> { [1] = "A" },
                pointsPerQuestion = 2m
            });
        var scoringSummary = scoring.GetProperty("result").GetProperty("scoring");
        AssertEqual(
            "RecognitionRejected",
            scoringSummary.GetProperty("unavailableReason").GetString(),
            "a rejected recognition must remain unscored after an answer key is supplied");
        AssertEqual(
            JsonValueKind.Null,
            scoringSummary.GetProperty("totalScore").ValueKind,
            "a rejected recognition must not produce a score");
    }
    finally
    {
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }
}

static async Task<Guid> VerifySubjectiveReviewWorkflowAsync(
    DesktopLocalAgentOperations operations,
    string ownerGrant,
    string otherGrant,
    string captureId,
    string storageRoot)
{
    var firstQuestionId = Guid.NewGuid();
    var secondQuestionId = Guid.NewGuid();
    await AssertOperationErrorAsync(
        () => RunAsync(
            operations,
            ownerGrant,
            TaskOperation.SubjectiveCreate,
            new
            {
                captureId,
                questions = new[]
                {
                    new
                    {
                        questionId = Guid.NewGuid(),
                        questionNumber = 1,
                        maxScore = 10m,
                        region = new { x = -1, y = 0, width = 8, height = 8 }
                    }
                }
            }),
        "INVALID_SUBJECTIVE_REVIEW");

    var created = await RunAsync(
        operations,
        ownerGrant,
        TaskOperation.SubjectiveCreate,
        new
        {
            captureId,
            questions = new[]
            {
                new
                {
                    questionId = firstQuestionId,
                    questionNumber = 1,
                    maxScore = 10m,
                    region = new { x = 0, y = 0, width = 8, height = 8 }
                },
                new
                {
                    questionId = secondQuestionId,
                    questionNumber = 2,
                    maxScore = 5m,
                    region = new { x = 20, y = 10, width = 10, height = 6 }
                }
            }
        });
    var reviewIdText = created.GetProperty("reviewId").GetString()
        ?? throw new InvalidOperationException("subjective create omitted reviewId");
    var reviewId = Guid.Parse(reviewIdText);
    AssertEqual(captureId, created.GetProperty("captureId").GetString(), "subjective document must bind to the authorized capture");
    AssertEqual(1L, created.GetProperty("version").GetInt64(), "subjective document must begin at version one");
    AssertEqual(2, created.GetProperty("questions").GetArrayLength(), "subjective document should preserve both reviewer-defined regions");
    AssertEqual("ungraded", created.GetProperty("questions")[0].GetProperty("status").GetString(), "new questions begin ungraded");
    AssertTrue(!created.TryGetProperty("totalScore", out _), "subjective documents must not invent a mixed OMR score");

    var image = await operations.ReadSubjectiveImageAsync(
        ownerGrant,
        reviewId,
        firstQuestionId,
        CancellationToken.None);
    using (var decodedCrop = SKBitmap.Decode(image.Bytes))
    {
        AssertTrue(decodedCrop is not null, "subjective image endpoint should return a decodable PNG");
        AssertEqual(8, decodedCrop!.Width, "subjective image width should match the selected pixel region");
        AssertEqual(8, decodedCrop.Height, "subjective image height should match the selected pixel region");
        AssertEqual(new SKColor(0, 0, 0, 255), decodedCrop.GetPixel(0, 0), "crop origin must preserve the source pixel at the selected x/y");
    }

    await AssertOperationErrorAsync(
        () => RunAsync(
            operations,
            otherGrant,
            TaskOperation.SubjectiveRead,
            new { reviewId = reviewIdText }),
        "SUBJECTIVE_REVIEW_NOT_FOUND");
    await AssertTaskErrorAsync(
        () => operations.ReadSubjectiveImageAsync(otherGrant, reviewId, firstQuestionId, CancellationToken.None),
        "SUBJECTIVE_REVIEW_NOT_FOUND");

    var storedPath = Path.Combine(storageRoot, "subjective-reviews", $"{reviewId:N}.json");
    using (File.Open(storedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        await AssertOperationErrorAsync(
            () => RunAsync(
                operations,
                ownerGrant,
                TaskOperation.SubjectiveGrade,
                new
                {
                    reviewId = reviewIdText,
                    expectedVersion = 1,
                    reviewer = "teacher",
                    edits = new[]
                    {
                        new { questionId = firstQuestionId, status = "draft", score = 8m, comment = "initial draft" }
                    }
                }),
            "SUBJECTIVE_REVIEW_STORE_FAILED");
    }

    var unchangedAfterFailedWrite = new SubjectiveReviewStore(storageRoot).LoadTrusted(reviewId)
        ?? throw new InvalidOperationException("store failure should leave the original review available");
    AssertEqual(1L, unchangedAfterFailedWrite.Snapshot.Version, "failed disk write must not advance the in-memory or persisted version");
    AssertEqual(0, unchangedAfterFailedWrite.Snapshot.History.Count, "failed disk write must not append history");

    var draft = await RunAsync(
        operations,
        ownerGrant,
        TaskOperation.SubjectiveGrade,
        new
        {
            reviewId = reviewIdText,
            expectedVersion = 1,
            reviewer = " \t=teacher",
            edits = new[]
            {
                new { questionId = firstQuestionId, status = "draft", score = 8m, comment = " \t=SUM(1,2)" },
                new { questionId = secondQuestionId, status = "draft", score = 4m, comment = "+unsafe" }
            }
        });
    AssertEqual(2L, draft.GetProperty("version").GetInt64(), "one atomic batch should increment the version once");
    AssertEqual("draft", draft.GetProperty("questions")[0].GetProperty("status").GetString(), "draft state should be explicit");

    var csv = await RunAsync(
        operations,
        ownerGrant,
        TaskOperation.SubjectiveExport,
        new { reviewId = reviewIdText, format = "csv" });
    var csvContent = csv.GetProperty("content").GetString() ?? string.Empty;
    AssertTrue(csvContent.Contains("\"'=teacher\"", StringComparison.Ordinal), "CSV must neutralize formula-like reviewer names");
    AssertTrue(csvContent.Contains("\"'=SUM(1,2)\"", StringComparison.Ordinal), "CSV must neutralize formula-like comments after leading whitespace");
    AssertTrue(csvContent.Contains("\"'+unsafe\"", StringComparison.Ordinal), "CSV must neutralize plus-prefixed comments");

    await AssertOperationErrorAsync(
        () => RunAsync(
            operations,
            ownerGrant,
            TaskOperation.SubjectiveGrade,
            new
            {
                reviewId = reviewIdText,
                expectedVersion = 1,
                reviewer = "teacher",
                edits = new[] { new { questionId = firstQuestionId, status = "draft", score = 8m, comment = "stale" } }
            }),
        "VERSION_CONFLICT");

    var confirmed = await RunAsync(
        operations,
        ownerGrant,
        TaskOperation.SubjectiveGrade,
        new
        {
            reviewId = reviewIdText,
            expectedVersion = 2,
            reviewer = "teacher",
            edits = new[]
            {
                new { questionId = firstQuestionId, status = "confirmed", score = 8m, comment = "=SUM(1,2)" },
                new { questionId = secondQuestionId, status = "confirmed", score = 4m, comment = "+unsafe" }
            }
        });
    AssertEqual(3L, confirmed.GetProperty("version").GetInt64(), "confirmation batch should advance the version once");
    AssertEqual(true, confirmed.GetProperty("isFinal").GetBoolean(), "all confirmed regions should form a final subjective document");
    AssertEqual(12m, confirmed.GetProperty("finalSubtotal").GetDecimal(), "final subtotal should sum only the subjective questions");

    var jsonExport = await RunAsync(
        operations,
        ownerGrant,
        TaskOperation.SubjectiveExport,
        new { reviewId = reviewIdText, format = "json" });
    using (var exported = JsonDocument.Parse(jsonExport.GetProperty("content").GetString() ?? string.Empty))
    {
        AssertEqual(3L, exported.RootElement.GetProperty("version").GetInt64(), "JSON export should contain the committed version");
        AssertEqual(2, exported.RootElement.GetProperty("history").GetArrayLength(), "JSON export should retain both audit batches");
    }

    var reloaded = new SubjectiveReviewStore(storageRoot).LoadTrusted(reviewId)
        ?? throw new InvalidOperationException("trusted store reload should find the saved review");
    AssertEqual(3L, reloaded.Snapshot.Version, "store reopen should restore the latest version");
    AssertEqual(2, reloaded.Snapshot.History.Count, "store reopen should restore audit history");
    AssertTrue(
        new SubjectiveReviewStore(storageRoot).LoadForGrant(reviewId, otherGrant) is null,
        "another grant must not adopt a persisted review");
    return reviewId;
}

static async Task VerifyLocalSubjectiveReviewServiceAsync(
    DesktopLocalAgentOperations operations,
    string ownerGrant,
    string otherGrant,
    string captureId,
    Guid webReviewId,
    string storageRoot)
{
    var captureStore = new CaptureStore(storageRoot);
    var service = new LocalSubjectiveReviewService(captureStore);
    var trustedStore = new SubjectiveReviewStore(storageRoot);
    Directory.CreateDirectory(Path.Combine(storageRoot, "captures", "invalid-capture-name"));
    var damagedCaptureId = Guid.NewGuid();
    var damagedCaptureDirectory = Path.Combine(storageRoot, "captures", damagedCaptureId.ToString("N"));
    Directory.CreateDirectory(damagedCaptureDirectory);
    await File.WriteAllTextAsync(Path.Combine(damagedCaptureDirectory, "manifest.json"), "{invalid-manifest");

    var capturePages = new List<LocalSubjectiveCaptureSummary>();
    var captureDiagnostics = new List<LocalSubjectiveReviewDiagnostic>();
    string? captureCursor = null;
    do
    {
        var page = await service.ListCapturesAsync(pageSize: 1, cursor: captureCursor);
        capturePages.AddRange(page.Items);
        captureDiagnostics.AddRange(page.Diagnostics);
        captureCursor = page.NextCursor;
    }
    while (captureCursor is not null);

    AssertTrue(capturePages.Any(item => item.CaptureId == captureId), "capture paging should return the uploaded capture");
    AssertTrue(capturePages.All(item => item.PixelWidth == 40 && item.PixelHeight == 40), "capture summaries should contain manifest-validated dimensions");
    AssertTrue(!JsonSerializer.Serialize(capturePages).Contains(storageRoot, StringComparison.Ordinal), "capture summaries must not expose local paths");
    AssertEqual("INVALID_CAPTURE_CURSOR", await GetServiceErrorCodeAsync(() => service.ListCapturesAsync(cursor: "../../outside")), "capture cursors must not accept paths");
    AssertEqual("INVALID_CAPTURE_ID", await GetServiceErrorCodeAsync(() => service.ReadCaptureImageAsync("../../outside")), "raw image access must validate capture IDs instead of accepting paths");
    AssertTrue(captureDiagnostics.Any(item => item.ResourceId == damagedCaptureId.ToString("N") && item.Code == "CAPTURE_HISTORY_INVALID"), "a damaged capture manifest should appear as a typed page diagnostic");

    var rawImage = await service.ReadCaptureImageAsync(captureId);
    await using (var rawStream = await rawImage.OpenReadAsync())
    {
        var header = new byte[8];
        var read = await rawStream.ReadAsync(header);
        AssertEqual(8, read, "raw capture stream should provide the PNG signature");
        AssertTrue(header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "raw capture stream must come from the validated PNG");
    }

    var localQuestionId = Guid.NewGuid();
    var localRegion = SubjectiveRegionDefinition.Create(
        localQuestionId,
        questionNumber: 1,
        SubjectivePixelRectangle.Create(0, 0, 8, 8, 40, 40),
        maximumPoints: 5m,
        imageWidth: 40,
        imageHeight: 40);
    var localReview = await service.CreateAsync(captureId, [localRegion]);
    AssertEqual(1L, localReview.Snapshot.Version, "trusted local reviews should begin at version one");
    AssertEqual(SubjectiveReviewStore.DesktopLocalOwner, trustedStore.LoadTrusted(localReview.ReviewId)?.OwnerGrantId, "new local reviews should have the reserved local owner");
    AssertTrue(trustedStore.LoadForGrant(localReview.ReviewId, ownerGrant) is null, "a web grant must not take over a local review");
    await AssertOperationErrorAsync(
        () => RunAsync(operations, ownerGrant, TaskOperation.SubjectiveRead, new { reviewId = localReview.ReviewId }),
        "SUBJECTIVE_REVIEW_NOT_FOUND");

    var localDocumentPath = Path.Combine(storageRoot, "subjective-reviews", $"{localReview.ReviewId:N}.json");
    using (File.Open(localDocumentPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        AssertEqual(
            "SUBJECTIVE_REVIEW_STORE_FAILED",
            await GetServiceErrorCodeAsync(() => service.ApplyEditsAsync(
                localReview.ReviewId,
                expectedVersion: 1,
                "local teacher",
                [new SubjectiveGradeEdit(localQuestionId, SubjectiveReviewStatus.Draft, 4m, "saved later")])),
            "failed local disk replacement should be reported");
    }

    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        try
        {
            await service.ApplyEditsAsync(
                localReview.ReviewId,
                1,
                "local teacher",
                [new SubjectiveGradeEdit(localQuestionId, SubjectiveReviewStatus.Draft, 4m, "saved later")],
                cancelled.Token);
            throw new InvalidOperationException("pre-cancelled local grade operation should be cancelled");
        }
        catch (OperationCanceledException)
        {
        }
    }

    AssertEqual(1L, trustedStore.LoadTrusted(localReview.ReviewId)?.Snapshot.Version, "write failure and cancellation must preserve the original local version");
    var localDraft = await service.ApplyEditsAsync(
        localReview.ReviewId,
        1,
        "local teacher",
        [new SubjectiveGradeEdit(localQuestionId, SubjectiveReviewStatus.Draft, 4m, "saved draft")]);
    var localFinal = await service.ApplyEditsAsync(
        localReview.ReviewId,
        2,
        "local teacher",
        [new SubjectiveGradeEdit(localQuestionId, SubjectiveReviewStatus.Confirmed, 4m, "saved draft")]);
    AssertEqual(2L, localDraft.Snapshot.Version, "one local batch should advance one version");
    AssertEqual(3L, localFinal.Snapshot.Version, "confirmation should be a separate atomic local batch");
    AssertEqual(4m, localFinal.Snapshot.FinalSubtotal, "local final subtotal should sum only the subjective region scores");

    var restartedService = new LocalSubjectiveReviewService(new CaptureStore(storageRoot), new SubjectiveReviewStore(storageRoot));
    var reopened = await restartedService.ReadAsync(localReview.ReviewId);
    AssertEqual(3L, reopened.Snapshot.Version, "a new local service instance should restore the latest version");
    AssertEqual(2, reopened.Snapshot.History.Count, "a new local service instance should restore audit history");
    var reopenedCrop = await restartedService.ReadQuestionImageAsync(localReview.ReviewId, localQuestionId);
    using (var decodedCrop = SKBitmap.Decode(reopenedCrop.Bytes))
    {
        AssertTrue(decodedCrop is not null, "crop recovery after service restart should decode");
        AssertEqual(new SKColor(0, 0, 0, 255), decodedCrop!.GetPixel(0, 0), "recovered crop should preserve the original selected pixels");
    }

    var existingWebReview = await service.ReadAsync(webReviewId);
    var oldOwnerQuestion = existingWebReview.Snapshot.Questions[0];
    var locallyReopenedWebReview = await service.ApplyEditsAsync(
        webReviewId,
        existingWebReview.Snapshot.Version,
        "trusted local teacher",
        [new SubjectiveGradeEdit(oldOwnerQuestion.QuestionId, SubjectiveReviewStatus.Draft, 7m, "reopened locally")]);
    AssertEqual(ownerGrant, trustedStore.LoadTrusted(webReviewId)?.OwnerGrantId, "trusted local edits must preserve the original web owner");
    AssertEqual(locallyReopenedWebReview.Snapshot.Version, (await RunAsync(operations, ownerGrant, TaskOperation.SubjectiveRead, new { reviewId = webReviewId })).GetProperty("version").GetInt64(), "the original grant should retain access after a local edit");

    var concurrencyReview = await RunAsync(
        operations,
        ownerGrant,
        TaskOperation.SubjectiveCreate,
        new
        {
            captureId,
            questions = new[]
            {
                new
                {
                    questionId = Guid.NewGuid(),
                    questionNumber = 1,
                    maxScore = 5m,
                    region = new { x = 10, y = 10, width = 8, height = 8 }
                }
            }
        });
    var concurrencyReviewId = Guid.Parse(concurrencyReview.GetProperty("reviewId").GetString()!);
    var concurrencyQuestionId = concurrencyReview.GetProperty("questions")[0].GetProperty("questionId").GetGuid();
    var localAttempt = CaptureLocalApplyOutcomeAsync(
        service,
        concurrencyReviewId,
        concurrencyQuestionId);
    var webAttempt = CaptureWebApplyOutcomeAsync(
        operations,
        ownerGrant,
        concurrencyReviewId,
        concurrencyQuestionId);
    var outcomes = await Task.WhenAll(localAttempt, webAttempt);
    AssertTrue(
        (outcomes[0] is null && outcomes[1] == "VERSION_CONFLICT")
        || (outcomes[0] == "VERSION_CONFLICT" && outcomes[1] is null),
        "local and web updates through separate Store instances must share expected-version serialization");
    AssertEqual(ownerGrant, trustedStore.LoadTrusted(concurrencyReviewId)?.OwnerGrantId, "concurrent trusted edit must not transfer grant ownership");

    var localCsv = await restartedService.ExportAsync(localReview.ReviewId, "csv");
    AssertTrue(localCsv.Content.Contains("confirmed", StringComparison.Ordinal), "local CSV export should retain saved confirmation status");
    var localJson = await restartedService.ExportAsync(localReview.ReviewId, "json");
    using (var exported = JsonDocument.Parse(localJson.Content))
    {
        AssertEqual(3L, exported.RootElement.GetProperty("version").GetInt64(), "local JSON export should retain the committed history version");
        AssertEqual(2, exported.RootElement.GetProperty("history").GetArrayLength(), "local JSON export should include the audit batches");
    }

    var corruptedReviewId = Guid.NewGuid();
    var reviewDirectory = Path.Combine(storageRoot, "subjective-reviews");
    await File.WriteAllTextAsync(Path.Combine(reviewDirectory, $"{corruptedReviewId:N}.json"), "{invalid-json");
    var allReviewIds = new HashSet<Guid>();
    var reviewDiagnostics = new List<LocalSubjectiveReviewDiagnostic>();
    string? reviewCursor = null;
    do
    {
        var page = await restartedService.ListReviewsAsync(pageSize: 1, cursor: reviewCursor);
        foreach (var summary in page.Items)
        {
            AssertTrue(allReviewIds.Add(summary.ReviewId), "review keyset pagination must not duplicate records");
        }

        reviewDiagnostics.AddRange(page.Diagnostics);
        reviewCursor = page.NextCursor;
    }
    while (reviewCursor is not null);
    AssertTrue(allReviewIds.Contains(localReview.ReviewId), "review pagination should include the new local review");
    AssertTrue(allReviewIds.Contains(webReviewId), "review pagination should include records created by an earlier web grant");
    AssertTrue(reviewDiagnostics.Any(item => item.ResourceId == corruptedReviewId.ToString("N") && item.Code == "SUBJECTIVE_REVIEW_INVALID"), "a damaged review should appear as a typed page diagnostic");
    AssertTrue(reviewDiagnostics.All(item => !item.Message.Contains(storageRoot, StringComparison.Ordinal)), "review diagnostics must not expose local paths");
    AssertEqual("INVALID_REVIEW_CURSOR", await GetServiceErrorCodeAsync(() => restartedService.ListReviewsAsync(cursor: "C:/users")), "review cursors must not accept paths");
    AssertTrue(captureDiagnostics.Any(item => item.Code == "CAPTURE_HISTORY_INVALID"), "capture paging should report invalid directory names rather than silently skip them");

    var savedCapture = captureStore.LoadById(captureId)
        ?? throw new InvalidOperationException("synthetic capture should exist before missing-image test");
    File.Delete(savedCapture.ImageFilePath);
    var missingImageRead = await restartedService.ReadAsync(localReview.ReviewId);
    AssertEqual(3L, missingImageRead.Snapshot.Version, "missing source image must not hide saved grading history");
    AssertTrue(missingImageRead.Capture is null, "missing source image should appear as unavailable metadata");
    AssertTrue((await restartedService.ExportAsync(localReview.ReviewId, "json")).Content.Contains("saved draft", StringComparison.Ordinal), "missing source image must not block review export");
    AssertEqual("CAPTURE_HISTORY_INVALID", await GetServiceErrorCodeAsync(() => restartedService.ReadQuestionImageAsync(localReview.ReviewId, localQuestionId)), "image access should clearly report a damaged or missing original");
    await AssertOperationErrorAsync(
        () => RunAsync(operations, otherGrant, TaskOperation.SubjectiveRead, new { reviewId = webReviewId }),
        "SUBJECTIVE_REVIEW_NOT_FOUND");
}

static async Task<string?> CaptureLocalApplyOutcomeAsync(
    LocalSubjectiveReviewService service,
    Guid reviewId,
    Guid questionId)
{
    try
    {
        await service.ApplyEditsAsync(
            reviewId,
            1,
            "local concurrent teacher",
            [new SubjectiveGradeEdit(questionId, SubjectiveReviewStatus.Draft, 3m, "local winner")]);
        return null;
    }
    catch (LocalSubjectiveReviewException exception) when (exception.Code == "VERSION_CONFLICT")
    {
        return exception.Code;
    }
}

static async Task<string?> CaptureWebApplyOutcomeAsync(
    DesktopLocalAgentOperations operations,
    string grantId,
    Guid reviewId,
    Guid questionId)
{
    try
    {
        await RunAsync(
            operations,
            grantId,
            TaskOperation.SubjectiveGrade,
            new
            {
                reviewId,
                expectedVersion = 1,
                reviewer = "web concurrent teacher",
                edits = new[] { new { questionId, status = "draft", score = 2m, comment = "web winner" } }
            });
        return null;
    }
    catch (LocalOperationException exception) when (exception.Code == "VERSION_CONFLICT")
    {
        return exception.Code;
    }
}

static async Task<string?> GetServiceErrorCodeAsync(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (LocalSubjectiveReviewException exception)
    {
        return exception.Code;
    }

    return null;
}

static async Task VerifySubjectiveImageCropperAsync()
{
    var rootDirectory = Path.Combine(Path.GetTempPath(), $"omrina-m5-crop-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(rootDirectory);
    try
    {
        const int width = 24;
        const int height = 16;
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                rgba[offset] = (byte)(x * 7);
                rgba[offset + 1] = (byte)(y * 11);
                rgba[offset + 2] = (byte)(x + y);
                rgba[offset + 3] = 255;
            }
        }

        var pngPath = Path.Combine(rootDirectory, "pattern.png");
        await File.WriteAllBytesAsync(pngPath, EncodePng(width, height, rgba));
        var region = SubjectivePixelRectangle.Create(4, 5, 6, 3, width, height);
        var croppedPng = await SubjectiveImageCropper.CropToPngAsync(
            new LocalInputImageFile(pngPath), region, width, height);
        using (var decodedPng = SKBitmap.Decode(croppedPng))
        {
            AssertTrue(decodedPng is not null, "PNG crop should decode");
            AssertEqual(6, decodedPng!.Width, "PNG crop should preserve the selected width");
            AssertEqual(3, decodedPng.Height, "PNG crop should preserve the selected height");
            AssertEqual(new SKColor(28, 55, 9, 255), decodedPng.GetPixel(0, 0), "PNG crop should copy the requested source origin");
        }

        using var jpegBitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                jpegBitmap.SetPixel(x, y, new SKColor(35, 170, 215, 255));
            }
        }

        using var jpegImage = SKImage.FromBitmap(jpegBitmap);
        using var jpegEncoded = jpegImage.Encode(SKEncodedImageFormat.Jpeg, 100)
            ?? throw new InvalidOperationException("synthetic JPEG encoding failed");
        var jpegPath = Path.Combine(rootDirectory, "pattern.jpg");
        await File.WriteAllBytesAsync(jpegPath, jpegEncoded.ToArray());
        var croppedJpeg = await SubjectiveImageCropper.CropToPngAsync(
            new LocalInputImageFile(jpegPath), region, width, height);
        using var decodedJpeg = SKBitmap.Decode(croppedJpeg);
        AssertTrue(decodedJpeg is not null, "JPEG scanline crop should produce a decodable PNG");
        AssertEqual(6, decodedJpeg!.Width, "JPEG crop should preserve the selected width");
        AssertEqual(3, decodedJpeg.Height, "JPEG crop should preserve the selected height");
        var sampled = decodedJpeg.GetPixel(0, 0);
        AssertTrue(
            Math.Abs(sampled.Red - 35) <= 8 && Math.Abs(sampled.Green - 170) <= 8 && Math.Abs(sampled.Blue - 215) <= 8,
            "JPEG crop should retain pixels from the selected source region");
    }
    finally
    {
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }
}

static Task<JsonElement> RunAsync(
    DesktopLocalAgentOperations operations,
    string grantId,
    TaskOperation operation,
    object parameters,
    Stream? image = null,
    CancellationToken cancellationToken = default)
{
    var serializedParameters = JsonSerializer.SerializeToElement(
        parameters,
        parameters.GetType(),
        AgentJson.Options);
    return operations.RunAsync(
        new TaskOperationRequest(grantId, operation, serializedParameters),
        image,
        cancellationToken);
}

static async Task AssertOperationErrorAsync(Func<Task<JsonElement>> action, string expectedCode)
{
    try
    {
        await action();
    }
    catch (LocalOperationException exception) when (exception.Code == expectedCode)
    {
        return;
    }

    throw new InvalidOperationException($"Expected local operation error '{expectedCode}'.");
}

static async Task AssertTaskErrorAsync(Func<Task> action, string expectedCode)
{
    try
    {
        await action();
    }
    catch (LocalOperationException exception) when (exception.Code == expectedCode)
    {
        return;
    }

    throw new InvalidOperationException($"Expected local operation error '{expectedCode}'.");
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'.");
    }
}

static byte[] CreateSyntheticPng(int width, int height)
{
    var pixels = new byte[checked(width * height * 4)];
    for (var pixel = 0; pixel < width * height; pixel++)
    {
        pixels[pixel * 4] = 255;
        pixels[pixel * 4 + 1] = 255;
        pixels[pixel * 4 + 2] = 255;
        pixels[pixel * 4 + 3] = 255;
    }

    pixels[0] = 0;
    pixels[1] = 0;
    pixels[2] = 0;
    return EncodePng(width, height, pixels);
}

static byte[] EncodePng(int width, int height, byte[] rgba)
{
    using var png = new MemoryStream();
    png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
    var header = new byte[13];
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), checked((uint)width));
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), checked((uint)height));
    header[8] = 8;
    header[9] = 6;
    WritePngChunk(png, "IHDR", header);

    var scanlines = new byte[checked(height * (1 + width * 4))];
    for (var row = 0; row < height; row++)
    {
        Buffer.BlockCopy(rgba, row * width * 4, scanlines, row * (1 + width * 4) + 1, width * 4);
    }

    using var compressed = new MemoryStream();
    using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
    {
        zlib.Write(scanlines);
    }

    WritePngChunk(png, "IDAT", compressed.ToArray());
    WritePngChunk(png, "IEND", Array.Empty<byte>());
    return png.ToArray();
}

static void WritePngChunk(Stream destination, string type, byte[] data)
{
    var typeBytes = Encoding.ASCII.GetBytes(type);
    var length = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
    destination.Write(length);
    destination.Write(typeBytes);
    destination.Write(data);
    var crcInput = new byte[typeBytes.Length + data.Length];
    Buffer.BlockCopy(typeBytes, 0, crcInput, 0, typeBytes.Length);
    Buffer.BlockCopy(data, 0, crcInput, typeBytes.Length, data.Length);
    var crc = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(crc, ComputeCrc32(crcInput));
    destination.Write(crc);
}

static uint ComputeCrc32(byte[] data)
{
    var crc = 0xFFFF_FFFFu;
    foreach (var value in data)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB8_8320u;
        }
    }

    return ~crc;
}

static async Task<int> RunHttpHarnessAsync()
{
    var rootDirectory = Path.Combine(Path.GetTempPath(), $"omrina-m3-http-{Guid.NewGuid():N}");
    Directory.CreateDirectory(rootDirectory);
    var imageBytes = CreateSyntheticPng(40, 40);
    var fixturePath = Path.Combine(rootDirectory, "synthetic-answer-sheet.png");
    await File.WriteAllBytesAsync(fixturePath, imageBytes);

    var scanner = new MockScannerService(rootDirectory, imageBytes);
    var operations = new DesktopLocalAgentOperations(
        new CaptureStore(rootDirectory),
        scanner,
        new FixedRecognitionService(),
        path =>
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        });
    await using var server = new LoopbackHealthServer(operations, port: 17845);
    try
    {
        await server.StartAsync();
        Console.WriteLine($@"READY {server.BaseAddress} fixture={fixturePath}");
        Console.WriteLine("Commands: pending | approve <requestId> | reject <requestId> | grants | quit");
        while (true)
        {
            var line = await Console.In.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            switch (parts[0])
            {
                case "pending":
                    Console.WriteLine($"PENDING {JsonSerializer.Serialize(server.PendingPairings, AgentJson.Options)}");
                    break;
                case "approve" when parts.Length == 2:
                    try
                    {
                        Console.WriteLine($"APPROVAL {JsonSerializer.Serialize(new { requestId = parts[1], code = server.ApprovePairing(parts[1]) }, AgentJson.Options)}");
                    }
                    catch (Exception exception)
                    {
                        Console.WriteLine($"APPROVAL_ERROR {exception.GetType().Name}");
                    }

                    break;
                case "reject" when parts.Length == 2:
                    Console.WriteLine($"REJECTED {server.RejectPairing(parts[1])}");
                    break;
                case "grants":
                    Console.WriteLine($"GRANTS {JsonSerializer.Serialize(server.ActiveGrants, AgentJson.Options)}");
                    break;
                case "quit":
                    return 0;
                default:
                    Console.WriteLine("ERROR unknown command");
                    break;
            }
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"HARNESS_ERROR {exception.GetType().Name}: {exception.Message}");
        return 1;
    }
    finally
    {
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    return 0;
}

sealed class MockScannerService(string rootDirectory, byte[] imageBytes) : IScannerService
{
    public int ScanCount { get; private set; }

    public string? LastOutputPath { get; private set; }

    public Task<IReadOnlyList<ScannerDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ScannerDevice> devices = [new ScannerDevice("mock:scanner", "模拟扫描仪", "M3 test adapter")];
        return Task.FromResult(devices);
    }

    public async Task<ScanResult> ScanAsync(
        ScannerDevice device,
        ScanOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(device.Id, "mock:scanner", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The test scanner received an unexpected device.");
        }

        ScanCount++;
        var pendingDirectory = Path.Combine(rootDirectory, "pending-scans");
        Directory.CreateDirectory(pendingDirectory);
        LastOutputPath = Path.Combine(pendingDirectory, $"synthetic-scan-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(LastOutputPath, imageBytes, cancellationToken);
        return new ScanResult(LastOutputPath, options.Dpi, options.PageSize, options.Flatbed);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FixedRecognitionService : IAnswerSheetRecognitionService
{
    public int RecognitionCount { get; private set; }

    public async Task<RecognitionResult> RecognizeAsync(
        AnswerSheetLayout layout,
        IInputImageFile file,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var input = await file.OpenReadAsync(cancellationToken);
        var signature = new byte[8];
        var count = await input.ReadAsync(signature.AsMemory(), cancellationToken);
        if (count != signature.Length
            || !signature.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            throw new InvalidOperationException("The test recognizer did not receive the synthetic PNG.");
        }

        RecognitionCount++;
        var options = Enumerable.Range(1, layout.OptionsPerQuestion)
            .Select(index =>
            {
                var label = ((char)('A' + index - 1)).ToString();
                var isSelected = index == 1;
                return new OptionRecognitionResult(
                    index,
                    label,
                    isSelected ? 0.9 : 0.1,
                    isSelected ? OptionMarkState.Selected : OptionMarkState.Unselected,
                    0.9);
            })
            .ToArray();
        var questions = Enumerable.Range(1, layout.QuestionCount)
            .Select(questionNumber => new QuestionRecognitionResult(
                questionNumber,
                QuestionMarkState.Single,
                options,
                0.9))
            .ToArray();
        return new RecognitionResult(
            layout.TemplateId,
            AnswerSheetLayout.TemplateSchemaVersion,
            RecognitionStatus.Accepted,
            PageOrientation.Degrees0,
            transform: null,
            confidence: 0.9,
            questions,
            Array.Empty<RecognitionDiagnostic>());
    }
}
