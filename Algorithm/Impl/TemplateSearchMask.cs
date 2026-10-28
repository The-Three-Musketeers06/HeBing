using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

internal static class TemplateSearchMask
{
    public static Mat Create(OpContext context, ParamBag parameters, Mat image)
    {
        Mat result = null;
        try
        {
            if (parameters.Bool("Use Region")) result = BlobDetectOp.RegionMask(image.Size(), parameters.Raw("Region"), parameters);
            if (parameters.Bool("Use Mask"))
            {
                var source = context.Images.Get(parameters.Int("Mask Im In", 1));
                if (source == null || source.IsDisposed || source.Empty()) throw new ArgumentException("掩膜图像为空，请选择上游控件的掩膜输出并先运行该控件。");
                if (source.Size() != image.Size()) throw new ArgumentException("掩膜尺寸与输入图像不一致，请使用相同宽高的掩膜。");
                if (source.Channels() != 1) throw new ArgumentException("掩膜必须是单通道图像：白色区域参与匹配，黑色区域排除。");
                using var mask = new Mat();
                using var zero = InputArray.Create(0d);
                Cv2.Compare(source, zero, mask, CmpTypes.GT);
                if (result == null) result = mask.Clone(); else Cv2.BitwiseAnd(result, mask, result);
            }
            if (result != null && Cv2.CountNonZero(result) < 25) throw new ArgumentException("搜索区域与掩膜的有效交集为空或过小，请重新设置区域或掩膜。");
            return result;
        }
        catch { result?.Dispose(); throw; }
    }
}
