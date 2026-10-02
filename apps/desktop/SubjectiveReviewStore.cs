using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Omrina.Core;

namespace Omrina.Desktop;

/// <summary>A persisted subjective review. OwnerGrantId is local metadata, never a token.</summary>
public sealed record SubjectiveReviewRecord(
    Guid ReviewId,
    string OwnerGrantId,
    SubjectiveGradingSnapshot Snapshot);

public sealed class SubjectiveReviewStoreException : Exception
{
    public SubjectiveReviewStoreException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Atomically stores validated immutable subjective snapshots below the app-owned capture root.
/// The trusted local entry point can load by a known review ID; grant reads are owner-checked.
/// </summary>
public sealed class SubjectiveReviewStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumStoredDocumentBytes = 384 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _directory;

    public SubjectiveReviewStore(string captureRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(captureRootDirectory))
        {
            throw new ArgumentException("Subjective review storage root is required.", nameof(captureRootDirectory));
        }

        _directory = Path.GetFullPath(Path.Combine(captureRootDirectory, "subjective-reviews"));
    }

    /// <summary>Loads a known record for a trusted local caller, without exposing a listing API.</summary>
    public SubjectiveReviewRecord? LoadTrusted(Guid reviewId)
    {
        return Load(reviewId);
    }

    /// <summary>Loads a record only when it belongs to the current grant.</summary>
    public SubjectiveReviewRecord? LoadForGrant(Guid reviewId, string grantId)
    {
        ValidateGrantId(grantId);
        var record = Load(reviewId);
        return record is not null && string.Equals(record.OwnerGrantId, grantId, StringComparison.Ordinal)
            ? record
            : null;
    }

    public async Task SaveAsync(
        Guid reviewId,
        string ownerGrantId,
        SubjectiveGradingSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ValidateReviewId(reviewId);
        ValidateGrantId(ownerGrantId);
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        var document = new StoredDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            ReviewId = reviewId,
            OwnerGrantId = ownerGrantId,
            SnapshotJson = snapshot.ToJson()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > MaximumStoredDocumentBytes)
        {
            throw TooLarge();
        }

        var temporaryPath = string.Empty;
        var committed = false;
        try
        {
            EnsureDirectory(create: true);
            var destinationPath = GetDocumentPath(reviewId);
            RejectReparsePoint(destinationPath);
            temporaryPath = Path.Combine(_directory, $".{reviewId:N}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite: true);
            committed = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SubjectiveReviewStoreException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new SubjectiveReviewStoreException(
                "SUBJECTIVE_REVIEW_STORE_FAILED",
                "批阅记录无法保存，请检查应用本地存储后重试。",
                exception);
        }
        finally
        {
            if (!committed && temporaryPath.Length > 0)
            {
                DeleteTemporaryFile(temporaryPath);
            }
        }
    }

    private SubjectiveReviewRecord? Load(Guid reviewId)
    {
        ValidateReviewId(reviewId);
        try
        {
            EnsureDirectory(create: false);
            if (!Directory.Exists(_directory))
            {
                return null;
            }

            var path = GetDocumentPath(reviewId);
            if (!File.Exists(path))
            {
                return null;
            }

            RejectReparsePoint(path);
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length <= 0 || fileInfo.Length > MaximumStoredDocumentBytes)
            {
                throw TooLarge();
            }

            var bytes = ReadDocumentBytes(path);
            if (bytes.Length <= 0 || bytes.Length > MaximumStoredDocumentBytes)
            {
                throw TooLarge();
            }

            var json = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            var document = JsonSerializer.Deserialize<StoredDocument>(json, JsonOptions)
                ?? throw InvalidDocument();
            if (document.SchemaVersion != CurrentSchemaVersion
                || document.ReviewId != reviewId
                || document.SnapshotJson is null)
            {
                throw InvalidDocument();
            }

            ValidateGrantId(document.OwnerGrantId);
            var snapshot = SubjectiveGradingSnapshot.FromJson(document.SnapshotJson);
            return new SubjectiveReviewRecord(reviewId, document.OwnerGrantId, snapshot);
        }
        catch (SubjectiveReviewStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException
            or DecoderFallbackException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException)
        {
            throw new SubjectiveReviewStoreException(
                "SUBJECTIVE_REVIEW_INVALID",
                "批阅记录已损坏或不受支持，无法读取。",
                exception);
        }
    }

    private void EnsureDirectory(bool create)
    {
        if (create)
        {
            Directory.CreateDirectory(_directory);
        }

        if (Directory.Exists(_directory)
            && (File.GetAttributes(_directory) & System.IO.FileAttributes.ReparsePoint) != 0)
        {
            throw new SubjectiveReviewStoreException(
                "SUBJECTIVE_REVIEW_STORE_FAILED",
                "批阅记录存储位置无效，无法继续访问。");
        }
    }

    private string GetDocumentPath(Guid reviewId)
    {
        return Path.Combine(_directory, $"{reviewId:N}.json");
    }

    private static byte[] ReadDocumentBytes(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 16 * 1024,
            options: FileOptions.SequentialScan);
        var bytes = new byte[MaximumStoredDocumentBytes + 1];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        if (offset > MaximumStoredDocumentBytes || (offset == MaximumStoredDocumentBytes && stream.ReadByte() != -1))
        {
            throw TooLarge();
        }

        Array.Resize(ref bytes, offset);
        return bytes;
    }

    private static void RejectReparsePoint(string path)
    {
        if (File.Exists(path)
            && (File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0)
        {
            throw new SubjectiveReviewStoreException(
                "SUBJECTIVE_REVIEW_INVALID",
                "批阅记录位置无效，无法安全访问。");
        }
    }

    private static void ValidateReviewId(Guid reviewId)
    {
        if (reviewId == Guid.Empty)
        {
            throw new ArgumentException("ReviewId must be a non-empty GUID.", nameof(reviewId));
        }
    }

    private static void ValidateGrantId(string grantId)
    {
        if (string.IsNullOrWhiteSpace(grantId)
            || grantId.Length > 128
            || !grantId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new ArgumentException("GrantId must be a short opaque identifier.", nameof(grantId));
        }
    }

    private static SubjectiveReviewStoreException TooLarge()
    {
        return new SubjectiveReviewStoreException(
            "SUBJECTIVE_REVIEW_TOO_LARGE",
            "批阅记录过大，请减少区域或历史内容后重试。");
    }

    private static SubjectiveReviewStoreException InvalidDocument()
    {
        return new SubjectiveReviewStoreException(
            "SUBJECTIVE_REVIEW_INVALID",
            "批阅记录已损坏或不受支持，无法读取。");
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Subjective review staging cleanup failed: {exception.GetType().Name}.");
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 32
        };
    }

    private sealed class StoredDocument
    {
        public int SchemaVersion { get; init; }

        public Guid ReviewId { get; init; }

        public string OwnerGrantId { get; init; } = string.Empty;

        public string SnapshotJson { get; init; } = string.Empty;
    }
}
