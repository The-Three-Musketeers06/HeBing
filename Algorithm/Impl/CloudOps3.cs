using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>3D 轮廓采集：按切面从点云提取一条剖面曲线。</summary>
public class ProfileCaptureOp : IOperator
{
    public string Driver => "3D ProfileCapture";

    public bool Run(OpContext ctx, ParamBag p)
    {
        var c = ctx.Clouds.Get(p.Int("IM In", 0));
        if (c == null || c.Count == 0) { ctx.Log("轮廓采集：输入为空", true); return false; }

        var cutPlane = p.Str("Cut Plane");
        var pos = p.Dbl("Profile Cut Position", 0);
        var width = Math.Max(1e-4, p.Dbl("Extract Width", 0.03));
        var res = Math.Max(1e-4, p.Dbl("Resolution", 0.1));

        // 提取剖面：固定一个轴，取带宽内点
        var pts = new List<(double u, double v, double w)>();
        for (int i = 0; i < c.Count; i++)
        {
            double x = c.X[i], y = c.Y[i], z = c.Z[i];
            if (cutPlane.StartsWith("XZ", StringComparison.OrdinalIgnoreCase))
            {
                if (Math.Abs(y - pos) > width / 2) continue;
                pts.Add((x, z, y));
            }
            else if (cutPlane.StartsWith("YZ", StringComparison.OrdinalIgnoreCase))
            {
                if (Math.Abs(x - pos) > width / 2) continue;
                pts.Add((y, z, x));
            }
            else
            {
                if (Math.Abs(z - pos) > width / 2) continue;
                pts.Add((x, y, z));
            }
        }
        if (pts.Count < 2) { ctx.Log($"轮廓采集：带宽内点 {pts.Count} 个，不足", true); return false; }

        // 按剖面位置分箱取均值（Resolution 决定分辨率）
        var ordered = pts.OrderBy(q => q.u).ToList();
        var bins = new List<(double u, double v)>();
        double binStart = ordered[0].u, sum = 0;
        int n = 0;
        foreach (var q in ordered)
        {
            if (q.u - binStart > res && n > 0)
            {
                bins.Add((binStart + (q.u - binStart) / 2, sum / n));
                binStart = q.u; sum = 0; n = 0;
            }
            sum += q.v; n++;
        }
        if (n > 0) bins.Add((binStart, sum / n));

        double vMin = bins.Min(b => b.v), vMax = bins.Max(b => b.v);
        double uMin = bins.Min(b => b.u), uMax = bins.Max(b => b.u);

        ctx.Vars.Set("Profile.Points", bins.Select(b => (object)new PointObj { X = b.u, Y = b.v }).ToList());
        ctx.Vars.Set("Profile.Count", bins.Count);
        ctx.Vars.Set("Profile.Min", vMin);
        ctx.Vars.Set("Profile.Max", vMax);
        ctx.Vars.Set("Profile.Range", vMax - vMin);

        // 渲染剖面曲线
        const int W = 800, H = 400, Pad = 30;
        using var img = new Mat(H, W, MatType.CV_8UC3, new Scalar(22, 22, 26));
        var spanU = Math.Max(1e-9, uMax - uMin);
        var spanV = Math.Max(1e-9, vMax - vMin);
        Point ToPx(double u, double v) => new(
            (int)(Pad + (u - uMin) / spanU * (W - 2 * Pad)),
            (int)(H - Pad - (v - vMin) / spanV * (H - 2 * Pad)));

        Cv2.Line(img, new Point(Pad, H - Pad), new Point(W - Pad, H - Pad), new Scalar(60, 60, 70), 1);
        Cv2.Line(img, new Point(Pad, Pad), new Point(Pad, H - Pad), new Scalar(60, 60, 70), 1);
        for (int i = 1; i < bins.Count; i++)
            Cv2.Line(img, ToPx(bins[i - 1].u, bins[i - 1].v), ToPx(bins[i].u, bins[i].v),
                new Scalar(90, 200, 255), 1, LineTypes.AntiAlias);
        Cv2.PutText(img, $"{cutPlane}  pos={pos:0.000}  pts={bins.Count}  range={vMax - vMin:0.000}",
            new Point(Pad, 20), HersheyFonts.HersheySimplex, 0.5, new Scalar(180, 180, 190), 1, LineTypes.AntiAlias);

        ctx.Images.Set(p.Int("IM Out", 2), img.Clone());
        ctx.Log($"轮廓采集：{cutPlane} 位置 {pos:0.000}，{bins.Count} 点，高度 {vMin:0.000}~{vMax:0.000}", false);
        return true;
    }
}

/// <summary>3D 几何构造（点/线/平面组合）。</summary>
public class GeometryCreate3DOp : IOperator
{
    public string Driver => "3D GeometryCreate";
    public bool Run(OpContext ctx, ParamBag p) => GeometryCompletion.Create(ctx, p);
}
