using System.Globalization;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Finite, double precision geometry used by the native-format measurement operators.</summary>
internal readonly record struct MeasureVector3(double X, double Y, double Z)
{
    public static MeasureVector3 operator +(MeasureVector3 a, MeasureVector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static MeasureVector3 operator -(MeasureVector3 a, MeasureVector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static MeasureVector3 operator *(MeasureVector3 a, double v) => new(a.X * v, a.Y * v, a.Z * v);
    public double Dot(MeasureVector3 b) => X * b.X + Y * b.Y + Z * b.Z;
    public MeasureVector3 Cross(MeasureVector3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    public double Length => Math.Sqrt(Dot(this));
    public bool Finite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    public MeasureVector3 Unit()
    {
        var scale = Math.Max(Math.Abs(X), Math.Max(Math.Abs(Y), Math.Abs(Z)));
        if (!Finite || scale <= 0) throw new ArgumentException("几何方向必须是有限的非零向量");
        var scaled = this * (1 / scale);
        return scaled * (1 / scaled.Length);
    }
    public Dictionary<string, object> Native() => new(StringComparer.OrdinalIgnoreCase) { ["X"] = X, ["Y"] = Y, ["Z"] = Z };
    public static MeasureVector3 Read(IReadOnlyDictionary<string, object> parent, string name)
    {
        if (parent == null || !parent.TryGetValue(name, out var raw) || raw is not IReadOnlyDictionary<string, object> group)
            throw new ArgumentException("缺少几何参数：" + name);
        double ReadAxis(string axis)
        {
            if (!group.TryGetValue(axis, out var v) || !double.TryParse(v?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                throw new ArgumentException("无效几何坐标：" + name + "." + axis);
            return number;
        }
        return new(ReadAxis("X"), ReadAxis("Y"), ReadAxis("Z"));
    }
}

/// <summary>Line/line, line/plane and plane/plane intersection with explicit degeneracy handling.</summary>
public sealed class GeometryIntersect3DOp : IOperator
{
    public string Driver => "3D GeometryIntersect";
    private readonly record struct Geometry(bool Plane, MeasureVector3 Position, MeasureVector3 Direction);
    private static Geometry ReadGeometry(object value)
    {
        if (value is PlaneObj plane)
        {
            var n = new MeasureVector3(plane.NX, plane.NY, plane.NZ);
            var unit = n.Unit();
            if (!double.IsFinite(plane.D)) throw new ArgumentException("平面方程无效");
            return new(true, unit * (-plane.D / n.Length), unit);
        }
        if (value is not IReadOnlyDictionary<string, object> raw) throw new ArgumentException("几何对象未绑定或类型不匹配");
        string type = raw.TryGetValue("Type", out var t) ? t?.ToString() : raw.ContainsKey("Normal") ? "plane" : "line3d";
        if (type is not ("line3d" or "plane")) throw new ArgumentException("不支持的相交几何类型：" + type);
        var g = raw.TryGetValue(type, out var nested) && nested is IReadOnlyDictionary<string, object> fields ? fields : raw;
        var pos = MeasureVector3.Read(g, "Position");
        var direction = MeasureVector3.Read(g, type == "plane" ? "Normal" : "Direction").Unit();
        return new(type == "plane", pos, direction);
    }
    public bool Run(OpContext ctx, ParamBag p)
    {
        ctx.CancellationToken.ThrowIfCancellationRequested();
        var a = ReadGeometry(p.Raw("First Object"));
        var b = ReadGeometry(p.Raw("Second Object"));
        var tolerance = p.Dbl("Distance Tolerance", 7);
        if (!double.IsFinite(tolerance) || tolerance < 0) throw new ArgumentException("相交距离公差必须是非负有限值");
        MeasureVector3 point;
        MeasureVector3? lineDirection = null;
        if (a.Plane && b.Plane)
        {
            var cross = a.Direction.Cross(b.Direction);
            var denominator = cross.Dot(cross);
            if (denominator <= 1e-20) { ctx.Log("几何交点：平行或重合平面没有唯一交线", true); return false; }
            // n1.p=c1, n2.p=c2; the point closest to the origin lies perpendicular to the intersection line.
            var c1 = a.Direction.Dot(a.Position); var c2 = b.Direction.Dot(b.Position);
            point = (b.Direction.Cross(cross) * c1 + cross.Cross(a.Direction) * c2) * (1 / denominator);
            lineDirection = cross.Unit();
            ctx.Vars.Set("intersection_line", new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["Position"] = point.Native(), ["Direction"] = lineDirection.Value.Native() });
        }
        else if (a.Plane || b.Plane)
        {
            var plane = a.Plane ? a : b; var line = a.Plane ? b : a;
            var denominator = plane.Direction.Dot(line.Direction);
            if (Math.Abs(denominator) <= 1e-10) { ctx.Log("几何交点：直线与平面没有唯一交点", true); return false; }
            point = line.Position + line.Direction * (plane.Direction.Dot(plane.Position - line.Position) / denominator);
            ctx.Vars.Set("intersection_point", point.Native());
        }
        else
        {
            var dot = a.Direction.Dot(b.Direction); var denominator = 1 - dot * dot;
            if (denominator <= 1e-20) { ctx.Log("几何交点：平行或重合直线没有唯一交点", true); return false; }
            var offset = a.Position - b.Position;
            var d = a.Direction.Dot(offset); var e = b.Direction.Dot(offset);
            var q1 = a.Position + a.Direction * ((dot * e - d) / denominator);
            var q2 = b.Position + b.Direction * ((e - dot * d) / denominator);
            var separation = (q1 - q2).Length;
            if (separation > tolerance + 1e-10) { ctx.Log($"几何交点：两直线距离 {separation:G8} 超过公差 {tolerance:G8}", true); return false; }
            point = (q1 + q2) * .5;
            ctx.Vars.Set("intersection_point", point.Native());
        }
        if (!point.Finite) throw new ArithmeticException("相交计算产生非有限坐标");
        if (p.Bool("Display Intersection Point"))
        {
            if (lineDirection.HasValue) ctx.Texts.Add(new Text3D { X = point.X, Y = point.Y, Z = point.Z, Text = "交线", Color = "yellow", Size = 14, ViewIndex = p.Int("IM In") });
            else ctx.NativeOverlays.Add(new() { Type = "point3d", ViewIndex = p.Int("IM In"), Values = point.Native() });
        }
        ctx.Log(lineDirection.HasValue ? "几何相交：已生成三维交线" : $"几何交点：({point.X:0.000}, {point.Y:0.000}, {point.Z:0.000})", false);
        return true;
    }
}

/// <summary>Least-squares plane flatness over selected points or per-region representative points.</summary>
public sealed class Flatness3DOp : IOperator
{
    public string Driver => "3D Flatness";
    public bool Run(OpContext ctx, ParamBag p)
    {
        var cloud = ctx.Clouds.Get(p.Int("IM In"));
        if (cloud == null || cloud.Count == 0) { ctx.Log("3D平面度：输入点云为空", true); return false; }
        var mode = p.Str("Flatness Mode", "All Points");
        if (mode is not ("All Points" or "Single Average Point" or "Highest Point")) throw new ArgumentException("无效平面度选点方式：" + mode);
        var filter = p.Str("Filter Mode", "None");
        if (filter is not ("None" or "Percentile")) throw new ArgumentException("无效平面度过滤方式：" + filter);
        var low = p.Dbl("Low Percentile", 10); var high = p.Dbl("High Percentile", 10);
        if (filter == "Percentile" && (!double.IsFinite(low) || !double.IsFinite(high) || low < 0 || high < 0 || low > 50 || high > 50 || low + high >= 100))
            throw new ArgumentException("高低百分位必须在0至50之间，且不能同时为50");
        var reference = p.Group("Reference");
        var toReference = CloudRegionSelector.CreateReferenceTransform(reference);
        double Height(int i) => toReference(cloud.X[i], cloud.Y[i], cloud.Z[i]).Z;
        MeasureVector3 Point(int i) => new(cloud.X[i], cloud.Y[i], cloud.Z[i]);
        var regions = CloudRegionSelector.SelectGroups(cloud, p);
        if (mode != "All Points" && regions.Count < 4) { ctx.Log("3D平面度：平均点或最高点模式至少需要4个区域", true); return false; }
        var filtered = new List<List<int>>();
        foreach (var region in regions)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            var valid = region.Where(i => Point(i).Finite).ToList();
            if (filter == "Percentile" && valid.Count > 0)
            {
                valid.Sort((a, b) => Height(a).CompareTo(Height(b)));
                double Quantile(double fraction)
                {
                    var index = (valid.Count - 1) * fraction; var below = (int)Math.Floor(index); var above = (int)Math.Ceiling(index);
                    return Height(valid[below]) * (1 - index + below) + Height(valid[above]) * (index - below);
                }
                var minimumHeight = Quantile(low / 100); var maximumHeight = Quantile(1 - high / 100);
                valid = valid.Where(i => Height(i) >= minimumHeight && Height(i) <= maximumHeight).ToList();
            }
            if (valid.Count == 0) { ctx.Log($"3D平面度：第{filtered.Count + 1}个区域没有有效点", true); return false; }
            filtered.Add(valid);
        }
        var samples = mode == "All Points"
            ? filtered.SelectMany(g => g).Distinct().Select(Point).ToList()
            : filtered.Select(g => mode == "Highest Point" ? Point(g.MaxBy(Height)) : Mean(g.Select(Point))).ToList();
        if (samples.Count < 3) { ctx.Log("3D平面度：有效测量点少于3个", true); return false; }
        var center = Mean(samples);
        var normal = FitNormal(samples, center);
        if (normal == null) { ctx.Log("3D平面度：点集共线或退化，无法确定平面", true); return false; }
        var originLocal = toReference(0, 0, 0);
        var normalLocal = toReference(normal.Value.X, normal.Value.Y, normal.Value.Z);
        if (normalLocal.Z - originLocal.Z < 0) normal = normal.Value * -1;
        double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
        var distances = new List<object>();
        foreach (var sample in samples)
        {
            var distance = (sample - center).Dot(normal.Value);
            distances.Add(distance);
            minimum = Math.Min(minimum, distance); maximum = Math.Max(maximum, distance);
        }
        var value = maximum - minimum;
        if (!double.IsFinite(value)) throw new ArithmeticException("平面度计算产生非有限结果");
        ctx.Vars.Set("flatness_value", value);
        if (mode == "Single Average Point") ctx.Vars.Set("distances", distances);
        var output = p.Group("Output");
        if (output?.TryGetValue("Output Region Height", out var heightOutput) == true && heightOutput is true)
        {
            var heightMode = output.TryGetValue("Height Mode", out var hm) ? hm?.ToString() : "Mean";
            if (heightMode is not ("Minimum" or "Maximum" or "Mean" or "Median")) throw new ArgumentException("无效区域高度统计方式：" + heightMode);
            var heights = new List<object>();
            foreach (var group in filtered)
            {
                var z = group.Select(Height).OrderBy(h => h).ToArray();
                var h = heightMode switch { "Minimum" => z[0], "Maximum" => z[^1], "Median" => (z[(z.Length - 1) / 2] + z[z.Length / 2]) / 2, _ => z.Average() };
                heights.Add(h);
                var position = Mean(group.Select(Point));
                if (p.Bool("Height Text")) ctx.Texts.Add(new Text3D { X = position.X, Y = position.Y, Z = position.Z, Text = h.ToString("0.000", CultureInfo.InvariantCulture), Color = "yellow", Size = 14, ViewIndex = p.Int("IM In") });
                if (p.Bool("Point 3D")) ctx.NativeOverlays.Add(new() { Type = "point3d", ViewIndex = p.Int("IM In"), Values = position.Native() });
            }
            ctx.Vars.Set("region_height", heights);
        }
        ctx.Log($"3D平面度：{value:0.000000}，{samples.Count}个测量点，{filtered.Count}个区域", false);
        return true;
    }
    private static MeasureVector3 Mean(IEnumerable<MeasureVector3> points)
    {
        var count = 0; var mean = new MeasureVector3();
        foreach (var point in points) { count++; mean += (point - mean) * (1.0 / count); }
        return mean;
    }
    private static MeasureVector3? FitNormal(IReadOnlyList<MeasureVector3> points, MeasureVector3 center)
    {
        var a = new double[3, 3]; var vectors = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        foreach (var point in points)
        {
            var p = point - center; var coordinates = new[] { p.X, p.Y, p.Z };
            for (var i = 0; i < 3; i++) for (var j = 0; j < 3; j++) a[i, j] += coordinates[i] * coordinates[j];
        }
        for (var iteration = 0; iteration < 60; iteration++)
        {
            var p = 0; var q = 1;
            for (var i = 0; i < 3; i++) for (var j = i + 1; j < 3; j++) if (Math.Abs(a[i, j]) > Math.Abs(a[p, q])) { p = i; q = j; }
            var scale = Math.Max(Math.Abs(a[0, 0]), Math.Max(Math.Abs(a[1, 1]), Math.Abs(a[2, 2])));
            if (scale == 0 || Math.Abs(a[p, q]) <= scale * 1e-14) break;
            var angle = .5 * Math.Atan2(2 * a[p, q], a[q, q] - a[p, p]);
            var c = Math.Cos(angle); var s = Math.Sin(angle);
            var pp = a[p, p]; var qq = a[q, q]; var pq = a[p, q];
            a[p, p] = c * c * pp - 2 * s * c * pq + s * s * qq;
            a[q, q] = s * s * pp + 2 * s * c * pq + c * c * qq;
            a[p, q] = a[q, p] = 0;
            for (var k = 0; k < 3; k++)
            {
                if (k != p && k != q)
                {
                    var kp = a[k, p]; var kq = a[k, q];
                    a[k, p] = a[p, k] = c * kp - s * kq; a[k, q] = a[q, k] = s * kp + c * kq;
                }
                var vp = vectors[k, p]; var vq = vectors[k, q];
                vectors[k, p] = c * vp - s * vq; vectors[k, q] = s * vp + c * vq;
            }
        }
        var indices = Enumerable.Range(0, 3).OrderBy(i => a[i, i]).ToArray();
        if (!double.IsFinite(a[indices[2], indices[2]]) || a[indices[2], indices[2]] <= 0 || a[indices[1], indices[1]] <= a[indices[2], indices[2]] * 1e-12) return null;
        var n = indices[0]; return new MeasureVector3(vectors[0, n], vectors[1, n], vectors[2, n]).Unit();
    }
}
