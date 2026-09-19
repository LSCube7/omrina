using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Omrina.Core;

/// <summary>A point measured in millimetres from the top-left corner of an A4 page.</summary>
public readonly record struct PointMm(double X, double Y);

/// <summary>A square registration mark measured in millimetres.</summary>
public sealed record RegistrationMark(string Id, PointMm TopLeft, double SizeMm);

/// <summary>A rectangular asymmetric marker placed near the top edge to identify page orientation.</summary>
public sealed record OrientationMarker(string Id, PointMm TopLeft, double WidthMm, double HeightMm);

/// <summary>The printed option label position for one page column.</summary>
public sealed record OptionHeader(
    int Column,
    int OptionIndex,
    string OptionLabel,
    PointMm Position);

/// <summary>
/// The machine-readable geometry of one answer bubble.
/// Coordinates are page-relative millimetres and option indexes are one-based.
/// </summary>
public sealed record AnswerBubble(
    int QuestionNumber,
    int OptionIndex,
    string OptionLabel,
    PointMm Center,
    double RadiusMm);

/// <summary>The geometry for one multiple-choice question.</summary>
public sealed class QuestionGeometry
{
    internal QuestionGeometry(
        int number,
        int column,
        int row,
        PointMm numberPosition,
        IReadOnlyList<AnswerBubble> bubbles)
    {
        Number = number;
        Column = column;
        Row = row;
        NumberPosition = numberPosition;
        Bubbles = bubbles;
    }

    /// <summary>The one-based question number printed beside the bubbles.</summary>
    public int Number { get; }

    /// <summary>The one-based page column containing this question.</summary>
    public int Column { get; }

    /// <summary>The one-based row within <see cref="Column" />.</summary>
    public int Row { get; }

    /// <summary>The baseline position of the printed question number.</summary>
    public PointMm NumberPosition { get; }

    /// <summary>The answer bubbles in option order.</summary>
    public IReadOnlyList<AnswerBubble> Bubbles { get; }
}

/// <summary>
/// Deterministic geometry for a single-page A4 multiple-choice answer sheet.
/// All coordinates and lengths are in millimetres.
/// </summary>
public sealed class AnswerSheetLayout
{
    public const double PageWidthMm = 210;
    public const double PageHeightMm = 297;
    public const int ColumnCount = 2;
    public const int MinOptionsPerQuestion = 2;
    public const int MaxOptionsPerQuestion = 6;
    public const int MaxTitleCharacters = 24;
    public const double RegistrationMarkSizeMm = 8;
    public const double BubbleRadiusMm = 3;
    public const double BubblePitchMm = 11;
    public const double QuestionRowHeightMm = 10;
    public const int TemplateSchemaVersion = 1;

    private const double RegistrationMarkInsetMm = 10;
    private const double OrientationMarkerTopMm = 10;
    private const double OrientationMarkerWidthMm = 4;
    private const double OrientationMarkerHeightMm = 4;
    private const double QuestionAreaTopMm = 52;
    private const double QuestionAreaBottomMm = 275;
    private const double QuestionColumnLeftMm = 14;
    private const double QuestionColumnPitchMm = 92;
    private const double BubbleStartOffsetMm = 32;
    private const double OptionHeaderYMm = 48;
    private const double TemplateNumberYMm = 21;
    internal const double TemplateNumberFontSizeMm = 3.5;
    internal const double TitleYMm = 29;
    internal const double TitleFontSizeMm = 8;

    private AnswerSheetLayout(
        string title,
        int questionCount,
        int optionsPerQuestion,
        string templateId,
        string templateNumber,
        PointMm templateNumberPosition,
        int rowsPerColumn,
        IReadOnlyList<RegistrationMark> registrationMarks,
        OrientationMarker orientationMarker,
        IReadOnlyList<OptionHeader> optionHeaders,
        IReadOnlyList<QuestionGeometry> questions,
        IReadOnlyList<AnswerBubble> bubbles)
    {
        Title = title;
        QuestionCount = questionCount;
        OptionsPerQuestion = optionsPerQuestion;
        TemplateId = templateId;
        TemplateNumber = templateNumber;
        TemplateNumberPosition = templateNumberPosition;
        RowsPerColumn = rowsPerColumn;
        RegistrationMarks = registrationMarks;
        OrientationMarker = orientationMarker;
        OptionHeaders = optionHeaders;
        Questions = questions;
        Bubbles = bubbles;
    }

