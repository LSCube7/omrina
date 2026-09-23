using System.Collections.ObjectModel;

namespace Omrina.Core;

/// <summary>Options that affect numeric scoring.</summary>
public sealed class ScoringOptions
{
    public ScoringOptions()
    {
    }

    public ScoringOptions(decimal pointsPerQuestion)
    {
        PointsPerQuestion = pointsPerQuestion;
    }

    /// <summary>Points awarded for one correct question. The default is one point.</summary>
    public decimal PointsPerQuestion { get; init; } = 1m;

    internal void Validate()
    {
        if (PointsPerQuestion <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PointsPerQuestion),
                PointsPerQuestion,
                "每题分值必须大于零。");
        }
    }
}

/// <summary>The state of the score produced by the scoring engine.</summary>
public enum ScoringDisposition
{
    /// <summary>No score is available because the answer key is missing or recognition was rejected.</summary>
    NotScored,

    /// <summary>All answer-bearing questions have a resolved interpretation.</summary>
    Final,

    /// <summary>A numeric estimate exists, but one or more questions still require review.</summary>
    Provisional
}

/// <summary>Why a numeric score was not produced.</summary>
public enum ScoreUnavailableReason
{
    None,
    AnswerKeyMissing,
    RecognitionRejected
}

/// <summary>The score of one question, including the recognition state used to calculate it.</summary>
public sealed record QuestionScore(
    int QuestionNumber,
    QuestionMarkState RecognitionState,
    string? RecognizedAnswer,
    string? ExpectedAnswer,
    decimal EarnedPoints,
    decimal MaximumPoints,
    bool? IsCorrect,
    bool RequiresReview,
    bool IsManuallyReviewed,
    bool IsBlankConfirmed)
{
    public decimal Score => EarnedPoints;

    public decimal MaxScore => MaximumPoints;

    public string? Answer => RecognizedAnswer;

    public string? AnswerKey => ExpectedAnswer;

    public bool IsReviewed => IsManuallyReviewed;
}

/// <summary>
/// Immutable output from <see cref="ScoringEngine"/>. A rejected recognition
/// can never be converted into a trusted score by editing question answers.
/// </summary>
public sealed class ScoringResult
{
    internal ScoringResult(
        RecognitionResult recognition,
        AnswerKey? answerKey,
        ScoringOptions options,
        IReadOnlyList<QuestionScore> questionScores,
        decimal? totalScore,
        decimal? maximumScore,
        ScoringDisposition disposition,
        ScoreUnavailableReason unavailableReason,
        ManualReviewSession? review)
    {
        Recognition = recognition;
        AnswerKey = answerKey;
        Options = options;
        QuestionScores = questionScores;
        TotalScore = totalScore;
        MaximumScore = maximumScore;
        Disposition = disposition;
        UnavailableReason = unavailableReason;
        Review = review;
    }

    public RecognitionResult Recognition { get; }

    public AnswerKey? AnswerKey { get; }

    public ScoringOptions Options { get; }

    public IReadOnlyList<QuestionScore> QuestionScores { get; }

    public IReadOnlyList<QuestionScore> Questions => QuestionScores;

    /// <summary>Null means that this result was not scored.</summary>
    public decimal? TotalScore { get; }

    /// <summary>Null means that this result was not scored.</summary>
    public decimal? MaximumScore { get; }

    public decimal? Score => TotalScore;

    public decimal? MaxScore => MaximumScore;

    /// <summary>Returns a value only after all pending review has been resolved.</summary>
    public decimal? FinalScore => IsFinal ? TotalScore : null;

    /// <summary>Returns the current numeric estimate only while it is provisional.</summary>
    public decimal? ProvisionalScore => IsProvisional ? TotalScore : null;

    public ScoringDisposition Disposition { get; }

    public ScoreUnavailableReason UnavailableReason { get; }

    public bool IsScored => TotalScore.HasValue && MaximumScore.HasValue;

    public bool IsProvisional => Disposition == ScoringDisposition.Provisional;

    public bool IsFinal => Disposition == ScoringDisposition.Final;

    /// <summary>
    /// True when any recognized question remains unresolved. This remains
    /// useful for recognition-only (unscored) exports where no numeric
    /// provisional disposition can be produced.
    /// </summary>
    public bool RequiresReview => IsProvisional
        || Recognition.Diagnostics.Any(diagnostic =>
            diagnostic.Code == RecognitionDiagnosticCode.PageQualityUncertain)
        || QuestionScores.Any(question => question.RequiresReview);

    public bool HasUnresolvedQuestions => RequiresReview;

    /// <summary>The immutable review snapshot used to produce this score, when any.</summary>
    public ManualReviewSession? Review { get; }

    /// <summary>Alias useful to callers that refer to one run as an assessment.</summary>
    public ScoringResult Assessment => this;
}

/// <summary>
/// Scores a recognition result against an explicit answer key. This class has
/// no dependency on image or UI code and deliberately never treats a filled
/// answer on the captured page as the answer key.
/// </summary>
public static class ScoringEngine
{
    public static ScoringResult Score(
        RecognitionResult recognition,
        AnswerKey? answerKey = null,
        ScoringOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(recognition);
        var effectiveOptions = options ?? new ScoringOptions();
        effectiveOptions.Validate();
        return ScoreCore(recognition, answerKey, effectiveOptions, review: null);
    }

