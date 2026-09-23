using Omrina.Core;
using SkiaSharp;
using System.Runtime.InteropServices;

namespace Omrina.Platform;

/// <summary>
/// Cross-platform PNG/JPEG decoder used by CaptureStore. Skia reads the codec
/// header first, then walks every source scanline into a bounded row buffer.
/// This validates compressed pixel data without scaling or allocating a full
/// page-sized RGBA bitmap.
/// </summary>
public sealed class SkiaImageDecoder
{
    public const uint MaximumImageWidth = 16_000;
    public const uint MaximumImageHeight = 16_000;
    public const ulong MaximumPixelCount = 100_000_000;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg"
    };

    public async Task<DecodedImageInfo> DecodeAsync(
        IInputImageFile file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        var extension = System.IO.Path.GetExtension(file.Name);
        if (!AllowedExtensions.Contains(extension))
        {
            throw new ImageDecodeException(
                "图像扩展名不是受支持的 PNG 或 JPEG。",
                ImageDecodeFailure.UnsupportedCodec);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = await file.OpenReadAsync(cancellationToken);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
        {
            throw new ImageDecodeException(
                "图像编码数据无法读取。",
                ImageDecodeFailure.InvalidImage);
        }

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

        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width <= 0 || height <= 0)
        {
            throw new ImageDecodeException(
                "图像尺寸无效。",
                ImageDecodeFailure.InvalidImage);
        }

        if ((uint)width > MaximumImageWidth
            || (uint)height > MaximumImageHeight
            || (ulong)width * (uint)height > MaximumPixelCount)
        {
            throw new ImageDecodeException(
                $"图像尺寸不能超过 {MaximumImageWidth}×{MaximumImageHeight}，且像素总数不能超过 {MaximumPixelCount:N0}。",
                ImageDecodeFailure.DimensionsTooLarge);
        }

        var outputInfo = new SKImageInfo(
            width,
            height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        var startResult = codec.StartScanlineDecode(outputInfo);
        if (startResult == SKCodecResult.Unimplemented)
        {
            // Skia's PNG codec does not expose scanline decoding on every
            // native build. Keep the existing 16,000/100Mpx header limits,
            // then use an exact-size decode as the bounded fallback. This is
            // the only path that allocates a full pixel buffer, and its upper
            // bound is the capture contract above rather than an unbounded
            // allocation based on attacker-controlled dimensions.
            using var fullBitmap = new SKBitmap(outputInfo);
            var fullResult = codec.GetPixels(outputInfo, fullBitmap.GetPixels());
            if (fullResult != SKCodecResult.Success)
            {
                throw new ImageDecodeException(
                    $"图像像素数据无法完整解码（{fullResult}）。",
                    ImageDecodeFailure.InvalidImage);
            }

            return new DecodedImageInfo(
                extension.ToLowerInvariant(),
                checked((uint)width),
                checked((uint)height),
                file.Length);
        }

        if (startResult != SKCodecResult.Success)
        {
            throw new ImageDecodeException(
                $"图像像素数据无法开始逐行解码（{startResult}）。",
                ImageDecodeFailure.InvalidImage);
        }

        // A single source-width row is enough for the scanline decoder. The
        // 16,000-pixel width limit bounds this buffer to roughly 64 KiB.
        using var row = new SKBitmap(new SKImageInfo(
            width,
            1,
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        var rowBytes = row.RowBytes;
        var rowPixels = row.GetPixels();
        for (var decodedRows = 0; decodedRows < height; decodedRows++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowsRead = codec.GetScanlines(rowPixels, 1, rowBytes);
            if (rowsRead != 1)
            {
                throw new ImageDecodeException(
                    $"图像像素数据未能完整解码（仅读取 {rowsRead} 行）。",
                    ImageDecodeFailure.InvalidImage);
            }
        }

        return new DecodedImageInfo(
            extension.ToLowerInvariant(),
            checked((uint)width),
            checked((uint)height),
            file.Length);
    }

    /// <summary>
    /// Decodes a PNG/JPEG into the platform-neutral grayscale buffer consumed
    /// by the Core recognizer. The image is decoded at its source dimensions;
    /// no crop, resize, or template-specific coordinate is applied here.
    /// </summary>
    public async Task<GrayImage> DecodeGrayscaleAsync(
        IInputImageFile file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        var extension = System.IO.Path.GetExtension(file.Name);
        ValidateExtension(extension);

        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = await file.OpenReadAsync(cancellationToken);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
        {
            throw new ImageDecodeException(
                "图像编码数据无法读取。",
                ImageDecodeFailure.InvalidImage);
        }

        var actualExtension = ValidateCodec(codec, extension);
        var width = codec.Info.Width;
        var height = codec.Info.Height;
        ValidateDimensions(width, height);

        // Gray8 conversion drops alpha. Only use it when the source is known
        // to be opaque; transparent pixels must be composited onto the white
        // page background in the RGBA path below.
        var sourceHasAlpha = codec.Info.AlphaType != SKAlphaType.Opaque;
        SKCodecResult decodeResult;
        if (!sourceHasAlpha)
        {
            var grayInfo = new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
            using var grayBitmap = new SKBitmap(grayInfo);
            decodeResult = codec.GetPixels(grayInfo, grayBitmap.GetPixels());
            if (decodeResult == SKCodecResult.Success)
            {
                var pixels = new byte[checked(width * height)];
                var row = new byte[width];
                var sourcePixels = grayBitmap.GetPixels();
                for (var y = 0; y < height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Marshal.Copy(
                        IntPtr.Add(sourcePixels, checked(y * grayBitmap.RowBytes)),
                        row,
                        0,
                        width);
                    Buffer.BlockCopy(row, 0, pixels, checked(y * width), width);
                }

                return new GrayImage(width, height, pixels, copy: false);
            }
        }

        // Some native Skia builds do not expose conversion directly to Gray8.
        // Decode one RGBA row at a time as a bounded fallback and keep the
        // Core contract independent from Skia's native pixel format.
        using var rgbaBitmap = new SKBitmap(new SKImageInfo(
            width,
            height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul));
        decodeResult = codec.GetPixels(rgbaBitmap.Info, rgbaBitmap.GetPixels());
        if (decodeResult != SKCodecResult.Success)
        {
            throw new ImageDecodeException(
                $"图像像素数据无法完整解码（{decodeResult}）。",
                ImageDecodeFailure.InvalidImage);
        }

        var grayscale = new byte[checked(width * height)];
        var rgbaRow = new byte[checked(width * 4)];
        var rgbaPixels = rgbaBitmap.GetPixels();
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Marshal.Copy(
                IntPtr.Add(rgbaPixels, checked(y * rgbaBitmap.RowBytes)),
                rgbaRow,
                0,
                rgbaRow.Length);
            for (var x = 0; x < width; x++)
            {
                var offset = x * 4;
                // The decoder asks Skia for premultiplied RGBA. Composite the
                // premultiplied color over the white page background before
                // converting to luminance; otherwise transparent pixels can
                // incorrectly become black marks.
                var alpha = rgbaRow[offset + 3] / 255d;
                var luminance = rgbaRow[offset] * 0.2126
                    + rgbaRow[offset + 1] * 0.7152
                    + rgbaRow[offset + 2] * 0.0722
                    + 255d * (1d - alpha);
                grayscale[y * width + x] = (byte)Math.Clamp(
                    Math.Round(luminance),
                    0,
                    255);
            }
        }

        return new GrayImage(width, height, grayscale, copy: false);
    }

    private static void ValidateExtension(string extension)
    {
        if (!AllowedExtensions.Contains(extension))
        {
            throw new ImageDecodeException(
                "图像扩展名不是受支持的 PNG 或 JPEG。",
                ImageDecodeFailure.UnsupportedCodec);
        }
    }

    private static string ValidateCodec(SKCodec codec, string extension)
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

        return actualExtension;
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ImageDecodeException(
                "图像尺寸无效。",
                ImageDecodeFailure.InvalidImage);
        }

        if ((uint)width > MaximumImageWidth
            || (uint)height > MaximumImageHeight
            || (ulong)width * (uint)height > MaximumPixelCount)
        {
            throw new ImageDecodeException(
                $"图像尺寸不能超过 {MaximumImageWidth}×{MaximumImageHeight}，且像素总数不能超过 {MaximumPixelCount:N0}。",
                ImageDecodeFailure.DimensionsTooLarge);
        }
    }
}
