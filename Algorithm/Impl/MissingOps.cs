using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

// ---------------- 补齐 2D 入门示例所需的算子 ----------------

/// <summary>裁剪图片（Crop Image）：按 Region 矩形裁剪。</summary>
public class CropImageOp : IOperator
{
    public string Driver => "Crop Image";

    public bool Run(OpContext ctx, ParamBag p)
    {
        var src = ctx.Images.Get(p.Int("IM In", 0));
        if (src == null) { ctx.Log("裁剪图片：输入为空", true); return false; }

        var (cx, cy, w, h) = ReadRect(p, "Region", src.Width / 2.0, src.Height / 2.0, src.Width, src.Height);
        var region = p.Group("Region");
        var shape = region?.GetValueOrDefault("rect2d") as IReadOnlyDictionary<string, object> ?? region;
        var rotation = DetectionOverlay2D.Number(shape, "Rotation");
        if (Math.Abs(rotation) > 1e-8 || p.Bool("Position Fix"))
        {
            if (!double.IsFinite(cx + cy + w + h + rotation) || w <= 0 || h <= 0)
                throw new ArgumentException("裁剪区域无效，请重新画选矩形");
            var radians = rotation * Math.PI / 180;
            var cosine = Math.Cos(radians); var sine = Math.Sin(radians);
            var corners = new[] { new Point2d(-w / 2, -h / 2), new(w / 2, -h / 2), new(w / 2, h / 2), new(-w / 2, h / 2) }
                .Select(q => new Point2d(cx + q.X * cosine - q.Y * sine, cy + q.X * sine + q.Y * cosine)).ToArray();
            if (p.Bool("Position Fix")) corners = BlobDetectOp.Transform(corners, p);
            Mat output;
            if (p.Bool("Rotate To Straight"))
            {
                var width = Math.Max(1, (int)Math.Round(Geo.Dist(corners[0], corners[1])));
                var height = Math.Max(1, (int)Math.Round(Geo.Dist(corners[1], corners[2])));
                if ((long)width * height > 200_000_000) throw new ArgumentException("裁剪区域过大");
                using var transform = Cv2.GetPerspectiveTransform(corners.Select(q => new Point2f((float)q.X, (float)q.Y)),
                    new[] { new Point2f(0, 0), new(width, 0), new(width, height), new(0, height) });
                output = new Mat();
                try { Cv2.WarpPerspective(src, output, transform, new Size(width, height), InterpolationFlags.Linear, BorderTypes.Constant); }
                catch { output.Dispose(); throw; }
            }
            else
            {
                var left = corners.Min(q => q.X); var right = corners.Max(q => q.X); var top = corners.Min(q => q.Y); var bottom = corners.Max(q => q.Y);
                if (!TryClipRect((left + right) / 2, (top + bottom) / 2, right - left, bottom - top, src.Width, src.Height, out var bounds))
                    throw new ArgumentException("裁剪区域与图像没有交集");
                output = new Mat(src, bounds).Clone();
            }
            ctx.SetImage(p.Int("IM Out", 1), output);
            ctx.Log($"裁剪图片：旋转区域 {output.Width}x{output.Height}", false);
            return true;
        }
        if (!TryClipRect(cx, cy, w, h, src.Width, src.Height, out var rect))
        {
            ctx.Log("裁剪图片：区域无效或与图像没有交集", true);
            return false;
        }

        var dst = new Mat(src, rect).Clone();
        ctx.SetImage(p.Int("IM Out", 1), dst);
        ctx.Log($"裁剪图片：({rect.X},{rect.Y}) {rect.Width}x{rect.Height}", false);
        return true;
    }

    internal static bool TryClipRect(double cx, double cy, double w, double h, int imageWidth, int imageHeight, out Rect rect)
    {
        rect = default;
        if (!double.IsFinite(cx) || !double.IsFinite(cy) || !double.IsFinite(w) || !double.IsFinite(h) ||
            w <= 0 || h <= 0 || imageWidth <= 0 || imageHeight <= 0) return false;
        var left = (int)Math.Clamp(Math.Round(cx - w / 2), 0, imageWidth);
        var top = (int)Math.Clamp(Math.Round(cy - h / 2), 0, imageHeight);
        var right = (int)Math.Clamp(Math.Round(cx + w / 2), 0, imageWidth);
        var bottom = (int)Math.Clamp(Math.Round(cy + h / 2), 0, imageHeight);
        if (right <= left || bottom <= top) return false;
        rect = new Rect(left, top, right - left, bottom - top);
        return true;
    }

