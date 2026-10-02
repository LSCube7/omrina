using Omrina.Core;
using SkiaSharp;

namespace Omrina.Platform;

/// <summary>Encodes legacy pixel crops and rectified template regions from validated captures.</summary>
public static class SubjectiveImageCropper
{
    public const ulong MaximumRegionPixelCount = 4_000_000;
    public const int MaximumPngBytes = 8 * 1024 * 1024;

    private static readonly SemaphoreSlim DecodeSlots = new(2, 2);

    /// <summary>Shares the bounded decode budget with template-location callers. Do not nest leases.</summary>
    public static async ValueTask<IDisposable> AcquireDecodeSlotAsync(CancellationToken cancellationToken = default)
    {
        await DecodeSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new DecodeLease();
    }

    private sealed class DecodeLease : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) DecodeSlots.Release();
        }
    }

    public static Task<byte[]> CropToPngAsync(
        IInputImageFile file,
        SubjectivePixelRectangle region,
        int expectedImageWidth,
        int expectedImageHeight,
        CancellationToken cancellationToken = default)
        => ProcessToPngAsync(file, region, null, expectedImageWidth, expectedImageHeight, cancellationToken);

    public static Task<byte[]> RectifyToPngAsync(IInputImageFile file, MappedSubjectiveRegion region,
        int expectedImageWidth, int expectedImageHeight, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(region);
        cancellationToken.ThrowIfCancellationRequested();
        var bounds = ValidateQuadrilateral(region, expectedImageWidth, expectedImageHeight);
        return ProcessToPngAsync(file, bounds, region, expectedImageWidth, expectedImageHeight, cancellationToken);
    }

    private static async Task<byte[]> ProcessToPngAsync(IInputImageFile file,
        SubjectivePixelRectangle region, MappedSubjectiveRegion? mapping, int expectedImageWidth,
        int expectedImageHeight, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();

        using (await AcquireDecodeSlotAsync(cancellationToken).ConfigureAwait(false))
        {
            var extension = Path.GetExtension(file.Name);
            if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                throw new ImageDecodeException("图像不是受支持的 PNG 或 JPEG。", ImageDecodeFailure.UnsupportedCodec);
            }

            await using var stream = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
            using var codec = SKCodec.Create(stream);
            if (codec is null)
            {
                throw new ImageDecodeException("图像编码数据无法读取。", ImageDecodeFailure.InvalidImage);
            }

            ValidateCodec(codec, extension);
            var imageWidth = codec.Info.Width;
            var imageHeight = codec.Info.Height;
            if (imageWidth <= 0 || imageHeight <= 0
                || (uint)imageWidth > SkiaImageDecoder.MaximumImageWidth
                || (uint)imageHeight > SkiaImageDecoder.MaximumImageHeight
                || (ulong)imageWidth * (uint)imageHeight > SkiaImageDecoder.MaximumPixelCount)
            {
                throw new ImageDecodeException("原图尺寸超出允许范围。", ImageDecodeFailure.DimensionsTooLarge);
            }

            if (imageWidth != expectedImageWidth || imageHeight != expectedImageHeight)
            {
                throw new ImageDecodeException("原图尺寸与采集记录不一致。", ImageDecodeFailure.InvalidImage);
            }

            var boundedRegion = SubjectivePixelRectangle.Create(
                region.X,
                region.Y,
                region.Width,
                region.Height,
                imageWidth,
                imageHeight);
            if ((ulong)boundedRegion.Width * (uint)boundedRegion.Height > MaximumRegionPixelCount)
            {
                throw new SubjectiveImageCropException(
                    "SUBJECTIVE_IMAGE_TOO_LARGE",
                    "答题区域图像过大，请缩小题目区域后重试。");
            }

            using var croppedBitmap = DecodeRegion(codec, boundedRegion, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using var rectifiedBitmap = mapping is null ? null : Rectify(croppedBitmap, boundedRegion, mapping, cancellationToken);
            using var encoded = (rectifiedBitmap ?? croppedBitmap).Encode(SKEncodedImageFormat.Png, quality: 100);
            if (encoded is null)
            {
                throw new ImageDecodeException("答题区域图像无法编码。", ImageDecodeFailure.InvalidImage);
            }

            if (encoded.Size > MaximumPngBytes)
            {
                throw new SubjectiveImageCropException(
                    "SUBJECTIVE_IMAGE_TOO_LARGE",
                    "答题区域图像过大，请缩小题目区域后重试。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var bytes = encoded.ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return bytes;
        }
    }

    private static SubjectivePixelRectangle ValidateQuadrilateral(MappedSubjectiveRegion region, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (region.OutputWidth <= 0 || region.OutputHeight <= 0
            || region.OutputWidth > SkiaImageDecoder.MaximumImageWidth
            || region.OutputHeight > SkiaImageDecoder.MaximumImageHeight
            || (ulong)region.OutputWidth * (uint)region.OutputHeight > MaximumRegionPixelCount)
            throw new SubjectiveImageCropException("SUBJECTIVE_IMAGE_TOO_LARGE", "校正后的答题区域图像过大。");
        var q = region.SourceQuadrilateral;
        PointPx[] points = [q.TopLeft, q.TopRight, q.BottomRight, q.BottomLeft];
        if (points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || point.X < 0 || point.Y < 0 || point.X > width || point.Y > height))
            throw new ArgumentException("题区四角超出原图范围。", nameof(region));
        var crosses = new double[4];
        for (var index = 0; index < 4; index++)
        {
            var a = points[index]; var b = points[(index + 1) % 4]; var c = points[(index + 2) % 4];
            crosses[index] = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
        }
        if (crosses.Any(value => !double.IsFinite(value) || Math.Abs(value) < 1e-8 || Math.Sign(value) != Math.Sign(crosses[0])))
            throw new ArgumentException("题区四角必须形成有序凸四边形。", nameof(region));
        var left = (int)Math.Floor(points.Min(point => point.X));
        var top = (int)Math.Floor(points.Min(point => point.Y));
        var right = (int)Math.Ceiling(points.Max(point => point.X));
        var bottom = (int)Math.Ceiling(points.Max(point => point.Y));
        var bounds = SubjectivePixelRectangle.Create(left, top, right - left, bottom - top, width, height);
        if ((ulong)bounds.Width * (uint)bounds.Height > MaximumRegionPixelCount)
            throw new SubjectiveImageCropException("SUBJECTIVE_IMAGE_TOO_LARGE", "题区解码范围过大。");
        _ = CreateUnitSquareTransform(q);
        return bounds;
    }

    // Analytic homography from (0,0), (1,0), (1,1), (0,1) to the four source corners.
    private static PageTransform CreateUnitSquareTransform(PixelQuadrilateral q)
    {
        var x0 = q.TopLeft.X; var y0 = q.TopLeft.Y;
        var x1 = q.TopRight.X; var y1 = q.TopRight.Y;
        var x2 = q.BottomRight.X; var y2 = q.BottomRight.Y;
        var x3 = q.BottomLeft.X; var y3 = q.BottomLeft.Y;
        var sx = x0 - x1 + x2 - x3; var sy = y0 - y1 + y2 - y3;
        double g = 0, h = 0;
        if (Math.Abs(sx) > 1e-12 || Math.Abs(sy) > 1e-12)
        {
            var dx1 = x1 - x2; var dx2 = x3 - x2;
            var dy1 = y1 - y2; var dy2 = y3 - y2;
            var denominator = dx1 * dy2 - dx2 * dy1;
            if (!double.IsFinite(denominator) || Math.Abs(denominator) < 1e-12)
                throw new ArgumentException("题区透视变换退化。", nameof(q));
            g = (sx * dy2 - dx2 * sy) / denominator;
            h = (dx1 * sy - sx * dy1) / denominator;
        }
        if (!double.IsFinite(g) || !double.IsFinite(h) || 1 + g <= 1e-12 || 1 + h <= 1e-12 || 1 + g + h <= 1e-12)
            throw new ArgumentException("题区透视变换存在奇点。", nameof(q));
        return new PageTransform(x1 - x0 + g * x1, x3 - x0 + h * x3, x0,
            y1 - y0 + g * y1, y3 - y0 + h * y3, y0, g, h, 1);
    }

    private static SKBitmap Rectify(SKBitmap source, SubjectivePixelRectangle bounds,
        MappedSubjectiveRegion region, CancellationToken cancellationToken)
    {
        var transform = CreateUnitSquareTransform(region.SourceQuadrilateral);
        var destination = new SKBitmap(new SKImageInfo(region.OutputWidth, region.OutputHeight,
            SKColorType.Rgba8888, SKAlphaType.Premul));
        try
        {
            // Work in premultiplied RGBA bytes, avoiding colour halos around transparent pixels.
            var sourceBytes = new byte[checked(source.RowBytes * source.Height)];
            System.Runtime.InteropServices.Marshal.Copy(source.GetPixels(), sourceBytes, 0, sourceBytes.Length);
            var destinationRow = new byte[destination.RowBytes];
            for (var y = 0; y < destination.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < destination.Width; x++)
                {
                    if ((x & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var point = transform.Map(new PointMm((x + 0.5) / destination.Width, (y + 0.5) / destination.Height));
                    if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                        throw new ArgumentException("题区透视变换无效。", nameof(region));
                    var sourceX = Math.Clamp(point.X - bounds.X - 0.5, 0, source.Width - 1d);
                    var sourceY = Math.Clamp(point.Y - bounds.Y - 0.5, 0, source.Height - 1d);
                    var x0 = (int)Math.Floor(sourceX); var y0 = (int)Math.Floor(sourceY);
                    var x1 = Math.Min(x0 + 1, source.Width - 1); var y1 = Math.Min(y0 + 1, source.Height - 1);
                    var fx = sourceX - x0; var fy = sourceY - y0;
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var top = sourceBytes[y0 * source.RowBytes + x0 * 4 + channel] * (1 - fx)
                            + sourceBytes[y0 * source.RowBytes + x1 * 4 + channel] * fx;
                        var bottom = sourceBytes[y1 * source.RowBytes + x0 * 4 + channel] * (1 - fx)
                            + sourceBytes[y1 * source.RowBytes + x1 * 4 + channel] * fx;
                        destinationRow[x * 4 + channel] = (byte)Math.Clamp(Math.Round(top * (1 - fy) + bottom * fy), 0, 255);
                    }
                }
                System.Runtime.InteropServices.Marshal.Copy(destinationRow, 0,
                    IntPtr.Add(destination.GetPixels(), checked(y * destination.RowBytes)), destinationRow.Length);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return destination;
        }
        catch { destination.Dispose(); throw; }
    }

    private static void ValidateCodec(SKCodec codec, string extension)
    {
        var actualExtension = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => ".png",
            SKEncodedImageFormat.Jpeg => ".jpg",
            _ => null
        };
        if (actualExtension is null
            || (extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                && !actualExtension.Equals(".png", StringComparison.Ordinal))
            || (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                && !actualExtension.Equals(".jpg", StringComparison.Ordinal)))
        {
            throw new ImageDecodeException(
                "图像实际编码格式与文件扩展名不匹配，或不是 PNG/JPEG。",
                ImageDecodeFailure.UnsupportedCodec);
        }
    }

    private static SKBitmap DecodeRegion(
        SKCodec codec,
        SubjectivePixelRectangle region,
        CancellationToken cancellationToken)
    {
        var requested = new SKRectI(
            region.X,
            region.Y,
            checked(region.X + region.Width),
            checked(region.Y + region.Height));
        var validSubset = requested;
        if (codec.GetValidSubset(ref validSubset) && validSubset == requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var subsetInfo = new SKImageInfo(
                region.Width,
                region.Height,
                SKColorType.Rgba8888,
                SKAlphaType.Premul);
            var subsetBitmap = new SKBitmap(subsetInfo);
            try
            {
                var result = codec.GetPixels(
                    subsetInfo,
                    subsetBitmap.GetPixels(),
                    new SKCodecOptions(validSubset));
                cancellationToken.ThrowIfCancellationRequested();
                if (result == SKCodecResult.Success)
                {
                    return subsetBitmap;
                }

                throw new ImageDecodeException(
                    "答题区域像素无法完整解码。",
                    ImageDecodeFailure.InvalidImage);
            }
            catch
            {
                subsetBitmap.Dispose();
                throw;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return DecodeRegionByScanlines(codec, region, cancellationToken);
    }

    private static SKBitmap DecodeRegionByScanlines(
        SKCodec codec,
        SubjectivePixelRectangle region,
        CancellationToken cancellationToken)
    {
        var sourceInfo = new SKImageInfo(
            codec.Info.Width,
            codec.Info.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        var startResult = codec.StartScanlineDecode(sourceInfo);
        if (startResult == SKCodecResult.Unimplemented)
        {
            return DecodeRegionFromFullBitmap(codec, region, cancellationToken);
        }

        if (startResult != SKCodecResult.Success)
        {
            throw new ImageDecodeException("图像像素无法开始逐行解码。", ImageDecodeFailure.InvalidImage);
        }

        using var sourceRow = new SKBitmap(new SKImageInfo(
            sourceInfo.Width,
            1,
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        var destination = new SKBitmap(new SKImageInfo(
            region.Width,
            region.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        try
        {
            var sourcePixels = sourceRow.GetPixels();
            var destinationPixels = destination.GetPixels();
            var sourceRowBytes = sourceRow.RowBytes;
            var rowBuffer = new byte[sourceRowBytes];
            var regionRowBytes = checked(region.Width * 4);
            var regionBottom = checked(region.Y + region.Height);

            for (var y = 0; y < sourceInfo.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (codec.GetScanlines(sourcePixels, 1, sourceRowBytes) != 1)
                {
                    throw new ImageDecodeException("图像像素未能完整解码。", ImageDecodeFailure.InvalidImage);
                }

                if (y < region.Y || y >= regionBottom)
                {
                    continue;
                }

                System.Runtime.InteropServices.Marshal.Copy(sourcePixels, rowBuffer, 0, sourceRowBytes);
                var destinationRow = IntPtr.Add(
                    destinationPixels,
                    checked((y - region.Y) * destination.RowBytes));
                System.Runtime.InteropServices.Marshal.Copy(
                    rowBuffer,
                    checked(region.X * 4),
                    destinationRow,
                    regionRowBytes);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return destination;
        }
        catch
        {
            destination.Dispose();
            throw;
        }
    }

    private static SKBitmap DecodeRegionFromFullBitmap(
        SKCodec codec,
        SubjectivePixelRectangle region,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceInfo = new SKImageInfo(
            codec.Info.Width,
            codec.Info.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        using var full = new SKBitmap(sourceInfo);
        var result = codec.GetPixels(sourceInfo, full.GetPixels());
        cancellationToken.ThrowIfCancellationRequested();
        if (result != SKCodecResult.Success)
        {
            throw new ImageDecodeException("图像像素无法完整解码。", ImageDecodeFailure.InvalidImage);
        }

        var cropped = new SKBitmap(new SKImageInfo(
            region.Width,
            region.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        try
        {
            // ExtractSubset shares the full image's pixel allocation. Copy only the selected
            // rows so the full decode is released before perspective sampling and encoding.
            var rowBytes = checked(region.Width * 4);
            var row = new byte[rowBytes];
            for (var y = 0; y < region.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = IntPtr.Add(full.GetPixels(), checked((region.Y + y) * full.RowBytes + region.X * 4));
                var destination = IntPtr.Add(cropped.GetPixels(), checked(y * cropped.RowBytes));
                System.Runtime.InteropServices.Marshal.Copy(source, row, 0, rowBytes);
                System.Runtime.InteropServices.Marshal.Copy(row, 0, destination, rowBytes);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return cropped;
        }
        catch
        {
            cropped.Dispose();
            throw;
        }
    }
}

public sealed class SubjectiveImageCropException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
