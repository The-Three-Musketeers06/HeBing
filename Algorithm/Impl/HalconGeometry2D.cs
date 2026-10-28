using HalconDotNet;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

internal static class HalconGeometry2D
{
    public static bool Run(OpContext context, ParamBag parameters, string driver)
    {
        DetectionOverlay2D.Begin(context, driver);
        context.Vars.Clear();
        context.CancellationToken.ThrowIfCancellationRequested();
        var input = context.Images.Get(parameters.Int("IM In"));
        if (input == null || input.IsDisposed || input.Empty()) throw new InvalidOperationException("输入图像为空");
        _ = HalconRuntime.Version;
        using var gray = Img.ToGray8(input);
        using var image = new HImage("byte", gray.Width, gray.Height, gray.Data);
        using var shield = parameters.Bool("Use Shielding Area")
            ? BlobDetectOp.RegionMask(gray.Size(), parameters.Raw("Shielding Area"), parameters) : null;
        var measurement = new Measurement(context, parameters, image, gray.Width, gray.Height, shield);
        bool result = driver switch
        {
            "Basic Line Find" => FindLine(measurement),
            "Basic Circle Find" => FindCircle(measurement),
            "Basic Edge Point Find" => FindEdgePoints(measurement),
            _ => throw new NotSupportedException(driver)
        };
        context.Vars.Set("Result", result);
        DetectionOverlay2D.ReferencePoints(context, parameters, driver);
        return result;
    }

    private sealed class Measurement(OpContext context, ParamBag parameters, HImage image, int width, int height, Mat shield)
    {
        public OpContext Context => context;
        public ParamBag Parameters => parameters;
        public List<Point2d> Points { get; } = new();
        public double Scale { get; } = FixtureScale(parameters);
        public Point2d[] Map(params Point2d[] points) => parameters.Bool("Position Fix") ? BlobDetectOp.Transform(points, parameters) : points;

