using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Owned profile measurements in world units along a depth-image trajectory.</summary>
public sealed class TrajectorySealant3DOp : IOperator
{
    public string Driver => "3D TrajectorySealant";
    private sealed record Sample(Point2d Pixel, double U, double X, double Y, double Z)
    {
        public Dictionary<string, object> World => new() { ["X"] = X, ["Y"] = Y, ["Z"] = Z };
    }

    public bool Run(OpContext ctx, ParamBag p)
    {
        p.Context ??= ctx;
        DetectionOverlay2D.Begin(ctx, Driver);
        int im = p.Int("IM In"), im3d = p.Int("IM In 3D", 1);
        var image = ctx.Images.Get(im) ?? throw new ArgumentException($"输入深度图 IM{im} 不存在");
        var source = ctx.Clouds.Get(im3d) ?? throw new ArgumentException($"输入点云 IM{im3d} 不存在");
        var geometry = ctx.Images.GetDepthGeometry(im)
            ?? throw new ArgumentException("深度图缺少三维映射，请先运行点云转深度图工具");
        if (geometry.InputCloud != null && !ReferenceEquals(geometry.InputCloud, source))
            throw new ArgumentException("深度图与当前输入点云不匹配，请重新生成深度图");
        var grid = geometry.Raster; var cloud = geometry.Cloud;
        if (image.Width != grid.Width || image.Height != grid.Height) throw new ArgumentException("深度图尺寸已改变，请重新生成三维映射");
        var trace = p.Group("Trace");
        if (trace?.GetValueOrDefault("Points") is not IEnumerable<object> vertices) throw new ArgumentException("轨迹缺少点数组");
        var path = vertices.Select(q => q is IReadOnlyDictionary<string, object> point
            ? new Point2d(C3.N(point, "X", double.NaN), C3.N(point, "Y", double.NaN)) : new Point2d(double.NaN, double.NaN)).ToList();
        if (path.Count < 2 || path.Any(q => !double.IsFinite(q.X + q.Y))) throw new ArgumentException("轨迹至少需要两个有效点");
        bool closed = trace.GetValueOrDefault("Closed") is true;
        double smoothing = C3.N(trace, "Smoothing Coefficient", 0);
        if (!double.IsFinite(smoothing) || smoothing < 0 || smoothing > 1) throw new ArgumentException("平滑系数必须在 0..1 范围内");
        if (closed && Geo.Dist(path[0], path[^1]) < 1e-9) path.RemoveAt(path.Count - 1);
        if (smoothing > 0) path = Smooth(path, closed, smoothing);
        if (closed) path.Add(path[0]);
        var lengths = Enumerable.Range(0, path.Count - 1).Select(i => Geo.Dist(path[i], path[i + 1])).ToArray();
        double total = lengths.Sum();
        var interval = p.GroupDbl("Profile Settings", "Interval", 10);
        var width = p.GroupDbl("Profile Settings", "Profile Width", 20);
        var reference = p.GroupDbl("Profile Settings", "Reference Height", 0);
        var detection = p.GroupDbl("Profile Settings", "Detection Height", 0);
        var minPoints = p.GroupDbl("Profile Settings", "Minimum Number of Points Over Detection Height", 10);
        if (!(total > 0) || !double.IsFinite(interval) || interval <= 0 || !double.IsFinite(width) || width < 1 || width > 8192
            || !double.IsFinite(reference + detection) || !double.IsFinite(minPoints) || minPoints < 1 || minPoints != Math.Truncate(minPoints))
            throw new ArgumentException("轨迹长度、剖面间隔、宽度或最少点数无效");
        var countValue = Math.Ceiling(total / interval);
        if (countValue > 100000 || countValue * (Math.Ceiling(width) + 1) > 8_000_000)
            throw new ArgumentException("剖面采样超过 800 万点，请增大间隔或减小宽度");
        int count = Math.Max(1, (int)countValue);
        bool measureHeight = p.Bool("Height Measurement"), measureWidth = p.Group("Width Measurement")?.GetValueOrDefault("Enable") is true;
        bool measureMargin = p.Bool("Margin Measurement");
        var mode = p.Group("Width Measurement")?.GetValueOrDefault("Sealant Width Search Mode")?.ToString() ?? "upper than detection height";
        if (mode is not ("upper than detection height" or "lower than detection height")) throw new ArgumentException("无效的胶宽搜索模式");
        foreach (var (group, enabled) in new[] { ("Width", measureWidth), ("Height", measureHeight), ("Margin", measureMargin) })
            if (p.Group(group + " Result Judgement")?.GetValueOrDefault("Enable") is true && !enabled)
                throw new ArgumentException(group + " 判断已启用，但对应测量未启用");
        var heights = new List<object>(); var widths = new List<object>(); var margins = new List<object>();
        var leftWidths = new List<object>(); var rightWidths = new List<object>();
        var records = new List<object>(); var broken = new List<bool>();
        var widthBad = new List<bool>(); var heightBad = new List<bool>(); var marginBad = new List<bool>();
        DetectionOverlay2D.Trace(ctx, p, Driver, "Display Trace 2D", path);
        Sample At(Point2d pixel, double u)
        {
            int x = (int)Math.Round(pixel.X), y = (int)Math.Round(pixel.Y);
            if (x < 0 || y < 0 || x >= grid.Width || y >= grid.Height) return null;
            int index = grid.PointIndices[y * grid.Width + x];
            return index < 0 ? null : new(pixel, u, cloud.X[index], cloud.Y[index], cloud.Z[index]);
        }
        void WorldGraphic(string toggle, string type, Dictionary<string, object> values, bool ok = true)
        { if (p.Bool(toggle)) DetectionOverlay2D.Add(ctx, p, Driver, type, values, ok: ok, view: im3d); }
        void PointGraphics(string kind, Sample sample)
        {
            if (sample == null) return;
            DetectionOverlay2D.Marker(ctx, p, Driver, "Display " + kind + " Points 2D", sample.Pixel);
            WorldGraphic("Display " + kind + " Points 3D", "point3d", sample.World);
        }
        int segment = 0; double segmentStart = 0;
        for (int n = 0; n < count; n++)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            double distance = closed ? n * total / count : count == 1 ? total / 2 : n * total / (count - 1);
            while (segment < lengths.Length - 1 && segmentStart + lengths[segment] < distance)
            { segmentStart += lengths[segment]; segment++; }
            if (lengths[segment] < 1e-9) continue;
            var a = path[segment]; var b = path[segment + 1];
            var t = (distance - segmentStart) / lengths[segment];
            var center = new Point2d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
            var normal = new Point2d(-(b.Y - a.Y) / lengths[segment], (b.X - a.X) / lengths[segment]);
            int steps = (int)Math.Ceiling(width);
            var profile = new List<Sample>(); var used = new HashSet<(int X, int Y)>();
            var runs = new List<List<Sample>>(); var run = new List<Sample>();
            void Flush() { if (run.Count > 0) { runs.Add(run); run = new(); } }
            for (int k = 0; k <= steps; k++)
            {
                var u = -width / 2 + k * width / steps;
                var pixel = new Point2d(center.X + normal.X * u, center.Y + normal.Y * u);
                if (!used.Add(((int)Math.Round(pixel.X), (int)Math.Round(pixel.Y)))) continue;
                var sample = At(pixel, u);
                if (sample != null) profile.Add(sample);
                if (sample != null && (mode == "upper than detection height" ? sample.Z - reference > detection : sample.Z - reference < detection)) run.Add(sample);
                else Flush();
            }
            Flush();
            var chosen = runs.OrderBy(r => r.Min(q => Math.Abs(q.U))).ThenByDescending(r => r.Count).FirstOrDefault();
            var left = chosen?.FirstOrDefault(); var right = chosen?.LastOrDefault();
            var top = profile.OrderByDescending(q => q.Z).FirstOrDefault();
            bool isBroken = profile.Count(q => q.Z - reference > detection) < minPoints;
            double? h = top == null ? null : top.Z - reference;
            double? w = left == null || right == null ? null : Math.Sqrt(Math.Pow(right.X - left.X, 2) + Math.Pow(right.Y - left.Y, 2));
            var scale = Math.Sqrt(normal.X * normal.X * grid.ResolutionX * grid.ResolutionX + normal.Y * normal.Y * grid.ResolutionY * grid.ResolutionY);
            double? leftWidth = left == null ? null : Math.Abs(left.U) * scale;
            double? rightWidth = right == null ? null : Math.Abs(right.U) * scale;
            double? margin = leftWidth.HasValue && rightWidth.HasValue ? Math.Min(leftWidth.Value, rightWidth.Value) : null;
            heights.Add(h); widths.Add(w); margins.Add(margin); broken.Add(isBroken);
            leftWidths.Add(leftWidth); rightWidths.Add(rightWidth);
            widthBad.Add(Outside(p, "Width", w)); heightBad.Add(Outside(p, "Height", h)); marginBad.Add(Outside(p, "Margin", margin));
            records.Add(new Dictionary<string, object> { ["Index"] = n, ["Position"] = DetectionOverlay2D.XY(center.X, center.Y),
                ["Broken"] = isBroken, ["Height"] = h, ["Width"] = w, ["Margin"] = margin, ["Valid Points"] = profile.Count });
            if (measureWidth) { PointGraphics("Width", left); PointGraphics("Width", right); }
            if (measureHeight) PointGraphics("Height", top);
            if (measureMargin) { PointGraphics("Margin", left); PointGraphics("Margin", right); }
            var start = new Point2d(center.X - normal.X * width / 2, center.Y - normal.Y * width / 2);
            var end = new Point2d(center.X + normal.X * width / 2, center.Y + normal.Y * width / 2);
            DetectionOverlay2D.Segment(ctx, p, Driver, "Display Profile 2D", start, end);
            WorldGraphic("Display Profile 3D", "trace3d", new() { ["Points"] = profile.Select(q => (object)q.World).ToArray() });
            if (isBroken)
            {
                DetectionOverlay2D.Text(ctx, p, Driver, "Display Broken Text 2D", center, "断胶 " + (n + 1), false);
                if (p.Bool("Display Broken Text 3D") && (At(center, 0) ?? top) is { } position)
                    ctx.Texts.Add(new() { X = position.X, Y = position.Y, Z = position.Z, Text = "断胶 " + (n + 1),
                        Color = "red", Size = 14, ViewIndex = im3d,
                        Owner = string.IsNullOrEmpty(ctx.OperationUuid) ? Driver : ctx.OperationUuid });
            }
        }
        bool brokenFailed = Reject(p, "Broken", broken, closed);
        bool widthFailed = measureWidth && Reject(p, "Width", widthBad, closed);
        bool heightFailed = measureHeight && Reject(p, "Height", heightBad, closed);
        bool marginFailed = measureMargin && Reject(p, "Margin", marginBad, closed);
        bool failed = brokenFailed || widthFailed || heightFailed || marginFailed;
        // The reference schema contains both Result (execution) and result (judgement).
        ctx.Vars = new VarPool(caseSensitive: true);
        if (measureHeight) ctx.Vars.Set("height", heights);
        if (measureWidth) ctx.Vars.Set("width", widths);
        if (measureMargin) { ctx.Vars.Set("margin", margins); ctx.Vars.Set("left_width", leftWidths); ctx.Vars.Set("right_width", rightWidths); }
        ctx.Vars.Set("result", brokenFailed ? "Broken" : widthFailed ? "Width" : heightFailed ? "Height" : marginFailed ? "Margin" : "OK");
        ctx.Vars.Set("Result", true);
        ctx.Vars.Set("Pass", !failed); ctx.Vars.Set("Broken Count", broken.Count(v => v)); ctx.Vars.Set("Measurements", records);
        ctx.Log($"胶路检测-轨迹：{records.Count} 个剖面，断胶 {broken.Count(v => v)}，判定 {(failed ? "NG" : "OK")}", false);
        return true;
    }

    private static bool Outside(ParamBag p, string kind, double? value)
    {
        var g = p.Group(kind + " Result Judgement");
        if (g?.GetValueOrDefault("Enable") is not true) return false;
        double lo = C3.N(g, "Minimum Value", 0), hi = C3.N(g, "Maximum Value", 1);
        if (!double.IsFinite(lo + hi) || lo > hi) throw new ArgumentException(kind + " 判断上下限无效");
        return !value.HasValue || value < lo || value > hi;
    }

    private static bool Reject(ParamBag p, string kind, IReadOnlyList<bool> outliers, bool closed)
    {
        var group = p.Group(kind + " Result Judgement");
        if (kind != "Broken" && group?.GetValueOrDefault("Enable") is not true) return false;
        var mode = group?.GetValueOrDefault("Judgement Mode")?.ToString() ?? "arbitrary";
        var threshold = C3.N(group, "Number of Outliers", 3);
        if (threshold < 1 || threshold != Math.Truncate(threshold) || !double.IsFinite(threshold)) throw new ArgumentException("异常点数必须为正整数");
        if (mode == "arbitrary") return outliers.Count(v => v) >= threshold;
        if (mode != "continuity") throw new ArgumentException("无效的判断模式：" + mode);
        int streak = 0;
        var scan = closed ? outliers.Concat(outliers.Take(Math.Max(0, outliers.Count - 1))) : outliers;
        foreach (var bad in scan) { streak = bad ? streak + 1 : 0; if (Math.Min(streak, outliers.Count) >= threshold) return true; }
        return false;
    }

    private static List<Point2d> Smooth(List<Point2d> points, bool closed, double coefficient)
    {
        var result = new List<Point2d>(); double fraction = coefficient / 4;
        if (!closed) result.Add(points[0]);
        for (int i = 0; i < points.Count - (closed ? 0 : 1); i++)
        {
            var a = points[i]; var b = points[(i + 1) % points.Count];
            result.Add(new(a.X * (1 - fraction) + b.X * fraction, a.Y * (1 - fraction) + b.Y * fraction));
            result.Add(new(a.X * fraction + b.X * (1 - fraction), a.Y * fraction + b.Y * (1 - fraction)));
        }
        if (!closed) result.Add(points[^1]);
        return result;
    }
}
