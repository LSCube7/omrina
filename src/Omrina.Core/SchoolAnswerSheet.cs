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
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.ExamId) || input.ExamId.Length > 80 || string.IsNullOrWhiteSpace(input.LayoutDocumentId) || input.LayoutDocumentId.Length > 80 || input.Version < 1 || string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 80
            || !Enum.IsDefined(input.Paper) || !Enum.IsDefined(input.Mode) || !Enum.IsDefined(input.BubbleShape) || !Enum.IsDefined(input.LabelPlacement) || (input.Paper == SchoolPaper.A4Portrait ? input.Columns != 1 : input.Columns is not (2 or 3)))
            throw new ArgumentException("考试标识、版本、标题或纸张栏数无效。");
        if (!double.IsFinite(input.BubbleWidthMm) || input.BubbleWidthMm <= 0 || input.BubbleWidthMm > 2 || !double.IsFinite(input.BubbleHeightMm) || input.BubbleHeightMm <= 0 || input.BubbleHeightMm > 4)
            throw new ArgumentException("圆框直径或矩形宽必须大于 0 且不超过 2 毫米；矩形高度不超过 4 毫米。");
        var identity = input.CandidateIdentity;
        if (identity is null || !Enum.IsDefined(identity.Mode) || identity.Digits is < 1 or > 20 || (identity.CandidateId is not null && (identity.CandidateId.Length != identity.Digits || identity.CandidateId.Any(c => c is < '0' or > '9'))))
            throw new ArgumentException("考号位数须为 1–20，预印考号须为对应位数的数字。");
        if (input.Questions is null || input.Questions.Count is < 1 or > 500 || input.Questions.Any(q => q is null)) throw new ArgumentException("题目数量须为 1–500。");
        var questions = input.Questions.Select(q => q with { Options = q.Options is null ? null : Array.AsReadOnly(q.Options.ToArray()) }).ToArray();
        if (questions.Select(q => q.Number).Distinct().Count() != questions.Length) throw new ArgumentException("题号不能重复。");
        foreach (var q in questions)
            if (q.Number <= 0 || !Enum.IsDefined(q.Type) || q.MaximumScore is <= 0 or > 1000000 || q.Body is null || q.Body.Length > 8000 || (q.Type == SchoolQuestionType.Choice && (q.Options is null || q.Options.Count is < 2 or > 6 || q.Options.Any(o => o is null || o.Length > 1000))) || (q.Type == SchoolQuestionType.Subjective && (!double.IsFinite(q.SubjectiveHeightMm) || q.SubjectiveHeightMm is < 10 or > 230)))
                throw new ArgumentException($"第 {q.Number} 题定义无效或答题区过大。");
        var definition = input with { Questions = Array.AsReadOnly(questions) };
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
        const double left = 22, top = 38, bottom = 275, gap = 8;
        var columnWidth = (width - left * 2 - gap * (definition.Columns - 1)) / definition.Columns;
        if (identity.Mode == CandidateIdentityMode.Marking && identity.Digits * 6 + 8 > columnWidth) throw new ArgumentException("考号位数超过当前栏宽，请减少位数或使用条码。");
        var pages = new List<AnswerSheetLayout>(); var index = 0;
        while (index < questions.Length)
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
                        digits.Add(new(position + 1, Array.AsReadOnly(Enumerable.Range(0, 10).Select(digit => NewBubble(-(position + 1), digit + 1, digit.ToString(CultureInfo.InvariantCulture), new(left + 6 + position * 6, top + 33 + digit * 4.5), definition)).ToArray())));
            }
            var texts = new List<SchoolPrintedText>(); var geometry = new List<QuestionGeometry>(); var bubbles = new List<AnswerBubble>(); var regions = new List<TemplateSubjectiveRegion>();
            for (var column = 0; column < definition.Columns && index < questions.Length; column++)
            {
                var x = left + column * (columnWidth + gap); var y = column == 0 ? top + infoHeight + 5 : top; var row = 0;
                while (index < questions.Length)
                {
                    var q = questions[index];
                    var lines = definition.Mode == SchoolContentMode.WithQuestions ? WrapSchoolText(q.Body, columnWidth).ToList() : new List<string>();
                    if (definition.Mode == SchoolContentMode.WithQuestions && q.Type == SchoolQuestionType.Choice)
                        for (var option = 0; option < q.Options!.Count; option++) lines.AddRange(WrapSchoolText($"{(char)('A' + option)}. {q.Options[option]}", columnWidth));
                    var bodyHeight = lines.Count * 4.5; var answerHeight = q.Type == SchoolQuestionType.Subjective ? q.SubjectiveHeightMm : Math.Max(8, definition.BubbleHeightMm + 4); var total = 7 + bodyHeight + answerHeight + 5;
                    if (total > bottom - top) throw new ArgumentException($"第 {q.Number} 题无法放入一栏，请缩短正文或降低该题答题区高度。");
                    if (y + total > bottom) break;
                    texts.Add(new(new(x, y + 4), $"{q.Number}. ({q.MaximumScore.ToString(CultureInfo.InvariantCulture)} 分)", 3));
                    for (var line = 0; line < lines.Count; line++) texts.Add(new(new(x, y + 9 + line * 4.5), lines[line], 3));
                    var answerY = y + 7 + bodyHeight;
                    if (q.Type == SchoolQuestionType.Subjective) regions.Add(TemplateSubjectiveRegion.CreateSchool(q.Number, q.MaximumScore, new(x, answerY, columnWidth, answerHeight), width));
                    else
                    {
                        var choices = Enumerable.Range(0, q.Options!.Count).Select(option => NewBubble(q.Number, option + 1, ((char)('A' + option)).ToString(), new(x + 10 + option * 12, answerY + answerHeight / 2), definition)).ToArray();
                        geometry.Add(new(q.Number, column + 1, ++row, new(x, answerY + answerHeight / 2), Array.AsReadOnly(choices))); bubbles.AddRange(choices);
                    }
                    index++; y += total;
                }
            }
            if (geometry.Count == 0 && regions.Count == 0) throw new ArgumentException($"第 {questions[index].Number} 题无法放入当前纸张，请缩短正文或降低高度。");
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{documentHash}|page:{pageIndex}"))).ToLowerInvariant();
            var marks = new[] { new RegistrationMark("registration-top-left",new(10,10),8), new RegistrationMark("registration-top-right",new(width-18,10),8), new RegistrationMark("registration-bottom-left",new(10,279),8), new RegistrationMark("registration-bottom-right",new(width-18,279),8) };
            var layout = new AnswerSheetLayout(definition.Title, geometry.Count, geometry.Count == 0 ? 2 : geometry.Max(q => q.Bubbles.Count),id,$"AS3-{pageIndex+1}-{id[..8]}",new(width/2,21),geometry.Count,Array.AsReadOnly(marks),new("orientation",new(width/2-2,10),4,4),Array.Empty<OptionHeader>(),geometry.AsReadOnly(),bubbles.AsReadOnly(),3,regions.AsReadOnly())
            { WidthMm=width, SchoolDefinition=definition, SchoolPageIndex=pageIndex, SchoolMetadata=new(definition.ExamId,definition.LayoutDocumentId,definition.Version,pageIndex+1,side,id), SchoolInformationArea=info, CandidateArea=candidateArea, ExamCodeArea=new(left+columnWidth-28,top+3,24,24), CandidateDigits=digits.AsReadOnly(), SchoolTexts=texts.AsReadOnly() };
            pages.Add(layout);
        }
        return pages.AsReadOnly();
    }
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
        var exampleCenter = new PointMm(info.X+23,info.Y+23);
        if (SchoolDefinition.BubbleShape == BubbleShape.Circle)
            svg.Append($"<circle cx=\"{F(exampleCenter.X)}\" cy=\"{F(exampleCenter.Y)}\" r=\"{F(exampleWidth/2)}\" fill=\"black\"/>");
        else Rect(new(exampleCenter.X-exampleWidth/2,exampleCenter.Y-exampleHeight/2,exampleWidth,exampleHeight),"black",0);
        Text(new(info.X+27,info.Y+24),"填满并涂黑",2.8);
        AppendSchoolMachineCodes(svg);
        if(CandidateArea is {} area && SchoolDefinition!.CandidateIdentity.Mode==CandidateIdentityMode.Barcode && SchoolDefinition.CandidateIdentity.CandidateId is null) { Rect(area); Text(new(area.X+2,area.Y+5),"请粘贴考号条码",3); }
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
