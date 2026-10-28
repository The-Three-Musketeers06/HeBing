using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Owned XY rasterization shared by ordered clouds and depth images.</summary>
internal static class CloudRasterizer
{
    public static double Spacing(IEnumerable<double> coordinates)
    {
        var values = coordinates.Where(double.IsFinite).Order().ToArray();
        if (values.Length < 2) throw new ArgumentException("自动分辨率检测至少需要两个不同坐标");
        var tolerance = Math.Max(1e-7, (values[^1] - values[0]) * 1e-7);
        var gaps = new List<double>();
        double previous = values[0];
        foreach (var value in values)
            if (value - previous > tolerance) { gaps.Add(value - previous); previous = value; }
        if (gaps.Count == 0) throw new ArgumentException("点云在该轴退化，请手动设置分辨率");
        gaps.Sort();
        return gaps[gaps.Count / 2];
    }

    public static (PointCloud Cloud, CloudRaster Raster) Build(OpContext ctx, PointCloud source,
        IReadOnlyList<int> selected, double dx, double dy, string mode,
        IReadOnlyDictionary<string, object> reference = null)
    {
        if (selected.Count == 0) throw new ArgumentException("裁切区域没有有效点");
        var transform = CloudRegionSelector.CreateReferenceTransform(reference);
        var points = new List<(int Index, double X, double Y, double Z)>(selected.Count);
        foreach (var index in selected)
        {
            if ((points.Count & 4095) == 0) ctx.CancellationToken.ThrowIfCancellationRequested();
            var q = transform(source.X[index], source.Y[index], source.Z[index]);
            if (double.IsFinite(q.X + q.Y + q.Z)) points.Add((index, q.X, q.Y, q.Z));
        }
        if (points.Count == 0) throw new ArgumentException("输入点云没有有限坐标");
        if (dx == 0) dx = Spacing(points.Select(q => q.X));
        if (dy == 0) dy = Spacing(points.Select(q => q.Y));
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || dx <= 0 || dy <= 0)
            throw new ArgumentException("XY 分辨率必须为正数（0 表示自动检测）");
        if (mode is not ("Minimum" or "Maximum" or "Mean" or "Median"))
            throw new ArgumentException("无效的 Z 聚合模式：" + mode);
        var x0 = points.Min(q => q.X); var y0 = points.Min(q => q.Y);
        var widthValue = Math.Floor((points.Max(q => q.X) - x0) / dx + 1e-4) + 1;
        var heightValue = Math.Floor((points.Max(q => q.Y) - y0) / dy + 1e-4) + 1;
        if (widthValue > 32768 || heightValue > 32768 || widthValue * heightValue > 16_000_000)
            throw new ArgumentException("点云栅格超过 1600 万像素，请增大 XY 分辨率");
        int width = (int)widthValue, height = (int)heightValue, cells = checked(width * height);
        var counts = new int[cells]; var sums = new double[cells]; var intensities = new double[cells];
        var chosen = Enumerable.Repeat(-1, cells).ToArray();
        var medians = mode == "Median" ? new Dictionary<int, List<double>>() : null;
        int visited = 0;
        foreach (var q in points)
        {
            if ((visited++ & 4095) == 0) ctx.CancellationToken.ThrowIfCancellationRequested();
            var x = Math.Clamp((int)Math.Floor((q.X - x0) / dx + 1e-4), 0, width - 1);
            var y = Math.Clamp((int)Math.Floor((q.Y - y0) / dy + 1e-4), 0, height - 1);
            int cell = y * width + x;
            if (counts[cell] == 0 || mode == "Minimum" && q.Z < sums[cell] || mode == "Maximum" && q.Z > sums[cell])
            { chosen[cell] = q.Index; if (mode is "Minimum" or "Maximum") sums[cell] = q.Z; }
            if (mode is "Mean" or "Median") sums[cell] += q.Z;
            if (medians != null)
            {
                if (!medians.TryGetValue(cell, out var values)) medians[cell] = values = new();
                values.Add(q.Z);
            }
            intensities[cell] += q.Index < source.I.Count ? source.I[q.Index] : 0;
            counts[cell]++;
        }
        var output = new PointCloud();
        var indices = Enumerable.Repeat(-1, cells).ToArray();
        var colors = source.ColorsRgb?.Length == source.Count ? new List<uint>() : null;
        for (int cell = 0; cell < cells; cell++)
        {
            if ((cell & 4095) == 0) ctx.CancellationToken.ThrowIfCancellationRequested();
            if (counts[cell] == 0) continue;
            var z = mode == "Mean" ? sums[cell] / counts[cell] : sums[cell];
            if (medians != null)
            { var values = medians[cell]; values.Sort(); z = (values[(values.Count - 1) / 2] + values[values.Count / 2]) / 2; }
            var world = World(x0 + cell % width * dx, y0 + cell / width * dy, z, reference);
            indices[cell] = output.Count;
            output.Add((float)world.X, (float)world.Y, (float)world.Z, (float)(intensities[cell] / counts[cell]));
            colors?.Add(source.ColorsRgb[chosen[cell]]);
        }
        output.ColorsRgb = colors?.ToArray();
        output.Raster = new(width, height, x0, y0, dx, dy, indices);
        return (output, output.Raster);
    }

    private static (double X, double Y, double Z) World(double x, double y, double z, IReadOnlyDictionary<string, object> r)
    {
        var rx = C3.N(r, "X Angle", 0) * Math.PI / 180; var ry = C3.N(r, "Y Angle", 0) * Math.PI / 180;
        var rz = C3.N(r, "Z Angle", 0) * Math.PI / 180;
        var yy = Math.Cos(rx) * y - Math.Sin(rx) * z; z = Math.Sin(rx) * y + Math.Cos(rx) * z;
        var xx = Math.Cos(ry) * x + Math.Sin(ry) * z; z = -Math.Sin(ry) * x + Math.Cos(ry) * z;
        return (Math.Cos(rz) * xx - Math.Sin(rz) * yy + C3.N(r, "X", 0),
            Math.Sin(rz) * xx + Math.Cos(rz) * yy + C3.N(r, "Y", 0), z + C3.N(r, "Z", 0));
    }
}

