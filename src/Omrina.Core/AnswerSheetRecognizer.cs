namespace Omrina.Core;

/// <summary>
/// Platform-independent answer-sheet recognizer. The caller must associate the
/// capture with a trusted <see cref="AnswerSheetLayout"/> before invoking it.
/// </summary>
public interface IAnswerSheetRecognizer
{
    RecognitionResult Recognize(
        AnswerSheetLayout layout,
        GrayImage image,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Locates registration marks, determines page orientation, and measures the
/// answer bubbles from an 8-bit grayscale capture.
///
/// The quality score exposed by this class is an engineering heuristic. It is
/// deliberately not presented as a probability or a machine-learning score.
/// </summary>
public sealed class AnswerSheetRecognizer : IAnswerSheetRecognizer
{
    private const int AnalysisMaximumDimension = 2400;
    private const byte DarkPixelThreshold = 160;
    private const double MinimumMarkerDarkness = 0.22;
    private const double MinimumOrientationGap = 0.08;
    private const double MinimumOptionEvidence = 0.15;
    private const double BlankOptionEvidence = 0.08;
    private const double StrongOptionEvidence = 0.28;
    private const double BubbleSampleRadiusRatio = 0.64;
    private const int BubbleSampleGridSize = 9;
    private const double MaximumRegistrationAspectError = 0.42;

    public RecognitionResult Recognize(
        AnswerSheetLayout layout,
        GrayImage image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        if (image.Width < 120 || image.Height < 120)
        {
            return Rejected(
                layout,
                [Diagnostic(
                    RecognitionDiagnosticCode.ImageTooSmall,
                    RecognitionDiagnosticSeverity.Error,
                    "图像尺寸过小，无法可靠定位答题纸。")]);
        }

        var components = FindDarkComponents(image, cancellationToken);
        if (components.Count < 4)
        {
            return Rejected(
                layout,
                [Diagnostic(
                    RecognitionDiagnosticCode.RegistrationMarksMissing,
                    RecognitionDiagnosticSeverity.Error,
                    "未检测到四个完整的页面定位块。",
                    components.Count / 4d)]);
        }

        if (!TrySelectRegistrationMarks(
                components,
                out var marks,
                out var registrationScore,
                out var selectionAmbiguous))
        {
            var largestComponentArea = components.Count == 0
                ? 0
                : components.Max(component => component.Area);
            var likelyMissing = components.Count(component =>
                component.Fill >= 0.38
                && component.Area >= largestComponentArea * 0.65) < 4;
            return Rejected(
                layout,
                [Diagnostic(
                    likelyMissing
                        ? RecognitionDiagnosticCode.RegistrationMarksMissing
                        : selectionAmbiguous
                        ? RecognitionDiagnosticCode.RegistrationMarksAmbiguous
                        : RecognitionDiagnosticCode.RegistrationMarksMismatch,
                    RecognitionDiagnosticSeverity.Error,
                    likelyMissing
                        ? "未检测到四个完整的页面定位块。"
                        : selectionAmbiguous
                        ? "页面定位块存在多个同样可信的组合。"
                        : "检测到的定位块不能组成当前答题纸的四角。")]);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryResolveOrientation(
                layout,
                image,
                marks,
                out var orientation,
                out var transform,
                out var markerScore,
                out var orientationAmbiguous))
        {
            var code = orientationAmbiguous
                ? RecognitionDiagnosticCode.OrientationAmbiguous
                : RecognitionDiagnosticCode.OrientationMarkerMissing;
            var message = orientationAmbiguous
                ? "方向标记无法唯一确定页面方向。"
                : "未检测到有效的顶部方向标记。";
            return Rejected(
                layout,
                [Diagnostic(code, RecognitionDiagnosticSeverity.Error, message, markerScore)]);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var questions = ReadQuestions(layout, image, transform, cancellationToken, out var uncertainQuestionCount);
        var diagnostics = new List<RecognitionDiagnostic>();
        var questionConfidence = questions.Count == 0
            ? 0
            : questions.Average(question => question.Confidence);
        var qualityNeedsReview = registrationScore < 0.60
            || markerScore < 0.55
            || questionConfidence < 0.55;
        if (uncertainQuestionCount > 0)
        {
            diagnostics.Add(Diagnostic(
                RecognitionDiagnosticCode.BubbleMeasurementsUncertain,
                RecognitionDiagnosticSeverity.Warning,
                $"有 {uncertainQuestionCount} 道题的填涂需要人工复核。",
                Math.Clamp(questionConfidence, 0, 1)));
        }

        if (qualityNeedsReview)
        {
            diagnostics.Add(Diagnostic(
                RecognitionDiagnosticCode.PageQualityUncertain,
                RecognitionDiagnosticSeverity.Warning,
                "页面定位、方向标记或整体填涂质量不足以自动接受，需要检查扫描图像。",
                Math.Clamp(
                    Math.Min(
                        registrationScore,
                        Math.Min(markerScore, questionConfidence)),
                    0,
                    1)));
        }

        var status = uncertainQuestionCount == 0 && !qualityNeedsReview
            ? RecognitionStatus.Accepted
            : RecognitionStatus.ReviewRequired;
        var confidence = Math.Clamp(
            0.55 * markerScore + 0.45 * questionConfidence,
            0,
            1);

        return new RecognitionResult(
            layout.TemplateId,
            AnswerSheetLayout.TemplateSchemaVersion,
            status,
            orientation,
            transform,
            confidence,
            questions,
            diagnostics);
    }

    /// <summary>Convenience overload retained for callers that put the image first.</summary>
    public RecognitionResult Recognize(
        GrayImage image,
        AnswerSheetLayout layout,
        CancellationToken cancellationToken = default)
    {
        return Recognize(layout, image, cancellationToken);
    }

    private static RecognitionResult Rejected(
        AnswerSheetLayout layout,
        IReadOnlyList<RecognitionDiagnostic> diagnostics)
    {
        return new RecognitionResult(
            layout.TemplateId,
            AnswerSheetLayout.TemplateSchemaVersion,
            RecognitionStatus.Rejected,
            PageOrientation.Unknown,
            transform: null,
            confidence: 0,
            questions: Array.Empty<QuestionRecognitionResult>(),
            diagnostics);
    }

    private static RecognitionDiagnostic Diagnostic(
        RecognitionDiagnosticCode code,
        RecognitionDiagnosticSeverity severity,
        string message,
        double? score = null)
    {
        return new RecognitionDiagnostic(code, severity, message, score);
    }

    private static List<DarkComponent> FindDarkComponents(
        GrayImage image,
        CancellationToken cancellationToken)
    {
        var scale = Math.Max(
            1,
            (int)Math.Ceiling(Math.Max(image.Width, image.Height) / (double)AnalysisMaximumDimension));
        var width = (image.Width + scale - 1) / scale;
        var height = (image.Height + scale - 1) / scale;
        var dark = new bool[checked(width * height)];

        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceY = Math.Min(image.Height - 1, y * scale + scale / 2);
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(image.Width - 1, x * scale + scale / 2);
                dark[y * width + x] = image.GetPixel(sourceX, sourceY) <= DarkPixelThreshold;
            }
        }

        var visited = new bool[dark.Length];
        var queue = new int[dark.Length];
        var components = new List<DarkComponent>();
        for (var start = 0; start < dark.Length; start++)
        {
            if (!dark[start] || visited[start])
            {
                continue;
            }

            var head = 0;
            var tail = 0;
            queue[tail++] = start;
            visited[start] = true;
            var minX = start % width;
            var maxX = minX;
            var minY = start / width;
            var maxY = minY;
            var count = 0;
            var sumX = 0L;
            var sumY = 0L;

            while (head < tail)
            {
                var current = queue[head++];
                var currentX = current % width;
                var currentY = current / width;
                count++;
                sumX += currentX;
                sumY += currentY;
                minX = Math.Min(minX, currentX);
                maxX = Math.Max(maxX, currentX);
                minY = Math.Min(minY, currentY);
                maxY = Math.Max(maxY, currentY);

                for (var deltaY = -1; deltaY <= 1; deltaY++)
                {
                    for (var deltaX = -1; deltaX <= 1; deltaX++)
                    {
                        if (deltaX == 0 && deltaY == 0)
                        {
                            continue;
                        }

                        var nextX = currentX + deltaX;
                        var nextY = currentY + deltaY;
                        if ((uint)nextX >= (uint)width || (uint)nextY >= (uint)height)
                        {
                            continue;
                        }

                        var next = nextY * width + nextX;
                        if (dark[next] && !visited[next])
                        {
                            visited[next] = true;
                            queue[tail++] = next;
                        }
                    }
                }
            }

            var boxWidth = maxX - minX + 1;
            var boxHeight = maxY - minY + 1;
            var fill = count / (double)(boxWidth * boxHeight);
            if (count < 8 || fill < 0.28)
            {
                continue;
            }

            components.Add(new DarkComponent(
                new PointPx(
                    (sumX / (double)count + 0.5) * scale,
                    (sumY / (double)count + 0.5) * scale),
                count * scale * scale,
                boxWidth * scale,
                boxHeight * scale,
                fill));
        }

        return components;
    }

    private static bool TrySelectRegistrationMarks(
        IReadOnlyList<DarkComponent> components,
        out IReadOnlyList<PointPx> marks,
        out double registrationScore,
        out bool ambiguous)
    {
        marks = Array.Empty<PointPx>();
        registrationScore = 0;
        ambiguous = false;

        var candidates = components
            .Where(component =>
                component.Fill >= 0.38
                && component.Width / Math.Max(1, component.Height) is >= 0.42 and <= 2.4)
            .OrderByDescending(component => component.Area)
            .Take(14)
            .ToArray();
        if (candidates.Length < 4)
        {
            return false;
        }

        CandidateSet? best = null;
        CandidateSet? second = null;
        for (var first = 0; first < candidates.Length - 3; first++)
        {
            for (var secondIndex = first + 1; secondIndex < candidates.Length - 2; secondIndex++)
            {
                for (var third = secondIndex + 1; third < candidates.Length - 1; third++)
                {
                    for (var fourth = third + 1; fourth < candidates.Length; fourth++)
                    {
                        var set = new[]
                        {
                            candidates[first],
                            candidates[secondIndex],
                            candidates[third],
                            candidates[fourth]
                        };
                        if (!TryOrderCorners(set, out var ordered))
                        {
                            continue;
                        }

                        if (!IsPlausibleRegistrationQuadrilateral(ordered))
                        {
                            continue;
                        }

                        var shapeScore = RegistrationShapeScore(ordered);
                        var sizeScore = RegistrationSizeScore(ordered);
                        var areaScore = AreaConsistencyScore(set);
                        var score = 0.52 * shapeScore
                            + 0.22 * sizeScore
                            + 0.16 * areaScore
                            + 0.10 * set.Average(item => item.Fill);
                        if (best is null || score > best.Score)
                        {
                            second = best;
                            best = new CandidateSet(ordered, score);
                        }
                        else if (second is null || score > second.Score)
                        {
                            second = new CandidateSet(ordered, score);
                        }
                    }
                }
            }
        }

        if (best is null || best.Score < 0.35)
        {
            return false;
        }

        if (second is not null && best.Score - second.Score < 0.025)
        {
            ambiguous = true;
            return false;
        }

        marks = [
            best.Ordered.TopLeft.Center,
            best.Ordered.TopRight.Center,
            best.Ordered.BottomLeft.Center,
            best.Ordered.BottomRight.Center
        ];
        registrationScore = best.Score;
        return true;
    }

    private static double RegistrationShapeScore(OrderedCorners corners)
    {
        var top = Distance(corners.TopLeft.Center, corners.TopRight.Center);
        var bottom = Distance(corners.BottomLeft.Center, corners.BottomRight.Center);
        var left = Distance(corners.TopLeft.Center, corners.BottomLeft.Center);
        var right = Distance(corners.TopRight.Center, corners.BottomRight.Center);
        if (Math.Min(Math.Min(top, bottom), Math.Min(left, right)) < 10)
        {
            return 0;
        }

        var horizontalConsistency = Math.Min(top, bottom) / Math.Max(top, bottom);
        var verticalConsistency = Math.Min(left, right) / Math.Max(left, right);
        var aspect = (top + bottom) / Math.Max(1e-9, left + right);
        var expected = AnswerSheetLayout.PageWidthMm / AnswerSheetLayout.PageHeightMm;
        var expectedRotated = 1 / expected;
        var aspectError = Math.Min(
            Math.Abs(Math.Log(Math.Max(1e-9, aspect / expected))),
            Math.Abs(Math.Log(Math.Max(1e-9, aspect / expectedRotated))));
        var aspectScore = Math.Clamp(1 - aspectError / 0.65, 0, 1);
        var convexScore = IsConvex(corners) ? 1 : 0;
        return 0.32 * horizontalConsistency
            + 0.32 * verticalConsistency
            + 0.20 * aspectScore
            + 0.16 * convexScore;
    }

    /// <summary>
    /// Rejects combinations that can make a tidy-looking quadrilateral but do
    /// not have the proportions of the printed A4 registration frame.  This is
    /// deliberately a gate rather than another small score contribution: a
    /// bubble or an unrelated black square must not replace a missing corner.
    /// </summary>
    private static bool IsPlausibleRegistrationQuadrilateral(OrderedCorners corners)
    {
        if (!IsConvex(corners))
        {
            return false;
        }

        var top = Distance(corners.TopLeft.Center, corners.TopRight.Center);
        var bottom = Distance(corners.BottomLeft.Center, corners.BottomRight.Center);
        var left = Distance(corners.TopLeft.Center, corners.BottomLeft.Center);
        var right = Distance(corners.TopRight.Center, corners.BottomRight.Center);
        if (!double.IsFinite(top)
            || !double.IsFinite(bottom)
            || !double.IsFinite(left)
            || !double.IsFinite(right)
            || Math.Min(Math.Min(top, bottom), Math.Min(left, right)) < 10)
        {
            return false;
        }

        // Perspective can make opposite sides different, but a complete page
        // must still remain close to A4's registration-centre aspect ratio.
        var aspect = (top + bottom) / (left + right);
        var expected = AnswerSheetLayout.PageWidthMm / AnswerSheetLayout.PageHeightMm;
        var aspectError = Math.Min(
            Math.Abs(Math.Log(aspect / expected)),
            Math.Abs(Math.Log(aspect * expected)));
        if (!double.IsFinite(aspectError) || aspectError > MaximumRegistrationAspectError)
        {
            return false;
        }

        return corners.TopLeft.Width / Math.Max(1, corners.TopLeft.Height) is >= 0.55 and <= 1.8
            && corners.TopRight.Width / Math.Max(1, corners.TopRight.Height) is >= 0.55 and <= 1.8
            && corners.BottomLeft.Width / Math.Max(1, corners.BottomLeft.Height) is >= 0.55 and <= 1.8
            && corners.BottomRight.Width / Math.Max(1, corners.BottomRight.Height) is >= 0.55 and <= 1.8;
    }

    private static double RegistrationSizeScore(OrderedCorners corners)
    {
        var horizontalSpan = (
            Distance(corners.TopLeft.Center, corners.TopRight.Center)
            + Distance(corners.BottomLeft.Center, corners.BottomRight.Center)) / 2;
        var verticalSpan = (
            Distance(corners.TopLeft.Center, corners.BottomLeft.Center)
            + Distance(corners.TopRight.Center, corners.BottomRight.Center)) / 2;
        if (horizontalSpan <= 0 || verticalSpan <= 0)
        {
            return 0;
        }

        var expectedMarkWidth = Math.Sqrt(
            horizontalSpan / 182d * verticalSpan / 269d) * AnswerSheetLayout.RegistrationMarkSizeMm;
        if (!double.IsFinite(expectedMarkWidth) || expectedMarkWidth <= 0)
        {
            return 0;
        }

        var scores = new[]
        {
            MarkerSizeScore(corners.TopLeft, expectedMarkWidth),
            MarkerSizeScore(corners.TopRight, expectedMarkWidth),
            MarkerSizeScore(corners.BottomLeft, expectedMarkWidth),
            MarkerSizeScore(corners.BottomRight, expectedMarkWidth)
        };
        return scores.Average();
    }

    private static double MarkerSizeScore(DarkComponent component, double expectedMarkWidth)
    {
        var widthRatio = component.Width / expectedMarkWidth;
        var heightRatio = component.Height / expectedMarkWidth;
        if (!double.IsFinite(widthRatio) || !double.IsFinite(heightRatio))
        {
            return 0;
        }

        return Math.Min(
            Math.Clamp(1 - Math.Abs(Math.Log(Math.Max(1e-9, widthRatio)) / Math.Log(2.2)), 0, 1),
            Math.Clamp(1 - Math.Abs(Math.Log(Math.Max(1e-9, heightRatio)) / Math.Log(2.2)), 0, 1));
    }

    private static bool IsConvex(OrderedCorners corners)
    {
        var points = new[]
        {
            corners.TopLeft.Center,
            corners.TopRight.Center,
            corners.BottomRight.Center,
            corners.BottomLeft.Center
        };
        var firstSign = 0d;
        for (var index = 0; index < points.Length; index++)
        {
            var current = points[index];
            var next = points[(index + 1) % points.Length];
            var after = points[(index + 2) % points.Length];
            var cross = Cross(
                new PointPx(next.X - current.X, next.Y - current.Y),
                new PointPx(after.X - next.X, after.Y - next.Y));
            if (Math.Abs(cross) < 1)
            {
                return false;
            }

            if (firstSign == 0)
            {
                firstSign = Math.Sign(cross);
            }
            else if (Math.Sign(cross) != Math.Sign(firstSign))
            {
                return false;
            }
        }

        return true;
    }

    private static double AreaConsistencyScore(IReadOnlyList<DarkComponent> components)
    {
        var average = components.Average(component => component.Area);
        if (average <= 0)
        {
            return 0;
        }

        var deviation = components.Average(component => Math.Abs(component.Area - average)) / average;
        return Math.Clamp(1 - deviation, 0, 1);
    }

    private static bool TryResolveOrientation(
        AnswerSheetLayout layout,
        GrayImage image,
        IReadOnlyList<PointPx> marks,
        out PageOrientation orientation,
        out PageTransform transform,
        out double markerScore,
        out bool ambiguous)
    {
        orientation = PageOrientation.Unknown;
        transform = default;
        markerScore = 0;
        ambiguous = false;

        if (!TryOrderCorners(marks, out var imageCorners))
        {
            return false;
        }

        var candidates = new List<OrientationCandidate>(4);
        foreach (var candidate in new[]
                 {
                     PageOrientation.Degrees0,
                     PageOrientation.Degrees90,
                     PageOrientation.Degrees180,
                     PageOrientation.Degrees270
                 })
        {
            var imagePoints = ImagePointsForOrientation(imageCorners, candidate);
            if (!TryCreateTransform(layout, imagePoints, out var candidateTransform))
            {
                continue;
            }

            if (!IsPageMappingUsable(image, candidateTransform))
            {
                continue;
            }

            var score = ScoreOrientationMarker(image, candidateTransform, layout.OrientationMarker);
            candidates.Add(new OrientationCandidate(candidate, candidateTransform, score));
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        candidates.Sort((left, right) => right.Score.CompareTo(left.Score));
        var best = candidates[0];
        markerScore = best.Score;
        if (best.Score < MinimumMarkerDarkness)
        {
            return false;
        }

        if (candidates.Count > 1 && best.Score - candidates[1].Score < MinimumOrientationGap)
        {
            ambiguous = true;
            return false;
        }

        orientation = best.Orientation;
        transform = best.Transform;
        return true;
    }

    private static PointPx[] ImagePointsForOrientation(
        OrderedCorners imageCorners,
        PageOrientation orientation)
    {
        return orientation switch
        {
            PageOrientation.Degrees0 =>
            [
                imageCorners.TopLeft.Center,
                imageCorners.TopRight.Center,
                imageCorners.BottomLeft.Center,
                imageCorners.BottomRight.Center
            ],
            PageOrientation.Degrees90 =>
            [
                imageCorners.TopRight.Center,
                imageCorners.BottomRight.Center,
                imageCorners.TopLeft.Center,
                imageCorners.BottomLeft.Center
            ],
            PageOrientation.Degrees180 =>
            [
                imageCorners.BottomRight.Center,
                imageCorners.BottomLeft.Center,
                imageCorners.TopRight.Center,
                imageCorners.TopLeft.Center
            ],
            PageOrientation.Degrees270 =>
            [
                imageCorners.BottomLeft.Center,
                imageCorners.TopLeft.Center,
                imageCorners.BottomRight.Center,
                imageCorners.TopRight.Center
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(orientation), orientation, null)
        };
    }

    private static bool TryCreateTransform(
        AnswerSheetLayout layout,
        IReadOnlyList<PointPx> imagePoints,
        out PageTransform transform)
    {
        var topLeft = layout.RegistrationMarks.First(mark => mark.Id == "registration-top-left");
        var topRight = layout.RegistrationMarks.First(mark => mark.Id == "registration-top-right");
        var bottomLeft = layout.RegistrationMarks.First(mark => mark.Id == "registration-bottom-left");
        var bottomRight = layout.RegistrationMarks.First(mark => mark.Id == "registration-bottom-right");
        var source = new[]
        {
            Center(topLeft),
            Center(topRight),
            Center(bottomLeft),
            Center(bottomRight)
        };
        return TrySolveHomography(source, imagePoints, out transform);
    }

    private static PointMm Center(RegistrationMark mark)
    {
        return new PointMm(
            mark.TopLeft.X + mark.SizeMm / 2,
            mark.TopLeft.Y + mark.SizeMm / 2);
    }

    private static double ScoreOrientationMarker(
        GrayImage image,
        PageTransform transform,
        OrientationMarker marker)
    {
        const int gridSize = 7;
        var darknessSum = 0d;
        var strongCount = 0;
        var sampleCount = 0;
        for (var row = 0; row < gridSize; row++)
        {
            for (var column = 0; column < gridSize; column++)
            {
                var x = marker.TopLeft.X + marker.WidthMm * (0.12 + 0.76 * column / (gridSize - 1));
                var y = marker.TopLeft.Y + marker.HeightMm * (0.12 + 0.76 * row / (gridSize - 1));
                var mapped = transform.Map(new PointMm(x, y));
                if (!TrySampleBilinear(image, mapped, out var value))
                {
                    return 0;
                }

                var darkness = (255 - value) / 255d;
                darknessSum += darkness;
                if (value <= 160)
                {
                    strongCount++;
                }

                sampleCount++;
            }
        }

        if (sampleCount == 0)
        {
            return 0;
        }

        return 0.55 * (darknessSum / sampleCount) + 0.45 * (strongCount / (double)sampleCount);
    }

    private static IReadOnlyList<QuestionRecognitionResult> ReadQuestions(
        AnswerSheetLayout layout,
        GrayImage image,
        PageTransform transform,
        CancellationToken cancellationToken,
        out int uncertainQuestionCount)
    {
        var questions = new List<QuestionRecognitionResult>(layout.Questions.Count);
        uncertainQuestionCount = 0;
        foreach (var question in layout.Questions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = new List<OptionRecognitionResult>(question.Bubbles.Count);
            foreach (var bubble in question.Bubbles)
            {
                var measurement = MeasureBubble(image, transform, bubble);
                options.Add(new OptionRecognitionResult(
                    bubble.OptionIndex,
                    bubble.OptionLabel,
                    measurement.FillRatio,
                    measurement.State,
                    measurement.Confidence));
            }

            var selected = options.Count(option => option.State == OptionMarkState.Selected);
            var uncertain = options.Any(option => option.State == OptionMarkState.Uncertain);
            var state = selected switch
            {
                > 1 => QuestionMarkState.Multiple,
                1 when !uncertain => QuestionMarkState.Single,
                0 when uncertain => QuestionMarkState.Uncertain,
                0 => QuestionMarkState.Blank,
                _ => QuestionMarkState.Uncertain
            };
            var confidence = state switch
            {
                QuestionMarkState.Single => options.Where(option => option.State == OptionMarkState.Selected).Min(option => option.Confidence),
                QuestionMarkState.Blank => options.Count == 0 ? 0 : options.Min(option => option.Confidence),
                QuestionMarkState.Multiple => Math.Clamp(options.Where(option => option.State == OptionMarkState.Selected).Average(option => option.Confidence) * 0.65, 0, 1),
                _ => Math.Clamp(options.Count == 0 ? 0 : options.Min(option => option.Confidence) * 0.6, 0, 1)
            };
            if (state != QuestionMarkState.Single)
            {
                uncertainQuestionCount++;
            }

            questions.Add(new QuestionRecognitionResult(question.Number, state, options, confidence));
        }

        return questions;
    }

    private static BubbleMeasurement MeasureBubble(
        GrayImage image,
        PageTransform transform,
        AnswerBubble bubble)
    {
        var darkCount = 0;
        var sampleCount = 0;
        var darknessSum = 0d;
        var radius = bubble.RadiusMm * BubbleSampleRadiusRatio;
        var radiusSquared = radius * radius;
        for (var row = 0; row < BubbleSampleGridSize; row++)
        {
            for (var column = 0; column < BubbleSampleGridSize; column++)
            {
                var normalizedX = column / (double)(BubbleSampleGridSize - 1) * 2 - 1;
                var normalizedY = row / (double)(BubbleSampleGridSize - 1) * 2 - 1;
                var localX = normalizedX * radius;
                var localY = normalizedY * radius;
                if (localX * localX + localY * localY > radiusSquared)
                {
                    continue;
                }

                var mapped = transform.Map(new PointMm(bubble.Center.X + localX, bubble.Center.Y + localY));
                if (!TrySampleBilinear(image, mapped, out var value))
                {
                    return new BubbleMeasurement(0, OptionMarkState.Uncertain, 0);
                }

                var darkness = (255 - value) / 255d;
                darknessSum += darkness;
                if (value <= DarkPixelThreshold)
                {
                    darkCount++;
                }

                sampleCount++;
            }
        }

        if (sampleCount == 0)
        {
            return new BubbleMeasurement(0, OptionMarkState.Uncertain, 0);
        }

        var fillRatio = darkCount / (double)sampleCount;
        var meanDarkness = darknessSum / sampleCount;
        var evidence = Math.Max(fillRatio, meanDarkness);
        if (evidence >= StrongOptionEvidence)
        {
            var confidence = Math.Clamp(
                (evidence - StrongOptionEvidence) / (1 - StrongOptionEvidence) * 0.75 + 0.25,
                0,
                1);
            return new BubbleMeasurement(fillRatio, OptionMarkState.Selected, confidence);
        }

        if (evidence <= BlankOptionEvidence)
        {
            var confidence = Math.Clamp(
                (BlankOptionEvidence - evidence) / BlankOptionEvidence * 0.75 + 0.25,
                0,
                1);
            return new BubbleMeasurement(fillRatio, OptionMarkState.Unselected, confidence);
        }

        var uncertainConfidence = Math.Clamp(
            1 - Math.Abs(evidence - MinimumOptionEvidence) / StrongOptionEvidence,
            0.1,
            0.55);
        return new BubbleMeasurement(fillRatio, OptionMarkState.Uncertain, uncertainConfidence);
    }

    private static bool TrySampleBilinear(GrayImage image, PointPx point, out byte value)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            value = 0;
            return false;
        }

        if (point.X < 0 || point.Y < 0 || point.X > image.Width - 1 || point.Y > image.Height - 1)
        {
            value = 0;
            return false;
        }

        var x = point.X;
        var y = point.Y;
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var x1 = Math.Min(image.Width - 1, x0 + 1);
        var y1 = Math.Min(image.Height - 1, y0 + 1);
        var fx = x - x0;
        var fy = y - y0;
        var top = image.GetPixel(x0, y0) * (1 - fx) + image.GetPixel(x1, y0) * fx;
        var bottom = image.GetPixel(x0, y1) * (1 - fx) + image.GetPixel(x1, y1) * fx;
        value = (byte)Math.Clamp(Math.Round(top * (1 - fy) + bottom * fy), 0, 255);
        return true;
    }

