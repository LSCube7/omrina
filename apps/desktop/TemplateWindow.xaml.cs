using AnswerSheet.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Globalization;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace AnswerSheet.Desktop;

public sealed partial class TemplateWindow : Window
{
    private const double PreviewScale = 2;
    private static readonly SolidColorBrush BlackBrush = new(Colors.Black);
    private static readonly SolidColorBrush WhiteBrush = new(Colors.White);
    private AnswerSheetLayout? _layout;
    private bool _previewMatchesInputs;

    public TemplateWindow()
    {
        InitializeComponent();
        SaveSvgButton.IsEnabled = false;
        GenerateLayout();
    }

    private void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        GenerateLayout();
    }

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        MarkPreviewOutdated();
    }

    private void NumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        MarkPreviewOutdated();
    }

    private void GenerateLayout()
    {
        GenerateButton.IsEnabled = false;
        TemplateStatusText.Text = "正在生成模板预览。";

        try
        {
            var questionCount = ReadInteger(QuestionCountBox, "题数");
            var optionsPerQuestion = ReadInteger(OptionsPerQuestionBox, "每题选项数");
            var layout = AnswerSheetLayout.Create(TitleBox.Text, questionCount, optionsPerQuestion);

            RenderLayout(layout);
            _layout = layout;
            _previewMatchesInputs = true;
            SaveSvgButton.IsEnabled = true;
            TemplateStatusText.Text = $"已生成 {layout.QuestionCount} 题、每题 {layout.OptionsPerQuestion} 个选项的 A4 模板。";
        }
        catch (Exception exception)
        {
            _layout = null;
            _previewMatchesInputs = false;
            SaveSvgButton.IsEnabled = false;
            PreviewCanvas.Children.Clear();
            var rootCause = exception.GetBaseException();
            TemplateStatusText.Text = $"模板生成失败：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。请检查标题、题数和选项数。";
        }
        finally
        {
            GenerateButton.IsEnabled = true;
        }
    }

    private async void SaveSvgButton_Click(object sender, RoutedEventArgs e)
    {
        if (_layout is null || !_previewMatchesInputs)
        {
            TemplateStatusText.Text = "参数已变更，请先重新生成预览后再保存。";
            return;
        }

        var layout = _layout;
        GenerateButton.IsEnabled = false;
        SaveSvgButton.IsEnabled = false;
        try
        {
            TemplateStatusText.Text = "正在选择 SVG 保存位置。";
            var picker = new FileSavePicker
            {
                SuggestedFileName = "answersheet-template"
            };
            picker.FileTypeChoices.Add("SVG 图像", [".svg"]);
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this));

            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                TemplateStatusText.Text = "已取消保存 SVG。";
                return;
            }

            TemplateStatusText.Text = "正在保存 SVG。";
            await FileIO.WriteTextAsync(file, layout.ToSvg(), Windows.Storage.Streams.UnicodeEncoding.Utf8);
            TemplateStatusText.Text = $"SVG 已保存：{file.Path}";
        }
        catch (Exception exception)
        {
            var rootCause = exception.GetBaseException();
            TemplateStatusText.Text = $"保存 SVG 失败：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。请重新选择位置后再试。";
        }
        finally
        {
            GenerateButton.IsEnabled = true;
            SaveSvgButton.IsEnabled = ReferenceEquals(_layout, layout) && _previewMatchesInputs;
        }
    }

    private void RenderLayout(AnswerSheetLayout layout)
    {
        PreviewCanvas.Children.Clear();
        PreviewCanvas.Width = AnswerSheetLayout.PageWidthMm * PreviewScale;
        PreviewCanvas.Height = AnswerSheetLayout.PageHeightMm * PreviewScale;

        AddShape(new Rectangle
        {
            Width = PreviewCanvas.Width,
            Height = PreviewCanvas.Height,
            Fill = WhiteBrush,
            Stroke = BlackBrush,
            StrokeThickness = 1
        }, 0, 0);

        foreach (var mark in layout.RegistrationMarks)
        {
            AddShape(new Rectangle
            {
                Width = mark.SizeMm * PreviewScale,
                Height = mark.SizeMm * PreviewScale,
                Fill = BlackBrush
            }, mark.TopLeft.X * PreviewScale, mark.TopLeft.Y * PreviewScale);
        }

        AddText(layout.Title, 0, 20, AnswerSheetLayout.PageWidthMm, 8, TextAlignment.Center, bold: true);
        AddText("每题请选择一个选项", 0, 33, AnswerSheetLayout.PageWidthMm, 3.5, TextAlignment.Center);

        foreach (var header in layout.OptionHeaders)
        {
            AddText(
                header.OptionLabel,
                header.Position.X - 5,
                header.Position.Y - 3,
                10,
                3.5,
                TextAlignment.Center);
        }

        foreach (var question in layout.Questions)
        {
            AddText(
                question.Number.ToString(CultureInfo.InvariantCulture),
                question.NumberPosition.X,
                question.NumberPosition.Y - 4.5,
                20,
                4.5,
                TextAlignment.Left);

            foreach (var bubble in question.Bubbles)
            {
                var diameter = bubble.RadiusMm * 2 * PreviewScale;
                AddShape(new Ellipse
                {
                    Width = diameter,
                    Height = diameter,
                    Fill = WhiteBrush,
                    Stroke = BlackBrush,
                    StrokeThickness = 1
                },
                (bubble.Center.X - bubble.RadiusMm) * PreviewScale,
                (bubble.Center.Y - bubble.RadiusMm) * PreviewScale);
            }
        }
    }

    private void AddText(
        string text,
        double leftMm,
        double topMm,
        double widthMm,
        double fontSizeMm,
        TextAlignment alignment,
        bool bold = false)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            Width = widthMm * PreviewScale,
            FontSize = fontSizeMm * PreviewScale,
            FontFamily = new FontFamily("Arial"),
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = BlackBrush,
            TextAlignment = alignment,
            TextWrapping = TextWrapping.NoWrap
        };
        PreviewCanvas.Children.Add(textBlock);
        Canvas.SetLeft(textBlock, leftMm * PreviewScale);
        Canvas.SetTop(textBlock, topMm * PreviewScale);
    }

    private void AddShape(Shape shape, double left, double top)
    {
        PreviewCanvas.Children.Add(shape);
        Canvas.SetLeft(shape, left);
        Canvas.SetTop(shape, top);
    }

    private static int ReadInteger(NumberBox numberBox, string name)
    {
        if (double.IsNaN(numberBox.Value)
            || double.IsInfinity(numberBox.Value)
            || numberBox.Value != Math.Truncate(numberBox.Value))
        {
            throw new ArgumentException($"{name}必须是整数。", name);
        }

        return checked((int)numberBox.Value);
    }

    private void MarkPreviewOutdated()
    {
        if (_layout is null || !_previewMatchesInputs)
        {
            return;
        }

        _previewMatchesInputs = false;
        SaveSvgButton.IsEnabled = false;
        TemplateStatusText.Text = "参数已变更，请重新生成预览。";
    }
}
