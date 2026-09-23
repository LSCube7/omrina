using System.Collections.ObjectModel;

namespace Omrina.Core;

/// <summary>The kind of immutable audit entry created by a manual review action.</summary>
public enum ManualReviewAction
{
    SetAnswer,
    ConfirmBlank,
    ResetToRecognition
}

/// <summary>
/// One question's current interpretation in a manual review snapshot.
/// <see cref="IsBlankConfirmed"/> deliberately differs from an unreviewed
/// blank recognition, so a reviewer action cannot be mistaken for an engine result.
/// </summary>
public sealed record ReviewedQuestionAnswer(
    int QuestionNumber,
    QuestionMarkState State,
    string? AnswerLabel,
    bool IsManuallyReviewed,
    bool IsBlankConfirmed)
{
    public bool IsBlank => State == QuestionMarkState.Blank;

    public bool IsReviewed => IsManuallyReviewed;

    public bool RequiresReview => !IsManuallyReviewed
        && (State != QuestionMarkState.Single || string.IsNullOrEmpty(AnswerLabel));

    internal static ReviewedQuestionAnswer FromRecognition(QuestionRecognitionResult question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var selected = question.Options
            .Where(option => option.State == OptionMarkState.Selected)
            .ToArray();
        var answer = question.State == QuestionMarkState.Single && selected.Length == 1
            ? selected[0].OptionLabel
            : null;
        return new ReviewedQuestionAnswer(
            question.QuestionNumber,
            question.State,
            answer,
            IsManuallyReviewed: false,
            IsBlankConfirmed: false);
    }

    internal static ReviewedQuestionAnswer Uncertain(int questionNumber)
    {
        return new ReviewedQuestionAnswer(
            questionNumber,
            QuestionMarkState.Uncertain,
            AnswerLabel: null,
            IsManuallyReviewed: false,
            IsBlankConfirmed: false);
    }
}

/// <summary>An append-only audit entry for one manual review operation.</summary>
public sealed record ManualReviewChange(
    int QuestionNumber,
    ReviewedQuestionAnswer Before,
    ReviewedQuestionAnswer After,
    string Reviewer,
    string? Reason,
    DateTimeOffset Timestamp,
    ManualReviewAction Action)
{
    public string? BeforeAnswer => Before.AnswerLabel;

    public string? AfterAnswer => After.AnswerLabel;

    public QuestionMarkState BeforeState => Before.State;

    public QuestionMarkState AfterState => After.State;

    public DateTimeOffset Time => Timestamp;

    public DateTimeOffset TimestampUtc => Timestamp;
}

/// <summary>
/// Immutable manual-review state. Every edit returns a new instance and
/// appends one audit entry containing before/after values, reviewer, reason,
/// and timestamp. The original recognition snapshot remains available forever.
/// </summary>
public sealed class ManualReviewSession
{
    private readonly IReadOnlyDictionary<int, ReviewedQuestionAnswer> originalAnswers;

    private ManualReviewSession(
        RecognitionResult originalRecognition,
        DateTimeOffset createdAt,
        IReadOnlyDictionary<int, ReviewedQuestionAnswer> originalAnswers,
        IReadOnlyDictionary<int, ReviewedQuestionAnswer> currentAnswers,
        IReadOnlyList<ManualReviewChange> history)
    {
        OriginalRecognition = originalRecognition;
        CreatedAt = createdAt;
        this.originalAnswers = originalAnswers;
        CurrentAnswers = currentAnswers;
        History = history;
    }

    public RecognitionResult OriginalRecognition { get; }

    public RecognitionResult Recognition => OriginalRecognition;

    public DateTimeOffset CreatedAt { get; }

    public IReadOnlyDictionary<int, ReviewedQuestionAnswer> OriginalAnswers => originalAnswers;

    public IReadOnlyDictionary<int, ReviewedQuestionAnswer> CurrentAnswers { get; }

    public IReadOnlyDictionary<int, ReviewedQuestionAnswer> Answers => CurrentAnswers;

    public IReadOnlyList<ManualReviewChange> History { get; }

    public IReadOnlyList<ManualReviewChange> ReviewHistory => History;

    public bool CanReview => OriginalRecognition.Status != RecognitionStatus.Rejected;

    public bool IsRejected => OriginalRecognition.Status == RecognitionStatus.Rejected;

    public bool IsComplete => CurrentAnswers.Values.All(answer => !answer.RequiresReview);

    public IReadOnlyList<int> ReviewRequiredQuestionNumbers =>
        CurrentAnswers.Values
            .Where(answer => answer.RequiresReview)
            .Select(answer => answer.QuestionNumber)
            .OrderBy(questionNumber => questionNumber)
            .ToArray();

    public IReadOnlyList<int> UnreviewedQuestionNumbers =>
        CurrentAnswers.Values
            .Where(answer => !answer.IsManuallyReviewed)
            .Select(answer => answer.QuestionNumber)
            .OrderBy(questionNumber => questionNumber)
            .ToArray();