    private static bool IsPageMappingUsable(GrayImage image, PageTransform transform)
    {
        var margin = Math.Max(8, Math.Min(image.Width, image.Height) * 0.02);
        var previousCellSigns = new List<double>();
        const int gridSize = 5;
        var mapped = new PointPx[gridSize, gridSize];
        for (var row = 0; row < gridSize; row++)
        {
            for (var column = 0; column < gridSize; column++)
            {
                var pagePoint = new PointMm(
                    AnswerSheetLayout.PageWidthMm * column / (gridSize - 1d),
                    AnswerSheetLayout.PageHeightMm * row / (gridSize - 1d));
                var point = transform.Map(pagePoint);
                if (!double.IsFinite(point.X)
                    || !double.IsFinite(point.Y)
                    || point.X < -margin
                    || point.Y < -margin
                    || point.X > image.Width - 1 + margin
                    || point.Y > image.Height - 1 + margin)
                {
                    return false;
                }

                mapped[row, column] = point;
            }
        }

        // A finite map at four corners is not sufficient for a projective
        // transform.  Require every sampled cell to preserve winding and area,
        // which rejects a singular denominator or a folded page domain.
        for (var row = 0; row < gridSize - 1; row++)
        {
            for (var column = 0; column < gridSize - 1; column++)
            {
                var horizontal = new PointPx(
                    mapped[row, column + 1].X - mapped[row, column].X,
                    mapped[row, column + 1].Y - mapped[row, column].Y);
                var vertical = new PointPx(
                    mapped[row + 1, column].X - mapped[row, column].X,
                    mapped[row + 1, column].Y - mapped[row, column].Y);
                var area = Cross(horizontal, vertical);
                if (!double.IsFinite(area) || Math.Abs(area) < 1e-6)
                {
                    return false;
                }

                previousCellSigns.Add(Math.Sign(area));
            }
        }

        if (previousCellSigns.Any(sign => sign != previousCellSigns[0]))
        {
            return false;
        }

        return true;
    }

