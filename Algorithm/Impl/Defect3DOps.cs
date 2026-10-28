using System.Diagnostics;
using System.Globalization;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>
/// Surface defects measured against a robust local quadratic background in Reference XY.
/// This is an independent geometric implementation, not the undisclosed vendor kernel.
/// Evidence/limitations: shots/defect3d-doc-evidence.json and defect3d-verification.txt.
/// </summary>
public sealed class Defect3DOp : IOperator
{
    public string Driver => "3D Defect";
    private readonly record struct Sample(int Original, double X, double Y, double Z);
    private sealed class Tile
    {
        public readonly List<int> Indices = new();
        public Sample Representative;
        public double CenterX, CenterY;
    }
    private sealed class Defect
    {
        public List<int> Indices;
        public double Area, Width, Height, Average, X, Y;
        public double X1, Y1, Z1, X2, Y2, Z2;
    }

    public bool Run(OpContext ctx, ParamBag p)
    {
        var watch = Stopwatch.StartNew();
        ctx.CancellationToken.ThrowIfCancellationRequested();
        p.Context ??= ctx;
        var im = p.Int("IM In");
        var cloud = ctx.Clouds.Get(im) ?? throw new ArgumentException($"输入点云 IM{im} 不存在");
        var length = Positive(p, "Length of Defects", .5);
        var radius = Positive(p, "Radius of ROI Traversal", .5);
        var threshold = NonNegative(p.Dbl("Height Threshold", .5), "Height Threshold");
        var maximum = p.Int("Maximum Number of Defects", 10);
        if (maximum < 1 || maximum > 1000) throw new ArgumentException("缺陷个数上限必须在 1..1000 范围内");
        var type = p.Str("Type of Defect", "concave and convex");
        if (type is not ("concave and convex" or "concave" or "convex")) throw new ArgumentException("无效的缺陷类型：" + type);
        var filter = p.Group("Filter Options");
        var area = Range(filter, "Area"); var height = Range(filter, "Height");
        var average = Range(filter, "Average Height"); var width = Range(filter, "Box Width");
        // No semantics for these v8 additions appear in the shipped help. Never silently ignore them.
        foreach (var axis in new[] { "X", "Y", "R" })
            if (NonNegative(C3.N(filter, "Margin " + axis, 0), "Margin " + axis) != 0)
                throw new NotSupportedException("3D Defect 暂未实现非零 Margin " + axis + "；原版帮助未披露其计算规则");

        var reference = p.Group("Reference");
        var selected = CloudRegionSelector.Select(cloud, p);
        var points = new List<Sample>(selected.Count);
        foreach (var index in selected)
        {
            if ((points.Count & 4095) == 0) ctx.CancellationToken.ThrowIfCancellationRequested();
            var local = CloudRegionSelector.ToReference(cloud.X[index], cloud.Y[index], cloud.Z[index], reference);
            if (double.IsFinite(local.X) && double.IsFinite(local.Y) && double.IsFinite(local.Z))
                points.Add(new(index, local.X, local.Y, local.Z));
        }
        cloud.DisplayScalars = null; cloud.DisplayMin = cloud.DisplayMax = null; cloud.DisplayScalarName = "Z";
        cloud.DisplayIndices = null;
        ctx.Clouds.SetPointSets(im, Array.Empty<PointSet3D>());
        Publish(ctx, Array.Empty<Defect>());
        if (points.Count < 6) return Failure(ctx, watch, "区域裁切失败或有效点不足，局部曲面至少需要6个点");

        var spacing = EstimateSpacing(points, ctx);
        if (!(spacing > 0)) return Failure(ctx, watch, "点云在参考 XY 平面上退化，无法建立表面背景");
        var cell = Math.Max(spacing * 2, Math.Min(length * .5, radius * .25));
        var originX = points.Min(point => point.X); var originY = points.Min(point => point.Y);
        var tiles = new Dictionary<(long X, long Y), Tile>();
        foreach (var (point, index) in points.Select((value, index) => (value, index)))
        {
            var key = Key(point.X, point.Y, originX, originY, cell);
            if (!tiles.TryGetValue(key, out var tile))
                tiles[key] = tile = new() { CenterX = originX + (key.X + .5) * cell, CenterY = originY + (key.Y + .5) * cell };
            tile.Indices.Add(index);
        }
        foreach (var tile in tiles.Values)
        {
            tile.Indices.Sort((a, b) => points[a].Z.CompareTo(points[b].Z));
            // Use the actual median point's XY, not the bin center; this preserves inclined planes.
            tile.Representative = points[tile.Indices[tile.Indices.Count / 2]];
        }
        var residuals = Enumerable.Repeat(double.NaN, points.Count).ToArray();
        var search = Math.Max(1L, (long)Math.Ceiling(radius / cell));
        var inner2 = length * length * .25; var outer2 = radius * radius;
        int unsupported = 0;
        foreach (var (key, tile) in tiles)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            var neighbors = new List<Sample>();
            // Sparse clouds can have huge coordinate gaps; bound traversal by occupied tiles.
            IEnumerable<Tile> candidates;
            if (search > 64 || search * search * 4 > tiles.Count)
                candidates = tiles.Values;
            else
            {
                var local = new List<Tile>();
                for (long dx = -search; dx <= search; dx++)
                    for (long dy = -search; dy <= search; dy++)
                        if (tiles.TryGetValue((key.X + dx, key.Y + dy), out var candidate)) local.Add(candidate);
                candidates = local;
            }
            foreach (var candidate in candidates)
            {
                var point = candidate.Representative;
                var dx = point.X - tile.CenterX; var dy = point.Y - tile.CenterY; var distance2 = dx * dx + dy * dy;
                if (distance2 <= outer2 && distance2 >= inner2) neighbors.Add(point);
            }
            // Uniform deterministic subsampling bounds work without depending on the sample's content.
            if (neighbors.Count > 256)
                neighbors = Enumerable.Range(0, 256).Select(i => neighbors[i * (neighbors.Count - 1) / 255]).ToList();
            var coefficients = RobustFit(neighbors, tile.CenterX, tile.CenterY, radius);
            if (coefficients == null) { unsupported += tile.Indices.Count; continue; }
            foreach (var index in tile.Indices)
            {
                var point = points[index];
                residuals[index] = point.Z - Evaluate(coefficients, (point.X - tile.CenterX) / radius, (point.Y - tile.CenterY) / radius);
            }
        }
        if (unsupported == points.Count) return Failure(ctx, watch, "ROI遍历半径内缺少非共线背景点；请增大半径或缩小缺陷长度");
        var defects = Components(points, residuals, threshold, type, spacing * 1.8, ctx)
            .Select(indices => Measure(indices, points, cloud))
            .Where(d => InRange(d.Area, area) && InRange(d.Height, height)
                && InRange(d.Average, average) && InRange(d.Width, width))
            .OrderByDescending(d => d.Area).ThenByDescending(d => d.Height).ThenBy(d => d.X).ThenBy(d => d.Y)
            .Take(maximum).ToArray();
        Publish(ctx, defects);
        var pointsets = defects.Select(d => new PointSet3D(cloud, d.Indices.Select(i => points[i].Original))).ToArray();
        ctx.Clouds.SetPointSets(im, pointsets);
        if (p.Bool("Pointset")) cloud.DisplayIndices = pointsets.SelectMany(set => set.Indices).Distinct().ToArray();
        else if (p.Bool("Pointset Cut") && p.Bool("Use Region")) cloud.DisplayIndices = points.Select(point => point.Original).ToArray();
        if (p.Bool("Preprocessed Pointcloud"))
        {
            var scalars = Enumerable.Repeat(float.NaN, cloud.Count).ToArray();
            for (int i = 0; i < points.Count; i++) scalars[points[i].Original] = (float)residuals[i];
            cloud.DisplayScalars = scalars; cloud.DisplayScalarName = "Height Difference";
            var bound = residuals.Where(double.IsFinite).Select(Math.Abs).DefaultIfEmpty(0).Max();
            cloud.DisplayMin = -Math.Max(bound, 1e-6); cloud.DisplayMax = Math.Max(bound, 1e-6);
        }
        for (int n = 0; n < defects.Length; n++)
        {
            var indices = defects[n].Indices.Select(i => points[i].Original).ToArray();
            var x1 = indices.Min(i => cloud.X[i]); var x2 = indices.Max(i => cloud.X[i]);
            var y1 = indices.Min(i => cloud.Y[i]); var y2 = indices.Max(i => cloud.Y[i]);
            var z1 = indices.Min(i => cloud.Z[i]); var z2 = indices.Max(i => cloud.Z[i]);
            var label = (n + 1).ToString(CultureInfo.InvariantCulture);
            if (p.Bool("Box 3D")) ctx.Boxes.Add(new Box3 { X1 = x1, X2 = x2, Y1 = y1, Y2 = y2, Z1 = z1, Z2 = z2,
                Color = "yellow", ViewIndex = im, Label = p.Bool("Text 3D") ? label : "", FillOpacity = .12 });
            else if (p.Bool("Text 3D")) ctx.Texts.Add(new Text3D { X = (x1 + x2) * .5, Y = (y1 + y2) * .5, Z = z2,
                Text = label, Color = "yellow", Size = 16, ViewIndex = im });
        }
        if (defects.Length == 0) return Failure(ctx, watch, "未能找到满足类型、高度阈值和筛选条件的缺陷");
        ctx.Vars.Set("Result", true); ctx.Vars.Set("Warning", unsupported > 0);
        ctx.Vars.Set("Message", unsupported > 0 ? $"{unsupported} 个点缺少足够的局部背景，未参与缺陷测量" : "");
        ctx.Vars.Set("Time", watch.Elapsed.TotalMilliseconds);
        if (unsupported > 0) ctx.Log(ctx.Vars.Get("Message").ToString(), false);
        return true;
    }

    private static bool Failure(OpContext ctx, Stopwatch watch, string message)
    {
        ctx.Vars.Set("Result", false); ctx.Vars.Set("Warning", false); ctx.Vars.Set("Message", message);
        ctx.Vars.Set("Time", watch.Elapsed.TotalMilliseconds); ctx.Log(message, true); return false;
    }
    private static void Publish(OpContext ctx, IReadOnlyList<Defect> defects)
    {
        ctx.Vars.Set("defect_num", defects.Count);
        ctx.Vars.Set("defect_id", Enumerable.Range(1, defects.Count).ToArray());
        ctx.Vars.Set("defect_area", defects.Select(d => d.Area).ToArray());
        ctx.Vars.Set("defect_bbox_max_width", defects.Select(d => d.Width).ToArray());
        ctx.Vars.Set("defect_centroid_x", defects.Select(d => d.X).ToArray());
        ctx.Vars.Set("defect_centroid_y", defects.Select(d => d.Y).ToArray());
        ctx.Vars.Set("defect_height", defects.Select(d => d.Height).ToArray());
        ctx.Vars.Set("defect_height_avg", defects.Select(d => d.Average).ToArray());
        ctx.Vars.Set("defect_aabb", defects.Select(d => new Dictionary<string, object> {
            ["_type"] = "box", ["_im"] = -1, ["_color"] = "",
            ["Start"] = NativePoint(d.X1, d.Y1, d.Z1), ["End"] = NativePoint(d.X2, d.Y2, d.Z2)
        }).ToArray());
    }
    private static Dictionary<string, object> NativePoint(double x, double y, double z) => new() {
        ["X"] = x, ["Y"] = y, ["Z"] = z, ["_type"] = "point3d", ["_im"] = -1, ["_color"] = "", ["_size"] = -1, ["_thickness"] = -1
    };
    private static Defect Measure(List<int> indices, List<Sample> points, PointCloud cloud)
    {
        var source = indices.Select(i => points[i].Original).ToArray();
        var x1 = source.Min(i => (double)cloud.X[i]); var x2 = source.Max(i => (double)cloud.X[i]);
        var y1 = source.Min(i => (double)cloud.Y[i]); var y2 = source.Max(i => (double)cloud.Y[i]);
        var z1 = source.Min(i => (double)cloud.Z[i]); var z2 = source.Max(i => (double)cloud.Z[i]);
        // Native runtime evidence (all 8 sample defects): height = End.Z - Start.Z,
        // average height = mean(source Z) - Start.Z, including concave components.
        // Background residual is only used to detect membership, not to replace source geometry.
        return new() { Indices = indices, Area = (x2 - x1) * (y2 - y1), Width = Math.Max(x2 - x1, y2 - y1),
            Height = z2 - z1, Average = source.Average(i => (double)cloud.Z[i]) - z1,
            X = source.Average(i => (double)cloud.X[i]), Y = source.Average(i => (double)cloud.Y[i]),
            X1 = x1, Y1 = y1, Z1 = z1, X2 = x2, Y2 = y2, Z2 = z2 };
    }
    private static List<List<int>> Components(List<Sample> points, double[] residuals, double threshold, string type, double radius, OpContext ctx)
    {
        var grid = new Dictionary<(long X, long Y), List<int>>();
        var candidates = new List<int>();
        for (int i = 0; i < points.Count; i++)
        {
            var h = residuals[i];
            if (!double.IsFinite(h) || Math.Abs(h) <= Math.Max(threshold, 1e-8)
                || type == "concave" && h >= 0 || type == "convex" && h <= 0) continue;
            candidates.Add(i); var key = Key(points[i].X, points[i].Y, 0, 0, radius);
            if (!grid.TryGetValue(key, out var bucket)) grid[key] = bucket = new();
            bucket.Add(i);
        }
        var visited = new bool[points.Count]; var groups = new List<List<int>>();
        foreach (var seed in candidates)
        {
            if (visited[seed]) continue;
            var group = new List<int>(); var queue = new Queue<int>(); queue.Enqueue(seed); visited[seed] = true;
            while (queue.Count > 0)
            {
                if ((group.Count & 1023) == 0) ctx.CancellationToken.ThrowIfCancellationRequested();
                var index = queue.Dequeue(); group.Add(index); var point = points[index];
                var key = Key(point.X, point.Y, 0, 0, radius);
                for (long dx = -1; dx <= 1; dx++)
                    for (long dy = -1; dy <= 1; dy++)
                    {
                        if (!grid.TryGetValue((key.X + dx, key.Y + dy), out var bucket)) continue;
                        foreach (var next in bucket)
                        {
                            if (visited[next] || Math.Sign(residuals[next]) != Math.Sign(residuals[index])) continue;
                            var xx = points[next].X - point.X; var yy = points[next].Y - point.Y;
                            if (xx * xx + yy * yy <= radius * radius) { visited[next] = true; queue.Enqueue(next); }
                        }
                    }
            }
            groups.Add(group);
        }
        return groups;
    }
    private static double EstimateSpacing(List<Sample> points, OpContext ctx)
    {
        var dx = points.Max(p => p.X) - points.Min(p => p.X); var dy = points.Max(p => p.Y) - points.Min(p => p.Y);
        if (dx <= 0 || dy <= 0) return 0;
        var cell = Math.Sqrt(dx * dy / points.Count) * 2;
        if (!(cell > 0) || !double.IsFinite(cell)) return 0;
        var originX = points.Min(p => p.X); var originY = points.Min(p => p.Y);
        var grid = new Dictionary<(long X, long Y), List<int>>();
        for (int i = 0; i < points.Count; i++)
        {
            var key = Key(points[i].X, points[i].Y, originX, originY, cell);
            if (!grid.TryGetValue(key, out var bucket)) grid[key] = bucket = new();
            bucket.Add(i);
        }
        var distances = new List<double>(); var step = Math.Max(1, points.Count / 512);
        for (int i = 0; i < points.Count; i += step)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            var point = points[i]; var key = Key(point.X, point.Y, originX, originY, cell); var best = double.PositiveInfinity;
            for (long xx = -2; xx <= 2; xx++)
                for (long yy = -2; yy <= 2; yy++)
                    if (grid.TryGetValue((key.X + xx, key.Y + yy), out var bucket))
                        foreach (var j in bucket)
                        {
                            var x = point.X - points[j].X; var y = point.Y - points[j].Y; var distance = x * x + y * y;
                            if (distance > cell * cell * 1e-12 && distance < best) best = distance;
                        }
            if (double.IsFinite(best)) distances.Add(Math.Sqrt(best));
        }
        return distances.Count > 0 ? Median(distances.ToArray()) : 0;
    }

    private static double[] RobustFit(List<Sample> points, double cx, double cy, double scale)
    {
        if (points.Count < 6) return null;
        int dimension = points.Count >= 12 ? 6 : 3;
        var rows = points.Select(p => Basis((p.X - cx) / scale, (p.Y - cy) / scale)).ToArray();
        var weights = Enumerable.Repeat(1.0, points.Count).ToArray();
        var zOrigin = Median(points.Select(p => p.Z).ToArray());
        double[] coefficients = null;
        for (int iteration = 0; iteration < 8; iteration++)
        {
            var matrix = new double[dimension, dimension]; var rhs = new double[dimension];
            for (int i = 0; i < points.Count; i++)
                for (int j = 0; j < dimension; j++)
                {
                    var a = rows[i][j] * weights[i]; rhs[j] += a * (points[i].Z - zOrigin);
                    for (int k = 0; k < dimension; k++) matrix[j, k] += a * rows[i][k];
                }
            var fitted = Solve(matrix, rhs);
            if (fitted == null)
            {
                if (dimension == 6) { dimension = 3; iteration--; Array.Fill(weights, 1.0); continue; }
                break;
            }
            coefficients = fitted;
            var residuals = points.Select((p, i) => p.Z - zOrigin - Dot(rows[i], fitted)).ToArray();
            var median = Median(residuals.ToArray());
            var sigma = 1.4826 * Median(residuals.Select(r => Math.Abs(r - median)).ToArray());
            var cutoff = Math.Max(4.685 * sigma, 1e-9 * Math.Max(1, Math.Abs(zOrigin)));
            for (int i = 0; i < weights.Length; i++)
            {
                var ratio = Math.Abs(residuals[i] - median) / cutoff;
                weights[i] = ratio >= 1 ? 0 : Math.Pow(1 - ratio * ratio, 2);
            }
        }
        if (coefficients != null) coefficients[0] += zOrigin;
        return coefficients;
    }
    private static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length; double maximum = 0;
        for (int i = 0; i < n; i++) maximum = Math.Max(maximum, Math.Abs(a[i, i]));
        for (int pivot = 0; pivot < n; pivot++)
        {
            int row = pivot;
            for (int i = pivot + 1; i < n; i++) if (Math.Abs(a[i, pivot]) > Math.Abs(a[row, pivot])) row = i;
            if (Math.Abs(a[row, pivot]) < maximum * 1e-11) return null;
            if (row != pivot)
            {
                for (int j = pivot; j < n; j++) (a[pivot, j], a[row, j]) = (a[row, j], a[pivot, j]);
                (b[pivot], b[row]) = (b[row], b[pivot]);
            }
            var diagonal = a[pivot, pivot];
            for (int j = pivot; j < n; j++) a[pivot, j] /= diagonal;
            b[pivot] /= diagonal;
            for (int i = 0; i < n; i++)
            {
                if (i == pivot) continue;
                var factor = a[i, pivot];
                for (int j = pivot; j < n; j++) a[i, j] -= factor * a[pivot, j];
                b[i] -= factor * b[pivot];
            }
        }
        return b.All(double.IsFinite) ? b : null;
    }
    private static double[] Basis(double x, double y) => new[] { 1.0, x, y, x * x, x * y, y * y };
    private static double Evaluate(double[] a, double x, double y) => Dot(a, Basis(x, y));
    private static double Dot(double[] a, double[] b) { double sum = 0; for (int i = 0; i < Math.Min(a.Length, b.Length); i++) sum += a[i] * b[i]; return sum; }
    private static double Median(double[] values) { Array.Sort(values); return values[values.Length / 2]; }
    private static (long X, long Y) Key(double x, double y, double ox, double oy, double cell)
        => (checked((long)Math.Floor((x - ox) / cell)), checked((long)Math.Floor((y - oy) / cell)));
    private static double Positive(ParamBag p, string name, double fallback)
    { var v = NonNegative(p.Dbl(name, fallback), name); return v > 0 ? v : throw new ArgumentException(name + " 必须大于0"); }
    private static double NonNegative(double value, string name)
        => double.IsFinite(value) && value >= 0 ? value : throw new ArgumentException(name + " 必须为非负有限值");
    private static (double Min, double Max) Range(IReadOnlyDictionary<string, object> p, string kind)
    {
        var min = NonNegative(C3.N(p, "Minimum " + kind, 0), "Minimum " + kind);
        var max = NonNegative(C3.N(p, "Maximum " + kind, 1e7), "Maximum " + kind);
        return min < max ? (min, max) : throw new ArgumentException(kind + " 筛选下限必须小于上限");
    }
    private static bool InRange(double value, (double Min, double Max) range) => value >= range.Min && value <= range.Max;
}
