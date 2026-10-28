using System;
using System.IO;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>把任意输入图统一成 8 位灰度（供多数 2D 算子使用）。</summary>
internal static class Img
{
    public static Mat ToGray8(Mat src)
    {
        if (src == null) return null;
        using var gray = new Mat();
        var input = src;
        if (src.Channels() == 3 || src.Channels() == 4)
        {
            Cv2.CvtColor(src, gray, src.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
            input = gray;
        }
        else if (src.Channels() != 1) throw new ArgumentException("不支持的图像通道数：" + src.Channels());
        var dst = new Mat();
        if (input.Depth() == MatType.CV_16U)
        {
            input.ConvertTo(dst, MatType.CV_8U, 255.0 / 65535.0);
            return dst;
        }
        if (input.Depth() != MatType.CV_8U)
        {
            input.ConvertTo(dst, MatType.CV_8U);
            return dst;
        }
        input.CopyTo(dst);
        return dst;
    }
}

/// <summary>加载图片（LoadImage）：IM Out、Type=File/Directory、File。</summary>
public class LoadImageOp : IOperator
{
    public string Driver => "LoadImage";

    public bool Run(OpContext ctx, ParamBag p)
    {
        InputFileSelector.Selection input;
        try{input=InputFileSelector.Select(ctx,p,Driver,".bmp",".jpeg",".jpg",".jpe",".png",".tiff",".tif");}
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException)
        {ctx.Log("加载图片失败："+error.Message,true);return false;}
        var path=input.Path;

        // Preserve depth, grayscale and alpha. Forcing Color silently destroys 16-bit
        // TIFF depth values before any downstream measurement can read them.
        var mat = Cv2.ImRead(path, ImreadModes.Unchanged);
        if (mat.Empty())
        {
            ctx.Log($"加载图片失败：无法解码 {path}", true);
            mat.Dispose();
            return false;
        }
        var slot = p.Int("IM Out", 0);
        ctx.SetImage(slot, mat);
        ctx.Clouds.Remove(slot);ctx.Profiles.Remove(slot);
        input.Commit(ctx);
        ctx.Vars.Set("Filename",Path.GetFileName(path));
        ctx.Vars.Set("Fullpath",Path.GetFullPath(path));
        ctx.Vars.Set("IM Out.Width", mat.Width);
        ctx.Vars.Set("IM Out.Height", mat.Height);
        ctx.Log($"加载图片 {Path.GetFileName(path)} ({mat.Width}x{mat.Height}) -> IM{slot}", false);
        return true;
    }
}

/// <summary>保存图片（SaveImage）。</summary>
public class SaveImageOp : IOperator
{
    public string Driver => "SaveImage";

    public bool Run(OpContext ctx, ParamBag p)
    {
        var slot = p.Int("IM In", 0);
        var img = ctx.Images.Get(slot);
        if (img == null) { ctx.Log($"保存图片失败：IM{slot} 为空", true); return false; }
        var file = ctx.ResolveOutputPath(p.Str("File"));
        if (string.IsNullOrEmpty(file)) { ctx.Log("保存图片失败：未指定文件", true); return false; }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        Cv2.ImWrite(file, img);
        ctx.Log($"保存图片 {Path.GetFileName(file)}", false);
        return true;
    }
}

/// <summary>阈值二值化（Basic Threshold Binarization）：Threshold Range / Thresh Type / Fill Holes。</summary>
public class ThresholdOp : IOperator
{
    public string Driver => "Basic Threshold Binarization";

