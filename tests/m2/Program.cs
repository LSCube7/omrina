using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using Omrina.Core;
using Omrina.Platform;

var failures = new List<string>();

Run("answer key validates completeness and range", failures, VerifyAnswerKeyValidation);
Run("accepted recognition scores with decimal points", failures, VerifyFinalScore);
Run("review and rejection dispositions are explicit", failures, VerifyScoreDispositions);
Run("page quality warning remains pending after question review", failures, VerifyPageQualityReview);
Run("manual review preserves blank distinction and history", failures, VerifyManualReviewHistory);
Run("JSON and CSV exports are safe", failures, VerifySafeExports);
Run("platform decoder validates ordinary PNG pixels", failures, VerifyOrdinaryImageDecode);
Run("platform decoder composites transparent pixels onto white", failures, VerifyTransparentPixelDecode);
Run("recognition service rejects oversized external input before opening", failures, VerifyOversizedInputRejected);
Run("recognition runs on a worker and propagates cancellation", failures, VerifyRecognitionRunsOffCallerAndCancels);

if (failures.Count > 0)
{
    Console.Error.WriteLine($"FAIL: {failures.Count} M2 regression test(s) failed.");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }

    return 1;
}

Console.WriteLine("PASS: 10 M2 scoring/review/platform regression tests.");
return 0;

static void Run(string name, List<string> failures, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS: {name}.");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
    }
}

static void VerifyAnswerKeyValidation()
{
    var key = AnswerKey.Create(2, 4, new Dictionary<int, string>
    {
        [1] = "A",
        [2] = "D"
    });
    AssertEqual(2, key.QuestionCount, "key should retain question count");
    AssertEqual("D", key[2], "key should retain answer labels");

    AssertThrows<ArgumentException>(
        () => AnswerKey.Create(2, 4, new Dictionary<int, string> { [1] = "A" }),
        "missing question should be rejected");
    AssertThrows<ArgumentException>(
        () => AnswerKey.Create(2, 4, new Dictionary<int, string> { [1] = "A", [3] = "B" }),
        "out-of-range question should be rejected");
    AssertThrows<ArgumentException>(
        () => AnswerKey.Create(1, 2, new Dictionary<int, string> { [1] = "C" }),
        "option outside the template should be rejected");
    AssertThrows<ArgumentException>(
        () => AnswerKey.Create(1, 4, new Dictionary<int, string> { [1] = "a" }),
        "lowercase option should be rejected");

    var mutableSource = new Dictionary<int, string> { [1] = "A" };
    var immutable = AnswerKey.Create(1, 4, mutableSource);
    mutableSource[1] = "D";
    AssertEqual("A", immutable[1], "key should copy caller data");
}

static void VerifyFinalScore()
{
    var recognition = CreateRecognition(
        RecognitionStatus.Accepted,
        Question(1, QuestionMarkState.Single, "A", "A", "B", "C", "D"),
        Question(2, QuestionMarkState.Single, "C", "B", "C", "D"));
    var key = AnswerKey.Create(2, 4, new Dictionary<int, string>
    {
        [1] = "A",
        [2] = "D"
    });

    var result = ScoringEngine.Score(recognition, key, new ScoringOptions(2.5m));
    AssertEqual(2.5m, result.TotalScore, "one answer should earn one configured question value");
    AssertEqual(5m, result.MaximumScore, "maximum score should use decimal points");
    AssertEqual(ScoringDisposition.Final, result.Disposition, "resolved answers should be final");
    AssertTrue(result.QuestionScores[0].IsCorrect == true, "first answer should be correct");
    AssertTrue(result.QuestionScores[1].IsCorrect == false, "second answer should be incorrect");
    AssertEqual("D", result.QuestionScores[1].ExpectedAnswer, "captured answer must not become the key");
}

