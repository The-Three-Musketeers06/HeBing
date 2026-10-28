using AiVisionStudio.Models;
using HalconDotNet;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

internal static class HalconChannelSplit
{
    public static bool Run(OpContext context, ParamBag parameters)
    {
        context.Vars.Clear();
        context.CancellationToken.ThrowIfCancellationRequested();
        var source = context.Images.Get(parameters.Int("IM In", 0));
        if (source == null || source.IsDisposed || source.Empty()) throw new ArgumentException("输入图像为空");
        if (source.Channels() is not (3 or 4)) throw new ArgumentException("RGB/HSV拆分需要三通道彩色图像或四通道彩色图像，单通道灰度图不能拆分彩色通道");
        if (source.Depth() != MatType.CV_8U && source.Depth() != MatType.CV_16U && source.Depth() != MatType.CV_32F)
            throw new ArgumentException("通道拆分支持8位无符号、16位无符号或32位浮点彩色图像");
        string mode = parameters.Str("Split Mode", "RGB").Trim().ToUpperInvariant();
        var names = ChannelSplitChannels.Names(mode);
        var slots = names.Select((name, index) => parameters.Int("IM Out " + name, index + 1)).ToArray();
        if (slots.Any(slot => slot < 0)) throw new ArgumentException("输出图像编号不能为负数");
        if (slots.Distinct().Count() != 3) throw new ArgumentException("三个通道的输出图像编号必须互不相同");

        int inputChannels = source.Channels();
        int inputDepth = source.Depth();
        _ = HalconRuntime.Version;
        using var image = CreateImage(source);
        using var red = image.Decompose3(out HImage green, out HImage blue);
        using (green)
        using (blue)
        {
            var outputs = new Mat[3];
            try
            {
                if (mode == "HSV")
                {
                    using var hue = red.TransFromRgb(green, blue, out HImage saturation, out HImage value, "hsv");
                    using (saturation)
                    using (value)
                    { outputs[0] = Copy(hue); outputs[1] = Copy(saturation); outputs[2] = Copy(value); }
                }
                else
                { outputs[0] = Copy(red); outputs[1] = Copy(green); outputs[2] = Copy(blue); }
                context.CancellationToken.ThrowIfCancellationRequested();
                for (int i = 0; i < outputs.Length; i++)
                { context.SetImage(slots[i], outputs[i]); outputs[i] = null; }
            }
            finally { foreach (var output in outputs) output?.Dispose(); }
        }
        context.Vars.Set("Split Mode", mode);
        context.Vars.Set("Input Channel Count", inputChannels);
        context.Vars.Set("Channel Count", 3);
        for (int i = 0; i < names.Count; i++) context.Vars.Set("IM Out " + names[i], slots[i]);
        if (mode == "HSV") context.Vars.Set("Hue Range", inputDepth == MatType.CV_8U ? "0～255" : inputDepth == MatType.CV_16U ? "0～21600（角分，不含21600）" : "0～2π（弧度，不含2π）");
        context.Log($"通道拆分 {mode}：已输出三个通道", false);
        return true;
    }

    internal static HImage CreateImage(Mat source)
    {
        var image = new HImage();
        try
        {
            using var contiguous = source.Clone();
            if (source.Depth() == MatType.CV_32F)
            {
                var planes = Cv2.Split(contiguous);
                try { image.GenImage3("real", source.Width, source.Height, planes[2].Data, planes[1].Data, planes[0].Data); }
                finally { foreach (var plane in planes) plane.Dispose(); }
            }
            else
            {
                bool wide = source.Depth() == MatType.CV_16U;
                string format = wide ? source.Channels() == 4 ? "bgrx64" : "bgr48" : source.Channels() == 4 ? "bgrx" : "bgr";
                image.GenImageInterleaved(contiguous.Data, format, source.Width, source.Height, 1, wide ? "uint2" : "byte", source.Width, source.Height, 0, 0, wide ? 16 : 8, 0);
            }
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    internal static Mat Copy(HImage image)
    {
        var pointer = image.GetImagePointer1(out string type, out int width, out int height);
        var matType = type switch
        {
            "byte" => MatType.CV_8UC1,
            "uint2" => MatType.CV_16UC1,
            "real" => MatType.CV_32FC1,
            _ => throw new InvalidOperationException("不支持的HALCON通道类型：" + type)
        };
        using var borrowed = Mat.FromPixelData(height, width, matType, pointer);
        return borrowed.Clone();
    }
}
