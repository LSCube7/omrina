using Omrina.Core;
using System.Text;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Omrina.Desktop;

/// <summary>Describes where a captured answer-sheet image came from.</summary>
public enum CaptureSourceType
{
    Import,
    Scan
}

/// <summary>A stable reference to the layout used for one captured page.</summary>
public sealed record CaptureTemplateReference(
    string TemplateId,
    int SchemaVersion,
    string Title,
    int QuestionCount,
    int OptionsPerQuestion)
{
    public const int CurrentSchemaVersion = AnswerSheetLayout.TemplateSchemaVersion;

    public static CaptureTemplateReference FromLayout(AnswerSheetLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return new CaptureTemplateReference(
            layout.TemplateId,
            CurrentSchemaVersion,
            layout.Title,
            layout.QuestionCount,
            layout.OptionsPerQuestion);
    }
}

/// <summary>Metadata recorded for the original image stored by <see cref="CaptureStore" />.</summary>
public sealed record CaptureManifest(
    int ManifestSchemaVersion,
    string CaptureId,
    DateTimeOffset CreatedAtUtc,
    string SourceType,
    string TemplateId,
    int TemplateSchemaVersion,
    string TemplateTitle,
    int QuestionCount,
    int OptionsPerQuestion,
    string ImagePath,
    string OriginalFileName,
    string ImageExtension,
    uint PixelWidth,
    uint PixelHeight,
    ulong ByteLength);

/// <summary>Result of a successful image import or scan-page save.</summary>
public sealed record CaptureRecord(
    CaptureManifest Manifest,
    string CaptureDirectory,
    string ImageFilePath,
    string ManifestFilePath);

/// <summary>Known validation and storage failures shown by the capture UI.</summary>
public class CaptureException : Exception
{
    public CaptureException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class CaptureValidationException : CaptureException
{
    public CaptureValidationException(string code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }
}

public sealed class CaptureStorageException : CaptureException
{
    public CaptureStorageException(string code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }
}

/// <summary>
/// Validates user-selected PNG/JPEG files and stores an immutable capture directory.
/// The source is read only after the user has selected a <see cref="StorageFile" />.
/// </summary>
public sealed class CaptureStore
{
    public const int ManifestSchemaVersion = 1;
    public const ulong MaximumFileSizeBytes = 100UL * 1024 * 1024;
    public const uint MaximumImageWidth = 16_000;
    public const uint MaximumImageHeight = 16_000;
    public const ulong MaximumPixelCount = 100_000_000;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public CaptureStore()
        : this(GetDefaultRootDirectory())
    {
    }

