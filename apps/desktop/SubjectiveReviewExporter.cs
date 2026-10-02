using System.Globalization;
using System.Text;
using System.Text.Json;
using Omrina.Core;
using Omrina.Protocol;

namespace Omrina.Desktop;

/// <summary>Pure wire/export formatting shared by the local service and grant adapter.</summary>
public static class SubjectiveReviewExporter
{
    public static object ToWireDocument(Guid reviewId, SubjectiveGradingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (reviewId == Guid.Empty)
        {
            throw new ArgumentException("ReviewId must be a non-empty GUID.", nameof(reviewId));
        }

        return new
        {
            reviewId,
            captureId = snapshot.CaptureId,
            version = snapshot.Version,
            questions = snapshot.Questions.Select(question => new
            {
                questionId = question.QuestionId,
                questionNumber = question.QuestionNumber,
                maxScore = question.MaximumScore,
                region = new
                {
                    x = question.Region.X,
                    y = question.Region.Y,
                    width = question.Region.Width,
                    height = question.Region.Height
                },
                status = ToWireStatus(question.Status),
                score = question.Score,
                comment = question.Comment,
                reviewer = question.Reviewer,
                confirmedAtUtc = question.ConfirmedAtUtc
            }).ToArray(),
            history = snapshot.History.Select(batch => new
            {
                version = batch.Version,
                reviewer = batch.Reviewer,
                timestampUtc = batch.TimestampUtc,
                changes = batch.Changes.Select(change => new
                {
                    questionId = change.QuestionId,
                    questionNumber = change.QuestionNumber,
                    action = change.Action switch
                    {
                        SubjectiveGradingAction.SetDraft => "setDraft",
                        SubjectiveGradingAction.Confirm => "confirm",
                        SubjectiveGradingAction.Reset => "reset",
                        _ => throw new InvalidOperationException("Unknown subjective grading action.")
                    },
                    before = ToWireGradeState(change.Before),
                    after = ToWireGradeState(change.After)
                }).ToArray()
            }).ToArray(),
            createdAtUtc = snapshot.CreatedAtUtc,
            updatedAtUtc = snapshot.UpdatedAtUtc,
            isFinal = snapshot.IsFinal,
            finalSubtotal = snapshot.FinalSubtotal
        };
    }

    public static string ExportJson(Guid reviewId, SubjectiveGradingSnapshot snapshot)
    {
        return JsonSerializer.Serialize(ToWireDocument(reviewId, snapshot), AgentJson.Options);
    }

    public static string ExportCsv(SubjectiveGradingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var builder = new StringBuilder();
        builder.AppendLine("questionId,questionNumber,maxScore,status,score,comment,reviewer,confirmedAtUtc");
        foreach (var question in snapshot.Questions)
        {
            AppendCsvRow(builder,
                question.QuestionId.ToString("D"),
                question.QuestionNumber.ToString(CultureInfo.InvariantCulture),
                question.MaximumScore.ToString(CultureInfo.InvariantCulture),
                ToWireStatus(question.Status),
                question.Score?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                question.Comment ?? string.Empty,
                question.Reviewer ?? string.Empty,
                question.ConfirmedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return builder.ToString();
    }

    private static object ToWireGradeState(SubjectiveGradeState state) => new
    {
        status = ToWireStatus(state.Status),
        score = state.Score,
        comment = state.Comment,
        reviewer = state.Reviewer,
        confirmedAtUtc = state.ConfirmedAtUtc
    };

    private static string ToWireStatus(SubjectiveReviewStatus status) => status switch
    {
        SubjectiveReviewStatus.Unreviewed => "ungraded",
        SubjectiveReviewStatus.Draft => "draft",
        SubjectiveReviewStatus.Confirmed => "confirmed",
        _ => throw new InvalidOperationException("Unknown subjective review status.")
    };

    private static void AppendCsvRow(StringBuilder builder, params string[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            var value = values[index];
            var firstNonWhitespace = value.AsSpan().TrimStart();
            if (!firstNonWhitespace.IsEmpty && firstNonWhitespace[0] is '=' or '+' or '-' or '@')
            {
                value = "'" + value;
            }

            builder.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }

        builder.AppendLine();
    }
}