    public string Title { get; }

    public int QuestionCount { get; }

    public int OptionsPerQuestion { get; }

    /// <summary>
    /// Stable SHA-256 identity of the schema version and normalized template parameters.
    /// The complete lowercase hexadecimal value is intended for manifest association.
    /// </summary>
    public string TemplateId { get; }

    /// <summary>A short human-readable number printed in the page header.</summary>
    public string TemplateNumber { get; }

    /// <summary>The centered baseline position of <see cref="TemplateNumber" />.</summary>
    public PointMm TemplateNumberPosition { get; }

    /// <summary>The number of questions that fit in each page column.</summary>
    public int RowsPerColumn { get; }

    public IReadOnlyList<RegistrationMark> RegistrationMarks { get; }

    /// <summary>
    /// The asymmetric top marker used to distinguish the page's upright orientation.
    /// Its rectangle is separate from the four registration marks and answer bubbles.
    /// </summary>
    public OrientationMarker OrientationMarker { get; }

    /// <summary>Printed A-F labels shown once above each active question column.</summary>
    public IReadOnlyList<OptionHeader> OptionHeaders { get; }

    public IReadOnlyList<QuestionGeometry> Questions { get; }

    /// <summary>A flattened view of <see cref="QuestionGeometry.Bubbles" /> for recognition.</summary>
    public IReadOnlyList<AnswerBubble> Bubbles { get; }

    /// <summary>Returns the maximum question count for the requested option count.</summary>
    public static int MaxQuestionCount(int optionsPerQuestion)
    {
        ValidateOptionsPerQuestion(optionsPerQuestion);
        return CalculateRowsPerColumn() * ColumnCount;
    }

    /// <summary>Builds deterministic A4 geometry for a multiple-choice answer sheet.</summary>
    public static AnswerSheetLayout Create(string title, int questionCount, int optionsPerQuestion)
    {
        var normalizedTitle = NormalizeAndValidateTitle(title);

        if (questionCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(questionCount),
                questionCount,
                "Question count must be greater than zero.");
        }

        ValidateOptionsPerQuestion(optionsPerQuestion);

