using Omrina.Core;
using Omrina.Platform;
using SkiaSharp;

internal static class SubjectivePerspectiveRegression
{
    public static async Task RunAsync()
    {
        const int width = 80, height = 70;
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++) bitmap.SetPixel(x, y, new SKColor((byte)(x * 2), (byte)(y * 2), 20, 255));
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("synthetic PNG encoding failed");
        using var jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, 100)
            ?? throw new InvalidOperationException("synthetic JPEG encoding failed");
        var reference = new PageTransform(40, 5, 10, 3, 35, 10, 0.25, 0.15, 1);
        foreach (var format in new[] { ("pattern.png", png!.ToArray(), 1), ("pattern.jpg", jpeg!.ToArray(), 6) })
        {
            for (var rotation = 0; rotation < 4; rotation++)
            {
                PointPx Expected(double u, double v) => rotation switch
                {
                    0 => reference.Map(new PointMm(u, v)),
                    1 => reference.Map(new PointMm(v, 1 - u)),
                    2 => reference.Map(new PointMm(1 - u, 1 - v)),
                    _ => reference.Map(new PointMm(1 - v, u))
                };
                var quad = new PixelQuadrilateral(Expected(0, 0), Expected(1, 0), Expected(1, 1), Expected(0, 1));
                var region = Mapping(quad, width, height);
                var input = new MemoryImageFile(format.Item1, format.Item2);
                var bytes = await SubjectiveImageCropper.RectifyToPngAsync(input, region, width, height);
                Check(input.LastStream?.WasDisposed == true, "source stream lifetime");
                Check(bytes.Length <= SubjectiveImageCropper.MaximumPngBytes
                    && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "bounded PNG output");
                using var decoded = SKBitmap.Decode(bytes);
                Check(decoded.Width == region.OutputWidth && decoded.Height == region.OutputHeight, "rectified target size");
                foreach (var x in new[] { 3, 14, 26 })
                    foreach (var y in new[] { 3, 12, 21 })
                    {
                        var point = Expected((x + 0.5) / decoded.Width, (y + 0.5) / decoded.Height);
                        var actual = decoded.GetPixel(x, y);
                        Check(Math.Abs(actual.Red - 2 * (point.X - 0.5)) <= format.Item3
                            && Math.Abs(actual.Green - 2 * (point.Y - 0.5)) <= format.Item3
                            && Math.Abs(actual.Blue - 20) <= format.Item3,
                            $"{format.Item1} perspective/rotation {rotation} sample ({x},{y})");
                    }
            }
        }
        var axis = Mapping(new(new(4, 5), new(10, 5), new(10, 8), new(4, 8)), width, height)
            with { OutputWidth = 6, OutputHeight = 3 };
        using (var result = SKBitmap.Decode(await SubjectiveImageCropper.RectifyToPngAsync(
                   new MemoryImageFile("axis.png", png!.ToArray()), axis, width, height)))
            Check(result.GetPixel(0, 0) == new SKColor(8, 10, 20, 255), "axis aligned pixel centres");
        var valid = Mapping(new(new(10, 10), new(50, 12), new(45, 50), new(12, 55)), width, height);
        var inputFile = new MemoryImageFile("valid.png", png!.ToArray());
        await RejectAsync(() => SubjectiveImageCropper.RectifyToPngAsync(inputFile,
            valid with { SourceQuadrilateral = new(new(double.NaN, 1), new(40, 1), new(40, 40), new(1, 40)) }, width, height));
        await RejectAsync(() => SubjectiveImageCropper.RectifyToPngAsync(inputFile,
            valid with { SourceQuadrilateral = new(new(1, 1), new(40, 40), new(40, 1), new(1, 40)) }, width, height));
        await RejectAsync(() => SubjectiveImageCropper.RectifyToPngAsync(inputFile,
            valid with { SourceQuadrilateral = new(new(1, 1), new(40, 1), new(20, 10), new(1, 40)) }, width, height));
        await RejectAsync(() => SubjectiveImageCropper.RectifyToPngAsync(inputFile,
            valid with { SourceQuadrilateral = new(new(-1, 1), new(40, 1), new(40, 40), new(1, 40)) }, width, height));
        await RejectAsync(() => SubjectiveImageCropper.RectifyToPngAsync(inputFile,
            valid with { OutputWidth = 2001, OutputHeight = 2000 }, width, height));
        await RejectAsync(() => SubjectiveImageCropper.RectifyToPngAsync(inputFile,
            valid with { SourceQuadrilateral = new(new(0, 0), new(3000, 0), new(3000, 3000), new(0, 3000)) }, 3000, 3000));
        await RejectAsync(() => SubjectiveImageCropper.RectifyToPngAsync(inputFile, valid, width + 1, height));
        await RejectAsync(() => SubjectiveImageCropper.RectifyToPngAsync(new MemoryImageFile("wrong.jpg", png.ToArray()), valid, width, height));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var notOpened = new MemoryImageFile("cancel.png", png.ToArray());
        await CancelledAsync(() => SubjectiveImageCropper.RectifyToPngAsync(notOpened, valid, width, height, cancellation.Token));
        Check(notOpened.LastStream is null, "cancel before opening source");
        using var cancelOnOpen = new CancellationTokenSource();
        var opened = new MemoryImageFile("cancel.png", png.ToArray(), cancelOnOpen.Cancel);
        await CancelledAsync(() => SubjectiveImageCropper.RectifyToPngAsync(opened, valid, width, height, cancelOnOpen.Token));
        Check(opened.LastStream?.WasDisposed == true, "cancelled decode stream disposed");
        await VerifySlotsAsync();
    }

    private static MappedSubjectiveRegion Mapping(PixelQuadrilateral quad, int width, int height)
        => new(Guid.NewGuid(), 11, 10m, new(110, 60, 80, 50), quad, 30, 25,
            SubjectivePixelRectangle.Create(0, 0, width, height, width, height));

    private static async Task VerifySlotsAsync()
    {
        using var first = await SubjectiveImageCropper.AcquireDecodeSlotAsync();
        using var second = await SubjectiveImageCropper.AcquireDecodeSlotAsync();
        using var cancel = new CancellationTokenSource();
        var queued = SubjectiveImageCropper.AcquireDecodeSlotAsync(cancel.Token).AsTask();
        Check(!queued.IsCompleted, "third decode must queue");
        cancel.Cancel();
        await CancelledAsync(async () => { using var lease = await queued; });
        first.Dispose();
        first.Dispose();
        using var next = await SubjectiveImageCropper.AcquireDecodeSlotAsync();
        using var nextCancel = new CancellationTokenSource();
        var stillQueued = SubjectiveImageCropper.AcquireDecodeSlotAsync(nextCancel.Token).AsTask();
        Check(!stillQueued.IsCompleted, "idempotent lease must not add slots");
        nextCancel.Cancel();
        await CancelledAsync(async () => { using var lease = await stillQueued; });
    }

    private static async Task RejectAsync(Func<Task<byte[]>> action)
    {
        try { await action(); }
        catch (ArgumentException) { return; }
        catch (ImageDecodeException) { return; }
        catch (SubjectiveImageCropException exception) when (exception.Code == "SUBJECTIVE_IMAGE_TOO_LARGE") { return; }
        throw new InvalidOperationException("invalid perspective crop was accepted");
    }
    private static async Task CancelledAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("cancelled crop completed");
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private sealed class MemoryImageFile(string name, byte[] bytes, Action? onOpen = null) : IInputImageFile
    {
        public string Name => name;
        public ulong Length => (ulong)bytes.Length;
        public TrackedStream? LastStream { get; private set; }
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastStream = new TrackedStream(bytes);
            onOpen?.Invoke();
            return ValueTask.FromResult<Stream>(LastStream);
        }
    }
    private sealed class TrackedStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public bool WasDisposed { get; private set; }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }
}