    /// <param name="rootDirectory">The app-owned local data directory. Tests may provide a temporary directory.</param>
    public CaptureStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Capture storage directory is required.", nameof(rootDirectory));
        }

        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    /// <summary>
    /// Imports a user-selected PNG/JPEG file, or saves a scan output, while associating it with a layout.
    /// </summary>
    public async Task<CaptureRecord> ImportAsync(
        AnswerSheetLayout layout,
        StorageFile file,
        CaptureSourceType sourceType = CaptureSourceType.Import,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(file);
        ValidateSourceType(sourceType);

        var template = CaptureTemplateReference.FromLayout(layout);
        var image = await CaptureImageValidator.ValidateAsync(file, cancellationToken);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            return await PersistAsync(template, file, image, sourceType, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<CaptureRecord> PersistAsync(
        CaptureTemplateReference template,
        StorageFile sourceFile,
        CaptureImageInfo image,
        CaptureSourceType sourceType,
        CancellationToken cancellationToken)
    {
        var capturesDirectory = Path.Combine(RootDirectory, "captures");
        Directory.CreateDirectory(capturesDirectory);

        var captureId = Guid.NewGuid().ToString("N");
        var stagingDirectory = Path.Combine(capturesDirectory, $".{captureId}.staging");
        var captureDirectory = Path.Combine(capturesDirectory, captureId);
        var imageName = $"original{image.Extension}";
        var imageFilePath = Path.Combine(stagingDirectory, imageName);
        var manifestFilePath = Path.Combine(stagingDirectory, "manifest.json");
        var manifestTempPath = Path.Combine(stagingDirectory, ".manifest.json.tmp");
        var relativeImagePath = ToManifestPath(Path.Combine("captures", captureId, imageName));
        var committed = false;

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            await CopyOriginalAsync(sourceFile, image, imageFilePath, cancellationToken);

            var manifest = new CaptureManifest(
                ManifestSchemaVersion,
                captureId,
                DateTimeOffset.UtcNow,
                SourceTypeValue(sourceType),
                template.TemplateId,
                template.SchemaVersion,
                template.Title,
                template.QuestionCount,
                template.OptionsPerQuestion,
                relativeImagePath,
                sourceFile.Name,
                image.Extension,
                image.PixelWidth,
                image.PixelHeight,
                image.ByteLength);

            await WriteManifestAsync(manifest, manifestTempPath, manifestFilePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // The final directory is created only after both original bytes and manifest are complete.
            // Directory.Move cannot overwrite an existing capture, preserving previously saved pages.
            Directory.Move(stagingDirectory, captureDirectory);
            committed = true;

            return new CaptureRecord(
                manifest,
                captureDirectory,
                Path.Combine(captureDirectory, imageName),
                Path.Combine(captureDirectory, "manifest.json"));
        }
        catch (OperationCanceledException)
        {
            CleanupStagingDirectory(stagingDirectory);
            throw;
        }
        catch (CaptureException)
        {
            CleanupStagingDirectory(stagingDirectory);
            throw;
        }
        catch (Exception exception)
        {
            CleanupStagingDirectory(stagingDirectory);
            throw new CaptureStorageException(
                "CAPTURE_COMMIT_FAILED",
                "图像保存失败，请重试。",
                exception);
        }
        finally
        {
            if (!committed)
            {
                CleanupStagingDirectory(stagingDirectory);
            }
        }
    }

    private static async Task CopyOriginalAsync(
        StorageFile sourceFile,
        CaptureImageInfo image,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        try
        {
            ulong copiedLength;
            using (var randomAccessStream = await sourceFile.OpenReadAsync())
            {
                await using var input = randomAccessStream.AsStreamForRead();
                await using var output = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);

                await input.CopyToAsync(output, 1024 * 1024, cancellationToken);
                await output.FlushAsync(cancellationToken);
                copiedLength = checked((ulong)output.Length);
            }

            if (copiedLength != image.ByteLength)
            {
                throw new CaptureStorageException(
                    "SOURCE_CHANGED",
                    "图像在读取过程中发生变化，请重新选择文件。");
            }

            // Validate the bytes that are actually stored. This closes the gap between
            // initial picker metadata and the copy if the source changed while it was read.
            var copiedFile = await StorageFile.GetFileFromPathAsync(destinationPath);
            var copiedImage = await CaptureImageValidator.ValidateAsync(copiedFile, cancellationToken);
            if (copiedImage != image)
            {
                throw new CaptureStorageException(
                    "SOURCE_CHANGED",
                    "图像在读取过程中发生变化，请重新选择文件。");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CaptureException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new CaptureStorageException(
                "IMAGE_COPY_FAILED",
                "读取或保存图像失败，请确认文件仍可访问后重试。",
                exception);
        }
    }

    private static async Task WriteManifestAsync(
        CaptureManifest manifest,
        string temporaryPath,
        string finalPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, Encoding.UTF8, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, finalPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new CaptureStorageException(
                "MANIFEST_WRITE_FAILED",
                "图像记录保存失败，请重试。",
                exception);
        }
    }

    private static void ValidateSourceType(CaptureSourceType sourceType)
    {
        if (!Enum.IsDefined(sourceType))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceType), sourceType, "Unknown capture source type.");
        }
    }

    private static string SourceTypeValue(CaptureSourceType sourceType)
    {
        return sourceType switch
        {
            CaptureSourceType.Import => "import",
            CaptureSourceType.Scan => "scan",
            _ => throw new ArgumentOutOfRangeException(nameof(sourceType), sourceType, "Unknown capture source type.")
        };
    }

    private static string ToManifestPath(string path)
    {
        return path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static void CleanupStagingDirectory(string stagingDirectory)
    {
        try
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Only the store-created staging directory is touched here. A failed cleanup must not
            // turn into deletion of a source file or any other user-selected path.
            System.Diagnostics.Debug.WriteLine(
                $"Capture staging cleanup failed: {exception.GetType().Name}.");
        }
    }

    private static string GetDefaultRootDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("无法确定应用本地数据目录。");
        }

        return Path.Combine(localApplicationData, "AnswerSheet");
    }
}

