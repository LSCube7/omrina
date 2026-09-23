using Omrina.Core;
using Omrina.Desktop;
using Omrina.Platform;
using SkiaSharp;
using System.Text.Json;
using System.Text.Json.Nodes;

var failures = new List<string>();
var repositoryRoot = FindRepositoryRoot();

var testRoot = Path.Combine(
    repositoryRoot,
    "artifacts",
    "m1-capture-test",
    DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
Directory.CreateDirectory(testRoot);
var sourcePath = Path.Combine(testRoot, "source.png");
var jpegSourcePath = Path.Combine(testRoot, "source.jpg");
await WriteFixtureImagesAsync(sourcePath, jpegSourcePath);

var authorizedSamplePath = Path.Combine(
    repositoryRoot,
    "artifacts",
    "scans",
    "test-scan-20260915-155124174.png");
if (File.Exists(authorizedSamplePath))
{
    Console.WriteLine($"Optional authorized scan sample available: {authorizedSamplePath}");
}

try
{
    await RunAsync("template identity is shared with Core", failures, VerifyTemplateIdentityAsync);
    await RunAsync("Skia grayscale decode returns pixels", failures, () => VerifyGrayscaleDecodeAsync(sourcePath));
    var recognitionSamplePath = Environment.GetEnvironmentVariable("OMRINA_RECOGNITION_SAMPLE");
    if (!string.IsNullOrWhiteSpace(recognitionSamplePath) && File.Exists(recognitionSamplePath))
    {
        await RunAsync(
            "optional answer-sheet recognition sample",
            failures,
            () => VerifyRecognitionSampleAsync(recognitionSamplePath));
    }
    await RunAsync("PNG import keeps bytes and writes association manifest", failures, () => VerifyImportAsync(sourcePath, testRoot));
    await RunAsync("JPEG import decodes the complete multi-pixel image", failures, () => VerifyJpegImportAsync(jpegSourcePath, testRoot));
    await RunAsync("scan source type uses the same storage API", failures, () => VerifyScanSourceAsync(sourcePath, testRoot));
    await RunAsync("invalid, truncated, and unsupported files leave no staging record", failures, () => VerifyInvalidInputsAsync(sourcePath, jpegSourcePath, testRoot));
    await RunAsync("cancelled import leaves no staging record", failures, () => VerifyCancellationAsync(sourcePath, testRoot));
    await RunAsync("latest capture history accepts the newest valid record", failures, () => VerifyLatestCaptureAsync(sourcePath, testRoot));
    await RunAsync("unsupported latest manifest schema is rejected without fallback", failures, () => VerifyUnsupportedLatestSchemaAsync(sourcePath, testRoot));
    await RunAsync("unsupported latest template schema is rejected", failures, () => VerifyUnsupportedLatestTemplateSchemaAsync(sourcePath, testRoot));
    await RunAsync("latest capture image path traversal is rejected", failures, () => VerifyLatestPathTraversalAsync(sourcePath, testRoot));
    await RunAsync("missing latest manifest is rejected", failures, () => VerifyMissingLatestManifestAsync(sourcePath, testRoot));
    await RunAsync("missing latest capture image is rejected", failures, () => VerifyMissingLatestImageAsync(sourcePath, testRoot));
}
finally
{
    Console.WriteLine($"Capture test output: {testRoot}");
}

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"FAIL: {failure}");
    }

    return 1;
}

Console.WriteLine("PASS: 13 capture regression tests.");
return 0;

static async Task WriteFixtureImagesAsync(string pngPath, string jpegPath)
{
    using var bitmap = new SKBitmap(3, 2, SKColorType.Rgba8888, SKAlphaType.Premul);
    bitmap.SetPixel(0, 0, SKColors.Red);
    bitmap.SetPixel(1, 0, SKColors.Green);
    bitmap.SetPixel(2, 0, SKColors.Blue);
    bitmap.SetPixel(0, 1, SKColors.White);
    bitmap.SetPixel(1, 1, SKColors.Black);
    bitmap.SetPixel(2, 1, SKColors.Yellow);

    using var image = SKImage.FromBitmap(bitmap);
    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
    using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 90);
    if (png is null || jpeg is null)
    {
        throw new InvalidOperationException("Skia could not encode capture fixtures.");
    }

    await File.WriteAllBytesAsync(pngPath, png.ToArray());
    await File.WriteAllBytesAsync(jpegPath, jpeg.ToArray());
}