    internal static (double cx, double cy, double w, double h) ReadRect(
        ParamBag p, string key, double dcx, double dcy, double dw, double dh)
    {
        var g = p.Group(key);
        if (g == null) return (dcx, dcy, dw, dh);

        static double N(IReadOnlyDictionary<string, object> d, string k, double def)
        {
            if (d == null || !d.TryGetValue(k, out var v) || v == null) return def;
            return double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : def;
        }

        IReadOnlyDictionary<string, object> Sub(IReadOnlyDictionary<string, object> d, string k)
            => d != null && d.TryGetValue(k, out var v) && v is IReadOnlyDictionary<string, object> s ? s : null;

        var inner = Sub(g, "rect2d") ?? g;
        if (g.TryGetValue("rect2d array", out var arr) && arr is List<object> list && list.Count > 0
            && list[0] is Dictionary<string, object> first)
            inner = first;
        var c = Sub(inner, "Center") ?? inner;
        var s = Sub(inner, "Size");
        return (N(c, "X", dcx), N(c, "Y", dcy), N(s, "Width", dw), N(s, "Height", dh));
    }
}

/// <summary>2D 数据显示：把绑定的变量按 data0..dataN 的位置叠加到图上。</summary>
public class DisplayDataOp : IOperator
{
    public string Driver => "2D Display Data";

    public bool Run(OpContext ctx, ParamBag p)
    {
        DetectionOverlay2D.Begin(ctx,Driver);
        var src = ctx.Images.Get(p.Int("IM In", 0));
        if (src == null) { ctx.Log("数据显示：输入为空", true); return false; }

        var output=p.Int("IM Out",p.Int("IM In"));
        var keys = p.StringList("_input_list");
        if (keys.Count == 0) keys = new List<string> { "data0", "data1", "data2", "data3" };

        static double N(IReadOnlyDictionary<string, object> d, string k, double def)
        {
            if (d == null || !d.TryGetValue(k, out var v) || v == null) return def;
            return double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : def;
        }

        var drawn = 0;
        foreach (var key in keys)
        {
            var g = p.Group(key);
            if (g == null) continue;
            if (g.TryGetValue("Display", out var dv) && dv is bool b && !b) continue;

            var content = p.Bound(key + ".Content");
            if (content == null && g.TryGetValue("Content", out var cv)) content = cv;
            if (content == null) continue;

            var decimals = (int)N(g, "Decimals", 3);
            var text = content is double d ? d.ToString("F" + Math.Clamp(decimals, 0, 8), CultureInfo.InvariantCulture)
                     : content.ToString();
            var prefix = (g.TryGetValue("Prefix", out var pf) ? pf?.ToString() : "")?.Trim('"') ?? "";
            var suffix = (g.TryGetValue("Suffix", out var sf) ? sf?.ToString() : "")?.Trim('"') ?? "";
            text = prefix + text + suffix;

            var x = (int)N(g, "X", 10);
            var y = (int)N(g, "Y", 10);
            var size = N(g, "Font Size", 12);
            DetectionOverlay2D.Add(ctx,p,Driver,"text2d",new(){{"Position",DetectionOverlay2D.XY(x,y)},{"Text",text}},key,view:output);
            drawn++;
        }

        if(output!=p.Int("IM In"))ctx.SetImage(output,src.Clone());
        ctx.Vars.Set("DisplayedCount",drawn);
        ctx.Log($"显示数据：{drawn} 项", false);
        return drawn > 0;
    }
}

/// <summary>Train a portable geometric contour template from the manually taught region.</summary>
public class ShapeModelOp : IOperator
{
    public string Driver => "Basic Shape Model";