static void VerifyScoreDispositions()
{
    var key = AnswerKey.Create(1, 2, new Dictionary<int, string> { [1] = "A" });
    var reviewRecognition = CreateRecognition(
        RecognitionStatus.ReviewRequired,
        Question(1, QuestionMarkState.Uncertain, null, "A", "B"));
    var provisional = ScoringEngine.Score(reviewRecognition, key);
    AssertEqual(0m, provisional.TotalScore, "provisional scoring may show the current estimate");
    AssertTrue(provisional.IsProvisional, "uncertain recognition must remain provisional");

    var statusOnlyReview = ScoringEngine.Score(
        CreateRecognition(
            RecognitionStatus.ReviewRequired,
            Question(1, QuestionMarkState.Single, "A", "A", "B")),
        key);
    AssertTrue(statusOnlyReview.IsProvisional, "ReviewRequired status must not be promoted by an answer key");

    var noKey = ScoringEngine.Score(reviewRecognition);
    AssertTrue(noKey.TotalScore is null, "recognition without an answer key must not score");
    AssertEqual(ScoreUnavailableReason.AnswerKeyMissing, noKey.UnavailableReason, "missing key reason should be explicit");
    AssertTrue(noKey.RequiresReview, "unscored recognition should retain pending review state");

    var rejectedRecognition = CreateRecognition(
        RecognitionStatus.Rejected,
        Question(1, QuestionMarkState.Single, "A", "B"));
    var rejected = ScoringEngine.Score(rejectedRecognition, key);
    AssertTrue(rejected.TotalScore is null, "rejected recognition must not score");
    AssertEqual(ScoreUnavailableReason.RecognitionRejected, rejected.UnavailableReason, "rejected reason should be explicit");
    var rejectedReview = ManualReview.Start(rejectedRecognition);
    AssertTrue(!rejectedReview.CanReview, "rejected recognition must remain non-reviewable");
    AssertThrows<InvalidOperationException>(
        () => rejectedReview.SetAnswer(1, "A", "reviewer"),
        "rejected recognition must reject question edits");
    AssertTrue(ScoringEngine.Score(rejectedReview, key).TotalScore is null, "review cannot calibrate rejection");
}

static void VerifyPageQualityReview()
{
    var recognition = new RecognitionResult(
        "template-test",
        AnswerSheetLayout.TemplateSchemaVersion,
        RecognitionStatus.ReviewRequired,
        PageOrientation.Degrees0,
        transform: null,
        confidence: 0.7,
        new[] { Question(1, QuestionMarkState.Single, "A", "A", "B") },
        new[]
        {
            new RecognitionDiagnostic(
                RecognitionDiagnosticCode.PageQualityUncertain,
                RecognitionDiagnosticSeverity.Warning,
                "page quality needs review")
        });
    var key = AnswerKey.Create(1, 2, new Dictionary<int, string> { [1] = "A" });

    var unscored = ScoringEngine.Score(recognition);
    AssertTrue(unscored.RequiresReview, "recognition without an answer key should retain pending review");

    var reviewed = ManualReview.Start(recognition).SetAnswer(1, "B", "reviewer");
    var scored = ScoringEngine.Score(reviewed, key);
    AssertTrue(!scored.QuestionScores[0].RequiresReview, "manual answer should resolve the question-level review");
    AssertEqual(
        ScoringDisposition.Provisional,
        scored.Disposition,
        "page-level quality warning should keep the score provisional after question review");
    AssertTrue(scored.RequiresReview, "page-level quality warning should remain visible as pending review");
}

static void VerifyManualReviewHistory()
{
    var recognition = CreateRecognition(
        RecognitionStatus.Accepted,
        Question(1, QuestionMarkState.Blank, null, "A", "B"));
    var startedAt = new DateTimeOffset(2026, 9, 20, 1, 2, 3, TimeSpan.Zero);
    var session = ManualReview.Start(recognition, startedAt);
    AssertTrue(session.CurrentAnswers[1].IsBlank, "engine blank should remain blank");
    AssertTrue(!session.CurrentAnswers[1].IsBlankConfirmed, "engine blank must not count as manually confirmed");
    AssertTrue(session.CurrentAnswers[1].RequiresReview, "unreviewed blank should remain pending");

    var reviewedAt = startedAt.AddMinutes(2);
    var reviewed = session.ConfirmBlank(1, "reviewer-1", "确认未作答", reviewedAt);
    AssertEqual(0, session.History.Count, "immutable session must not change in place");
    AssertEqual(1, reviewed.History.Count, "one review action should create one history entry");
    AssertEqual("reviewer-1", reviewed.History[0].Reviewer, "reviewer should be retained");
    AssertEqual("确认未作答", reviewed.History[0].Reason, "reason should be retained");
    AssertEqual(reviewedAt, reviewed.History[0].Timestamp, "timestamp should be retained");
    AssertTrue(reviewed.CurrentAnswers[1].IsBlankConfirmed, "manual blank confirmation should be explicit");
    AssertTrue(!reviewed.CurrentAnswers[1].RequiresReview, "confirmed blank should no longer be pending");

    var key = AnswerKey.Create(1, 2, new Dictionary<int, string> { [1] = "A" });
    var final = ScoringEngine.Score(reviewed, key);
    AssertEqual(ScoringDisposition.Final, final.Disposition, "confirmed blank should be scoreable as zero");
    AssertEqual(0m, final.TotalScore, "confirmed blank should earn zero points");

    var corrected = reviewed.SetAnswer(1, "B", "reviewer-2", "改判", reviewedAt.AddMinutes(1));
    AssertTrue(corrected.CurrentAnswers[1].IsManuallyReviewed, "corrected answer should be marked reviewed");
    AssertEqual(2, corrected.History.Count, "each manual change should remain in history");
    AssertEqual("B", corrected.History[1].AfterAnswer, "history should retain the new answer");
    AssertTrue(reviewed.CurrentAnswers[1].IsBlankConfirmed, "previous snapshot must remain immutable");
}

