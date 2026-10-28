using HalconDotNet;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

internal static class HalconColorConvert
{
    public static bool Run(OpContext context, ParamBag parameters)
    {
        context.Vars.Clear();
        context.CancellationToken.ThrowIfCancellationRequested();
        try
        {
            var source = context.Images.Get(parameters.Int("IM In", 0));
            if (source == null || source.IsDisposed || source.Empty()) throw new ArgumentException("没有输入图像，请选择前面控件的图像结果并先运行取图步骤。");
            string mode = parameters.Str("Convert Type", "Color to Gray").Trim();
            mode = mode switch { "彩色转灰度" => "Color to Gray", "灰度转彩色" => "Gray to Color", "彩色转HSV" => "Color to HSV", _ => mode };
            if (mode is not ("Color to Gray" or "Gray to Color" or "Color to HSV"))
                throw new ArgumentException("转换类型无效，请选择“彩色转灰度”“灰度转彩色”或“彩色转HSV”。");
            int channels = source.Channels(), depth = source.Depth();
            if (depth != MatType.CV_8U && depth != MatType.CV_16U && depth != MatType.CV_32F)
                throw new ArgumentException("当前图像像素类型不受支持，请使用8位、16位无符号或32位浮点图像。");
            if (mode == "Gray to Color" && channels != 1)
                throw new ArgumentException($"当前输入是{channels}通道图像。“灰度转彩色”只接受单通道灰度图；彩色图转灰度请选择“彩色转灰度”。");
            if (mode == "Color to HSV" && channels is not (3 or 4))
                throw new ArgumentException("当前输入不是彩色图。转换到HSV需要三通道或四通道彩色图像，请重新选择彩色图像来源。");
            if (channels is not (1 or 3 or 4))
                throw new ArgumentException($"当前图像有{channels}个通道，无法转换。请使用单通道灰度图或三、四通道彩色图。");
            int width = source.Width, height = source.Height;
            int output = parameters.Int("IM Out", 1);
            if (output < 0) throw new ArgumentException("结果图像的内部存储位置无效，请重新添加此控件。");
            _ = HalconRuntime.Version;
            using var input = CreateImage(source);
            Mat result = null;
            try
            {
                if (mode == "Color to Gray")
                {
                    using var gray = input.Rgb1ToGray();
                    result = HalconChannelSplit.Copy(gray);
                }
                else if (mode == "Gray to Color")
                {
                    using var color = input.Compose3(input, input);
                    using var red = color.Decompose3(out var green, out var blue);
                    using (green) using (blue) result = Merge(blue, green, red);
                }
                else
                {
                    using var red = input.Decompose3(out var green, out var blue);
                    using (green) using (blue)
                    {
                        using var hue = red.TransFromRgb(green, blue, out var saturation, out var value, "hsv");
                        using (saturation) using (value) result = Merge(hue, saturation, value);
                    }
                }
                context.CancellationToken.ThrowIfCancellationRequested();
                int outputChannels = result.Channels();
                context.SetImage(output, result); result = null;
                context.Vars.Set("Convert Type", mode switch { "Color to Gray" => "彩色转灰度", "Gray to Color" => "灰度转彩色", _ => "彩色转HSV" });
                context.Vars.Set("Input Channel Count", channels);
                context.Vars.Set("Channel Count", outputChannels);
                context.Vars.Set("Width", width);
                context.Vars.Set("Height", height);
                context.Vars.Set("Message", channels == 1 && mode == "Color to Gray" ? "输入已经是灰度图，已保留灰度结果。" : "");
                return true;
            }
            finally { result?.Dispose(); }
        }
        catch (ArgumentException error) { context.Log("彩色转换：" + error.Message, true); return false; }
        catch (HOperatorException error)
        {
            System.Diagnostics.Trace.WriteLine(error);
            context.Log("彩色转换失败：HALCON未能完成图像转换，请检查输入图像是否有效，以及运行许可是否可用。", true);
            return false;
        }
    }

    private static HImage CreateImage(Mat source)
    {
        if (source.Channels() != 1) return HalconChannelSplit.CreateImage(source);
        using var contiguous = source.Clone();
        string type = source.Depth() == MatType.CV_8U ? "byte" : source.Depth() == MatType.CV_16U ? "uint2" : "real";
        return new HImage(type, source.Width, source.Height, contiguous.Data);
    }

    private static Mat Merge(HImage first, HImage second, HImage third)
    {
        using var a = HalconChannelSplit.Copy(first);
        using var b = HalconChannelSplit.Copy(second);
        using var c = HalconChannelSplit.Copy(third);
        var result = new Mat();
        try { Cv2.Merge([a, b, c], result); return result; }
        catch { result.Dispose(); throw; }
    }
}
