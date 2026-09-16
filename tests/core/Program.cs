using System.Globalization;
using System.Xml.Linq;
using AnswerSheet.Core;

var failures = new List<string>();

Run("valid maximum capacity", failures, VerifyMaximumCapacity);
Run("overflow is rejected", failures, VerifyOverflow);
Run("bubbles do not overlap", failures, VerifyBubbleSpacing);
Run("geometry stays inside the page", failures, VerifyGeometryBounds);
Run("illegal inputs are rejected", failures, VerifyIllegalInputs);
Run("title is XML escaped", failures, VerifyTitleEscaping);
Run("layout is deterministic", failures, VerifyDeterminism);

if (args.Contains("--write-example", StringComparer.Ordinal))
{
    WriteExampleSvg();
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"FAIL: {failures.Count} core regression test(s) failed.");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }

    return 1;
}

Console.WriteLine("PASS: 7 AnswerSheet.Core regression tests.");
return 0;

static void Run(string name, List<string> failures, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS: {name}.");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
    }
}

static void VerifyMaximumCapacity()
{
    var maximum = AnswerSheetLayout.MaxQuestionCount(6);
    AssertEqual(44, maximum, "A4 capacity should be 44 questions.");

    var layout = AnswerSheetLayout.Create("M1 示例", maximum, 6);
    AssertEqual(maximum, layout.Questions.Count, "all maximum questions should be laid out");
    AssertEqual(maximum * 6, layout.Bubbles.Count, "every question should have six bubbles");
    AssertEqual(4, layout.RegistrationMarks.Count, "four registration marks are required");
    AssertEqual(12, layout.OptionHeaders.Count, "each active column should have six option headers");
    AssertEqual(22, layout.RowsPerColumn, "each page column should have 22 rows");
    AssertEqual(1, layout.Questions[0].Column, "first question should be in the first column");
    AssertEqual(2, layout.Questions[22].Column, "question 23 should start the second column");
    AssertEqual(22, layout.Questions[21].Row, "question 22 should be the last row in column one");
    AssertEqual(new PointMm(14, 58.5), layout.Questions[0].NumberPosition, "question number position should be explicit");
}

static void VerifyOverflow()
{
    var maximum = AnswerSheetLayout.MaxQuestionCount(4);
    AssertThrows<ArgumentOutOfRangeException>(
        () => AnswerSheetLayout.Create("容量测试", maximum + 1, 4),
        "question count above page capacity");
}

static void VerifyBubbleSpacing()
{
    var layout = AnswerSheetLayout.Create("间距测试", AnswerSheetLayout.MaxQuestionCount(6), 6);
    for (var firstIndex = 0; firstIndex < layout.Bubbles.Count; firstIndex++)
    {
        for (var secondIndex = firstIndex + 1; secondIndex < layout.Bubbles.Count; secondIndex++)
        {
            var first = layout.Bubbles[firstIndex];
            var second = layout.Bubbles[secondIndex];
            var deltaX = first.Center.X - second.Center.X;
            var deltaY = first.Center.Y - second.Center.Y;
            var minimumDistance = first.RadiusMm + second.RadiusMm;
            AssertTrue(
                deltaX * deltaX + deltaY * deltaY > minimumDistance * minimumDistance,
                $"bubbles {first.QuestionNumber}/{first.OptionLabel} and {second.QuestionNumber}/{second.OptionLabel} overlap");
        }
    }
}

static void VerifyGeometryBounds()
{
    var layout = AnswerSheetLayout.Create("边界测试", AnswerSheetLayout.MaxQuestionCount(6), 6);

    foreach (var mark in layout.RegistrationMarks)
    {
        AssertTrue(mark.TopLeft.X >= 0 && mark.TopLeft.Y >= 0, $"registration mark {mark.Id} starts inside the page");
        AssertTrue(mark.TopLeft.X + mark.SizeMm <= AnswerSheetLayout.PageWidthMm, $"registration mark {mark.Id} fits horizontally");
        AssertTrue(mark.TopLeft.Y + mark.SizeMm <= AnswerSheetLayout.PageHeightMm, $"registration mark {mark.Id} fits vertically");
    }

    foreach (var header in layout.OptionHeaders)
    {
        AssertTrue(header.Position.X >= 0 && header.Position.X <= AnswerSheetLayout.PageWidthMm, "option header x should be inside the page");
        AssertTrue(header.Position.Y >= 0 && header.Position.Y <= AnswerSheetLayout.PageHeightMm, "option header y should be inside the page");
    }

    foreach (var question in layout.Questions)
    {
        AssertTrue(question.NumberPosition.X >= 0 && question.NumberPosition.X <= AnswerSheetLayout.PageWidthMm, "question number x should be inside the page");
        AssertTrue(question.NumberPosition.Y >= 0 && question.NumberPosition.Y <= AnswerSheetLayout.PageHeightMm, "question number y should be inside the page");
    }

    foreach (var bubble in layout.Bubbles)
    {
        AssertTrue(bubble.Center.X - bubble.RadiusMm >= 0, "bubble should not cross the left page edge");
        AssertTrue(bubble.Center.X + bubble.RadiusMm <= AnswerSheetLayout.PageWidthMm, "bubble should not cross the right page edge");
        AssertTrue(bubble.Center.Y - bubble.RadiusMm >= 0, "bubble should not cross the top page edge");
        AssertTrue(bubble.Center.Y + bubble.RadiusMm <= AnswerSheetLayout.PageHeightMm, "bubble should not cross the bottom page edge");

        foreach (var mark in layout.RegistrationMarks)
        {
            var nearestX = Math.Clamp(bubble.Center.X, mark.TopLeft.X, mark.TopLeft.X + mark.SizeMm);
            var nearestY = Math.Clamp(bubble.Center.Y, mark.TopLeft.Y, mark.TopLeft.Y + mark.SizeMm);
            var deltaX = bubble.Center.X - nearestX;
            var deltaY = bubble.Center.Y - nearestY;
            AssertTrue(
                deltaX * deltaX + deltaY * deltaY > bubble.RadiusMm * bubble.RadiusMm,
                $"bubble {bubble.QuestionNumber}/{bubble.OptionLabel} should not intersect {mark.Id}");
        }
    }
}