        var rowsPerColumn = CalculateRowsPerColumn();
        var maximumQuestionCount = rowsPerColumn * ColumnCount;
        if (questionCount > maximumQuestionCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(questionCount),
                questionCount,
                $"The A4 page supports at most {maximumQuestionCount} questions.");
        }

        var registrationMarks = CreateRegistrationMarks();
        var orientationMarker = CreateOrientationMarker();
        var templateNumberPosition = new PointMm(PageWidthMm / 2, TemplateNumberYMm);
        EnsurePointInsidePage(templateNumberPosition, "Template number");
        var activeColumnCount = (questionCount + rowsPerColumn - 1) / rowsPerColumn;
        var optionHeaders = CreateOptionHeaders(activeColumnCount, optionsPerQuestion);
        var questions = new List<QuestionGeometry>(questionCount);
        var bubbles = new List<AnswerBubble>(questionCount * optionsPerQuestion);

        for (var questionIndex = 0; questionIndex < questionCount; questionIndex++)
        {
            var column = questionIndex / rowsPerColumn + 1;
            var row = questionIndex % rowsPerColumn + 1;
            var questionNumber = questionIndex + 1;
            var bubblesForQuestion = new List<AnswerBubble>(optionsPerQuestion);
            var columnStartX = QuestionColumnLeftMm + (column - 1) * QuestionColumnPitchMm;
            var centerY = QuestionAreaTopMm + (row - 0.5) * QuestionRowHeightMm;
            var numberPosition = new PointMm(columnStartX, centerY + 1.5);
            EnsurePointInsidePage(numberPosition, $"Question number {questionNumber}");

            for (var optionIndex = 0; optionIndex < optionsPerQuestion; optionIndex++)
            {
                var center = new PointMm(
                    columnStartX + BubbleStartOffsetMm + optionIndex * BubblePitchMm,
                    centerY);
                var bubble = new AnswerBubble(
                    questionNumber,
                    optionIndex + 1,
                    OptionLabel(optionIndex),
                    center,
                    BubbleRadiusMm);
                EnsureBubbleInsidePage(bubble);
                bubblesForQuestion.Add(bubble);
                bubbles.Add(bubble);
            }

            questions.Add(new QuestionGeometry(
                questionNumber,
                column,
                row,
                numberPosition,
                ReadOnly(bubblesForQuestion)));
        }

        ValidateBubbleSpacing(bubbles);
        ValidateOrientationMarker(
            orientationMarker,
            registrationMarks,
            templateNumberPosition,
            optionHeaders,
            bubbles);

        var templateId = CreateTemplateId(normalizedTitle, questionCount, optionsPerQuestion);
        var templateNumber = CreateTemplateNumber(templateId, questionCount, optionsPerQuestion);

        return new AnswerSheetLayout(
            normalizedTitle,
            questionCount,
            optionsPerQuestion,
            templateId,
            templateNumber,
            templateNumberPosition,
            rowsPerColumn,
            ReadOnly(registrationMarks),
            orientationMarker,
            ReadOnly(optionHeaders),
            ReadOnly(questions),
            ReadOnly(bubbles));
    }

    /// <summary>Exports this layout as a deterministic, page-sized SVG document.</summary>
    public string ToSvg() => SvgTemplateExporter.Export(this);

    private static int CalculateRowsPerColumn()
    {
        return (int)Math.Floor((QuestionAreaBottomMm - QuestionAreaTopMm) / QuestionRowHeightMm);
    }

    private static List<RegistrationMark> CreateRegistrationMarks()
    {
        var farX = PageWidthMm - RegistrationMarkInsetMm - RegistrationMarkSizeMm;
        var farY = PageHeightMm - RegistrationMarkInsetMm - RegistrationMarkSizeMm;
        var marks = new List<RegistrationMark>
        {
            new("registration-top-left", new PointMm(RegistrationMarkInsetMm, RegistrationMarkInsetMm), RegistrationMarkSizeMm),
            new("registration-top-right", new PointMm(farX, RegistrationMarkInsetMm), RegistrationMarkSizeMm),
            new("registration-bottom-left", new PointMm(RegistrationMarkInsetMm, farY), RegistrationMarkSizeMm),
            new("registration-bottom-right", new PointMm(farX, farY), RegistrationMarkSizeMm),
        };

        foreach (var mark in marks)
        {
            if (mark.TopLeft.X < 0
                || mark.TopLeft.Y < 0
                || mark.TopLeft.X + mark.SizeMm > PageWidthMm
                || mark.TopLeft.Y + mark.SizeMm > PageHeightMm)
            {
                throw new InvalidOperationException($"Registration mark '{mark.Id}' is outside the A4 page.");
            }
        }

        return marks;
    }

    private static OrientationMarker CreateOrientationMarker()
    {
        var marker = new OrientationMarker(
            "orientation-top",
            new PointMm((PageWidthMm - OrientationMarkerWidthMm) / 2, OrientationMarkerTopMm),
            OrientationMarkerWidthMm,
            OrientationMarkerHeightMm);
        EnsureRectangleInsidePage(marker.TopLeft, marker.WidthMm, marker.HeightMm, marker.Id);
        return marker;
    }

    private static List<OptionHeader> CreateOptionHeaders(int activeColumnCount, int optionsPerQuestion)
    {
        var headers = new List<OptionHeader>(activeColumnCount * optionsPerQuestion);
        for (var column = 1; column <= activeColumnCount; column++)
        {
            var columnStartX = QuestionColumnLeftMm + (column - 1) * QuestionColumnPitchMm;
            for (var optionIndex = 0; optionIndex < optionsPerQuestion; optionIndex++)
            {
                var position = new PointMm(
                    columnStartX + BubbleStartOffsetMm + optionIndex * BubblePitchMm,
                    OptionHeaderYMm);
                EnsurePointInsidePage(position, $"Option header {column}/{optionIndex + 1}");
                headers.Add(new OptionHeader(column, optionIndex + 1, OptionLabel(optionIndex), position));
            }
        }

        return headers;
    }

    private static string NormalizeAndValidateTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title must contain visible text.", nameof(title));
        }

        if (title.IndexOfAny(['\r', '\n', '\t']) >= 0)
        {
            throw new ArgumentException("Title must be a single line.", nameof(title));
        }

        try
        {
            XmlConvert.VerifyXmlChars(title);
        }
        catch (XmlException exception)
        {
            throw new ArgumentException("Title contains characters that are not valid in XML.", nameof(title), exception);
        }

        var normalizedTitle = title.Normalize(NormalizationForm.FormC);
        try
        {
            XmlConvert.VerifyXmlChars(normalizedTitle);
        }
        catch (XmlException exception)
        {
            throw new ArgumentException("Title contains characters that are not valid in XML.", nameof(title), exception);
        }

        var titleCharacterCount = normalizedTitle.EnumerateRunes().Count();
        if (titleCharacterCount > MaxTitleCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(title),
                title,
                $"Title must contain at most {MaxTitleCharacters} Unicode characters.");
        }

        return normalizedTitle;
    }

    private static void ValidateOptionsPerQuestion(int optionsPerQuestion)
    {
        if (optionsPerQuestion is < MinOptionsPerQuestion or > MaxOptionsPerQuestion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(optionsPerQuestion),
                optionsPerQuestion,
                $"Options per question must be between {MinOptionsPerQuestion} and {MaxOptionsPerQuestion}.");
        }
    }

    private static void EnsureBubbleInsidePage(AnswerBubble bubble)
    {
        if (bubble.RadiusMm <= 0
            || bubble.Center.X - bubble.RadiusMm < 0
            || bubble.Center.X + bubble.RadiusMm > PageWidthMm
            || bubble.Center.Y - bubble.RadiusMm < 0
            || bubble.Center.Y + bubble.RadiusMm > PageHeightMm)
        {
            throw new InvalidOperationException(
                $"Bubble for question {bubble.QuestionNumber}, option {bubble.OptionIndex} is outside the A4 page.");
        }
    }

    private static void EnsurePointInsidePage(PointMm point, string description)
    {
        if (point.X < 0 || point.X > PageWidthMm || point.Y < 0 || point.Y > PageHeightMm)
        {
            throw new InvalidOperationException($"{description} is outside the A4 page.");
        }
    }

    private static void EnsureRectangleInsidePage(PointMm topLeft, double width, double height, string description)
    {
        if (width <= 0
            || height <= 0
            || topLeft.X < 0
            || topLeft.Y < 0
            || topLeft.X + width > PageWidthMm
            || topLeft.Y + height > PageHeightMm)
        {
            throw new InvalidOperationException($"{description} is outside the A4 page.");
        }
    }

    private static void ValidateOrientationMarker(
        OrientationMarker marker,
        IReadOnlyList<RegistrationMark> registrationMarks,
        PointMm templateNumberPosition,
        IReadOnlyList<OptionHeader> optionHeaders,
        IReadOnlyList<AnswerBubble> bubbles)
    {
        EnsureRectangleInsidePage(marker.TopLeft, marker.WidthMm, marker.HeightMm, $"Orientation marker '{marker.Id}'");

        foreach (var registrationMark in registrationMarks)
        {
            if (RectanglesOverlap(
                marker.TopLeft,
                marker.WidthMm,
                marker.HeightMm,
                registrationMark.TopLeft,
                registrationMark.SizeMm,
                registrationMark.SizeMm))
            {
                throw new InvalidOperationException(
                    $"Orientation marker '{marker.Id}' overlaps registration mark '{registrationMark.Id}'.");
            }
        }

        foreach (var bubble in bubbles)
        {
            if (CircleIntersectsRectangle(bubble.Center, bubble.RadiusMm, marker.TopLeft, marker.WidthMm, marker.HeightMm))
            {
                throw new InvalidOperationException(
                    $"Orientation marker '{marker.Id}' overlaps bubble {bubble.QuestionNumber}/{bubble.OptionLabel}.");
            }
        }

        if (marker.TopLeft.Y + marker.HeightMm >= templateNumberPosition.Y - TemplateNumberFontSizeMm)
        {
            throw new InvalidOperationException($"Orientation marker '{marker.Id}' overlaps the template number.");
        }

        if (marker.TopLeft.Y + marker.HeightMm >= TitleYMm - TitleFontSizeMm)
        {
            throw new InvalidOperationException($"Orientation marker '{marker.Id}' overlaps the title.");
        }

        foreach (var header in optionHeaders)
        {
            if (marker.TopLeft.Y + marker.HeightMm >= header.Position.Y - TemplateNumberFontSizeMm)
            {
                throw new InvalidOperationException(
                    $"Orientation marker '{marker.Id}' overlaps option header {header.Column}/{header.OptionIndex}.");
            }
        }
    }

    private static bool RectanglesOverlap(
        PointMm firstTopLeft,
        double firstWidth,
        double firstHeight,
        PointMm secondTopLeft,
        double secondWidth,
        double secondHeight)
    {
        return firstTopLeft.X < secondTopLeft.X + secondWidth
            && firstTopLeft.X + firstWidth > secondTopLeft.X
            && firstTopLeft.Y < secondTopLeft.Y + secondHeight
            && firstTopLeft.Y + firstHeight > secondTopLeft.Y;
    }

    private static bool CircleIntersectsRectangle(
        PointMm center,
        double radius,
        PointMm rectangleTopLeft,
        double rectangleWidth,
        double rectangleHeight)
    {
        var nearestX = Math.Clamp(center.X, rectangleTopLeft.X, rectangleTopLeft.X + rectangleWidth);
        var nearestY = Math.Clamp(center.Y, rectangleTopLeft.Y, rectangleTopLeft.Y + rectangleHeight);
        var deltaX = center.X - nearestX;
        var deltaY = center.Y - nearestY;
        return deltaX * deltaX + deltaY * deltaY <= radius * radius;
    }

    private static string CreateTemplateId(string normalizedTitle, int questionCount, int optionsPerQuestion)
    {
        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"answersheet-template|schema:{TemplateSchemaVersion}|title:{normalizedTitle.EnumerateRunes().Count()}:{normalizedTitle}|questions:{questionCount}|options:{optionsPerQuestion}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string CreateTemplateNumber(string templateId, int questionCount, int optionsPerQuestion)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"AS{TemplateSchemaVersion}-{questionCount:D2}x{optionsPerQuestion}-{templateId[..8].ToUpperInvariant()}");
    }

    private static void ValidateBubbleSpacing(IReadOnlyList<AnswerBubble> bubbles)
    {
        for (var firstIndex = 0; firstIndex < bubbles.Count; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < bubbles.Count; secondIndex++)
            {
                var first = bubbles[firstIndex];
                var second = bubbles[secondIndex];
                var deltaX = first.Center.X - second.Center.X;
                var deltaY = first.Center.Y - second.Center.Y;
                var minimumDistance = first.RadiusMm + second.RadiusMm;
                if (deltaX * deltaX + deltaY * deltaY <= minimumDistance * minimumDistance)
                {
                    throw new InvalidOperationException(
                        $"Bubbles for question {first.QuestionNumber}, option {first.OptionIndex} and "
                        + $"question {second.QuestionNumber}, option {second.OptionIndex} overlap.");
                }
            }
        }
    }

    private static string OptionLabel(int zeroBasedOptionIndex)
    {
        return ((char)('A' + zeroBasedOptionIndex)).ToString(CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values)
    {
        return new ReadOnlyCollection<T>(values.ToArray());
    }
}