static async Task RunAsync(
    string name,
    List<string> failures,
    Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.GetBaseException().Message}");
    }
}

static Task VerifyTemplateIdentityAsync()
{
    var layout = AnswerSheetLayout.Create("M1 Capture", 20, 4);
    var reference = CaptureTemplateReference.FromLayout(layout);

    AssertEqual(layout.TemplateId, reference.TemplateId, "capture should reuse Core template ID");
    AssertEqual(AnswerSheetLayout.TemplateSchemaVersion, reference.SchemaVersion, "capture should reuse Core template schema");
    AssertEqual(layout.Title, reference.Title, "manifest reference should preserve normalized title");
    return Task.CompletedTask;
}

static async Task VerifyGrayscaleDecodeAsync(string sourcePath)
{
    var decoder = new SkiaImageDecoder();
    var image = await decoder.DecodeGrayscaleAsync(new LocalInputImageFile(sourcePath));

    AssertEqual(3, image.Width, "grayscale decode should preserve width");
    AssertEqual(2, image.Height, "grayscale decode should preserve height");
    AssertTrue(image.GetPixel(0, 0) < 100, "red should have non-zero luminance conversion");
    AssertTrue(image.GetPixel(1, 0) is > 40 and < 220, "green should have middle luminance");
    AssertTrue(image.GetPixel(2, 0) < 100, "blue should have non-zero luminance conversion");
    AssertEqual((byte)255, image.GetPixel(0, 1), "white should remain white");
    AssertEqual((byte)0, image.GetPixel(1, 1), "black should remain black");
}

static async Task VerifyRecognitionSampleAsync(string sourcePath)
{
    var layout = AnswerSheetLayout.Create("真实样本", 20, 4);
    var service = new SkiaAnswerSheetRecognitionService();
    var result = await service.RecognizeAsync(layout, new LocalInputImageFile(sourcePath));

    Console.WriteLine(
        $"Recognition sample: status={result.Status}, orientation={result.Orientation}, "
        + $"confidence={result.Confidence:0.###}, questions={result.Questions.Count}");
    foreach (var question in result.Questions)
    {
        var selected = string.Join(
            ",",
            question.Options
                .Where(option => option.State == OptionMarkState.Selected)
                .Select(option => option.OptionLabel));
        Console.WriteLine($"Q{question.QuestionNumber}: {question.State} [{selected}]");
    }

    AssertTrue(result.Status != RecognitionStatus.Rejected, "authorized recognition sample should locate the page");
    AssertEqual(PageOrientation.Degrees0, result.Orientation, "authorized sample should be upright");
    AssertEqual(20, result.Questions.Count, "authorized sample question count");
}

static async Task VerifyImportAsync(string sourcePath, string testRoot)
{
    var layout = AnswerSheetLayout.Create("PNG 导入", 20, 4);
    var source = new LocalInputImageFile(sourcePath);
    var store = new CaptureStore(testRoot);
    var capture = await store.ImportAsync(layout, source, CaptureSourceType.Import);

    AssertTrue(File.Exists(capture.ImageFilePath), "stored original should exist");
    AssertTrue(File.Exists(capture.ManifestFilePath), "manifest should exist");
    AssertTrue(!capture.CaptureDirectory.Contains(".staging", StringComparison.Ordinal), "staging directory must not be committed");

    var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
    var storedBytes = await File.ReadAllBytesAsync(capture.ImageFilePath);
    AssertTrue(sourceBytes.AsSpan().SequenceEqual(storedBytes), "stored bytes should equal selected source bytes");

    var manifestJson = await File.ReadAllTextAsync(capture.ManifestFilePath);
    using var document = JsonDocument.Parse(manifestJson);
    var manifest = document.RootElement;
    AssertEqual("import", manifest.GetProperty("sourceType").GetString(), "source type should be import");
    AssertEqual(layout.TemplateId, manifest.GetProperty("templateId").GetString(), "manifest should reference Core template ID");
    AssertEqual(AnswerSheetLayout.TemplateSchemaVersion, manifest.GetProperty("templateSchemaVersion").GetInt32(), "manifest should record template schema");
    AssertEqual(layout.QuestionCount, manifest.GetProperty("questionCount").GetInt32(), "manifest should record question count");
    AssertEqual(layout.OptionsPerQuestion, manifest.GetProperty("optionsPerQuestion").GetInt32(), "manifest should record option count");
    AssertEqual("captures/" + capture.Manifest.CaptureId + "/original.png", manifest.GetProperty("imagePath").GetString(), "manifest image path should be relative and stable");
    AssertEqual((long)sourceBytes.Length, manifest.GetProperty("byteLength").GetInt64(), "manifest should record copied byte length");
    AssertEqual(3U, manifest.GetProperty("pixelWidth").GetUInt32(), "manifest should record the source width");
    AssertEqual(2U, manifest.GetProperty("pixelHeight").GetUInt32(), "manifest should record the source height");
}

