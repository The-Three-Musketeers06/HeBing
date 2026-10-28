using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>3D 平面输出。</summary>
public class PlaneObj
{
    public double NX, NY, NZ, D;
    public double CX, CY, CZ;
    public int Inliers;
    public IReadOnlyDictionary<string,object> Position => new Dictionary<string,object>{{"X",CX},{"Y",CY},{"Z",CZ}};
    public IReadOnlyDictionary<string,object> Normal => new Dictionary<string,object>{{"X",NX},{"Y",NY},{"Z",NZ}};

    /// <summary>点 (x,y) 处平面高度。</summary>
    public double ZAt(double x, double y)
        => Math.Abs(NZ) < 1e-9 ? CZ : -(NX * x + NY * y + D) / NZ;

    public override string ToString()
        => $"N=({NX:0.000},{NY:0.000},{NZ:0.000}) D={D:0.000} 中心({CX:0.00},{CY:0.00},{CZ:0.00})";
}

/// <summary>点云范围调试输出。</summary>
internal static class CloudDbg
{
    public static string Bounds(PointCloud c)
    {
        if (c == null || c.Count == 0) return "空";
        float x1 = float.MaxValue, x2 = float.MinValue, y1 = float.MaxValue, y2 = float.MinValue, z1 = float.MaxValue, z2 = float.MinValue;
        for (int i = 0; i < c.Count; i++)
        {
            if (c.X[i] < x1) x1 = c.X[i]; if (c.X[i] > x2) x2 = c.X[i];
            if (c.Y[i] < y1) y1 = c.Y[i]; if (c.Y[i] > y2) y2 = c.Y[i];
            if (c.Z[i] < z1) z1 = c.Z[i]; if (c.Z[i] > z2) z2 = c.Z[i];
        }
        return $"X {x1:0.##}..{x2:0.##} Y {y1:0.##}..{y2:0.##} Z {z1:0.##}..{z2:0.##}";
    }
}

/// <summary>3D 算子公共参数读取。</summary>
internal static class C3
{
    public static double N(IReadOnlyDictionary<string, object> d, string k, double def)
    {
        if (d == null || !d.TryGetValue(k, out var v) || v == null) return def;
        return double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : def;
    }

    public static IReadOnlyDictionary<string, object> Sub(IReadOnlyDictionary<string, object> d, string k)
        => d != null && d.TryGetValue(k, out var v) && v is IReadOnlyDictionary<string, object> s ? s : null;

    public static (double x, double y, double z) P3(IReadOnlyDictionary<string, object> d)
        => d == null ? (0, 0, 0) : (N(d, "X", 0), N(d, "Y", 0), N(d, "Z", 0));

    /// <summary>把 Region 解析成若干盒子（支持 box / box array / cylinderbox）。</summary>
    public static List<(double x1, double y1, double z1, double x2, double y2, double z2)> Boxes(ParamBag p)
    {
        // 优先用绑定的区域（如 3D Region / CreateROI 的输出）
        var bound = p.Bound("Region");
        var fromBound = ToBoxes(bound);
        if (fromBound.Count > 0) return fromBound;

        var r = p.Group("Region");
        return ParseRegion(r);
    }

    /// <summary>把算子输出的区域对象转成盒子。</summary>
    public static List<(double x1, double y1, double z1, double x2, double y2, double z2)> ToBoxes(object bound)
    {
        var list = new List<(double, double, double, double, double, double)>();
        if (bound is List<object> items)
        {
            foreach (var it in items)
                if (it is IReadOnlyDictionary<string, object> d)
                {
                    var s = P3(Sub(d, "Start"));
                    var e = P3(Sub(d, "End"));
                    if (Math.Abs(s.x - e.x) < 1e-9 && Math.Abs(s.y - e.y) < 1e-9) continue;
                    list.Add((Math.Min(s.x, e.x), Math.Min(s.y, e.y), Math.Min(s.z, e.z),
                              Math.Max(s.x, e.x), Math.Max(s.y, e.y), Math.Max(s.z, e.z)));
                }
        }
        return list;
    }

