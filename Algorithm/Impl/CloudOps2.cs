using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>3D 变换：平移 + 旋转（XYZ 欧拉角，单位度）。</summary>
public class CloudTransformOp : IOperator
{
    public string Driver => "3D TransformationFilter";

    public bool Run(OpContext ctx, ParamBag p)
    {
        var input = p.Int("IM In", 0);
        var c = ctx.Clouds.Get(input);
        if (c == null || c.Count == 0) { ctx.Log("3D transform: input is empty", true); return false; }

        var matrix = new double[4, 4];
        for (int i = 0; i < 4; i++) matrix[i, i] = 1;
        var mode = p.Str("Transformation Mode", "Legacy");
        double Number(string group, string key, double fallback)
        {
            var value = p.Bound(group + "." + key + ".Value") ?? p.Bound(group + "." + key);
            if (value != null && double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var bound)) return bound;
            var outer = p.Group(group);
            return C3.Sub(outer, key) is { } nested ? C3.N(nested, "Value", fallback) : C3.N(outer, key, fallback);
        }
        bool Flag(string group, string key)
            => p.Group(group) is { } g && g.TryGetValue(key, out var value) && value is true;
        void Rotation(double rx, double ry, double rz, bool inverse)
        {
            rx *= Math.PI / 180; ry *= Math.PI / 180; rz *= Math.PI / 180;
            var cx = Math.Cos(rx); var sx = Math.Sin(rx);
            var cy = Math.Cos(ry); var sy = Math.Sin(ry);
            var cz = Math.Cos(rz); var sz = Math.Sin(rz);
            var r = new double[,] {
                { cz * cy, cz * sy * sx - sz * cx, cz * sy * cx + sz * sx },
                { sz * cy, sz * sy * sx + cz * cx, sz * sy * cx - cz * sx },
                { -sy, cy * sx, cy * cx }
            };
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++) matrix[row, col] = inverse ? r[col, row] : r[row, col];
        }
        switch (mode.ToLowerInvariant())
        {
            case "translation":
                var sign = Flag("Translation", "invert translation") ? -1 : 1;
                for (int axis = 0; axis < 3; axis++)
                    matrix[axis, 3] = sign * Number("Translation", "translation in " + "XYZ"[axis], 0);
                break;
            case "rotation":
                Rotation(Number("Rotation", "rotation around X-axis(degrees)", 0),
                    Number("Rotation", "rotation around Y-axis(degrees)", 0),
                    Number("Rotation", "rotation around Z-axis(degrees)", 0), Flag("Rotation", "invert rotation"));
                break;
            case "scaling":
                for (int axis = 0; axis < 3; axis++)
                {
                    var scale = Number("Scaling", "scalefactor for " + "XYZ"[axis], 1);
                    if (Flag("Scaling", "invert scaling"))
                    {
                        if (Math.Abs(scale) < 1e-15) { ctx.Log("3D transform: cannot invert zero scale", true); return false; }
                        scale = 1 / scale;
                    }
                    matrix[axis, axis] = scale;
                }
                break;
            case "mirror":
                for (int axis = 0; axis < 3; axis++) matrix[axis, axis] = Flag("Mirror", "XYZ"[axis].ToString()) ? -1 : 1;
                break;
            case "shearing":
                var shear = p.Group("Shearing");
                var axes = shear != null && shear.TryGetValue("shear axes", out var axesValue) ? axesValue?.ToString() : "shear X relative to Y";
                var match = System.Text.RegularExpressions.Regex.Match(axes ?? "", @"^shear ([XYZ]) relative to ([XYZ])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!match.Success || match.Groups[1].Value.Equals(match.Groups[2].Value, StringComparison.OrdinalIgnoreCase))
                { ctx.Log("3D transform: unsupported shear axes", true); return false; }
                var unit = shear != null && shear.TryGetValue("shearing unit", out var unitValue) ? unitValue?.ToString() : "raw shear factor";
                var factor = unit != null && unit.Contains("angle", StringComparison.OrdinalIgnoreCase)
                    ? Math.Tan(Number("Shearing", "shear angle", 0) * Math.PI / 180) : Number("Shearing", "shear factor", 0);
                if (Flag("Shearing", "invert shearing")) factor = -factor;
                matrix["XYZ".IndexOf(match.Groups[1].Value.ToUpperInvariant()), "XYZ".IndexOf(match.Groups[2].Value.ToUpperInvariant())] = factor;
                break;
            case "matrix":
                var raw = p.Bound("Matrix") ?? p.Raw("Matrix");
                if (raw is not System.Collections.IEnumerable sequence)
                { ctx.Log("3D transform: matrix requires 16 numbers", true); return false; }
                var values = sequence.Cast<object>().Select(value => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                if (values.Length != 16) { ctx.Log("3D transform: matrix requires 16 numbers", true); return false; }
                for (int row = 0; row < 4; row++)
                    for (int col = 0; col < 4; col++) matrix[row, col] = values[row * 4 + col];
                break;
            case "reference transformation":
                Rotation(Number("Reference transformation", "X Angle", 0), Number("Reference transformation", "Y Angle", 0),
                    Number("Reference transformation", "Z Angle", 0), false);
                for (int axis = 0; axis < 3; axis++) matrix[axis, 3] = Number("Reference transformation", "XYZ"[axis].ToString(), 0);
                break;
            case "legacy":
                Rotation(P.D(p, 0, "Rotation X", "X Angle"), P.D(p, 0, "Rotation Y", "Y Angle"), P.D(p, 0, "Rotation Z", "Z Angle"), false);
                matrix[0, 3] = p.Dbl("Translation X", p.Dbl("X Offset", 0));
                matrix[1, 3] = p.Dbl("Translation Y", p.Dbl("Y Offset", 0));
                matrix[2, 3] = p.Dbl("Translation Z", p.Dbl("Z Offset", 0));
                break;
            default:
                ctx.Log($"3D transform: unsupported mode {mode}", true);
                return false;
        }
        if (matrix.Cast<double>().Any(value => !double.IsFinite(value)))
        { ctx.Log("3D transform: parameters must be finite", true); return false; }
        var output = new PointCloud();
        for (int i = 0; i < c.Count; i++)
        {
            double x = c.X[i], y = c.Y[i], z = c.Z[i];
            var w = matrix[3, 0] * x + matrix[3, 1] * y + matrix[3, 2] * z + matrix[3, 3];
            if (Math.Abs(w) < 1e-15) { ctx.Log("3D transform: point maps to infinity", true); return false; }
            var nx = (float)((matrix[0, 0] * x + matrix[0, 1] * y + matrix[0, 2] * z + matrix[0, 3]) / w);
            var ny = (float)((matrix[1, 0] * x + matrix[1, 1] * y + matrix[1, 2] * z + matrix[1, 3]) / w);
            var nz = (float)((matrix[2, 0] * x + matrix[2, 1] * y + matrix[2, 2] * z + matrix[2, 3]) / w);
            if (!float.IsFinite(nx) || !float.IsFinite(ny) || !float.IsFinite(nz))
            { ctx.Log("3D transform: output coordinate is invalid", true); return false; }
            output.Add(nx, ny, nz, c.I[i]);
        }
        var slot = p.Has("output to different IM") && !p.Bool("output to different IM") ? input : p.Int("IM Out", 0);
        ctx.Clouds.Set(slot, output);
        ctx.Vars.Set("Transform.Count", output.Count);
        ctx.Log($"3D transform: {mode}, {output.Count} points -> IM{slot}", false);
        return true;
    }
}

