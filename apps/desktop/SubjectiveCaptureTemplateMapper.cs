using Omrina.Core;
using Omrina.Platform;

namespace Omrina.Desktop;

/// <summary>Immutable provenance linking a review's pixel regions to its original mm template.</summary>
public sealed record SubjectiveReviewTemplateMapping(
    string TemplateJson,
    PageTransform PageTransform,
    IReadOnlyList<SubjectiveMappedRegionProvenance> Regions);

/// <summary>JSON-safe mapped region data. The bounding rectangle is stored as integers because
/// SubjectivePixelRectangle intentionally has no public constructor.</summary>
public sealed record SubjectiveMappedRegionProvenance(
    Guid QuestionId,
    int QuestionNumber,
    decimal MaximumScore,
    RectMm RectangleMm,
    PixelQuadrilateral SourceQuadrilateral,
    int OutputWidth,
    int OutputHeight,
    int BoundingX,
    int BoundingY,
    int BoundingWidth,
    int BoundingHeight)
{
    public static SubjectiveMappedRegionProvenance FromMapped(MappedSubjectiveRegion region) => new(
        region.QuestionId,
        region.QuestionNumber,
        region.MaximumScore,
        region.RectangleMm,
        region.SourceQuadrilateral,
        region.OutputWidth,
        region.OutputHeight,
        region.BoundingRectangle.X,
        region.BoundingRectangle.Y,
        region.BoundingRectangle.Width,
        region.BoundingRectangle.Height);

    public MappedSubjectiveRegion ToMapped(int imageWidth, int imageHeight) => new(
        QuestionId,
        QuestionNumber,
        MaximumScore,
        RectangleMm,
        SourceQuadrilateral,
        OutputWidth,
        OutputHeight,
        SubjectivePixelRectangle.Create(BoundingX, BoundingY, BoundingWidth, BoundingHeight, imageWidth, imageHeight));
}

/// <summary>Decodes and locates a persisted template on its original capture before mapping regions.</summary>
public static class SubjectiveCaptureTemplateMapper
{
    private static readonly SkiaImageDecoder Decoder = new();
    private static readonly AnswerSheetRecognizer Recognizer = new();

    public static async Task<SubjectiveReviewTemplateMapping> LocateAndMapAsync(
        AnswerSheetLayout layout,
        IInputImageFile file,
        int expectedImageWidth,
        int expectedImageHeight,
        CancellationToken cancellationToken = default,
        bool requireSubjective = true)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();
        if (layout.SchemaVersion is not (AnswerSheetLayout.MixedTemplateSchemaVersion or 3)
            || (requireSubjective && layout.SubjectiveRegions.Count is < 1 or > SubjectiveGradingSnapshot.MaximumQuestionCount))
        {
            throw new SubjectiveCaptureMappingException(
                "SUBJECTIVE_TEMPLATE_REQUIRED",
                "采集记录关联的模板没有可批阅的主观题区域。请使用包含主观题区域的模板重新采集。");
        }

        using var decodeLease = await SubjectiveImageCropper.AcquireDecodeSlotAsync(cancellationToken)
            .ConfigureAwait(false);
        var grayImage = await Task.Run(
            () => Decoder.DecodeGrayscaleAsync(file, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (grayImage.Width != expectedImageWidth || grayImage.Height != expectedImageHeight)
        {
            throw new SubjectiveCaptureMappingException(
                "SUBJECTIVE_IMAGE_INVALID",
                "原图尺寸与采集记录不一致，无法定位主观题区域。");
        }

        var location = await Task.Run(
            () => Recognizer.LocatePage(layout, grayImage, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!location.CanMapRegions || location.Transform is not { } transform)
        {
            throw new SubjectiveCaptureMappingException(
                "SUBJECTIVE_PAGE_NOT_LOCATED",
                "无法可靠定位答题纸页面，未创建主观题批阅记录。请检查原图或重新采集。");
        }

        try
        {
            var regions = SubjectiveTemplateMapper.Map(layout, location, grayImage.Width, grayImage.Height);
            return new SubjectiveReviewTemplateMapping(
                layout.ToJson(),
                transform,
                regions.Select(SubjectiveMappedRegionProvenance.FromMapped).ToArray());
        }
        catch (ArgumentException exception)
        {
            throw new SubjectiveCaptureMappingException(
                "SUBJECTIVE_REGION_MAPPING_FAILED",
                "主观题区域无法安全映射到原图，未创建批阅记录。",
                exception);
        }
    }
}

public sealed class SubjectiveCaptureMappingException : Exception
{
    public SubjectiveCaptureMappingException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
