namespace Omrina.Core;

/// <summary>A shared paper image resource; member scores remain independent.</summary>
public sealed record MappedSchoolGroup(string GroupId, string Title, IReadOnlyList<Guid> QuestionIds,
    IReadOnlyList<int> QuestionNumbers, MappedSubjectiveRegion ImageRegion);

public static class SchoolGroupMapper
{
    public static bool IsValidGroupId(string? id) => id is { Length: > 0 and <= 80 }
        && char.IsAsciiLetterOrDigit(id[0])
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    public static IReadOnlyList<MappedSchoolGroup> Map(AnswerSheetLayout layout, PageLocationResult location,
        int imageWidth, int imageHeight)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(location);
        if (!location.CanMapRegions || location.TemplateId != layout.TemplateId
            || location.TemplateSchemaVersion != layout.SchemaVersion || location.ImageWidth != imageWidth
            || location.ImageHeight != imageHeight)
            throw new ArgumentException("页面定位或模板身份不匹配。", nameof(location));
        return MapRegistered(layout, location.Transform!.Value, imageWidth, imageHeight);
    }

    /// <summary>Maps geometry with a previously validated registration persisted with the template.</summary>
    public static IReadOnlyList<MappedSchoolGroup> MapRegistered(AnswerSheetLayout layout, PageTransform transform, int imageWidth, int imageHeight)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (imageWidth <= 0 || imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth));
        if (layout.SchemaVersion != AnswerSheetLayout.SchoolTemplateSchemaVersion)
            return layout.SubjectiveRegions.Select(q => SubjectiveTemplateMapper.MapRectangle(q.Rectangle, transform, imageWidth, imageHeight, q.QuestionId, q.QuestionNumber, q.MaximumScore)).Select(region =>
                new MappedSchoolGroup("question-" + region.QuestionNumber, "第 " + region.QuestionNumber + " 题",
                    new[] { region.QuestionId }, new[] { region.QuestionNumber }, region)).ToArray();
        return layout.SchoolGroups.Select(group =>
        {
            var members = layout.SubjectiveRegions.Where(q => group.QuestionNumbers.Contains(q.QuestionNumber)).ToArray();
            return new MappedSchoolGroup(group.GroupId, group.Title,
                members.Select(q => q.QuestionId).ToArray(), group.QuestionNumbers,
                SubjectiveTemplateMapper.MapRectangle(group.RectangleMm, transform, imageWidth, imageHeight,
                    Guid.Empty, 0, members.Sum(q => q.MaximumScore), 16_000_000));
        }).ToArray();
    }
}