public sealed class SequentialProcessingOp : IOperator
{
    public string Driver => "SequentialProcessing";
    public bool Run(OpContext ctx, ParamBag p)
    {
        p.Context ??= ctx;
        var input = p.Int("IM In"); var output = p.Int("IM Out");
        var source = ctx.Clouds.Get(input) ?? throw new ArgumentException($"输入点云 IM{input} 不存在");
        var selected = CloudRegionSelector.Select(source, p);
        var (cloud, raster) = CloudRasterizer.Build(ctx, source, selected,
            p.Dbl("X Resolution"), p.Dbl("Y Resolution"), p.Str("Z Mode", "Mean"), p.Group("Reference"));
        ctx.Images.Remove(output); ctx.Profiles.Remove(output); ctx.Clouds.Set(output, cloud);
        ctx.Vars.Set("Width", raster.Width); ctx.Vars.Set("Height", raster.Height);
        ctx.Vars.Set("Point Count", cloud.Count);
        ctx.Log($"3D 点云有序化：{source.Count} 点 -> {raster.Width}×{raster.Height}，有效 {cloud.Count} 点，{p.Str("Z Mode", "Mean")}", false);
        return true;
    }
}

public sealed class PointCloudToDepthImageOp : IOperator
{
    public string Driver => "PointCloudToDepthImage";
    public bool Run(OpContext ctx, ParamBag p)
    {
        p.Context ??= ctx;
        var input = p.Int("IM In"); var output = p.Int("IM Out");
        var source = ctx.Clouds.Get(input) ?? throw new ArgumentException($"输入点云 IM{input} 不存在");
        if (source.Count == 0) throw new ArgumentException("输入点云为空");
        var selected = CloudRegionSelector.Select(source, p);
        var automatic = p.Group("Resolution of Depth Image")?.GetValueOrDefault("Automatically Detect XY Resolution") is not false;
        double dx = automatic ? source.Raster?.ResolutionX ?? 0 : p.GroupDbl("Resolution of Depth Image", "X-Resolution", .1);
        double dy = automatic ? source.Raster?.ResolutionY ?? 0 : p.GroupDbl("Resolution of Depth Image", "Y-Resolution", .1);
        var original = p.Bool("Output at Original Image Dimensions");
        var existing = source.Raster;
        PointCloud cloud; CloudRaster raster;
        if (existing != null && (original || automatic && !p.Bool("Use Region")))
        {
            cloud = source; raster = existing;
            if (p.Bool("Use Region"))
            {
                var membership = selected.ToHashSet();
                raster = existing with { PointIndices = existing.PointIndices.Select(i => membership.Contains(i) ? i : -1).ToArray() };
            }
        }
        else
        {
            var area = original ? Enumerable.Range(0, source.Count).ToList() : selected;
            (cloud, raster) = CloudRasterizer.Build(ctx, source, area, dx, dy, "Mean");
            if (original && p.Bool("Use Region"))
            {
                // Keep the full frame, then mask unselected cells in the same grid.
                var mask = new HashSet<int>(selected.Select(i =>
                    Math.Clamp((int)Math.Floor((source.Y[i] - raster.OriginY) / raster.ResolutionY + 1e-4), 0, raster.Height - 1) * raster.Width +
                    Math.Clamp((int)Math.Floor((source.X[i] - raster.OriginX) / raster.ResolutionX + 1e-4), 0, raster.Width - 1)));
                raster = raster with { PointIndices = raster.PointIndices.Select((i, cell) => mask.Contains(cell) ? i : -1).ToArray() };
            }
        }
        var kind = p.Str("Output Depth Image Type", "8-bit depth map");
        bool floating = kind == "Float depth map", sixteen = kind == "16-bit depth map";
        if (!floating && !sixteen && kind != "8-bit depth map") throw new ArgumentException("无效的深度图类型：" + kind);
        var valid = raster.PointIndices.Where(i => i >= 0).Distinct().ToArray();
        if (valid.Length == 0) throw new ArgumentException("深度图区域没有有效点");
        double lo = valid.Min(i => cloud.Z[i]), hi = valid.Max(i => cloud.Z[i]);
        var autoZ = p.Group("Resolution of Depth Image")?.GetValueOrDefault("Automatically Detect Z Resolution") is not false;
        double dz = floating ? 1 : autoZ ? (hi > lo ? (hi - lo) / (sixteen ? 65535 : 255) : 1)
            : p.GroupDbl("Resolution of Depth Image", "Z-Resolution", .1);
        if (!(dz > 0) || !double.IsFinite(dz)) throw new ArgumentException("Z 分辨率必须为有限正数");
        double offsetZ = floating ? 0 : autoZ ? lo : 0;
        double invalid = floating ? p.Dbl("Definition of invalid points in float depth map", -10000)
            : p.Int(sixteen ? "Definition of invalid points in 16-bit depth map" : "Definition of invalid points in 8-bit depth map");
        if (!double.IsFinite(invalid) || !floating && (invalid < 0 || invalid > (sixteen ? 65535 : 255)))
            throw new ArgumentException("无效点填充值超出深度图类型范围");
        var image = new Mat(raster.Height, raster.Width, floating ? MatType.CV_32FC1 : sixteen ? MatType.CV_16UC1 : MatType.CV_8UC1, Scalar.All(invalid));
        try
        {
            for (int cell = 0; cell < raster.PointIndices.Length; cell++)
            {
                if ((cell & 4095) == 0) ctx.CancellationToken.ThrowIfCancellationRequested();
                int i = raster.PointIndices[cell]; if (i < 0) continue;
                int y = cell / raster.Width, x = cell % raster.Width;
                if (floating) image.Set(y, x, cloud.Z[i]);
                else if (sixteen) image.Set(y, x, (ushort)Math.Clamp(Math.Round((cloud.Z[i] - offsetZ) / dz), 0, 65535));
                else image.Set(y, x, (byte)Math.Clamp(Math.Round((cloud.Z[i] - offsetZ) / dz), 0, 255));
            }
            ctx.SetImage(output, image);
            ctx.Images.SetDepthGeometry(output, new(cloud, raster, source));
        }
        catch { if (!ReferenceEquals(ctx.Images.Get(output), image)) image.Dispose(); throw; }
        ctx.Vars.Set("offset_x", raster.OriginX); ctx.Vars.Set("offset_y", raster.OriginY); ctx.Vars.Set("offset_z", offsetZ);
        ctx.Vars.Set("resolution_x", raster.ResolutionX); ctx.Vars.Set("resolution_y", raster.ResolutionY); ctx.Vars.Set("resolution_z", dz);
        ctx.Vars.Set("value_for_invalid_points", invalid);
        ctx.Log($"点云转深度图：{raster.Width}×{raster.Height} {kind} -> IM{output}，有效 {valid.Length} 点", false);
        return true;
    }
}

