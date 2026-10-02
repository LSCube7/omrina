using Omrina.Core;
using SkiaSharp;

namespace Omrina.Platform;

/// <summary>Encodes one reviewer-selected pixel region from a validated capture as PNG.</summary>
public static class SubjectiveImageCropper
{
    public const ulong MaximumRegionPixelCount = 4_000_000;
    public const int MaximumPngBytes = 8 * 1024 * 1024;

    private static readonly SemaphoreSlim DecodeSlots = new(2, 2);

    public static async Task<byte[]> CropToPngAsync(
        IInputImageFile file,
        SubjectivePixelRectangle region,
        int expectedImageWidth,
        int expectedImageHeight,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();

        await DecodeSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
            using var encoded = croppedBitmap.Encode(SKEncodedImageFormat.Png, quality: 100);
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
            return encoded.ToArray();
        }
        finally
        {
            DecodeSlots.Release();
        }
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

        var regionRect = new SKRectI(
            region.X,
            region.Y,
            checked(region.X + region.Width),
            checked(region.Y + region.Height));
        var cropped = new SKBitmap(new SKImageInfo(
            region.Width,
            region.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        try
        {
            if (!full.ExtractSubset(cropped, regionRect))
            {
                throw new ImageDecodeException("答题区域无法从原图提取。", ImageDecodeFailure.InvalidImage);
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
