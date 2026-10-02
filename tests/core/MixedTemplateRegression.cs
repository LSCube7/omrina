using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Omrina.Core;

internal static class MixedTemplateRegression
{
    public static void Run()
    {
        var legacy = AnswerSheetLayout.Create("旧模板", 10, 4);
        var oldIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "answersheet-template|schema:1|title:3:旧模板|questions:10|options:4"))).ToLowerInvariant();
        Check(legacy.TemplateId == oldIdentity && legacy.SchemaVersion == 1, "legacy identity changed");
        Check(AnswerSheetLayout.FromJson(legacy.ToJson()).TemplateId == oldIdentity, "legacy roundtrip changed");
        Check(AnswerSheetLayout.Create("旧模板", 10, 4, []).ToSvg() == legacy.ToSvg(), "empty regions changed legacy SVG");
        var first = Region(11, 20, new(110, 60, 80, 55));
        var second = Region(12, 10, new(110, 125, 80, 50));
        var layout = AnswerSheetLayout.Create("混合模板", 10, 4, [second, first]);
        Check(layout.SchemaVersion == 2 && layout.SubjectiveRegions[0].QuestionId == first.QuestionId, "canonical region order");
        Check(AnswerSheetLayout.FromJson(layout.ToJson()).ToJson() == layout.ToJson(), "mixed roundtrip");
        Check(AnswerSheetLayout.Create("混合模板", 10, 4, [first, second]).TemplateId == layout.TemplateId, "order changed identity");
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Check(Region(11, 20.00m, new(110, 60, 80, 55)).QuestionId == first.QuestionId, "culture/decimal scale changed identity");
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        var svg = XDocument.Parse(layout.ToSvg());
        Check(svg.Descendants().Count(element => (string?)element.Attribute("data-role") == "subjective-region") == 2, "missing printed regions");
        Reject(() => Region(11, 1, new(double.NaN, 60, 80, 50)));
        Reject(() => Region(11, 1, new(110, 30, 80, 50)));
        Reject(() => Region(11, 1, new(110, 270, 80, 50)));
        Reject(() => AnswerSheetLayout.Create("混合模板", 10, 4, [Region(1, 1, new(110, 60, 80, 50))]));
        Reject(() => AnswerSheetLayout.Create("混合模板", 10, 4, [first, first]));
        Reject(() => AnswerSheetLayout.Create("混合模板", 10, 4, [first, Region(12, 1, new(110, 100, 80, 50))]));
        Reject(() => AnswerSheetLayout.Create("混合模板", 10, 4, [first, Region(12, 1, new(110, 115, 80, 50))]));
        Reject(() => AnswerSheetLayout.Create("混合模板", 10, 4, [Region(11, 1, new(10, 52, 80, 50))]));
        foreach (var key in new[] { "templateId", "schemaVersion", "unknown" })
        {
            var changed = JsonNode.Parse(layout.ToJson())!;
            if (key == "schemaVersion") changed[key] = 1; else changed[key] = "invalid";
            Reject(() => AnswerSheetLayout.FromJson(changed.ToJsonString()));
        }
        var tampered = JsonNode.Parse(layout.ToJson())!;
        tampered["subjectiveRegions"]![0]!["questionId"] = Guid.NewGuid();
        Reject(() => AnswerSheetLayout.FromJson(tampered.ToJsonString()));
        Reject(() => AnswerSheetLayout.FromJson(layout.ToJson().Replace("\"schemaVersion\":2", "\"schemaVersion\":2,\"schemaVersion\":2")));
        Reject(() => AnswerSheetLayout.FromJson(new string(' ', 65537)));
        var recognizer = new AnswerSheetRecognizer();
        foreach (var orientation in new[] { PageOrientation.Degrees0, PageOrientation.Degrees90,
                     PageOrientation.Degrees180, PageOrientation.Degrees270 })
        {
            var image = RecognitionRegression.RenderMixed(layout, orientation, multiple: true);
            var recognition = recognizer.Recognize(layout, image);
            Check(!recognition.CanScore && recognition.TemplateSchemaVersion == 2, "multiple choice should need review");
            var location = recognizer.LocatePage(layout, image);
            Check(location.CanMapRegions && location.Orientation == orientation, "page location should not depend on choice score");
            var mapped = SubjectiveTemplateMapper.Map(layout, location, image.Width, image.Height);
            Check(mapped.Count == 2 && mapped[0].QuestionId == first.QuestionId, "region mapping identity");
            var expected = location.Transform!.Value.Map(new PointMm(first.Rectangle.X, first.Rectangle.Y));
            Check(mapped[0].SourceQuadrilateral.TopLeft == expected && mapped[0].OutputWidth > 0, "original-image corner mapping");
            Reject(() => SubjectiveTemplateMapper.Map(legacy, location, image.Width, image.Height));
            Reject(() => SubjectiveTemplateMapper.Map(layout, location, image.Width + 1, image.Height));
        }
        var projectiveImage = RecognitionRegression.RenderMixed(layout, PageOrientation.Degrees0,
            projective: new PageTransform(2.04, 0.04, 5, 0.02, 2.08, 4, 0.0003, 0.00015, 1));
        var projectiveLocation = recognizer.LocatePage(layout, projectiveImage);
        Check(projectiveLocation.CanMapRegions, "projective page should locate");
        var projectiveRegion = SubjectiveTemplateMapper.Map(layout, projectiveLocation, projectiveImage.Width, projectiveImage.Height)[0];
        Check(projectiveRegion.SourceQuadrilateral.TopLeft.Y != projectiveRegion.SourceQuadrilateral.TopRight.Y, "quad was replaced by bounding rectangle");
        var missing = RecognitionRegression.RenderMixed(layout, PageOrientation.Degrees0, missingMark: true);
        var rejected = recognizer.LocatePage(layout, missing);
        Check(!rejected.CanMapRegions, "incomplete page should reject mapping");
        Reject(() => SubjectiveTemplateMapper.Map(layout, rejected, missing.Width, missing.Height));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { recognizer.LocatePage(layout, missing, cancellation.Token); throw new InvalidOperationException("cancel ignored"); }
        catch (OperationCanceledException) { }
    }
    private static TemplateSubjectiveRegion Region(int number, decimal score, RectMm rectangle)
        => TemplateSubjectiveRegion.Create(number, score, rectangle);
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("invalid definition was accepted");
    }
}