    /// <summary>解析一个 Region 结构（含 Type 与 box/box array/cylinderbox）。</summary>
    public static List<(double x1, double y1, double z1, double x2, double y2, double z2)> ParseRegion(
        IReadOnlyDictionary<string, object> r)
    {
        var list = new List<(double, double, double, double, double, double)>();
        if (r == null) return list;

        void AddBox(IReadOnlyDictionary<string, object> b)
        {
            var s = P3(Sub(b, "Start"));
            var e = P3(Sub(b, "End"));
            // 跳过未设置的占位盒子（Start == End）
            if (Math.Abs(s.x - e.x) < 1e-9 && Math.Abs(s.y - e.y) < 1e-9) return;
            // 某轴退化（Start==End）视为该轴不限制
            double x1 = Math.Min(s.x, e.x), x2 = Math.Max(s.x, e.x);
            double y1 = Math.Min(s.y, e.y), y2 = Math.Max(s.y, e.y);
            double z1 = Math.Min(s.z, e.z), z2 = Math.Max(s.z, e.z);
            if (Math.Abs(s.x - e.x) < 1e-9) { x1 = double.MinValue; x2 = double.MaxValue; }
            if (Math.Abs(s.y - e.y) < 1e-9) { y1 = double.MinValue; y2 = double.MaxValue; }
            if (Math.Abs(s.z - e.z) < 1e-9) { z1 = double.MinValue; z2 = double.MaxValue; }
            list.Add((x1, y1, z1, x2, y2, z2));
        }

        var type = (r.TryGetValue("Type", out var t) ? t?.ToString() : "") ?? "";
        if (type.Contains("array", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var key in new[] { "box array", "box_array", "box" })
                if (r.TryGetValue(key, out var arr) && arr is List<object> items)
                {
                    foreach (var it in items)
                        if (it is IReadOnlyDictionary<string, object> bd) AddBox(bd);
                    break;
                }
        }
        else if (type.StartsWith("box", StringComparison.OrdinalIgnoreCase))
        {
            if (Sub(r, "box") is { } b) AddBox(b);
        }
        else if (type.StartsWith("cylinder", StringComparison.OrdinalIgnoreCase))
        {
            if (Sub(r, "cylinderbox") is { } cy)
            {
                var bottom = P3(Sub(cy, "Bottom"));
                var radius = N(cy, "Radius", 5);
                var height = N(cy, "Height", 5);
                list.Add((bottom.x - radius, bottom.y - radius, bottom.z,
                          bottom.x + radius, bottom.y + radius, bottom.z + height));
            }
        }

        // 兜底：region 里直接有 box
        if (list.Count == 0 && Sub(r, "box") is { } b2) AddBox(b2);
        return list;
    }

    public static List<int> SelectIndices(PointCloud c, IReadOnlyList<(double x1, double y1, double z1, double x2, double y2, double z2)> boxes)
    {
        var idx = new List<int>();
        if (c == null) return idx;
        if (boxes.Count == 0)
        {
            for (int i = 0; i < c.Count; i++) idx.Add(i);
            return idx;
        }
        for (int i = 0; i < c.Count; i++)
        {
            var x = c.X[i]; var y = c.Y[i]; var z = c.Z[i];
            foreach (var b in boxes)
                if (x >= b.x1 && x <= b.x2 && y >= b.y1 && y <= b.y2 && z >= b.z1 && z <= b.z2)
                {
                    idx.Add(i);
                    break;
                }
        }
        return idx;
    }

}

/// <summary>加载点云。</summary>
public class LoadPointCloudOp : IOperator
{
    public string Driver => "LoadPointCloud";

    public bool Run(OpContext ctx, ParamBag p)
    {
        InputFileSelector.Selection input;
        try{
            // Preserve legacy PCD directory playback when ancillary TXT/CSV files coexist.
            var mode=p.Str("Type",string.IsNullOrWhiteSpace(p.Str("File"))&&!string.IsNullOrWhiteSpace(p.Str("Directory"))?"Directory":"File");
            var folder=mode.Equals("Directory",StringComparison.OrdinalIgnoreCase)?ctx.ResolvePath(p.Str("Directory")):null;
            var hasPcd=folder!=null&&Directory.Exists(folder)&&Directory.EnumerateFiles(folder).Any(f=>Path.GetExtension(f).Equals(".pcd",StringComparison.OrdinalIgnoreCase));
            input=InputFileSelector.Select(ctx,p,Driver,hasPcd?new[]{".pcd"}:new[]{".pcd",".txt",".xyz",".csv"});
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException)
        {ctx.Log("加载点云："+error.Message,true);return false;}
        var path=input.Path;

        var cloud = PcdIO.Load(path);
        if (cloud.Count == 0) { ctx.Log($"加载点云：解析为空 {Path.GetFileName(path)}", true); return false; }

        ctx.Clouds.Set(p.Int("IM Out", 0), cloud);
        ctx.Images.Remove(p.Int("IM Out"));ctx.Profiles.Remove(p.Int("IM Out"));
        input.Commit(ctx);
        ctx.Vars.Set("PointCloud.Count", cloud.Count);
        ctx.Vars.Set("Filename", Path.GetFileName(path));
        ctx.Vars.Set("Fullpath", Path.GetFullPath(path));
        var (lo, hi) = cloud.ZRange();
        ctx.Log($"加载点云 {Path.GetFileName(path)}：{cloud.Count} 点，Z {lo:0.000}~{hi:0.000}", false);
        return true;
    }
}

