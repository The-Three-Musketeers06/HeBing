using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AiVisionStudio.Models;

namespace AiVisionStudio.Views;

internal static class HmiVisuals
{
    public static readonly string[] Palette = ["#4992ff", "#7cffb2", "#fddd60", "#ff6e76", "#58d9f9", "#05c091", "#ff8a45", "#8d48e3", "#dd79ff"];
    public static Color Color(string value, string fallback = "#ffffff")
    {
        try
        {
            value = (value ?? fallback).Trim();
            // HMI files store CSS #RRGGBBAA; WPF's native parser expects #AARRGGBB.
            if (value.Length == 9 && value[0] == '#')
                return System.Windows.Media.Color.FromArgb(byte.Parse(value[7..9], NumberStyles.HexNumber), byte.Parse(value[1..3], NumberStyles.HexNumber), byte.Parse(value[3..5], NumberStyles.HexNumber), byte.Parse(value[5..7], NumberStyles.HexNumber));
            if (value.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase) || value.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase))
            {
                var parts = value[(value.IndexOf('(') + 1)..value.IndexOf(')')].Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                return System.Windows.Media.Color.FromArgb(parts.Length == 4 ? (byte)Math.Clamp(parts[3] * 255, 0, 255) : (byte)255, (byte)parts[0], (byte)parts[1], (byte)parts[2]);
            }
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch { return (Color)ColorConverter.ConvertFromString(fallback); }
    }
    public static SolidColorBrush Brush(string value, string fallback = "#ffffff") => new(Color(value, fallback));
    public static object Unwrap(object value)
    {
        if (value is JsonNode node)
        {
            if (node is JsonArray arr) return arr.Select(Unwrap).ToArray();
            if (node is JsonObject obj) return obj.ToDictionary(x => x.Key, x => Unwrap(x.Value));
            return Unwrap(JsonSerializer.Deserialize<JsonElement>(node.ToJsonString()));
        }
        if (value is JsonElement el) return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(), JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.True => true, JsonValueKind.False => false,
            JsonValueKind.Array => el.EnumerateArray().Select(x => Unwrap(x)).ToArray(),
            JsonValueKind.Object => el.EnumerateObject().ToDictionary(x => x.Name, x => Unwrap(x.Value)), _ => null
        };
        return value;
    }
    public static string Text(object value)
    {
        value = Unwrap(value);
        if (value == null) return "";
        if (value is bool b) return b ? "True" : "False";
        if (value is double d) return d.ToString("0.###", CultureInfo.InvariantCulture);
        if (value is float f) return f.ToString("0.###", CultureInfo.InvariantCulture);
        if (value is IEnumerable list && value is not string) return JsonSerializer.Serialize(value);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }
    public static double Number(object value, double fallback = 0) => double.TryParse(Text(value), NumberStyles.Any, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result) ? result : fallback;
    public static bool Boolean(object value) => value is bool b ? b : string.Equals(Text(value), "true", StringComparison.OrdinalIgnoreCase) || Text(value) == "1" || Text(value) == "OK";
    public static string[] References(HmiElement element) => element.Option?["parameter"] is JsonArray a ? a.Select(x => Text(x)).ToArray() : [];
    public static string[] Strings(JsonNode node) => node is JsonArray a ? a.Select(Text).ToArray() : [];
    public static object Value(HmiElement element, IHmiRuntimeContext runtime, bool design)
    {
        var refs = References(element).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        return !design && refs.Length > 0 ? runtime?.Read(refs[0]) : Unwrap(element.Option?["dataset"]);
    }
    public static BitmapSource Bitmap(string source, string basePath)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        byte[] bytes;
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) bytes = Convert.FromBase64String(source[(source.IndexOf(',') + 1)..]);
        else
        {
            // Network images are loaded explicitly by the host; opening a design is local and deterministic.
            if (Uri.TryCreate(source, UriKind.Absolute, out var remote) && remote.Scheme is "http" or "https") return null;
            var path = Path.IsPathRooted(source) ? source : Path.Combine(Path.GetDirectoryName(basePath) ?? "", source);
            if (!File.Exists(path)) return null;
            bytes = File.ReadAllBytes(path);
        }
        return Bitmap(bytes);
    }
    private static readonly HttpClient ImageClient = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly ConcurrentDictionary<string, BitmapSource> RemoteImages = new(StringComparer.Ordinal);
    public static async Task<BitmapSource> RemoteBitmapAsync(string source, CancellationToken cancellation)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidDataException("远程图片地址必须为 HTTP/HTTPS");
        if (RemoteImages.TryGetValue(source, out var cached)) return cached;
        using var response = await ImageClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation); response.EnsureSuccessStatusCode();
        const int maximum = 32 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("远程图片超过 32 MB");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation); using var bytes = new MemoryStream(); var buffer = new byte[81920];
        int read; while ((read = await stream.ReadAsync(buffer, cancellation)) > 0) { if (bytes.Length + read > maximum) throw new InvalidDataException("远程图片超过 32 MB"); bytes.Write(buffer, 0, read); }
        cancellation.ThrowIfCancellationRequested(); var bitmap = Bitmap(bytes.ToArray());
        if (RemoteImages.Count >= 16) RemoteImages.TryRemove(RemoteImages.Keys.First(), out _); RemoteImages[source] = bitmap; return bitmap;
    }
    private static BitmapSource Bitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var originalWidth = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0].PixelWidth;
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; if (originalWidth > 2048) bitmap.DecodePixelWidth = 2048; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }
    public static List<Dictionary<string, object>> Rows(object value)
    {
        value = Unwrap(value);
        if (value is string json)
        {
            try { value = Unwrap(JsonNode.Parse(json)); } catch { return []; }
        }
        if (value is IDictionary<string, object> map)
        {
            if (map.TryGetValue("data", out var data)) return Rows(data);
            if (map.TryGetValue("source", out data)) return Rows(data);
            return [new(map)];
        }
        var rows = new List<Dictionary<string, object>>();
        if (value is IEnumerable items && value is not string)
            foreach (var item in items)
            {
                var v = Unwrap(item);
                if (v is IReadOnlyDictionary<string, object> rd) rows.Add(rd.ToDictionary(x => x.Key, x => x.Value));
                else if (v is IDictionary<string, object> d) rows.Add(new(d));
                else if (v is IEnumerable cells && v is not string) rows.Add(cells.Cast<object>().Select((x, i) => (x, i)).ToDictionary(x => "c" + x.i, x => x.x));
                else rows.Add(new() { ["value"] = v });
            }
        return rows;
    }
}

