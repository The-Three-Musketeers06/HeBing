using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using AiVisionStudio.Models;

namespace AiVisionStudio.Views;

/// <summary>Native renderers for original non-cartesian and multidimensional chart datasets.</summary>
public sealed class HmiAdvancedChart(HmiElement element) : FrameworkElement
{
    public static readonly HashSet<string> Types = ["ScatterCommon", "ScatterLogarithmicRegression", "Process", "WaterPolo", "Radar", "Funnel", "Dial", "Heatmap", "TreeMap", "Graph", "Sankey"];
    public JsonNode Data { get; private set; }
    public int PrimitiveCount { get; private set; }
    public void SetData(JsonNode data) { Data = data; InvalidateVisual(); }
    private string C(int index) { var palette = HmiVisuals.Strings(element.GetNode("option.customThemeColor.themeColor.color")); return palette.Length > 0 ? palette[Math.Abs(index) % palette.Length] : HmiVisuals.Palette[Math.Abs(index) % HmiVisuals.Palette.Length]; }
    private static double N(JsonNode node, double fallback = 0) => HmiVisuals.Number(node, fallback);
    private static string S(JsonNode node) => HmiVisuals.Text(node);
    private void Text(DrawingContext dc, string value, Point position, double size = 12, string color = "#dddddd", bool center = false, double maxWidth = 0)
    {
        var text = new FormattedText(value ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), Math.Clamp(size, 6, 200), HmiVisuals.Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (maxWidth > 0) { text.MaxTextWidth = maxWidth; text.MaxLineCount = 1; text.Trimming = TextTrimming.CharacterEllipsis; }
        dc.DrawText(text, new Point(position.X - (center ? text.Width / 2 : 0), position.Y));
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); PrimitiveCount = 0; var w = ActualWidth; var h = ActualHeight; if (w < 5 || h < 5) return;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h))); dc.DrawRectangle(HmiVisuals.Brush(element.Background, "transparent"), null, new Rect(0, 0, w, h));
        if (Data == null) Text(dc, "暂无数据", new Point(w / 2, h / 2 - 8), 16, "#888888", true);
        else switch (element.Type)
        {
            case "Process": case "WaterPolo": case "Dial": Gauge(dc, w, h); break;
            case "Radar": Radar(dc, w, h); break;
            case "Funnel": Funnel(dc, w, h); break;
            case "Heatmap": Heatmap(dc, w, h); break;
            case "TreeMap": TreeMap(dc, Data as JsonArray ?? [], new Rect(5, 5, Math.Max(1, w - 10), Math.Max(1, h - 10)), 0); break;
            case "Graph": Graph(dc, w, h); break;
            case "Sankey": Sankey(dc, w, h); break;
            default: Scatter(dc, w, h); break;
        }
        dc.Pop();
    }
    private static StreamGeometry Polygon(IEnumerable<Point> points)
    {
        var array = points.ToArray(); var geo = new StreamGeometry(); if (array.Length == 0) return geo;
        using var context = geo.Open(); context.BeginFigure(array[0], true, true); context.PolyLineTo(array.Skip(1).ToArray(), true, false); return geo;
    }
    private void Gauge(DrawingContext dc, double w, double h)
    {
        var value = N(Data); var radius = Math.Max(1, Math.Min(w, h) * .38); var center = new Point(w / 2, h / 2); var type = element.Type;
        if (type == "Process" && element.GetString("option.type") == "line")
        {
            var rect = new Rect(w * .05, h * .4, w * .9, Math.Max(4, h * .12)); dc.DrawRoundedRectangle(HmiVisuals.Brush(element.GetString("option.railColor", "#3e3e3f")), null, rect, rect.Height / 2, rect.Height / 2); dc.DrawRoundedRectangle(HmiVisuals.Brush(element.GetString("option.color", C(0))), null, new Rect(rect.Left, rect.Top, rect.Width * Math.Clamp(value / 100, 0, 1), rect.Height), rect.Height / 2, rect.Height / 2); Text(dc, value.ToString("0.##") + element.GetString("option.unit", "%"), new Point(w / 2, rect.Bottom + 4), element.GetNumber("option.indicatorTextSize", 20), element.GetString("option.indicatorTextColor", "#ffffff"), true); PrimitiveCount = 2; return;
        }
        if (type == "WaterPolo")
        {
            var ratio = Math.Clamp(value, 0, 1); var circle = new EllipseGeometry(center, radius, radius); dc.DrawGeometry(HmiVisuals.Brush(element.GetString("option.series[0].backgroundStyle.color", "#243560")), new Pen(HmiVisuals.Brush(C(0)), 2), circle); dc.PushClip(circle);
            var y = center.Y + radius - 2 * radius * ratio; var phase = DateTime.UtcNow.TimeOfDay.TotalSeconds;
            for (int layer = 1; layer >= 0; layer--)
            {
                var points = new List<Point> { new(center.X - radius, center.Y + radius) };
                for (int i = 0; i <= 80; i++) { var x = center.X - radius + i * radius / 40; points.Add(new(x, y + Math.Sin(i * Math.PI / 20 + phase + layer * 2) * radius * .045)); } points.Add(new(center.X + radius, center.Y + radius));
                var color = HmiVisuals.Color(element.GetString("option.series[0].color[0].colorStops[0].color", C(0))); dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(layer == 1 ? (byte)110 : (byte)210, color.R, color.G, color.B)), null, Polygon(points)); PrimitiveCount++;
            }
            dc.Pop(); Text(dc, (ratio * 100).ToString("0.##") + "%", new Point(center.X, center.Y - 20), element.GetNumber("option.series[0].label.fontSize", 40), element.GetString("option.series[0].label.color", "#ffffff"), true); return;
        }
        if (type == "Dial")
        {
            var minimum = element.GetNumber("option.series[0].min"); var maximum = element.GetNumber("option.series[0].max", 100); var ratio = Math.Clamp((value - minimum) / Math.Max(.001, maximum - minimum), 0, 1); var split = Math.Clamp((int)element.GetNumber("option.series[0].splitNumber", 10), 1, 100);
            Point At(double angle, double rad) => new(center.X + Math.Cos(angle * Math.PI / 180) * rad, center.Y + Math.Sin(angle * Math.PI / 180) * rad);
            for (int i = 0; i < 270; i++) { var angle = 135 + i; dc.DrawLine(new Pen(HmiVisuals.Brush(i / 270d <= ratio ? "#2af4ff" : "#254667"), radius * .08), At(angle, radius), At(angle + 1, radius)); }
            for (int i = 0; i <= split; i++) { var angle = 135 + 270d * i / split; dc.DrawLine(new Pen(HmiVisuals.Brush("#42e5fb"), 2), At(angle, radius * .85), At(angle, radius * .98)); Text(dc, (minimum + (maximum - minimum) * i / split).ToString("0.#"), At(angle, radius * .72), 11, "#dddddd", true); }
            dc.DrawLine(new Pen(HmiVisuals.Brush("#2af4ff"), Math.Max(2, element.GetNumber("option.series[0].pointer.width", 4))), center, At(135 + ratio * 270, radius * .78)); dc.DrawEllipse(HmiVisuals.Brush("#2af4ff"), null, center, 5, 5); Text(dc, value.ToString("0.##"), new Point(center.X, center.Y + radius * .55), 20, "#ffffff", true); PrimitiveCount = split + 2; return;
        }
        var progress = Math.Clamp(value / 100, 0, 1); var thickness = Math.Max(4, radius * .1); dc.DrawEllipse(null, new Pen(HmiVisuals.Brush(element.GetString("option.railColor", "#3e3e3f")), thickness), center, radius, radius);
        for (int i = 0; i < Math.Ceiling(360 * progress); i++) { var a = (i - 90 + element.GetNumber("option.offsetDegree")) * Math.PI / 180; var b = a + Math.PI / 180; dc.DrawLine(new Pen(HmiVisuals.Brush(element.GetString("option.color", C(0))), thickness), new Point(center.X + Math.Cos(a) * radius, center.Y + Math.Sin(a) * radius), new Point(center.X + Math.Cos(b) * radius, center.Y + Math.Sin(b) * radius)); }
        Text(dc, value.ToString("0.##") + element.GetString("option.unit", "%"), new Point(center.X, center.Y - 20), element.GetNumber("option.indicatorTextSize", 34), element.GetString("option.indicatorTextColor", "#ffffff"), true); PrimitiveCount = 2;
    }
    private void Radar(DrawingContext dc, double w, double h)
    {
        var indicators = Data?["radarIndicator"] as JsonArray ?? element.GetNode("option.radar.indicator") as JsonArray ?? [];
        var series = Data?["seriesData"] as JsonArray ?? []; var n = indicators.Count; if (n < 3) return;
        var center = new Point(w / 2, h * .54); var radius = Math.Min(w, h) * .34;
        Point At(int i, double value) => new(center.X + Math.Cos(i * Math.PI * 2 / n - Math.PI / 2) * radius * value, center.Y + Math.Sin(i * Math.PI * 2 / n - Math.PI / 2) * radius * value);
        for (int level = 1; level <= 5; level++) dc.DrawGeometry(level % 2 == 0 ? HmiVisuals.Brush("#ffffff09") : null, new Pen(HmiVisuals.Brush("#5c6271"), 1), Polygon(Enumerable.Range(0, n).Select(i => At(i, level / 5d))));
        for (int i = 0; i < n; i++) { dc.DrawLine(new Pen(HmiVisuals.Brush("#5c6271"), 1), center, At(i, 1)); Text(dc, S(indicators[i]?["name"]), At(i, 1.15), element.GetNumber("option.radar.axisName.fontSize", 12), element.GetString("option.radar.axisName.color", "#eeeeee"), true); }
        var fallbackMax = series.OfType<JsonObject>().SelectMany(s => s["value"] as JsonArray ?? []).Select(x => N(x)).DefaultIfEmpty(1).Max();
        for (int s = 0; s < series.Count; s++)
        {
            var values = series[s]?["value"] as JsonArray ?? []; var points = Enumerable.Range(0, n).Select(i => At(i, Math.Clamp(N(values.ElementAtOrDefault(i)) / Math.Max(.001, N(indicators[i]?["max"], fallbackMax)), 0, 1))).ToArray(); var color = HmiVisuals.Color(C(s));
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(45, color.R, color.G, color.B)), new Pen(new SolidColorBrush(color), 2), Polygon(points)); foreach (var p in points) dc.DrawEllipse(new SolidColorBrush(color), null, p, 3, 3); Text(dc, "● " + S(series[s]?["name"]), new Point(15 + s * 130, 10), 13, C(s)); PrimitiveCount += points.Length;
        }
    }
    private void Funnel(DrawingContext dc, double w, double h)
    {
        var rows = HmiVisuals.Rows(Data); var dimensions = HmiVisuals.Strings(Data?["dimensions"]); var labelKey = dimensions.FirstOrDefault() ?? "name"; var valueKey = dimensions.ElementAtOrDefault(1) ?? "value"; var values = rows.Select(r => (Label: HmiVisuals.Text(r.GetValueOrDefault(labelKey)), Value: Math.Max(0, HmiVisuals.Number(r.GetValueOrDefault(valueKey))))).ToArray();
        values = element.GetString("option.series[0].sort", "descending") == "ascending" ? values.OrderBy(x => x.Value).ToArray() : values.OrderByDescending(x => x.Value).ToArray(); if (values.Length == 0) return;
        var maximum = Math.Max(1, values.Max(x => x.Value)); var rowHeight = Math.Max(1, (h - 50) / values.Length); var gap = element.GetNumber("option.series[0].gap", 5);
        for (int i = 0; i < values.Length; i++)
        {
            var top = w * .8 * values[i].Value / maximum; var bottom = i + 1 < values.Length ? w * .8 * values[i + 1].Value / maximum : top * .4; var y = 35 + i * rowHeight;
            dc.DrawGeometry(HmiVisuals.Brush(C(i)), new Pen(HmiVisuals.Brush(element.GetString("option.series[0].itemStyle.borderColor", "transparent")), element.GetNumber("option.series[0].itemStyle.borderWidth")), Polygon([new(w / 2 - top / 2, y), new(w / 2 + top / 2, y), new(w / 2 + bottom / 2, y + Math.Max(1, rowHeight - gap)), new(w / 2 - bottom / 2, y + Math.Max(1, rowHeight - gap))])); Text(dc, values[i].Label + "  " + values[i].Value.ToString("0.##"), new Point(w / 2, y + rowHeight / 2 - 7), 12, "#ffffff", true); PrimitiveCount++;
        }
    }
    private void Heatmap(DrawingContext dc, double w, double h)
    {
        var xs = HmiVisuals.Strings(Data?["xAxis"]); var ys = HmiVisuals.Strings(Data?["yAxis"]); var rows = Data?["seriesData"] as JsonArray ?? []; if (xs.Length == 0 || ys.Length == 0) return;
        var rect = new Rect(Math.Min(65, w * .15), 25, Math.Max(1, w - 90), Math.Max(1, h - 65)); var cw = rect.Width / xs.Length; var ch = rect.Height / ys.Length; var min = element.GetNumber("option.visualMap.min"); var max = element.GetNumber("option.visualMap.max", 10);
        var colors = HmiVisuals.Strings(element.GetNode("option.visualMap.inRange.color")); var low = HmiVisuals.Color(colors.FirstOrDefault() ?? "#263253"); var high = HmiVisuals.Color(colors.LastOrDefault() ?? "#ff8656");
        foreach (var row in rows.OfType<JsonArray>())
        {
            if (row.Count < 3) continue; var x = (int)N(row[0]); var y = (int)N(row[1]); var value = N(row[2], double.NaN); if (x < 0 || x >= xs.Length || y < 0 || y >= ys.Length || !double.IsFinite(value)) continue; var t = Math.Clamp((value - min) / Math.Max(.001, max - min), 0, 1);
            var color = Color.FromRgb((byte)(low.R + (high.R - low.R) * t), (byte)(low.G + (high.G - low.G) * t), (byte)(low.B + (high.B - low.B) * t)); dc.DrawRectangle(new SolidColorBrush(color), new Pen(HmiVisuals.Brush("#ffffff20"), .5), new Rect(rect.Left + x * cw, rect.Top + y * ch, cw, ch)); if (cw > 18 && ch > 15) Text(dc, value.ToString("0.#"), new Point(rect.Left + (x + .5) * cw, rect.Top + (y + .5) * ch - 6), 10, "#ffffff", true); PrimitiveCount++;
        }
        for (int i = 0; i < xs.Length; i += Math.Max(1, (int)Math.Ceiling(xs.Length / 12d))) Text(dc, xs[i], new Point(rect.Left + (i + .5) * cw, rect.Bottom + 5), 10, "#dddddd", true);
        for (int i = 0; i < ys.Length; i++) Text(dc, ys[i], new Point(2, rect.Top + (i + .5) * ch - 6), 10, "#dddddd", false, rect.Left - 4);
    }
    private void TreeMap(DrawingContext dc, JsonArray items, Rect area, int depth)
    {
        if (depth > 8 || area.Width < 2 || area.Height < 2) return; var total = items.Sum(x => Math.Max(0, N(x?["value"]))); if (total <= 0) return; var offset = 0d;
        for (int i = 0; i < items.Count; i++)
        {
            var ratio = Math.Max(0, N(items[i]?["value"])) / total; var horizontal = area.Width >= area.Height; var cell = horizontal ? new Rect(area.Left + offset, area.Top, area.Width * ratio, area.Height) : new Rect(area.Left, area.Top + offset, area.Width, area.Height * ratio); offset += horizontal ? cell.Width : cell.Height;
            dc.DrawRectangle(HmiVisuals.Brush(C(i + depth)), new Pen(HmiVisuals.Brush("#25252a"), 2), cell); Text(dc, S(items[i]?["name"]) + " " + N(items[i]?["value"]).ToString("0.#"), new Point(cell.Left + 5, cell.Top + 5), 12, "#ffffff", false, Math.Max(1, cell.Width - 10)); PrimitiveCount++;
            if (items[i]?["children"] is JsonArray children && children.Count > 0 && cell.Width > 25 && cell.Height > 40) TreeMap(dc, children, new Rect(cell.Left + 5, cell.Top + 25, cell.Width - 10, cell.Height - 30), depth + 1);
        }
    }
    private void Scatter(DrawingContext dc, double w, double h)
    {
        var datasets = Data as JsonArray ?? []; var points = new List<(double X, double Y, int Series)>();
        for (int d = 0; d < datasets.Count; d++) if (datasets[d]?["source"] is JsonArray rows) foreach (var row in rows.OfType<JsonArray>()) { if (row.Count < 2) continue; var x = N(row[0], double.NaN); var y = N(row[1], double.NaN); if (double.IsFinite(x) && double.IsFinite(y)) points.Add((x, y, element.Type == "ScatterLogarithmicRegression" && row.Count > 4 ? N(row[4]) > 2000 ? 1 : 0 : d)); }
        if (points.Count == 0) return; var xMin = points.Min(x => x.X); var xMax = points.Max(x => x.X); var yMin = points.Min(x => x.Y); var yMax = points.Max(x => x.Y); var rect = new Rect(45, 35, Math.Max(1, w - 70), Math.Max(1, h - 70));
        Point At(double x, double y) => new(rect.Left + (x - xMin) / Math.Max(.001, xMax - xMin) * rect.Width, rect.Bottom - (y - yMin) / Math.Max(.001, yMax - yMin) * rect.Height);
        for (int i = 0; i <= 4; i++) { var t = i / 4d; dc.DrawLine(new Pen(HmiVisuals.Brush("#484753"), 1), new Point(rect.Left, rect.Bottom - rect.Height * t), new Point(rect.Right, rect.Bottom - rect.Height * t)); Text(dc, (yMin + (yMax - yMin) * t).ToString("0.#"), new Point(2, rect.Bottom - rect.Height * t - 6), 10); Text(dc, (xMin + (xMax - xMin) * t).ToString("0.#"), new Point(rect.Left + rect.Width * t, rect.Bottom + 6), 10, "#dddddd", true); }
        foreach (var point in points) { var r = Math.Clamp(element.GetNumber($"option.series[{point.Series}].symbolSize", 8) / 2, 1, 30); dc.DrawEllipse(HmiVisuals.Brush(C(point.Series)), null, At(point.X, point.Y), r, r); PrimitiveCount++; }
        if (element.Type == "ScatterLogarithmicRegression")
        {
            var valid = points.Where(p => p.X > 0).ToArray(); if (valid.Length > 1) { var avgX = valid.Average(p => Math.Log(p.X)); var avgY = valid.Average(p => p.Y); var denom = valid.Sum(p => Math.Pow(Math.Log(p.X) - avgX, 2)); var b = denom > 0 ? valid.Sum(p => (Math.Log(p.X) - avgX) * (p.Y - avgY)) / denom : 0; var a = avgY - b * avgX; Point? before = null; for (int i = 0; i < 128; i++) { var x = Math.Max(.0001, xMin + (xMax - xMin) * i / 127); var p = At(x, a + b * Math.Log(x)); if (before.HasValue) dc.DrawLine(new Pen(HmiVisuals.Brush(C(2)), 2), before.Value, p); before = p; } Text(dc, $"y = {a:0.###} + {b:0.###} ln(x)", new Point(50, 8), 12, C(2)); PrimitiveCount++; }
        }
    }
    private void Graph(DrawingContext dc, double w, double h)
    {
        var nodes = Data?["nodes"] as JsonArray ?? Data?["data"] as JsonArray ?? []; var links = Data?["links"] as JsonArray ?? []; if (nodes.Count == 0) return;
        var list = nodes.OfType<JsonObject>().ToArray(); var xmin = list.Min(n => N(n["x"])); var xmax = list.Max(n => N(n["x"])); var ymin = list.Min(n => N(n["y"])); var ymax = list.Max(n => N(n["y"])); var points = new Dictionary<string, Point>();
        for (int i = 0; i < list.Length; i++) { var n = list[i]; var p = xmax > xmin && ymax > ymin ? new Point(25 + (N(n["x"]) - xmin) / (xmax - xmin) * Math.Max(1, w - 50), 25 + (N(n["y"]) - ymin) / (ymax - ymin) * Math.Max(1, h - 50)) : new Point(w / 2 + Math.Cos(i * Math.PI * 2 / list.Length) * w * .38, h / 2 + Math.Sin(i * Math.PI * 2 / list.Length) * h * .38); points[S(n["id"] ?? n["name"])] = p; }
        foreach (var link in links.OfType<JsonObject>()) if (points.TryGetValue(S(link["source"]), out var source) && points.TryGetValue(S(link["target"]), out var target)) { dc.DrawLine(new Pen(HmiVisuals.Brush("#9bb5d050"), 1), source, target); PrimitiveCount++; }
        foreach (var n in list) { var p = points[S(n["id"] ?? n["name"])]; var r = Math.Clamp(N(n["symbolSize"], 14) / 2, 3, 35); dc.DrawEllipse(HmiVisuals.Brush(C((int)N(n["category"]))), null, p, r, r); if (r >= 8 || list.Length < 15) Text(dc, S(n["name"]), new Point(p.X, p.Y + r + 2), 10, "#dddddd", true); PrimitiveCount++; }
    }
    private void Sankey(DrawingContext dc, double w, double h)
    {
        var nodes = Data?["label"] as JsonArray ?? Data?["data"] as JsonArray ?? []; var links = Data?["links"] as JsonArray ?? []; var names = nodes.OfType<JsonObject>().Select(n => S(n["name"])).Distinct().ToArray(); if (names.Length == 0) return;
        var depths = names.ToDictionary(n => n, _ => 0); for (int iteration = 0; iteration < names.Length; iteration++) foreach (var link in links.OfType<JsonObject>()) { var from = S(link["source"]); var to = S(link["target"]); if (depths.ContainsKey(from) && depths.ContainsKey(to)) depths[to] = Math.Min(names.Length - 1, Math.Max(depths[to], depths[from] + 1)); }
        var columns = depths.GroupBy(x => x.Value).OrderBy(x => x.Key).ToArray(); var rects = new Dictionary<string, Rect>(); var maxLink = Math.Max(1, links.OfType<JsonObject>().Select(x => N(x["value"])).DefaultIfEmpty(1).Max());
        for (int c = 0; c < columns.Length; c++) { var list = columns[c].ToArray(); for (int i = 0; i < list.Length; i++) rects[list[i].Key] = new Rect(30 + c * Math.Max(1, w - 85) / Math.Max(1, columns.Length - 1), 25 + i * Math.Max(1, h - 50) / list.Length, 16, Math.Max(12, (h - 70) / list.Length * .6)); }
        foreach (var link in links.OfType<JsonObject>()) if (rects.TryGetValue(S(link["source"]), out var a) && rects.TryGetValue(S(link["target"]), out var b))
        {
            var geom = new StreamGeometry(); using (var context = geom.Open()) { context.BeginFigure(new Point(a.Right, a.Top + a.Height / 2), false, false); context.BezierTo(new Point((a.Right + b.Left) / 2, a.Top + a.Height / 2), new Point((a.Right + b.Left) / 2, b.Top + b.Height / 2), new Point(b.Left, b.Top + b.Height / 2), true, false); }
            var color = HmiVisuals.Color(C(Array.IndexOf(names, S(link["source"])))); dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(100, color.R, color.G, color.B)), Math.Max(2, N(link["value"]) / maxLink * Math.Min(40, a.Height * .7))), geom); PrimitiveCount++;
        }
        for (int i = 0; i < names.Length; i++) { var r = rects[names[i]]; dc.DrawRectangle(HmiVisuals.Brush(C(i)), null, r); Text(dc, names[i], new Point(r.Left + 20, r.Top + r.Height / 2 - 7), 12); PrimitiveCount++; }
    }
}