/// <summary>Deterministic SVG serialization for <see cref="AnswerSheetLayout" />.</summary>
public static class SvgTemplateExporter
{
    public static string Export(AnswerSheetLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var svg = new StringBuilder();
        svg.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        svg.AppendLine(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"210mm\" height=\"297mm\" "
            + "viewBox=\"0 0 210 297\" role=\"document\" shape-rendering=\"geometricPrecision\">");
        svg.AppendLine("  <metadata>");
        svg.Append("    <answer-sheet-template schema-version=\"")
            .Append(AnswerSheetLayout.TemplateSchemaVersion.ToString(CultureInfo.InvariantCulture))
            .Append("\" template-id=\"")
            .Append(EscapeXml(layout.TemplateId))
            .Append("\" template-number=\"")
            .Append(EscapeXml(layout.TemplateNumber))
            .Append("\" question-count=\"")
            .Append(layout.QuestionCount.ToString(CultureInfo.InvariantCulture))
            .Append("\" options-per-question=\"")
            .Append(layout.OptionsPerQuestion.ToString(CultureInfo.InvariantCulture))
            .AppendLine("\" />");
        svg.AppendLine("  </metadata>");
        svg.Append("  <title>").Append(EscapeXml(layout.Title)).AppendLine("</title>");
        svg.AppendLine("  <g id=\"registration-marks\" data-role=\"registration-marks\" fill=\"#000000\">");
        foreach (var mark in layout.RegistrationMarks)
        {
            svg.Append("    <rect id=\"")
                .Append(EscapeXml(mark.Id))
                .Append("\" x=\"")
                .Append(Format(mark.TopLeft.X))
                .Append("\" y=\"")
                .Append(Format(mark.TopLeft.Y))
                .Append("\" width=\"")
                .Append(Format(mark.SizeMm))
                .Append("\" height=\"")
                .Append(Format(mark.SizeMm))
                .AppendLine("\" />");
        }

        svg.AppendLine("  </g>");
        svg.AppendLine("  <g id=\"orientation-marker\" data-role=\"orientation-marker\" fill=\"#000000\">");
        svg.Append("    <rect id=\"")
            .Append(EscapeXml(layout.OrientationMarker.Id))
            .Append("\" x=\"")
            .Append(Format(layout.OrientationMarker.TopLeft.X))
            .Append("\" y=\"")
            .Append(Format(layout.OrientationMarker.TopLeft.Y))
            .Append("\" width=\"")
            .Append(Format(layout.OrientationMarker.WidthMm))
            .Append("\" height=\"")
            .Append(Format(layout.OrientationMarker.HeightMm))
            .AppendLine("\" />");
        svg.AppendLine("  </g>");
        svg.AppendLine("  <g id=\"option-headers\" data-role=\"option-headers\" font-family=\"Arial, sans-serif\">");
        foreach (var header in layout.OptionHeaders)
        {
            svg.Append("    <text class=\"option-label\" data-role=\"option-header\" data-column=\"")
                .Append(header.Column.ToString(CultureInfo.InvariantCulture))
                .Append("\" data-option-index=\"")
                .Append(header.OptionIndex.ToString(CultureInfo.InvariantCulture))
                .Append("\" x=\"")
                .Append(Format(header.Position.X))
                .Append("\" y=\"")
                .Append(Format(header.Position.Y))
                .Append("\" text-anchor=\"middle\" font-size=\"3.5\">")
                .Append(EscapeXml(header.OptionLabel))
                .AppendLine("</text>");
        }

        svg.AppendLine("  </g>");
        svg.AppendLine("  <g id=\"sheet-header\" data-role=\"header\" font-family=\"Arial, sans-serif\">");
        svg.Append("    <text class=\"template-number\" data-role=\"template-number\" x=\"")
            .Append(Format(layout.TemplateNumberPosition.X))
            .Append("\" y=\"")
            .Append(Format(layout.TemplateNumberPosition.Y))
            .Append("\" text-anchor=\"middle\" font-size=\"")
            .Append(Format(AnswerSheetLayout.TemplateNumberFontSizeMm))
            .Append("\">")
            .Append(EscapeXml(layout.TemplateNumber))
            .AppendLine("</text>");
        svg.Append("    <text x=\"")
            .Append(Format(AnswerSheetLayout.PageWidthMm / 2))
            .Append("\" y=\"")
            .Append(Format(AnswerSheetLayout.TitleYMm))
            .Append("\" text-anchor=\"middle\" font-size=\"")
            .Append(Format(AnswerSheetLayout.TitleFontSizeMm))
            .AppendLine("\" font-weight=\"bold\">");
        svg.Append("      ").Append(EscapeXml(layout.Title)).AppendLine("</text>");
        svg.AppendLine("    <text x=\"105\" y=\"38\" text-anchor=\"middle\" font-size=\"3.5\">每题请选择一个选项</text>");
        svg.AppendLine("  </g>");
        svg.AppendLine("  <g id=\"questions\" data-role=\"questions\" font-family=\"Arial, sans-serif\">");

        foreach (var question in layout.Questions)
        {
            var questionId = $"question-{question.Number:D2}";
            svg.Append("    <g id=\"")
                .Append(questionId)
                .Append("\" data-role=\"question\" data-question-number=\"")
                .Append(question.Number.ToString(CultureInfo.InvariantCulture))
                .Append("\" data-column=\"")
                .Append(question.Column.ToString(CultureInfo.InvariantCulture))
                .Append("\" data-row=\"")
                .Append(question.Row.ToString(CultureInfo.InvariantCulture))
                .AppendLine("\">");

            svg.Append("      <text class=\"question-number\" x=\"")
                .Append(Format(question.NumberPosition.X))
                .Append("\" y=\"")
                .Append(Format(question.NumberPosition.Y))
                .Append("\" font-size=\"4.5\">")
                .Append(question.Number.ToString(CultureInfo.InvariantCulture))
                .AppendLine("</text>");

            svg.AppendLine("      <g data-role=\"answer-options\">");
            foreach (var bubble in question.Bubbles)
            {
                var bubbleId = $"q{bubble.QuestionNumber:D2}-option-{bubble.OptionLabel.ToLowerInvariant()}";
                svg.Append("        <circle id=\"")
                    .Append(bubbleId)
                    .Append("\" data-role=\"answer-bubble\" data-question-number=\"")
                    .Append(bubble.QuestionNumber.ToString(CultureInfo.InvariantCulture))
                    .Append("\" data-option-index=\"")
                    .Append(bubble.OptionIndex.ToString(CultureInfo.InvariantCulture))
                    .Append("\" data-option-label=\"")
                    .Append(EscapeXml(bubble.OptionLabel))
                    .Append("\" data-center-x-mm=\"")
                    .Append(Format(bubble.Center.X))
                    .Append("\" data-center-y-mm=\"")
                    .Append(Format(bubble.Center.Y))
                    .Append("\" data-radius-mm=\"")
                    .Append(Format(bubble.RadiusMm))
                    .Append("\" cx=\"")
                    .Append(Format(bubble.Center.X))
                    .Append("\" cy=\"")
                    .Append(Format(bubble.Center.Y))
                    .Append("\" r=\"")
                    .Append(Format(bubble.RadiusMm))
                    .AppendLine("\" fill=\"#FFFFFF\" stroke=\"#000000\" stroke-width=\"0.5\" />");

            }

            svg.AppendLine("      </g>");
            svg.AppendLine("    </g>");
        }

        svg.AppendLine("  </g>");
        svg.AppendLine("</svg>");
        return svg.ToString();
    }

    private static string Format(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string EscapeXml(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '&':
                    escaped.Append("&amp;");
                    break;
                case '<':
                    escaped.Append("&lt;");
                    break;
                case '>':
                    escaped.Append("&gt;");
                    break;
                case '"':
                    escaped.Append("&quot;");
                    break;
                case '\'':
                    escaped.Append("&apos;");
                    break;
                default:
                    escaped.Append(character);
                    break;
            }
        }

        return escaped.ToString();
    }
}
