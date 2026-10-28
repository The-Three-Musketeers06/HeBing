using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>参数取值（多候选名，兼容不同版本的参数命名）。</summary>
internal static class P
{
    public static int I(ParamBag p, int def, params string[] names)
    {
        foreach (var n in names)
            if (p.Has(n)) return p.Int(n, def);
        return def;
    }

    public static double D(ParamBag p, double def, params string[] names)
    {
        foreach (var n in names)
            if (p.Has(n)) return p.Dbl(n, def);
        return def;
    }

    public static bool B(ParamBag p, bool def, params string[] names)
    {
        foreach (var n in names)
            if (p.Has(n)) return p.Bool(n, def);
        return def;
    }

    public static string S(ParamBag p, string def, params string[] names)
    {
        foreach (var n in names)
            if (p.Has(n)) return p.Str(n, def);
        return def;
    }
}

/// <summary>单输入单输出算子基类（IM In -> IM Out）。</summary>
public abstract class UnaryOp : IOperator
{
    public abstract string Driver { get; }
    protected abstract Mat Process(OpContext ctx, ParamBag p, Mat src);

    public bool Run(OpContext ctx, ParamBag p)
    {
        var src = ctx.Images.Get(p.Int("IM In", 0));
        if (src == null) { ctx.Log($"{Driver}：输入图像为空", true); return false; }
        Mat dst = null;
        try { dst = Process(ctx, p, src); }
        catch (Exception ex) { ctx.Log($"{Driver} 失败：{ex.Message}", true); return false; }
        if (dst == null) { ctx.Log($"{Driver}：无输出", true); return false; }
        ctx.SetImage(p.Int("IM Out", 1), dst);
        return true;
    }

    protected static Mat Gray8(Mat src) => Img.ToGray8(src);

    protected static MorphShapes KernelShape(string name) => name?.ToLowerInvariant() switch
    {
        "ellipse" or "disk" or "circle" => MorphShapes.Ellipse,
        "cross" => MorphShapes.Cross,
        _ => MorphShapes.Rect
    };

    protected static InterpolationFlags Interp(string name) => name?.ToLowerInvariant() switch
    {
        "nearest" => InterpolationFlags.Nearest,
        "area" => InterpolationFlags.Area,
        "cubic" => InterpolationFlags.Cubic,
        _ => InterpolationFlags.Linear
    };
}

// ---------------- 阈值类 ----------------

public class AutoThresholdOp : UnaryOp
{
    public override string Driver => "Basic Auto Threshold";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        using var g = Gray8(src);
        var dst = new Mat();
        Cv2.Threshold(g, dst, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        if (P.S(p, "White", "Thresh Type").StartsWith("Black", StringComparison.OrdinalIgnoreCase))
            Cv2.BitwiseNot(dst, dst);
        ctx.Vars.Set("AutoThreshold.Value", Cv2.Threshold(g, new Mat(), 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu));
        return dst;
    }
}

public class AdaptiveThresholdOp : UnaryOp
{
    public override string Driver => "Basic Adaptive Threshold";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        using var g = Gray8(src);
        var kw = Math.Max(3, P.I(p, 15, "Kernel Width"));
        var kh = Math.Max(3, P.I(p, 15, "Kernel Height"));
        if (kw % 2 == 0) kw++;
        if (kh % 2 == 0) kh++;
        var method = P.S(p, "Gaussian", "Method").StartsWith("Mean", StringComparison.OrdinalIgnoreCase)
            ? AdaptiveThresholdTypes.MeanC : AdaptiveThresholdTypes.GaussianC;
        var dst = new Mat();
        Cv2.AdaptiveThreshold(g, dst, 255, method, ThresholdTypes.Binary, kw, -P.D(p, 10, "Threshold Offset"));
        if (P.S(p, "White", "Thresh Type").StartsWith("Black", StringComparison.OrdinalIgnoreCase))
            Cv2.BitwiseNot(dst, dst);
        return dst;
    }
}

public class SauvolaThresholdOp : UnaryOp
{
    public override string Driver => "Basic Sauvola Threshold";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        using var g = Gray8(src);
        int w = Math.Max(3, P.I(p, 15, "Kernel Size", "Kernel Width", "Window Size"));
        double k = P.D(p, 0.2, "K", "K Value");
        double r = P.D(p, 128, "R", "Dynamic Range");

        using var grayF = new Mat();
        g.ConvertTo(grayF, MatType.CV_32F);
        using var mean = new Mat();
        using var sq = new Mat();
        using var meanSq = new Mat();
        Cv2.Blur(grayF, mean, new Size(w, w));
        Cv2.Multiply(grayF, grayF, sq, 1.0);
        Cv2.Blur(sq, meanSq, new Size(w, w));

