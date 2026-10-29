using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Replays exclusion geometry in order, then clips it to the training region.</summary>
public static class TemplateShieldingMask
{
    public static Mat Create(Size size, ParamBag parameters, Mat trainingRegion = null)
    {
        var mask = new Mat(size, MatType.CV_8UC1, Scalar.Black);
        try
        {
            if (parameters.Bool("Use Shielding Area"))
            {
                if (parameters.Raw("Shielding Area") == null) throw new ArgumentException("已开启屏蔽区域，请先绘制需要排除的区域。");
                using var area = BlobDetectOp.RegionMask(size, parameters.Raw("Shielding Area"), parameters);
                Cv2.BitwiseOr(mask, area, mask);
            }
            if (parameters.Raw("Shielding Strokes") is IEnumerable<object> strokes)
            {
                var operations = strokes.ToArray();
                bool Flag(object stroke, string key) => stroke is IReadOnlyDictionary<string, object> data && bool.TryParse(data.GetValueOrDefault(key)?.ToString(), out var flag) && flag;
                for (int index = 0; index < operations.Length; index++)
                {
                    bool invert = Flag(operations[index], "Invert"), erase = Flag(operations[index], "Erase");
                    var batch = new List<object> { operations[index] };
                    // A brush stroke has many small polygons. Rasterize consecutive additions or
                    // erasures together instead of allocating one full image per brush sample.
                    if (!invert)
                        while (index + 1 < operations.Length && !Flag(operations[index + 1], "Invert") && Flag(operations[index + 1], "Erase") == erase)
                            batch.Add(operations[++index]);
                    using var area = BlobDetectOp.RegionMask(size, batch, parameters);
                    if (invert) Cv2.BitwiseXor(mask, area, mask);
                    else if (erase)
                    { using var keep = new Mat(); Cv2.BitwiseNot(area, keep); Cv2.BitwiseAnd(mask, keep, mask); }
                    else Cv2.BitwiseOr(mask, area, mask);
                }
            }
            if (trainingRegion != null) Cv2.BitwiseAnd(mask, trainingRegion, mask);
            return mask;
        }
        catch { mask.Dispose(); throw; }
    }
}