    private static bool TryOrderCorners(
        IReadOnlyList<DarkComponent> components,
        out OrderedCorners ordered)
    {
        if (components.Count != 4)
        {
            ordered = default;
            return false;
        }

        if (!TryOrderCorners(
                components.Select(component => component.Center).ToArray(),
                out var pointOrder))
        {
            ordered = default;
            return false;
        }

        ordered = new OrderedCorners(
            components.First(component => component.Center == pointOrder.TopLeft.Center),
            components.First(component => component.Center == pointOrder.TopRight.Center),
            components.First(component => component.Center == pointOrder.BottomLeft.Center),
            components.First(component => component.Center == pointOrder.BottomRight.Center));
        return true;
    }

    private static bool TryOrderCorners(
        IReadOnlyList<PointPx> points,
        out OrderedCorners ordered)
    {
        if (points.Count != 4)
        {
            ordered = default;
            return false;
        }

        var topLeft = points.OrderBy(point => point.X + point.Y).First();
        var bottomRight = points.OrderByDescending(point => point.X + point.Y).First();
        var topRight = points.OrderByDescending(point => point.X - point.Y).First();
        var bottomLeft = points.OrderBy(point => point.X - point.Y).First();
        var selected = new[] { topLeft, topRight, bottomLeft, bottomRight };
        if (selected.Distinct().Count() != 4)
        {
            ordered = default;
            return false;
        }

        var cross = Cross(
            new PointPx(topRight.X - topLeft.X, topRight.Y - topLeft.Y),
            new PointPx(bottomLeft.X - topLeft.X, bottomLeft.Y - topLeft.Y));
        if (Math.Abs(cross) < 1)
        {
            ordered = default;
            return false;
        }

        ordered = new OrderedCorners(
            new DarkComponent(topLeft, 0, 0, 0, 0),
            new DarkComponent(topRight, 0, 0, 0, 0),
            new DarkComponent(bottomLeft, 0, 0, 0, 0),
            new DarkComponent(bottomRight, 0, 0, 0, 0));
        if (!IsConvex(ordered))
        {
            ordered = default;
            return false;
        }

        return true;
    }