        using var varMat = new Mat();
        using var std = new Mat();
        Cv2.Subtract(meanSq, mean.Mul(mean).ToMat(), varMat);
        Cv2.Sqrt(varMat, std);

        using var t = new Mat();
        using var tmp = new Mat();
        Cv2.Multiply(std, 1.0 / r, tmp);
        Cv2.Subtract(tmp, new Scalar(1), tmp);
        Cv2.Multiply(tmp, k, tmp);
        Cv2.Add(tmp, new Scalar(1), tmp);
        Cv2.Multiply(mean, tmp, t);

        var dst = new Mat();
        Cv2.Compare(grayF, t, dst, CmpTypes.GT);
        var out8 = new Mat();
        dst.ConvertTo(out8, MatType.CV_8U);
        return out8;
    }
}

// ---------------- 滤波 / 增强 ----------------

public class BilateralFilterOp : UnaryOp
{
    public override string Driver => "Basic Bilateral Filter";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        var d = Math.Max(1, P.I(p, 9, "Diameter", "Kernel Size", "Kernel X"));
        var sigma = P.D(p, 75, "Sigma Color", "Sigma");
        var sigmaSpace = P.D(p, 75, "Sigma Space", "Sigma");
        var dst = new Mat();
        Cv2.BilateralFilter(src, dst, d, sigma, sigmaSpace);
        return dst;
    }
}

public class ClaheOp : UnaryOp
{
    public override string Driver => "Basic Constant Time Clahe";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        using var g = Gray8(src);
        var clip = P.D(p, 0.1, "Clip Ratio", "Clip Limit");
        var radius = Math.Max(1, P.I(p, 100, "Radius") / 8);
        var clahe = Cv2.CreateCLAHE(clip * 10, new Size(radius, radius));
        var dst = new Mat();
        clahe.Apply(g, dst);
        return dst;
    }
}

public class ContrastOp : UnaryOp
{
    public override string Driver => "Basic Contrast";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        var k = P.D(p, 1.0, "K", "Contrast", "Factor");
        var dst = new Mat();
        src.ConvertTo(dst, -1, k, 0);
        return dst;
    }
}

public class GammaOp : UnaryOp
{
    public override string Driver => "Basic Gamma";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        var gamma = P.D(p, 1.0, "Gamma");
        if (gamma <= 0) gamma = 1;
        var dst = new Mat();
        using var f = new Mat();
        src.ConvertTo(f, MatType.CV_32F, 1.0 / 255.0);
        Cv2.Pow(f, gamma, f);
        f.ConvertTo(dst, src.Depth(), 255.0);
        return dst;
    }
}

public class HistogramEqualizeOp : UnaryOp
{
    public override string Driver => "Basic Histogram Equalize";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        using var g = Gray8(src);
        var dst = new Mat();
        Cv2.EqualizeHist(g, dst);
        return dst;
    }
}

public class HistogramStretchOp : UnaryOp
{
    public override string Driver => "Basic Histogram Stretch";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        using var g = Gray8(src);
        var lo = p.GroupDbl("Gray Range", "min", 0);
        var hi = p.GroupDbl("Gray Range", "max", 255);
        if (hi <= lo) hi = lo + 1;
        var dst = new Mat();
        g.ConvertTo(dst, MatType.CV_8U, 255.0 / (hi - lo), -lo * 255.0 / (hi - lo));
        return dst;
    }
}

public class LaplacianFilterOp : UnaryOp
{
    public override string Driver => "Basic Laplacian Filter";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        using var g = Gray8(src);
        var k = Math.Max(1, P.I(p, 3, "Kernel Size", "Kernel X"));
        if (k % 2 == 0) k++;
        var dst = new Mat();
        Cv2.Laplacian(g, dst, MatType.CV_16S, k);
        var abs = new Mat();
        Cv2.ConvertScaleAbs(dst, abs);
        dst.Dispose();
        return abs;
    }
}

public class LinearTransformOp : UnaryOp
{
    public override string Driver => "Basic Linear Transform";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        var a = P.D(p, 1.0, "A", "Scale", "Alpha", "K");
        var b = P.D(p, 0.0, "B", "Offset", "Beta");
        var dst = new Mat();
        src.ConvertTo(dst, -1, a, b);
        return dst;
    }
}

public class MeanStdNormalizeOp : UnaryOp
{
    public override string Driver => "Basic Mean Std Normalize";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        using var g = Gray8(src);
        Cv2.MeanStdDev(g, out var mean, out var std);
        var dst = new Mat();
        var s = std.Val0 == 0 ? 1 : std.Val0;
        g.ConvertTo(dst, MatType.CV_32F);
        Cv2.Subtract(dst, mean, dst);
        Cv2.Divide(dst, s, dst);
        // 归一到 0..255
        Cv2.Normalize(dst, dst, 0, 255, NormTypes.MinMax);
        var out8 = new Mat();
        dst.ConvertTo(out8, MatType.CV_8U);
        dst.Dispose();
        return out8;
    }
}

