namespace Omrina.Core;

/// <summary>Overall disposition of a recognition result.</summary>
public enum RecognitionStatus
{
    /// <summary>Registration and bubble measurements passed all acceptance checks.</summary>
    Accepted,

    /// <summary>The page was located, but one or more measurements need review.</summary>
    ReviewRequired,

    /// <summary>The page could not be located or safely associated with the layout.</summary>
    Rejected
}

/// <summary>Rotation of the captured page relative to the layout's upright orientation.</summary>
public enum PageOrientation
{
    Unknown = -1,
    Degrees0 = 0,
    Degrees90 = 90,
    Degrees180 = 180,
    Degrees270 = 270
}

/// <summary>Question-level interpretation of the option fill measurements.</summary>
public enum QuestionMarkState
{
    Single,
    Blank,
    Multiple,
    Uncertain
}

/// <summary>Per-option interpretation retained alongside the raw fill ratio.</summary>
public enum OptionMarkState
{
    Selected,
    Unselected,
    Uncertain
}

public enum RecognitionDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>Stable machine-readable diagnostic identifiers for UI and export.</summary>
public enum RecognitionDiagnosticCode
{
    InputInvalid,
    ImageTooSmall,
    RegistrationMarksMissing,
    RegistrationMarksAmbiguous,
    RegistrationMarksMismatch,
    PerspectiveInvalid,
    OrientationAmbiguous,
    OrientationMarkerMissing,
    BubbleMeasurementsUncertain,
    PageQualityUncertain,
    TemplateAssociationRequired,
    RecognitionCancelled
}

public sealed record RecognitionDiagnostic(
    RecognitionDiagnosticCode Code,
    RecognitionDiagnosticSeverity Severity,
    string Message,
    double? Score = null);

/// <summary>A pixel coordinate in the decoded image.</summary>
public readonly record struct PointPx(double X, double Y);

/// <summary>
/// Projective transform from layout millimetres to image pixels.
/// Coordinates are represented in homogeneous form and are reversible for
/// points inside a valid page quadrilateral.
/// </summary>
public readonly record struct PageTransform(
    double M11,
    double M12,
    double M13,
    double M21,
    double M22,
    double M23,
    double M31,
    double M32,
    double M33)
{
    public PointPx Map(PointMm point)
    {
        var denominator = M31 * point.X + M32 * point.Y + M33;
        if (!double.IsFinite(denominator) || Math.Abs(denominator) < 1e-12)
        {
            return new PointPx(double.NaN, double.NaN);
        }

        return new PointPx(
            (M11 * point.X + M12 * point.Y + M13) / denominator,
            (M21 * point.X + M22 * point.Y + M23) / denominator);
    }

    public bool TryMapInverse(PointPx point, out PointMm mapped)
    {
        var determinant =
            M11 * (M22 * M33 - M23 * M32)
            - M12 * (M21 * M33 - M23 * M31)
            + M13 * (M21 * M32 - M22 * M31);
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-12)
        {
            mapped = default;
            return false;
        }

        var inverse11 = (M22 * M33 - M23 * M32) / determinant;
        var inverse12 = (M13 * M32 - M12 * M33) / determinant;
        var inverse13 = (M12 * M23 - M13 * M22) / determinant;
        var inverse21 = (M23 * M31 - M21 * M33) / determinant;
        var inverse22 = (M11 * M33 - M13 * M31) / determinant;
        var inverse23 = (M13 * M21 - M11 * M23) / determinant;
        var inverse31 = (M21 * M32 - M22 * M31) / determinant;
        var inverse32 = (M12 * M31 - M11 * M32) / determinant;
        var inverse33 = (M11 * M22 - M12 * M21) / determinant;
        var denominator = inverse31 * point.X + inverse32 * point.Y + inverse33;
        if (!double.IsFinite(denominator) || Math.Abs(denominator) < 1e-12)
        {
            mapped = default;
            return false;
        }

        mapped = new PointMm(
            (inverse11 * point.X + inverse12 * point.Y + inverse13) / denominator,
            (inverse21 * point.X + inverse22 * point.Y + inverse23) / denominator);
        return double.IsFinite(mapped.X) && double.IsFinite(mapped.Y);
    }
}

public sealed record OptionRecognitionResult(
    int OptionIndex,
    string OptionLabel,
    double FillRatio,
    OptionMarkState State,
    double Confidence);

public sealed record QuestionRecognitionResult(
    int QuestionNumber,
    QuestionMarkState State,
    IReadOnlyList<OptionRecognitionResult> Options,
    double Confidence);

/// <summary>
/// Complete M2 output. The caller must supply the layout associated with the
/// capture manifest; recognition deliberately does not infer a template from
/// a short printed number or from the pixels.
/// </summary>
public sealed class RecognitionResult
{
    public RecognitionResult(
        string templateId,
        int templateSchemaVersion,
        RecognitionStatus status,
        PageOrientation orientation,
        PageTransform? transform,
        double confidence,
        IReadOnlyList<QuestionRecognitionResult> questions,
        IReadOnlyList<RecognitionDiagnostic> diagnostics)
    {
        TemplateId = string.IsNullOrWhiteSpace(templateId)
            ? throw new ArgumentException("模板身份不能为空。", nameof(templateId))
            : templateId;
        TemplateSchemaVersion = templateSchemaVersion;
        Status = status;
        Orientation = orientation;
        Transform = transform;
        Confidence = Math.Clamp(confidence, 0, 1);
        Questions = questions ?? throw new ArgumentNullException(nameof(questions));
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public string TemplateId { get; }

    public int TemplateSchemaVersion { get; }

    public RecognitionStatus Status { get; }

    public PageOrientation Orientation { get; }

    public PageTransform? Transform { get; }

    /// <summary>Heuristic quality score, not a probability.</summary>
    public double Confidence { get; }

    public IReadOnlyList<QuestionRecognitionResult> Questions { get; }

    public IReadOnlyList<RecognitionDiagnostic> Diagnostics { get; }

    public bool CanScore => Status == RecognitionStatus.Accepted;
}
