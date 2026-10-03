using System.Globalization;
using System.Text;
using ZXing;
using ZXing.Common;

namespace Omrina.Core;

/// <summary>Compact Data Matrix page identifiers and separate candidate Code 128 codes.</summary>
public static class SchoolMachineCode
{
    private const string PageCodePrefix = "OM1";
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int PageDigestBytes = 16;
    private const int EncodedPageCodeLength = 29;
    private const int QuietZoneModules = 1;

    /// <summary>Minimum printed width and height of each Data Matrix module.</summary>
    public const double ExamModuleSizeMm = 0.5;

    /// <summary>Returns the on-paper identifier derived from a full immutable template ID.</summary>
    public static string PageCode(SchoolPageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!IsFullTemplateId(metadata.TemplateId))
        {
            throw new ArgumentException("页面模板标识必须是 64 位小写十六进制摘要。", nameof(metadata));
        }

        var digest = Convert.FromHexString(metadata.TemplateId);
        return PageCodePrefix + EncodeBase32(digest.AsSpan(0, PageDigestBytes));
    }

    /// <summary>The complete Data Matrix payload; its fixed-length value contains no exam metadata.</summary>
    public static string ExamPayload(SchoolPageMetadata metadata) => PageCode(metadata);

    /// <summary>Encodes an ECC 200 Data Matrix symbol without scaling or an implicit quiet zone.</summary>
    public static BitMatrix EncodeExam(SchoolPageMetadata metadata)
    {
        var matrix = new MultiFormatWriter().encode(
            PageCode(metadata),
            BarcodeFormat.DATA_MATRIX,
            0,
            0,
            new Dictionary<EncodeHintType, object>
            {
                [EncodeHintType.MARGIN] = 0,
                [EncodeHintType.DATA_MATRIX_SHAPE] = ZXing.Datamatrix.Encoder.SymbolShapeHint.FORCE_SQUARE
            });

        if (matrix.Width != matrix.Height)
        {
            throw new InvalidOperationException("页面 Data Matrix 必须为正方形 ECC 200 符号。");
        }

        return matrix;
    }

    /// <summary>Returns the printed footprint including one quiet module on every side.</summary>
    public static (double WidthMm, double HeightMm) ExamCodeSizeMm(SchoolPageMetadata metadata)
    {
        var matrix = EncodeExam(metadata);
        var width = (matrix.Width + QuietZoneModules * 2) * ExamModuleSizeMm;
        var height = (matrix.Height + QuietZoneModules * 2) * ExamModuleSizeMm;
        return (width, height);
    }

    /// <summary>
    /// Parses one decoded Data Matrix payload. The format prefix, fixed length, alphabet and
    /// unused Base32 bits are checked so alternate spellings cannot resolve to the same page.
    /// </summary>
    public static string? ParsePageCode(string? payload)
    {
        if (payload is null || payload.Length != EncodedPageCodeLength
            || !payload.StartsWith(PageCodePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var encodedDigest = payload.AsSpan(PageCodePrefix.Length);
        foreach (var character in encodedDigest)
        {
            if (Base32Alphabet.IndexOf(character) < 0)
            {
                return null;
            }
        }

        // Sixteen bytes use 128 bits. The final Base32 character has two canonical zero bits.
        if ((Base32Alphabet.IndexOf(encodedDigest[^1]) & 0b11) != 0)
        {
            return null;
        }

        return payload;
    }

    public static BitMatrix EncodeCandidate(string candidateId)
    {
        ArgumentNullException.ThrowIfNull(candidateId);
        if (candidateId.Length is < 1 or > 20 || candidateId.Any(c => c is < '0' or > '9'))
        {
            throw new ArgumentException("考号必须为 1–20 位数字。", nameof(candidateId));
        }

        return new MultiFormatWriter().encode(
            candidateId,
            BarcodeFormat.CODE_128,
            0,
            30,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.MARGIN] = 20 });
    }

    /// <summary>Returns a page code only when the image contains exactly one decodable Data Matrix.</summary>
    public static string? DecodeExam(GrayImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.DATA_MATRIX }
            }
        };

        var results = reader.DecodeMultiple(
            image.Pixels.ToArray(),
            image.Width,
            image.Height,
            RGBLuminanceSource.BitmapFormat.Gray8);

        if (results is not { Length: 1 })
        {
            return null;
        }

        return ParsePageCode(results[0].Text);
    }

    public static string? DecodeCandidate(GrayImage image, int expectedDigits)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (expectedDigits is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedDigits));
        }

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.CODE_128 } }
        };
        var texts = (reader.DecodeMultiple(image.Pixels.ToArray(), image.Width, image.Height, RGBLuminanceSource.BitmapFormat.Gray8)
            ?? Array.Empty<Result>()).Select(result => result.Text).Distinct(StringComparer.Ordinal).ToArray();
        if (texts.Length != 1)
        {
            return null;
        }

        var text = texts[0];
        return text.Length == expectedDigits && text.All(c => c is >= '0' and <= '9') ? text : null;
    }

    private static bool IsFullTemplateId(string? templateId)
    {
        if (templateId is not { Length: 64 })
        {
            return false;
        }

        return templateId.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static string EncodeBase32(ReadOnlySpan<byte> bytes)
    {
        var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bitCount = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bitCount += 8;
            while (bitCount >= 5)
            {
                bitCount -= 5;
                output.Append(Base32Alphabet[(buffer >> bitCount) & 0b1_1111]);
            }
        }

        if (bitCount > 0)
        {
            output.Append(Base32Alphabet[(buffer << (5 - bitCount)) & 0b1_1111]);
        }

        return output.ToString();
    }

    public static bool ValidateExpected(AnswerSheetLayout layout, GrayImage image, PageTransform transform)
    {
        if (layout.SchoolMetadata is not { } expected || layout.ExamCodeArea is not { } area)
        {
            return false;
        }

        var decodedPageCode = DecodeExam(Rectify(image, transform, area, 600));
        return string.Equals(decodedPageCode, PageCode(expected), StringComparison.Ordinal);
    }

    internal static GrayImage Rectify(GrayImage image, PageTransform transform, RectMm area, int width)
    {
        var height = Math.Max(1, (int)Math.Round(width * area.Height / area.Width));
        var pixels = new byte[checked(width * height)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var point = transform.Map(new(
                    area.X + (x + .5) * area.Width / width,
                    area.Y + (y + .5) * area.Height / height));
                pixels[y * width + x] = double.IsFinite(point.X) && double.IsFinite(point.Y)
                    ? image.GetPixelOrWhite((int)Math.Round(point.X), (int)Math.Round(point.Y))
                    : (byte)255;
            }
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
            var dx = area.Width / matrix.Width;
            var dy = area.Height / matrix.Height;
            for (var y = 0; y < matrix.Height; y++)
            {
                var x = 0;
                while (x < matrix.Width)
                {
                    if (!matrix[x, y])
                    {
                        x++;
                        continue;
                    }

                    var start = x;
                    while (x < matrix.Width && matrix[x, y])
                    {
                        x++;
                    }

                    svg.Append($"<rect x=\"{F(area.X + start * dx)}\" y=\"{F(area.Y + y * dy)}\" width=\"{F((x - start) * dx)}\" height=\"{F(dy)}\" fill=\"black\"/>");
                }
            }
        }

        void ExamMatrix(BitMatrix matrix, RectMm area)
        {
            var module = SchoolMachineCode.ExamModuleSizeMm;
            var footprintWidth = (matrix.Width + 2) * module;
            var footprintHeight = (matrix.Height + 2) * module;
            if (footprintWidth > area.Width + 1e-9 || footprintHeight > area.Height + 1e-9)
            {
                throw new InvalidOperationException("Data Matrix 编码密度超过页面为其预留的空间。");
            }

            var left = area.X + (area.Width - footprintWidth) / 2 + module;
            var top = area.Y + (area.Height - footprintHeight) / 2 + module;
            for (var y = 0; y < matrix.Height; y++)
            {
                var x = 0;
                while (x < matrix.Width)
                {
                    if (!matrix[x, y])
                    {
                        x++;
                        continue;
                    }

                    var start = x;
                    while (x < matrix.Width && matrix[x, y])
                    {
                        x++;
                    }

                    svg.Append($"<rect x=\"{F(left + start * module)}\" y=\"{F(top + y * module)}\" width=\"{F((x - start) * module)}\" height=\"{F(module)}\" fill=\"black\"/>");
                }
            }
        }

        ExamMatrix(SchoolMachineCode.EncodeExam(SchoolMetadata!), ExamCodeArea!.Value);
        if (CandidateArea is { } candidate && SchoolDefinition!.CandidateIdentity is { Mode: CandidateIdentityMode.Barcode, CandidateId: { } id })
        {
            Matrix(SchoolMachineCode.EncodeCandidate(id), candidate);
        }
    }
}