public class PetwittFilterOp : UnaryOp
{
    public override string Driver => "Basic Prewitt Filter";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        using var g = Gray8(src);
        var kx = new Mat(3, 3, MatType.CV_32FC1);
        kx.SetArray(new float[] { -1, 0, 1, -1, 0, 1, -1, 0, 1 });
        var ky = new Mat(3, 3, MatType.CV_32FC1);
        ky.SetArray(new float[] { -1, -1, -1, 0, 0, 0, 1, 1, 1 });
        var dx = new Mat();
        var dy = new Mat();
        Cv2.Filter2D(g, dx, MatType.CV_16S, kx);
        Cv2.Filter2D(g, dy, MatType.CV_16S, ky);
        var dst = new Mat();
        var ax = new Mat();
        var ay = new Mat();
        Cv2.ConvertScaleAbs(dx, ax);
        Cv2.ConvertScaleAbs(dy, ay);
        Cv2.AddWeighted(ax, 1, ay, 1, 0, dst);
        kx.Dispose(); ky.Dispose(); dx.Dispose(); dy.Dispose(); ax.Dispose(); ay.Dispose();
        return dst;
    }
}

public class RobertsFilterOp : UnaryOp
{
    public override string Driver => "Basic Roberts Filter";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        using var g = Gray8(src);
        var kx = new Mat(2, 2, MatType.CV_32FC1);
        kx.SetArray(new float[] { 1, 0, 0, -1 });
        var ky = new Mat(2, 2, MatType.CV_32FC1);
        ky.SetArray(new float[] { 0, 1, -1, 0 });
        var dx = new Mat();
        var dy = new Mat();
        Cv2.Filter2D(g, dx, MatType.CV_16S, kx);
        Cv2.Filter2D(g, dy, MatType.CV_16S, ky);
        var ax = new Mat();
        var ay = new Mat();
        Cv2.ConvertScaleAbs(dx, ax);
        Cv2.ConvertScaleAbs(dy, ay);
        var dst = new Mat();
        Cv2.AddWeighted(ax, 1, ay, 1, 0, dst);
        kx.Dispose(); ky.Dispose(); dx.Dispose(); dy.Dispose(); ax.Dispose(); ay.Dispose();
        return dst;
    }
}

public class SharpenOp : UnaryOp
{
    public override string Driver => "Basic Sharpen";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        var amount = P.D(p, 1.0, "Amount", "Strength", "K");
        var f = (float)(4 + 1.0 / Math.Max(0.01, amount));
        var k = new Mat(3, 3, MatType.CV_32FC1);
        k.SetArray(new float[] { 0, -1, 0, -1, f, -1, 0, -1, 0 });
        var dst = new Mat();
        Cv2.Filter2D(src, dst, -1, k);
        k.Dispose();
        return dst;
    }
}

public class FlipOp : UnaryOp
{
    public override string Driver => "Basic Flip";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        var mode = P.S(p, "Horizontal", "Flip Type", "Direction", "Mode");
        var fm = mode.ToLowerInvariant() switch
        {
            "vertical" or "top-bottom" or "y" => FlipMode.Y,
            "both" or "xy" => FlipMode.XY,
            _ => FlipMode.X
        };
        var dst = new Mat();
        Cv2.Flip(src, dst, fm);
        return dst;
    }
}

public class ResizeOp : UnaryOp
{
    public override string Driver => "Basic Resize";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        var mode = P.S(p, "Scale", "Resize Mode");
        int w, h;
        if (mode.StartsWith("Size", StringComparison.OrdinalIgnoreCase))
        {
            w = Math.Max(1, P.I(p, src.Width, "Size X", "Width"));
            h = Math.Max(1, P.I(p, src.Height, "Size Y", "Height"));
        }
        else
        {
            var sx = P.D(p, 1.0, "Scale X");
            var sy = P.D(p, 1.0, "Scale Y");
            w = Math.Max(1, (int)(src.Width * sx));
            h = Math.Max(1, (int)(src.Height * sy));
        }
        var dst = new Mat();
        Cv2.Resize(src, dst, new Size(w, h), 0, 0, Interp(P.S(p, "Bilinear", "Interpolation")));
        return dst;
    }
}

