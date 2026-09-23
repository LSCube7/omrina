namespace Omrina.Core;

/// <summary>
/// An 8-bit luminance image supplied to the answer-sheet recognizer.
/// Pixel values use the usual range where 0 is black and 255 is white.
/// </summary>
public sealed class GrayImage
{
    private readonly byte[] pixels;

    /// <summary>
    /// Creates a grayscale image. The pixel array is copied by default so the
    /// recognizer always sees an immutable snapshot of its input.
    /// </summary>
    public GrayImage(int width, int height, byte[] pixels, bool copy = true)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "图像宽度必须大于零。");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "图像高度必须大于零。");
        }

        ArgumentNullException.ThrowIfNull(pixels);
        var expectedLength = checked(width * height);
        if (pixels.Length != expectedLength)
        {
            throw new ArgumentException(
                $"灰度像素数量必须为 {expectedLength}，实际为 {pixels.Length}。",
                nameof(pixels));
        }

        Width = width;
        Height = height;
        this.pixels = copy ? pixels.ToArray() : pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Returns a read-only view of row-major grayscale pixels.</summary>
    public ReadOnlyMemory<byte> Pixels => pixels;

    public byte GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException();
        }

        return pixels[y * Width + x];
    }

    /// <summary>Returns a pixel without throwing; outside pixels are white.</summary>
    public byte GetPixelOrWhite(int x, int y)
    {
        return (uint)x < (uint)Width && (uint)y < (uint)Height
            ? pixels[y * Width + x]
            : (byte)255;
    }

    /// <summary>
    /// Builds a grayscale image from a row-major span. The data is copied.
    /// </summary>
    public static GrayImage FromBytes(int width, int height, ReadOnlySpan<byte> pixels)
    {
        return new GrayImage(width, height, pixels.ToArray(), copy: false);
    }
}
