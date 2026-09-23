using Omrina.Core;

internal static class RecognitionRegression
{
    public static void Run()
    {
        var layout = AnswerSheetLayout.Create("识别回归", 20, 4);
        var recognizer = new AnswerSheetRecognizer();

        var upright = Render(layout, PageOrientation.Degrees0, selected: question => question.Number % 4);
        var uprightResult = recognizer.Recognize(layout, upright);
        AssertEqual(RecognitionStatus.Accepted, uprightResult.Status, "upright page should be accepted");
        AssertEqual(PageOrientation.Degrees0, uprightResult.Orientation, "upright orientation");
        AssertTrue(uprightResult.Questions.All(question => question.State == QuestionMarkState.Single), "upright questions should be single");

        foreach (var orientation in new[]
                 {
                     PageOrientation.Degrees90,
                     PageOrientation.Degrees180,
                     PageOrientation.Degrees270
                 })
        {
            var rotated = Render(layout, orientation, selected: question => (question.Number + 1) % 4);
            var result = recognizer.Recognize(layout, rotated);
            AssertEqual(RecognitionStatus.Accepted, result.Status, $"{orientation} page should be accepted");
            AssertEqual(orientation, result.Orientation, $"{orientation} orientation");
        }

        var transformed = Render(
            layout,
            PageOrientation.Degrees0,
            selected: question => question.Number % 4,
            transform: new AffinePageTransform(2.05, 0.045, 0.018, 2.08, 5, 4, 450, 640));
        var transformedResult = recognizer.Recognize(layout, transformed);
        AssertEqual(RecognitionStatus.Accepted, transformedResult.Status, "scaled and tilted page should be accepted");
        AssertTrue(transformedResult.Transform is not null, "accepted page should expose a transform");

        var perspectiveTransform = new PageTransform(
            2.04,
            0.04,
            5,
            0.02,
            2.08,
            4,
            0.0003,
            0.00015,
            1);
        var perspective = Render(
            layout,
            PageOrientation.Degrees0,
            selected: question => question.Number % 4,
            projectiveTransform: perspectiveTransform,
            outputWidth: 450,
            outputHeight: 640);
        var perspectiveResult = recognizer.Recognize(layout, perspective);
        AssertEqual(RecognitionStatus.Accepted, perspectiveResult.Status, "perspective page should be accepted");
        AssertEqual(PageOrientation.Degrees0, perspectiveResult.Orientation, "perspective orientation");

        var blank = Render(layout, PageOrientation.Degrees0, selected: _ => -1);
        var blankResult = recognizer.Recognize(layout, blank);
        AssertEqual(RecognitionStatus.ReviewRequired, blankResult.Status, "blank page should require review");
        AssertTrue(blankResult.Questions.All(question => question.State == QuestionMarkState.Blank), "blank questions should be blank");

        var multiple = Render(layout, PageOrientation.Degrees0, selected: _ => 0, extraSelected: (_, option) => option == 1);
        var multipleResult = recognizer.Recognize(layout, multiple);
        AssertEqual(RecognitionStatus.ReviewRequired, multipleResult.Status, "multiple marks should require review");
        AssertTrue(multipleResult.Questions.All(question => question.State == QuestionMarkState.Multiple), "multiple questions should be multiple");

        var light = Render(layout, PageOrientation.Degrees0, selected: _ => 0, fillValue: 210);
        var lightResult = recognizer.Recognize(layout, light);
        AssertEqual(RecognitionStatus.ReviewRequired, lightResult.Status, "light fill should require review");
        AssertTrue(lightResult.Questions.Any(question => question.State == QuestionMarkState.Uncertain), "light fill should be uncertain");

        var weakPage = Render(layout, PageOrientation.Degrees0, selected: _ => 0, fillValue: 165);
        var weakPageResult = recognizer.Recognize(layout, weakPage);
        AssertEqual(RecognitionStatus.ReviewRequired, weakPageResult.Status, "weak page quality should require review");
        AssertTrue(weakPageResult.Questions.All(question => question.State == QuestionMarkState.Single), "weak page can still have single answers");
        AssertTrue(weakPageResult.Diagnostics.Any(diagnostic =>
            diagnostic.Code == RecognitionDiagnosticCode.PageQualityUncertain
            && diagnostic.Severity == RecognitionDiagnosticSeverity.Warning), "page quality must be distinguishable from per-question uncertainty");
        AssertTrue(weakPageResult.Diagnostics.All(diagnostic =>
            diagnostic.Code != RecognitionDiagnosticCode.BubbleMeasurementsUncertain), "single answers must not create a question-level warning");

        var missingMark = Render(layout, PageOrientation.Degrees0, selected: question => question.Number % 4, omitRegistration: "registration-bottom-right");
        var missingResult = recognizer.Recognize(layout, missingMark);
        AssertEqual(RecognitionStatus.Rejected, missingResult.Status, "missing registration mark should reject");
        AssertTrue(
            missingResult.Diagnostics.Any(diagnostic => diagnostic.Code == RecognitionDiagnosticCode.RegistrationMarksMissing),
            "missing registration diagnostic");

        var fakeMark = Render(
            layout,
            PageOrientation.Degrees0,
            selected: question => question.Number % 4,
            omitRegistration: "registration-bottom-right",
            extraDarkRectangle: new RectangleMm(104, 140, AnswerSheetLayout.RegistrationMarkSizeMm, AnswerSheetLayout.RegistrationMarkSizeMm));
        var fakeMarkResult = recognizer.Recognize(layout, fakeMark);
        AssertEqual(RecognitionStatus.Rejected, fakeMarkResult.Status, "a false mark must not replace a missing corner");
        AssertTrue(
            fakeMarkResult.Diagnostics.Any(diagnostic =>
                diagnostic.Code is RecognitionDiagnosticCode.RegistrationMarksMissing
                    or RecognitionDiagnosticCode.RegistrationMarksMismatch
                    or RecognitionDiagnosticCode.RegistrationMarksAmbiguous),
            "false mark should produce a registration diagnostic");

        var fourFalseMarks = Render(
            layout,
            PageOrientation.Degrees0,
            selected: question => question.Number % 4,
            omitAllRegistration: true,
            extraDarkRectangles:
            [
                new RectangleMm(46, 88, AnswerSheetLayout.RegistrationMarkSizeMm, AnswerSheetLayout.RegistrationMarkSizeMm),
                new RectangleMm(164, 88, AnswerSheetLayout.RegistrationMarkSizeMm, AnswerSheetLayout.RegistrationMarkSizeMm),
                new RectangleMm(46, 168, AnswerSheetLayout.RegistrationMarkSizeMm, AnswerSheetLayout.RegistrationMarkSizeMm),
                new RectangleMm(164, 168, AnswerSheetLayout.RegistrationMarkSizeMm, AnswerSheetLayout.RegistrationMarkSizeMm)
            ]);
        var fourFalseMarksResult = recognizer.Recognize(layout, fourFalseMarks);
        AssertEqual(RecognitionStatus.Rejected, fourFalseMarksResult.Status, "an interior A4-sized square set must not become a page");
        AssertTrue(
            fourFalseMarksResult.Diagnostics.Any(diagnostic =>
                diagnostic.Code is RecognitionDiagnosticCode.RegistrationMarksMissing
                    or RecognitionDiagnosticCode.RegistrationMarksMismatch
                    or RecognitionDiagnosticCode.RegistrationMarksAmbiguous),
            "interior square set should produce a registration diagnostic");

        var singular = new PageTransform(1, 0, 0, 0, 1, 0, 0, 1, -100);
        var singularPoint = singular.Map(new PointMm(0, 100));
        AssertTrue(double.IsNaN(singularPoint.X) && double.IsNaN(singularPoint.Y), "zero projective denominator must not map to black pixels");
        AssertTrue(!singular.TryMapInverse(new PointPx(0, 1), out _), "inverse projective horizon must fail");
    }