static async Task VerifyJpegImportAsync(string sourcePath, string testRoot)
{
    var layout = AnswerSheetLayout.Create("JPEG 导入", 20, 4);
    var source = new LocalInputImageFile(sourcePath);
    var store = new CaptureStore(testRoot);
    var capture = await store.ImportAsync(layout, source, CaptureSourceType.Import);

    AssertEqual(".jpg", capture.Manifest.ImageExtension, "JPEG fixture should retain its selected extension");
    AssertEqual(3U, capture.Manifest.PixelWidth, "JPEG manifest should record source width");
    AssertEqual(2U, capture.Manifest.PixelHeight, "JPEG manifest should record source height");
    AssertTrue(File.Exists(capture.ImageFilePath), "stored JPEG should exist");
}

static async Task VerifyScanSourceAsync(string sourcePath, string testRoot)
{
    var layout = AnswerSheetLayout.Create("扫描页", 20, 4);
    var source = new LocalInputImageFile(sourcePath);
    var store = new CaptureStore(testRoot);
    var capture = await store.ImportAsync(layout, source, CaptureSourceType.Scan);

    AssertEqual("scan", capture.Manifest.SourceType, "scan source should be persisted as scan");
    AssertEqual(layout.TemplateId, capture.Manifest.TemplateId, "scan should use the same template association");
}

static async Task VerifyInvalidInputsAsync(string sourcePath, string jpegSourcePath, string testRoot)
{
    var store = new CaptureStore(testRoot);
    var layout = AnswerSheetLayout.Create("非法图像", 1, 2);
    var invalidPath = Path.Combine(testRoot, "invalid.png");
    var truncatedPath = Path.Combine(testRoot, "truncated.png");
    var truncatedJpegPath = Path.Combine(testRoot, "truncated.jpg");
    var oversizedPath = Path.Combine(testRoot, "oversized.png");
    var unsupportedPath = Path.Combine(testRoot, "invalid.txt");
    await File.WriteAllTextAsync(invalidPath, "this is not an image");
    var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
    await File.WriteAllBytesAsync(truncatedPath, sourceBytes[..(sourceBytes.Length / 2)]);
    var jpegBytes = await File.ReadAllBytesAsync(jpegSourcePath);
    await File.WriteAllBytesAsync(truncatedJpegPath, jpegBytes[..(jpegBytes.Length / 2)]);
    await WriteOversizedFixtureAsync(oversizedPath);
    await File.WriteAllTextAsync(unsupportedPath, "unsupported");

    var invalidFile = new LocalInputImageFile(invalidPath);
    var truncatedFile = new LocalInputImageFile(truncatedPath);
    var truncatedJpegFile = new LocalInputImageFile(truncatedJpegPath);
    var oversizedFile = new LocalInputImageFile(oversizedPath);
    var unsupportedFile = new LocalInputImageFile(unsupportedPath);
    var stagingBefore = CountStagingDirectories(testRoot);

    var invalidException = await AssertThrowsAsync<CaptureValidationException>(
        () => store.ImportAsync(layout, invalidFile, CaptureSourceType.Import));
    AssertEqual("INVALID_IMAGE", invalidException.Code, "invalid image should have a stable error code");

    var truncatedException = await AssertThrowsAsync<CaptureValidationException>(
        () => store.ImportAsync(layout, truncatedFile, CaptureSourceType.Import));
    AssertEqual("INVALID_IMAGE", truncatedException.Code, "truncated image should fail complete decode");

    var truncatedJpegException = await AssertThrowsAsync<CaptureValidationException>(
        () => store.ImportAsync(layout, truncatedJpegFile, CaptureSourceType.Import));
    AssertEqual("INVALID_IMAGE", truncatedJpegException.Code, "truncated JPEG should fail complete decode");

    var oversizedException = await AssertThrowsAsync<CaptureValidationException>(
        () => store.ImportAsync(layout, oversizedFile, CaptureSourceType.Import));
    AssertEqual("IMAGE_DIMENSIONS_TOO_LARGE", oversizedException.Code, "oversized image should preserve the dimension error code");

    var unsupportedException = await AssertThrowsAsync<CaptureValidationException>(
        () => store.ImportAsync(layout, unsupportedFile, CaptureSourceType.Import));
    AssertEqual("UNSUPPORTED_IMAGE_TYPE", unsupportedException.Code, "unsupported extension should be rejected before reading");
    AssertEqual(stagingBefore, CountStagingDirectories(testRoot), "failed validation should leave no staging directory");
}