        public void Scan(Point2d center, Point2d direction, double length, double thickness)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (center.X < 0 || center.Y < 0 || center.X >= width || center.Y >= height) return;
            if (parameters.Str("Search Direction", "Forward") == "Backward") direction = new(-direction.X, -direction.Y);
            double sigma = Math.Max(.4, parameters.Int("Filter Width", 3) / 3d);
            using var measure = new HMeasure(center.Y, center.X, -Math.Atan2(direction.Y, direction.X), length / 2, thickness / 2, width, height, "bilinear");
            string transition = parameters.Str("Polarity", "All") switch
            {
                "Dark -> Bright" => "positive", "Bright -> Dark" => "negative", _ => "all"
            };
            measure.MeasurePos(image, sigma, Math.Max(.001, parameters.Dbl("Min Contrast", 10)), transition, "all",
                out var rows, out var columns, out var amplitudes, out var distances);
            using (rows) using (columns) using (amplitudes) using (distances)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var valid = Enumerable.Range(0, rows.Length).Where(index =>
                {
                    var x = columns[index].D; var y = rows[index].D;
                    if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= width || y >= height) return false;
                    return shield == null || shield.At<byte>(Math.Clamp((int)Math.Round(y), 0, height - 1), Math.Clamp((int)Math.Round(x), 0, width - 1)) == 0;
                }).ToArray();
                if (valid.Length == 0) return;
                IEnumerable<int> selected = parameters.Str("Selection", "First") switch
                {
                    "All" => valid,
                    "Last" => [valid[^1]],
                    "Strongest" => [valid.MaxBy(index => Math.Abs(amplitudes[index].D))],
                    _ => [valid[0]]
                };
                foreach (var index in selected) Points.Add(new(columns[index].D, rows[index].D));
            }
        }
    }

    private static double FixtureScale(ParamBag p)
    {
        if (!p.Bool("Position Fix")) return 1;
        var info = p.Group("Points Information");
        var basis = Group(info, "Base Point"); var run = Group(info, "Run Point");
        if (basis == null || run == null || Group(basis, "Point") == null || Group(run, "Point") == null)
            throw new InvalidOperationException("缺少定位基准或运行位姿，请先运行前置定位工具");
        double bx = Number(basis, "ScaleX", 1), by = Number(basis, "ScaleY", 1);
        double rx = Number(run, "ScaleX", 1), ry = Number(run, "ScaleY", 1);
        if (bx <= 0 || by <= 0 || rx <= 0 || ry <= 0 || Math.Abs(rx / bx - ry / by) > 1e-6)
            throw new InvalidOperationException("二维卡尺定位修正需要有效的等比缩放");
        return rx / bx;
    }

    private static bool FindLine(Measurement measurement)
    {
        var p = measurement.Parameters; var context = measurement.Context;
        const string driver = "Basic Line Find";
        var caliper = Group(p.Group("Region"), "line_caliper") ?? throw new ArgumentException("请绘制直线卡尺区域");
        var ends = measurement.Map(Point(Group(caliper, "Start")), Point(Group(caliper, "End")));
        var delta = ends[1] - ends[0]; var length = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        if (length < 1) throw new ArgumentException("直线卡尺起点与终点不能重合");
        var normal = new Point2d(-delta.Y / length, delta.X / length);
        int count = Math.Clamp((int)Number(caliper, "Caliper Count", 20), 3, 200);
        double depth = Positive(caliper, "Caliper Height", 20) * measurement.Scale;
        double breadth = Positive(caliper, "Caliper Width", 5) * measurement.Scale;
        for (int i = 0; i < count; i++)
        {
            double t = (i + .5) / count;
            measurement.Scan(new(ends[0].X + delta.X * t, ends[0].Y + delta.Y * t), normal, depth, breadth);
        }
        if (measurement.Points.Count < 2) return Missing(context, "HALCON 查找直线：有效边缘点不足");
        var points = measurement.Points;
        var fit = FitLine(points, p.Str("Fit Method", "Not Ignore") == "Auto Ignore");
        if (p.Str("Fit Method") == "Ignore Exception" && p.Int("Ignore Point Count") > 0)
        {
            using var rows = new HTuple(points.Select(point => point.Y).ToArray());
            using var cols = new HTuple(points.Select(point => point.X).ToArray());
            HOperatorSet.DistancePl(rows, cols, fit.Y1, fit.X1, fit.Y2, fit.X2, out var distances);
            using (distances) points = KeepPoints(points, distances.DArr, p, 2);
            fit = FitLine(points, false);
        }
        using var endRows = new HTuple(ends.Select(point => point.Y).ToArray());
        using var endColumns = new HTuple(ends.Select(point => point.X).ToArray());
        HOperatorSet.ProjectionPl(endRows, endColumns, fit.Y1, fit.X1, fit.Y2, fit.X2, out var projectedRows, out var projectedColumns);
        SegmentObj segment;
        using (projectedRows) using (projectedColumns)
            segment = new() { X1 = projectedColumns[0].D, Y1 = projectedRows[0].D, X2 = projectedColumns[1].D, Y2 = projectedRows[1].D };
        context.Vars.Set("Segment", segment); context.Vars.Set("Angle", segment.Angle);
        context.Vars.Set("Length", segment.Length); context.Vars.Set("EdgePointCount", points.Count);
        if (p.Bool("Display Segment", true)) DetectionOverlay2D.Add(context, p, driver, "segment2d",
            new() { ["Start"] = DetectionOverlay2D.XY(segment.X1, segment.Y1), ["End"] = DetectionOverlay2D.XY(segment.X2, segment.Y2) });
        if (p.Bool("Display Points")) foreach (var point in points) DetectionOverlay2D.Point(context, p, driver, point.X, point.Y);
        context.Log($"HALCON 查找直线：{points.Count}点，角度{segment.Angle:0.000}°，长度{segment.Length:0.000}", false);
        return true;
    }

    private static SegmentObj FitLine(List<Point2d> points, bool robust)
    {
        using var rows = new HTuple(points.Select(point => point.Y).ToArray());
        using var cols = new HTuple(points.Select(point => point.X).ToArray());
        using var contour = new HXLDCont(); contour.GenContourPolygonXld(rows, cols);
        contour.FitLineContourXld(robust ? "tukey" : "regression", -1, 0, 5, 2,
            out double y1, out double x1, out double y2, out double x2, out _, out _, out _);
        return new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };
    }

    private static bool FindCircle(Measurement measurement)
    {
        var p = measurement.Parameters; var context = measurement.Context;
        const string driver = "Basic Circle Find";
        var caliper = Group(p.Group("Region"), "circle_caliper") ?? throw new ArgumentException("请绘制圆卡尺区域");
        double radius = Positive(caliper, "Radius", 0) * measurement.Scale;
        var originalCenter = Point(Group(caliper, "Center"));
        var axes = measurement.Map(originalCenter, new(originalCenter.X + 1, originalCenter.Y));
        var center = axes[0];
        double rotation = Math.Atan2(axes[1].Y - center.Y, axes[1].X - center.X);
        int count = Math.Clamp((int)Number(caliper, "Caliper Count", 20), 3, 200);
        double depth = Positive(caliper, "Caliper Height", 20) * measurement.Scale;
        double breadth = Positive(caliper, "Caliper Width", 5) * measurement.Scale;
        for (int i = 0; i < count; i++)
        {
            double angle = rotation + Math.Tau * i / count;
            var direction = new Point2d(Math.Cos(angle), Math.Sin(angle));
            measurement.Scan(new(center.X + direction.X * radius, center.Y + direction.Y * radius), direction, depth, breadth);
        }
        if (measurement.Points.Count < 3) return Missing(context, "HALCON 查找圆：有效边缘点不足");
        var points = measurement.Points;
        var circle = FitCircle(points, p.Str("Fit Method", "Not Ignore") == "Auto Ignore");
        if (p.Str("Fit Method") == "Ignore Exception" && p.Int("Ignore Point Count") > 0)
        {
            using var rows = new HTuple(points.Select(point => point.Y).ToArray());
            using var cols = new HTuple(points.Select(point => point.X).ToArray());
            HOperatorSet.DistancePp(rows, cols, circle.CenterY, circle.CenterX, out var distances);
            using (distances) points = KeepPoints(points, distances.DArr.Select(distance => Math.Abs(distance - circle.Radius)).ToArray(), p, 3);
            circle = FitCircle(points, false);
        }
        context.Vars.Set("Circle", circle); context.Vars.Set("Center", new PointObj { X = circle.CenterX, Y = circle.CenterY });
        context.Vars.Set("Radius", circle.Radius); context.Vars.Set("EdgePointCount", points.Count);
        if (p.Bool("Display Circle", true)) DetectionOverlay2D.Add(context, p, driver, "circle2d",
            new() { ["Center"] = DetectionOverlay2D.XY(circle.CenterX, circle.CenterY), ["Radius"] = circle.Radius });
        if (p.Bool("Display Points")) foreach (var point in points) DetectionOverlay2D.Point(context, p, driver, point.X, point.Y);
        context.Log($"HALCON 查找圆：圆心({circle.CenterX:0.000},{circle.CenterY:0.000})，半径{circle.Radius:0.000}，{points.Count}点", false);
        return true;
    }

    private static CircleObj FitCircle(List<Point2d> points, bool robust)
    {
        using var rows = new HTuple(points.Select(point => point.Y).ToArray());
        using var cols = new HTuple(points.Select(point => point.X).ToArray());
        using var contour = new HXLDCont(); contour.GenContourPolygonXld(rows, cols);
        contour.FitCircleContourXld(robust ? "geotukey" : "geometric", -1, 0, 0, 5, 2,
            out double row, out double column, out double radius, out _, out _, out string _);
        return new() { CenterX = column, CenterY = row, Radius = radius };
    }

    private static List<Point2d> KeepPoints(List<Point2d> points, double[] distances, ParamBag parameters, int minimum)
    {
        int remove = parameters.Int("Ignore Point Count");
        if (remove > points.Count - minimum)
        {
            if (!parameters.Bool("Reduce Ignore Nums")) throw new ArgumentException("剔除数量过多，剩余边缘点不足以拟合；请减小剔除数量或启用自动减少");
            remove = points.Count - minimum;
        }
        return Enumerable.Range(0, points.Count).OrderBy(index => distances[index]).Take(points.Count - remove)
            .OrderBy(index => index).Select(index => points[index]).ToList();
    }

    private static bool FindEdgePoints(Measurement measurement)
    {
        var p = measurement.Parameters; var context = measurement.Context;
        const string driver = "Basic Edge Point Find";
        var rect = Group(p.Group("Region"), "rect2d") ?? throw new ArgumentException("请绘制矩形搜索区域");
        var size = Group(rect, "Size");
        double width = Positive(size, "Width", 0) * measurement.Scale;
        double height = Positive(size, "Height", 0) * measurement.Scale;
        double angle = Number(rect, "Rotation") * Math.PI / 180;
        var origin = Point(Group(rect, "Center"));
        var axes = measurement.Map(origin, new(origin.X + Math.Cos(angle), origin.Y + Math.Sin(angle)));
        var direction = new Point2d((axes[1].X - axes[0].X) / measurement.Scale, (axes[1].Y - axes[0].Y) / measurement.Scale);
        int profiles = Math.Clamp((int)Math.Ceiling(height), 1, 512);
        for (int i = 0; i < profiles; i++)
        {
            double offset = (i + .5) * height / profiles - height / 2;
            measurement.Scan(new(axes[0].X - direction.Y * offset, axes[0].Y + direction.X * offset), direction, width, 1);
        }
        context.Vars.Set("Points", measurement.Points.Select(point => (object)new PointObj { X = point.X, Y = point.Y }).ToList());
        context.Vars.Set("Point Count", measurement.Points.Count);
        foreach (var point in measurement.Points) DetectionOverlay2D.Marker(context, p, driver, "Display Points", point);
        context.Log($"HALCON 查找边缘点：{measurement.Points.Count}点", measurement.Points.Count == 0);
        return measurement.Points.Count > 0;
    }

    private static bool Missing(OpContext context, string message) { context.Vars.Set("Message", message); context.Log(message, true); return false; }
    private static IReadOnlyDictionary<string, object> Group(IReadOnlyDictionary<string, object> map, string key)
        => map?.GetValueOrDefault(key) as IReadOnlyDictionary<string, object>;
    private static double Number(IReadOnlyDictionary<string, object> map, string key, double fallback = 0)
    {
        double value = DetectionOverlay2D.Number(map, key, fallback);
        if (!double.IsFinite(value)) throw new ArgumentException("参数必须为有限数值：" + key);
        return value;
    }
    private static double Positive(IReadOnlyDictionary<string, object> map, string key, double fallback)
    {
        double value = Number(map, key, fallback);
        if (value <= 0) throw new ArgumentException("请设置有效的" + key);
        return value;
    }
    private static Point2d Point(IReadOnlyDictionary<string, object> map)
    {
        if (map == null) throw new ArgumentException("缺少区域坐标");
        return new(Number(map, "X"), Number(map, "Y"));
    }
}