/// <summary>Lightweight WPF chart renderer. Data is supplied separately from design-time ECharts options.</summary>
public sealed class HmiChart : FrameworkElement
{
    public HmiElement Element { get; }
    public List<string> Labels { get; } = [];
    public List<string> SeriesNames { get; } = [];
    public List<double[]> Series { get; } = [];
    public string EmptyText { get; set; } = "暂无数据";
    public HmiChart(HmiElement element)
    {
        Element = element; ClipToBounds = true;
        MouseMove += (_, args) => { if (!Element.GetBoolean("option.tooltip.show", true) || Labels.Count == 0) { ToolTip = null; return; } var index = Math.Clamp((int)((args.GetPosition(this).X - 60) / Math.Max(1, ActualWidth - 120) * Labels.Count), 0, Labels.Count - 1); ToolTip = Labels[index] + "\n" + string.Join("\n", Series.Select((values, series) => (SeriesNames.ElementAtOrDefault(series) ?? "") + ": " + (index < values.Length && double.IsFinite(values[index]) ? values[index].ToString("0.###") : "?"))); };
    }
    public void SetData(IEnumerable<string> labels, IEnumerable<string> seriesNames, IEnumerable<double[]> values)
    {
        Labels.Clear(); Labels.AddRange(labels); SeriesNames.Clear(); SeriesNames.AddRange(seriesNames); Series.Clear(); Series.AddRange(values); InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth; var h = ActualHeight;
        if (w < 5 || h < 5) return;
        dc.DrawRectangle(HmiVisuals.Brush(Element.GetString("option.backgroundColor", "transparent"), "transparent"), null, new Rect(0, 0, w, h));
        if (Series.Count == 0 || Series.All(x => x.Length == 0)) { DrawText(dc, EmptyText, new Point(w / 2, h / 2), 16, "#777777", true); return; }
        var type = Element.Type;
        if (type is "PieCommon" or "PieCircle" or "NGStatistics" or "NGStatisticsSearch") { DrawPie(dc, w, h); return; }
        var horizontal = type.Contains("Crossrange") || type == "CapsuleChart";
        double Edge(string name, double size, double fallback) { var text = Element.GetString("option.grid." + name); return text.EndsWith('%') && double.TryParse(text.TrimEnd('%'), out var percent) ? size * percent / 100 : Element.GetNumber("option.grid." + name, fallback); }
        var left = Math.Clamp(Edge("left", w, Math.Min(60, w * .18)), 0, w - 1); var top = Math.Clamp(Edge("top", h, Math.Min(60, h * .22)), 0, h - 1);
        var rect = new Rect(left, top, Math.Max(1, w - left - Edge("right", w, Math.Min(60, w * .12))), Math.Max(1, h - top - Edge("bottom", h, Math.Min(50, h * .14))));
        var numberAxis = horizontal ? "xAxis" : "yAxis"; var categoryAxis = horizontal ? "yAxis" : "xAxis";
        bool Axis(string name, string prop, bool fallback = true) => Element.GetBoolean("option." + name + "." + prop, fallback);
        double AxisSize(string name) => Element.GetNumber("option." + name + ".axisLabel.fontSize", 11);
        string AxisColor(string name) => Element.GetString("option." + name + ".axisLabel.color", "#B9B8CE");
        var all = Series.SelectMany(x => x).Where(double.IsFinite).ToArray();
        var max = all.Length > 0 ? Math.Max(0, all.Max()) : 1d; var min = all.Length > 0 ? Math.Min(0, all.Min()) : 0d;
        if (max == min) max = min + 1;
        var span = max - min; max += span * .1;
        var pen = new Pen(HmiVisuals.Brush(Element.GetString("option." + numberAxis + ".splitLine.lineStyle.color", "#484753")), 1);
        for (int i = 0; i <= 4; i++)
        {
            var frac = i / 4d;
            if (horizontal)
            {
                var x = rect.Left + frac * rect.Width; if (Axis(numberAxis, "show") && Axis(numberAxis, "splitLine.show", false)) dc.DrawLine(pen, new Point(x, rect.Top), new Point(x, rect.Bottom));
                if (Axis(numberAxis, "show") && Axis(numberAxis, "axisLabel.show")) DrawText(dc, (min + (max - min) * (Axis(numberAxis, "inverse", false) ? 1 - frac : frac)).ToString("0.##"), new Point(x, rect.Bottom + 6), AxisSize(numberAxis), AxisColor(numberAxis), true);
            }
            else
            {
                var y = rect.Bottom - frac * rect.Height; if (Axis(numberAxis, "show") && Axis(numberAxis, "splitLine.show")) dc.DrawLine(pen, new Point(rect.Left, y), new Point(rect.Right, y));
                if (Axis(numberAxis, "show") && Axis(numberAxis, "axisLabel.show")) DrawText(dc, (min + (max - min) * (Axis(numberAxis, "inverse", false) ? 1 - frac : frac)).ToString("0.##"), new Point(3, y - 7), AxisSize(numberAxis), AxisColor(numberAxis));
            }
        }
        var n = Math.Max(1, Labels.Count); var group = (horizontal ? rect.Height : rect.Width) / n;
        for (int i = 0; i < n; i++)
        {
            if (i % Math.Max(1, (int)Math.Ceiling(n / 12d)) != 0) continue;
            var index = Axis(categoryAxis, "inverse", false) ? n - i - 1 : i;
            if (Axis(categoryAxis, "show") && Axis(categoryAxis, "axisLabel.show")) DrawText(dc, Labels.ElementAtOrDefault(i) ?? (i + 1).ToString(), horizontal ? new Point(3, rect.Top + group * (index + .5) - 6) : new Point(rect.Left + group * (index + .5), rect.Bottom + 8), AxisSize(categoryAxis), AxisColor(categoryAxis), !horizontal, horizontal ? rect.Left - 6 : Math.Max(35, group * 1.8), Element.GetNumber("option." + categoryAxis + ".axisLabel.rotate"));
        }
        for (int s = 0; s < Series.Count; s++)
        {
            var color = SeriesColor(s); var brush = HmiVisuals.Brush(color); var data = Series[s]; var path = $"option.series[{s}]";
            void ValueLabel(double value, Point point) { if (Element.GetBoolean(path + ".label.show")) DrawText(dc, value.ToString("0.##"), new Point(point.X, point.Y - Element.GetNumber(path + ".label.fontSize", 12) - 4), Element.GetNumber(path + ".label.fontSize", 12), Element.GetString(path + ".label.color", color), true); }
            var line = type.Contains("Line") && !type.Contains("Bar") || type == "OneD" || type == "BarLine" && s > 0;
            var points = new List<Point>();
            for (int i = 0; i < data.Length && i < n; i++)
            {
                var v = data[i]; if (!double.IsFinite(v)) continue;
                var normalized = (v - min) / (max - min); var zero = -min / (max - min); if (Axis(numberAxis, "inverse", false)) { normalized = 1 - normalized; zero = 1 - zero; }
                var index = Axis(categoryAxis, "inverse", false) ? n - i - 1 : i;
                var pt = new Point(rect.Left + group * (index + .5), rect.Bottom - normalized * rect.Height);
                if (line) { points.Add(pt); ValueLabel(v, pt); continue; }
                var size = Math.Max(1, group * .7 / Series.Count); var barWidth = Math.Clamp(Element.GetNumber(path + ".barWidth", size * .85), .5, group / Series.Count); var radius = Element.GetNumber(path + ".itemStyle.borderRadius", 2);
                if (horizontal)
                {
                    var a = rect.Left + zero * rect.Width; var b = rect.Left + normalized * rect.Width;
                    dc.DrawRoundedRectangle(brush, null, new Rect(Math.Min(a, b), rect.Top + group * (index + .5) - Series.Count * size / 2 + size * s, Math.Max(.5, Math.Abs(b - a)), barWidth), radius, radius); ValueLabel(v, new Point(b, rect.Top + group * (index + .5)));
                }
                else
                {
                    var a = rect.Bottom - zero * rect.Height;
                    dc.DrawRoundedRectangle(brush, null, new Rect(rect.Left + group * (index + .5) - Series.Count * size / 2 + size * s, Math.Min(a, pt.Y), barWidth, Math.Max(.5, Math.Abs(a - pt.Y))), radius, radius); ValueLabel(v, pt);
                }
            }
            if (line && points.Count > 0)
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(points[0], false, false);
                    for (int i = 1; i < points.Count; i++) { var previous = points[i - 1]; var next = points[i]; if (Element.GetBoolean(path + ".step") || Element.GetString(path + ".step") is "start" or "middle" or "end") { ctx.LineTo(new Point(next.X, previous.Y), true, false); ctx.LineTo(next, true, false); } else if (Element.GetBoolean(path + ".smooth")) { var middle = (previous.X + next.X) / 2; ctx.BezierTo(new Point(middle, previous.Y), new Point(middle, next.Y), next, true, false); } else ctx.LineTo(next, true, false); }
                }
                if (type == "LineGradients" && points.Count > 1)
                {
                    var fill = new StreamGeometry(); using (var ctx = fill.Open()) { ctx.BeginFigure(new Point(points[0].X, rect.Bottom), true, true); ctx.PolyLineTo(points, true, false); ctx.LineTo(new Point(points[^1].X, rect.Bottom), true, false); }
                    var c = HmiVisuals.Color(color); dc.DrawGeometry(new LinearGradientBrush(c, Color.FromArgb(0, c.R, c.G, c.B), 90), null, fill);
                }
                var linePen = new Pen(brush, Math.Max(0, Element.GetNumber(path + ".lineStyle.width", 2))); linePen.DashStyle = Element.GetString(path + ".lineStyle.type") switch { "dashed" => DashStyles.Dash, "dotted" => DashStyles.Dot, _ => DashStyles.Solid }; dc.DrawGeometry(null, linePen, geo);
                var symbol = Element.GetNumber(path + ".symbolSize", 5) / 2; if (symbol > 0) foreach (var p in points) dc.DrawEllipse(brush, null, p, symbol, symbol);
            }
        }
        if (Element.GetBoolean("option.legend.show", true))
            for (int s = 0; s < Series.Count; s++)
            {
                var verticalLegend = Element.GetString("option.legend.orient") == "vertical"; var font = Element.GetNumber("option.legend.textStyle.fontSize", 12); var x = rect.Left + (verticalLegend ? 0 : s * Math.Min(160, rect.Width / Series.Count)); var y = verticalLegend ? 10 + s * (font + 8) : 11;
                dc.DrawEllipse(HmiVisuals.Brush(SeriesColor(s)), null, new Point(x, y + font / 2), 5, 5);
                DrawText(dc, SeriesNames.ElementAtOrDefault(s) ?? "", new Point(x + 10, y), font, Element.GetString("option.legend.textStyle.color", "#B9B8CE"), false, 145);
            }
    }
    private string SeriesColor(int i)
    {
        var direct = Element.GetNode($"option.series[{i}].itemStyle.color"); if (direct is JsonValue value && value.TryGetValue<string>(out var specified) && !string.IsNullOrWhiteSpace(specified)) return specified;
        var colors = HmiVisuals.Strings(Element.GetNode("option.customThemeColor.themeColor.color"));
        if (colors.Length == 0) colors = HmiVisuals.Strings(Element.GetNode("option.colors"));
        return colors.Length > 0 ? colors[i % colors.Length] : HmiVisuals.Palette[i % HmiVisuals.Palette.Length];
    }
    private void DrawPie(DrawingContext dc, double w, double h)
    {
        var values = Series[0].Select(x => double.IsFinite(x) ? Math.Max(0, x) : 0).ToArray();
        if (Element.Type == "PieCircle") { var value = Math.Clamp(values.FirstOrDefault(), 0, 1); values = [value, 1 - value]; }
        var total = values.Sum(); var center = new Point(w / 2, h / 2 + 10); var radius = Math.Max(1, Math.Min(w, h) * .32);
        var ring = Element.Type != "PieCommon" || Element.GetString("option.type", "ring") == "ring";
        var thickness = ring ? radius * (Element.Type == "PieCircle" ? .14 : .38) : radius;
        var sweepStart = -90d;
        if (total <= 0) dc.DrawEllipse(null, new Pen(HmiVisuals.Brush("#45454b"), thickness), center, radius - thickness / 2, radius - thickness / 2);
        else for (int i = 0; i < values.Length; i++)
        {
            var degrees = values[i] / total * 360;
            if (degrees < .001) continue;
            var geom = new StreamGeometry();
            Point At(double angle, double r) => new(center.X + Math.Cos(angle * Math.PI / 180) * r, center.Y + Math.Sin(angle * Math.PI / 180) * r);
            var r = radius - thickness / 2;
            if (degrees >= 359.999) dc.DrawEllipse(null, new Pen(HmiVisuals.Brush(SeriesColor(i)), thickness), center, r, r);
            else
            {
                using (var ctx = geom.Open()) { ctx.BeginFigure(At(sweepStart, r), false, false); ctx.ArcTo(At(sweepStart + degrees, r), new Size(r, r), 0, degrees > 180, SweepDirection.Clockwise, true, false); }
                dc.DrawGeometry(null, new Pen(HmiVisuals.Brush(SeriesColor(i)), thickness), geom);
            }
            sweepStart += degrees;
        }
        if (Element.Type == "PieCircle") DrawText(dc, (values[0] * 100).ToString("0.#") + "%", new Point(center.X, center.Y - 18), Element.GetNumber("option.title.textStyle.fontSize", 30), Element.GetString("option.title.textStyle.color", "#ffffff"), true);
        else for (int i = 0; i < Math.Min(values.Length, 8); i++) DrawText(dc, $"● {Labels.ElementAtOrDefault(i)}  {values[i]:0.##}", new Point(8 + i % 3 * w / 3, 5 + i / 3 * 20), 12, SeriesColor(i), false, w / 3 - 12);
    }
    private void DrawText(DrawingContext dc, string text, Point pt, double size, string color, bool center = false, double maxWidth = 0, double rotation = 0)
    {
        var formatted = new FormattedText(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), Math.Clamp(size, 6, 100), HmiVisuals.Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (maxWidth > 0) { formatted.MaxTextWidth = Math.Max(1, maxWidth); formatted.Trimming = TextTrimming.CharacterEllipsis; formatted.MaxLineCount = 1; }
        if (center) pt.X -= formatted.Width / 2;
        if (rotation != 0) dc.PushTransform(new RotateTransform(rotation, pt.X, pt.Y)); dc.DrawText(formatted, pt); if (rotation != 0) dc.Pop();
    }
}
