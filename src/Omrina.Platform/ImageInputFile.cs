namespace Omrina.Platform;

/// <summary>
/// A file selected by the user for image import. The platform picker owns how a
/// selection is made; the capture store only receives this narrow read contract.
/// </summary>
public interface IInputImageFile
{
    string Name { get; }

    /// <summary>The length observed when the selection was created.</summary>
    ulong Length { get; }

    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// File-system implementation used by the desktop pickers and by scan output.
/// </summary>
public sealed class LocalInputImageFile : IInputImageFile
{
    public LocalInputImageFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("图像文件路径不能为空。", nameof(path));
        }

        Path = System.IO.Path.GetFullPath(path);
        Name = System.IO.Path.GetFileName(Path);
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("图像文件名不能为空。", nameof(path));
        }
    }

    public string Path { get; }

    public string Name { get; }

    public ulong Length
    {
        get
        {
            var length = new FileInfo(Path).Length;
            return checked((ulong)length);
        }
    }

    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }
}

/// <summary>Image metadata returned after a complete codec decode check.</summary>
public sealed record DecodedImageInfo(
    string Extension,
    uint PixelWidth,
    uint PixelHeight,
    ulong ByteLength);

/// <summary>Raised when a selected image cannot be decoded as a supported image.</summary>
public sealed class ImageDecodeException : Exception
{
    public ImageDecodeException(string message, ImageDecodeFailure failure, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public ImageDecodeFailure Failure { get; }
}

public enum ImageDecodeFailure
{
    InvalidImage,
    UnsupportedCodec,
    DimensionsTooLarge,
    FileTooLarge
}
