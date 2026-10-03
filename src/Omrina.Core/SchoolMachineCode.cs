using System.Globalization;
using System.Text;
using System.Text.Json;
using ZXing;
using ZXing.Common;

namespace Omrina.Core;

/// <summary>Exam/page QR codes and candidate Code 128 codes have separate payloads and readers.</summary>
public static class SchoolMachineCode
{
    private const string Prefix = "OMRINA3:";
    public static string ExamPayload(SchoolPageMetadata metadata) => Prefix + JsonSerializer.Serialize(metadata);
    public static BitMatrix EncodeExam(SchoolPageMetadata metadata) => new MultiFormatWriter().encode(ExamPayload(metadata), BarcodeFormat.QR_CODE, 0, 0,
        new Dictionary<EncodeHintType, object> { [EncodeHintType.CHARACTER_SET] = "UTF-8", [EncodeHintType.MARGIN] = 4 });
    public static BitMatrix EncodeCandidate(string candidateId)
    {
        if (candidateId.Length is < 1 or > 20 || candidateId.Any(c => c is < '0' or > '9')) throw new ArgumentException("考号必须为 1–20 位数字。");
        return new MultiFormatWriter().encode(candidateId, BarcodeFormat.CODE_128, 0, 30,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 20 });
    }
    public static SchoolPageMetadata? DecodeExam(GrayImage image)
    {
        var result = Decode(image, BarcodeFormat.QR_CODE);
        if (result is null || !result.Text.StartsWith(Prefix, StringComparison.Ordinal) || result.Text.Length > 2000) return null;
        try
        {
            var metadata = JsonSerializer.Deserialize<SchoolPageMetadata>(result.Text[Prefix.Length..]);
            return metadata is { Version: > 0, PageNumber: > 0 } && Enum.IsDefined(metadata.Side)
                && !string.IsNullOrEmpty(metadata.ExamId) && !string.IsNullOrEmpty(metadata.LayoutDocumentId)
                && metadata.TemplateId?.Length == 64 ? metadata : null;
        }
        catch (JsonException) { return null; }
    }
    public static string? DecodeCandidate(GrayImage image, int expectedDigits)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.CODE_128 } }
        };
        var texts = (reader.DecodeMultiple(image.Pixels.ToArray(), image.Width, image.Height, RGBLuminanceSource.BitmapFormat.Gray8)
            ?? Array.Empty<Result>()).Select(result => result.Text).Distinct(StringComparer.Ordinal).ToArray();
        if (texts.Length != 1) return null;
        var text = texts[0];
        return text.Length == expectedDigits && text.All(c => c is >= '0' and <= '9') ? text : null;
    }
    private static Result? Decode(GrayImage image, BarcodeFormat format) => new BarcodeReaderGeneric
    {
        AutoRotate = true,
        Options = new DecodingOptions { TryHarder = true, PossibleFormats = new List<BarcodeFormat> { format } }
    }.Decode(image.Pixels.ToArray(), image.Width, image.Height, RGBLuminanceSource.BitmapFormat.Gray8);

    public static bool ValidateExpected(AnswerSheetLayout layout, GrayImage image, PageTransform transform)
        => layout.SchoolMetadata is {} expected && layout.ExamCodeArea is {} area && DecodeExam(Rectify(image, transform, area, 600)) == expected;

    internal static GrayImage Rectify(GrayImage image, PageTransform transform, RectMm area, int width)
    {
        var height = Math.Max(1, (int)Math.Round(width * area.Height / area.Width));
        var pixels = new byte[checked(width * height)];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var point = transform.Map(new(area.X + (x + .5) * area.Width / width, area.Y + (y + .5) * area.Height / height));
                pixels[y * width + x] = double.IsFinite(point.X) && double.IsFinite(point.Y) ? image.GetPixelOrWhite((int)Math.Round(point.X), (int)Math.Round(point.Y)) : (byte)255;
            }
        return new(width, height, pixels, false);
    }
}

public sealed partial class AnswerSheetLayout
{
    private void AppendSchoolMachineCodes(StringBuilder svg)
    {
        static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        void Matrix(BitMatrix matrix, RectMm area)
        {
            var dx = area.Width / matrix.Width; var dy = area.Height / matrix.Height;
            for (var y = 0; y < matrix.Height; y++)
            {
                var x = 0;
                while (x < matrix.Width)
                {
                    if (!matrix[x,y]) { x++; continue; }
                    var start = x;
                    while (x < matrix.Width && matrix[x,y]) x++;
                    svg.Append($"<rect x=\"{F(area.X + start * dx)}\" y=\"{F(area.Y + y * dy)}\" width=\"{F((x-start)*dx)}\" height=\"{F(dy)}\" fill=\"black\"/>");
                }
            }
        }
        Matrix(SchoolMachineCode.EncodeExam(SchoolMetadata!), ExamCodeArea!.Value);
        if (CandidateArea is {} candidate && SchoolDefinition!.CandidateIdentity is { Mode: CandidateIdentityMode.Barcode, CandidateId: {} id })
            Matrix(SchoolMachineCode.EncodeCandidate(id), candidate);
    }
}