/// <summary>保存点云。</summary>
public class SavePointCloudOp : IOperator
{
    public string Driver => "SavePointCloud";

    public bool Run(OpContext ctx, ParamBag p)
    {
        var c = ctx.Clouds.Get(p.Int("IM In", 0));
        if (c == null) { ctx.Log("保存点云：输入为空", true); return false; }
        var file = ctx.ResolveOutputPath(p.Str("File")) ?? throw new InvalidOperationException("保存点云：文件路径为空");
        PcdIO.Save(file, c);
        ctx.Log($"保存点云 {Path.GetFileName(file)}：{c.Count} 点", false);
        return true;
    }
}

/// <summary>3D 裁剪：按 Region 或 XYZ 范围保留点。</summary>
public class CutFilterOp : IOperator
{
    public string Driver => "3D CutFilter";

    public bool Run(OpContext ctx, ParamBag p)
    {
        var c = ctx.Clouds.Get(p.Int("IM In", 0));
        if (c == null) { ctx.Log("3D 裁剪：输入为空", true); return false; }

        var outCloud = new PointCloud();
        var mode = p.Str("Cutting Mode", "X-Range");
        var output = p.Int("IM Out", 0);
        var options = p.Group("Options");
        var invert = options != null && options.TryGetValue("Invert cutting", out var iv) && iv is true;

        if (mode.Contains("Copy", StringComparison.OrdinalIgnoreCase) ||
            mode.Contains("None", StringComparison.OrdinalIgnoreCase) ||
            (mode.Equals("Region", StringComparison.OrdinalIgnoreCase) && !p.Bool("Use Region", false)))
        {
            outCloud.AddRange(c);
            ctx.Clouds.Set(output, outCloud);
            ctx.Vars.Set("CutFilter.Count", outCloud.Count);
            ctx.Log($"3D 裁剪：复制 {c.Count} 点 -> IM{output}", false);
            return true;
        }

        if (mode.Contains("Range", StringComparison.OrdinalIgnoreCase))
        {
            // 每轴独立：Start==End 表示该轴不限制
            var allAxes = mode.Equals("Range", StringComparison.OrdinalIgnoreCase);
            var xr = allAxes || mode.Equals("X-Range", StringComparison.OrdinalIgnoreCase) ? p.Group("X-Range") : null;
            var yr = allAxes || mode.Equals("Y-Range", StringComparison.OrdinalIgnoreCase) ? p.Group("Y-Range") : null;
            var zr = allAxes || mode.Equals("Z-Range", StringComparison.OrdinalIgnoreCase) ? p.Group("Z-Range") : null;
            (double lo, double hi) Axis(IReadOnlyDictionary<string, object> g)
            {
                if (g == null) return (double.MinValue, double.MaxValue);
                var a = C3.N(g, "Start", 0); var b = C3.N(g, "End", 0);
                if (Math.Abs(a - b) < 1e-9) return (double.MinValue, double.MaxValue);
                return (Math.Min(a, b), Math.Max(a, b));
            }
            var (x1, x2) = Axis(xr); var (y1, y2) = Axis(yr); var (z1, z2) = Axis(zr);
            for (int i = 0; i < c.Count; i++)
            {
                var (x, y, z) = c.At(i);
                var inside = x >= x1 && x <= x2 && y >= y1 && y <= y2 && z >= z1 && z <= z2;
                if (inside == invert) continue;
                outCloud.Add(x, y, z, c.I[i]);
            }
        }
        else
        {
            var selected = CloudRegionSelector.Select(c, p).ToHashSet();
            for (int i = 0; i < c.Count; i++)
                if (selected.Contains(i) != invert)
                    outCloud.Add(c.X[i], c.Y[i], c.Z[i], c.I[i]);
        }

        ctx.Clouds.Set(output, outCloud);
        ctx.Vars.Set("CutFilter.Count", outCloud.Count);
        ctx.Log($"3D 裁剪：{c.Count} -> {outCloud.Count} 点（IM{output} {CloudDbg.Bounds(outCloud)}）", false);
        return true;
    }
}

/// <summary>3D 平面拟合（PCA + RANSAC 细化）。</summary>
public class PlaneOp : IOperator
{
    public string Driver => "3D Plane";