    private static double Cross(PointPx first, PointPx second)
    {
        return first.X * second.Y - first.Y * second.X;
    }

    private static double Distance(PointPx first, PointPx second)
    {
        return Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));
    }

    private static bool TrySolveHomography(
        IReadOnlyList<PointMm> source,
        IReadOnlyList<PointPx> target,
        out PageTransform transform)
    {
        transform = default;
        if (source.Count != 4 || target.Count != 4)
        {
            return false;
        }

        var matrix = new double[8, 9];
        for (var index = 0; index < 4; index++)
        {
            var x = source[index].X;
            var y = source[index].Y;
            var u = target[index].X;
            var v = target[index].Y;
            var row = index * 2;
            matrix[row, 0] = x;
            matrix[row, 1] = y;
            matrix[row, 2] = 1;
            matrix[row, 6] = -u * x;
            matrix[row, 7] = -u * y;
            matrix[row, 8] = u;
            matrix[row + 1, 3] = x;
            matrix[row + 1, 4] = y;
            matrix[row + 1, 5] = 1;
            matrix[row + 1, 6] = -v * x;
            matrix[row + 1, 7] = -v * y;
            matrix[row + 1, 8] = v;
        }

        for (var column = 0; column < 8; column++)
        {
            var pivot = column;
            for (var row = column + 1; row < 8; row++)
            {
                if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column]))
                {
                    pivot = row;
                }
            }

            if (Math.Abs(matrix[pivot, column]) < 1e-10)
            {
                return false;
            }

            if (pivot != column)
            {
                for (var swapColumn = column; swapColumn <= 8; swapColumn++)
                {
                    (matrix[column, swapColumn], matrix[pivot, swapColumn]) =
                        (matrix[pivot, swapColumn], matrix[column, swapColumn]);
                }
            }

            var divisor = matrix[column, column];
            for (var normalizeColumn = column; normalizeColumn <= 8; normalizeColumn++)
            {
                matrix[column, normalizeColumn] /= divisor;
            }

            for (var row = 0; row < 8; row++)
            {
                if (row == column)
                {
                    continue;
                }

                var factor = matrix[row, column];
                if (Math.Abs(factor) < 1e-15)
                {
                    continue;
                }

                for (var eliminateColumn = column; eliminateColumn <= 8; eliminateColumn++)
                {
                    matrix[row, eliminateColumn] -= factor * matrix[column, eliminateColumn];
                }
            }
        }

        var values = new double[8];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = matrix[index, 8];
            if (!double.IsFinite(values[index]))
            {
                return false;
            }
        }

        transform = new PageTransform(
            values[0],
            values[1],
            values[2],
            values[3],
            values[4],
            values[5],
            values[6],
            values[7],
            1);
        return true;
    }

    private sealed record DarkComponent(
        PointPx Center,
        double Area,
        double Width,
        double Height,
        double Fill);

    private sealed record CandidateSet(OrderedCorners Ordered, double Score);

    private readonly record struct OrderedCorners(
        DarkComponent TopLeft,
        DarkComponent TopRight,
        DarkComponent BottomLeft,
        DarkComponent BottomRight);

    private sealed record OrientationCandidate(
        PageOrientation Orientation,
        PageTransform Transform,
        double Score);

    private readonly record struct BubbleMeasurement(
        double FillRatio,
        OptionMarkState State,
        double Confidence);
}
