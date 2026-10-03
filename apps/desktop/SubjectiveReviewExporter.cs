using System.Globalization;
using System.Text;
using System.Text.Json;
using Omrina.Core;
using Omrina.Protocol;

namespace Omrina.Desktop;

/// <summary>Pure wire/export formatting shared by the local service and grant adapter.</summary>
public static class SubjectiveReviewExporter
{
    public static object ToWireDocument(Guid reviewId, SubjectiveGradingSnapshot snapshot, SubjectiveReviewTemplateMapping? mapping = null, CaptureManifest? capture = null, SubjectiveReviewIdentity? identity = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (reviewId == Guid.Empty)
        {
            throw new ArgumentException("ReviewId must be a non-empty GUID.", nameof(reviewId));
        }

        var groups = GetGroups(snapshot, mapping);
        var schoolMetadata = capture?.SchoolMetadata ?? (mapping is null ? null : AnswerSheetLayout.FromJson(mapping.TemplateJson).SchoolMetadata);
        var resolvedIdentity = identity ?? new SubjectiveReviewIdentity(capture?.CandidateId, capture?.IdentityStatus);
        return new
        {
            reviewId,
            captureId = snapshot.CaptureId,
            version = snapshot.Version,
            schoolMetadata,
            candidateId = resolvedIdentity.CandidateId,
            identityStatus = resolvedIdentity.IdentityStatus ?? (schoolMetadata is null ? null : "RequireAssociation"),
            groups = groups.Select(group => new { group.GroupId, group.Title, group.QuestionIds, group.QuestionNumbers, maxScore = group.MaximumScore, group.ProvisionalSubtotal, group.FinalSubtotal, group.Status, group.RectangleMm }).ToArray(),
            questions = snapshot.Questions.Select(question => new
            {
                imageGroupId = groups.FirstOrDefault(g => g.QuestionIds.Contains(question.QuestionId))?.GroupId,
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

    public static string ExportJson(Guid reviewId, SubjectiveGradingSnapshot snapshot, SubjectiveReviewTemplateMapping? mapping = null, SubjectiveReviewIdentity? identity = null)
    {
        return JsonSerializer.Serialize(ToWireDocument(reviewId, snapshot, mapping, identity: identity), AgentJson.Options);
    }

    public static string ExportCsv(SubjectiveGradingSnapshot snapshot, SubjectiveReviewTemplateMapping? mapping = null, SubjectiveReviewIdentity? identity = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var groups = GetGroups(snapshot, mapping);
        var identityStatus = identity?.IdentityStatus
            ?? (mapping is not null && AnswerSheetLayout.FromJson(mapping.TemplateJson).SchoolMetadata is not null ? "RequireAssociation" : null);
        var builder = new StringBuilder();
        builder.AppendLine("questionId,questionNumber,maxScore,status,score,comment,reviewer,confirmedAtUtc,imageGroupId,imageGroupTitle,groupStatus,groupProvisionalSubtotal,groupFinalSubtotal,candidateId,identityStatus");
        foreach (var question in snapshot.Questions)
        {
            var group = groups.FirstOrDefault(g => g.QuestionIds.Contains(question.QuestionId));
            AppendCsvRow(builder,
                question.QuestionId.ToString("D"),
                question.QuestionNumber.ToString(CultureInfo.InvariantCulture),
                question.MaximumScore.ToString(CultureInfo.InvariantCulture),
                ToWireStatus(question.Status),
                question.Score?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                question.Comment ?? string.Empty,
                question.Reviewer ?? string.Empty,
                question.ConfirmedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                group?.GroupId ?? string.Empty, group?.Title ?? string.Empty, group?.Status ?? string.Empty,
                group?.ProvisionalSubtotal.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                group?.FinalSubtotal?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                identity?.CandidateId ?? string.Empty, identityStatus ?? string.Empty);
        }

        return builder.ToString();
    }

    public static IReadOnlyList<SubjectiveReviewGroup> GetGroups(SubjectiveGradingSnapshot snapshot, SubjectiveReviewTemplateMapping? mapping)
    {
        if (mapping is null) return Array.Empty<SubjectiveReviewGroup>();
        var layout = AnswerSheetLayout.FromJson(mapping.TemplateJson);
        return SchoolGroupMapper.MapRegistered(layout, mapping.PageTransform, snapshot.ImageWidth, snapshot.ImageHeight)
            .Where(g => g.QuestionIds.Count > 0).Select(g =>
            {
                var members = snapshot.Questions.Where(q => g.QuestionIds.Contains(q.QuestionId)).ToArray();
                var final = members.Length == g.QuestionIds.Count && members.All(q => q.Status == SubjectiveReviewStatus.Confirmed);
                var subtotal = members.Sum(q => q.Score ?? 0);
                return new SubjectiveReviewGroup(g.GroupId, g.Title, g.QuestionIds,
                    members.Select(q => q.QuestionNumber).ToArray(), members.Sum(q => q.MaximumScore), subtotal,
                    final ? subtotal : null, final ? "final" : "provisional", g.ImageRegion.RectangleMm);
            }).ToArray();
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

public sealed record SubjectiveReviewGroup(string GroupId, string Title, IReadOnlyList<Guid> QuestionIds,
    IReadOnlyList<int> QuestionNumbers, decimal MaximumScore, decimal ProvisionalSubtotal,
    decimal? FinalSubtotal, string Status, RectMm RectangleMm);

/// <summary>Validated capture identity supplied by the caller; no identity is inferred from grades.</summary>
public sealed record SubjectiveReviewIdentity(string? CandidateId, string? IdentityStatus);