static async Task WriteOversizedFixtureAsync(string path)
{
    // Width exceeds the established 16,000-pixel limit while keeping the
    // fixture itself small. CaptureStore must reject it from codec metadata
    // before attempting a pixel allocation or scanline walk.
    using var bitmap = new SKBitmap(16_001, 1);
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    if (data is null)
    {
        throw new InvalidOperationException("Skia could not encode the oversized fixture.");
    }

    await File.WriteAllBytesAsync(path, data.ToArray());
}

static async Task VerifyCancellationAsync(string sourcePath, string testRoot)
{
    var layout = AnswerSheetLayout.Create("取消导入", 1, 2);
    var source = new LocalInputImageFile(sourcePath);
    var store = new CaptureStore(testRoot);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    await AssertThrowsAsync<OperationCanceledException>(
        () => store.ImportAsync(layout, source, CaptureSourceType.Import, cancellation.Token));
    AssertEqual(0, CountStagingDirectories(testRoot), "cancelled import should leave no staging directory");
}

static async Task VerifyLatestCaptureAsync(string sourcePath, string testRoot)
{
    var store = new CaptureStore(Path.Combine(testRoot, "history-latest"));
    var layout = AnswerSheetLayout.Create("最近记录", 1, 2);
    var older = await store.ImportAsync(layout, new LocalInputImageFile(sourcePath));
    var latest = await store.ImportAsync(layout, new LocalInputImageFile(sourcePath));
    Directory.SetLastWriteTimeUtc(older.CaptureDirectory, DateTime.UtcNow.AddMinutes(-2));
    Directory.SetLastWriteTimeUtc(latest.CaptureDirectory, DateTime.UtcNow.AddMinutes(-1));

    var loaded = store.LoadLatest();
    AssertEqual(latest.Manifest.CaptureId, loaded?.Manifest.CaptureId, "history should load the newest committed capture");
    AssertTrue(
        File.ReadAllBytes(loaded!.ImageFilePath).AsSpan().SequenceEqual(File.ReadAllBytes(sourcePath)),
        "history should preserve the stored original bytes");
}

static async Task VerifyUnsupportedLatestSchemaAsync(string sourcePath, string testRoot)
{
    var store = new CaptureStore(Path.Combine(testRoot, "history-schema"));
    var layout = AnswerSheetLayout.Create("Schema 边界", 1, 2);
    var older = await store.ImportAsync(layout, new LocalInputImageFile(sourcePath));
    var latest = await store.ImportAsync(layout, new LocalInputImageFile(sourcePath));
    await UpdateManifestAsync(
        latest,
        manifest => manifest["manifestSchemaVersion"] = CaptureStore.ManifestSchemaVersion + 1);
    Directory.SetLastWriteTimeUtc(older.CaptureDirectory, DateTime.UtcNow.AddMinutes(-2));
    Directory.SetLastWriteTimeUtc(latest.CaptureDirectory, DateTime.UtcNow.AddMinutes(-1));

    var exception = await AssertThrowsAsync<CaptureStorageException>(
        () => Task.Run(() => store.LoadLatest()));
    AssertEqual("CAPTURE_HISTORY_INVALID", exception.Code, "unsupported newest manifest should fail without falling back");
}

