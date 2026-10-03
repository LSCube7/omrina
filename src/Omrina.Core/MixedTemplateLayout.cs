using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Omrina.Core;

public readonly record struct RectMm(double X, double Y, double Width, double Height);

public sealed class TemplateSubjectiveRegion
{
    private TemplateSubjectiveRegion(int number, decimal score, RectMm rectangle)
    {
        QuestionNumber = number;
        MaximumScore = score;
        Rectangle = rectangle;
        QuestionId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical))[..16]);
    }

    public Guid QuestionId { get; }
    public int QuestionNumber { get; }
    public decimal MaximumScore { get; }
    public RectMm Rectangle { get; }
    internal string Canonical => string.Create(CultureInfo.InvariantCulture,
        $"subjective|{QuestionNumber}|{MaximumScore:G29}|{Rectangle.X:R}|{Rectangle.Y:R}|{Rectangle.Width:R}|{Rectangle.Height:R}");

    internal static TemplateSubjectiveRegion CreateSchool(int number, decimal score, RectMm rectangle, double width)
    {
        if (rectangle.X < 22 || rectangle.Y < 38 || rectangle.Width < 40 || rectangle.Height < 10 || rectangle.X + rectangle.Width > width - 22 || rectangle.Y + rectangle.Height > 275) throw new ArgumentException("答题区域超出纸面。");
        return new TemplateSubjectiveRegion(number, score, rectangle);
    }

    public static TemplateSubjectiveRegion Create(int questionNumber, decimal maximumScore, RectMm rectangle)
    {
        if (questionNumber <= 0 || maximumScore <= 0 || maximumScore > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(questionNumber), "题号和满分必须为正，满分不得超过 1000000。");
        if (!double.IsFinite(rectangle.X) || !double.IsFinite(rectangle.Y)
            || !double.IsFinite(rectangle.Width) || !double.IsFinite(rectangle.Height)
            || rectangle.X < 10 || rectangle.Y < 52 || rectangle.Width < 40 || rectangle.Height < 10
            || rectangle.X + rectangle.Width > 200 || rectangle.Y + rectangle.Height > 275)
            throw new ArgumentOutOfRangeException(nameof(rectangle), "答题区域必须在纸面正文内，宽至少 40 毫米、高至少 10 毫米。");
        return new TemplateSubjectiveRegion(questionNumber, maximumScore, rectangle);
    }
}

public sealed partial class AnswerSheetLayout
{
    private static readonly JsonSerializerOptions TemplateJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static AnswerSheetLayout Create(string title, int questionCount, int optionsPerQuestion,
        IEnumerable<TemplateSubjectiveRegion> subjectiveRegions)
    {
        ArgumentNullException.ThrowIfNull(subjectiveRegions);
        var basis = Create(title, questionCount, optionsPerQuestion);
        var regions = subjectiveRegions.Take(65).ToArray();
        if (regions.Length == 0) return basis;
        if (regions.Length > 64 || regions.Any(region => region is null))
            throw new ArgumentException("主观题区域最多 64 个且不能为空。", nameof(subjectiveRegions));
        Array.Sort(regions, (left, right) => left.QuestionNumber.CompareTo(right.QuestionNumber));
        for (var index = 0; index < regions.Length; index++)
        {
            var region = regions[index];
            if (region.QuestionNumber <= questionCount || (index > 0 && regions[index - 1].QuestionNumber == region.QuestionNumber))
                throw new ArgumentException("主观题题号不能重复或与选择题相同。", nameof(subjectiveRegions));
            if (regions.Take(index).Any(other => Overlaps(PrintedBounds(region.Rectangle), PrintedBounds(other.Rectangle))))
                throw new ArgumentException("主观题区域不能重叠。", nameof(subjectiveRegions));
            foreach (var question in basis.Questions)
            {
                var row = new RectMm(question.NumberPosition.X - 2, question.Bubbles[0].Center.Y - 5,
                    question.Bubbles[^1].Center.X + BubbleRadiusMm + 2 - (question.NumberPosition.X - 2), 10);
                if (Overlaps(PrintedBounds(region.Rectangle), row))
                    throw new ArgumentException("主观题区域不能覆盖选择题。", nameof(subjectiveRegions));
            }
        }
        var canonical = $"answersheet-mixed|schema:2|base:{basis.TemplateId}|" + string.Join(";", regions.Select(region => region.Canonical));
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var number = string.Create(CultureInfo.InvariantCulture, $"AS2-{questionCount:D2}x{optionsPerQuestion}-{id[..8].ToUpperInvariant()}");
        return new AnswerSheetLayout(basis.Title, questionCount, optionsPerQuestion, id, number,
            basis.TemplateNumberPosition, basis.RowsPerColumn, basis.RegistrationMarks, basis.OrientationMarker,
            basis.OptionHeaders, basis.Questions, basis.Bubbles, MixedTemplateSchemaVersion, Array.AsReadOnly(regions));
    }

