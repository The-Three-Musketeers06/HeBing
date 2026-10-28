using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AiVisionStudio.Models;

namespace AiVisionStudio.Views;

/// <summary>Native vector borders and header ornaments from HMI decoration options.</summary>
internal sealed class HmiDecoration(HmiElement element) : FrameworkElement
{
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); var w = ActualWidth; var h = ActualHeight; if (w <= 2 || h <= 2) return;
        var colors = HmiVisuals.Strings(element.Option?["colors"]); var primary = HmiVisuals.Brush(colors.ElementAtOrDefault(0) ?? "#03a9f4"); var secondary = HmiVisuals.Brush(colors.ElementAtOrDefault(1) ?? "#195690");
        dc.DrawRectangle(HmiVisuals.Brush(element.Background, "transparent"), null, new Rect(0, 0, w, h));
        if (element.Type.StartsWith("Decorates"))
        {
            if (element.Type == "Decorates02")
            {
                var y = h / 2; dc.DrawLine(new Pen(secondary, element.GetNumber("option.lineHeight", 3)), new Point(0, y), new Point(w, y));
                for (int i = 0; i < 6; i++) { dc.DrawLine(new Pen(primary, 2), new Point(i * 11, y - 7), new Point(i * 11 + 8, y + 7)); dc.DrawLine(new Pen(primary, 2), new Point(w - i * 11, y - 7), new Point(w - i * 11 - 8, y + 7)); }
            }
            else
            {
                var points = new[] { new Point(0, h * .25), new Point(w * .3, h * .25), new Point(w * .34, h * .7), new Point(w * .66, h * .7), new Point(w * .7, h * .25), new Point(w, h * .25) };
                for (int i = 1; i < points.Length; i++) dc.DrawLine(new Pen(primary, 2), points[i - 1], points[i]);
                dc.DrawLine(new Pen(secondary, 1), new Point(w * .12, h * .85), new Point(w * .88, h * .85));
            }
            return;
        }
        if (element.Type == "Title01")
        {
            var geo = new StreamGeometry(); using (var ctx = geo.Open()) { ctx.BeginFigure(new Point(0, h * .25), true, true); ctx.PolyLineTo([new Point(w * .12, h * .85), new Point(w * .88, h * .85), new Point(w, h * .25)], true, false); }
            dc.DrawGeometry(HmiVisuals.Brush("#082d4990"), new Pen(primary, 1), geo); DrawText(dc, element.GetString("option.dataset"), w / 2, h / 2, element.GetNumber("option.textSize", 32), element.GetString("option.textColor", "#ffffff")); return;
        }
        var outer = new Rect(2, 2, Math.Max(1, w - 4), Math.Max(1, h - 4));
        dc.DrawRectangle(null, new Pen(secondary, 1), outer);
        var corner = Math.Min(30, Math.Min(w, h) / 6); var thick = new Pen(primary, element.Type is "Border07" or "Border13" ? 3 : 2);
        foreach (var pair in new[] { (new Point(2, 2), 1, 1), (new Point(w - 2, 2), -1, 1), (new Point(2, h - 2), 1, -1), (new Point(w - 2, h - 2), -1, -1) })
        {
            dc.DrawLine(thick, pair.Item1, new Point(pair.Item1.X + pair.Item2 * corner, pair.Item1.Y)); dc.DrawLine(thick, pair.Item1, new Point(pair.Item1.X, pair.Item1.Y + pair.Item3 * corner));
        }
        if (element.Type is "Border01" or "Border02" or "Border05")
        {
            dc.DrawLine(new Pen(primary, 1), new Point(10, 8), new Point(w * .3, 8)); dc.DrawLine(new Pen(primary, 1), new Point(w * .7, h - 8), new Point(w - 10, h - 8));
        }
        if (element.Type == "Border04")
        {
            var titleWidth = Math.Min(w - 8, element.GetNumber("option.borderTitleWidth", 250)); var titleHeight = element.GetNumber("option.borderTitleHeight", 32);
            dc.DrawRectangle(secondary, null, new Rect((w - titleWidth) / 2, 0, titleWidth, titleHeight));
            DrawText(dc, element.GetString("option.borderTitle"), w / 2, titleHeight / 2, element.GetNumber("option.borderTitleSize", 18), element.GetString("option.borderTitleColor", "#ffffff"));
        }
    }
    private void DrawText(DrawingContext dc, string value, double x, double y, double size, string color)
    {
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, HmiVisuals.Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip); dc.DrawText(text, new Point(x - text.Width / 2, y - text.Height / 2));
    }
}