/// <summary>点云质心（3D PointCloudCentroid）。</summary>
public class CentroidOp : IOperator
{
    public string Driver => "3D PointCloudCentroid";

    public bool Run(OpContext ctx, ParamBag p)
    { return GeometryCompletion.Centroid(ctx, p); }
}

/// <summary>3D 距离：点到点 / 点到平面 / 平面到平面。</summary>
public class Distance3DOp : IOperator
{
    public string Driver => "3D Distance";

    public bool Run(OpContext ctx, ParamBag p)
    {
        if(p.Has("Mode"))return NativeDistanceMeasurement.Run(ctx,p);
        var p1 = p.Bound("Point 1") as PointObj ?? AsPoint(p, "Point 1");
        var p2 = p.Bound("Point 2") as PointObj ?? AsPoint(p, "Point 2");
        var pl1 = p.Bound("Plane 1") as PlaneObj;
        var pl2 = p.Bound("Plane 2") as PlaneObj;

        double d;
        if (pl1 != null && pl2 != null)
        {
            // 两平面距离（取平面上一点到另一平面）
            d = Math.Abs(pl1.NX * pl2.CX + pl1.NY * pl2.CY + pl1.NZ * pl2.CZ + pl1.D);
            ctx.Log($"3D 距离（平面-平面）：{d:0.000}", false);
        }
        else if (pl1 != null && p2 != null)
        {
            d = Math.Abs(pl1.NX * p2.X + pl1.NY * p2.Y + pl1.NZ * 0 + pl1.D);
            ctx.Log($"3D 距离（点-平面）：{d:0.000}", false);
        }
        else if (p1 != null && p2 != null)
        {
            d = Math.Sqrt(Math.Pow(p1.X - p2.X, 2) + Math.Pow(p1.Y - p2.Y, 2));
            ctx.Log($"3D 距离（点-点）：{d:0.000}", false);
        }
        else
        {
            ctx.Log("3D 距离：参数不足", true);
            return false;
        }

        ctx.Vars.Set("Distance", d);
        return true;
    }

