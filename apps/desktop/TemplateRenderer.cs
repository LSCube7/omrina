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
    }

    public static Rect GetContentBounds(AnswerSheetLayout layout, double unitsPerMillimetre)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (unitsPerMillimetre <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitsPerMillimetre));
        }

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
