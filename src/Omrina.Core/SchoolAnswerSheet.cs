using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace Omrina.Core;

public enum SchoolPaper { A4Portrait, A3Landscape }
public enum SchoolContentMode { AnswerOnly, WithQuestions }
public enum SchoolQuestionType { Choice, Subjective }
public enum BubbleShape { Circle, Rectangle }
public enum LabelPlacement { Inside, Outside }
public enum CandidateIdentityMode { Barcode, Marking }
public enum SchoolPageSide { Front, Back }
public sealed record SchoolCandidateIdentity([property: JsonRequired] CandidateIdentityMode Mode = CandidateIdentityMode.Barcode,
    [property: JsonRequired] int Digits = 8, [property: JsonRequired] string? CandidateId = null);
public sealed record SchoolQuestionDefinition([property: JsonRequired] int Number, [property: JsonRequired] SchoolQuestionType Type,
    [property: JsonRequired] decimal MaximumScore = 1, [property: JsonRequired] string Body = "",
    [property: JsonRequired] IReadOnlyList<string>? Options = null, [property: JsonRequired] double SubjectiveHeightMm = 30);
public sealed record SchoolQuestionGroupDefinition([property: JsonRequired] string Id, [property: JsonRequired] string Title,
    [property: JsonRequired] IReadOnlyList<int> QuestionNumbers, [property: JsonRequired] int ChoiceColumns = 1);
public enum SchoolLayoutOrder { Mixed, Separated }
public sealed record SchoolQuestionGroupGeometry(string GroupId, string Title, IReadOnlyList<int> QuestionNumbers, RectMm RectangleMm, int ChoiceColumns = 1);
public sealed record SchoolSheetDefinition
{
    [JsonRequired] public string ExamId { get; init; } = "";
    [JsonRequired] public string LayoutDocumentId { get; init; } = "";
    [JsonRequired] public int Version { get; init; } = 1;
    [JsonRequired] public string Title { get; init; } = "答题纸";
    [JsonRequired] public SchoolPaper Paper { get; init; } = SchoolPaper.A4Portrait;
    [JsonRequired] public SchoolContentMode Mode { get; init; } = SchoolContentMode.AnswerOnly;
    [JsonRequired] public int Columns { get; init; } = 1;
    [JsonRequired] public BubbleShape BubbleShape { get; init; } = BubbleShape.Circle;
    [JsonRequired] public LabelPlacement LabelPlacement { get; init; } = LabelPlacement.Outside;
    [JsonRequired] public double BubbleWidthMm { get; init; } = 1.8;
    [JsonRequired] public double BubbleHeightMm { get; init; } = 1.8;
    [JsonRequired] public SchoolCandidateIdentity CandidateIdentity { get; init; } = new();
    [JsonRequired] public bool Duplex { get; init; }
    [JsonRequired] public bool RepeatBackIdentity { get; init; } = true;
    [JsonRequired] public IReadOnlyList<SchoolQuestionDefinition> Questions { get; init; } = Array.Empty<SchoolQuestionDefinition>();
    [JsonRequired] public IReadOnlyList<SchoolQuestionGroupDefinition> Groups { get; init; } = Array.Empty<SchoolQuestionGroupDefinition>();
    [JsonRequired] public SchoolLayoutOrder LayoutOrder { get; init; } = SchoolLayoutOrder.Mixed;
}
public sealed record SchoolPageMetadata(string ExamId, string LayoutDocumentId, int Version, int PageNumber, SchoolPageSide Side, string TemplateId);
public sealed record SchoolPrintedText(PointMm Position, string Text, double FontSizeMm);
public sealed record CandidateDigitGeometry(int Position, IReadOnlyList<AnswerBubble> Bubbles);
public sealed class SchoolAnswerSheet
{
    private SchoolAnswerSheet(IReadOnlyList<AnswerSheetLayout> pages) => Pages = pages;
    public IReadOnlyList<AnswerSheetLayout> Pages { get; }
    public static SchoolAnswerSheet Create(SchoolSheetDefinition definition) => new(AnswerSheetLayout.CreateSchoolPages(definition));
}