    private static bool Overlaps(RectMm left, RectMm right) => left.X < right.X + right.Width
        && right.X < left.X + left.Width && left.Y < right.Y + right.Height && right.Y < left.Y + left.Height;
    private static RectMm PrintedBounds(RectMm rectangle)
        => new(rectangle.X - 0.25, rectangle.Y - 0.25, rectangle.Width + 0.5, rectangle.Height + 0.5);

    public string ToJson() => SchemaVersion == 3 ? SchoolJson() : JsonSerializer.Serialize(new TemplateDocument(SchemaVersion, TemplateId, Title,
        QuestionCount, OptionsPerQuestion, SubjectiveRegions.Select(region => new RegionDocument(region.QuestionId,
            region.QuestionNumber, region.MaximumScore, region.Rectangle)).ToArray()), TemplateJsonOptions);

    public static AnswerSheetLayout FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > 64 * 1024) throw new ArgumentException("模板定义过大。", nameof(json));
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateProperties(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("模板定义必须为对象。", nameof(json));
            if (document.RootElement.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number
                && schema.TryGetInt32(out var schemaNumber) && schemaNumber == 3) return SchoolFromJson(json);
            var parsed = JsonSerializer.Deserialize<TemplateDocument>(json, TemplateJsonOptions)
                ?? throw new ArgumentException("模板定义为空。", nameof(json));
            if (parsed.SubjectiveRegions is null || parsed.SubjectiveRegions.Length > 64
                || parsed.SchemaVersion is not (TemplateSchemaVersion or MixedTemplateSchemaVersion)
                || (parsed.SchemaVersion == TemplateSchemaVersion) != (parsed.SubjectiveRegions.Length == 0))
                throw new ArgumentException("模板版本与题区定义不一致。", nameof(json));
            var regions = parsed.SubjectiveRegions.Select(region => region is null
                ? throw new ArgumentException("题区定义为空。", nameof(json))
                : TemplateSubjectiveRegion.Create(region.QuestionNumber, region.MaximumScore, region.Rectangle)).ToArray();
            for (var index = 0; index < regions.Length; index++)
                if (regions[index].QuestionId != parsed.SubjectiveRegions[index].QuestionId)
                    throw new ArgumentException("题区身份不匹配。", nameof(json));
            var layout = Create(parsed.Title, parsed.QuestionCount, parsed.OptionsPerQuestion, regions);
            if (layout.TemplateId != parsed.TemplateId || layout.Title != parsed.Title)
                throw new ArgumentException("模板身份不匹配。", nameof(json));
            return layout;
        }
        catch (JsonException exception) { throw new ArgumentException("模板定义无效。", nameof(json), exception); }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("模板包含重复字段。");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private sealed record TemplateDocument(
        [property: JsonRequired] int SchemaVersion, [property: JsonRequired] string TemplateId,
        [property: JsonRequired] string Title, [property: JsonRequired] int QuestionCount,
        [property: JsonRequired] int OptionsPerQuestion, [property: JsonRequired] RegionDocument[] SubjectiveRegions);
    private sealed record RegionDocument([property: JsonRequired] Guid QuestionId,
        [property: JsonRequired] int QuestionNumber, [property: JsonRequired] decimal MaximumScore,
        [property: JsonRequired] RectMm Rectangle);
}
