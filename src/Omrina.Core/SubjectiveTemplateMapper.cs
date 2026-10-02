namespace Omrina.Core;

public enum PageLocationStatus { Located, ReviewRequired, Rejected }

public sealed class PageLocationResult
{
    internal PageLocationResult(string templateId, int schema, PageLocationStatus status,
        PageOrientation orientation, PageTransform? transform, double registrationScore, double markerScore,
        IReadOnlyList<RecognitionDiagnostic> diagnostics, int imageWidth, int imageHeight)
    {
        TemplateId = templateId;
        TemplateSchemaVersion = schema;
        Status = status;
        Orientation = orientation;
        Transform = transform;
        RegistrationScore = registrationScore;
        MarkerScore = markerScore;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
    }
    public string TemplateId { get; }
    public int TemplateSchemaVersion { get; }
    public PageLocationStatus Status { get; }
    public PageOrientation Orientation { get; }
    public PageTransform? Transform { get; }
    public double RegistrationScore { get; }
    public double MarkerScore { get; }
    public IReadOnlyList<RecognitionDiagnostic> Diagnostics { get; }
    public bool CanMapRegions => Status == PageLocationStatus.Located && Transform.HasValue;
    public int ImageWidth { get; }
    public int ImageHeight { get; }
}

public readonly record struct PixelQuadrilateral(PointPx TopLeft, PointPx TopRight, PointPx BottomRight, PointPx BottomLeft);

public sealed record MappedSubjectiveRegion(Guid QuestionId, int QuestionNumber, decimal MaximumScore,
    RectMm RectangleMm, PixelQuadrilateral SourceQuadrilateral, int OutputWidth, int OutputHeight,
    SubjectivePixelRectangle BoundingRectangle);

public static class SubjectiveTemplateMapper
{
    public static IReadOnlyList<MappedSubjectiveRegion> Map(AnswerSheetLayout layout, PageLocationResult location,
        int imageWidth, int imageHeight)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(location);
        if (imageWidth <= 0 || imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth));
        if (!location.CanMapRegions || location.TemplateId != layout.TemplateId
            || location.TemplateSchemaVersion != layout.SchemaVersion
            || location.ImageWidth != imageWidth || location.ImageHeight != imageHeight)
            throw new ArgumentException("页面定位未通过或模板身份不匹配。", nameof(location));
        var transform = location.Transform!.Value;
        var result = new List<MappedSubjectiveRegion>();
        foreach (var region in layout.SubjectiveRegions)
        {
            var rectangle = region.Rectangle;
            PointMm[] corners = [new(rectangle.X, rectangle.Y), new(rectangle.X + rectangle.Width, rectangle.Y),
                new(rectangle.X + rectangle.Width, rectangle.Y + rectangle.Height), new(rectangle.X, rectangle.Y + rectangle.Height)];
            var denominators = corners.Select(point => transform.M31 * point.X + transform.M32 * point.Y + transform.M33).ToArray();
            if (denominators.Any(value => !double.IsFinite(value) || Math.Abs(value) < 1e-12)
                || denominators.Any(value => Math.Sign(value) != Math.Sign(denominators[0])))
                throw new ArgumentException("题区透视变换无效。", nameof(location));
            var points = corners.Select(transform.Map).ToArray();
            if (points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)
                || point.X < 0 || point.Y < 0 || point.X > imageWidth || point.Y > imageHeight
                || !transform.TryMapInverse(point, out _)))
                throw new ArgumentException("题区超出采集图像或透视变换无效。", nameof(location));
            var cross = new double[4];
            for (var index = 0; index < 4; index++)
            {
                var a = points[index]; var b = points[(index + 1) % 4]; var c = points[(index + 2) % 4];
                cross[index] = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            }
            if (cross.Any(value => Math.Abs(value) < 1e-8 || Math.Sign(value) != Math.Sign(cross[0])))
                throw new ArgumentException("题区四角无法组成有效区域。", nameof(location));
            var width = Math.Ceiling((Distance(points[0], points[1]) + Distance(points[3], points[2])) / 2);
            var height = Math.Ceiling((Distance(points[0], points[3]) + Distance(points[1], points[2])) / 2);
            if (width < 1 || height < 1 || width > 16000 || height > 16000 || width * height > 4_000_000)
                throw new ArgumentException("校正后的题区图像过大。", nameof(location));
            var x = (int)Math.Floor(points.Min(point => point.X)); var y = (int)Math.Floor(points.Min(point => point.Y));
            var right = (int)Math.Ceiling(points.Max(point => point.X)); var bottom = (int)Math.Ceiling(points.Max(point => point.Y));
            result.Add(new MappedSubjectiveRegion(region.QuestionId, region.QuestionNumber, region.MaximumScore,
                rectangle, new(points[0], points[1], points[2], points[3]), (int)width, (int)height,
                SubjectivePixelRectangle.Create(x, y, right - x, bottom - y, imageWidth, imageHeight)));
        }
        return result.AsReadOnly();
    }
    private static double Distance(PointPx left, PointPx right)
        => Math.Sqrt(Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));
}
