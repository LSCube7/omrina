using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Xml;

namespace AnswerSheet.Core;

/// <summary>A point measured in millimetres from the top-left corner of an A4 page.</summary>
public readonly record struct PointMm(double X, double Y);

/// <summary>A square registration mark measured in millimetres.</summary>
public sealed record RegistrationMark(string Id, PointMm TopLeft, double SizeMm);

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

    private const double RegistrationMarkInsetMm = 10;
    private const double QuestionAreaTopMm = 52;
    private const double QuestionAreaBottomMm = 275;
    private const double QuestionColumnLeftMm = 14;
    private const double QuestionColumnPitchMm = 92;
    private const double BubbleStartOffsetMm = 32;
    private const double OptionHeaderYMm = 48;

    private AnswerSheetLayout(
        string title,
        int questionCount,
        int optionsPerQuestion,
        int rowsPerColumn,
        IReadOnlyList<RegistrationMark> registrationMarks,
        IReadOnlyList<OptionHeader> optionHeaders,
        IReadOnlyList<QuestionGeometry> questions,
        IReadOnlyList<AnswerBubble> bubbles)
    {
        Title = title;
        QuestionCount = questionCount;
        OptionsPerQuestion = optionsPerQuestion;
        RowsPerColumn = rowsPerColumn;
        RegistrationMarks = registrationMarks;
        OptionHeaders = optionHeaders;
        Questions = questions;
        Bubbles = bubbles;
    }

    public string Title { get; }

    public int QuestionCount { get; }

    public int OptionsPerQuestion { get; }

    /// <summary>The number of questions that fit in each page column.</summary>
    public int RowsPerColumn { get; }

    public IReadOnlyList<RegistrationMark> RegistrationMarks { get; }

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
        ValidateTitle(title);

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

        return new AnswerSheetLayout(
            title,
            questionCount,
            optionsPerQuestion,
            rowsPerColumn,
            ReadOnly(registrationMarks),
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

    private static void ValidateTitle(string title)
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

        var titleCharacterCount = title.EnumerateRunes().Count();
        if (titleCharacterCount > MaxTitleCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(title),
                title,
                $"Title must contain at most {MaxTitleCharacters} Unicode characters.");
        }
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
        svg.AppendLine("    <text x=\"105\" y=\"29\" text-anchor=\"middle\" font-size=\"8\" font-weight=\"bold\">");
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
