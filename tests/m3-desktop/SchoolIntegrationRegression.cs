using System.Text.Json;
using Omrina.Core;
using Omrina.Desktop;
using Omrina.Platform;
using Omrina.Protocol;
using Omrina.Scanning;
using Omrina.Server;
using SkiaSharp;
using ZXing.Common;

namespace Omrina.M3.Desktop.Tests;

internal static class SchoolIntegrationRegression
{
    public static async Task<int> RunHttpHarnessAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "omrina-school-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var definition = new SchoolSheetDefinition
        {
            ExamId = "school-http-exam", LayoutDocumentId = "school-http-layout", Title = "学校 SDK 测试",
            Paper = SchoolPaper.A3Landscape, Columns = 3,
            CandidateIdentity = new(CandidateIdentityMode.Barcode, 4, "0123"),
            Questions = [new(7, SchoolQuestionType.Choice, 2, Options: ["甲", "乙"]), new(15, SchoolQuestionType.Subjective, 5)],
            Groups = [new("choice-group", "选择题组", [7]), new("written-group", "解答题组", [15])],
            LayoutOrder = SchoolLayoutOrder.Mixed
        };
        var fixture = Path.Combine(root, "school.png");
        Render(SchoolAnswerSheet.Create(definition).Pages[0], fixture);
        var operations = new DesktopLocalAgentOperations(new CaptureStore(root), new NoScanner(), new SkiaAnswerSheetRecognitionService(), _ => { });
        await using var server = new LoopbackHealthServer(operations, port: 0);
        try
        {
            await server.StartAsync();
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            Console.WriteLine("READY " + JsonSerializer.Serialize(new { endpoint = server.BaseAddress, fixture, definition }, options));
            while (await Console.In.ReadLineAsync() is {} line)
            {
                if (line == "quit") return 0;
                if (line.StartsWith("approve ", StringComparison.Ordinal))
                    Console.WriteLine("APPROVAL " + JsonSerializer.Serialize(new { code = server.ApprovePairing(line[8..]) }));
            }
            return 0;
        }
        finally { await server.StopAsync(); Directory.Delete(root, recursive: true); }
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "omrina-school-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var definition = new SchoolSheetDefinition
            {
                ExamId = "exam-test", LayoutDocumentId = "layout-test", Title = "集成测试",
                Paper = SchoolPaper.A3Landscape, Columns = 3,
                CandidateIdentity = new(CandidateIdentityMode.Barcode, 4, "0123"),
                Questions = [new(7, SchoolQuestionType.Choice, 2, Options: ["甲", "乙"]), new(15, SchoolQuestionType.Subjective, 5)],
                Groups = [new("choice-group", "选择题组", [7]), new("written-group", "解答题组", [15])],
                LayoutOrder = SchoolLayoutOrder.Mixed
            };
            var examStore = new SchoolExamStore(root);
            var savedDefinition = examStore.SaveDefinition(definition);
            Check(savedDefinition.Groups.Count == 2 && savedDefinition.Groups[0].Id == "choice-group"
                && savedDefinition.Groups[1].QuestionNumbers.SequenceEqual(new[] { 15 }), "school store should preserve explicit group definitions");
            Check(examStore.ListDefinitions().Count == 1, "saved exam should reopen");
            var reopenedDefinition = examStore.GetDefinition("exam-test", "layout-test", 1);
            Check(reopenedDefinition is not null && reopenedDefinition.Paper == SchoolPaper.A3Landscape
                && reopenedDefinition.Groups.Count == 2, "paper and explicit groups should persist");
            Expect<InvalidOperationException>(() => examStore.SaveDefinition(definition with { Title = "覆盖历史" }));
            examStore.SaveAnswerKey(definition, new Dictionary<int, string> { [7] = "B" });
            Check(examStore.ReadAnswerKey(definition)?[7] == "B", "shared answer keys should preserve global question numbers");
            Expect<ArgumentException>(() => examStore.SaveAnswerKey(definition, new Dictionary<int, string> { [1] = "A" }));
            var layout = SchoolAnswerSheet.Create(definition).Pages[0];
            var imagePath = Path.Combine(root, "school.png");
            Render(layout, imagePath);
            var captures = new CaptureStore(root);
            var capture = await captures.ImportAsync(layout, new LocalInputImageFile(imagePath));
            Check(capture.Manifest.SchoolMetadata == layout.SchoolMetadata, "capture should bind actual school page");
            Check(capture.Manifest.CandidateId == "0123" && capture.Manifest.IdentityStatus == "Identified", "only reliable candidate barcode should bind candidate");
            Check(captures.LoadById(capture.Manifest.CaptureId)?.TemplateLayout?.WidthMm == 420, "A3 snapshot should reopen");
            var mapping = await SubjectiveCaptureTemplateMapper.LocateAndMapAsync(layout, new LocalInputImageFile(imagePath), 4200, 2970);
            Check(mapping.Regions.Count == 1, "A3 subjective regions should use actual paper width");
            var wrong = SchoolAnswerSheet.Create(definition with { ExamId = "other-exam" }).Pages[0];
            await ExpectAsync<CaptureValidationException>(() => captures.ImportAsync(wrong, new LocalInputImageFile(imagePath)), "CAPTURE_SCHOOL_PAGE_MISMATCH");
            var noCandidate = SchoolAnswerSheet.Create(definition with { CandidateIdentity = new(CandidateIdentityMode.Barcode, 4) }).Pages[0];
            Render(noCandidate, imagePath);
            var unidentified = await captures.ImportAsync(noCandidate, new LocalInputImageFile(imagePath));
            Check(unidentified.Manifest.CandidateId is null && unidentified.Manifest.IdentityStatus == "RequireAssociation", "unreadable candidate must stay unassociated");
            var subjectiveOnly = SchoolAnswerSheet.Create(definition with
            {
                Questions = [new(22, SchoolQuestionType.Subjective, 5)],
                Groups = [new("written-only-group", "解答题组", [22])]
            }).Pages[0];
            Render(subjectiveOnly, imagePath);
            var onlyCapture = await captures.ImportAsync(subjectiveOnly, new LocalInputImageFile(imagePath));
            Check(captures.LoadById(onlyCapture.Manifest.CaptureId)?.Manifest.QuestionCount == 0, "subjective-only page should persist without fake choice questions");
            var operations = new DesktopLocalAgentOperations(captures, new NoScanner(), new SkiaAnswerSheetRecognitionService(), _ => { });
            // Resolving every answer cannot promote an unidentified student page to a final score.
            using (var snapshot = JsonDocument.Parse(noCandidate.ToJson()))
            {
                await operations.RunAsync(new("grant-a", TaskOperation.Template,
                    JsonSerializer.SerializeToElement(new { schoolDefinition = snapshot.RootElement.GetProperty("definition") })), null, default);
            }
            Render(noCandidate, imagePath);
            using (var upload = new MemoryStream(File.ReadAllBytes(imagePath)))
            {
                var uploaded = await operations.RunAsync(new("grant-a", TaskOperation.Upload,
                    JsonSerializer.SerializeToElement(new { templateId = noCandidate.TemplateId, fileName = "unknown-candidate.png" })), upload, default);
                var recognized = await operations.RunAsync(new("grant-a", TaskOperation.Recognize,
                    JsonSerializer.SerializeToElement(new { captureId = uploaded.GetProperty("captureId").GetString() })), null, default);
                Check(recognized.GetProperty("identityStatus").GetString() == "RequireAssociation", "recognition should preserve pending identity");
                var resultId = recognized.GetProperty("resultId").GetString();
                var scored = await operations.RunAsync(new("grant-a", TaskOperation.Score,
                    JsonSerializer.SerializeToElement(new { resultId, answerKey = new Dictionary<string, string> { ["7"] = "A" } })), null, default);
                Check(scored.GetProperty("result").GetProperty("scoring").GetProperty("disposition").GetString() == "Provisional", "unidentified school score must be provisional");
                var reviewed = await operations.RunAsync(new("grant-a", TaskOperation.Review,
                    JsonSerializer.SerializeToElement(new { resultId, expectedVersion = scored.GetProperty("version").GetInt32(), reviewer = "synthetic-teacher",
                        edits = new[] { new { questionNumber = 7, answer = "A", reason = "确认所有选择题" } } })), null, default);
                Check(reviewed.GetProperty("identityStatus").GetString() == "RequireAssociation"
                    && reviewed.GetProperty("result").GetProperty("scoring").GetProperty("disposition").GetString() == "Provisional",
                    "question review cannot clear pending student association");
                var exported = await operations.RunAsync(new("grant-a", TaskOperation.Export,
                    JsonSerializer.SerializeToElement(new { resultId, format = "json" })), null, default);
                using var exportDocument = JsonDocument.Parse(exported.GetProperty("content").GetString()!);
                Check(exportDocument.RootElement.GetProperty("scoring").GetProperty("disposition").GetString() == "Provisional", "export must preserve provisional identity score");
            }
            var parameters = JsonSerializer.SerializeToElement(new { schoolDefinition = new
            {
                examId = "sdk-exam", layoutDocumentId = "sdk-layout", paper = "A3Landscape", columns = 2,
                questions = new[] { new { number = 9, type = "Subjective", maximumScore = 5, subjectiveHeightMm = 30 } },
                groups = new[] { new { id = "sdk-written-group", title = "解答题组", questionNumbers = new[] { 9 } } },
                layoutOrder = "Mixed"
            }});
            var result = await operations.RunAsync(new TaskOperationRequest("grant-a", TaskOperation.Template, parameters), null, default);
            var page = result.GetProperty("pages")[0];
            Check(page.GetProperty("schemaVersion").GetInt32() == 3 && page.GetProperty("widthMm").GetDouble() == 420, "SDK template should expose page dimensions");
            Check(page.GetProperty("side").GetString() == "Front", "SDK side should be a string");
            var sdkGroup = page.GetProperty("schoolGroups").EnumerateArray().Single();
            Check(sdkGroup.GetProperty("groupId").GetString() == "sdk-written-group"
                && sdkGroup.GetProperty("questionNumbers")[0].GetInt32() == 9
                && sdkGroup.GetProperty("rectangleMm").GetProperty("width").GetDouble() > 0,
                "SDK template request should preserve group identity, membership, and placed bounds");
            var scanOperations = new DesktopLocalAgentOperations(captures, new FixtureScanner(root, layout), new SkiaAnswerSheetRecognitionService(), _ => { });
            using (var scanSnapshot = JsonDocument.Parse(layout.ToJson()))
                await scanOperations.RunAsync(new("scan-grant", TaskOperation.Template,
                    JsonSerializer.SerializeToElement(new { schoolDefinition = scanSnapshot.RootElement.GetProperty("definition") })), null, default);
            var scanned = await scanOperations.RunAsync(new("scan-grant", TaskOperation.Scan,
                JsonSerializer.SerializeToElement(new { templateId = layout.TemplateId, deviceId = "fixture-scanner", dpi = 300 })), null, default);
            Check(scanned.GetProperty("schoolMetadata").GetProperty("examId").GetString() == definition.ExamId
                && scanned.GetProperty("schoolMetadata").GetProperty("side").GetString() == "Front"
                && scanned.GetProperty("candidateId").GetString() == "0123"
                && scanned.GetProperty("identityStatus").GetString() == "Identified"
                && scanned.GetProperty("pageSize").GetString() == "A3",
                "school scan summary must match upload metadata and candidate association");
            var oversizedDefinition = definition with
            {
                ExamId = "large-school-result", Paper = SchoolPaper.A4Portrait, Columns = 1,
                CandidateIdentity = new(CandidateIdentityMode.Barcode, 4),
                Questions = Enumerable.Range(1, 64).Select(number => new SchoolQuestionDefinition(number,
                    SchoolQuestionType.Subjective, 5, SubjectiveHeightMm: 100)).ToArray(),
                Groups = Enumerable.Range(1, 64).Select(number => new SchoolQuestionGroupDefinition(
                    $"large-subjective-{number}", $"解答题组 {number}", [number])).ToArray()
            };
            var oversizedPages = SchoolAnswerSheet.Create(oversizedDefinition).Pages;
            Check(oversizedPages.Count == 64, "oversized result fixture should stay within supported page count");
            using (var largeSnapshot = JsonDocument.Parse(oversizedPages[0].ToJson()))
            {
                await ExpectAsync<LocalOperationException>(() => operations.RunAsync(new("large-grant", TaskOperation.Template,
                    JsonSerializer.SerializeToElement(new { schoolDefinition = largeSnapshot.RootElement.GetProperty("definition") })), null, default), "TEMPLATE_DOCUMENT_TOO_LARGE");
            }
            await ExpectAsync<LocalOperationException>(() => operations.RunAsync(new("large-grant", TaskOperation.Scan,
                JsonSerializer.SerializeToElement(new { templateId = oversizedPages[0].TemplateId, deviceId = "none", dpi = 300 })), null, default), "TEMPLATE_NOT_FOUND");
            var invalid = JsonSerializer.SerializeToElement(new { schoolDefinition = new { examId = "sdk-exam", layoutDocumentId = "sdk-layout", localPath = imagePath, questions = Array.Empty<object>() } });
            await ExpectAsync<LocalOperationException>(() => operations.RunAsync(new("grant-a", TaskOperation.Template, invalid), null, default), "INVALID_PARAMETERS");
            var manifestJson = File.ReadAllText(capture.ManifestFilePath);
            File.WriteAllText(capture.ManifestFilePath, manifestJson.Replace("exam-test", "other-exam", StringComparison.Ordinal));
            Expect<CaptureStorageException>(() => captures.LoadById(capture.Manifest.CaptureId));
            Console.WriteLine("PASS: school exam immutable versions, shared global-number answers, strict SDK templates, A3 capture/candidate/subjective mapping, wrong exam rejection and subjective-only persistence.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Render(AnswerSheetLayout layout, string path)
    {
        using var bitmap = new SKBitmap((int)layout.WidthMm * 10, (int)layout.HeightMm * 10);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var black = new SKPaint { Color = SKColors.Black, IsAntialias = false };
        void Rectangle(double x, double y, double width, double height) => canvas.DrawRect((float)x * 10, (float)y * 10, (float)width * 10, (float)height * 10, black);
        foreach (var mark in layout.RegistrationMarks) Rectangle(mark.TopLeft.X, mark.TopLeft.Y, mark.SizeMm, mark.SizeMm);
        var orientation = layout.OrientationMarker;
        Rectangle(orientation.TopLeft.X, orientation.TopLeft.Y, orientation.WidthMm, orientation.HeightMm);
        void Matrix(BitMatrix matrix, RectMm area)
        {
            for (var y = 0; y < matrix.Height; y++) for (var x = 0; x < matrix.Width; x++)
                if (matrix[x, y]) Rectangle(area.X + x * area.Width / matrix.Width, area.Y + y * area.Height / matrix.Height, area.Width / matrix.Width, area.Height / matrix.Height);
        }
        Matrix(SchoolMachineCode.EncodeExam(layout.SchoolMetadata!), layout.ExamCodeArea!.Value);
        if (layout.SchoolDefinition!.CandidateIdentity.CandidateId is {} id && layout.CandidateArea is {} candidate)
            Matrix(SchoolMachineCode.EncodeCandidate(id), candidate);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
    private static async Task ExpectAsync<T>(Func<Task> action, string code) where T : Exception
    {
        try { await action(); }
        catch (T exception) when (exception is CaptureException capture && capture.Code == code || exception is LocalOperationException operation && operation.Code == code) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}: {code}");
    }
    private sealed class FixtureScanner(string root, AnswerSheetLayout layout) : IScannerService
    {
        public Task<IReadOnlyList<ScannerDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScannerDevice>>([new("fixture-scanner", "Synthetic scanner", "fixture")]);
        public Task<ScanResult> ScanAsync(ScannerDevice device, ScanOptions options, CancellationToken cancellationToken = default)
        {
            Check(options.PageSize == "A3" && options.Flatbed, "school scan must request the actual A3 paper");
            var directory = Path.Combine(root, "pending-scans");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "synthetic-school.png");
            Render(layout, path);
            return Task.FromResult(new ScanResult(path, options.Dpi, options.PageSize, options.Flatbed));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoScanner : IScannerService
    {
        public Task<IReadOnlyList<ScannerDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ScannerDevice>>(Array.Empty<ScannerDevice>());
        public Task<ScanResult> ScanAsync(ScannerDevice device, ScanOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
