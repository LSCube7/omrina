using Omrina.Core;
using Omrina.Platform;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;

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
    int OptionsPerQuestion,
    string? TemplateJson = null,
    AnswerSheetLayout? Layout = null)
{
    // Schema 1 is retained for captures created before mixed answer sheets.
    public const int CurrentSchemaVersion = AnswerSheetLayout.TemplateSchemaVersion;

    public static CaptureTemplateReference FromLayout(AnswerSheetLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return new CaptureTemplateReference(
            layout.TemplateId,
            layout.SchemaVersion,
            layout.Title,
            layout.QuestionCount,
            layout.OptionsPerQuestion,
            layout.SchemaVersion != AnswerSheetLayout.TemplateSchemaVersion ? layout.ToJson() : null,
            layout);
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
    ulong ByteLength)
{
    public SchoolPageMetadata? SchoolMetadata { get; init; }
    public string? ImageSha256 { get; init; }
    public string? CandidateId { get; init; }
    public string? IdentityStatus { get; init; }
}

/// <summary>Result of a successful image import or scan-page save.</summary>
public sealed record CaptureRecord(
    CaptureManifest Manifest,
    string CaptureDirectory,
    string ImageFilePath,
    string ManifestFilePath)
{
    /// <summary>The verified template layout, reconstructed for legacy records or loaded from its sidecar.</summary>
    public AnswerSheetLayout? TemplateLayout { get; init; }
}

public sealed record CaptureStoreDiagnostic(string ResourceId, string Code, string Message);

public sealed record CaptureStorePage(
    IReadOnlyList<CaptureRecord> Items,
    IReadOnlyList<CaptureStoreDiagnostic> Diagnostics,
    string? NextCursor);

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
/// The source is read only after the user has selected an <see cref="IInputImageFile" />.
/// </summary>
public sealed class CaptureStore
{
    public const int ManifestSchemaVersion = 1;
    private const int MaximumManifestBytes = 64 * 1024;
    private const int MaximumTemplateSnapshotBytes = 1024 * 1024;
    private const int MaximumPageSize = 50;
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
    /// Loads the newest committed capture without touching the original bytes.
    /// A malformed newest record is reported to the caller instead of being
    /// silently replaced with an older capture.
    /// </summary>
    public CaptureRecord? LoadLatest()
    {
        var capturesDirectory = Path.Combine(RootDirectory, "captures");
        if (!Directory.Exists(capturesDirectory))
        {
            return null;
        }

        DirectoryInfo? latestDirectory;
        try
        {
            latestDirectory = new DirectoryInfo(capturesDirectory)
                .EnumerateDirectories()
                .Where(directory => !directory.Name.StartsWith(".", StringComparison.Ordinal))
                .OrderByDescending(directory => directory.LastWriteTimeUtc)
                .ThenByDescending(directory => directory.Name, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (Exception exception)
        {
            throw new CaptureStorageException(
                "CAPTURE_HISTORY_READ_FAILED",
                "无法读取最近采集记录，请稍后重试。",
                exception);
        }

        return latestDirectory is null ? null : ReadCommittedRecord(latestDirectory);
    }

    /// <summary>Loads a committed capture by its opaque GUID ID, never by a caller-supplied path.</summary>
    public CaptureRecord? LoadById(string captureId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(captureId, out var parsedId) || parsedId == Guid.Empty)
        {
            throw new CaptureValidationException("INVALID_CAPTURE_ID", "采集记录 ID 无效。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var capturesDirectory = Path.Combine(RootDirectory, "captures");
        if (!Directory.Exists(capturesDirectory))
        {
            return null;
        }

        RejectReparsePoint(RootDirectory, isDirectory: true);
        RejectReparsePoint(capturesDirectory, isDirectory: true);
        var captureDirectory = Path.Combine(capturesDirectory, parsedId.ToString("N"));
        if (!Directory.Exists(captureDirectory))
        {
            return null;
        }

        RejectReparsePoint(captureDirectory, isDirectory: true);
        cancellationToken.ThrowIfCancellationRequested();
        return ReadCommittedRecord(new DirectoryInfo(captureDirectory));
    }

    /// <summary>Enumerates captures in GUID-N ordinal order with bounded page memory.</summary>
    public CaptureStorePage LoadPage(
        int pageSize = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePageSize(pageSize);
        var afterId = DecodeCursor(cursor);
        var items = new List<CaptureRecord>(pageSize);
        var diagnostics = new List<CaptureStoreDiagnostic>();
        var lastConsumedId = afterId;
        var hasMoreRecords = false;
        var invalidDirectoryNameFound = false;
        var consumedCount = 0;
        var capturesDirectory = Path.Combine(RootDirectory, "captures");

        if (!Directory.Exists(capturesDirectory))
        {
            return new CaptureStorePage(items, diagnostics, NextCursor: null);
        }

        try
        {
            RejectReparsePoint(RootDirectory, isDirectory: true);
            RejectReparsePoint(capturesDirectory, isDirectory: true);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nextName = FindNextCaptureDirectory(
                    capturesDirectory,
                    lastConsumedId,
                    cancellationToken,
                    ref invalidDirectoryNameFound);
                if (nextName is null)
                {
                    break;
                }

                var normalizedId = nextName;
                consumedCount++;
                CaptureRecord? record;
                try
                {
                    record = LoadById(normalizedId, cancellationToken);
                }
                catch (CaptureException exception)
                {
                    diagnostics.Add(new CaptureStoreDiagnostic(normalizedId, exception.Code, exception.Message));
                    record = null;
                }

                lastConsumedId = normalizedId;
                if (record is not null)
                {
                    items.Add(record);
                }

                if (consumedCount == pageSize)
                {
                    hasMoreRecords = FindNextCaptureDirectory(
                        capturesDirectory,
                        lastConsumedId,
                        cancellationToken,
                        ref invalidDirectoryNameFound) is not null;
                    break;
                }
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CaptureStorageException(
                "CAPTURE_HISTORY_READ_FAILED",
                "无法读取采集记录列表，请稍后重试。",
                exception);
        }

        if (cursor is null && invalidDirectoryNameFound)
        {
            diagnostics.Add(new CaptureStoreDiagnostic(
                "invalid-capture-directory-names",
                "CAPTURE_HISTORY_INVALID",
                "检测到无法识别的采集记录目录；这些目录未被打开。"));
        }

        var nextCursor = hasMoreRecords ? EncodeCursor(lastConsumedId!) : null;
        return new CaptureStorePage(items, diagnostics, nextCursor);
    }

    /// <summary>
    /// Returns an opaque file reader. Opening it revalidates the committed capture and manifest;
    /// callers never receive a path that could be reconstructed or altered.
    /// </summary>
    public IInputImageFile GetInputFile(string captureId, CancellationToken cancellationToken = default)
    {
        var record = LoadById(captureId, cancellationToken)
            ?? throw new CaptureStorageException("CAPTURE_NOT_FOUND", "找不到本地采集图像。");
        return new StoredCaptureInputImageFile(
            this,
            record.Manifest.CaptureId,
            $"capture{record.Manifest.ImageExtension.ToLowerInvariant()}",
            record.Manifest.ByteLength);
    }

    private async ValueTask<Stream> OpenStoredImageAsync(
        string captureId,
        ulong expectedLength,
        CancellationToken cancellationToken)
    {
        var record = await Task.Run(() => LoadById(captureId, cancellationToken), cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CaptureStorageException("CAPTURE_NOT_FOUND", "找不到本地采集图像。");
        cancellationToken.ThrowIfCancellationRequested();
        if (record.Manifest.ByteLength != expectedLength)
        {
            throw InvalidHistory("采集图像与已验证记录不一致。", null);
        }

        var stream = new FileStream(
            record.ImageFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != checked((long)expectedLength))
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw InvalidHistory("采集图像与已验证记录不一致。", null);
        }

        return stream;
    }

    private CaptureRecord ReadCommittedRecord(DirectoryInfo captureDirectory)
    {
        var manifestFilePath = Path.Combine(captureDirectory.FullName, "manifest.json");
        try
        {
            RejectReparsePoint(RootDirectory, isDirectory: true);
            RejectReparsePoint(Path.Combine(RootDirectory, "captures"), isDirectory: true);
            RejectReparsePoint(captureDirectory.FullName, isDirectory: true);
            if (!File.Exists(manifestFilePath))
            {
                throw InvalidHistory("最近采集记录缺少模板关联信息，无法打开。", null);
            }

            RejectReparsePoint(manifestFilePath, isDirectory: false);
            var manifestJson = ReadManifestJson(manifestFilePath);
            var manifest = JsonSerializer.Deserialize<CaptureManifest>(manifestJson, ManifestJsonOptions)
                ?? throw InvalidHistory("最近采集记录的模板关联信息为空。", null);

            if (manifest.ManifestSchemaVersion != ManifestSchemaVersion)
            {
                throw InvalidHistory(
                    "最近采集记录的关联信息版本不受当前应用支持。",
                    null);
            }

            if (manifest.TemplateSchemaVersion is not (AnswerSheetLayout.TemplateSchemaVersion or AnswerSheetLayout.MixedTemplateSchemaVersion or 3))
            {
                throw new CaptureStorageException(
                    "CAPTURE_TEMPLATE_UNSUPPORTED",
                    $"最近采集记录的模板 schema {manifest.TemplateSchemaVersion} 不受当前版本支持。",
                    null);
            }

            ValidateHistoryManifest(manifest, captureDirectory, imagePath: null);

            if (string.IsNullOrWhiteSpace(manifest.CaptureId)
                || !string.Equals(manifest.CaptureId, captureDirectory.Name, StringComparison.Ordinal))
            {
                throw InvalidHistory("最近采集记录的 CaptureId 与目录不一致。", null);
            }

            if (string.IsNullOrWhiteSpace(manifest.ImagePath)
                || Path.IsPathRooted(manifest.ImagePath))
            {
                throw InvalidHistory("最近采集记录的原图路径无效。", null);
            }

            var imagePath = Path.GetFullPath(
                Path.Combine(RootDirectory, manifest.ImagePath.Replace('/', Path.DirectorySeparatorChar)));
            var rootDirectory = Path.GetFullPath(RootDirectory);
            var captureRoot = Path.GetFullPath(
                Path.Combine(rootDirectory, "captures", captureDirectory.Name));
            EnsurePathWithinRoot(rootDirectory, imagePath, "最近采集记录的原图路径越过了应用数据目录。", null);

            if (!PathsEqual(captureRoot, captureDirectory.FullName)
                || !PathsEqual(Path.GetDirectoryName(imagePath) ?? string.Empty, captureRoot))
            {
                throw InvalidHistory("最近采集记录的原图路径与记录目录不一致。", null);
            }

            if (!File.Exists(imagePath))
            {
                throw InvalidHistory("最近采集记录的原图不存在，无法打开。", null);
            }

            RejectReparsePoint(imagePath, isDirectory: false);

            ValidateHistoryManifest(manifest, captureDirectory, imagePath);
            var templateLayout = ReadAssociatedTemplate(manifest, captureDirectory);

            return new CaptureRecord(
                manifest,
                captureDirectory.FullName,
                imagePath,
                manifestFilePath)
            {
                TemplateLayout = templateLayout
            };
        }
        catch (CaptureStorageException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw InvalidHistory("最近采集记录的模板关联信息格式无效。", exception);
        }
        catch (IOException exception)
        {
            throw InvalidHistory("最近采集记录无法读取，请确认本地文件仍可访问。", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw InvalidHistory("最近采集记录无法读取，请检查应用本地数据权限。", exception);
        }
        catch (Exception exception)
        {
            throw InvalidHistory("最近采集记录已损坏，无法打开。", exception);
        }
    }

    private static void EnsurePathWithinRoot(
        string rootDirectory,
        string candidatePath,
        string message,
        Exception? innerException)
    {
        var relative = Path.GetRelativePath(rootDirectory, candidatePath);
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw InvalidHistory(message, innerException);
        }
    }

    private static AnswerSheetLayout ReadAssociatedTemplate(CaptureManifest manifest, DirectoryInfo captureDirectory)
    {
        if (manifest.TemplateSchemaVersion == AnswerSheetLayout.TemplateSchemaVersion)
        {
            var legacyLayout = AnswerSheetLayout.Create(
                manifest.TemplateTitle,
                manifest.QuestionCount,
                manifest.OptionsPerQuestion);
            if (legacyLayout.TemplateId != manifest.TemplateId)
            {
                throw new CaptureStorageException(
                    "CAPTURE_TEMPLATE_MISMATCH",
                    "采集记录中的旧版模板身份与模板参数不一致。");
            }

            return legacyLayout;
        }

        var templatePath = Path.Combine(captureDirectory.FullName, "template.json");
        if (!File.Exists(templatePath))
        {
            throw new CaptureStorageException(
                "CAPTURE_TEMPLATE_MISSING",
                "采集记录缺少混合答题纸模板快照，无法定位主观题区域。");
        }

        RejectReparsePoint(templatePath, isDirectory: false);
        AnswerSheetLayout layout;
        try
        {
            layout = AnswerSheetLayout.FromJson(ReadTemplateSnapshotJson(templatePath));
        }
        catch (CaptureStorageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        {
            throw new CaptureStorageException(
                "CAPTURE_TEMPLATE_INVALID",
                "采集记录中的混合答题纸模板快照无效。",
                exception);
        }

        if (layout.SchemaVersion != manifest.TemplateSchemaVersion
            || layout.SchemaVersion is not (AnswerSheetLayout.MixedTemplateSchemaVersion or 3)
            || (layout.SchemaVersion == AnswerSheetLayout.MixedTemplateSchemaVersion && layout.SubjectiveRegions.Count == 0)
            || layout.TemplateId != manifest.TemplateId
            || layout.Title != manifest.TemplateTitle
            || layout.QuestionCount != manifest.QuestionCount
            || layout.OptionsPerQuestion != manifest.OptionsPerQuestion
            || (layout.SchemaVersion == 3 && layout.SchoolMetadata != manifest.SchoolMetadata))
        {
            throw new CaptureStorageException(
                "CAPTURE_TEMPLATE_MISMATCH",
                "采集记录的模板快照与模板身份不一致。");
        }

        if (layout.SchemaVersion == 3)
        {
            var imagePath = Path.Combine(captureDirectory.FullName, $"original{manifest.ImageExtension.ToLowerInvariant()}");
            if (manifest.IdentityStatus is not ("Identified" or "RequireAssociation")
                || (manifest.IdentityStatus == "RequireAssociation" && manifest.CandidateId is not null)
                || (manifest.IdentityStatus == "Identified" && (layout.CandidateArea is null
                    || manifest.CandidateId is null || manifest.CandidateId.Length != layout.SchoolDefinition!.CandidateIdentity.Digits
                    || manifest.CandidateId.Any(character => character is < '0' or > '9'))))
                throw new CaptureStorageException("CAPTURE_IDENTITY_INVALID", "答卷身份状态与模板不一致，请重新检查原图。");
            if (manifest.ImageSha256 is null || ComputeImageHash(imagePath) != manifest.ImageSha256)
                throw new CaptureStorageException("CAPTURE_IMAGE_MISMATCH", "答卷原图与保存记录不一致，无法确认考试归属。");
        }
        return layout;
    }

    private static string ComputeImageHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static CaptureStorageException InvalidHistory(string message, Exception? innerException)
    {
        return new CaptureStorageException("CAPTURE_HISTORY_INVALID", message, innerException);
    }

    private static string ReadManifestJson(string path) => ReadBoundedUtf8Json(
        path,
        MaximumManifestBytes,
        InvalidHistory("最近采集记录的模板关联信息大小无效。", null));

    private static string ReadTemplateSnapshotJson(string path) => ReadBoundedUtf8Json(
        path,
        MaximumTemplateSnapshotBytes,
        new CaptureStorageException("CAPTURE_TEMPLATE_INVALID", "混合答题纸模板快照大小无效。"));

    private static string ReadBoundedUtf8Json(string path, int maximumBytes, CaptureStorageException sizeError)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            options: FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
        {
            throw sizeError;
        }

        var buffer = new byte[maximumBytes + 1];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        if (offset > maximumBytes || stream.ReadByte() != -1)
        {
            throw sizeError;
        }

        var hasUtf8Bom = offset >= 3
            && buffer[0] == 0xEF
            && buffer[1] == 0xBB
            && buffer[2] == 0xBF;
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(buffer, hasUtf8Bom ? 3 : 0, offset - (hasUtf8Bom ? 3 : 0));
    }

    private static string? FindNextCaptureDirectory(
        string capturesDirectory,
        string? afterId,
        CancellationToken cancellationToken,
        ref bool invalidDirectoryNameFound)
    {
        string? selectedId = null;
        foreach (var directory in Directory.EnumerateDirectories(capturesDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (name.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            if (!Guid.TryParseExact(name, "N", out var parsedId))
            {
                invalidDirectoryNameFound = true;
                continue;
            }

            var normalizedId = parsedId.ToString("N");
            if (!string.Equals(name, normalizedId, StringComparison.Ordinal))
            {
                invalidDirectoryNameFound = true;
                continue;
            }

            if (afterId is not null && string.CompareOrdinal(normalizedId, afterId) <= 0)
            {
                continue;
            }

            if (selectedId is null || string.CompareOrdinal(normalizedId, selectedId) < 0)
            {
                selectedId = normalizedId;
            }
        }

        return selectedId;
    }

    private static void ValidatePageSize(int pageSize)
    {
        if (pageSize is < 1 or > MaximumPageSize)
        {
            throw new CaptureValidationException("INVALID_PAGE_SIZE", "每页最多读取 50 条记录。");
        }
    }

    private static string? DecodeCursor(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }

        try
        {
            if (cursor.Length > 64 || cursor.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            {
                throw new FormatException();
            }

            var normalized = cursor.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
            var value = Encoding.ASCII.GetString(Convert.FromBase64String(normalized));
            if (!value.StartsWith("v1:", StringComparison.Ordinal)
                || !Guid.TryParseExact(value.AsSpan(3), "N", out var captureId)
                || captureId == Guid.Empty
                || !string.Equals(value.AsSpan(3).ToString(), captureId.ToString("N"), StringComparison.Ordinal))
            {
                throw new FormatException();
            }

            return captureId.ToString("N");
        }
        catch (FormatException)
        {
            throw new CaptureValidationException("INVALID_CAPTURE_CURSOR", "采集记录列表位置无效，请重新加载。");
        }
    }

    private static string EncodeCursor(string captureId)
    {
        return Convert.ToBase64String(Encoding.ASCII.GetBytes($"v1:{captureId}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static void RejectReparsePoint(string path, bool isDirectory)
    {
        var exists = isDirectory ? Directory.Exists(path) : File.Exists(path);
        if (exists && (File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0)
        {
            throw InvalidHistory("采集记录路径无效，无法安全访问。", null);
        }
    }

    private static void ValidateHistoryManifest(
        CaptureManifest manifest,
        DirectoryInfo captureDirectory,
        string? imagePath)
    {
        if (manifest.CreatedAtUtc == default)
        {
            throw InvalidHistory("最近采集记录缺少创建时间。", null);
        }

        if (!string.Equals(manifest.SourceType, "import", StringComparison.Ordinal)
            && !string.Equals(manifest.SourceType, "scan", StringComparison.Ordinal))
        {
            throw InvalidHistory("最近采集记录的来源类型无效。", null);
        }

        if (string.IsNullOrWhiteSpace(manifest.TemplateId)
            || string.IsNullOrWhiteSpace(manifest.TemplateTitle)
            || (manifest.TemplateSchemaVersion == 3
                ? manifest.QuestionCount < 0 || manifest.OptionsPerQuestion < 0 || manifest.OptionsPerQuestion > 10
                : manifest.QuestionCount <= 0
                    || manifest.OptionsPerQuestion is < AnswerSheetLayout.MinOptionsPerQuestion or > AnswerSheetLayout.MaxOptionsPerQuestion
                    || manifest.QuestionCount > AnswerSheetLayout.MaxQuestionCount(manifest.OptionsPerQuestion)))
        {
            throw InvalidHistory("最近采集记录的模板参数无效。", null);
        }

        if (string.IsNullOrWhiteSpace(manifest.ImageExtension)
            || (!string.Equals(manifest.ImageExtension, ".png", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(manifest.ImageExtension, ".jpg", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(manifest.ImageExtension, ".jpeg", StringComparison.OrdinalIgnoreCase)))
        {
            throw InvalidHistory("最近采集记录的图像扩展名无效。", null);
        }

        if (manifest.PixelWidth == 0
            || manifest.PixelHeight == 0
            || manifest.PixelWidth > MaximumImageWidth
            || manifest.PixelHeight > MaximumImageHeight
            || (ulong)manifest.PixelWidth * manifest.PixelHeight > MaximumPixelCount)
        {
            throw InvalidHistory("最近采集记录的图像尺寸无效。", null);
        }

        if (manifest.ByteLength == 0 || manifest.ByteLength > MaximumFileSizeBytes)
        {
            throw InvalidHistory("最近采集记录的图像大小无效。", null);
        }

        if (imagePath is null)
        {
            return;
        }

        var imageFile = new FileInfo(imagePath);
        if (imageFile.Length != checked((long)manifest.ByteLength))
        {
            throw InvalidHistory("最近采集记录的原图大小与保存信息不一致。", null);
        }

        var expectedFileName = $"original{manifest.ImageExtension.ToLowerInvariant()}";
        if (!string.Equals(imageFile.Name, expectedFileName, StringComparison.OrdinalIgnoreCase)
            || !PathsEqual(imageFile.DirectoryName ?? string.Empty, captureDirectory.FullName))
        {
            throw InvalidHistory("最近采集记录的原图文件名与记录目录不一致。", null);
        }
    }

    /// <summary>
    /// Imports a user-selected PNG/JPEG file, or saves a scan output, while associating it with a layout.
    /// </summary>
    public async Task<CaptureRecord> ImportAsync(
        AnswerSheetLayout layout,
        IInputImageFile file,
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
        IInputImageFile sourceFile,
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
        var templateSnapshotPath = Path.Combine(stagingDirectory, "template.json");
        var templateSnapshotTempPath = Path.Combine(stagingDirectory, ".template.json.tmp");
        var relativeImagePath = ToManifestPath(Path.Combine("captures", captureId, imageName));
        var committed = false;
        SchoolCandidateRecognitionResult? candidateIdentity = null;

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            await CopyOriginalAsync(sourceFile, image, imageFilePath, cancellationToken);
            if (template.SchemaVersion == 3)
            {
                var gray = await new SkiaImageDecoder().DecodeGrayscaleAsync(new LocalInputImageFile(imageFilePath), cancellationToken);
                var location = await Task.Run(() => new AnswerSheetRecognizer().LocatePage(template.Layout!, gray, cancellationToken), cancellationToken);
                if (!location.CanMapRegions || location.Transform is not { } transform
                    || !SchoolMachineCode.ValidateExpected(template.Layout!, gray, transform))
                    throw new CaptureValidationException("CAPTURE_SCHOOL_PAGE_MISMATCH", "无法确认考试、答题纸版本或页码，请检查所选答题纸及完整原图后重试。未保存该页面。");
                candidateIdentity = SchoolCandidateRecognition.Read(template.Layout!, gray, transform);
            }

            if (template.SchemaVersion is AnswerSheetLayout.MixedTemplateSchemaVersion or 3)
            {
                if (template.TemplateJson is null
                    || template.Layout is null
                    || template.Layout.SchemaVersion != template.SchemaVersion
                    || template.Layout.TemplateId != template.TemplateId
                    || (template.SchemaVersion == AnswerSheetLayout.MixedTemplateSchemaVersion && template.Layout.SubjectiveRegions.Count == 0))
                {
                    throw new CaptureValidationException(
                        "CAPTURE_TEMPLATE_INVALID",
                        "混合答题纸缺少经过验证的模板快照。");
                }

                await WriteTemplateSnapshotAsync(
                    template.TemplateJson,
                    templateSnapshotTempPath,
                    templateSnapshotPath,
                    cancellationToken);
            }
            else if (template.SchemaVersion != AnswerSheetLayout.TemplateSchemaVersion)
            {
                throw new CaptureValidationException(
                    "CAPTURE_TEMPLATE_UNSUPPORTED",
                    "答题纸模板版本不受支持。");
            }

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
                image.ByteLength)
            {
                SchoolMetadata = template.Layout?.SchoolMetadata,
                ImageSha256 = template.SchemaVersion == 3 ? ComputeImageHash(imageFilePath) : null,
                CandidateId = candidateIdentity is { RequiresReview: false } ? candidateIdentity.CandidateId : null,
                IdentityStatus = template.SchemaVersion == 3 ? (candidateIdentity is { RequiresReview: false, CandidateId: not null } ? "Identified" : "RequireAssociation") : null
            };

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
                Path.Combine(captureDirectory, "manifest.json"))
            {
                TemplateLayout = template.Layout
            };
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
        IInputImageFile sourceFile,
        CaptureImageInfo image,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        try
        {
            ulong copiedLength;
            await using (var input = await sourceFile.OpenReadAsync(cancellationToken))
            {
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
            var copiedFile = new LocalInputImageFile(destinationPath);
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

    private static async Task WriteTemplateSnapshotAsync(
        string templateJson,
        string temporaryPath,
        string finalPath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(templateJson) is <= 0 or > MaximumTemplateSnapshotBytes)
            {
                throw new CaptureValidationException(
                    "CAPTURE_TEMPLATE_INVALID",
                    "混合答题纸模板快照大小无效。");
            }

            await File.WriteAllTextAsync(temporaryPath, templateJson, new UTF8Encoding(false), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, finalPath);
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
                "CAPTURE_TEMPLATE_WRITE_FAILED",
                "混合答题纸模板快照保存失败。",
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

    private sealed class StoredCaptureInputImageFile : IInputImageFile
    {
        private readonly CaptureStore _store;
        private readonly string _captureId;
        private readonly ulong _expectedLength;

        public StoredCaptureInputImageFile(CaptureStore store, string captureId, string name, ulong length)
        {
            _store = store;
            _captureId = captureId;
            Name = name;
            _expectedLength = length;
        }

        public string Name { get; }

        public ulong Length => _expectedLength;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            return _store.OpenStoredImageAsync(_captureId, _expectedLength, cancellationToken);
        }
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

    private static readonly SkiaImageDecoder Decoder = new();

    public static async Task<CaptureImageInfo> ValidateAsync(
        IInputImageFile file,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(file.Name);
        if (!AllowedExtensions.Contains(extension))
        {
            throw new CaptureValidationException(
                "UNSUPPORTED_IMAGE_TYPE",
                "只支持 PNG 或 JPEG 图像。请重新选择文件。");
        }

        ulong byteLength;
        try
        {
            byteLength = file.Length;
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

        if (byteLength == 0)
        {
            throw new CaptureValidationException(
                "EMPTY_IMAGE",
                "所选图像为空，请重新选择 PNG 或 JPEG 文件。");
        }

        if (byteLength > CaptureStore.MaximumFileSizeBytes)
        {
            throw new CaptureValidationException(
                "IMAGE_FILE_TOO_LARGE",
                $"图像文件不能超过 {CaptureStore.MaximumFileSizeBytes / (1024 * 1024)} MB。");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decoded = await Decoder.DecodeAsync(file, cancellationToken);
            if (decoded.ByteLength != byteLength)
            {
                throw new CaptureValidationException(
                    "SOURCE_CHANGED",
                    "图像在读取过程中发生变化，请重新选择文件。");
            }

            var pixelWidth = decoded.PixelWidth;
            var pixelHeight = decoded.PixelHeight;
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

            return new CaptureImageInfo(
                decoded.Extension,
                pixelWidth,
                pixelHeight,
                byteLength);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CaptureException)
        {
            throw;
        }
        catch (ImageDecodeException exception) when (exception.Failure == ImageDecodeFailure.UnsupportedCodec)
        {
            throw new CaptureValidationException(
                "UNSUPPORTED_IMAGE_CODEC",
                "图像编码格式不是受支持的 PNG 或 JPEG，请重新选择文件。",
                exception);
        }
        catch (ImageDecodeException exception) when (exception.Failure == ImageDecodeFailure.DimensionsTooLarge)
        {
            throw new CaptureValidationException(
                "IMAGE_DIMENSIONS_TOO_LARGE",
                $"图像尺寸不能超过 {CaptureStore.MaximumImageWidth}×{CaptureStore.MaximumImageHeight}，且像素总数不能超过 {CaptureStore.MaximumPixelCount:N0}。",
                exception);
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