static void VerifySafeExports()
{
    var recognition = CreateRecognition(
        RecognitionStatus.Accepted,
        Question(1, QuestionMarkState.Uncertain, null, "A", "B"));
    var session = ManualReview.Start(recognition)
        .SetAnswer(1, "A", "=SUM(1,1)", "备注,\n第二行");
    var key = AnswerKey.Create(1, 2, new Dictionary<int, string> { [1] = "A" });
    var result = ScoringEngine.Score(session, key);

    var json = ResultExporter.ToJson(result);
    using var parsed = JsonDocument.Parse(json);
    AssertEqual("=SUM(1,1)", parsed.RootElement
        .GetProperty("review")
        .GetProperty("history")[0]
        .GetProperty("reviewer")
        .GetString(), "JSON should preserve data without executing it");

    var csv = ResultExporter.ToCsv(result);
    AssertTrue(csv.Contains("'=SUM(1,1)", StringComparison.Ordinal), "CSV should prefix formula-like reviewer values");
    AssertTrue(csv.Contains("\"备注,\n第二行\"", StringComparison.Ordinal), "CSV should quote commas and newlines");
    AssertTrue(csv.StartsWith("recordType,templateId,", StringComparison.Ordinal), "CSV should include stable headers");
}

static void VerifyOrdinaryImageDecode()
{
    var content = CreateRgbaPng(
        width: 2,
        height: 1,
        rgba: new byte[]
        {
            255, 255, 255, 255,
            0, 0, 0, 255
        });
    var file = new MemoryInputImageFile("ordinary.png", content);

    var decoded = new SkiaImageDecoder()
        .DecodeAsync(file)
        .GetAwaiter()
        .GetResult();

    AssertEqual(".png", decoded.Extension, "PNG decode should retain its normalized extension");
    AssertEqual(2U, decoded.PixelWidth, "PNG decode should report its width");
    AssertEqual(1U, decoded.PixelHeight, "PNG decode should report its height");
    AssertEqual(checked((ulong)content.Length), decoded.ByteLength, "PNG decode should report its byte length");
    AssertTrue(file.WasOpened, "ordinary PNG should be opened and decoded");
}

static void VerifyTransparentPixelDecode()
{
    // Transparent and partially transparent ink should be blended with the
    // white page background; opaque black must remain a printed mark.
    var file = new MemoryInputImageFile(
        "transparent.png",
        CreateRgbaPng(
            width: 3,
            height: 1,
            rgba: new byte[]
            {
                0, 0, 0, 0,
                0, 0, 0, 128,
                0, 0, 0, 255
            }));

    var image = new SkiaImageDecoder()
        .DecodeGrayscaleAsync(file)
        .GetAwaiter()
        .GetResult();

    AssertEqual((byte)255, image.GetPixel(0, 0), "transparent black should composite to white");
    AssertEqual((byte)127, image.GetPixel(1, 0), "half-transparent black should composite over white");
    AssertEqual((byte)0, image.GetPixel(2, 0), "opaque black should remain black");
}

static void VerifyOversizedInputRejected()
{
    var file = new MemoryInputImageFile(
        "too-large.png",
        Array.Empty<byte>(),
        reportedLength: SkiaAnswerSheetRecognitionService.MaximumInputFileSizeBytes + 1);
    var layout = AnswerSheetLayout.Create("m2-test", 20, 4);
    var service = new SkiaAnswerSheetRecognitionService();

    try
    {
        service.RecognizeAsync(layout, file).GetAwaiter().GetResult();
    }
    catch (ImageDecodeException exception)
    {
        AssertEqual(ImageDecodeFailure.FileTooLarge, exception.Failure, "oversized input failure");
        AssertTrue(!file.WasOpened, "oversized input should be rejected before opening");
        return;
    }

    throw new InvalidOperationException("oversized input should throw ImageDecodeException");
}

