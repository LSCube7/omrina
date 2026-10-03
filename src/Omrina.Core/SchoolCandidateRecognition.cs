namespace Omrina.Core;

public sealed record CandidateDigitRecognition(int Position, QuestionMarkState State, string? Digit,
    IReadOnlyList<OptionRecognitionResult> Options);
public sealed record SchoolCandidateRecognitionResult(string? CandidateId, bool RequiresReview,
    IReadOnlyList<CandidateDigitRecognition> Digits);

public static class SchoolCandidateRecognition
{
    /// <summary>Reads only the candidate area after the caller has validated the exam/page code.</summary>
    public static SchoolCandidateRecognitionResult Read(AnswerSheetLayout layout, GrayImage image, PageTransform transform)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(image);
        if (layout.SchoolDefinition is not {} definition || layout.CandidateArea is not {} area)
            return new(null, true, Array.Empty<CandidateDigitRecognition>());
        if (!SchoolMachineCode.ValidateExpected(layout, image, transform))
            return new(null, true, Array.Empty<CandidateDigitRecognition>());
        if (definition.CandidateIdentity.Mode == CandidateIdentityMode.Barcode)
        {
            var id = SchoolMachineCode.DecodeCandidate(SchoolMachineCode.Rectify(image, transform, area, 1600), definition.CandidateIdentity.Digits);
            return new(id, id is null, Array.Empty<CandidateDigitRecognition>());
        }
        return AnswerSheetRecognizer.ReadCandidateDigits(layout, image, transform);
    }
}

public sealed partial class AnswerSheetRecognizer
{
    internal static SchoolCandidateRecognitionResult ReadCandidateDigits(AnswerSheetLayout layout, GrayImage image, PageTransform transform)
    {
        var digits = new List<CandidateDigitRecognition>();
        foreach (var column in layout.CandidateDigits)
        {
            var options = column.Bubbles.Select(bubble =>
            {
                var measurement = MeasureBubble(image, transform, bubble);
                return new OptionRecognitionResult(bubble.OptionIndex, bubble.OptionLabel, measurement.FillRatio,
                    measurement.State, measurement.Confidence);
            }).ToArray();
            var selected = options.Where(option => option.State == OptionMarkState.Selected).ToArray();
            var uncertain = options.Any(option => option.State == OptionMarkState.Uncertain);
            var state = selected.Length > 1 ? QuestionMarkState.Multiple : uncertain ? QuestionMarkState.Uncertain
                : selected.Length == 0 ? QuestionMarkState.Blank : QuestionMarkState.Single;
            digits.Add(new(column.Position, state, state == QuestionMarkState.Single ? selected[0].OptionLabel : null,
                Array.AsReadOnly(options)));
        }
        var review = digits.Count == 0 || digits.Any(digit => digit.State != QuestionMarkState.Single);
        return new(review ? null : string.Concat(digits.Select(digit => digit.Digit)), review, digits.AsReadOnly());
    }
}
