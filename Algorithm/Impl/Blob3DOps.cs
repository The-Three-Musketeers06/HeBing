namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Euclidean connected components with original point membership retained.</summary>
public sealed class Blob3DOp : IOperator
{
    public string Driver => "3D Blob";

    public bool Run(OpContext ctx, ParamBag p)
    {
        int input = p.Int("IM In", 0);
        var cloud = ctx.Clouds.Get(input);
        if (cloud == null || cloud.Count == 0) { ctx.Log("3D 斑点：输入为空", true); return false; }
        p.Context ??= ctx;
        DetectionOverlay2D.Begin(ctx, Driver);
        var groups = CloudRegionSelector.SelectGroups(cloud, p);
        var outputs = new List<object>();
        var allBlobs = new List<object>();
        var allCenters = new List<object>();
        var pointsets = new List<PointSet3D>();
        foreach (var selected in groups)
        {
            var accepted = ClusterGroup(ctx, p, cloud, selected);
            var centers = new List<object>();
            var blobs = new List<object>();
            foreach (var cluster in accepted)
            {
                var center = new Dictionary<string, object> { ["X"] = cluster.X, ["Y"] = cluster.Y, ["Z"] = cluster.Z };
                centers.Add(center);
                blobs.Add(new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) {
                    ["Point Count"] = (double)cluster.Points.Count, ["_type"] = "pointset", ["IM"] = input,
                    ["Center X"] = cluster.X, ["Center Y"] = cluster.Y, ["Center Z"] = cluster.Z,
                    ["X"] = cluster.X, ["Y"] = cluster.Y, ["Z"] = cluster.Z,
                    ["Center"] = center, ["Index"] = pointsets.Count, ["Pointset"] = cluster.Points
                });
                pointsets.Add(cluster.Points);
                if (p.Bool("Point 3D")) DetectionOverlay2D.Add(ctx, p, Driver, "point3d", center, view: input);
                if (p.Bool("Text 3D")) ctx.Texts.Add(new() { X = cluster.X, Y = cluster.Y, Z = cluster.Z,
                    Text = pointsets.Count.ToString(), Color = "green", Size = 14, ViewIndex = input,
                    Owner = string.IsNullOrEmpty(ctx.OperationUuid) ? Driver : ctx.OperationUuid });
            }
            outputs.Add(new Dictionary<string, object> {
                ["blob_num"] = (double)blobs.Count, ["blobs"] = blobs, ["blob_center_points"] = centers
            });
            allBlobs.AddRange(blobs); allCenters.AddRange(centers);
        }
        ctx.Clouds.SetPointSets(input, pointsets);
        // ROI arrays have one result per input region, including empty regions. Scripts
        // depend on that correspondence; flattening first changes both count and order.
        ctx.Vars.Set("blobs_outputs", outputs);
        ctx.Vars.Set("blob_num", (double)allBlobs.Count);
        ctx.Vars.Set("blob_center_points", allCenters);
        ctx.Vars.Set("blobs", allBlobs);
        if (p.Bool("Pointset")) cloud.DisplayIndices = pointsets.SelectMany(s => s.Indices).Distinct().ToArray();
        else cloud.DisplayIndices = p.Bool("Pointset Cut") && p.Bool("Use Region")
            ? groups.SelectMany(g => g).Distinct().ToArray() : null;
        ctx.Log($"3D 斑点：{groups.Count} 个区域 -> {allBlobs.Count} 个斑点，保留分区域输出和簇点集", false);
        return true;
    }

    private static List<Cluster> ClusterGroup(OpContext ctx, ParamBag p, PointCloud cloud, List<int> selected)
    {
        if (!p.Str("Algorithm", "Euclidean cluster").Equals("Euclidean cluster", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("3D 斑点：Simple blob 需要有序点云拓扑，尚未实现");
        double tolerance = p.GroupDbl("Euclidean cluster", "Cluster tolerance", 0.5);
        int minimum = Integer(p.GroupDbl("Euclidean cluster", "Min cluster size", 1), "Min cluster size");
        int maximum = Integer(p.GroupDbl("Euclidean cluster", "Max cluster size", 100000000), "Max cluster size");
        if (!double.IsFinite(tolerance) || tolerance <= 0 || maximum < minimum)
            throw new InvalidOperationException("3D 斑点：聚类距离/尺寸范围无效");
        var grid = new Dictionary<(long X, long Y, long Z), List<int>>();
        (long X, long Y, long Z) Key(int index)
        {
            var x = Math.Floor(cloud.X[index] / tolerance); var y = Math.Floor(cloud.Y[index] / tolerance); var z = Math.Floor(cloud.Z[index] / tolerance);
            if (!double.IsFinite(x + y + z) || Math.Abs(x) > long.MaxValue - 2d || Math.Abs(y) > long.MaxValue - 2d || Math.Abs(z) > long.MaxValue - 2d)
                throw new InvalidOperationException("3D 斑点：坐标或聚类距离超出范围");
            return ((long)x, (long)y, (long)z);
        }
        foreach (var index in selected)
        {
            var key = Key(index);
            if (!grid.TryGetValue(key, out var cell)) grid[key] = cell = new List<int>();
            cell.Add(index);
        }
        var visited = new bool[cloud.Count];
        var accepted = new List<Cluster>();
        double squaredTolerance = tolerance * tolerance;
        foreach (var seed in selected)
        {
            if (visited[seed]) continue;
            ctx.CancellationToken.ThrowIfCancellationRequested();
            var queue = new Queue<int>();
            var members = new List<int>();
            visited[seed] = true; queue.Enqueue(seed);
            while (queue.TryDequeue(out var current))
            {
                if ((members.Count & 4095) == 0) ctx.CancellationToken.ThrowIfCancellationRequested();
                members.Add(current);
                var key = Key(current);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (!grid.TryGetValue((key.X + dx, key.Y + dy, key.Z + dz), out var neighbors)) continue;
                            foreach (var neighbor in neighbors)
                            {
                                if (visited[neighbor]) continue;
                                double x = cloud.X[current] - cloud.X[neighbor];
                                double y = cloud.Y[current] - cloud.Y[neighbor];
                                double z = cloud.Z[current] - cloud.Z[neighbor];
                                if (x * x + y * y + z * z > squaredTolerance) continue;
                                visited[neighbor] = true; queue.Enqueue(neighbor);
                            }
                        }
            }
            if (members.Count >= minimum && members.Count <= maximum) accepted.Add(new Cluster(cloud, members));
        }
        var settings = p.Group("Output settings");
        var sort = settings != null && settings.TryGetValue("Sort mode", out var sortValue) ? sortValue?.ToString()?.ToLowerInvariant() : "no sorting";
        Func<Cluster, double> sortKey = sort switch {
            "point count" => cluster => cluster.Points.Count, "x" => cluster => cluster.X,
            "y" => cluster => cluster.Y, "z" => cluster => cluster.Z,
            "no sorting" or "" or null => null,
            _ => throw new NotSupportedException($"3D 斑点排序模式尚未实现：{sort}")
        };
        if (sortKey != null)
        {
            var descending = settings.TryGetValue("Sort direction1", out var direction) &&
                string.Equals(direction?.ToString(), "descending", StringComparison.OrdinalIgnoreCase);
            accepted = (descending ? accepted.OrderByDescending(sortKey) : accepted.OrderBy(sortKey)).ToList();
        }
        var maxOutput = Integer(p.GroupDbl("Output settings", "Max number of objects to output", 5), "Max number of objects to output");
        if (maxOutput > 0) accepted = accepted.Take(maxOutput).ToList();

        return accepted;
    }

    private static int Integer(double value, string name)
        => double.IsFinite(value) && value >= 0 && value <= int.MaxValue && value == Math.Truncate(value) ? (int)value
            : throw new InvalidOperationException($"3D 斑点：{name} 必须为非负整数");

    private sealed class Cluster
    {
        public PointSet3D Points { get; }
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public Cluster(PointCloud source, List<int> members)
        {
            Points = new PointSet3D(source, members);
            X = members.Average(index => (double)source.X[index]);
            Y = members.Average(index => (double)source.Y[index]);
            Z = members.Average(index => (double)source.Z[index]);
        }
    }
}
