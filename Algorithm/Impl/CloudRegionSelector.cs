namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Exact membership for supported 3D regions; array order is preserved.</summary>
public static class CloudRegionSelector
{
    public static List<int> Select(PointCloud cloud, ParamBag p)
        => SelectGroups(cloud, p).SelectMany(group => group).Distinct().OrderBy(index => index).ToList();

    public static List<List<int>> SelectGroups(PointCloud cloud, ParamBag p)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        if (!p.Bool("Use Region", false)) return new() { Enumerable.Range(0, cloud.Count).ToList() };
        return SelectGroups(cloud, p.Raw("Region"), p.Context, p.Group("Reference"));
    }

    public static List<List<int>> SelectGroups(PointCloud cloud, object region, OpContext context,
        IReadOnlyDictionary<string, object> reference = null)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        if (region == null) throw new InvalidOperationException("已启用的区域没有有效输入");
        if (region is PointSet3D pointSet) return new() { pointSet.InCloud(cloud) };
        if (region is IEnumerable<object> array && region is not IReadOnlyDictionary<string, object>)
        {
            var groups = new List<List<int>>();
            foreach (var item in array) groups.AddRange(SelectGroups(cloud, item, context, reference));
            return groups;
        }
        if (region is not IReadOnlyDictionary<string, object> map)
            throw new NotSupportedException($"不支持的区域对象：{region.GetType().Name}");
        if (map.TryGetValue("Pointset", out var selection) && selection is PointSet3D captured)
            return new() { captured.InCloud(cloud) };
        if (map.TryGetValue("Region", out var nested))
            return SelectGroups(cloud, nested, context, C3.Sub(map, "Reference") ?? reference);
        var type = map.TryGetValue("Type", out var kind) ? kind?.ToString()?.ToLowerInvariant() : null;
        if (string.IsNullOrEmpty(type) && map.ContainsKey("Start") && map.ContainsKey("End"))
            type = C3.Sub(map, "Start")?.ContainsKey("Z") == true ? "box" : "window2d";
        if (type != null && type.EndsWith(" array", StringComparison.Ordinal))
        {
            if (!map.TryGetValue(type, out var entries) || entries is not IEnumerable<object> items)
                throw new InvalidOperationException($"区域数组不存在：{type}");
            var groups = new List<List<int>>();
            var elementType = type[..^6];
            foreach (var item in items)
                groups.AddRange(SelectGroups(cloud, new Dictionary<string, object> { ["Type"] = elementType, [elementType] = item }, context, reference));
            return groups;
        }
        var body = type != null && C3.Sub(map, type) is { } sub ? sub : map;
        if (type == "pointset")
        {
            if (map.TryGetValue("pointset", out var raw) && raw is PointSet3D direct) return new() { direct.InCloud(cloud) };
            var im = Integer(body, "IM"); var index = Integer(body, "Index");
            var set = context?.Clouds.GetPointSet(im, index)
                ?? throw new InvalidOperationException($"点集不存在：IM{im}[{index}]");
            return new() { set.InCloud(cloud) };
        }

        Func<double, double, double, bool> contains;
        switch (type)
        {
            case "box":
            case "window2d":
            {
                var start = RequiredMap(body, "Start"); var end = RequiredMap(body, "End");
                var x1 = Number(start, "X"); var x2 = Number(end, "X");
                var y1 = Number(start, "Y"); var y2 = Number(end, "Y");
                var z1 = type == "box" ? Number(start, "Z") : double.NegativeInfinity;
                var z2 = type == "box" ? Number(end, "Z") : double.PositiveInfinity;
                contains = (x, y, z) => Between(x, x1, x2) && Between(y, y1, y2) && Between(z, z1, z2);
                break;
            }
            case "circlewindow2d":
            case "cylinderbox":
            {
                var center = RequiredMap(body, type == "cylinderbox" ? "Bottom" : "Center");
                var cx = Number(center, "X"); var cy = Number(center, "Y");
                var cz = type == "cylinderbox" ? Number(center, "Z") : 0;
                var radius = Positive(body, "Radius");
                var height = type == "cylinderbox" ? Positive(body, "Height") : 0;
                contains = (x, y, z) => (x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius
                    && (type != "cylinderbox" || Between(z, cz, cz + height));
                break;
            }
            case "orientedbox":
            {
                var center = RequiredMap(body, "Center"); var size = RequiredMap(body, "Size");
                var rotation = RequiredMap(body, "Rotation");
                var localReference = new Dictionary<string, object> {
                    ["X"] = Number(center, "X"), ["Y"] = Number(center, "Y"), ["Z"] = Number(center, "Z"),
                    ["X Angle"] = Number(rotation, "X"), ["Y Angle"] = Number(rotation, "Y"), ["Z Angle"] = Number(rotation, "Z")
                };
                var halfX = Positive(size, "X") / 2; var halfY = Positive(size, "Y") / 2; var halfZ = Positive(size, "Z") / 2;
                var localTransform = ReferenceTransform(localReference);
                contains = (x, y, z) => {
                    var local = localTransform(x, y, z);
                    return Math.Abs(local.X) <= halfX + 1e-9 && Math.Abs(local.Y) <= halfY + 1e-9 && Math.Abs(local.Z) <= halfZ + 1e-9;
                };
                break;
            }
            case "polygon2d":
            {
                if (!body.TryGetValue("Points", out var points) || points is not IEnumerable<object> values)
                    throw new InvalidOperationException("多边形区域缺少点数组");
                var vertices = values.Select(value => value is IReadOnlyDictionary<string, object> vertex
                    ? (X: Number(vertex, "X"), Y: Number(vertex, "Y")) : throw new InvalidOperationException("多边形顶点无效")).ToArray();
                if (vertices.Length < 3) throw new InvalidOperationException("多边形区域至少需要3个顶点");
                contains = (x, y, z) => InPolygon(x, y, vertices);
                break;
            }
            default: throw new NotSupportedException($"尚不支持的3D区域类型：{type ?? "未指定"}");
        }
        var selected = new List<int>();
        var transform = ReferenceTransform(reference);
        for (int i = 0; i < cloud.Count; i++)
        {
            if ((i & 4095) == 0) context?.CancellationToken.ThrowIfCancellationRequested();
            var point = transform(cloud.X[i], cloud.Y[i], cloud.Z[i]);
            if (contains(point.X, point.Y, point.Z)) selected.Add(i);
        }
        return new() { selected };
    }

    public static (double X, double Y, double Z) ToReference(double x, double y, double z, IReadOnlyDictionary<string, object> reference)
        => ReferenceTransform(reference)(x, y, z);

    public static Func<double, double, double, (double X, double Y, double Z)> CreateReferenceTransform(IReadOnlyDictionary<string, object> reference)
        => ReferenceTransform(reference);

    public static List<object> SplitRegions(object region)
    {
        if (region is IEnumerable<object> list && region is not IReadOnlyDictionary<string, object>)
            return list.SelectMany(SplitRegions).ToList();
        if (region is IReadOnlyDictionary<string, object> map && map.TryGetValue("Type", out var rawType))
        {
            var type = rawType?.ToString()?.ToLowerInvariant();
            if (type != null && type.EndsWith(" array", StringComparison.Ordinal))
            {
                if (!map.TryGetValue(type, out var rawItems) || rawItems is not IEnumerable<object> items)
                    throw new InvalidOperationException($"区域数组不存在：{type}");
                return items.Select(item => (object)new Dictionary<string, object> { ["Type"] = type[..^6], [type[..^6]] = item }).ToList();
            }
        }
        return region != null ? new() { region } : throw new InvalidOperationException("区域没有有效输入");
    }

    private static Func<double, double, double, (double X, double Y, double Z)> ReferenceTransform(IReadOnlyDictionary<string, object> reference)
    {
        if (reference == null) return (x, y, z) => (x, y, z);
        var tx = C3.N(reference, "X", 0); var ty = C3.N(reference, "Y", 0); var tz = C3.N(reference, "Z", 0);
        var rx = C3.N(reference, "X Angle", 0) * Math.PI / 180;
        var ry = C3.N(reference, "Y Angle", 0) * Math.PI / 180;
        var rz = C3.N(reference, "Z Angle", 0) * Math.PI / 180;
        if (new[] { tx, ty, tz, rx, ry, rz }.Any(value => !double.IsFinite(value))) throw new InvalidOperationException("区域参考坐标必须为有限数值");
        var cx = Math.Cos(rx); var sx = Math.Sin(rx); var cy = Math.Cos(ry); var sy = Math.Sin(ry); var cz = Math.Cos(rz); var sz = Math.Sin(rz);
        return (x, y, z) => {
            x -= tx; y -= ty; z -= tz;
            var x1 = cz * x + sz * y; var y1 = -sz * x + cz * y;
            var x2 = cy * x1 - sy * z; var z2 = sy * x1 + cy * z;
            return (x2, cx * y1 + sx * z2, -sx * y1 + cx * z2);
        };
    }

    private static bool Between(double value, double a, double b) => value >= Math.Min(a, b) && value <= Math.Max(a, b);
    private static IReadOnlyDictionary<string, object> RequiredMap(IReadOnlyDictionary<string, object> map, string key)
        => C3.Sub(map, key) ?? throw new InvalidOperationException($"区域缺少参数：{key}");
    private static double Number(IReadOnlyDictionary<string, object> map, string key)
    {
        if (!map.TryGetValue(key, out var raw) || !double.TryParse(raw?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new InvalidOperationException($"区域数值无效：{key}");
        return value;
    }
    private static double Positive(IReadOnlyDictionary<string, object> map, string key)
    {
        var value = Number(map, key);
        return value > 0 ? value : throw new InvalidOperationException($"区域尺寸必须为正：{key}");
    }
    private static int Integer(IReadOnlyDictionary<string, object> map, string key)
    {
        var value = Number(map, key);
        return value >= 0 && value <= int.MaxValue && Math.Truncate(value) == value ? (int)value
            : throw new InvalidOperationException($"点集索引无效：{key}");
    }
    private static bool InPolygon(double x, double y, (double X, double Y)[] vertices)
    {
        bool inside = false;
        for (int i = 0, j = vertices.Length - 1; i < vertices.Length; j = i++)
        {
            var a = vertices[j]; var b = vertices[i];
            var cross = (x - a.X) * (b.Y - a.Y) - (y - a.Y) * (b.X - a.X);
            if (Math.Abs(cross) < 1e-9 && Between(x, a.X, b.X) && Between(y, a.Y, b.Y)) return true;
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}