    public bool Run(OpContext ctx, ParamBag p)
    {
        DetectionOverlay2D.Begin(ctx, Driver);
        ctx.Vars.Set("ShapeModel.File", ""); ctx.Vars.Set("ShapeModel.Width", 0); ctx.Vars.Set("ShapeModel.Height", 0);
        ctx.Vars.Set("ShapeModel.FeatureCount", 0); ctx.Vars.Set("Base Point", null);
        var embedded = p.Has(ContourTemplateStorage.ModelKey);
        ContourTemplateModel model;
        var file = "";
        if (embedded) model = ContourTemplateStorage.Read(p, ctx);
        else
        {
            var image = ctx.Images.Get(p.Int("IM In", 0));
            model = ContourTemplateEngine.Train(image, p, ctx.CancellationToken);
            var requested = P.S(p, "", "Model File", "File", "Save File");
            if (string.IsNullOrWhiteSpace(requested)) requested = "model.aiv2dmodel";
            file = ctx.ResolveOutputPath(requested);
            ctx.CancellationToken.ThrowIfCancellationRequested();
            ContourTemplateEngine.Save(file, model);
        }
        ctx.Vars.Set("ShapeModel.File", file); ctx.Vars.Set("ShapeModel.Width", model.Width); ctx.Vars.Set("ShapeModel.Height", model.Height);
        ctx.Vars.Set("ShapeModel.FeatureCount", model.Features.Count);
        ctx.Vars.Set("Base Point", ContourTemplateOutputs.Pose(model.OriginX, model.OriginY, model.OriginAngle, 1));
        if (p.Bool("Display Edge")) foreach (var contour in model.Contours)
            DetectionOverlay2D.Add(ctx, p, Driver, "trace2d", new() { ["Points"] = contour.Select(point => (object)DetectionOverlay2D.XY(point.X + model.OffsetX, point.Y + model.OffsetY)).ToArray() }, "Display Edge Attribute");
        if (p.Bool("Base Point")) DetectionOverlay2D.Point(ctx, p, Driver, model.OriginX, model.OriginY, "Base Point Display");
        ctx.Log($"轮廓模板已就绪：{model.Features.Count} 个特征，{model.Width}×{model.Height}", false);
        return true;
    }
}
/// <summary>2D 胶路检测：统计 Region 内的胶线宽度/连续性。</summary>
public class SealantOp : IOperator
{
    public string Driver => "2D Sealant";

    public bool Run(OpContext ctx, ParamBag p)
    {
        DetectionOverlay2D.Begin(ctx,Driver);
        var src = ctx.Images.Get(p.Int("IM In", 0));
        if (src == null) { ctx.Log("胶路检测：输入为空", true); return false; }
        if(p.Group("Trajectory Options")?.GetValueOrDefault("Points") is IEnumerable<object> path)
        {
            var points=path.OfType<IReadOnlyDictionary<string,object>>().Select(q=>new Point2d(DetectionOverlay2D.Number(q,"X"),DetectionOverlay2D.Number(q,"Y"))).ToArray();
            if(points.Length>=2)return SealantTrajectory2D.Run(ctx,p,src,points);
        }

        using var gray = Img.ToGray8(src);
        var lo = p.GroupDbl("Threshold Range", "min", 100);
        var hi = p.GroupDbl("Threshold Range", "max", 255);
        using var bin = new Mat();
        Cv2.InRange(gray, new Scalar(lo), new Scalar(hi), bin);

        var (cx, cy, w, h) = CropImageOp.ReadRect(p, "Region", gray.Width / 2.0, gray.Height / 2.0, gray.Width, gray.Height);
        if (!CropImageOp.TryClipRect(cx, cy, w, h, gray.Width, gray.Height, out var rect))
        {
            ctx.Log("胶路检测：区域无效或与图像没有交集", true);
            return false;
        }

        using var roi = new Mat(bin, rect);
        var roiWidth = roi.Width;
        var roiHeight = roi.Height;
        var cols = new int[roiWidth];
        for (int x = 0; x < roiWidth; x++)
        {
            var count = 0;
            for (int y = 0; y < roiHeight; y++)
                if (roi.At<byte>(y, x) > 0) count++;
            cols[x] = count;
        }
        var widths = cols.Where(c => c > 0).ToArray();
        var avg = widths.Length > 0 ? widths.Average() : 0;
        var gaps = cols.Count(c => c == 0);
        var ratio = roiWidth > 0 ? (double)widths.Length / roiWidth : 0;

        ctx.Vars.Set("Sealant.AverageWidth", avg);
        ctx.Vars.Set("Sealant.MaxWidth", widths.Length > 0 ? widths.Max() : 0);
        ctx.Vars.Set("Sealant.GapColumns", gaps);
        ctx.Vars.Set("Sealant.Coverage", ratio);
        if(p.Bool("Edge of Sealant"))
        {
            Cv2.FindContours(roi,out var contours,out _,RetrievalModes.List,ContourApproximationModes.ApproxSimple);
            foreach(var contour in contours.Where(c=>c.Length>1))DetectionOverlay2D.Add(ctx,p,Driver,"trace2d",new(){{"Points",contour.Select(q=>(object)DetectionOverlay2D.XY(q.X+rect.X,q.Y+rect.Y)).ToArray()}});
        }
        ctx.Log($"胶路检测：平均宽 {avg:0.0}px，最大 {ctx.Vars.GetDouble("Sealant.MaxWidth"):0}px，断胶列 {gaps}，覆盖 {ratio:P1}", false);
        return ratio > 0.5;
    }
}