    private static GrayImage Render(
        AnswerSheetLayout layout,
        PageOrientation orientation,
        Func<QuestionGeometry, int> selected,
        Func<QuestionGeometry, int, bool>? extraSelected = null,
        AffinePageTransform? transform = null,
        string? omitRegistration = null,
        byte fillValue = 0,
        PageTransform? projectiveTransform = null,
        int outputWidth = 450,
        int outputHeight = 640,
        RectangleMm? extraDarkRectangle = null,
        bool omitAllRegistration = false,
        IReadOnlyList<RectangleMm>? extraDarkRectangles = null)
    {
        var pageTransform = transform ?? AffinePageTransform.ForOrientation(orientation, 2);
        var width = projectiveTransform is null ? pageTransform.OutputWidth : outputWidth;
        var height = projectiveTransform is null ? pageTransform.OutputHeight : outputHeight;
        var pixels = Enumerable.Repeat((byte)255, checked(width * height)).ToArray();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double pageX;
                double pageY;
                if (projectiveTransform is { } projective)
                {
                    if (!projective.TryMapInverse(new PointPx(x + 0.5, y + 0.5), out var pagePoint))
                    {
                        continue;
                    }

                    pageX = pagePoint.X;
                    pageY = pagePoint.Y;
                }
                else if (!pageTransform.TryInverse(x + 0.5, y + 0.5, out pageX, out pageY))
                {
                    continue;
                }

                var value = (byte)255;
                foreach (var registration in layout.RegistrationMarks)
                {
                    if (omitAllRegistration || registration.Id == omitRegistration)
                    {
                        continue;
                    }

                    if (InsideRectangle(pageX, pageY, registration.TopLeft.X, registration.TopLeft.Y, registration.SizeMm, registration.SizeMm))
                    {
                        value = 0;
                    }
                }

                var marker = layout.OrientationMarker;
                if (InsideRectangle(pageX, pageY, marker.TopLeft.X, marker.TopLeft.Y, marker.WidthMm, marker.HeightMm))
                {
                    value = 0;
                }

                if (extraDarkRectangle is { } falseMark
                    && InsideRectangle(pageX, pageY, falseMark.Left, falseMark.Top, falseMark.Width, falseMark.Height))
                {
                    value = 0;
                }

                if (extraDarkRectangles?.Any(falseMark =>
                        InsideRectangle(pageX, pageY, falseMark.Left, falseMark.Top, falseMark.Width, falseMark.Height)) == true)
                {
                    value = 0;
                }

                foreach (var question in layout.Questions)
                {
                    var selectedIndex = selected(question);
                    foreach (var bubble in question.Bubbles)
                    {
                        var distance = Math.Sqrt(
                            Math.Pow(pageX - bubble.Center.X, 2)
                            + Math.Pow(pageY - bubble.Center.Y, 2));
                        var isSelected = bubble.OptionIndex - 1 == selectedIndex
                            || (extraSelected?.Invoke(question, bubble.OptionIndex - 1) ?? false);
                        if (isSelected && distance <= bubble.RadiusMm * 0.72)
                        {
                            value = fillValue;
                        }
                        else if (Math.Abs(distance - bubble.RadiusMm) <= 0.55)
                        {
                            value = 0;
                        }
                    }
                }

                pixels[y * width + x] = value;
            }
        }

        return new GrayImage(width, height, pixels, copy: false);
    }

    private static bool InsideRectangle(double x, double y, double left, double top, double width, double height)
    {
        return x >= left && x <= left + width && y >= top && y <= top + height;
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}; expected {expected}, actual {actual}");
        }
    }

    private readonly record struct RectangleMm(double Left, double Top, double Width, double Height);

    private readonly record struct AffinePageTransform(
        double A,
        double B,
        double C,
        double D,
        double OffsetX,
        double OffsetY,
        int OutputWidth,
        int OutputHeight)
    {
        public static AffinePageTransform ForOrientation(PageOrientation orientation, double scale)
        {
            return orientation switch
            {
                PageOrientation.Degrees0 => new AffinePageTransform(
                    scale,
                    0,
                    0,
                    scale,
                    0,
                    0,
                    (int)(AnswerSheetLayout.PageWidthMm * scale),
                    (int)(AnswerSheetLayout.PageHeightMm * scale)),
                PageOrientation.Degrees90 => new AffinePageTransform(
                    0,
                    -scale,
                    scale,
                    0,
                    AnswerSheetLayout.PageHeightMm * scale,
                    0,
                    (int)(AnswerSheetLayout.PageHeightMm * scale),
                    (int)(AnswerSheetLayout.PageWidthMm * scale)),
                PageOrientation.Degrees180 => new AffinePageTransform(
                    -scale,
                    0,
                    0,
                    -scale,
                    AnswerSheetLayout.PageWidthMm * scale,
                    AnswerSheetLayout.PageHeightMm * scale,
                    (int)(AnswerSheetLayout.PageWidthMm * scale),
                    (int)(AnswerSheetLayout.PageHeightMm * scale)),
                PageOrientation.Degrees270 => new AffinePageTransform(
                    0,
                    scale,
                    -scale,
                    0,
                    0,
                    AnswerSheetLayout.PageWidthMm * scale,
                    (int)(AnswerSheetLayout.PageHeightMm * scale),
                    (int)(AnswerSheetLayout.PageWidthMm * scale)),
                _ => throw new ArgumentOutOfRangeException(nameof(orientation), orientation, null)
            };
        }

        public bool TryInverse(double imageX, double imageY, out double pageX, out double pageY)
        {
            var determinant = A * D - B * C;
            if (Math.Abs(determinant) < 1e-10)
            {
                pageX = 0;
                pageY = 0;
                return false;
            }

            var translatedX = imageX - OffsetX;
            var translatedY = imageY - OffsetY;
            pageX = (D * translatedX - B * translatedY) / determinant;
            pageY = (-C * translatedX + A * translatedY) / determinant;
            return pageX >= 0 && pageX <= AnswerSheetLayout.PageWidthMm
                && pageY >= 0 && pageY <= AnswerSheetLayout.PageHeightMm;
        }
    }
}