static async Task VerifyUnsupportedLatestTemplateSchemaAsync(string sourcePath, string testRoot)
{
    var (store, capture) = await CreateHistoryCaptureAsync(sourcePath, testRoot, "template-schema");
    await UpdateManifestAsync(
        capture,
        manifest => manifest["templateSchemaVersion"] = AnswerSheetLayout.TemplateSchemaVersion + 1);

    var exception = await AssertThrowsAsync<CaptureStorageException>(
        () => Task.Run(() => store.LoadLatest()));
    AssertEqual("CAPTURE_HISTORY_INVALID", exception.Code, "unsupported template schema should be rejected");
}

static async Task VerifyLatestPathTraversalAsync(string sourcePath, string testRoot)
{
    var (store, capture) = await CreateHistoryCaptureAsync(sourcePath, testRoot, "path-traversal");
    await UpdateManifestAsync(
        capture,
        manifest => manifest["imagePath"] = $"captures/{capture.Manifest.CaptureId}/../../../outside.png");

    var exception = await AssertThrowsAsync<CaptureStorageException>(
        () => Task.Run(() => store.LoadLatest()));
    AssertEqual("CAPTURE_HISTORY_INVALID", exception.Code, "image paths outside the app data root must be rejected");
}

static async Task VerifyMissingLatestManifestAsync(string sourcePath, string testRoot)
{
    var (store, capture) = await CreateHistoryCaptureAsync(sourcePath, testRoot, "missing-manifest");
    File.Delete(capture.ManifestFilePath);

    var exception = await AssertThrowsAsync<CaptureStorageException>(
        () => Task.Run(() => store.LoadLatest()));
    AssertEqual("CAPTURE_HISTORY_INVALID", exception.Code, "missing manifest should be reported as invalid history");
}

static async Task VerifyMissingLatestImageAsync(string sourcePath, string testRoot)
{
    var (store, capture) = await CreateHistoryCaptureAsync(sourcePath, testRoot, "missing-image");
    File.Delete(capture.ImageFilePath);

    var exception = await AssertThrowsAsync<CaptureStorageException>(
        () => Task.Run(() => store.LoadLatest()));
    AssertEqual("CAPTURE_HISTORY_INVALID", exception.Code, "missing original image should be reported as invalid history");
}

static async Task<(CaptureStore Store, CaptureRecord Capture)> CreateHistoryCaptureAsync(
    string sourcePath,
    string testRoot,
    string scenario)
{
    var store = new CaptureStore(Path.Combine(testRoot, $"history-{scenario}"));
    var layout = AnswerSheetLayout.Create($"历史 {scenario}", 1, 2);
    var capture = await store.ImportAsync(layout, new LocalInputImageFile(sourcePath));
    return (store, capture);
}

static async Task UpdateManifestAsync(CaptureRecord capture, Action<JsonObject> update)
{
    var manifest = JsonNode.Parse(await File.ReadAllTextAsync(capture.ManifestFilePath))?.AsObject()
        ?? throw new InvalidOperationException("Capture manifest fixture is not a JSON object.");
    update(manifest);
    await File.WriteAllTextAsync(capture.ManifestFilePath, manifest.ToJsonString());
}

static int CountStagingDirectories(string rootDirectory)
{
    var capturesDirectory = Path.Combine(rootDirectory, "captures");
    return Directory.Exists(capturesDirectory)
        ? Directory.EnumerateDirectories(capturesDirectory, ".*.staging").Count()
        : 0;
}

static async Task<TException> AssertThrowsAsync<TException>(Func<Task> operation)
    where TException : Exception
{
    try
    {
        await operation();
    }
    catch (TException exception)
    {
        return exception;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
         directory is not null;
         directory = directory.Parent)
    {
        if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            return directory.FullName;
        }
    }

    throw new InvalidOperationException("Cannot find repository root.");
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T? actual, string message)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message}. Expected '{expected}', got '{actual}'.");
    }
}