public sealed class DepthImageToPointCloudOp : IOperator
{
    public string Driver => "DepthImageToPointCloud";
    public bool Run(OpContext ctx, ParamBag p)
    {
        var input = p.Int("IM In"); var output = p.Int("IM Out");
        var image = ctx.Images.Get(input) ?? throw new ArgumentException($"输入深度图 IM{input} 不存在");
        if (image.Empty() || image.Channels() != 1) throw new ArgumentException("深度图必须为非空单通道图像");
        if ((long)image.Width * image.Height > 16_000_000) throw new ArgumentException("深度图超过 1600 万像素");
        double dx = p.GroupDbl("Resolution", "X-Resolution", 1), dy = p.GroupDbl("Resolution", "Y-Resolution", 1), dz = p.GroupDbl("Resolution", "Z-Resolution", 1);
        double ox = p.GroupDbl("Offset", "X-Offset", 0), oy = p.GroupDbl("Offset", "Y-Offset", 0), oz = p.GroupDbl("Offset", "Z-Offset", 0);
        if (!double.IsFinite(dx + dy + dz + ox + oy + oz) || dx <= 0 || dy <= 0 || dz <= 0)
            throw new ArgumentException("深度图分辨率必须为正数，偏移必须为有限数值");
        var type = image.Type();
        if (type != MatType.CV_8UC1 && type != MatType.CV_16UC1 && type != MatType.CV_16SC1
            && type != MatType.CV_32FC1 && type != MatType.CV_64FC1) throw new ArgumentException("不支持的深度图像素类型：" + type);
        var invalid = p.Dbl("Definition of invalid points in depth map");
        int imageWidth = image.Width, imageHeight = image.Height;
        var mapping = ctx.Images.GetDepthGeometry(input)?.Raster;
        var cloud = new PointCloud(); var indices = Enumerable.Repeat(-1, checked(image.Width * image.Height)).ToArray();
        for (int y = 0; y < imageHeight; y++)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < imageWidth; x++)
            {
                double value = type == MatType.CV_8UC1 ? image.At<byte>(y, x) : type == MatType.CV_16UC1 ? image.At<ushort>(y, x)
                    : type == MatType.CV_16SC1 ? image.At<short>(y, x) : type == MatType.CV_32FC1 ? image.At<float>(y, x) : image.At<double>(y, x);
                int cell = y * imageWidth + x;
                // A generated map knows the difference between a valid zero and a hole.
                if (!double.IsFinite(value) || (mapping != null ? mapping.PointIndices[cell] < 0 : value.Equals(invalid))) continue;
                indices[cell] = cloud.Count;
                double wx = ox + x * dx, wy = oy + y * dy, wz = oz + value * dz;
                if (!float.IsFinite((float)wx) || !float.IsFinite((float)wy) || !float.IsFinite((float)wz))
                    throw new ArgumentException("深度图坐标换算超出浮点数范围");
                cloud.Add((float)wx, (float)wy, (float)wz);
            }
        }
        if (cloud.Count == 0) throw new ArgumentException("深度图没有有效点");
        cloud.Raster = new(image.Width, image.Height, ox, oy, dx, dy, indices);
        ctx.Images.Remove(output); ctx.Profiles.Remove(output); ctx.Clouds.Set(output, cloud);
        ctx.Vars.Set("Point Count", cloud.Count);
        ctx.Log($"深度图转点云：{imageWidth}×{imageHeight} -> IM{output}，有效 {cloud.Count} 点", false);
        return true;
    }
}