    public bool Run(OpContext ctx, ParamBag p)
    {
        var src = ctx.Images.Get(p.Int("IM In", 0));
        if (src == null) { ctx.Log("阈值二值化：输入图像为空", true); return false; }
        var lo = p.GroupDbl("Threshold Range", "min", 100);
        var hi = p.GroupDbl("Threshold Range", "max", 255);
        var white = !string.Equals(p.Str("Thresh Type", "White"), "Black", StringComparison.OrdinalIgnoreCase);

        using var gray = Img.ToGray8(src);
        var dst = new Mat();
        Cv2.InRange(gray, new Scalar(lo), new Scalar(hi), dst);
        if (!white) Cv2.BitwiseNot(dst, dst);
        if (p.Bool("Fill Holes"))
        {
            var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            Cv2.MorphologyEx(dst, dst, MorphTypes.Close, kernel);
            kernel.Dispose();
        }
        ctx.SetImage(p.Int("IM Out", 1), dst);
        var nonZero = Cv2.CountNonZero(dst);
        ctx.Vars.Set("Threshold.WhitePixels", nonZero);
        ctx.Log($"阈值二值化 [{lo}-{hi}] 白像素 {nonZero}", false);
        return true;
    }
}

/// <summary>均值滤波（Basic Mean Filter）。</summary>
public class MeanFilterOp : IOperator
{
    public string Driver => "Basic Mean Filter";

    public bool Run(OpContext ctx, ParamBag p)
    {
        var src = ctx.Images.Get(p.Int("IM In", 0));
        if (src == null) { ctx.Log("均值滤波：输入为空", true); return false; }
        var k = Math.Max(1, p.Int("Kernel Size", 3));
        if (k % 2 == 0) k++;
        var dst = new Mat();
        Cv2.Blur(src, dst, new Size(k, k));
        ctx.SetImage(p.Int("IM Out", 1), dst);
        ctx.Log($"均值滤波 {k}x{k}", false);
        return true;
    }
}

/// <summary>高斯滤波（Basic Gaussian Filter）。</summary>
public class GaussianFilterOp : IOperator
{
    public string Driver => "Basic Gaussian Filter";

    public bool Run(OpContext ctx, ParamBag p)
    {
        var src = ctx.Images.Get(p.Int("IM In", 0));
        if (src == null) { ctx.Log("高斯滤波：输入为空", true); return false; }
        var k = Math.Max(1, p.Int("Kernel Size", 3));
        if (k % 2 == 0) k++;
        var dst = new Mat();
        Cv2.GaussianBlur(src, dst, new Size(k, k), 0);
        ctx.SetImage(p.Int("IM Out", 1), dst);
        ctx.Log($"高斯滤波 {k}x{k}", false);
        return true;
    }
}

/// <summary>中值滤波（Basic Median Filter）。</summary>
public class MedianFilterOp : IOperator
{
    public string Driver => "Basic Median Filter";

    public bool Run(OpContext ctx, ParamBag p)
    {
        var src = ctx.Images.Get(p.Int("IM In", 0));
        if (src == null) { ctx.Log("中值滤波：输入为空", true); return false; }
        var k = Math.Max(1, p.Int("Kernel Size", 3));
        if (k % 2 == 0) k++;
        var dst = new Mat();
        Cv2.MedianBlur(src, dst, k);
        ctx.SetImage(p.Int("IM Out", 1), dst);
        ctx.Log($"中值滤波 {k}x{k}", false);
        return true;
    }
}

/// <summary>Sobel 滤波（Basic Sobel Filter）。</summary>
public class SobelFilterOp : IOperator
{
    public string Driver => "Basic Sobel Filter";

    public bool Run(OpContext ctx, ParamBag p)
    {
        var src = ctx.Images.Get(p.Int("IM In", 0));
        if (src == null) { ctx.Log("Sobel：输入为空", true); return false; }
        using var gray = Img.ToGray8(src);
        var dst = new Mat();
        Cv2.Sobel(gray, dst, MatType.CV_16S, 1, 0);
        var abs = new Mat();
        Cv2.ConvertScaleAbs(dst, abs);
        dst.Dispose();
        ctx.SetImage(p.Int("IM Out", 1), abs);
        ctx.Log("Sobel 滤波", false);
        return true;
    }
}
