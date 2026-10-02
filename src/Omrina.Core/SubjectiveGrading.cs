using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Omrina.Core;

public enum SubjectiveReviewStatus
{
    Unreviewed,
    Draft,
    Confirmed
}

public enum SubjectiveGradingAction
{
    SetDraft,
    Confirm,
    Reset
}

/// <summary>One requested grade-state change in an atomic review batch.</summary>
public sealed record SubjectiveGradeEdit(
    Guid QuestionId,
    SubjectiveReviewStatus Status,
    decimal? Score,
    string? Comment);

/// <summary>Grade fields retained in an immutable audit event.</summary>
public sealed record SubjectiveGradeState(
    SubjectiveReviewStatus Status,
    decimal? Score,
    string? Comment,
    string? Reviewer,
    DateTimeOffset? ConfirmedAtUtc);

/// <summary>One question and its reviewer-defined pixel region on the captured page.</summary>
public sealed record SubjectiveGradingQuestion(
    Guid QuestionId,
    int QuestionNumber,
    SubjectivePixelRectangle Region,
    decimal MaximumScore,
    SubjectiveReviewStatus Status,
    decimal? Score,
    string? Comment,
    string? Reviewer,
    DateTimeOffset? ConfirmedAtUtc);

/// <summary>One question's before/after states inside a batch.</summary>
public sealed record SubjectiveGradingChange(
    Guid QuestionId,
    int QuestionNumber,
    SubjectiveGradingAction Action,
    SubjectiveGradeState Before,
    SubjectiveGradeState After);

/// <summary>One atomic reviewer submission; all changes share this version, reviewer and time.</summary>
public sealed record SubjectiveGradingBatch(
    long Version,
    string Reviewer,
    DateTimeOffset TimestampUtc,
    IReadOnlyList<SubjectiveGradingChange> Changes);

