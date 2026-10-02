using System.Globalization;
using Omrina.Core;

namespace Omrina.Desktop;

/// <summary>Validates text entered by the subjective-review form without UI dependencies.</summary>
internal static class SubjectiveReviewInputParser
{
    private const NumberStyles IntegerStyles = NumberStyles.Integer;
    private const NumberStyles DecimalStyles =
        NumberStyles.AllowLeadingWhite
        | NumberStyles.AllowTrailingWhite
        | NumberStyles.AllowLeadingSign
        | NumberStyles.AllowDecimalPoint;

    public static bool TryParseInteger(
        string? text,
        int minimum,
        int maximum,
        out int value)
    {
        value = default;
        return minimum <= maximum
            && int.TryParse(text, IntegerStyles, CultureInfo.InvariantCulture, out value)
            && value >= minimum
            && value <= maximum;
    }

    public static bool TryParseMaximumScore(
        string? text,
        CultureInfo culture,
        out decimal maximumScore)
    {
        maximumScore = default;
        return decimal.TryParse(text, DecimalStyles, culture, out maximumScore)
            && maximumScore > 0
            && maximumScore <= SubjectiveRegionDefinition.MaximumPointsLimit;
    }

    public static bool TryParseScore(
        string? text,
        decimal maximumScore,
        CultureInfo culture,
        out decimal score)
    {
        score = default;
        return maximumScore >= 0
            && maximumScore <= SubjectiveRegionDefinition.MaximumPointsLimit
            && decimal.TryParse(text, DecimalStyles, culture, out score)
            && score >= 0
            && score <= maximumScore;
    }

    public static bool IsRegionWithinImage(
        int x,
        int y,
        int width,
        int height,
        int imageWidth,
        int imageHeight)
    {
        return imageWidth > 0
            && imageHeight > 0
            && x >= 0
            && y >= 0
            && width > 0
            && height > 0
            && (long)x + width <= imageWidth
            && (long)y + height <= imageHeight;
    }

    public static bool CanConfirmDraft(
        SubjectiveReviewStatus status,
        decimal? savedScore,
        string? savedComment,
        decimal requestedScore,
        string? requestedComment)
    {
        return status == SubjectiveReviewStatus.Draft
            && savedScore.HasValue
            && savedScore.Value == requestedScore
            && string.Equals(savedComment, requestedComment, StringComparison.Ordinal);
    }

    public static string FormatRegionDefinition(
        int questionNumber,
        decimal maximumScore,
        int x,
        int y,
        int width,
        int height,
        CultureInfo culture)
    {
        return string.Format(
            culture,
            "第 {0} 题 · 满分 {1} · 区域 X={2}, Y={3}, 宽 {4}, 高 {5}",
            questionNumber,
            maximumScore,
            x,
            y,
            width,
            height);
    }
}

/// <summary>Marks asynchronous selection requests stale when selection changes.</summary>
internal sealed class AsyncSelectionGeneration
{
    private long _generation;

    public long Begin() => Interlocked.Increment(ref _generation);

    public bool IsCurrent(long generation) => Volatile.Read(ref _generation) == generation;

    public void Invalidate() => Interlocked.Increment(ref _generation);
}