public class RotateOp : UnaryOp
{
    public override string Driver => "Basic Rotate";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        var angle = P.D(p, 0, "Rotate Angle", "Angle");
        var useCenter = P.B(p, true, "Use Image Center");
        var cx = useCenter ? src.Width / 2.0 : P.D(p, 0, "Center X");
        var cy = useCenter ? src.Height / 2.0 : P.D(p, 0, "Center Y");
        var m = Cv2.GetRotationMatrix2D(new Point2f((float)cx, (float)cy), angle, 1.0);
        var dst = new Mat();
        Cv2.WarpAffine(src, dst, m, src.Size(), Interp(P.S(p, "Bilinear", "Interpolation")));
        return dst;
    }
}

public class MorphOp : UnaryOp
{
    private readonly string _driver;
    private readonly MorphTypes _type;

    public MorphOp(string driver, MorphTypes type) { _driver = driver; _type = type; }
    public override string Driver => _driver;

    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        var kx = Math.Max(1, P.I(p, 3, "Kernel Size X", "Kernel X", "Kernel Size"));
        var ky = Math.Max(1, P.I(p, 3, "Kernel Size Y", "Kernel Y", "Kernel Size"));
        var kernel = Cv2.GetStructuringElement(KernelShape(P.S(p, "Rect", "Kernel Type")), new Size(kx, ky));
        var dst = new Mat();
        Cv2.MorphologyEx(src, dst, _type, kernel);
        kernel.Dispose();
        return dst;
    }
}

public class PolarUnwrapOp : UnaryOp
{
    public override string Driver => "Basic Polar Unwrap";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        var cx = p.GroupDbl("Region", "Center X", src.Width / 2.0);
        var cy = p.GroupDbl("Region", "Center Y", src.Height / 2.0);
        var radius = P.D(p, Math.Min(src.Width, src.Height) / 2.0, "Radius", "Outer Radius");
        var dst = new Mat();
        Cv2.WarpPolar(src, dst, new Size(Math.Max(1, (int)radius), Math.Max(1, src.Height)),
            new Point2f((float)cx, (float)cy), radius, InterpolationFlags.Linear, WarpPolarMode.Linear);
        return dst;
    }
}

public class PolarRevertOp : UnaryOp
{
    public override string Driver => "Basic Polar Revert";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        var dst = new Mat();
        Cv2.WarpPolar(src, dst, src.Size(), new Point2f(src.Width / 2f, src.Height / 2f),
            Math.Min(src.Width, src.Height) / 2.0,
            InterpolationFlags.Linear | (InterpolationFlags)0x10, WarpPolarMode.Linear); // 0x10 = WARP_POLAR_INVERSE
        return dst;
    }
}

// ---------------- 颜色 / 位深 ----------------

public class DepthToGrayOp : UnaryOp
{
    public override string Driver => "Depth To Gray";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        var lo = p.GroupDbl("Threshold", "min", 0);
        var hi = p.GroupDbl("Threshold", "max", 65535);
        if (hi <= lo) hi = lo + 1;
        var dst = new Mat();
        src.ConvertTo(dst, MatType.CV_8U, 255.0 / (hi - lo), -lo * 255.0 / (hi - lo));
        return dst;
    }
}

public class Gray16To8Op : UnaryOp
{
    public override string Driver => "Basic 16Gray To 8Gray";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        if (src.Type() != MatType.CV_16UC1) throw new ArgumentException("输入必须是16位单通道无符号灰度图像（Gray16U）");
        var lo = p.GroupDbl("Threshold", "min", 0);
        var hi = p.GroupDbl("Threshold", "max", 65535);
        if (!double.IsFinite(lo) || !double.IsFinite(hi) || lo < 0 || hi > 65535 || hi <= lo)
            throw new ArgumentException("灰度范围必须满足 0 ≤ 最小值 < 最大值 ≤ 65535");
        var dst = new Mat();
        src.ConvertTo(dst, MatType.CV_8U, 255.0 / (hi - lo), -lo * 255.0 / (hi - lo));
        return dst;
    }
}

public class ColorConvertOp : IOperator
{
    public string Driver => "Basic Color Convert";
    public bool Run(OpContext context, ParamBag parameters) => HalconColorConvert.Run(context, parameters);
}

public class ChannelExtractOp : UnaryOp
{
    public override string Driver => "Basic Channel Extract";
    protected override Mat Process(OpContext ctx, ParamBag p, Mat src)
    {
        var idx = P.I(p, 0, "Channel", "Channel Index", "Index");
        var dst = new Mat();
        Cv2.ExtractChannel(src, dst, Math.Clamp(idx, 0, Math.Max(0, src.Channels() - 1)));
        return dst;
    }
}

/// <summary>通道分离（多输出）：RGB 或 HSV。</summary>
public class ChannelSplitOp : IOperator
{
    public string Driver => "Basic Channel Split";

    public bool Run(OpContext ctx, ParamBag p) => HalconChannelSplit.Run(ctx, p);
}