/// <summary>
/// Immutable grading snapshot for the reviewer-defined regions on one captured page.
/// Batch edits are atomic, append-only, and protected by an expected version.
/// </summary>
public sealed class SubjectiveGradingSnapshot
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumQuestionCount = 64;
    public const int MaximumHistoryBatchCount = 128;
    public const int MaximumJsonBytes = 1_048_576;
    public const int MaximumReviewerLength = 64;
    public const int MaximumCommentLength = 256;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly IReadOnlyDictionary<Guid, SubjectiveGradingQuestion> questionsById;
    private readonly IReadOnlyDictionary<int, SubjectiveGradingQuestion> questionsByNumber;

    private SubjectiveGradingSnapshot(
        string captureId,
        int imageWidth,
        int imageHeight,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        long version,
        IReadOnlyList<SubjectiveGradingQuestion> questions,
        IReadOnlyList<SubjectiveGradingBatch> history)
    {
        CaptureId = captureId;
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        Version = version;
        Questions = questions;
        History = history;
        questionsById = new ReadOnlyDictionary<Guid, SubjectiveGradingQuestion>(
            questions.ToDictionary(question => question.QuestionId));
        questionsByNumber = new ReadOnlyDictionary<int, SubjectiveGradingQuestion>(
            questions.ToDictionary(question => question.QuestionNumber));
    }

    public int SchemaVersion => CurrentSchemaVersion;

    public string CaptureId { get; }

    public int ImageWidth { get; }

    public int ImageHeight { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>Starts at one and advances once per successful batch.</summary>
    public long Version { get; }

    public IReadOnlyList<SubjectiveGradingQuestion> Questions { get; }

    public IReadOnlyList<SubjectiveGradingBatch> History { get; }

    public bool IsFinal => Questions.Count > 0
        && Questions.All(question => question.Status == SubjectiveReviewStatus.Confirmed);

    /// <summary>Available only after every question is confirmed.</summary>
    public decimal? FinalSubtotal => IsFinal
        ? Questions.Sum(question => question.Score!.Value)
        : null;

    public static SubjectiveGradingSnapshot Start(
        string captureId,
        int imageWidth,
        int imageHeight,
        IEnumerable<SubjectiveRegionDefinition> regions,
        DateTimeOffset? createdAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var normalizedCaptureId = SubjectiveValueValidation.CaptureId(captureId, nameof(captureId));
        if (imageWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "图像宽度必须为正整数。");
        }

        if (imageHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageHeight), "图像高度必须为正整数。");
        }

        var regionArray = regions.ToArray();
        if (regionArray.Length is < 1 or > MaximumQuestionCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(regions),
                $"一页必须定义 1 到 {MaximumQuestionCount} 个主观题区域。");
        }

        var questionIds = new HashSet<Guid>();
        var questionNumbers = new HashSet<int>();
        foreach (var region in regionArray)
        {
            ArgumentNullException.ThrowIfNull(region);
            region.ValidateForImage(imageWidth, imageHeight);
            if (!questionIds.Add(region.QuestionId))
            {
                throw new ArgumentException("主观题区域 ID 不能重复。", nameof(regions));
            }

            if (!questionNumbers.Add(region.QuestionNumber))
            {
                throw new ArgumentException("主观题题号不能重复。", nameof(regions));
            }
        }

        var created = SubjectiveValueValidation.UtcTimestamp(createdAtUtc, nameof(createdAtUtc));
        var questions = regionArray
            .OrderBy(region => region.QuestionNumber)
            .Select(
                region => new SubjectiveGradingQuestion(
                    region.QuestionId,
                    region.QuestionNumber,
                    region.Rectangle,
                    region.MaximumPoints,
                    SubjectiveReviewStatus.Unreviewed,
                    Score: null,
                    Comment: null,
                    Reviewer: null,
                    ConfirmedAtUtc: null))
            .ToArray();
        return new SubjectiveGradingSnapshot(
            normalizedCaptureId,
            imageWidth,
            imageHeight,
            created,
            created,
            version: 1,
            ReadOnly(questions),
            Array.Empty<SubjectiveGradingBatch>());
    }

    public SubjectiveGradingQuestion GetQuestion(int questionNumber)
    {
        return questionsByNumber.TryGetValue(questionNumber, out var question)
            ? question
            : throw new ArgumentOutOfRangeException(nameof(questionNumber), questionNumber, "不存在该题号的主观题区域。");
    }

    /// <summary>
    /// Applies one atomic batch. A confirmed question must be explicitly changed
    /// to Draft before its score or comment can change; confirmation is a separate batch.
    /// </summary>
    public SubjectiveGradingSnapshot ApplyEdits(
        IEnumerable<SubjectiveGradeEdit> edits,
        string reviewer,
        long expectedVersion,
        DateTimeOffset? timestampUtc = null)
    {
        EnsureExpectedVersion(expectedVersion);
        if (History.Count >= MaximumHistoryBatchCount)
        {
            throw new InvalidOperationException($"审计历史最多保留 {MaximumHistoryBatchCount} 个批次。");
        }

        ArgumentNullException.ThrowIfNull(edits);
        var editArray = edits.ToArray();
        if (editArray.Length is < 1 or > MaximumQuestionCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(edits),
                $"每批必须包含 1 到 {MaximumQuestionCount} 项修改。");
        }

        var normalizedReviewer = SubjectiveValueValidation.RequiredText(
            reviewer,
            nameof(reviewer),
            MaximumReviewerLength);
        var timestamp = SubjectiveValueValidation.UtcTimestamp(timestampUtc, nameof(timestampUtc));
        if (timestamp < UpdatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampUtc), "操作时间不能早于上一版本。");
        }

        var seenIds = new HashSet<Guid>();
        var orderedEdits = new List<(SubjectiveGradingQuestion Current, SubjectiveGradeEdit Edit)>(editArray.Length);
        foreach (var edit in editArray)
        {
            ArgumentNullException.ThrowIfNull(edit);
            if (edit.QuestionId == Guid.Empty || !seenIds.Add(edit.QuestionId))
            {
                throw new ArgumentException("每批题目 ID 必须有效且不能重复。", nameof(edits));
            }

            if (!questionsById.TryGetValue(edit.QuestionId, out var current))
            {
                throw new ArgumentOutOfRangeException(nameof(edits), "批次引用了不属于此采集页的题目 ID。");
            }

            orderedEdits.Add((current, edit));
        }

        orderedEdits.Sort((first, second) => first.Current.QuestionNumber.CompareTo(second.Current.QuestionNumber));
        var changes = new List<SubjectiveGradingChange>(orderedEdits.Count);
        var nextQuestions = Questions.ToDictionary(question => question.QuestionId);
        foreach (var (current, edit) in orderedEdits)
        {
            var before = ToGradeState(current);
            var after = CreateEditedState(edit, current, normalizedReviewer, timestamp);
            var action = ValidateTransition(before, after, current.MaximumScore, normalizedReviewer, timestamp);
            if (before == after)
            {
                throw new ArgumentException("批次不能包含没有状态变化的题目。", nameof(edits));
            }

            changes.Add(new SubjectiveGradingChange(
                current.QuestionId,
                current.QuestionNumber,
                action,
                before,
                after));
            nextQuestions[current.QuestionId] = current with
            {
                Status = after.Status,
                Score = after.Score,
                Comment = after.Comment,
                Reviewer = after.Reviewer,
                ConfirmedAtUtc = after.ConfirmedAtUtc
            };
        }

        var nextVersion = checked(Version + 1);
        var batch = new SubjectiveGradingBatch(
            nextVersion,
            normalizedReviewer,
            timestamp,
            ReadOnly(changes));
        var history = History.Concat([batch]).ToArray();
        var next = new SubjectiveGradingSnapshot(
            CaptureId,
            ImageWidth,
            ImageHeight,
            CreatedAtUtc,
            timestamp,
            nextVersion,
            ReadOnly(nextQuestions.Values.OrderBy(question => question.QuestionNumber)),
            ReadOnly(history));
        _ = next.SerializeUtf8();
        return next;
    }

    public string ToJson(bool indented = false)
    {
        return Encoding.UTF8.GetString(SerializeUtf8(indented));
    }

    /// <summary>Rehydrates only if every batch replays to the exact stored question states.</summary>
    public static SubjectiveGradingSnapshot FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("主观评分 JSON 不能为空。", nameof(json));
        }

        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(json), "主观评分快照不能超过 1 MiB。");
        }

        SnapshotDocument document;
        try
        {
            document = JsonSerializer.Deserialize<SnapshotDocument>(json, SerializerOptions)
                ?? throw new JsonException("snapshot is null");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("主观评分 JSON 格式无效。", nameof(json), exception);
        }

        return Restore(document);
    }

    private static SubjectiveGradingSnapshot Restore(SnapshotDocument document)
    {
        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException("不支持该主观评分快照版本。", nameof(document));
        }

        if (document.CaptureId is null || document.Questions is null || document.History is null)
        {
            throw new ArgumentException("主观评分快照缺少必要字段。", nameof(document));
        }

        var captureId = SubjectiveValueValidation.CaptureId(document.CaptureId, nameof(document.CaptureId));
        if (!string.Equals(captureId, document.CaptureId, StringComparison.Ordinal))
        {
            throw new ArgumentException("快照采集页 ID 未规范化。", nameof(document));
        }

        if (document.ImageWidth <= 0 || document.ImageHeight <= 0)
        {
            throw new ArgumentException("快照图像尺寸无效。", nameof(document));
        }

        EnsureUtcTimestamp(document.CreatedAtUtc, nameof(document.CreatedAtUtc));
        EnsureUtcTimestamp(document.UpdatedAtUtc, nameof(document.UpdatedAtUtc));
        if (document.Questions.Count is < 1 or > MaximumQuestionCount)
        {
            throw new ArgumentException("快照主观题数量必须在 1 到 64 之间。", nameof(document));
        }

        if (document.History.Count > MaximumHistoryBatchCount)
        {
            throw new ArgumentException("快照审计批次数不能超过 128。", nameof(document));
        }

        if (document.Version < 1 || document.Version != (long)document.History.Count + 1)
        {
            throw new ArgumentException("快照版本与审计历史不匹配。", nameof(document));
        }

        var questions = new List<SubjectiveGradingQuestion>(document.Questions.Count);
        var questionsById = new Dictionary<Guid, SubjectiveGradingQuestion>();
        var questionsByNumber = new Dictionary<int, SubjectiveGradingQuestion>();
        foreach (var questionDocument in document.Questions)
        {
            if (questionDocument is null || questionDocument.Region is null)
            {
                throw new ArgumentException("快照题目区域缺少必要字段。", nameof(document));
            }

            var rectangle = SubjectivePixelRectangle.Create(
                questionDocument.Region.X,
                questionDocument.Region.Y,
                questionDocument.Region.Width,
                questionDocument.Region.Height,
                document.ImageWidth,
                document.ImageHeight);
            var region = SubjectiveRegionDefinition.Create(
                questionDocument.QuestionId,
                questionDocument.QuestionNumber,
                rectangle,
                questionDocument.MaximumScore,
                document.ImageWidth,
                document.ImageHeight);
            var question = new SubjectiveGradingQuestion(
                region.QuestionId,
                region.QuestionNumber,
                region.Rectangle,
                region.MaximumPoints,
                questionDocument.Status,
                questionDocument.Score,
                ValidateStoredOptionalText(questionDocument.Comment, MaximumCommentLength, nameof(document)),
                questionDocument.Reviewer,
                questionDocument.ConfirmedAtUtc);
            ValidateGradeState(ToGradeState(question), region.MaximumPoints, document.CreatedAtUtc);
            if (!questionsById.TryAdd(question.QuestionId, question)
                || !questionsByNumber.TryAdd(question.QuestionNumber, question))
            {
                throw new ArgumentException("快照包含重复的题目 ID 或题号。", nameof(document));
            }

            questions.Add(question);
        }

        questions.Sort((first, second) => first.QuestionNumber.CompareTo(second.QuestionNumber));
        var replayed = questions.ToDictionary(
            question => question.QuestionId,
            question => UnreviewedState());
        var replayedHistory = new List<SubjectiveGradingBatch>(document.History.Count);
        var previousTimestamp = document.CreatedAtUtc;
        for (var index = 0; index < document.History.Count; index++)
        {
            var batchDocument = document.History[index];
            if (batchDocument is null || batchDocument.Changes is null)
            {
                throw new ArgumentException("快照审计批次缺少必要字段。", nameof(document));
            }

            var expectedBatchVersion = (long)index + 2;
            if (batchDocument.Version != expectedBatchVersion)
            {
                throw new ArgumentException("快照审计版本必须连续。", nameof(document));
            }

            var reviewer = ValidateStoredText(batchDocument.Reviewer, MaximumReviewerLength, nameof(document));
            EnsureUtcTimestamp(batchDocument.TimestampUtc, nameof(document));
            if (batchDocument.TimestampUtc < previousTimestamp)
            {
                throw new ArgumentException("审计时间不能早于快照创建时间或前一批次。", nameof(document));
            }

            if (batchDocument.Changes.Count is < 1 or > MaximumQuestionCount)
            {
                throw new ArgumentException("每个审计批次必须有 1 到 64 项修改。", nameof(document));
            }

            var changedIds = new HashSet<Guid>();
            var changes = new List<SubjectiveGradingChange>(batchDocument.Changes.Count);
            foreach (var changeDocument in batchDocument.Changes)
            {
                if (changeDocument is null
                    || changeDocument.Before is null
                    || changeDocument.After is null
                    || !questionsById.TryGetValue(changeDocument.QuestionId, out var question)
                    || question.QuestionNumber != changeDocument.QuestionNumber
                    || !changedIds.Add(changeDocument.QuestionId))
                {
                    throw new ArgumentException("审计记录引用了未知或重复的题目。", nameof(document));
                }

                var before = FromDocument(changeDocument.Before);
                var after = FromDocument(changeDocument.After);
                ValidateGradeState(before, question.MaximumScore, document.CreatedAtUtc);
                ValidateGradeState(after, question.MaximumScore, document.CreatedAtUtc);
                if (before != replayed[question.QuestionId])
                {
                    throw new ArgumentException("审计记录原始状态与前序批次不一致。", nameof(document));
                }

                var action = changeDocument.Action;
                ValidateTransition(before, after, question.MaximumScore, reviewer, batchDocument.TimestampUtc, action);
                replayed[question.QuestionId] = after;
                changes.Add(new SubjectiveGradingChange(
                    question.QuestionId,
                    question.QuestionNumber,
                    action,
                    before,
                    after));
            }

            changes.Sort((first, second) => first.QuestionNumber.CompareTo(second.QuestionNumber));
            replayedHistory.Add(new SubjectiveGradingBatch(
                expectedBatchVersion,
                reviewer,
                batchDocument.TimestampUtc,
                ReadOnly(changes)));
            previousTimestamp = batchDocument.TimestampUtc;
        }

        foreach (var question in questions)
        {
            if (ToGradeState(question) != replayed[question.QuestionId])
            {
                throw new ArgumentException("快照当前题目状态与审计历史不一致。", nameof(document));
            }
        }

        if (document.UpdatedAtUtc != previousTimestamp)
        {
            throw new ArgumentException("快照更新时间与最后一个批次不一致。", nameof(document));
        }

        var restored = new SubjectiveGradingSnapshot(
            captureId,
            document.ImageWidth,
            document.ImageHeight,
            document.CreatedAtUtc,
            document.UpdatedAtUtc,
            document.Version,
            ReadOnly(questions),
            ReadOnly(replayedHistory));
        if (document.IsFinal != restored.IsFinal || document.FinalSubtotal != restored.FinalSubtotal)
        {
            throw new ArgumentException("快照最终状态或总分与题目状态不一致。", nameof(document));
        }

        _ = restored.SerializeUtf8();
        return restored;
    }

    private byte[] SerializeUtf8(bool indented = false)
    {
        var document = new SnapshotDocument
        {
            SchemaVersion = SchemaVersion,
            CaptureId = CaptureId,
            ImageWidth = ImageWidth,
            ImageHeight = ImageHeight,
            Version = Version,
            CreatedAtUtc = CreatedAtUtc,
            UpdatedAtUtc = UpdatedAtUtc,
            Questions = Questions.Select(ToDocument).ToList(),
            History = History.Select(ToDocument).ToList(),
            IsFinal = IsFinal,
            FinalSubtotal = FinalSubtotal
        };
        var options = new JsonSerializerOptions(SerializerOptions) { WriteIndented = indented };
        var json = JsonSerializer.SerializeToUtf8Bytes(document, options);
        if (json.Length > MaximumJsonBytes)
        {
            throw new InvalidOperationException("主观评分快照 JSON 不能超过 1 MiB。");
        }

        return json;
    }

    private void EnsureExpectedVersion(long expectedVersion)
    {
        if (expectedVersion != Version)
        {
            throw new InvalidOperationException(
                $"主观评分版本冲突：expectedVersion={expectedVersion}，currentVersion={Version}。");
        }
    }

    private static SubjectiveGradeState CreateEditedState(
        SubjectiveGradeEdit edit,
        SubjectiveGradingQuestion current,
        string reviewer,
        DateTimeOffset timestamp)
    {
        if (!Enum.IsDefined(edit.Status))
        {
            throw new ArgumentOutOfRangeException(nameof(edit), "主观题批阅状态无效。");
        }

        if (edit.Status == SubjectiveReviewStatus.Unreviewed)
        {
            if (edit.Score is not null || !string.IsNullOrWhiteSpace(edit.Comment))
            {
                throw new ArgumentException("未批阅状态不能包含分值或评语。", nameof(edit));
            }

            return UnreviewedState();
        }

        if (edit.Score is not decimal score)
        {
            throw new ArgumentException("草稿或已确认状态必须包含分值。", nameof(edit));
        }

        ValidateScore(score, current.MaximumScore, nameof(edit));
        var comment = SubjectiveValueValidation.OptionalText(
            edit.Comment,
            nameof(edit),
            MaximumCommentLength);
        DateTimeOffset? confirmationTime = edit.Status == SubjectiveReviewStatus.Confirmed
            ? timestamp
            : null;
        return new SubjectiveGradeState(edit.Status, score, comment, reviewer, confirmationTime);
    }

    private static SubjectiveGradingAction ValidateTransition(
        SubjectiveGradeState before,
        SubjectiveGradeState after,
        decimal maximumScore,
        string reviewer,
        DateTimeOffset timestamp,
        SubjectiveGradingAction? expectedAction = null)
    {
        ValidateGradeState(before, maximumScore, DateTimeOffset.MinValue + TimeSpan.FromTicks(1));
        ValidateGradeState(after, maximumScore, DateTimeOffset.MinValue + TimeSpan.FromTicks(1));
        if (before == after)
        {
            throw new ArgumentException("审计批次不能包含没有状态变化的题目。", nameof(after));
        }

        SubjectiveGradingAction action;
        switch (after.Status)
        {
            case SubjectiveReviewStatus.Unreviewed:
                if (before.Status == SubjectiveReviewStatus.Unreviewed
                    || after.Score is not null
                    || after.Comment is not null
                    || after.Reviewer is not null
                    || after.ConfirmedAtUtc is not null)
                {
                    throw new ArgumentException("重设操作的前后状态不合法。", nameof(after));
                }

                action = SubjectiveGradingAction.Reset;
                break;
            case SubjectiveReviewStatus.Draft:
                if (after.Score is null
                    || after.Reviewer != reviewer
                    || after.ConfirmedAtUtc is not null)
                {
                    throw new ArgumentException("草稿状态必须含有效分值、批阅人且不能有确认时间。", nameof(after));
                }

                action = SubjectiveGradingAction.SetDraft;
                break;
            case SubjectiveReviewStatus.Confirmed:
                if (before.Status != SubjectiveReviewStatus.Draft
                    || before.Score != after.Score
                    || before.Comment != after.Comment
                    || after.Reviewer != reviewer
                    || after.ConfirmedAtUtc != timestamp)
                {
                    throw new ArgumentException("确认只允许将未更改的草稿转为已确认状态。", nameof(after));
                }

                action = SubjectiveGradingAction.Confirm;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(after));
        }

        if (expectedAction is not null && expectedAction.Value != action)
        {
            throw new ArgumentException("审计操作与状态转换不匹配。", nameof(expectedAction));
        }

        return action;
    }

    private static void ValidateGradeState(
        SubjectiveGradeState state,
        decimal maximumScore,
        DateTimeOffset createdAtUtc)
    {
        if (!Enum.IsDefined(state.Status))
        {
            throw new ArgumentException("题目批阅状态无效。", nameof(state));
        }

        if (state.Score is decimal score)
        {
            ValidateScore(score, maximumScore, nameof(state));
        }

        var comment = ValidateStoredOptionalText(state.Comment, MaximumCommentLength, nameof(state));
        if (comment != state.Comment)
        {
            throw new ArgumentException("快照评语未规范化。", nameof(state));
        }

        switch (state.Status)
        {
            case SubjectiveReviewStatus.Unreviewed:
                if (state.Score is not null
                    || state.Comment is not null
                    || state.Reviewer is not null
                    || state.ConfirmedAtUtc is not null)
                {
                    throw new ArgumentException("未批阅状态不能包含分值或批阅记录。", nameof(state));
                }

                break;
            case SubjectiveReviewStatus.Draft:
                if (state.Score is null || state.Reviewer is null || state.ConfirmedAtUtc is not null)
                {
                    throw new ArgumentException("草稿必须包含分值和批阅人，且不能有确认时间。", nameof(state));
                }

                _ = ValidateStoredText(state.Reviewer, MaximumReviewerLength, nameof(state));
                break;
            case SubjectiveReviewStatus.Confirmed:
                if (state.Score is null || state.Reviewer is null || state.ConfirmedAtUtc is null)
                {
                    throw new ArgumentException("已确认状态必须包含分值、批阅人和确认时间。", nameof(state));
                }

                _ = ValidateStoredText(state.Reviewer, MaximumReviewerLength, nameof(state));
                EnsureUtcTimestamp(state.ConfirmedAtUtc.Value, nameof(state));
                if (state.ConfirmedAtUtc.Value < createdAtUtc)
                {
                    throw new ArgumentException("确认时间不能早于快照创建时间。", nameof(state));
                }

                break;
        }
    }

    private static void ValidateScore(decimal score, decimal maximumScore, string parameterName)
    {
        if (maximumScore <= 0 || maximumScore > SubjectiveRegionDefinition.MaximumPointsLimit)
        {
            throw new ArgumentOutOfRangeException(parameterName, "题目满分超出允许范围。");
        }

        if (score < 0 || score > maximumScore)
        {
            throw new ArgumentOutOfRangeException(parameterName, "得分必须在零分与该题满分之间。");
        }
    }

    private static SubjectiveGradeState UnreviewedState()
    {
        return new SubjectiveGradeState(
            SubjectiveReviewStatus.Unreviewed,
            Score: null,
            Comment: null,
            Reviewer: null,
            ConfirmedAtUtc: null);
    }

    private static SubjectiveGradeState ToGradeState(SubjectiveGradingQuestion question)
    {
        return new SubjectiveGradeState(
            question.Status,
            question.Score,
            question.Comment,
            question.Reviewer,
            question.ConfirmedAtUtc);
    }

    private static SubjectiveGradeState FromDocument(GradeStateDocument document)
    {
        return new SubjectiveGradeState(
            document.Status,
            document.Score,
            ValidateStoredOptionalText(document.Comment, MaximumCommentLength, nameof(document)),
            document.Reviewer,
            document.ConfirmedAtUtc);
    }

    private static string? ValidateStoredOptionalText(string? value, int maximumLength, string parameterName)
    {
        var normalized = SubjectiveValueValidation.OptionalText(value, parameterName, maximumLength);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            throw new ArgumentException("快照评语未规范化。", parameterName);
        }

        return normalized;
    }

    private static string ValidateStoredText(string? value, int maximumLength, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentException("快照中的必填文本不能为空。", parameterName);
        }

        var normalized = SubjectiveValueValidation.RequiredText(value, parameterName, maximumLength);
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            throw new ArgumentException("快照中的必填文本未规范化。", parameterName);
        }

        return normalized;
    }

    private static void EnsureUtcTimestamp(DateTimeOffset timestamp, string parameterName)
    {
        if (timestamp == DateTimeOffset.MinValue || timestamp.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("快照时间戳必须是有效的 UTC 时间。", parameterName);
        }
    }

    private static ReadOnlyCollection<T> ReadOnly<T>(IEnumerable<T> items)
    {
        return new ReadOnlyCollection<T>(items.ToArray());
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 32
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static QuestionDocument ToDocument(SubjectiveGradingQuestion question)
    {
        return new QuestionDocument
        {
            QuestionId = question.QuestionId,
            QuestionNumber = question.QuestionNumber,
            MaximumScore = question.MaximumScore,
            Region = new RectangleDocument
            {
                X = question.Region.X,
                Y = question.Region.Y,
                Width = question.Region.Width,
                Height = question.Region.Height
            },
            Status = question.Status,
            Score = question.Score,
            Comment = question.Comment,
            Reviewer = question.Reviewer,
            ConfirmedAtUtc = question.ConfirmedAtUtc
        };
    }

    private static GradeStateDocument ToDocument(SubjectiveGradeState state)
    {
        return new GradeStateDocument
        {
            Status = state.Status,
            Score = state.Score,
            Comment = state.Comment,
            Reviewer = state.Reviewer,
            ConfirmedAtUtc = state.ConfirmedAtUtc
        };
    }

    private static BatchDocument ToDocument(SubjectiveGradingBatch batch)
    {
        return new BatchDocument
        {
            Version = batch.Version,
            Reviewer = batch.Reviewer,
            TimestampUtc = batch.TimestampUtc,
            Changes = batch.Changes.Select(ToDocument).ToList()
        };
    }

    private static ChangeDocument ToDocument(SubjectiveGradingChange change)
    {
        return new ChangeDocument
        {
            QuestionId = change.QuestionId,
            QuestionNumber = change.QuestionNumber,
            Action = change.Action,
            Before = ToDocument(change.Before),
            After = ToDocument(change.After)
        };
    }

    private sealed class SnapshotDocument
    {
        public SnapshotDocument()
        {
        }

        public required int SchemaVersion { get; init; }

        public required string? CaptureId { get; init; }

        public required int ImageWidth { get; init; }

        public required int ImageHeight { get; init; }

        public required long Version { get; init; }

        public required DateTimeOffset CreatedAtUtc { get; init; }

        public required DateTimeOffset UpdatedAtUtc { get; init; }

        public required List<QuestionDocument>? Questions { get; init; }

        public required List<BatchDocument>? History { get; init; }

        public required bool IsFinal { get; init; }

        public required decimal? FinalSubtotal { get; init; }
    }

    private sealed class QuestionDocument
    {
        public QuestionDocument()
        {
        }

        public required Guid QuestionId { get; init; }

        public required int QuestionNumber { get; init; }

        public required decimal MaximumScore { get; init; }

        public required RectangleDocument? Region { get; init; }

        public required SubjectiveReviewStatus Status { get; init; }

        public required decimal? Score { get; init; }

        public required string? Comment { get; init; }

        public required string? Reviewer { get; init; }

        public required DateTimeOffset? ConfirmedAtUtc { get; init; }
    }

    private sealed class RectangleDocument
    {
        public RectangleDocument()
        {
        }

        public required int X { get; init; }

        public required int Y { get; init; }

        public required int Width { get; init; }

        public required int Height { get; init; }
    }

    private sealed class GradeStateDocument
    {
        public GradeStateDocument()
        {
        }

        public required SubjectiveReviewStatus Status { get; init; }

        public required decimal? Score { get; init; }

        public required string? Comment { get; init; }

        public required string? Reviewer { get; init; }

        public required DateTimeOffset? ConfirmedAtUtc { get; init; }
    }

    private sealed class BatchDocument
    {
        public BatchDocument()
        {
        }

        public required long Version { get; init; }

        public required string? Reviewer { get; init; }

        public required DateTimeOffset TimestampUtc { get; init; }

        public required List<ChangeDocument>? Changes { get; init; }
    }

    private sealed class ChangeDocument
    {
        public ChangeDocument()
        {
        }

        public required Guid QuestionId { get; init; }

        public required int QuestionNumber { get; init; }

        public required SubjectiveGradingAction Action { get; init; }

        public required GradeStateDocument? Before { get; init; }

        public required GradeStateDocument? After { get; init; }
    }
}
