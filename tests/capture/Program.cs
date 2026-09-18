using AnswerSheet.Core;
using AnswerSheet.Desktop;
using System.Text.Json;
using Windows.Storage;

var failures = new List<string>();
var repositoryRoot = FindRepositoryRoot();

var testRoot = Path.Combine(
    repositoryRoot,
    "artifacts",
    "m1-capture-test",
    DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
Directory.CreateDirectory(testRoot);
var sourcePath = Path.Combine(testRoot, "source.png");
await File.WriteAllBytesAsync(sourcePath, Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));

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
    await RunAsync("PNG import keeps bytes and writes association manifest", failures, () => VerifyImportAsync(sourcePath, testRoot));
    await RunAsync("scan source type uses the same storage API", failures, () => VerifyScanSourceAsync(sourcePath, testRoot));
    await RunAsync("invalid and unsupported files leave no staging record", failures, () => VerifyInvalidInputsAsync(testRoot));
    await RunAsync("cancelled import leaves no staging record", failures, () => VerifyCancellationAsync(sourcePath, testRoot));
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

Console.WriteLine("PASS: 5 capture regression tests.");
return 0;

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

static async Task VerifyImportAsync(string sourcePath, string testRoot)
{
    var layout = AnswerSheetLayout.Create("PNG 导入", 20, 4);
    var source = await StorageFile.GetFileFromPathAsync(sourcePath);
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
    AssertTrue(manifest.GetProperty("pixelWidth").GetUInt32() > 0, "manifest should record decoded width");
    AssertTrue(manifest.GetProperty("pixelHeight").GetUInt32() > 0, "manifest should record decoded height");
}

static async Task VerifyScanSourceAsync(string sourcePath, string testRoot)
{
    var layout = AnswerSheetLayout.Create("扫描页", 20, 4);
    var source = await StorageFile.GetFileFromPathAsync(sourcePath);
    var store = new CaptureStore(testRoot);
    var capture = await store.ImportAsync(layout, source, CaptureSourceType.Scan);

    AssertEqual("scan", capture.Manifest.SourceType, "scan source should be persisted as scan");
    AssertEqual(layout.TemplateId, capture.Manifest.TemplateId, "scan should use the same template association");
}

static async Task VerifyInvalidInputsAsync(string testRoot)
{
    var store = new CaptureStore(testRoot);
    var layout = AnswerSheetLayout.Create("非法图像", 1, 2);
    var invalidPath = Path.Combine(testRoot, "invalid.png");
    var unsupportedPath = Path.Combine(testRoot, "invalid.txt");
    await File.WriteAllTextAsync(invalidPath, "this is not an image");
    await File.WriteAllTextAsync(unsupportedPath, "unsupported");

    var invalidFile = await StorageFile.GetFileFromPathAsync(invalidPath);
    var unsupportedFile = await StorageFile.GetFileFromPathAsync(unsupportedPath);
    var stagingBefore = CountStagingDirectories(testRoot);

    var invalidException = await AssertThrowsAsync<CaptureValidationException>(
        () => store.ImportAsync(layout, invalidFile, CaptureSourceType.Import));
    AssertEqual("INVALID_IMAGE", invalidException.Code, "invalid image should have a stable error code");

    var unsupportedException = await AssertThrowsAsync<CaptureValidationException>(
        () => store.ImportAsync(layout, unsupportedFile, CaptureSourceType.Import));
    AssertEqual("UNSUPPORTED_IMAGE_TYPE", unsupportedException.Code, "unsupported extension should be rejected before reading");
    AssertEqual(stagingBefore, CountStagingDirectories(testRoot), "failed validation should leave no staging directory");
}

static async Task VerifyCancellationAsync(string sourcePath, string testRoot)
{
    var layout = AnswerSheetLayout.Create("取消导入", 1, 2);
    var source = await StorageFile.GetFileFromPathAsync(sourcePath);
    var store = new CaptureStore(testRoot);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    await AssertThrowsAsync<OperationCanceledException>(
        () => store.ImportAsync(layout, source, CaptureSourceType.Import, cancellation.Token));
    AssertEqual(0, CountStagingDirectories(testRoot), "cancelled import should leave no staging directory");
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