    public bool Run(OpContext ctx, ParamBag p)
    {
        var c = ctx.Clouds.Get(p.Int("IM In", 0));
        if (c == null) { ctx.Log("3D 平面：输入为空", true); return false; }

        var idx = CloudRegionSelector.Select(c, p);
        if (idx.Count < 3) { ctx.Log($"3D 平面：有效点 {idx.Count} 个，不足", true); return false; }

        var plane = FitPlane(c, idx);
        if (plane == null) { ctx.Log("3D 平面：拟合失败", true); return false; }

        ctx.Vars.Set("Plane", plane);
        ctx.Vars.Set("Plane.NX", plane.NX);
        ctx.Vars.Set("Plane.NY", plane.NY);
        ctx.Vars.Set("Plane.NZ", plane.NZ);
        ctx.Vars.Set("Plane.D", plane.D);
        ctx.Vars.Set("Plane.Inliers", plane.Inliers);
        ctx.Vars.Set("Plane.Flatness", Flatness(c, idx, plane));

        // 变换点云：把平面摆正到 Z=0
        var op = p.Group("Operation");
        if (op == null || !op.TryGetValue("Transform Point Cloud", out var tv) || tv is not bool tb || tb)
        {
            var outCloud = Transform(c, plane);
            ctx.Clouds.Set(p.Int("IM Out", 1), outCloud);
            ctx.Vars.Set("Plane", new PlaneObj { NX=0,NY=0,NZ=1,D=0,CX=plane.CX,CY=plane.CY,CZ=0,Inliers=plane.Inliers });
        }

        ctx.Log($"3D 平面：{plane} 内点 {plane.Inliers}/{idx.Count}", false);
        return true;
    }

    internal static PlaneObj FitPlane(PointCloud c, IReadOnlyList<int> idx)
    {
        // 先用全部点做最小二乘，再用残差裁剪做两轮细化（近似 RANSAC）
        var list = idx.ToList();
        PlaneObj best = null;
        for (int iter = 0; iter < 3; iter++)
        {
            if (list.Count < 3) break;
            double sx = 0, sy = 0, sz = 0;
            foreach (var i in list) { sx += c.X[i]; sy += c.Y[i]; sz += c.Z[i]; }
            int n = list.Count;
            double mx = sx / n, my = sy / n, mz = sz / n;

            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var i in list)
            {
                var dx = c.X[i] - mx; var dy = c.Y[i] - my; var dz = c.Z[i] - mz;
                xx += dx * dx; xy += dx * dy; xz += dx * dz;
                yy += dy * dy; yz += dy * dz; zz += dz * dz;
            }
            var (nx, ny, nz) = SmallestEigen(xx, xy, xz, yy, yz, zz);
            var d = -(nx * mx + ny * my + nz * mz);
            var plane = new PlaneObj { NX = nx, NY = ny, NZ = nz, D = d, CX = mx, CY = my, CZ = mz, Inliers = n };

            var dists = list.Select(i => Math.Abs(nx * c.X[i] + ny * c.Y[i] + nz * c.Z[i] + d)).ToList();
            var med = Median(dists);
            var th = Math.Max(0.02, med * 3);
            var keep = new List<int>();
            for (int k = 0; k < list.Count; k++) if (dists[k] <= th) keep.Add(list[k]);
            best = plane;
            if (keep.Count < 3 || keep.Count == list.Count) break;
            list = keep;
        }
        return best;
    }

    private static double Median(List<double> v)
    {
        if (v.Count == 0) return 0;
        var s = v.OrderBy(x => x).ToList();
        return s[s.Count / 2];
    }

    /// <summary>3x3 协方差矩阵最小特征值对应的特征向量（幂迭代法求最大，再正交补）。</summary>
    private static (double, double, double) SmallestEigen(double xx, double xy, double xz, double yy, double yz, double zz)
    {
        // 用 3x3 对称矩阵的 Jacobi 迭代（稳定、代码短）
        var a = new double[3, 3] { { xx, xy, xz }, { xy, yy, yz }, { xz, yz, zz } };
        var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int sweep = 0; sweep < 12; sweep++)
        {
            for (int p = 0; p < 2; p++)
                for (int q = p + 1; q < 3; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-12) continue;
                    var theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    if (Math.Abs(theta) < 1e-12) t = 1;
                    var cos = 1 / Math.Sqrt(t * t + 1);
                    var sin = t * cos;
                    for (int k = 0; k < 3; k++)
                    {
                        var akp = a[k, p]; var akq = a[k, q];
                        a[k, p] = cos * akp - sin * akq;
                        a[k, q] = sin * akp + cos * akq;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        var apk = a[p, k]; var aqk = a[q, k];
                        a[p, k] = cos * apk - sin * aqk;
                        a[q, k] = sin * apk + cos * aqk;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        var vkp = v[k, p]; var vkq = v[k, q];
                        v[k, p] = cos * vkp - sin * vkq;
                        v[k, q] = sin * vkp + cos * vkq;
                    }
                }
        }
        var minIdx = 0;
        for (int k = 1; k < 3; k++) if (a[k, k] < a[minIdx, minIdx]) minIdx = k;
        var nx = v[0, minIdx]; var ny = v[1, minIdx]; var nz = v[2, minIdx];
        var norm = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (norm < 1e-12) return (0, 0, 1);
        if (nz < 0) norm = -norm;
        return (nx / norm, ny / norm, nz / norm);
    }

    internal static double Flatness(PointCloud c, IReadOnlyList<int> idx, PlaneObj pl)
    {
        if (idx.Count == 0) return 0;
        double max = double.MinValue, min = double.MaxValue;
        foreach (var i in idx)
        {
            var d = pl.NX * c.X[i] + pl.NY * c.Y[i] + pl.NZ * c.Z[i] + pl.D;
            if (d > max) max = d;
            if (d < min) min = d;
        }
        return max - min;
    }

    internal static PointCloud Transform(PointCloud c, PlaneObj pl)
    {
        var o = c.Clone();
        var norm=Math.Sqrt(pl.NX*pl.NX+pl.NY*pl.NY+pl.NZ*pl.NZ);
        if(norm<=1e-15||!double.IsFinite(norm))throw new ArgumentException("平面法向无效");
        var nx=pl.NX/norm;var ny=pl.NY/norm;var nz=pl.NZ/norm;
        if(nz<0){nx=-nx;ny=-ny;nz=-nz;}
        // Minimal rotation maps the plane normal onto +Z. The reference captures
        // preserve its XY center and place the fitted plane at Z=0.
        var factor=1/(1+nz);
        for (int i = 0; i < c.Count; i++)
        {
            var x = c.X[i] - pl.CX; var y = c.Y[i] - pl.CY; var z = c.Z[i] - pl.CZ;
            o.X[i]=(float)((1-nx*nx*factor)*x-nx*ny*factor*y-nx*z+pl.CX);
            o.Y[i]=(float)(-nx*ny*factor*x+(1-ny*ny*factor)*y-ny*z+pl.CY);
            o.Z[i]=(float)(nx*x+ny*y+nz*z);
        }
        return o;
    }
}

