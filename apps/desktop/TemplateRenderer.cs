using System.Xml.Linq;
using Omrina.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Globalization;
using System.Text;
using Windows.Foundation;

namespace Omrina.Desktop;

internal static class TemplateRenderer
{
    public const double DipsPerMillimetre = 96d / 25.4d;

    private static readonly SolidColorBrush BlackBrush = new(Colors.Black);
    private static readonly SolidColorBrush WhiteBrush = new(Colors.White);

    public static void Render(AnswerSheetLayout layout, Canvas canvas, double unitsPerMillimetre, bool showPageOutline)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(canvas);
        if (unitsPerMillimetre <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitsPerMillimetre));
        }

        if (layout.SchoolDefinition is not null)
        {
            RenderSchool(layout, canvas, unitsPerMillimetre, showPageOutline);
            return;
        }
        canvas.Children.Clear();
        canvas.Width = AnswerSheetLayout.PageWidthMm * unitsPerMillimetre;
        canvas.Height = AnswerSheetLayout.PageHeightMm * unitsPerMillimetre;
        canvas.Background = WhiteBrush;

        if (showPageOutline)
        {
            AddShape(canvas, new Rectangle
            {
                Width = canvas.Width,
                Height = canvas.Height,
                Fill = WhiteBrush,
                Stroke = BlackBrush,
                StrokeThickness = 1
            }, 0, 0);
        }

        foreach (var mark in layout.RegistrationMarks)
        {
            AddShape(canvas, new Rectangle
            {
                Width = mark.SizeMm * unitsPerMillimetre,
                Height = mark.SizeMm * unitsPerMillimetre,
                Fill = BlackBrush
            }, mark.TopLeft.X * unitsPerMillimetre, mark.TopLeft.Y * unitsPerMillimetre);
        }

        AddShape(canvas, new Rectangle
        {
            Width = layout.OrientationMarker.WidthMm * unitsPerMillimetre,
            Height = layout.OrientationMarker.HeightMm * unitsPerMillimetre,
            Fill = BlackBrush
        },
        layout.OrientationMarker.TopLeft.X * unitsPerMillimetre,
        layout.OrientationMarker.TopLeft.Y * unitsPerMillimetre);

        var templateNumberWidth = EstimateTextWidthMm(layout.TemplateNumber, 3.5);
        AddText(
            canvas,
            layout.TemplateNumber,
            layout.TemplateNumberPosition.X - templateNumberWidth / 2,
            layout.TemplateNumberPosition.Y - 3.5,
            templateNumberWidth,
            3.5,
            TextAlignment.Center,
            unitsPerMillimetre);
        AddText(canvas, layout.Title, 0, 21, AnswerSheetLayout.PageWidthMm, 8, TextAlignment.Center, unitsPerMillimetre, bold: true);
        AddText(canvas, "每题请选择一个选项", 0, 33, AnswerSheetLayout.PageWidthMm, 3.5, TextAlignment.Center, unitsPerMillimetre);

        foreach (var header in layout.OptionHeaders)
        {
            AddText(
                canvas,
                header.OptionLabel,
                header.Position.X - 5,
                header.Position.Y - 3,
                10,
                3.5,
                TextAlignment.Center,
                unitsPerMillimetre);
        }

        foreach (var question in layout.Questions)
        {
            AddText(
                canvas,
                question.Number.ToString(CultureInfo.InvariantCulture),
                question.NumberPosition.X,
                question.NumberPosition.Y - 4.5,
                20,
                4.5,
                TextAlignment.Left,
                unitsPerMillimetre);

            foreach (var bubble in question.Bubbles)
            {
                var diameter = bubble.RadiusMm * 2 * unitsPerMillimetre;
                AddShape(canvas, new Ellipse
                {
                    Width = diameter,
                    Height = diameter,
                    Fill = WhiteBrush,
                    Stroke = BlackBrush,
                    StrokeThickness = unitsPerMillimetre * 0.5
                },
                (bubble.Center.X - bubble.RadiusMm) * unitsPerMillimetre,
                (bubble.Center.Y - bubble.RadiusMm) * unitsPerMillimetre);
            }
        }

        foreach (var region in layout.SubjectiveRegions)
        {
            var rectangle = region.Rectangle;
            AddShape(canvas, new Rectangle
            {
                Width = rectangle.Width * unitsPerMillimetre,
                Height = rectangle.Height * unitsPerMillimetre,
                Fill = new SolidColorBrush(Colors.Transparent),
                Stroke = BlackBrush,
                StrokeThickness = unitsPerMillimetre * 0.5
            }, rectangle.X * unitsPerMillimetre, rectangle.Y * unitsPerMillimetre);
            AddSubjectiveLabel(canvas, region, unitsPerMillimetre);
        }
    }

    public static Rect GetContentBounds(AnswerSheetLayout layout, double unitsPerMillimetre)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (unitsPerMillimetre <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitsPerMillimetre));
        }

        if (layout.SchoolDefinition is not null)
            return SchoolBounds(layout, unitsPerMillimetre);
        var minimumX = double.MaxValue;
        var minimumY = double.MaxValue;
        var maximumX = double.MinValue;
        var maximumY = double.MinValue;

        void IncludeBounds(double leftMm, double topMm, double widthMm, double heightMm)
        {
            minimumX = Math.Min(minimumX, leftMm);
            minimumY = Math.Min(minimumY, topMm);
            maximumX = Math.Max(maximumX, leftMm + widthMm);
            maximumY = Math.Max(maximumY, topMm + heightMm);
        }

        foreach (var mark in layout.RegistrationMarks)
        {
            IncludeBounds(mark.TopLeft.X, mark.TopLeft.Y, mark.SizeMm, mark.SizeMm);
        }

        IncludeBounds(
            layout.OrientationMarker.TopLeft.X,
            layout.OrientationMarker.TopLeft.Y,
            layout.OrientationMarker.WidthMm,
            layout.OrientationMarker.HeightMm);

        foreach (var bubble in layout.Bubbles)
        {
            IncludeBounds(
                bubble.Center.X - bubble.RadiusMm,
                bubble.Center.Y - bubble.RadiusMm,
                bubble.RadiusMm * 2,
                bubble.RadiusMm * 2);
        }

        foreach (var header in layout.OptionHeaders)
        {
            IncludeBounds(header.Position.X - 5, header.Position.Y - 3, 10, 3.5);
        }

        foreach (var question in layout.Questions)
        {
            IncludeBounds(question.NumberPosition.X, question.NumberPosition.Y - 4.5, 20, 4.5);
        }

        foreach (var region in layout.SubjectiveRegions)
        {
            IncludeBounds(
                region.Rectangle.X - 0.25,
                region.Rectangle.Y - 0.25,
                region.Rectangle.Width + 0.5,
                region.Rectangle.Height + 0.5);
        }

        var templateNumberWidth = EstimateTextWidthMm(layout.TemplateNumber, 3.5);
        IncludeBounds(
            layout.TemplateNumberPosition.X - templateNumberWidth / 2,
            layout.TemplateNumberPosition.Y - 3.5,
            templateNumberWidth,
            3.5);

        var titleWidth = EstimateTextWidthMm(layout.Title, 8);
        IncludeBounds(
            AnswerSheetLayout.PageWidthMm / 2 - titleWidth / 2,
            21,
            titleWidth,
            8);

        var instructionWidth = EstimateTextWidthMm("每题请选择一个选项", 3.5);
        IncludeBounds(
            AnswerSheetLayout.PageWidthMm / 2 - instructionWidth / 2,
            33,
            instructionWidth,
            3.5);

        return new Rect(
            minimumX * unitsPerMillimetre,
            minimumY * unitsPerMillimetre,
            (maximumX - minimumX) * unitsPerMillimetre,
            (maximumY - minimumY) * unitsPerMillimetre);
    }

    // Read the engine's controlled SVG so paper preview, print and export share geometry.
    private static IEnumerable<XElement> SchoolElements(AnswerSheetLayout layout) =>
        XDocument.Parse(layout.ToSvg()).Root!.Elements();
    private static double Attribute(XElement element, string name, double fallback = 0) =>
        double.TryParse((string?)element.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private static Brush? SvgBrush(string? value) => value switch
    {
        "none" => null,
        "white" or "#ffffff" => WhiteBrush,
        _ => BlackBrush
    };
    private static Rect ElementBounds(XElement element)
    {
        var kind = element.Name.LocalName;
        if (kind == "circle") { var radius = Attribute(element, "r"); return new(Attribute(element, "cx") - radius, Attribute(element, "cy") - radius, radius * 2, radius * 2); }
        if (kind == "text")
        {
            var size = Attribute(element, "font-size", 3); var width = EstimateTextWidthMm(element.Value, size);
            var x = Attribute(element, "x"); var anchor = (string?)element.Attribute("text-anchor");
            if (anchor == "middle") x -= width / 2; else if (anchor == "end") x -= width;
            return new(x, Attribute(element, "y") - size, width, size * 1.3);
        }
        if (kind == "line") return new(Math.Min(Attribute(element, "x1"), Attribute(element, "x2")), Math.Min(Attribute(element, "y1"), Attribute(element, "y2")), Math.Abs(Attribute(element, "x2") - Attribute(element, "x1")), Math.Abs(Attribute(element, "y2") - Attribute(element, "y1")));
        return new(Attribute(element, "x"), Attribute(element, "y"), Attribute(element, "width"), Attribute(element, "height"));
    }
    private static Rect SchoolBounds(AnswerSheetLayout layout, double scale)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var element in SchoolElements(layout))
        {
            if (element.Name.LocalName is not ("rect" or "circle" or "text" or "line")) continue;
            if ((string?)element.Attribute("fill") == "white" && Attribute(element, "width") == layout.WidthMm) continue;
            var bounds = ElementBounds(element); var stroke = Attribute(element, "stroke-width") / 2;
            left = Math.Min(left, bounds.Left - stroke); top = Math.Min(top, bounds.Top - stroke);
            right = Math.Max(right, bounds.Right + stroke); bottom = Math.Max(bottom, bounds.Bottom + stroke);
        }
        return new(left * scale, top * scale, (right - left) * scale, (bottom - top) * scale);
    }
    private static void RenderSchool(AnswerSheetLayout layout, Canvas canvas, double scale, bool outline)
    {
        canvas.Children.Clear(); canvas.Width = layout.WidthMm * scale; canvas.Height = layout.HeightMm * scale; canvas.Background = WhiteBrush;
        foreach (var element in SchoolElements(layout))
        {
            var kind = element.Name.LocalName;
            if (kind is "metadata" or "title") continue;
            var bounds = ElementBounds(element);
            if (kind == "text")
            {
                AddText(canvas, element.Value, bounds.Left, bounds.Top, bounds.Width, Attribute(element, "font-size", 3), TextAlignment.Left, scale);
                continue;
            }
            if (kind == "line")
            {
                AddShape(canvas, new Line { X1 = Attribute(element, "x1") * scale, X2 = Attribute(element, "x2") * scale, Y1 = Attribute(element, "y1") * scale, Y2 = Attribute(element, "y2") * scale, Stroke = BlackBrush, StrokeThickness = Attribute(element, "stroke-width", .2) * scale }, 0, 0);
                continue;
            }
            Shape shape = kind switch { "rect" => new Rectangle(), "circle" => new Ellipse(), _ => throw new InvalidOperationException($"答题卡包含暂不支持的图形：{kind}。") };
            shape.Width = bounds.Width * scale; shape.Height = bounds.Height * scale;
            shape.Fill = SvgBrush((string?)element.Attribute("fill"));
            shape.Stroke = element.Attribute("stroke") is null ? null : SvgBrush((string?)element.Attribute("stroke"));
            shape.StrokeThickness = Attribute(element, "stroke-width", .2) * scale;
            AddShape(canvas, shape, bounds.Left * scale, bounds.Top * scale);
        }
        if (outline) AddShape(canvas, new Rectangle { Width = canvas.Width, Height = canvas.Height, Stroke = BlackBrush, StrokeThickness = 1 }, 0, 0);
    }

    private static double EstimateTextWidthMm(string text, double fontSizeMillimetres)
    {
        var width = 0d;
        foreach (var rune in text.EnumerateRunes())
        {
            width += rune.Value < 0x80
                ? fontSizeMillimetres * 0.65
                : fontSizeMillimetres;
        }

        return width;
    }

    private static string FormatSubjectiveLabel(TemplateSubjectiveRegion region) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{region.QuestionNumber}（{region.MaximumScore:G29} 分）");

    private static void AddSubjectiveLabel(
        Canvas canvas,
        TemplateSubjectiveRegion region,
        double unitsPerMillimetre)
    {
        var text = FormatSubjectiveLabel(region);
        var rectangle = region.Rectangle;
        var textWidthMm = EstimateTextWidthMm(text, 3.5);
        var availableWidthMm = rectangle.Width - 4;
        var textBlock = new TextBlock
        {
            Text = text,
            Width = textWidthMm * unitsPerMillimetre,
            FontSize = 3.5 * unitsPerMillimetre,
            FontFamily = new FontFamily("Arial"),
            Foreground = BlackBrush,
            TextWrapping = TextWrapping.NoWrap,
            RenderTransformOrigin = new Point(0, 0)
        };
        if (textWidthMm > availableWidthMm)
        {
            textBlock.RenderTransform = new ScaleTransform { ScaleX = availableWidthMm / textWidthMm };
        }

        canvas.Children.Add(textBlock);
        Canvas.SetLeft(textBlock, (rectangle.X + 2) * unitsPerMillimetre);
        Canvas.SetTop(textBlock, (rectangle.Y + 1) * unitsPerMillimetre);
    }

    private static void AddText(
        Canvas canvas,
        string text,
        double leftMillimetres,
        double topMillimetres,
        double widthMillimetres,
        double fontSizeMillimetres,
        TextAlignment alignment,
        double unitsPerMillimetre,
        bool bold = false)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            Width = widthMillimetres * unitsPerMillimetre,
            FontSize = fontSizeMillimetres * unitsPerMillimetre,
            FontFamily = new FontFamily("Arial"),
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = BlackBrush,
            TextAlignment = alignment,
            TextWrapping = TextWrapping.NoWrap
        };
        canvas.Children.Add(textBlock);
        Canvas.SetLeft(textBlock, leftMillimetres * unitsPerMillimetre);
        Canvas.SetTop(textBlock, topMillimetres * unitsPerMillimetre);
    }

    private static void AddShape(Canvas canvas, Shape shape, double left, double top)
    {
        canvas.Children.Add(shape);
        Canvas.SetLeft(shape, left);
        Canvas.SetTop(shape, top);
    }
}