static void VerifyIllegalInputs()
{
    AssertThrows<ArgumentNullException>(
        () => AnswerSheetLayout.Create(null!, 1, 4),
        "null title");
    AssertThrows<ArgumentException>(
        () => AnswerSheetLayout.Create("   ", 1, 4),
        "blank title");
    AssertThrows<ArgumentException>(
        () => AnswerSheetLayout.Create("line\nbreak", 1, 4),
        "multi-line title");
    AssertThrows<ArgumentOutOfRangeException>(
        () => AnswerSheetLayout.Create("invalid count", 0, 4),
        "zero question count");
    AssertThrows<ArgumentOutOfRangeException>(
        () => AnswerSheetLayout.Create("too few", 1, 1),
        "one option");
    AssertThrows<ArgumentOutOfRangeException>(
        () => AnswerSheetLayout.Create("too many", 1, 7),
        "seven options");
    AssertThrows<ArgumentException>(
        () => AnswerSheetLayout.Create("invalid\0xml", 1, 4),
        "XML-invalid title");
    AssertThrows<ArgumentOutOfRangeException>(
        () => AnswerSheetLayout.Create(new string('中', AnswerSheetLayout.MaxTitleCharacters + 1), 1, 4),
        "title above the Unicode character limit");
}

static void VerifyTitleEscaping()
{
    const string title = "A & <B> \"C\" 'D'";
    var layout = AnswerSheetLayout.Create(title, 2, 4);
    var svg = layout.ToSvg();

    AssertTrue(svg.Contains("A &amp; &lt;B&gt; &quot;C&quot; &apos;D&apos;", StringComparison.Ordinal), "title should be escaped in SVG text");
    var document = XDocument.Parse(svg, LoadOptions.PreserveWhitespace);
    AssertEqual(title, document.Root?.Element(XName.Get("title", "http://www.w3.org/2000/svg"))?.Value, "XML parser should recover the original title");
    AssertTrue(svg.Contains("width=\"210mm\" height=\"297mm\"", StringComparison.Ordinal), "SVG should declare A4 dimensions");
    AssertTrue(svg.Contains("viewBox=\"0 0 210 297\"", StringComparison.Ordinal), "SVG should use the A4 millimetre viewBox");
}

static void VerifyDeterminism()
{
    var first = AnswerSheetLayout.Create("确定性", 44, 2).ToSvg();
    var second = AnswerSheetLayout.Create("确定性", 44, 2).ToSvg();
    AssertEqual(first, second, "same input should produce byte-identical SVG");
}

static void WriteExampleSvg()
{
    var repositoryRoot = FindRepositoryRoot();
    var outputDirectory = Path.Combine(repositoryRoot, "artifacts", "templates");
    Directory.CreateDirectory(outputDirectory);
    var outputPath = Path.Combine(outputDirectory, "m1-example-a4.svg");
    var svg = AnswerSheetLayout.Create("M1 示例答题纸", 30, 4).ToSvg();
    File.WriteAllText(outputPath, svg, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    Console.WriteLine($"PASS: generated {outputPath}.");
}

static string FindRepositoryRoot()
{
    var current = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (current is not null)
    {
        if (Directory.Exists(Path.Combine(current.FullName, ".git")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    throw new InvalidOperationException("Could not locate the repository root.");
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T? actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"{message} Expected: {FormatValue(expected)}; actual: {FormatValue(actual)}.");
    }
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name} for {message}.");
}

static string FormatValue<T>(T? value)
{
    return value is IFormattable formattable
        ? formattable.ToString(null, CultureInfo.InvariantCulture) ?? "<null>"
        : value?.ToString() ?? "<null>";
}