    private static PointObj AsPoint(ParamBag p, string key)
    {
        var g = p.Group(key);
        if (g == null) return null;
        return new PointObj { X = C3.N(g, "X", 0), Y = C3.N(g, "Y", 0) };
    }
}

/// <summary>3D 显示：把点云渲染成俯视深度图（可选着色）。</summary>
public class Display3DOp : IOperator
{
    public string Driver => "3D Display";

    public bool Run(OpContext ctx, ParamBag p)
    {
        var c = ctx.Clouds.Get(p.Int("IM In", 0));
        if (c == null || c.Count == 0) { ctx.Log("3D 显示：点云为空", true); return false; }

        ctx.Images.Set(p.Int("IM Out", 0), Render(c));
        ctx.Log($"3D 显示：{c.Count} 点渲染为 800x600", false);
        return true;
    }

    /// <summary>把点云渲染成俯视伪彩深度图（界面预览也用它）。</summary>
    internal static Mat Render(PointCloud c)
    {
        const int W = 800, H = 600;
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
        var (zlo, zhi) = c.ZRange();
        for (int i = 0; i < c.Count; i++)
        {
            if (c.X[i] < minX) minX = c.X[i];
            if (c.X[i] > maxX) maxX = c.X[i];
            if (c.Y[i] < minY) minY = c.Y[i];
            if (c.Y[i] > maxY) maxY = c.Y[i];
        }
        var spanX = Math.Max(1e-6, maxX - minX);
        var spanY = Math.Max(1e-6, maxY - minY);
        var span = Math.Max(spanX, spanY);
        var spanZ = Math.Max(1e-6, zhi - zlo);

        using var img = new Mat(H, W, MatType.CV_8UC3, new Scalar(24, 24, 28));
        var zBuf = new float[H, W];
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) zBuf[y, x] = float.MaxValue;

        for (int i = 0; i < c.Count; i++)
        {
            var px = (int)((c.X[i] - minX) / span * (W - 20)) + 10;
            var py = H - 10 - (int)((c.Y[i] - minY) / span * (H - 20));
            if (px < 0 || py < 0 || px >= W || py >= H) continue;
            if (c.Z[i] >= zBuf[py, px]) continue;
            zBuf[py, px] = c.Z[i];
        }

        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (zBuf[y, x] == float.MaxValue) continue;
                var t = (zBuf[y, x] - zlo) / spanZ;
                // 蓝 -> 青 -> 黄 -> 红 伪彩
                byte b = (byte)(255 * Math.Clamp(1.5 - Math.Abs(t - 0.0) * 3, 0, 1));
                byte g = (byte)(255 * Math.Clamp(1.2 - Math.Abs(t - 0.5) * 2.2, 0, 1));
                byte r = (byte)(255 * Math.Clamp(t * 1.6 - 0.2, 0, 1));
                img.Set(y, x, new Vec3b(b, g, r));
            }

        return img.Clone();
    }
}

/// <summary>3D 圆拟合（点云上的圆：投影到最佳平面后拟合）。</summary>
public class Circle3DOp : IOperator
{
    public string Driver => "3D Circle";

    public bool Run(OpContext ctx, ParamBag p)
    { return CircleCompletion3D.Run(ctx, p); }
}

/// <summary>3D 圆柱拟合（轴向取拟合平面法向，半径取径向均值）。</summary>
public class CylinderOp : IOperator
{
    public string Driver => "3D CylinderFitting";