/// <summary>3D 高度：Region 内相对参考平面的高度统计。</summary>
public class HeightOp : IOperator
{
    public string Driver => _driver;

    private readonly string _driver;
    public HeightOp(string driver = "3D Height") => _driver = driver;

    public bool Run(OpContext ctx, ParamBag p)
    {
        DetectionOverlay2D.Begin(ctx, Driver);
        var cloud = ctx.Clouds.Get(p.Int("IM In"));
        if (cloud == null) { ctx.Log("高度：输入为空", true); return false; }
        var groups = CloudRegionSelector.SelectGroups(cloud, p);
        var region = p.Bool("Use Region") ? p.Raw("Region") : null;
        var isArray = region is IEnumerable<object> && region is not IReadOnlyDictionary<string, object>
            || region is IReadOnlyDictionary<string, object> map && Convert.ToString(map.GetValueOrDefault("Type"))?.EndsWith(" array") == true;
        if (!isArray) return RunSingle(ctx, p, groups.SelectMany(g => g).Distinct().ToList());
        if (groups.Count == 0) throw new InvalidOperationException("高度 ROI 阵列为空");
        var output = ctx.Vars;
        var arrays = new Dictionary<string, List<object>>();
        bool warning = false; string message = "";
        try
        {
            for (int i = 0; i < groups.Count; i++)
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();
                ctx.Vars = new VarPool();
                if (!RunSingle(ctx, p, groups[i])) throw new InvalidOperationException($"高度 ROI[{i}] 没有有效测量结果");
                foreach (var kv in ctx.Vars.All)
                {
                    if (kv.Key == "Warning") { warning |= Equals(kv.Value, true); continue; }
                    if (kv.Key == "Message") { message = Convert.ToString(kv.Value); continue; }
                    if (!arrays.TryGetValue(kv.Key, out var values)) arrays[kv.Key] = values = new();
                    values.Add(kv.Value);
                }
            }
            foreach (var kv in arrays) output.Set(kv.Key, kv.Value);
            if (warning) { output.Set("Warning", true); output.Set("Message", message); }
            return true;
        }
        finally { ctx.Vars = output; }
    }

    private bool RunSingle(OpContext ctx, ParamBag p, List<int> idx)
    {
        var c = ctx.Clouds.Get(p.Int("IM In", 0));
        if (c == null) { ctx.Log($"{Prefix}：输入为空", true); return false; }

        var plane = p.Bound("Reference Plane") as PlaneObj ?? p.Bound("Basic Plane") as PlaneObj;
        if (plane == null)
        {
            var r = p.Group("Reference");
            if (r != null) plane = new PlaneObj { NX = 0, NY = 0, NZ = 1, D = -C3.N(r, "Z", 0), CZ = C3.N(r, "Z", 0) };
        }
        plane ??= new PlaneObj { NZ = 1, D = 0 };
        var basic=p.Raw("Basic Plane") as IReadOnlyDictionary<string,object>;
        if(basic!=null)
        {
            basic=C3.Sub(basic,"plane")??basic;
            var position=MeasureVector3.Read(basic,"Position");var normal=MeasureVector3.Read(basic,"Normal").Unit();
            plane=new PlaneObj{NX=normal.X,NY=normal.Y,NZ=normal.Z,D=-normal.Dot(position),CX=position.X,CY=position.Y,CZ=position.Z};
        }

        if (idx.Count == 0) { ctx.Log($"{Prefix}：Region 内无点", true); return false; }

        double min = double.MaxValue, max = double.MinValue, sum = 0;
        var values=new List<(int Index,double Height)>(idx.Count);
        var normalLength=Math.Sqrt(plane.NX*plane.NX+plane.NY*plane.NY+plane.NZ*plane.NZ);
        if(!double.IsFinite(normalLength)||normalLength<=0)throw new ArgumentException("高度基准平面法向无效");
        foreach (var i in idx)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            var h = (plane.NX*c.X[i]+plane.NY*c.Y[i]+plane.NZ*c.Z[i]+plane.D)/normalLength;
            values.Add((i,h));
            if (h < min) min = h;
            if (h > max) max = h;
            sum += h;
        }
        var mean = sum / idx.Count;
        double var = 0;
        foreach (var point in values)
        {
            var h = point.Height;
            var += (h - mean) * (h - mean);
        }
        var std = Math.Sqrt(var / idx.Count);

        ctx.Vars.Set($"{Prefix}.Min", min);
        ctx.Vars.Set($"{Prefix}.Max", max);
        ctx.Vars.Set($"{Prefix}.Mean", mean);
        ctx.Vars.Set($"{Prefix}.Std", std);
        ctx.Vars.Set($"{Prefix}.Range", max - min);
        ctx.Vars.Set("h", max);
        ctx.Vars.Set($"{Prefix}.Count", idx.Count);
        if(_driver=="3D Height")
        {
            var ordered=values.OrderBy(v=>v.Height).ToList();
            double Quantile(IReadOnlyList<(int Index,double Height)> data,double percentile)
            {
                if(!double.IsFinite(percentile)||percentile<0||percentile>100)throw new ArgumentException("高度百分位必须在 0..100 范围内");
                var at=(data.Count-1)*percentile/100;var low=(int)Math.Floor(at);var high=(int)Math.Ceiling(at);
                return data[low].Height+(data[high].Height-data[low].Height)*(at-low);
            }
            var removal=p.Str("Outlier removal method","None");
            if(removal=="Percentile trimming")
            {
                var trim=p.Group("Percentile trimming");var low=C3.N(trim,"Percent to trim from bottom",10);var high=C3.N(trim,"Percent to trim from top",10);
                if(low<0||high<0||low>50||high>50||low+high>=100||!double.IsFinite(low)||!double.IsFinite(high))throw new ArgumentException("高度上下剔除百分比无效");
                var a=Quantile(ordered,low);var b=Quantile(ordered,100-high);ordered=ordered.Where(v=>v.Height>=a&&v.Height<=b).ToList();
                if(ordered.Count==0)throw new ArgumentException("高度剔除后没有有效测量点");
            }
            else if(removal!="None")throw new ArgumentException("无效的高度离群剔除方式："+removal);
            void Result(int number,string mode)
            {
                if(mode=="none")return;
                var value=mode switch{"Minimum"=>ordered[0].Height,"Maximum"=>ordered[^1].Height,"Mean"=>ordered.Average(v=>v.Height),"Median"=>Quantile(ordered,50),"Percentile"=>Quantile(ordered,p.Dbl("Percentile"+number,10)),_=>throw new ArgumentException("无效的高度结果模式："+mode)};
                var closest=ordered.MinBy(v=>Math.Abs(v.Height-value)).Index;
                var point=new Dictionary<string,object>{{"X",(double)c.X[closest]},{"Y",(double)c.Y[closest]},{"Z",(double)c.Z[closest]}};
                ctx.Vars.Set($"height_{number}_value",value);ctx.Vars.Set($"height_{number}_position_point",point);
                if(p.Bool("Height Text"+number))ctx.Texts.Add(new(){X=c.X[closest],Y=c.Y[closest],Z=c.Z[closest],Text=value.ToString("0.000",CultureInfo.InvariantCulture),Color="#00FF00",Size=14,SizeIsPixels=true,ViewIndex=p.Int("IM In")});
                if(p.Bool("Height Position"+number))ctx.NativeOverlays.Add(new(){Type="point3d",ViewIndex=p.Int("IM In"),Values=point});
            }
            Result(1,p.Str("First result mode","Maximum"));Result(2,p.Str("Second result mode","none"));
            if(p.Has("First result mode")){ctx.Vars.Set("Warning",true);ctx.Vars.Set("Message","自有高度统计的百分位插值和定位点仍待原版数值校准");}
        }
        ctx.Log($"{Prefix}：{idx.Count} 点，Min {min:0.000} Max {max:0.000} 均值 {mean:0.000} 极差 {max - min:0.000}（σ {std:0.000}）", false);
        return true;
    }

    private string Prefix => _driver switch
    {
        "3D RectProbe" => "RectProbe",
        "3D Height" => "Height",
        _ => _driver.Replace("3D ", "")
    };
}

