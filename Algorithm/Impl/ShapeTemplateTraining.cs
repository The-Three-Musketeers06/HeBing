using System.Text.Json;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

// Ported from AiVisionStudio-1: contour extraction and normalized gradient features.
public static partial class ContourTemplateEngine
{
    private static ContourTemplateModel TrainShape(Mat image, ParamBag parameters, CancellationToken cancellation = default)
    {
        ValidateImage(image); cancellation.ThrowIfCancellationRequested();
        var p = parameters ?? throw new ArgumentNullException(nameof(parameters));
        using var gray = Img.ToGray8(image);
        using var region = p.Raw("Region") != null
            ? BlobDetectOp.RegionMask(gray.Size(), p.Raw("Region"), p)
            : new Mat(gray.Size(), MatType.CV_8UC1, Scalar.White);
        if (Cv2.CountNonZero(region) < 25) throw new ArgumentException("模板区域为空或过小，请在图像中重新绘制模板区域。");
        using var shieldMask = TemplateShieldingMask.Create(gray.Size(), p, region);
        using (var visible = new Mat()) { Cv2.BitwiseNot(shieldMask, visible); Cv2.BitwiseAnd(region, visible, region); }
        if (Cv2.CountNonZero(region) < 25) throw new ArgumentException("屏蔽覆盖了整个模板，请用橡皮擦恢复特征或减小屏蔽区域。");
        using var selected = new Mat(); Cv2.FindNonZero(region, selected);
        var bounds = Cv2.BoundingRect(selected);
        if (bounds.Width < 5 || bounds.Height < 5) throw new ArgumentException("模板有效区域至少需要 5 × 5 像素。");
        using var crop = new Mat(gray, bounds); using var mask = new Mat(region, bounds);
        var fingerprint = ImageFingerprint(image);
        var model = new ContourTemplateModel
        {
            Version = 1, Backend = ShapeBackend, SourceFingerprint = fingerprint, InputFingerprint = fingerprint,
            Width = bounds.Width, Height = bounds.Height, OffsetX = bounds.X, OffsetY = bounds.Y,
            TrainingImageWidth = image.Width, TrainingImageHeight = image.Height,
            TrainingRegionJson = JsonSerializer.Serialize(p.Raw("Region") ?? new Dictionary<string, object>
            {
                ["Type"] = "rect2d", ["rect2d"] = new Dictionary<string, object> { ["Center"] = DetectionOverlay2D.XY((image.Width - 1) / 2d, (image.Height - 1) / 2d), ["Size"] = new Dictionary<string, object> { ["Width"] = image.Width - 1d, ["Height"] = image.Height - 1d }, ["Rotation"] = 0d }
            }),
            ShieldingAreaJson = p.Bool("Use Shielding Area") ? JsonSerializer.Serialize(p.Raw("Shielding Area")) : null,
            ShieldingStrokesJson = JsonSerializer.Serialize(p.Raw("Shielding Strokes")),
            OriginX = bounds.X + (bounds.Width - 1) / 2d, OriginY = bounds.Y + (bounds.Height - 1) / 2d,
            PyramidLevels = (int)NestedValue(p.Group("Create Model"), "Num Levels", 4),
            MinContourSize = IsAuto(p.Group("Parameters"), "Min Size") ? 6 : (int)NestedValue(p.Group("Parameters"), "Min Size", 6),
            MinContrast = IsAuto(p.Group("Parameters"), "Min Contrast") ? 4 : NestedValue(p.Group("Parameters"), "Min Contrast", 4)
        };
        var create = p.Group("Create Model");
        if (IsAuto(create, "Num Levels")) model.PyramidLevels = Math.Clamp((int)Math.Floor(Math.Log2(Math.Max(16, Math.Min(bounds.Width, bounds.Height)) / 16d)) + 1, 1, 8);
        var angles = NestedGroup(create, "Angle Range", "Value");
        var scales = NestedGroup(create, "Scale Range", "Value");
        model.AngleMin = Number(angles, "min", -180); model.AngleMax = Number(angles, "max", 180);
        model.ScaleMin = Number(scales, "min", 1); model.ScaleMax = Number(scales, "max", 1);
        model.TrainingAngleStep = IsAuto(create, "Angle Step") ? 0 : NestedValue(create, "Angle Step", 5);
        model.TrainingScaleStep = IsAuto(create, "Scale Step") ? 0 : NestedValue(create, "Scale Step", .05);
        ValidateRanges(model.AngleMin, model.AngleMax, model.ScaleMin, model.ScaleMax);
        if (!Finite(model.TrainingAngleStep, model.TrainingScaleStep) || model.TrainingAngleStep < 0 || model.TrainingAngleStep > 90 || model.TrainingScaleStep < 0 || model.TrainingScaleStep > 1)
            throw new ArgumentException("模板训练角度步长需在 0～90，缩放步长需在 0～1（0 表示自动）。");
        if (model.PyramidLevels < 1 || model.PyramidLevels > 10) throw new ArgumentException("模板金字塔层数必须在 1～10 之间。");
        if (model.MinContrast < 0 || model.MinContrast > 1443 || model.MinContourSize < 0 || model.MinContourSize > 100000)
            throw new ArgumentException("最小对比度或轮廓长度无效，请调整建模参数。");
        var origin = p.Group("Model Origin");
        if (origin != null && !Boolean(origin, "Auto", true))
        {
            model.OriginX = Number(origin, "X"); model.OriginY = Number(origin, "Y"); model.OriginAngle = Number(origin, "Angle");
            if (!Finite(model.OriginX, model.OriginY, model.OriginAngle) || model.OriginX < 0 || model.OriginX >= image.Width || model.OriginY < 0 || model.OriginY >= image.Height)
                throw new ArgumentException("模板原点必须位于训练图像内，角度必须是有效数值。");
        }
        using var smooth = new Mat(); Cv2.GaussianBlur(crop, smooth, new Size(3, 3), .8);
        using var gx = new Mat(); using var gy = new Mat();
        Cv2.Sobel(smooth, gx, MatType.CV_32F, 1, 0); Cv2.Sobel(smooth, gy, MatType.CV_32F, 0, 1);
        gx.GetArray(out float[] dx); gy.GetArray(out float[] dy);
        if (IsAuto(p.Group("Parameters"), "Edge Threshold"))
        {
            var magnitudes = Enumerable.Range(0, dx.Length).Where(i => (i % 3) == 0).Select(i => Math.Sqrt(dx[i] * dx[i] + dy[i] * dy[i])).Where(v => v > 8).OrderBy(v => v).ToArray();
            model.EdgeHigh = magnitudes.Length == 0 ? 80 : Math.Clamp(magnitudes[(int)((magnitudes.Length - 1) * .60)], 20, 180);
            model.EdgeLow = model.EdgeHigh * .4;
        }
        else
        {
            var threshold = NestedGroup(p.Group("Parameters"), "Edge Threshold", "Value");
            model.EdgeLow = Number(threshold, "min", 30); model.EdgeHigh = Number(threshold, "max", 80);
            if (!Finite(model.EdgeLow, model.EdgeHigh) || model.EdgeLow < 0 || model.EdgeHigh <= model.EdgeLow || model.EdgeHigh > 255)
                throw new ArgumentException("边缘阈值需要满足 0 ≤ 下限 < 上限 ≤ 255。");
        }
        using var edges = new Mat(); Cv2.Canny(smooth, edges, model.EdgeLow, model.EdgeHigh);
        using var inside = new Mat(); using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.Erode(mask, inside, kernel); Cv2.BitwiseAnd(edges, inside, edges);
        Cv2.FindContours(edges, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxNone);
        var seen = new HashSet<int>(); var allFeatures = new List<ContourTemplateFeature>();
        foreach (var contour in contours.Where(c => c.Length >= Math.Max(2, model.MinContourSize)))
        {
            cancellation.ThrowIfCancellationRequested();
            model.Contours.Add(contour.Select(q => new ContourTemplatePoint { X = q.X, Y = q.Y }).ToList());
            foreach (var point in contour)
            {
                var index = point.Y * bounds.Width + point.X;
                var magnitude = Math.Sqrt(dx[index] * dx[index] + dy[index] * dy[index]);
                if (magnitude < Math.Max(1, model.MinContrast) || !seen.Add(index)) continue;
                allFeatures.Add(new() { X = point.X, Y = point.Y, Nx = dx[index] / magnitude, Ny = dy[index] / magnitude });
            }
        }
        var optimize = Boolean(NestedGroup(p.Group("Parameters"), "Optimization"), "Value", false);
        model.Features = Sample(allFeatures, optimize ? 1200 : 2400).ToList();
        if (model.Features.Count < 12) throw new ArgumentException("模板有效轮廓不足，请扩大模板区域、减小屏蔽范围或降低边缘阈值。");
        Cv2.ImEncode(".png", crop, out var training); Cv2.ImEncode(".png", mask, out var encodedMask);
        Cv2.ImEncode(".png", image.Depth() is MatType.CV_8U or MatType.CV_16U ? image : gray, out var source); model.SourceImagePng = source;
        model.TrainingImagePng = training; model.MaskPng = encodedMask;
        cancellation.ThrowIfCancellationRequested(); ValidateModel(model); return model;
    }

}
