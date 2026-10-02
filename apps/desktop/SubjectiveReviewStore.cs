using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
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

public sealed record SubjectiveReviewStoreDiagnostic(string ResourceId, string Code, string Message);

public sealed record SubjectiveReviewPage(
    IReadOnlyList<SubjectiveReviewRecord> Items,
    IReadOnlyList<SubjectiveReviewStoreDiagnostic> Diagnostics,
    string? NextCursor);

/// <summary>
/// Atomically stores validated immutable subjective snapshots below the app-owned capture root.
/// The trusted local entry point can load by a known review ID; grant reads are owner-checked.
/// </summary>
public sealed class SubjectiveReviewStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumStoredDocumentBytes = 384 * 1024;
    private const int MaximumPageSize = 50;
    public const string DesktopLocalOwner = "desktop-local";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly StringComparer RootPathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RootTransactions = new(RootPathComparer);
    private readonly string _directory;
    private readonly SemaphoreSlim _rootTransaction;

    public SubjectiveReviewStore(string captureRootDirectory)
    {
        if (string.IsNullOrWhiteSpace(captureRootDirectory))
        {
            throw new ArgumentException("Subjective review storage root is required.", nameof(captureRootDirectory));
        }

        _directory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(captureRootDirectory, "subjective-reviews")));
        _rootTransaction = RootTransactions.GetOrAdd(_directory, static _ => new SemaphoreSlim(1, 1));
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

    /// <summary>Creates a record without allowing an existing ID to be overwritten.</summary>
    public async Task<SubjectiveReviewRecord> CreateAsync(
        Guid reviewId,
        string ownerGrantId,
        SubjectiveGradingSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ValidateReviewId(reviewId);
        ValidateGrantId(ownerGrantId);
        ArgumentNullException.ThrowIfNull(snapshot);
        await _rootTransaction.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Load(reviewId) is not null)
            {
                throw new SubjectiveReviewStoreException(
                    "SUBJECTIVE_REVIEW_ID_CONFLICT",
                    "批阅记录 ID 已存在，请重试。");
            }

            await SaveCoreAsync(reviewId, ownerGrantId, snapshot, overwrite: false, cancellationToken)
                .ConfigureAwait(false);
            return new SubjectiveReviewRecord(reviewId, ownerGrantId, snapshot);
        }
        finally
        {
            _rootTransaction.Release();
        }
    }

    /// <summary>
    /// Applies one expected-version transition for the owning grant. Load, owner/version checks,
    /// immutable-identity checks, and atomic replacement share a process-wide gate per root.
    /// </summary>
    public Task<SubjectiveReviewRecord> ApplyForGrantAsync(
        Guid reviewId,
        string grantId,
        long expectedVersion,
        Func<SubjectiveGradingSnapshot, SubjectiveGradingSnapshot> apply,
        CancellationToken cancellationToken = default)
    {
        ValidateGrantId(grantId);
        return ApplyCoreAsync(reviewId, grantId, expectedVersion, apply, cancellationToken);
    }

    /// <summary>
    /// Applies a trusted local transition while preserving the stored owner metadata.
    /// </summary>
    public Task<SubjectiveReviewRecord> ApplyTrustedAsync(
        Guid reviewId,
        long expectedVersion,
        Func<SubjectiveGradingSnapshot, SubjectiveGradingSnapshot> apply,
        CancellationToken cancellationToken = default)
    {
        return ApplyCoreAsync(reviewId, expectedOwnerGrantId: null, expectedVersion, apply, cancellationToken);
    }

    public SubjectiveReviewPage ListTrustedPage(
        int pageSize = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePageSize(pageSize);
        var afterId = DecodeCursor(cursor);
        var items = new List<SubjectiveReviewRecord>(pageSize);
        var diagnostics = new List<SubjectiveReviewStoreDiagnostic>();
        var lastConsumedId = afterId;
        var hasMoreRecords = false;
        var invalidFileNameFound = false;
        var consumedCount = 0;

        try
        {
            EnsureDirectory(create: false);
            if (!Directory.Exists(_directory))
            {
                return new SubjectiveReviewPage(items, diagnostics, NextCursor: null);
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nextPath = FindNextReviewPath(lastConsumedId, cancellationToken, ref invalidFileNameFound);
                if (nextPath is null)
                {
                    break;
                }

                var stem = Path.GetFileNameWithoutExtension(nextPath);
                _ = Guid.TryParseExact(stem, "N", out var reviewId);
                var normalizedId = reviewId.ToString("N");
                consumedCount++;
                SubjectiveReviewRecord? record;
                try
                {
                    record = Load(reviewId);
                }
                catch (SubjectiveReviewStoreException exception)
                {
                    diagnostics.Add(new SubjectiveReviewStoreDiagnostic(normalizedId, exception.Code, exception.Message));
                    record = null;
                }

                lastConsumedId = normalizedId;
                if (record is not null)
                {
                    items.Add(record);
                }

                if (consumedCount == pageSize)
                {
                    hasMoreRecords = FindNextReviewPath(lastConsumedId, cancellationToken, ref invalidFileNameFound)
                        is not null;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SubjectiveReviewStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SubjectiveReviewStoreException(
                "SUBJECTIVE_REVIEW_STORE_FAILED",
                "无法读取本地批阅记录列表。",
                exception);
        }

        if (cursor is null && invalidFileNameFound)
        {
            diagnostics.Add(new SubjectiveReviewStoreDiagnostic(
                "invalid-record-filenames",
                "SUBJECTIVE_REVIEW_INVALID",
                "检测到无法识别的批阅记录文件名；这些文件未被打开。"));
        }

        var nextCursor = hasMoreRecords ? EncodeCursor(lastConsumedId!) : null;
        return new SubjectiveReviewPage(items, diagnostics, nextCursor);
    }

    private async Task<SubjectiveReviewRecord> ApplyCoreAsync(
        Guid reviewId,
        string? expectedOwnerGrantId,
        long expectedVersion,
        Func<SubjectiveGradingSnapshot, SubjectiveGradingSnapshot> apply,
        CancellationToken cancellationToken)
    {
        ValidateReviewId(reviewId);
        ArgumentNullException.ThrowIfNull(apply);
        if (expectedVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        await _rootTransaction.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = Load(reviewId)
                ?? throw new SubjectiveReviewStoreException(
                    "SUBJECTIVE_REVIEW_NOT_FOUND",
                    "找不到此批阅记录。");
            if (expectedOwnerGrantId is not null
                && !string.Equals(current.OwnerGrantId, expectedOwnerGrantId, StringComparison.Ordinal))
            {
                throw new SubjectiveReviewStoreException(
                    "SUBJECTIVE_REVIEW_NOT_FOUND",
                    "找不到此授权下的批阅记录。");
            }

            if (current.Snapshot.Version != expectedVersion)
            {
                throw new SubjectiveReviewStoreException(
                    "VERSION_CONFLICT",
                    "批阅记录已更新，请先重新读取后再提交。");
            }

            var nextSnapshot = apply(current.Snapshot)
                ?? throw new InvalidOperationException("A subjective update must return a snapshot.");
            ValidateTransition(current, nextSnapshot, expectedVersion);
            cancellationToken.ThrowIfCancellationRequested();
            await SaveCoreAsync(reviewId, current.OwnerGrantId, nextSnapshot, overwrite: true, cancellationToken)
                .ConfigureAwait(false);
            return current with { Snapshot = nextSnapshot };
        }
        finally
        {
            _rootTransaction.Release();
        }
    }

    private async Task SaveCoreAsync(
        Guid reviewId,
        string ownerGrantId,
        SubjectiveGradingSnapshot snapshot,
        bool overwrite,
        CancellationToken cancellationToken)
    {
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
            File.Move(temporaryPath, destinationPath, overwrite);
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

    private string? FindNextReviewPath(
        string? afterId,
        CancellationToken cancellationToken,
        ref bool invalidFileNameFound)
    {
        string? selectedPath = null;
        string? selectedId = null;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stem = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(stem, "N", out var parsed))
            {
                invalidFileNameFound = true;
                continue;
            }

            var normalizedId = parsed.ToString("N");
            if (!string.Equals(stem, normalizedId, StringComparison.Ordinal))
            {
                invalidFileNameFound = true;
                continue;
            }

            if (afterId is not null && string.CompareOrdinal(normalizedId, afterId) <= 0)
            {
                continue;
            }

            if (selectedId is null || string.CompareOrdinal(normalizedId, selectedId) < 0)
            {
                selectedId = normalizedId;
                selectedPath = path;
            }
        }

        return selectedPath;
    }

    private static void ValidatePageSize(int pageSize)
    {
        if (pageSize is < 1 or > MaximumPageSize)
        {
            throw new SubjectiveReviewStoreException(
                "INVALID_PAGE_SIZE",
                "每页最多读取 50 条记录。");
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
                || !Guid.TryParseExact(value.AsSpan(3), "N", out var reviewId)
                || reviewId == Guid.Empty
                || !string.Equals(value.AsSpan(3).ToString(), reviewId.ToString("N"), StringComparison.Ordinal))
            {
                throw new FormatException();
            }

            return reviewId.ToString("N");
        }
        catch (FormatException)
        {
            throw new SubjectiveReviewStoreException("INVALID_REVIEW_CURSOR", "批阅记录列表位置无效，请重新加载。");
        }
    }

    private static string EncodeCursor(string reviewId)
    {
        return Convert.ToBase64String(Encoding.ASCII.GetBytes($"v1:{reviewId}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static void ValidateTransition(
        SubjectiveReviewRecord current,
        SubjectiveGradingSnapshot next,
        long expectedVersion)
    {
        var previous = current.Snapshot;
        if (next.Version != checked(expectedVersion + 1)
            || next.History.Count != previous.History.Count + 1
            || !previous.History.SequenceEqual(next.History.Take(previous.History.Count))
            || !string.Equals(previous.CaptureId, next.CaptureId, StringComparison.Ordinal)
            || previous.ImageWidth != next.ImageWidth
            || previous.ImageHeight != next.ImageHeight
            || previous.CreatedAtUtc != next.CreatedAtUtc
            || previous.Questions.Count != next.Questions.Count)
        {
            throw new SubjectiveReviewStoreException(
                "INVALID_SUBJECTIVE_TRANSITION",
                "批阅更新不能更改采集关联、题目区域或历史记录。");
        }

        var nextQuestions = next.Questions.ToDictionary(question => question.QuestionId);
        foreach (var question in previous.Questions)
        {
            if (!nextQuestions.TryGetValue(question.QuestionId, out var updated)
                || updated.QuestionNumber != question.QuestionNumber
                || updated.Region != question.Region
                || updated.MaximumScore != question.MaximumScore)
            {
                throw new SubjectiveReviewStoreException(
                    "INVALID_SUBJECTIVE_TRANSITION",
                    "批阅更新不能更改采集关联、题目区域或历史记录。");
            }
        }
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