static void VerifyRecognitionRunsOffCallerAndCancels()
{
    var file = new MemoryInputImageFile(
        "worker-test.png",
        CreateRgbaPng(1, 1, new byte[] { 255, 255, 255, 255 }));
    var recognizer = new BlockingAnswerSheetRecognizer();
    var service = new SkiaAnswerSheetRecognitionService(new SkiaImageDecoder(), recognizer);
    var layout = AnswerSheetLayout.Create("m2-worker-test", 1, 2);
    using var cancellation = new CancellationTokenSource();

    var recognitionTask = service.RecognizeAsync(layout, file, cancellation.Token);
    AssertTrue(
        recognizer.RecognitionStarted.Wait(TimeSpan.FromSeconds(5)),
        "recognizer should start on a worker thread");
    AssertTrue(file.WasOpenedOnThreadPool, "image decoding should start off the caller thread");
    AssertTrue(recognizer.RanOnThreadPool, "Core recognition should not run on the caller thread");

    cancellation.Cancel();
    AssertThrows<OperationCanceledException>(
        () => recognitionTask.GetAwaiter().GetResult(),
        "cancellation should propagate while Core recognition is running");
}

static byte[] CreateRgbaPng(int width, int height, byte[] rgba)
{
    if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4))
    {
        throw new ArgumentException("RGBA test image dimensions do not match the pixel data.");
    }

    using var png = new MemoryStream();
    png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

    var header = new byte[13];
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), checked((uint)width));
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), checked((uint)height));
    header[8] = 8; // bit depth
    header[9] = 6; // RGBA
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
    var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
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
            crc = (crc & 1) == 0
                ? crc >> 1
                : (crc >> 1) ^ 0xEDB8_8320u;
        }
    }

    return ~crc;
}

static RecognitionResult CreateRecognition(
    RecognitionStatus status,
    params QuestionRecognitionResult[] questions)
{
    return new RecognitionResult(
        "template-test",
        AnswerSheetLayout.TemplateSchemaVersion,
        status,
        PageOrientation.Degrees0,
        transform: null,
        confidence: 0.95,
        questions,
        Array.Empty<RecognitionDiagnostic>());
}

static QuestionRecognitionResult Question(
    int number,
    QuestionMarkState state,
    string? selectedLabel,
    params string[] labels)
{
    var options = labels
        .Select(
            (label, index) => new OptionRecognitionResult(
                index + 1,
                label,
                FillRatio: label == selectedLabel ? 0.9 : 0.1,
                State: label == selectedLabel ? OptionMarkState.Selected : OptionMarkState.Unselected,
                Confidence: 0.9))
        .ToArray();
    return new QuestionRecognitionResult(number, state, options, 0.9);
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
        throw new InvalidOperationException($"{message}; expected '{expected}', actual '{actual}'.");
    }
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"{message}; expected {typeof(TException).Name}.");
}

sealed class MemoryInputImageFile : IInputImageFile
{
    private readonly byte[] content;

    public MemoryInputImageFile(string name, byte[] content, ulong? reportedLength = null)
    {
        Name = name;
        this.content = content;
        Length = reportedLength ?? checked((ulong)content.LongLength);
    }

    public string Name { get; }

    public ulong Length { get; }

    public bool WasOpened { get; private set; }

    public bool WasOpenedOnThreadPool { get; private set; }

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        WasOpened = true;
        WasOpenedOnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new MemoryStream(content, writable: false);
        return ValueTask.FromResult(stream);
    }
}

sealed class BlockingAnswerSheetRecognizer : IAnswerSheetRecognizer
{
    public ManualResetEventSlim RecognitionStarted { get; } = new(false);

    public bool RanOnThreadPool { get; private set; }

    public RecognitionResult Recognize(
        AnswerSheetLayout layout,
        GrayImage image,
        CancellationToken cancellationToken = default)
    {
        RanOnThreadPool = Thread.CurrentThread.IsThreadPoolThread;
        RecognitionStarted.Set();
        cancellationToken.WaitHandle.WaitOne();
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Cancellation was expected in this test.");
    }
}