    public bool Run(OpContext ctx, ParamBag p)
    {
        var c = ctx.Clouds.Get(p.Int("IM In", 0));
        if (c == null) { ctx.Log("圆柱拟合：输入为空", true); return false; }
        p.Context ??= ctx;
        var idx = CloudRegionSelector.Select(c, p);
        if (idx.Count < 6) { ctx.Log($"圆柱拟合：有效点 {idx.Count} 个，不足", true); return false; }
        var minimum=p.GroupDbl("Points Limit","Minimum number of points",0);var maximum=p.GroupDbl("Points Limit","Maximum number of points",1000000);
        if(minimum<0||maximum<minimum||idx.Count<minimum||idx.Count>maximum)throw new ArgumentException("圆柱输入点数不满足限制");
        if(p.Group("Statistical Filter")?.GetValueOrDefault("Enable") is true)
        {
            int k=(int)p.GroupDbl("Statistical Filter","Set the Number of Neighboring Points",100);double factor=p.GroupDbl("Statistical Filter","Set Standard Deviation Multiple Threshold",.15);
            if(k<1||k>=idx.Count||!double.IsFinite(factor)||factor<0)throw new ArgumentException("圆柱统计滤波参数无效");
            var subset=CloudSetOp.Copy(c,idx);var tree=new CloudNeighbors3D(subset,ctx.CancellationToken);var heap=new double[k];var distances=new double[idx.Count];
            for(int i=0;i<idx.Count;i++){if((i&255)==0)ctx.CancellationToken.ThrowIfCancellationRequested();distances[i]=tree.MeanDistance(i,heap);}
            double average=distances.Average(),deviation=Math.Sqrt(distances.Average(v=>(v-average)*(v-average)));idx=idx.Where((v,i)=>distances[i]<=average+factor*deviation).ToList();
            if(idx.Count<6)throw new ArgumentException("圆柱统计滤波后有效点不足六个");
        }
        double threshold=p.GroupDbl("Set RANSAC Parameters","Set Distance Threshold",.15);if(!double.IsFinite(threshold)||threshold<=0)throw new ArgumentException("圆柱距离阈值无效");
        var points=idx.Select(i=>MetrologyMath.At(c,i)).ToList();var fit=SurfaceCylinderFit.Fit(points,ctx.CancellationToken);
        var inliers=idx.Where(i=>Math.Abs((MetrologyMath.At(c,i)-fit.Center).Cross(fit.Axis).Length-fit.Radius)<=threshold).ToList();
        if(inliers.Count<6)throw new ArgumentException("圆柱拟合内点不足，请检查距离阈值");
        if(inliers.Count<idx.Count)fit=SurfaceCylinderFit.Fit(inliers.Select(i=>MetrologyMath.At(c,i)).ToList(),ctx.CancellationToken);
        var bottom=fit.Center+fit.Axis*fit.MinAxial;var top=fit.Center+fit.Axis*fit.MaxAxial;
        var cylinder=new Dictionary<string,object>{{"Top",top.Native()},{"Bottom",bottom.Native()},{"Radius",fit.Radius}};
        ctx.Vars.Set("Cylinder.CenterX", fit.Center.X);
        ctx.Vars.Set("Cylinder.CenterY", fit.Center.Y);
        ctx.Vars.Set("Cylinder.Radius", fit.Radius);
        ctx.Vars.Set("Cylinder.Std", Math.Sqrt(fit.Residual));
        ctx.Vars.Set("Cylinder.Count", idx.Count);
        ctx.Vars.Set("central_axis",new Dictionary<string,object>{{"Position",fit.Center.Native()},{"Direction",fit.Axis.Native()}});
        ctx.Vars.Set("cylinder",new List<object>{cylinder});ctx.Vars.Set("radius",fit.Radius);ctx.Vars.Set("diameter",fit.Radius*2);ctx.Vars.Set("inliers",new List<object>{new PointSet3D(c,inliers)});
        DetectionOverlay2D.Begin(ctx,Driver);if(p.Bool("Display Cylinder"))DetectionOverlay2D.Add(ctx,p,Driver,"cylinder",cylinder,view:p.Int("IM In"));
        if(p.Bool("Inliers"))c.DisplayIndices=inliers.ToArray();else if(p.Bool("Pointset Cut"))c.DisplayIndices=idx.ToArray();
        MetrologyMath.Warning(ctx,"自有圆柱采用三主轴初始化、径向最小二乘和残差裁剪；未使用原版法线 RANSAC，结果需校准");
        ctx.Log($"圆柱拟合：R={fit.Radius:0.000}，内点 {inliers.Count}/{idx.Count}，输出 central_axis 与 cylinder", false);
        return true;
    }
}