/// <summary>3D 滤波：Percentile / Median / Gaussian / DownSample / OutlierRemoval。</summary>
public class CloudFilterOp : IOperator
{
    public string Driver { get; }

    public CloudFilterOp(string driver) => Driver = driver;

    public bool Run(OpContext ctx, ParamBag p)
    {
        var c = ctx.Clouds.Get(p.Int("IM In", 0));
        if (c == null || c.Count == 0) { ctx.Log($"{Driver}：输入为空", true); return false; }

        var outCloud = new PointCloud();
        switch (Driver)
        {
            case "3D PercentileFilter":
            {
                var lo = P.D(p, 10, "Low Percentile", "Lower Percentile", "Percentile Low", "Min Percentile");
                // 原版 High Percentile 表示从顶部剔除的比例；旧版 Upper Percentile 是上分位。
                var hi = p.Has("High Percentile") ? 100 - p.Dbl("High Percentile", 10)
                    : P.D(p, 90, "Upper Percentile", "Percentile High", "Max Percentile");
                if (!double.IsFinite(lo) || !double.IsFinite(hi) || lo < 0 || hi > 100 || lo >= hi)
                { ctx.Log("3D 分位滤波：分位范围无效", true); return false; }
                var axis = p.Str("Filter Axis", "Z").ToUpperInvariant();
                var coordinates = axis switch { "X" => c.X, "Y" => c.Y, "Z" => c.Z, _ => null };
                if (coordinates == null) { ctx.Log($"3D 分位滤波：无效坐标轴 {axis}", true); return false; }
                var sorted = coordinates.OrderBy(value => value).ToArray();
                double Quantile(double percent)
                {
                    var position = (sorted.Length - 1) * percent / 100;
                    var lower = (int)Math.Floor(position);
                    var upper = (int)Math.Ceiling(position);
                    return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
                }
                var a = Quantile(lo);
                var b = Quantile(hi);
                for (int i = 0; i < c.Count; i++)
                    if (coordinates[i] >= a && coordinates[i] <= b) outCloud.Add(c.X[i], c.Y[i], c.Z[i], c.I[i]);
                break;
            }
            case "3D MedianFilter":
            case "3D GaussianFilter":
            {
                // 按 XY 网格分箱，取箱内中值/均值做平滑
                var size = Math.Max(0.05, P.D(p, 0.5, "Grid Size", "Radius", "Filter Size"));
                var bins = new Dictionary<(int, int), List<int>>();
                for (int i = 0; i < c.Count; i++)
                {
                    var k = ((int)Math.Floor(c.X[i] / size), (int)Math.Floor(c.Y[i] / size));
                    if (!bins.TryGetValue(k, out var l)) bins[k] = l = new List<int>();
                    l.Add(i);
                }
                for (int i = 0; i < c.Count; i++)
                {
                    var k = ((int)Math.Floor(c.X[i] / size), (int)Math.Floor(c.Y[i] / size));
                    var list = bins[k];
                    double z;
                    if (Driver == "3D MedianFilter")
                    {
                        var s = list.Select(j => (double)c.Z[j]).OrderBy(x => x).ToList();
                        z = s[s.Count / 2];
                    }
                    else z = list.Average(j => (double)c.Z[j]);
                    outCloud.Add(c.X[i], c.Y[i], (float)z, c.I[i]);
                }
                break;
            }
            case "3D DownSampleFilter":
            {
                var method = p.Str("Method", "Voxel Grid");
                if (!method.Equals("Voxel Grid", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Log($"3D 下采样：尚不支持方法 {method}", true);
                    return false;
                }
                var voxel = p.Group("Voxel Grid");
                var sx = C3.N(voxel, "Leaf Size X", 0.1);
                var sy = C3.N(voxel, "Leaf Size Y", 0.1);
                var sz = C3.N(voxel, "Leaf Size Z", 0.1);
                var minimum = Math.Max(1, (int)C3.N(voxel, "Minimum Points In Per Voxel", 0));
                if (!double.IsFinite(sx) || !double.IsFinite(sy) || !double.IsFinite(sz) || sx <= 0 || sy <= 0 || sz <= 0)
                {
                    ctx.Log("3D 下采样：体素各轴尺寸必须大于 0", true);
                    return false;
                }
                var bins = new Dictionary<(double, double, double), (double x, double y, double z, double intensity, int count)>();
                for (int i = 0; i < c.Count; i++)
                {
                    var key = (Math.Floor(c.X[i] / sx), Math.Floor(c.Y[i] / sy), Math.Floor(c.Z[i] / sz));
                    bins.TryGetValue(key, out var bin);
                    bins[key] = (bin.x + c.X[i], bin.y + c.Y[i], bin.z + c.Z[i], bin.intensity + c.I[i], bin.count + 1);
                }
                foreach (var bin in bins.Values)
                    if (bin.count >= minimum)
                        outCloud.Add((float)(bin.x / bin.count), (float)(bin.y / bin.count),
                            (float)(bin.z / bin.count), (float)(bin.intensity / bin.count));
                break;
            }
            case "3D OutlierRemovalFilter":
            {
                outCloud = CloudOutlierRemoval.Run(ctx, p, c);
                break;
            }
            case "3D HoleFillingFilter":
            {
                ctx.Log("3D 补洞尚未实现，未生成输出点云", true);
                return false;
            }
            default:
                ctx.Log($"不支持的点云滤波算子：{Driver}", true);
                return false;
        }

        ctx.Clouds.Set(p.Int("IM Out", 0), outCloud);
        ctx.Log($"{Driver}：{c.Count} -> {outCloud.Count} 点", false);
        return outCloud.Count > 0;
    }
}

/// <summary>3D 区域：合并 Region1..N，输出区域数组供后续算子绑定。</summary>
public class Region3DOp : IOperator
{
    public string Driver => "3D Region";

