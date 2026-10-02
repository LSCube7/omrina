namespace Omrina.Core;

/// <summary>A rectangle in pixels on one captured page, using a half-open right and bottom edge.</summary>
public readonly record struct SubjectivePixelRectangle
{
    private SubjectivePixelRectangle(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    public static SubjectivePixelRectangle Create(
        int x,
        int y,
        int width,
        int height,
        int imageWidth,
        int imageHeight)
    {
        ValidateImageSize(imageWidth, imageHeight);
        ValidateBounds(x, y, width, height, imageWidth, imageHeight);
        return new SubjectivePixelRectangle(x, y, width, height);
    }

    internal void ValidateWithin(int imageWidth, int imageHeight)
    {
        ValidateImageSize(imageWidth, imageHeight);
        ValidateBounds(X, Y, Width, Height, imageWidth, imageHeight);
    }

    private static void ValidateImageSize(int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "图像宽度必须为正整数。");
        }

        if (imageHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageHeight), "图像高度必须为正整数。");
        }
    }

    private static void ValidateBounds(int x, int y, int width, int height, int imageWidth, int imageHeight)
    {
        if (x < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "区域 X 坐标不能小于零。");
        }

        if (y < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(y), "区域 Y 坐标不能小于零。");
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "区域宽度必须为正整数。");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "区域高度必须为正整数。");
        }

        if ((long)x + width > imageWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "区域右边界超出图像范围。");
        }

        if ((long)y + height > imageHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "区域下边界超出图像范围。");
        }
    }
}

/// <summary>
/// A reviewer-defined region for one question on a captured page. It contains
/// only pixel coordinates and is intentionally independent of answer-sheet layouts.
/// </summary>
public sealed class SubjectiveRegionDefinition
{
    private SubjectiveRegionDefinition(
        Guid questionId,
        int questionNumber,
        SubjectivePixelRectangle rectangle,
        decimal maximumPoints)
    {
        QuestionId = questionId;
        QuestionNumber = questionNumber;
        Rectangle = rectangle;
        MaximumPoints = maximumPoints;
    }

    public const decimal MaximumPointsLimit = 1_000_000m;

    public Guid QuestionId { get; }

    public int QuestionNumber { get; }

    public SubjectivePixelRectangle Rectangle { get; }

    public decimal MaximumPoints { get; }

    public static SubjectiveRegionDefinition Create(
        Guid questionId,
        int questionNumber,
        SubjectivePixelRectangle rectangle,
        decimal maximumPoints,
        int imageWidth,
        int imageHeight)
    {
        Validate(questionId, questionNumber, rectangle, maximumPoints, imageWidth, imageHeight);
        return new SubjectiveRegionDefinition(questionId, questionNumber, rectangle, maximumPoints);
    }

    internal void ValidateForImage(int imageWidth, int imageHeight)
    {
        Validate(QuestionId, QuestionNumber, Rectangle, MaximumPoints, imageWidth, imageHeight);
    }

    internal static void Validate(
        Guid questionId,
        int questionNumber,
        SubjectivePixelRectangle rectangle,
        decimal maximumPoints,
        int imageWidth,
        int imageHeight)
    {
        if (questionId == Guid.Empty)
        {
            throw new ArgumentException("主观题区域 ID 不能为空。", nameof(questionId));
        }

        if (questionNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(questionNumber), "题号必须为正整数。");
        }

        if (maximumPoints <= 0 || maximumPoints > MaximumPointsLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPoints),
                $"每题满分必须大于零且不超过 {MaximumPointsLimit}。");
        }

        rectangle.ValidateWithin(imageWidth, imageHeight);
    }
}

internal static class SubjectiveValueValidation
{
    public static string CaptureId(string value, string parameterName)
    {
        var normalized = RequiredText(value, parameterName, maximumLength: 38);
        if (!Guid.TryParse(normalized, out var parsed) || parsed == Guid.Empty)
        {
            throw new ArgumentException("采集页 ID 必须是有效的非空 GUID。", parameterName);
        }

        return normalized;
    }

    public static string RequiredText(string value, string parameterName, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("文本不能为空。", parameterName);
        }

        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("文本超出允许范围。", parameterName);
        }

        return normalized;
    }

    public static string? OptionalText(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            throw new ArgumentException("文本超出允许范围。", parameterName);
        }

        return normalized;
    }

    public static DateTimeOffset UtcTimestamp(DateTimeOffset? value, string parameterName)
    {
        var timestamp = (value ?? DateTimeOffset.UtcNow).ToUniversalTime();
        if (timestamp == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(parameterName, "时间戳不能为默认值。");
        }

        return timestamp;
    }
}
