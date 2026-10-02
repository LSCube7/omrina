using System.Globalization;
using Omrina.Core;
using Omrina.Desktop;

namespace Omrina.M3.Desktop.Tests;

public static class SubjectiveReviewInputRegression
{
    public static void Run()
    {
        VerifyIntegerFields();
        VerifyDecimalFields();
        VerifyRegionBounds();
        VerifyDraftConfirmation();
        VerifySelectionGeneration();
    }

    private static void VerifyIntegerFields()
    {
        Assert(
            SubjectiveReviewInputParser.TryParseInteger("1", 1, int.MaxValue, out var firstQuestion)
            && firstQuestion == 1,
            "positive question numbers should parse");
        Assert(
            SubjectiveReviewInputParser.TryParseInteger("2147483647", 1, int.MaxValue, out var maxInt)
            && maxInt == int.MaxValue,
            "Int32 maximum should parse at the boundary");
        Assert(
            !SubjectiveReviewInputParser.TryParseInteger("2147483648", 1, int.MaxValue, out _),
            "integer overflow should be rejected");
        Assert(
            !SubjectiveReviewInputParser.TryParseInteger(string.Empty, 1, int.MaxValue, out _),
            "empty integer input should be rejected");
        Assert(
            !SubjectiveReviewInputParser.TryParseInteger("1.5", 1, int.MaxValue, out _),
            "fractional values should be rejected for integer fields");
        Assert(
            !SubjectiveReviewInputParser.TryParseInteger("-1", 0, int.MaxValue, out _),
            "negative region coordinates should be rejected");
    }

    private static void VerifyDecimalFields()
    {
        var invariant = CultureInfo.InvariantCulture;
        Assert(
            SubjectiveReviewInputParser.TryParseMaximumScore("10.5", invariant, out var maximumScore)
            && maximumScore == 10.5m,
            "maximum score should retain decimal precision");
        Assert(
            !SubjectiveReviewInputParser.TryParseMaximumScore("0", invariant, out _),
            "maximum score must be positive");
        Assert(
            !SubjectiveReviewInputParser.TryParseMaximumScore("1000000.01", invariant, out _),
            "maximum score must respect the domain limit");

        Assert(
            SubjectiveReviewInputParser.TryParseScore("0", 10m, invariant, out var zeroScore)
            && zeroScore == 0m,
            "zero is a valid earned score");
        Assert(
            SubjectiveReviewInputParser.TryParseScore("10", 10m, invariant, out var fullScore)
            && fullScore == 10m,
            "a score equal to the maximum should parse");
        Assert(
            !SubjectiveReviewInputParser.TryParseScore("10.01", 10m, invariant, out _),
            "a score above the question maximum should be rejected");
        Assert(
            !SubjectiveReviewInputParser.TryParseScore(string.Empty, 10m, invariant, out _),
            "empty score input should be rejected");
        Assert(
            !SubjectiveReviewInputParser.TryParseScore("NaN", 10m, invariant, out _),
            "NaN should be rejected");
        Assert(
            !SubjectiveReviewInputParser.TryParseScore("Infinity", 10m, invariant, out _),
            "infinity should be rejected");
        Assert(
            !SubjectiveReviewInputParser.TryParseScore("1e1000", decimal.MaxValue, invariant, out _),
            "non-finite exponential values should be rejected");

        var german = CultureInfo.GetCultureInfo("de-DE");
        Assert(
            SubjectiveReviewInputParser.TryParseScore("5,5", 10m, german, out var localizedScore)
            && localizedScore == 5.5m,
            "decimal input should respect the active decimal separator");
        Assert(
            !SubjectiveReviewInputParser.TryParseScore("5,5", 1000m, invariant, out _),
            "a comma must not be silently treated as a thousands separator");
        Assert(
            !SubjectiveReviewInputParser.TryParseScore("5.5", 1000m, german, out _),
            "a non-local decimal separator should not be reinterpreted");
    }

    private static void VerifyRegionBounds()
    {
        Assert(
            SubjectiveReviewInputParser.IsRegionWithinImage(90, 90, 10, 10, 100, 100),
            "a region ending at the image edge should be accepted");
        Assert(
            !SubjectiveReviewInputParser.IsRegionWithinImage(91, 90, 10, 10, 100, 100),
            "a region extending beyond the image width should be rejected");
        Assert(
            !SubjectiveReviewInputParser.IsRegionWithinImage(int.MaxValue, 0, int.MaxValue, 1, 100, 100),
            "overflowing region boundaries should be rejected safely");
        Assert(
            !SubjectiveReviewInputParser.IsRegionWithinImage(0, 0, 0, 1, 100, 100),
            "zero-width regions should be rejected");
    }

    private static void VerifyDraftConfirmation()
    {
        Assert(
            SubjectiveReviewInputParser.CanConfirmDraft(SubjectiveReviewStatus.Draft, 5m, "saved", 5m, "saved"),
            "a draft may be confirmed without changing its saved values");
        Assert(
            !SubjectiveReviewInputParser.CanConfirmDraft(SubjectiveReviewStatus.Draft, 5m, "saved", 5.5m, "saved"),
            "a draft score must be saved before confirmation");
        Assert(
            !SubjectiveReviewInputParser.CanConfirmDraft(SubjectiveReviewStatus.Draft, 5m, "saved", 5m, "edited"),
            "a draft comment must be saved before confirmation");
        Assert(
            !SubjectiveReviewInputParser.CanConfirmDraft(SubjectiveReviewStatus.Confirmed, 5m, "saved", 5m, "saved"),
            "a confirmed question cannot be confirmed again as a draft");
        Assert(
            !SubjectiveReviewInputParser.CanConfirmDraft(SubjectiveReviewStatus.Draft, null, null, 0m, null),
            "a draft without a saved score cannot be confirmed");
    }

    private static void VerifySelectionGeneration()
    {
        var generation = new AsyncSelectionGeneration();
        var firstRequest = generation.Begin();
        Assert(generation.IsCurrent(firstRequest), "the latest image request should remain current");
        var secondRequest = generation.Begin();
        Assert(!generation.IsCurrent(firstRequest), "changing question should stale the previous image request");
        Assert(generation.IsCurrent(secondRequest), "the newly selected question should own the current request");
        generation.Invalidate();
        Assert(!generation.IsCurrent(secondRequest), "invalidating page state should stale a pending image request");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
