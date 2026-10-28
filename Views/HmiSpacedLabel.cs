using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AiVisionStudio.Views;

/// <summary>Typography used by native HMI tabs, whose letterSpacing is part of the source format.</summary>
public sealed class HmiSpacedLabel : FrameworkElement
{
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 16;
    public Brush Foreground { get; set; } = Brushes.White;
    public bool Bold { get; set; }
    public double Spacing { get; set; }
    public string Alignment { get; set; } = "center";
    public string WritingMode { get; set; } = "horizontal-tb";
    protected override void OnRender(DrawingContext dc)
    {
        var chars = StringInfo.GetTextElementEnumerator(Text ?? ""); var glyphs = new List<FormattedText>();
        while (chars.MoveNext()) glyphs.Add(new FormattedText(chars.GetTextElement(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), Math.Clamp(FontSize, 1, 500), Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip));
        if (WritingMode is "vertical-rl" or "vertical-lr")
        {
            var lineHeight = Math.Max(1, glyphs.Select(g => g.Height).DefaultIfEmpty(FontSize).Max() + Spacing);
            var perColumn = Math.Max(1, (int)(ActualHeight / lineHeight)); var columns = (int)Math.Ceiling(glyphs.Count / (double)perColumn); var columnWidth = Math.Max(1, FontSize * 1.25);
            var blockWidth = columns * columnWidth; var startX = Alignment == "left" ? 0 : Alignment == "right" ? ActualWidth - blockWidth : (ActualWidth - blockWidth) / 2;
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
            for (int i = 0; i < glyphs.Count; i++) { var column = i / perColumn; var row = i % perColumn; if (WritingMode == "vertical-rl") column = columns - column - 1; var glyph = glyphs[i]; dc.DrawText(glyph, new Point(startX + column * columnWidth + (columnWidth - glyph.Width) / 2, row * lineHeight)); }
            dc.Pop(); return;
        }
        var width = glyphs.Sum(g => g.WidthIncludingTrailingWhitespace) + Math.Max(0, glyphs.Count - 1) * Spacing; var height = glyphs.Select(g => g.Height).DefaultIfEmpty(0).Max();
        var x = Alignment == "left" ? 0 : Alignment == "right" ? ActualWidth - width : (ActualWidth - width) / 2;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        foreach (var glyph in glyphs) { dc.DrawText(glyph, new Point(x, (ActualHeight - height) / 2)); x += glyph.WidthIncludingTrailingWhitespace + Spacing; }
        dc.Pop();
    }
}