public sealed partial class AnswerSheetLayout
{
    public const int SchoolTemplateSchemaVersion = 3;
    public double WidthMm { get; private set; } = PageWidthMm;
    public double HeightMm { get; private set; } = PageHeightMm;
    public SchoolSheetDefinition? SchoolDefinition { get; private set; }
    public int SchoolPageIndex { get; private set; }
    public SchoolPageMetadata? SchoolMetadata { get; private set; }
    public RectMm? SchoolInformationArea { get; private set; }
    public RectMm? CandidateArea { get; private set; }
    public RectMm? ExamCodeArea { get; private set; }
    public IReadOnlyList<CandidateDigitGeometry> CandidateDigits { get; private set; } = Array.Empty<CandidateDigitGeometry>();
    public IReadOnlyList<SchoolPrintedText> SchoolTexts { get; private set; } = Array.Empty<SchoolPrintedText>();
    public IReadOnlyList<SchoolQuestionGroupGeometry> SchoolGroups { get; private set; } = Array.Empty<SchoolQuestionGroupGeometry>();
    private static readonly JsonSerializerOptions SchoolJsonOptions = new(TemplateJsonOptions) { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
    private sealed record SchoolDocument([property: JsonRequired] int SchemaVersion, [property: JsonRequired] string TemplateId, [property: JsonRequired] SchoolSheetDefinition Definition, [property: JsonRequired] int PageIndex);
    private string SchoolJson() => JsonSerializer.Serialize(new SchoolDocument(3, TemplateId, SchoolDefinition!, SchoolPageIndex), SchoolJsonOptions);
    private static AnswerSheetLayout SchoolFromJson(string json)
    {
        var parsed = JsonSerializer.Deserialize<SchoolDocument>(json, SchoolJsonOptions) ?? throw new ArgumentException("模板定义为空。");
        if (parsed.SchemaVersion != 3 || parsed.Definition is null || parsed.PageIndex < 0) throw new ArgumentException("答题纸版本或页码无效。");
        var pages = CreateSchoolPages(parsed.Definition);
        if (parsed.PageIndex >= pages.Count || pages[parsed.PageIndex].TemplateId != parsed.TemplateId) throw new ArgumentException("答题纸身份不匹配。");
        return pages[parsed.PageIndex];
    }
    internal static IReadOnlyList<AnswerSheetLayout> CreateSchoolPages(SchoolSheetDefinition input)
        => CreateSchoolPagesCore(input);

    private static bool TryNormalizeTenthMillimeter(double value, out double normalized, out double tenths)
    {
        normalized = 0;
        tenths = 0;
        if (!double.IsFinite(value) || value <= 0) return false;
        var scaled = value * 10;
        if (!double.IsFinite(scaled)) return false;
        var rounded = Math.Round(scaled, MidpointRounding.AwayFromZero);
        if (rounded < 1 || rounded > 9007199254740991d || Math.Abs(scaled - rounded) > 1e-9) return false;
        tenths = rounded;
        normalized = rounded / 10;
        return true;
    }

    private sealed record PreparedSchoolQuestion(SchoolQuestionDefinition Definition, IReadOnlyList<string> BodyLines, double AnswerHeight, double TotalHeight, bool CompactChoice);
    private sealed record PreparedSchoolQuestionGroup(SchoolQuestionGroupDefinition Definition, IReadOnlyList<PreparedSchoolQuestion> Questions, double HeaderHeight, double TotalHeight);

    private static IReadOnlyList<AnswerSheetLayout> CreateSchoolPagesCore(SchoolSheetDefinition input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.ExamId) || input.ExamId.Length > 80 || string.IsNullOrWhiteSpace(input.LayoutDocumentId) || input.LayoutDocumentId.Length > 80 || input.Version < 1 || string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 80
            || !Enum.IsDefined(input.Paper) || !Enum.IsDefined(input.Mode) || !Enum.IsDefined(input.LayoutOrder) || !Enum.IsDefined(input.BubbleShape) || !Enum.IsDefined(input.LabelPlacement) || (input.Paper == SchoolPaper.A4Portrait ? input.Columns != 1 : input.Columns is not (2 or 3)))
            throw new ArgumentException("考试标识、版本、标题或纸张栏数无效。");
        var normalizedWidth = TryNormalizeTenthMillimeter(input.BubbleWidthMm, out var bubbleWidth, out var widthTenths);
        var normalizedHeight = TryNormalizeTenthMillimeter(input.BubbleHeightMm, out var bubbleHeight, out var heightTenths);
        if (!normalizedWidth || !normalizedHeight || heightTenths > 20
                || (input.BubbleShape == BubbleShape.Circle && widthTenths > 20))
            throw new ArgumentException("尺寸须按 0.1 毫米递增；圆框直径和矩形高度须在 0.1–2 毫米之间。");
        var identity = input.CandidateIdentity;
        if (identity is null || !Enum.IsDefined(identity.Mode) || identity.Digits is < 1 or > 20 || (identity.CandidateId is not null && (identity.CandidateId.Length != identity.Digits || identity.CandidateId.Any(c => c is < '0' or > '9'))))
            throw new ArgumentException("考号位数须为 1–20，预印考号须为对应位数的数字。");
        if (input.Questions is null || input.Questions.Count is < 1 or > 500 || input.Questions.Any(q => q is null)) throw new ArgumentException("题目数量须为 1–500。");
        var questions = input.Questions.Select(q => q with { Options = q.Options is null ? null : Array.AsReadOnly(q.Options.ToArray()) }).ToArray();
        if (questions.Select(q => q.Number).Distinct().Count() != questions.Length) throw new ArgumentException("题号不能重复。");
        foreach (var q in questions)
            if (q.Number <= 0 || !Enum.IsDefined(q.Type) || q.MaximumScore is <= 0 or > 1000000 || q.Body is null || q.Body.Length > 8000 || (q.Type == SchoolQuestionType.Choice && (q.Options is null || q.Options.Count is < 2 or > 6 || q.Options.Any(o => o is null || o.Length > 1000))) || (q.Type == SchoolQuestionType.Subjective && (!double.IsFinite(q.SubjectiveHeightMm) || q.SubjectiveHeightMm is < 10 or > 230)))
                throw new ArgumentException($"第 {q.Number} 题定义无效或答题区过大。");
        var groups = NormalizeSchoolGroups(input.Groups, questions, input.Mode);
        var definition = input with
        {
            Questions = Array.AsReadOnly(questions),
            Groups = Array.AsReadOnly(groups),
            BubbleWidthMm = bubbleWidth,
            BubbleHeightMm = bubbleHeight
        };
        var canonical = JsonSerializer.Serialize(definition, SchoolJsonOptions);
        if (Encoding.UTF8.GetByteCount(canonical) > 60000) throw new ArgumentException("答题纸定义过大，请减少正文或题数。");
        var documentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("school|3|" + canonical))).ToLowerInvariant();
        var width = definition.Paper == SchoolPaper.A4Portrait ? 210d : 420d;
        ValidatePrintableText(definition.Title);
        if ((width - 44) / definition.Title.Length < 3 || definition.Title.IndexOfAny(['\n','\r','\t']) >= 0)
            throw new ArgumentException("考试标题过长或包含换行，请使用简短的单行标题。");
        foreach (var question in questions)
        {
            ValidatePrintableText(question.Body);
            foreach (var option in question.Options ?? Array.Empty<string>()) ValidatePrintableText(option);
        }
        foreach (var group in groups) ValidatePrintableText(group.Title);
        const double left = 22, top = 38, bottom = 275, gap = 8;
        var columnWidth = (width - left * 2 - gap * (definition.Columns - 1)) / definition.Columns;
        var wideBubble = definition.BubbleWidthMm > 2;
        var bubbleWidthMm = definition.BubbleWidthMm;
        var bubbleHeightMm = definition.BubbleHeightMm;
        var candidatePitch = wideBubble ? bubbleWidthMm + 3 : 6;
        var candidateStart = wideBubble ? Math.Max(6, bubbleWidthMm / 2 + 2) : 6;
        var candidateLabelSpace = definition.LabelPlacement == LabelPlacement.Outside ? 2 : 0;
        if (identity.Mode == CandidateIdentityMode.Marking)
        {
            var requiredCandidateWidth = wideBubble
                ? candidateStart + (identity.Digits - 1) * candidatePitch + bubbleWidthMm / 2 + candidateLabelSpace + 4
                : identity.Digits * 6 + 8;
            if (requiredCandidateWidth > columnWidth)
                throw new ArgumentException("考号填涂框超过当前栏宽，请减少位数或矩形宽度，或使用条码。");
        }
        var choicePitch = wideBubble ? bubbleWidthMm + 3 : 12;
        var contentWidth = columnWidth - 8;
        var choiceColumnsByQuestion = groups
            .SelectMany(group => group.QuestionNumbers.Select(number => (number, group.ChoiceColumns)))
            .ToDictionary(item => item.number, item => item.ChoiceColumns);
        foreach (var question in questions.Where(question => question.Type == SchoolQuestionType.Choice))
        {
            var choiceStart = wideBubble ? Math.Max(10, bubbleWidthMm / 2 + 2) : 10;
            var labelSpace = definition.LabelPlacement == LabelPlacement.Outside ? 2.4 : 0;
            var compactChoice = definition.Mode == SchoolContentMode.AnswerOnly;
            var blockWidth = compactChoice ? contentWidth / choiceColumnsByQuestion[question.Number] : contentWidth;
            var optionSpan = (question.Options!.Count - 1) * choicePitch + bubbleWidthMm / 2 + labelSpace;
            var rowStart = compactChoice ? blockWidth - optionSpan : choiceStart;
            var requiredChoiceWidth = choiceStart + optionSpan;
            if (rowStart - bubbleWidthMm / 2 < 0 || (!compactChoice && requiredChoiceWidth > contentWidth))
                throw new ArgumentException($"第 {question.Number} 题的填涂框或选项标签超出题号块宽度，请减少题号块数或选项数，或减小填涂框宽度。");
            if (compactChoice && rowStart - bubbleWidthMm / 2 < EstimateSchoolTextWidth($"{question.Number}. ({question.MaximumScore.ToString(CultureInfo.InvariantCulture)} 分)") + 2)
                throw new ArgumentException($"第 {question.Number} 题的题号和选项填涂框无法在紧凑行内排开，请减少选项数或缩短分值格式。");
        }
        var exampleRight = wideBubble ? 22 + bubbleWidthMm + 3 + 5 * 2.8 : 41;
        if (exampleRight > columnWidth - 30)
            throw new ArgumentException("填涂示例放不进信息栏，请减小矩形宽度。");
        var questionByNumber = questions.ToDictionary(question => question.Number);
        var orderedGroups = groups.Select(group =>
        {
            var groupQuestions = group.QuestionNumbers.Select(number =>
            {
                var question = questionByNumber[number];
                var lines = definition.Mode == SchoolContentMode.WithQuestions ? WrapSchoolText(question.Body, contentWidth).ToList() : new List<string>();
                if (definition.Mode == SchoolContentMode.WithQuestions && question.Type == SchoolQuestionType.Choice)
                    for (var option = 0; option < question.Options!.Count; option++) lines.AddRange(WrapSchoolText($"{(char)('A' + option)}. {question.Options[option]}", contentWidth));
                var compactChoice = definition.Mode == SchoolContentMode.AnswerOnly && question.Type == SchoolQuestionType.Choice;
                var answerHeight = question.Type == SchoolQuestionType.Subjective
                    ? question.SubjectiveHeightMm
                    : compactChoice ? Math.Max(2, bubbleHeightMm) : Math.Max(8, bubbleHeightMm + 4);
                var totalHeight = compactChoice ? 6 : 7 + lines.Count * 4.5 + answerHeight + 5;
                return new PreparedSchoolQuestion(question, Array.AsReadOnly(lines.ToArray()), answerHeight, totalHeight, compactChoice);
            }).ToArray();
            var titleLines = WrapSchoolText(group.Title, contentWidth).ToArray();
            var headerHeight = 1 + titleLines.Length * 4.5;
            var compactChoiceGroup = groupQuestions[0].CompactChoice;
            var questionHeight = compactChoiceGroup
                ? Math.Ceiling((double)groupQuestions.Length / group.ChoiceColumns) * 6
                : groupQuestions.Sum(question => question.TotalHeight);
            return new PreparedSchoolQuestionGroup(group, Array.AsReadOnly(groupQuestions), headerHeight, headerHeight + questionHeight + 1);
        }).ToArray();
        if (definition.LayoutOrder == SchoolLayoutOrder.Separated)
            orderedGroups = orderedGroups.OrderBy(group => questionByNumber[group.Definition.QuestionNumbers[0]].Type).ToArray();
        var firstColumnStart = top + (definition.CandidateIdentity.Mode == CandidateIdentityMode.Marking ? 82 : 46) + 5;
        var regularColumnHeight = bottom - top;
        var firstColumnHeight = bottom - firstColumnStart;
        foreach (var group in orderedGroups)
        {
            var largestAvailableColumn = definition.Columns > 1 ? regularColumnHeight : firstColumnHeight;
            if (group.TotalHeight > largestAvailableColumn)
                throw new ArgumentException($"题组“{group.Definition.Title}”超过完整栏高，无法排版。");
        }

        var pages = new List<AnswerSheetLayout>(); var groupIndex = 0;
        while (groupIndex < orderedGroups.Length)
        {
            if (pages.Count >= 64) throw new ArgumentException("答题纸不得超过 64 页。");
            var pageIndex = pages.Count;
            var side = definition.Duplex && pageIndex % 2 == 1 ? SchoolPageSide.Back : SchoolPageSide.Front;
            var showIdentity = side == SchoolPageSide.Front || definition.RepeatBackIdentity;
            var infoHeight = showIdentity ? (identity.Mode == CandidateIdentityMode.Marking ? 82d : 46d) : 32d;
            var info = new RectMm(left, top, columnWidth, infoHeight);
            var digits = new List<CandidateDigitGeometry>(); RectMm? candidateArea = null;
            if (showIdentity)
            {
                candidateArea = new RectMm(left + 4, top + 28, columnWidth - 8, infoHeight - 30);
                if (identity.Mode == CandidateIdentityMode.Marking)
                    for (var position = 0; position < identity.Digits; position++)
                        digits.Add(new(position + 1, Array.AsReadOnly(Enumerable.Range(0, 10).Select(digit => NewBubble(-(position + 1), digit + 1, digit.ToString(CultureInfo.InvariantCulture), new(left + candidateStart + position * candidatePitch, top + 33 + digit * 4.5), definition)).ToArray())));
            }
            var texts = new List<SchoolPrintedText>(); var geometry = new List<QuestionGeometry>(); var bubbles = new List<AnswerBubble>(); var regions = new List<TemplateSubjectiveRegion>();
            var schoolGroups = new List<SchoolQuestionGroupGeometry>(); var placedAnyGroup = false;
            for (var column = 0; column < definition.Columns && groupIndex < orderedGroups.Length; column++)
            {
                var x = left + column * (columnWidth + gap); var y = column == 0 ? top + infoHeight + 5 : top; var row = 0;
                while (groupIndex < orderedGroups.Length)
                {
                    var group = orderedGroups[groupIndex];
                    if (y + group.TotalHeight > bottom) break;
                    var groupRectangle = new RectMm(x, y, columnWidth, group.TotalHeight);
                    var groupGeometry = new SchoolQuestionGroupGeometry(group.Definition.Id, group.Definition.Title,
                        Array.AsReadOnly(group.Definition.QuestionNumbers.ToArray()), groupRectangle, group.Definition.ChoiceColumns);
                    schoolGroups.Add(groupGeometry);
                    var titleLines = WrapSchoolText(group.Definition.Title, contentWidth).ToArray();
                    for (var line = 0; line < titleLines.Length; line++) texts.Add(new(new(x + 4, y + 3 + line * 4.5), titleLines[line], 3));
                    var questionY = y + group.HeaderHeight;
                    var compactBlockWidth = contentWidth / group.Definition.ChoiceColumns;
                    for (var memberIndex = 0; memberIndex < group.Questions.Count; memberIndex++)
                    {
                        var prepared = group.Questions[memberIndex];
                        var q = prepared.Definition;
                        var compactColumn = prepared.CompactChoice ? memberIndex % group.Definition.ChoiceColumns : 0;
                        var compactRow = prepared.CompactChoice ? memberIndex / group.Definition.ChoiceColumns : 0;
                        var memberX = x + 4 + (prepared.CompactChoice ? compactColumn * compactBlockWidth : 0);
                        var memberY = questionY + (prepared.CompactChoice ? compactRow * 6 : 0);
                        texts.Add(new(new(memberX, memberY + (prepared.CompactChoice ? 4.5 : 4)), $"{q.Number}. ({q.MaximumScore.ToString(CultureInfo.InvariantCulture)} 分)", 3));
                        for (var line = 0; line < prepared.BodyLines.Count; line++) texts.Add(new(new(memberX, memberY + 9 + line * 4.5), prepared.BodyLines[line], 3));
                        var bodyHeight = prepared.BodyLines.Count * 4.5;
                        var answerY = memberY + (prepared.CompactChoice ? 2 : 7 + bodyHeight);
                        if (q.Type == SchoolQuestionType.Subjective)
                            regions.Add(TemplateSubjectiveRegion.CreateSchool(q.Number, q.MaximumScore, new(x, answerY, columnWidth, prepared.AnswerHeight), width));
                        else
                        {
                            var baseChoiceStart = wideBubble ? Math.Max(10, bubbleWidthMm / 2 + 2) : 10;
                            var labelSpace = definition.LabelPlacement == LabelPlacement.Outside ? 2.4 : 0;
                            var choiceStart = prepared.CompactChoice
                                ? compactColumn * compactBlockWidth + compactBlockWidth - ((q.Options!.Count - 1) * choicePitch + bubbleWidthMm / 2 + labelSpace)
                                : baseChoiceStart;
                            var bubbleCenterY = prepared.CompactChoice ? memberY + 3 : answerY + prepared.AnswerHeight / 2;
                            var choices = Enumerable.Range(0, q.Options!.Count).Select(option => NewBubble(q.Number, option + 1, ((char)('A' + option)).ToString(), new(x + 4 + choiceStart + option * choicePitch, bubbleCenterY), definition)).ToArray();
                            geometry.Add(new(q.Number, column + 1, ++row, new(memberX, answerY + prepared.AnswerHeight / 2), Array.AsReadOnly(choices))); bubbles.AddRange(choices);
                        }
                        if (!prepared.CompactChoice)
                        {
                            questionY += prepared.TotalHeight;
                        }
                    }
                    if (group.Questions[0].CompactChoice)
                        questionY += Math.Ceiling((double)group.Questions.Count / group.Definition.ChoiceColumns) * 6;
                    y += group.TotalHeight;
                    groupIndex++;
                    placedAnyGroup = true;
                }
            }
            if (!placedAnyGroup) throw new ArgumentException($"题组“{orderedGroups[groupIndex].Definition.Title}”无法放入当前纸张栏位。");
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{documentHash}|page:{pageIndex}"))).ToLowerInvariant();
            var metadata = new SchoolPageMetadata(definition.ExamId, definition.LayoutDocumentId, definition.Version, pageIndex + 1, side, id);
            var codeSize = SchoolMachineCode.ExamCodeSizeMm(metadata);
            const double reservedCodeSizeMm = 24;
            if (codeSize.WidthMm > reservedCodeSizeMm || codeSize.HeightMm > reservedCodeSizeMm)
                throw new ArgumentException("页面 Data Matrix 编码密度超过信息区预留空间，无法按最小模块尺寸排版。");
            var codeArea = new RectMm(
                left + columnWidth - 16 - codeSize.WidthMm / 2,
                top + 15 - codeSize.HeightMm / 2,
                codeSize.WidthMm,
                codeSize.HeightMm);
            var marks = new[] { new RegistrationMark("registration-top-left",new(10,10),8), new RegistrationMark("registration-top-right",new(width-18,10),8), new RegistrationMark("registration-bottom-left",new(10,279),8), new RegistrationMark("registration-bottom-right",new(width-18,279),8) };
            var layout = new AnswerSheetLayout(definition.Title, geometry.Count, geometry.Count == 0 ? 2 : geometry.Max(q => q.Bubbles.Count),id,$"AS3-{pageIndex+1}-{id[..8]}",new(width/2,21),geometry.Count,Array.AsReadOnly(marks),new("orientation",new(width/2-2,10),4,4),Array.Empty<OptionHeader>(),geometry.AsReadOnly(),bubbles.AsReadOnly(),3,regions.AsReadOnly())
            { WidthMm=width, SchoolDefinition=definition, SchoolPageIndex=pageIndex, SchoolMetadata=metadata, SchoolInformationArea=info, CandidateArea=candidateArea, ExamCodeArea=codeArea, CandidateDigits=digits.AsReadOnly(), SchoolTexts=texts.AsReadOnly(), SchoolGroups=schoolGroups.AsReadOnly() };
            pages.Add(layout);
        }
        return pages.AsReadOnly();
    }

    private static SchoolQuestionGroupDefinition[] NormalizeSchoolGroups(
        IReadOnlyList<SchoolQuestionGroupDefinition>? inputGroups,
        IReadOnlyList<SchoolQuestionDefinition> questions,
        SchoolContentMode mode)
    {
        if (inputGroups is null) throw new ArgumentException("题组列表不能为空。");
        if (inputGroups.Count == 0)
            return questions.Select(question => new SchoolQuestionGroupDefinition($"question-{question.Number}", $"第 {question.Number} 题", Array.AsReadOnly(new[] { question.Number }))).ToArray();
        if (inputGroups.Count > questions.Count || inputGroups.Any(group => group is null)) throw new ArgumentException("题组数量或定义无效。");

        var questionByNumber = questions.ToDictionary(question => question.Number);
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        var coveredQuestions = new HashSet<int>();
        var normalizedGroups = new SchoolQuestionGroupDefinition[inputGroups.Count];
        for (var groupIndex = 0; groupIndex < inputGroups.Count; groupIndex++)
        {
            var group = inputGroups[groupIndex];
            if (!IsValidSchoolGroupId(group.Id) || !groupIds.Add(group.Id) || string.IsNullOrWhiteSpace(group.Title) || group.Title.Length > 80
                || group.ChoiceColumns is < 1 or > 3
                || group.Title.IndexOfAny(['\n', '\r', '\t']) >= 0 || group.QuestionNumbers is null || group.QuestionNumbers.Count is < 1 or > 500)
                throw new ArgumentException("题组 ID、标题、客观题块数或成员无效。");

            SchoolQuestionType? groupType = null;
            var memberNumbers = group.QuestionNumbers.ToArray();
            foreach (var questionNumber in memberNumbers)
            {
                if (!questionByNumber.TryGetValue(questionNumber, out var question) || !coveredQuestions.Add(questionNumber))
                    throw new ArgumentException("题组成员必须是题目中的唯一题号，且所有题目只能属于一个题组。");
                if (groupType is not null && groupType.Value != question.Type)
                    throw new ArgumentException($"题组“{group.Title}”不能混合客观题和主观题。");
                groupType = question.Type;
            }
            if (group.ChoiceColumns > 1 && (mode != SchoolContentMode.AnswerOnly || groupType != SchoolQuestionType.Choice))
                throw new ArgumentException("多个题号块仅支持不含题目正文的客观题组；主观题组和含正文模式必须使用一个题号块。");
            normalizedGroups[groupIndex] = group with { QuestionNumbers = Array.AsReadOnly(memberNumbers) };
        }
        if (coveredQuestions.Count != questions.Count) throw new ArgumentException("题组成员必须完整覆盖所有题目。");
        return normalizedGroups;
    }

    private static bool IsValidSchoolGroupId(string? id)
        => id is { Length: >= 1 and <= 80 }
            && (id[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
            && id.Skip(1).All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static double EstimateSchoolTextWidth(string text)
        => text.EnumerateRunes().Sum(rune => rune.Value >= 0x2e80 ? 3d : char.IsWhiteSpace((char)rune.Value) ? 1.2d : 1.7d);
    private static AnswerBubble NewBubble(int question,int option,string label,PointMm center,SchoolSheetDefinition definition)
        => new(question,option,label,center,definition.BubbleWidthMm/2) { Shape=definition.BubbleShape, HeightMm=definition.BubbleShape==BubbleShape.Circle?definition.BubbleWidthMm:definition.BubbleHeightMm, LabelPlacement=definition.LabelPlacement };
    private static IEnumerable<string> WrapSchoolText(string text,double width)
    {
        var capacity = (int)Math.Floor(width / 3.2);
        foreach (var paragraph in text.Replace("\r", "").Replace("\t", "    ").Split('\n'))
        {
            if (paragraph.Length == 0) { if (text.Length != 0) yield return ""; continue; }
            var characters = paragraph.EnumerateRunes().Select(character => character.ToString()).ToArray();
            for (var offset = 0; offset < characters.Length; offset += capacity)
                yield return string.Concat(characters.Skip(offset).Take(capacity));
        }
    }
    private static void ValidatePrintableText(string text)
    {
        try { XmlConvert.VerifyXmlChars(text); }
        catch (XmlException exception) { throw new ArgumentException("题目或标题含有无法打印的字符。", exception); }
    }
    private string SchoolSvg()
    {
        static string F(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
        static string E(string value) => SecurityElement.Escape(value)!;
        var svg = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(WidthMm)}mm\" height=\"{F(HeightMm)}mm\" viewBox=\"0 0 {F(WidthMm)} {F(HeightMm)}\"><rect x=\"0\" y=\"0\" width=\"{F(WidthMm)}\" height=\"{F(HeightMm)}\" fill=\"white\"/>");
        void Line(double x1, double y1, double x2, double y2) => svg.Append($"<line x1=\"{F(x1)}\" y1=\"{F(y1)}\" x2=\"{F(x2)}\" y2=\"{F(y2)}\" stroke=\"black\" stroke-width=\"0.2\"/>");
        void Rect(RectMm r,string fill="none",double stroke=.2) => svg.Append($"<rect x=\"{F(r.X)}\" y=\"{F(r.Y)}\" width=\"{F(r.Width)}\" height=\"{F(r.Height)}\" fill=\"{fill}\" stroke=\"black\" stroke-width=\"{F(stroke)}\"/>");
        void Text(PointMm p,string text,double size,string anchor="start") => svg.Append($"<text x=\"{F(p.X)}\" y=\"{F(p.Y)}\" font-family=\"sans-serif\" font-size=\"{F(size)}\" text-anchor=\"{anchor}\">{E(text)}</text>");
        foreach(var mark in RegistrationMarks) Rect(new(mark.TopLeft.X,mark.TopLeft.Y,mark.SizeMm,mark.SizeMm),"black",0);
        Rect(new(OrientationMarker.TopLeft.X,OrientationMarker.TopLeft.Y,OrientationMarker.WidthMm,OrientationMarker.HeightMm),"black",0);
        Text(new(WidthMm/2,29),Title,Math.Min(6,(WidthMm-44)/Title.Length),"middle"); Text(new(WidthMm/2,21),TemplateNumber,2.5,"middle");
        var info=SchoolInformationArea!.Value; Rect(info);
        var sideLabel = SchoolMetadata!.Side == SchoolPageSide.Front ? "正面" : "反面";
        Text(new(info.X+3,info.Y+6),$"第 {SchoolPageIndex+1} 页 · {sideLabel}",2.8);
        if(CandidateArea is not null) { Text(new(info.X+3,info.Y+12),"姓名：________ 班级：________",2.8); Text(new(info.X+3,info.Y+18),"考号：________ 缺考：□",2.8); }
        Text(new(info.X+3,info.Y+24),"填涂示例：",2.8);
        var exampleWidth = SchoolDefinition!.BubbleWidthMm;
        var exampleHeight = SchoolDefinition.BubbleShape == BubbleShape.Circle ? exampleWidth : SchoolDefinition.BubbleHeightMm;
        var wideExample = exampleWidth > 2;
        var exampleCenter = new PointMm(info.X + (wideExample ? 22 + exampleWidth / 2 : 23), info.Y+23);
        if (SchoolDefinition.BubbleShape == BubbleShape.Circle)
            svg.Append($"<circle cx=\"{F(exampleCenter.X)}\" cy=\"{F(exampleCenter.Y)}\" r=\"{F(exampleWidth/2)}\" fill=\"black\"/>");
        else Rect(new(exampleCenter.X-exampleWidth/2,exampleCenter.Y-exampleHeight/2,exampleWidth,exampleHeight),"black",0);
        Text(new(info.X + (wideExample ? 22 + exampleWidth + 3 : 27),info.Y+24),"填满并涂黑",2.8);
        AppendSchoolMachineCodes(svg);
        if(CandidateArea is {} area && SchoolDefinition!.CandidateIdentity.Mode==CandidateIdentityMode.Barcode && SchoolDefinition.CandidateIdentity.CandidateId is null) { Rect(area); Text(new(area.X+2,area.Y+5),"请粘贴考号条码",3); }
        foreach (var group in SchoolGroups)
        {
            Rect(group.RectangleMm);
            var titleLineCount = WrapSchoolText(group.Title, group.RectangleMm.Width - 8).Count();
            var dividerY = group.RectangleMm.Y + 0.5 + titleLineCount * 4.5;
            Line(group.RectangleMm.X + 1, dividerY, group.RectangleMm.X + group.RectangleMm.Width - 1, dividerY);
        }
        foreach(var text in SchoolTexts) Text(text.Position,text.Text,text.FontSizeMm);
        foreach(var region in SubjectiveRegions) Rect(region.Rectangle);
        foreach(var bubble in Bubbles.Concat(CandidateDigits.SelectMany(d=>d.Bubbles)))
        {
            if(bubble.Shape==BubbleShape.Circle) svg.Append($"<circle cx=\"{F(bubble.Center.X)}\" cy=\"{F(bubble.Center.Y)}\" r=\"{F(bubble.RadiusMm)}\" fill=\"none\" stroke=\"black\" stroke-width=\"0.12\"/>");
            else Rect(new(bubble.Center.X-bubble.RadiusMm,bubble.Center.Y-bubble.HeightMm/2,bubble.RadiusMm*2,bubble.HeightMm),"none",.12);
            if(bubble.LabelPlacement==LabelPlacement.Inside) Text(new(bubble.Center.X,bubble.Center.Y+bubble.HeightMm*.18),bubble.OptionLabel,Math.Min(bubble.RadiusMm*2*.48,bubble.HeightMm*.48),"middle");
            else Text(new(bubble.Center.X+bubble.RadiusMm+.6,bubble.Center.Y+.6),bubble.OptionLabel,1.8);
        }
        return svg.Append("</svg>").ToString();
    }
}