    public IReadOnlyList<int> BlankConfirmedQuestionNumbers =>
        CurrentAnswers.Values
            .Where(answer => answer.IsBlankConfirmed)
            .Select(answer => answer.QuestionNumber)
            .OrderBy(questionNumber => questionNumber)
            .ToArray();

    /// <summary>Starts an immutable review from a private snapshot of recognition.</summary>
    public static ManualReviewSession Start(
        RecognitionResult recognition,
        DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(recognition);
        var snapshot = CloneRecognition(recognition);
        var answers = new Dictionary<int, ReviewedQuestionAnswer>(snapshot.Questions.Count);
        foreach (var question in snapshot.Questions)
        {
            if (question.QuestionNumber <= 0)
            {
                continue;
            }

            if (answers.ContainsKey(question.QuestionNumber))
            {
                throw new ArgumentException(
                    $"识别结果包含重复题号 {question.QuestionNumber}，无法建立复核快照。",
                    nameof(recognition));
            }

            answers.Add(
                question.QuestionNumber,
                ReviewedQuestionAnswer.FromRecognition(question));
        }

        var readOnlyAnswers = ReadOnlyDictionary(answers);
        return new ManualReviewSession(
            snapshot,
            (createdAt ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            readOnlyAnswers,
            readOnlyAnswers,
            Array.Empty<ManualReviewChange>());
    }

    public static ManualReviewSession Create(
        RecognitionResult recognition,
        DateTimeOffset? createdAt = null)
    {
        return Start(recognition, createdAt);
    }

    public ManualReviewSession SetAnswer(
        int questionNumber,
        string answerLabel,
        string reviewer,
        string? reason = null,
        DateTimeOffset? timestamp = null)
    {
        return Apply(
            questionNumber,
            CreateAnsweredState(questionNumber, answerLabel),
            reviewer,
            reason,
            timestamp,
            ManualReviewAction.SetAnswer);
    }

    public ManualReviewSession CorrectAnswer(
        int questionNumber,
        string answerLabel,
        string reviewer,
        string? reason = null,
        DateTimeOffset? timestamp = null)
    {
        return SetAnswer(questionNumber, answerLabel, reviewer, reason, timestamp);
    }

    public ManualReviewSession ApplyAnswer(
        int questionNumber,
        string answerLabel,
        string reviewer,
        string? reason = null,
        DateTimeOffset? timestamp = null)
    {
        return SetAnswer(questionNumber, answerLabel, reviewer, reason, timestamp);
    }

    public ManualReviewSession ApplyCorrection(
        int questionNumber,
        string answerLabel,
        string reviewer,
        string? reason = null,
        DateTimeOffset? timestamp = null)
    {
        return SetAnswer(questionNumber, answerLabel, reviewer, reason, timestamp);
    }

    public ManualReviewSession ConfirmBlank(
        int questionNumber,
        string reviewer,
        string? reason = null,
        DateTimeOffset? timestamp = null)
    {
        EnsureCanReview();
        var before = GetCurrent(questionNumber);
        var after = new ReviewedQuestionAnswer(
            questionNumber,
            QuestionMarkState.Blank,
            AnswerLabel: null,
            IsManuallyReviewed: true,
            IsBlankConfirmed: true);
        return Apply(
            questionNumber,
            after,
            reviewer,
            reason,
            timestamp,
            ManualReviewAction.ConfirmBlank,
            before);
    }

    public ManualReviewSession MarkBlank(
        int questionNumber,
        string reviewer,
        string? reason = null,
        DateTimeOffset? timestamp = null)
    {
        return ConfirmBlank(questionNumber, reviewer, reason, timestamp);
    }

    public ManualReviewSession ResetToRecognition(
        int questionNumber,
        string reviewer,
        string? reason = null,
        DateTimeOffset? timestamp = null)
    {
        EnsureCanReview();
        var after = originalAnswers.TryGetValue(questionNumber, out var original)
            ? original
            : throw new ArgumentOutOfRangeException(nameof(questionNumber));
        return Apply(
            questionNumber,
            after,
            reviewer,
            reason,
            timestamp,
            ManualReviewAction.ResetToRecognition);
    }

    public ManualReviewSession ClearReview(
        int questionNumber,
        string reviewer,
        string? reason = null,
        DateTimeOffset? timestamp = null)
    {
        return ResetToRecognition(questionNumber, reviewer, reason, timestamp);
    }

    public ManualReviewSession Snapshot() => this;

    public ManualReviewSession ToResult() => this;

    private ManualReviewSession Apply(
        int questionNumber,
        ReviewedQuestionAnswer after,
        string reviewer,
        string? reason,
        DateTimeOffset? timestamp,
        ManualReviewAction action,
        ReviewedQuestionAnswer? beforeOverride = null)
    {
        EnsureCanReview();
        var before = beforeOverride ?? GetCurrent(questionNumber);
        var normalizedReviewer = NormalizeReviewer(reviewer);
        var normalizedReason = NormalizeReason(reason);
        var change = new ManualReviewChange(
            questionNumber,
            before,
            after,
            normalizedReviewer,
            normalizedReason,
            (timestamp ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            action);

        var nextAnswers = new Dictionary<int, ReviewedQuestionAnswer>(CurrentAnswers)
        {
            [questionNumber] = after
        };
        var nextHistory = new List<ManualReviewChange>(History.Count + 1);
        nextHistory.AddRange(History);
        nextHistory.Add(change);
        return new ManualReviewSession(
            OriginalRecognition,
            CreatedAt,
            originalAnswers,
            ReadOnlyDictionary(nextAnswers),
            new ReadOnlyCollection<ManualReviewChange>(nextHistory));
    }

    private ReviewedQuestionAnswer CreateAnsweredState(int questionNumber, string answerLabel)
    {
        EnsureCanReview();
        var before = GetCurrent(questionNumber);
        var normalizedAnswer = NormalizeAnswerLabel(answerLabel, questionNumber, before);
        return new ReviewedQuestionAnswer(
            questionNumber,
            QuestionMarkState.Single,
            normalizedAnswer,
            IsManuallyReviewed: true,
            IsBlankConfirmed: false);
    }

    private ReviewedQuestionAnswer GetCurrent(int questionNumber)
    {
        if (!CurrentAnswers.TryGetValue(questionNumber, out var answer))
        {
            throw new ArgumentOutOfRangeException(
                nameof(questionNumber),
                questionNumber,
                "识别结果中不存在该题号。");
        }

        return answer;
    }

    private void EnsureCanReview()
    {
        if (!CanReview)
        {
            throw new InvalidOperationException(
                "Rejected 的识别结果不能通过题目编辑变为可信结果，请重新采集或重新识别。");
        }
    }

    private string NormalizeAnswerLabel(
        string answerLabel,
        int questionNumber,
        ReviewedQuestionAnswer current)
    {
        if (string.IsNullOrWhiteSpace(answerLabel))
        {
            throw new ArgumentException("人工答案选项不能为空。", nameof(answerLabel));
        }

        var normalized = answerLabel.Trim();
        if (!AnswerKey.IsValidOptionLabel(normalized, AnswerSheetLayout.MaxOptionsPerQuestion))
        {
            throw new ArgumentException(
                "人工答案必须是 A 到 F 的单个大写选项标签。",
                nameof(answerLabel));
        }

        var sourceQuestion = OriginalRecognition.Questions
            .FirstOrDefault(question => question.QuestionNumber == questionNumber);
        if (sourceQuestion is not null
            && sourceQuestion.Options.Count > 0
            && !sourceQuestion.Options.Any(option => string.Equals(option.OptionLabel, normalized, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"第 {questionNumber} 题没有选项 {normalized}。",
                nameof(answerLabel));
        }

        return normalized;
    }

    private static string NormalizeReviewer(string reviewer)
    {
        if (string.IsNullOrWhiteSpace(reviewer))
        {
            throw new ArgumentException("复核人不能为空。", nameof(reviewer));
        }

        return reviewer.Trim();
    }

    private static string? NormalizeReason(string? reason)
    {
        return string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }

    private static IReadOnlyDictionary<int, ReviewedQuestionAnswer> ReadOnlyDictionary(
        IDictionary<int, ReviewedQuestionAnswer> source)
    {
        return new ReadOnlyDictionary<int, ReviewedQuestionAnswer>(
            new Dictionary<int, ReviewedQuestionAnswer>(source));
    }

    private static RecognitionResult CloneRecognition(RecognitionResult source)
    {
        var questions = source.Questions
            .Select(
                question => new QuestionRecognitionResult(
                    question.QuestionNumber,
                    question.State,
                    new ReadOnlyCollection<OptionRecognitionResult>(
                        question.Options
                            .Select(
                                option => new OptionRecognitionResult(
                                    option.OptionIndex,
                                    option.OptionLabel,
                                    option.FillRatio,
                                    option.State,
                                    option.Confidence))
                            .ToArray()),
                    question.Confidence))
            .ToArray();
        var diagnostics = source.Diagnostics.ToArray();
        return new RecognitionResult(
            source.TemplateId,
            source.TemplateSchemaVersion,
            source.Status,
            source.Orientation,
            source.Transform,
            source.Confidence,
            new ReadOnlyCollection<QuestionRecognitionResult>(questions),
            new ReadOnlyCollection<RecognitionDiagnostic>(diagnostics));
    }
}

/// <summary>Factory for the immutable manual-review workflow.</summary>
public static class ManualReview
{
    public static ManualReviewSession Start(
        RecognitionResult recognition,
        DateTimeOffset? createdAt = null)
    {
        return ManualReviewSession.Start(recognition, createdAt);
    }

    public static ManualReviewSession Create(
        RecognitionResult recognition,
        DateTimeOffset? createdAt = null)
    {
        return Start(recognition, createdAt);
    }
}