    public bool Run(OpContext ctx, ParamBag p)
    {
        var input = p.Int("IM In", 0);
        var cloud = ctx.Clouds.Get(input) ?? throw new InvalidOperationException("3D Region: input cloud is missing");
        var groups = new List<HashSet<int>>();
        var reference = p.Group("Reference");
        for (int i = 1; i <= 8; i++)
        {
            var group = p.Group("Region" + i);
            if (group == null || !group.TryGetValue("Enable", out var enabled) || enabled is not true) continue;
            if (!group.TryGetValue("Region", out var region)) throw new InvalidOperationException("3D Region: enabled region is missing");
            groups.Add(CloudRegionSelector.SelectGroups(cloud, region, ctx, reference).SelectMany(indices => indices).ToHashSet());
        }
        if (groups.Count == 0) throw new InvalidOperationException("3D Region: no region is enabled");
        var result = new HashSet<int>(groups[0]);
        var mode = p.Str("Mode", "add").ToLowerInvariant();
        if (mode is not ("add" or "intersection" or "subtract")) throw new NotSupportedException($"3D Region: unsupported mode {mode}");
        foreach (var group in groups.Skip(1))
            switch (mode)
            {
                case "add": result.UnionWith(group); break;
                case "intersection": result.IntersectWith(group); break;
                case "subtract": result.ExceptWith(group); break;
            }
        var output = new PointSet3D(cloud, result);
        ctx.Clouds.SetPointSets(input, new[] { output });
        ctx.Vars.Set("Region", output);
        ctx.Vars.Set("Region.Count", output.Count);
        ctx.Log($"3D Region: {groups.Count} inputs, {mode}, {output.Count} points", false);
        return true;
    }

}