    /// <summary>Scores the current immutable state of a manual review.</summary>
    public static ScoringResult Score(
        ManualReviewSession review,
        AnswerKey? answerKey = null,
        ScoringOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(review);
        var effectiveOptions = options ?? new ScoringOptions();
        effectiveOptions.Validate();
        return ScoreCore(review.OriginalRecognition, answerKey, effectiveOptions, review);
    }

    private static ScoringResult ScoreCore(
        RecognitionResult recognition,
        AnswerKey? answerKey,
        ScoringOptions options,
        ManualReviewSession? review)
    {
        if (recognition.Status == RecognitionStatus.Rejected)
        {
            return new ScoringResult(
                recognition,
                answerKey,
                options,
                Array.Empty<QuestionScore>(),
                totalScore: null,
                maximumScore: null,
                ScoringDisposition.NotScored,
                ScoreUnavailableReason.RecognitionRejected,
                review);
        }

        var observations = review is null
            ? CreateRecognitionObservations(recognition.Questions)
            : review.CurrentAnswers;

        if (answerKey is null)
        {
            var recognitionOnlyScores = CreateRecognitionOnlyScores(observations, options);
            return new ScoringResult(
                recognition,
                answerKey: null,
                options,
                recognitionOnlyScores,
                totalScore: null,
                maximumScore: null,
                ScoringDisposition.NotScored,
                ScoreUnavailableReason.AnswerKeyMissing,
                review);
        }

        var questionScores = new List<QuestionScore>(answerKey.QuestionCount + observations.Count);
        decimal total = 0m;
        // ReviewRequired is never silently promoted to a final score merely
        // because a caller supplies an answer key. A ManualReviewSession can
        // resolve its question-level findings explicitly.
        var requiresReview = recognition.Diagnostics.Any(diagnostic =>
                diagnostic.Code == RecognitionDiagnosticCode.PageQualityUncertain)
            || (review is null && recognition.Status == RecognitionStatus.ReviewRequired);

        for (var questionNumber = 1; questionNumber <= answerKey.QuestionCount; questionNumber++)
        {
            if (!observations.TryGetValue(questionNumber, out var observation))
            {
                observation = ReviewedQuestionAnswer.Uncertain(questionNumber);
            }

            var expectedAnswer = answerKey[questionNumber];
            var isCorrect = observation.AnswerLabel is not null
                && observation.State == QuestionMarkState.Single
                && string.Equals(observation.AnswerLabel, expectedAnswer, StringComparison.Ordinal);
            var earnedPoints = isCorrect ? options.PointsPerQuestion : 0m;
            var questionRequiresReview = observation.RequiresReview;
            requiresReview |= questionRequiresReview;
            total += earnedPoints;
            questionScores.Add(
                new QuestionScore(
                    questionNumber,
                    observation.State,
                    observation.AnswerLabel,
                    expectedAnswer,
                    earnedPoints,
                    options.PointsPerQuestion,
                    isCorrect,
                    questionRequiresReview,
                    observation.IsManuallyReviewed,
                    observation.IsBlankConfirmed));
        }

        // Preserve any engine output outside the answer-key range as an
        // explicitly reviewable row instead of silently dropping it.
        foreach (var pair in observations.OrderBy(pair => pair.Key))
        {
            if (pair.Key >= 1 && pair.Key <= answerKey.QuestionCount)
            {
                continue;
            }

            requiresReview = true;
            var observation = pair.Value;
            questionScores.Add(
                new QuestionScore(
                    pair.Key,
                    observation.State,
                    observation.AnswerLabel,
                    ExpectedAnswer: null,
                    EarnedPoints: 0m,
                    MaximumPoints: 0m,
                    IsCorrect: null,
                    RequiresReview: true,
                    observation.IsManuallyReviewed,
                    observation.IsBlankConfirmed));
        }

        var disposition = requiresReview
            ? ScoringDisposition.Provisional
            : ScoringDisposition.Final;
        var maximumScore = answerKey.QuestionCount * options.PointsPerQuestion;
        return new ScoringResult(
            recognition,
            answerKey,
            options,
            new ReadOnlyCollection<QuestionScore>(questionScores),
            total,
            maximumScore,
            disposition,
            ScoreUnavailableReason.None,
            review);
    }

    private static IReadOnlyDictionary<int, ReviewedQuestionAnswer> CreateRecognitionObservations(
        IReadOnlyList<QuestionRecognitionResult> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);
        var observations = new Dictionary<int, ReviewedQuestionAnswer>(questions.Count);
        foreach (var question in questions)
        {
            if (question.QuestionNumber <= 0)
            {
                continue;
            }

            observations[question.QuestionNumber] = ReviewedQuestionAnswer.FromRecognition(question);
        }

        return new ReadOnlyDictionary<int, ReviewedQuestionAnswer>(observations);
    }

    private static IReadOnlyList<QuestionScore> CreateRecognitionOnlyScores(
        IReadOnlyDictionary<int, ReviewedQuestionAnswer> observations,
        ScoringOptions options)
    {
        var scores = new List<QuestionScore>(observations.Count);
        foreach (var pair in observations.OrderBy(pair => pair.Key))
        {
            var observation = pair.Value;
            scores.Add(
                new QuestionScore(
                    pair.Key,
                    observation.State,
                    observation.AnswerLabel,
                    ExpectedAnswer: null,
                    EarnedPoints: 0m,
                    MaximumPoints: 0m,
                    IsCorrect: null,
                    RequiresReview: observation.RequiresReview,
                    observation.IsManuallyReviewed,
                    observation.IsBlankConfirmed));
        }

        return new ReadOnlyCollection<QuestionScore>(scores);
    }
}
