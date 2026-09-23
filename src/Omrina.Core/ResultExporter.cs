using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Omrina.Core;

public enum ResultExportFormat
{
    Json,
    Csv
}

/// <summary>
/// Deterministic, platform-independent exports for a scoring result.
/// JSON uses the framework encoder; CSV quotes every structural delimiter and
/// prefixes spreadsheet formula-like cells to prevent formula injection.
/// </summary>
public static class ResultExporter
{
    private const int ExportSchemaVersion = 1;

    private const string CsvHeader =
        "recordType,templateId,recognitionStatus,disposition,isProvisional,questionNumber,"
        + "recognizedAnswer,expectedAnswer,recognitionState,earnedPoints,maximumPoints,isCorrect,"
        + "requiresReview,isManuallyReviewed,blankConfirmed,reviewAction,beforeAnswer,afterAnswer,"
        + "reviewer,reason,timestamp,diagnosticCode,diagnosticMessage";

    public static string Export(
        ScoringResult result,
        ResultExportFormat format,
        bool indented = false)
    {
        return format switch
        {
            ResultExportFormat.Json => ToJson(result, indented),
            ResultExportFormat.Csv => ToCsv(result),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "不支持的导出格式。")
        };
    }

    public static string ToJson(ScoringResult result, bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        var document = CreateJsonDocument(result);
        var options = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.Default,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Serialize(document, options);
    }

    public static string ExportJson(ScoringResult result, bool indented = false)
    {
        return ToJson(result, indented);
    }

    public static string ToCsv(ScoringResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder();
        builder.AppendLine(CsvHeader);

        AppendRow(
            builder,
            "summary",
            result.Recognition.TemplateId,
            result.Recognition.Status.ToString(),
            result.Disposition.ToString(),
            result.IsProvisional.ToString(CultureInfo.InvariantCulture),
            questionNumber: null,
            recognizedAnswer: null,
            expectedAnswer: null,
            recognitionState: null,
            earnedPoints: result.TotalScore,
            maximumPoints: result.MaximumScore,
            isCorrect: null,
            requiresReview: result.RequiresReview,
            isManuallyReviewed: null,
            blankConfirmed: null,
            reviewAction: null,
            beforeAnswer: null,
            afterAnswer: null,
            reviewer: null,
            reason: null,
            timestamp: null,
            diagnosticCode: result.UnavailableReason == ScoreUnavailableReason.None
                ? null
                : result.UnavailableReason.ToString(),
            diagnosticMessage: null);

        var latestChanges = result.Review?.History
            .GroupBy(change => change.QuestionNumber)
            .ToDictionary(group => group.Key, group => group.Last());
        foreach (var question in result.QuestionScores.OrderBy(question => question.QuestionNumber))
        {
            ManualReviewChange? latestChange = null;
            if (latestChanges is not null)
            {
                latestChanges.TryGetValue(question.QuestionNumber, out latestChange);
            }
            AppendRow(
                builder,
                "question",
                result.Recognition.TemplateId,
                result.Recognition.Status.ToString(),
                result.Disposition.ToString(),
                result.IsProvisional.ToString(CultureInfo.InvariantCulture),
                question.QuestionNumber,
                question.RecognizedAnswer,
                question.ExpectedAnswer,
                question.RecognitionState.ToString(),
                question.EarnedPoints,
                question.MaximumPoints,
                question.IsCorrect,
                question.RequiresReview,
                question.IsManuallyReviewed,
                question.IsBlankConfirmed,
                latestChange?.Action.ToString(),
                latestChange?.BeforeAnswer,
                latestChange?.AfterAnswer,
                latestChange?.Reviewer,
                latestChange?.Reason,
                latestChange?.Timestamp,
                diagnosticCode: null,
                diagnosticMessage: null);
        }

        if (result.Review is not null)
        {
            foreach (var change in result.Review.History)
            {
                AppendRow(
                    builder,
                    "review",
                    result.Recognition.TemplateId,
                    result.Recognition.Status.ToString(),
                    result.Disposition.ToString(),
                    result.IsProvisional.ToString(CultureInfo.InvariantCulture),
                    change.QuestionNumber,
                    change.After.AnswerLabel,
                    result.AnswerKey?.TryGetAnswer(change.QuestionNumber, out var expected) == true
                        ? expected
                        : null,
                    change.After.State.ToString(),
                    earnedPoints: null,
                    maximumPoints: null,
                    isCorrect: null,
                    requiresReview: change.After.RequiresReview,
                    isManuallyReviewed: change.After.IsManuallyReviewed,
                    blankConfirmed: change.After.IsBlankConfirmed,
                    change.Action.ToString(),
                    change.BeforeAnswer,
                    change.AfterAnswer,
                    change.Reviewer,
                    change.Reason,
                    change.Timestamp,
                    diagnosticCode: null,
                    diagnosticMessage: null);
            }
        }

        foreach (var diagnostic in result.Recognition.Diagnostics)
        {
            AppendRow(
                builder,
                "diagnostic",
                result.Recognition.TemplateId,
                result.Recognition.Status.ToString(),
                result.Disposition.ToString(),
                result.IsProvisional.ToString(CultureInfo.InvariantCulture),
                questionNumber: null,
                recognizedAnswer: null,
                expectedAnswer: null,
                recognitionState: null,
                earnedPoints: null,
                maximumPoints: null,
                isCorrect: null,
                requiresReview: null,
                isManuallyReviewed: null,
                blankConfirmed: null,
                reviewAction: null,
                beforeAnswer: null,
                afterAnswer: null,
                reviewer: null,
                reason: null,
                timestamp: null,
                diagnosticCode: diagnostic.Code.ToString(),
                diagnosticMessage: diagnostic.Message);
        }

        return builder.ToString();
    }

    public static string ExportCsv(ScoringResult result)
    {
        return ToCsv(result);
    }

    private static object CreateJsonDocument(ScoringResult result)
    {
        var recognition = CreateRecognitionDocument(result.Recognition);
        var review = result.Review is null
            ? null
            : new
            {
                createdAt = result.Review.CreatedAt,
                currentAnswers = result.Review.CurrentAnswers.Values
                    .OrderBy(answer => answer.QuestionNumber)
                    .Select(CreateReviewedAnswerDocument),
                history = result.Review.History.Select(CreateReviewChangeDocument)
            };

        return new
        {
            schemaVersion = ExportSchemaVersion,
            recognition,
            answerKey = result.AnswerKey is null
                ? null
                : new
                {
                    questionCount = result.AnswerKey.QuestionCount,
                    optionsPerQuestion = result.AnswerKey.OptionsPerQuestion,
                    answers = result.AnswerKey.Answers
                        .OrderBy(pair => pair.Key)
                        .ToDictionary(pair => pair.Key.ToString(CultureInfo.InvariantCulture), pair => pair.Value)
                },
            scoring = new
            {
                disposition = result.Disposition,
                unavailableReason = result.UnavailableReason,
                isScored = result.IsScored,
                isProvisional = result.IsProvisional,
                requiresReview = result.RequiresReview,
                totalScore = result.TotalScore,
                maximumScore = result.MaximumScore,
                pointsPerQuestion = result.Options.PointsPerQuestion,
                questions = result.QuestionScores
                    .OrderBy(question => question.QuestionNumber)
                    .Select(
                        question => new
                        {
                            questionNumber = question.QuestionNumber,
                            recognitionState = question.RecognitionState,
                            recognizedAnswer = question.RecognizedAnswer,
                            expectedAnswer = question.ExpectedAnswer,
                            earnedPoints = question.EarnedPoints,
                            maximumPoints = question.MaximumPoints,
                            isCorrect = question.IsCorrect,
                            requiresReview = question.RequiresReview,
                            isManuallyReviewed = question.IsManuallyReviewed,
                            isBlankConfirmed = question.IsBlankConfirmed
                        })
            },
            review
        };
    }

    private static object CreateRecognitionDocument(RecognitionResult recognition)
    {
        return new
        {
            templateId = recognition.TemplateId,
            templateSchemaVersion = recognition.TemplateSchemaVersion,
            status = recognition.Status,
            orientation = recognition.Orientation,
            transform = recognition.Transform,
            confidence = recognition.Confidence,
            questions = recognition.Questions
                .OrderBy(question => question.QuestionNumber)
                .Select(
                    question => new
                    {
                        questionNumber = question.QuestionNumber,
                        state = question.State,
                        confidence = question.Confidence,
                        options = question.Options
                            .OrderBy(option => option.OptionIndex)
                            .Select(
                                option => new
                                {
                                    optionIndex = option.OptionIndex,
                                    optionLabel = option.OptionLabel,
                                    fillRatio = option.FillRatio,
                                    state = option.State,
                                    confidence = option.Confidence
                                })
                    }),
            diagnostics = recognition.Diagnostics.Select(
                diagnostic => new
                {
                    code = diagnostic.Code,
                    severity = diagnostic.Severity,
                    message = diagnostic.Message,
                    score = diagnostic.Score
                })
        };
    }

    private static object CreateReviewedAnswerDocument(ReviewedQuestionAnswer answer)
    {
        return new
        {
            questionNumber = answer.QuestionNumber,
            state = answer.State,
            answerLabel = answer.AnswerLabel,
            isManuallyReviewed = answer.IsManuallyReviewed,
            isBlankConfirmed = answer.IsBlankConfirmed,
            requiresReview = answer.RequiresReview
        };
    }

    private static object CreateReviewChangeDocument(ManualReviewChange change)
    {
        return new
        {
            questionNumber = change.QuestionNumber,
            action = change.Action,
            reviewer = change.Reviewer,
            reason = change.Reason,
            timestamp = change.Timestamp,
            before = CreateReviewedAnswerDocument(change.Before),
            after = CreateReviewedAnswerDocument(change.After)
        };
    }

    private static void AppendRow(
        StringBuilder builder,
        string recordType,
        string templateId,
        string recognitionStatus,
        string disposition,
        string isProvisional,
        int? questionNumber,
        string? recognizedAnswer,
        string? expectedAnswer,
        string? recognitionState,
        decimal? earnedPoints,
        decimal? maximumPoints,
        bool? isCorrect,
        bool? requiresReview,
        bool? isManuallyReviewed,
        bool? blankConfirmed,
        string? reviewAction,
        string? beforeAnswer,
        string? afterAnswer,
        string? reviewer,
        string? reason,
        DateTimeOffset? timestamp,
        string? diagnosticCode,
        string? diagnosticMessage)
    {
        var values = new string?[]
        {
            recordType,
            templateId,
            recognitionStatus,
            disposition,
            isProvisional,
            questionNumber?.ToString(CultureInfo.InvariantCulture),
            recognizedAnswer,
            expectedAnswer,
            recognitionState,
            earnedPoints?.ToString(CultureInfo.InvariantCulture),
            maximumPoints?.ToString(CultureInfo.InvariantCulture),
            isCorrect?.ToString(CultureInfo.InvariantCulture),
            requiresReview?.ToString(CultureInfo.InvariantCulture),
            isManuallyReviewed?.ToString(CultureInfo.InvariantCulture),
            blankConfirmed?.ToString(CultureInfo.InvariantCulture),
            reviewAction,
            beforeAnswer,
            afterAnswer,
            reviewer,
            reason,
            timestamp?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            diagnosticCode,
            diagnosticMessage
        };

        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(EscapeCsvCell(values[index]));
        }

        builder.AppendLine();
    }

    private static string EscapeCsvCell(string? value)
    {
        var text = value ?? string.Empty;
        if (LooksLikeSpreadsheetFormula(text))
        {
            text = "'" + text;
        }

        if (text.IndexOfAny(['"', ',', '\r', '\n']) >= 0)
        {
            return '"' + text.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
        }

        return text;
    }

    private static bool LooksLikeSpreadsheetFormula(string value)
    {
        var first = value.AsSpan();
        var offset = 0;
        while (offset < first.Length && first[offset] is ' ' or '\t' or '\r' or '\n')
        {
            offset++;
        }

        return offset < first.Length && first[offset] is '=' or '+' or '-' or '@';
    }
}
