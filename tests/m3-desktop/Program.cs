using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Omrina.Core;
using Omrina.Desktop;
using Omrina.Platform;
using Omrina.Protocol;
using Omrina.Scanning;
using Omrina.Server;

if (args.Contains("--http-harness", StringComparer.Ordinal))
{
    return await RunHttpHarnessAsync();
}

try
{
    VerifyScannerDeviceIdentityMap();
    await VerifyDesktopAgentOperationsAsync();
    await VerifyActualSkiaRejectsSyntheticFixtureAsync();
    Console.WriteLine("PASS: scanner identity stability/revocation, M3 adapter workflow (accepted recognition is simulated), and actual Skia rejection without scoring.");
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