internal sealed record CaptureImageInfo(
    string Extension,
    uint PixelWidth,
    uint PixelHeight,
    ulong ByteLength);

internal static class CaptureImageValidator
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg"
    };

    public static async Task<CaptureImageInfo> ValidateAsync(
        StorageFile file,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(file.Name);
        if (!AllowedExtensions.Contains(extension))
        {
            throw new CaptureValidationException(
                "UNSUPPORTED_IMAGE_TYPE",
                "只支持 PNG 或 JPEG 图像。请重新选择文件。");
        }

        BasicProperties properties;
        try
        {
            properties = await file.GetBasicPropertiesAsync();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new CaptureValidationException(
                "SOURCE_UNAVAILABLE",
                "无法读取所选图像，请确认文件仍可访问后重试。",
                exception);
        }

        if (properties.Size == 0)
        {
            throw new CaptureValidationException(
                "EMPTY_IMAGE",
                "所选图像为空，请重新选择 PNG 或 JPEG 文件。");
        }

        if (properties.Size > CaptureStore.MaximumFileSizeBytes)
        {
            throw new CaptureValidationException(
                "IMAGE_FILE_TOO_LARGE",
                $"图像文件不能超过 {CaptureStore.MaximumFileSizeBytes / (1024 * 1024)} MB。");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var randomAccessStream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
            var codecId = decoder.DecoderInformation.CodecId;
            if (codecId != BitmapDecoder.PngDecoderId && codecId != BitmapDecoder.JpegDecoderId)
            {
                throw new CaptureValidationException(
                    "UNSUPPORTED_IMAGE_CODEC",
                    "图像编码格式不是受支持的 PNG 或 JPEG，请重新选择文件。");
            }

            var pixelWidth = decoder.PixelWidth;
            var pixelHeight = decoder.PixelHeight;
            if (pixelWidth == 0 || pixelHeight == 0)
            {
                throw new CaptureValidationException(
                    "INVALID_IMAGE_DIMENSIONS",
                    "无法读取图像尺寸，请重新选择有效图像。");
            }

            if (pixelWidth > CaptureStore.MaximumImageWidth
                || pixelHeight > CaptureStore.MaximumImageHeight
                || (ulong)pixelWidth * pixelHeight > CaptureStore.MaximumPixelCount)
            {
                throw new CaptureValidationException(
                    "IMAGE_DIMENSIONS_TOO_LARGE",
                    $"图像尺寸不能超过 {CaptureStore.MaximumImageWidth}×{CaptureStore.MaximumImageHeight}，且像素总数不能超过 {CaptureStore.MaximumPixelCount:N0}。");
            }

            // BitmapDecoder.CreateAsync validates the container header. Requesting a
            // one-pixel transformed decode also exercises the compressed pixel data,
            // without allocating a full-size RGBA buffer for a large page.
            var transform = new BitmapTransform
            {
                ScaledWidth = 1,
                ScaledHeight = 1
            };
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            _ = pixelData.DetachPixelData();

            return new CaptureImageInfo(
                extension.ToLowerInvariant(),
                pixelWidth,
                pixelHeight,
                properties.Size);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CaptureException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new CaptureValidationException(
                "INVALID_IMAGE",
                "无法解码所选图像，请重新选择有效的 PNG 或 JPEG 文件。",
                exception);
        }
    }
}
