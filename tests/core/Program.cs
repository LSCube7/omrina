using System.Globalization;
using System.Xml.Linq;
using Omrina.Core;

var failures = new List<string>();

Run("valid maximum capacity", failures, VerifyMaximumCapacity);
Run("option count capacities", failures, VerifyOptionCountCapacities);
Run("overflow is rejected", failures, VerifyOverflow);
Run("bubbles do not overlap", failures, VerifyBubbleSpacing);
Run("geometry stays inside the page", failures, VerifyGeometryBounds);
Run("illegal inputs are rejected", failures, VerifyIllegalInputs);
Run("title is XML escaped", failures, VerifyTitleEscaping);
Run("layout is deterministic", failures, VerifyDeterminism);
Run("template identity is stable", failures, VerifyTemplateIdentity);
Run("recognition geometry and mark states", failures, RecognitionRegression.Run);

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

Console.WriteLine("PASS: 10 Omrina.Core regression tests.");
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

static void VerifyOptionCountCapacities()
{
    for (var options = AnswerSheetLayout.MinOptionsPerQuestion;
         options <= AnswerSheetLayout.MaxOptionsPerQuestion;
         options++)
    {
        var maximum = AnswerSheetLayout.MaxQuestionCount(options);
        var layout = AnswerSheetLayout.Create($"{options} 个选项", maximum, options);

        AssertEqual(44, maximum, $"A4 capacity should be 44 questions for {options} options");
        AssertEqual(maximum, layout.Questions.Count, $"all questions should fit for {options} options");
        AssertEqual(maximum * options, layout.Bubbles.Count, $"bubble count should match for {options} options");
        AssertEqual(AnswerSheetLayout.ColumnCount * options, layout.OptionHeaders.Count, $"header count should match for {options} options");
    }
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

    var orientationMarker = layout.OrientationMarker;
    AssertTrue(orientationMarker.TopLeft.X >= 0 && orientationMarker.TopLeft.Y >= 0, "orientation marker should start inside the page");
    AssertTrue(orientationMarker.WidthMm > 0 && orientationMarker.HeightMm > 0, "orientation marker should have positive dimensions");
    AssertTrue(orientationMarker.TopLeft.X + orientationMarker.WidthMm <= AnswerSheetLayout.PageWidthMm, "orientation marker should fit horizontally");
    AssertTrue(orientationMarker.TopLeft.Y + orientationMarker.HeightMm <= AnswerSheetLayout.PageHeightMm, "orientation marker should fit vertically");
    AssertTrue(
        orientationMarker.TopLeft.Y + orientationMarker.HeightMm < layout.TemplateNumberPosition.Y - 3.5,
        "orientation marker should be separated from the template number");

    foreach (var mark in layout.RegistrationMarks)
    {
        AssertTrue(mark.TopLeft.X >= 0 && mark.TopLeft.Y >= 0, $"registration mark {mark.Id} starts inside the page");
        AssertTrue(mark.TopLeft.X + mark.SizeMm <= AnswerSheetLayout.PageWidthMm, $"registration mark {mark.Id} fits horizontally");
        AssertTrue(mark.TopLeft.Y + mark.SizeMm <= AnswerSheetLayout.PageHeightMm, $"registration mark {mark.Id} fits vertically");
        AssertTrue(
            !RectanglesOverlap(orientationMarker.TopLeft, orientationMarker.WidthMm, orientationMarker.HeightMm, mark.TopLeft, mark.SizeMm, mark.SizeMm),
            $"orientation marker should not intersect {mark.Id}");
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

        AssertTrue(
            !CircleIntersectsRectangle(
                bubble.Center,
                bubble.RadiusMm,
                orientationMarker.TopLeft,
                orientationMarker.WidthMm,
                orientationMarker.HeightMm),
            $"bubble {bubble.QuestionNumber}/{bubble.OptionLabel} should not intersect the orientation marker");
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

static void VerifyTemplateIdentity()
{
    var first = AnswerSheetLayout.Create("稳定模板", 20, 4);
    var second = AnswerSheetLayout.Create("稳定模板", 20, 4);
    var decomposed = AnswerSheetLayout.Create("Cafe\u0301", 20, 4);
    var composed = AnswerSheetLayout.Create("Café", 20, 4);
    var differentQuestionCount = AnswerSheetLayout.Create("稳定模板", 21, 4);
    var differentOptionCount = AnswerSheetLayout.Create("稳定模板", 20, 5);
    var differentTitle = AnswerSheetLayout.Create("另一模板", 20, 4);

    AssertEqual(first.TemplateId, second.TemplateId, "same parameters should produce the same template ID");
    AssertEqual(first.TemplateNumber, second.TemplateNumber, "same parameters should produce the same template number");
    AssertEqual(first.TemplateId.Length, 64, "template ID should contain the complete SHA-256 hex digest");
    AssertEqual(first.TemplateId.ToLowerInvariant(), first.TemplateId, "template ID should use lowercase hexadecimal");
    AssertTrue(first.TemplateId != differentQuestionCount.TemplateId, "question count should affect template ID");
    AssertTrue(first.TemplateId != differentOptionCount.TemplateId, "option count should affect template ID");
    AssertTrue(first.TemplateId != differentTitle.TemplateId, "title should affect template ID");
    AssertEqual(composed.Title, decomposed.Title, "title should be normalized to NFC");
    AssertEqual(composed.TemplateId, decomposed.TemplateId, "equivalent NFC titles should share the template ID");
    AssertEqual(composed.ToSvg(), decomposed.ToSvg(), "equivalent NFC titles should share deterministic SVG");

    var svg = first.ToSvg();
    var document = XDocument.Parse(svg, LoadOptions.PreserveWhitespace);
    var svgNamespace = XNamespace.Get("http://www.w3.org/2000/svg");
    var metadata = document.Root?.Element(svgNamespace + "metadata")?.Element(svgNamespace + "answer-sheet-template");
    AssertEqual(first.TemplateId, metadata?.Attribute("template-id")?.Value, "SVG metadata should carry the template ID");
    AssertEqual(first.TemplateNumber, metadata?.Attribute("template-number")?.Value, "SVG metadata should carry the template number");
    AssertTrue(svg.Contains($"data-role=\"orientation-marker\"", StringComparison.Ordinal), "SVG should carry the orientation marker geometry");
    AssertTrue(svg.Contains(first.TemplateNumber, StringComparison.Ordinal), "SVG should visibly print the template number");
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

static bool RectanglesOverlap(
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

static bool CircleIntersectsRectangle(
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
