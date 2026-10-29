using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HalconDotNet;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

public static partial class ContourTemplateEngine
{
    // Keep the persisted driver identifier so existing projects retain their operations and bindings.
    public const string ShapeDriver = "Basic NCC Match";
    public const string GrayDriver = ShapeDriver;
    public const string ShapeBackend = "OpenCV.Shape";
    public static bool IsTemplateTool(string driver) => driver is "Basic Shape Match" or "Basic Shape Model" or ShapeDriver;
    public static bool IsMatcher(string driver) => driver is "Basic Shape Match" or ShapeDriver;

    public static string ImageFingerprint(Mat image)
    {
        ValidateImage(image);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"{image.Width}:{image.Height}:{image.Type()}:"));
        var row = new byte[checked(image.Width * (int)image.ElemSize())];
        for (int y = 0, height = image.Height; y < height; y++) { Marshal.Copy(image.Ptr(y), row, 0, row.Length); hash.AppendData(row); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static HImage NativeImage(Mat image)
    {
        if (image.Channels() is not (1 or 3 or 4)) throw new ArgumentException("模板匹配只支持灰度或彩色图像。");
        if (image.Depth() != MatType.CV_8U && image.Depth() != MatType.CV_16U && image.Depth() != MatType.CV_32F)
            throw new ArgumentException("模板匹配支持8位、16位无符号或32位浮点图像。");
        HImage input;
        if (image.Channels() == 1)
        {
            using var contiguous = image.Clone();
            input = new HImage(image.Depth() == MatType.CV_8U ? "byte" : image.Depth() == MatType.CV_16U ? "uint2" : "real", image.Width, image.Height, contiguous.Data);
        }
        else input = HalconChannelSplit.CreateImage(image);
        using (input)
        {
            if (image.Channels() == 1) return input.CopyImage();
            return input.Rgb1ToGray();
        }
    }

    private static HRegion NativeRegion(Mat mask)
    {
        var rows = new List<int>(); var starts = new List<int>(); var ends = new List<int>();
        var row = new byte[mask.Width];
        for (int y = 0, height = mask.Height; y < height; y++)
        {
            Marshal.Copy(mask.Ptr(y), row, 0, row.Length);
            for (int x = 0; x < row.Length; x++)
            {
                if (row[x] == 0) continue;
                int start = x;
                while (x + 1 < row.Length && row[x + 1] != 0) x++;
                rows.Add(y); starts.Add(start); ends.Add(x);
            }
        }
        using var r = new HTuple(rows.ToArray()); using var s = new HTuple(starts.ToArray()); using var e = new HTuple(ends.ToArray());
        var region = new HRegion();
        try { region.GenRegionRuns(r, s, e); return region; } catch { region.Dispose(); throw; }
    }

    private static void TrainNative(Mat image, Mat mask, ContourTemplateModel model, ParamBag parameters, CancellationToken cancellation)
    {
        _ = HalconRuntime.Version;
        using var nativeImage = NativeImage(image);
        using var nativeRegion = NativeRegion(mask);
        using var reduced = nativeImage.ReduceDomain(nativeRegion);
        nativeRegion.AreaCenter(out double centerY, out double centerX);
        model.Backend = "HALCON.Shape";
        model.SourceFingerprint = ImageFingerprint(image);
        model.InputFingerprint = model.SourceFingerprint;
        using var levels = IsAuto(parameters.Group("Create Model"), "Num Levels") ? new HTuple("auto") : new HTuple(model.PyramidLevels);
        using var angleStep = model.TrainingAngleStep <= 0 ? new HTuple("auto") : new HTuple(model.TrainingAngleStep * Math.PI / 180);
        using var scaleStep = model.TrainingScaleStep <= 0 ? new HTuple("auto") : new HTuple(model.TrainingScaleStep);
        using var contrast = IsAuto(parameters.Group("Parameters"), "Edge Threshold")
            ? new HTuple("auto") : new HTuple(new[] { (int)model.EdgeLow, (int)model.EdgeHigh, Math.Max(1, model.MinContourSize) });
        using var minContrast = IsAuto(parameters.Group("Parameters"), "Min Contrast") ? new HTuple("auto") : new HTuple(model.MinContrast);
        using var optimization = new HTuple(Boolean(NestedGroup(parameters.Group("Parameters"), "Optimization"), "Value", false) ? "point_reduction_medium" : "auto");
        var metrics = new[] { "use_polarity", "ignore_global_polarity", "ignore_local_polarity" };
        try
        {
            foreach (string metric in metrics)
            {
                cancellation.ThrowIfCancellationRequested();
                using var stream = new MemoryStream();
                {
                    using var handle = new HShapeModel();
                    handle.CreateScaledShapeModel(reduced, levels, -model.AngleMax * Math.PI / 180, (model.AngleMax - model.AngleMin) * Math.PI / 180,
                        angleStep, model.ScaleMin, model.ScaleMax, scaleStep, optimization, metric, contrast, minContrast);
                    handle.SetShapeModelOrigin(model.OriginY - centerY, model.OriginX - centerX);
                    if (metric == "use_polarity")
                    {
                        using var contours = handle.GetShapeModelContours(1);
                        for (int i = 1; i <= contours.CountObj(); i++)
                        {
                            using var contour = contours.SelectObj(i);
                            contour.GetContourXld(out var ys, out var xs);
                            using (ys) using (xs)
                            {
                                var points = Enumerable.Range(0, xs.Length).Select(j => new ContourTemplatePoint
                                { X = xs[j].D + model.OriginX - model.OffsetX, Y = ys[j].D + model.OriginY - model.OffsetY }).ToList();
                                model.Contours.Add(points);
                            }
                        }
                    }
                    handle.Serialize(stream);
                }
                model.NativeModels[metric] = stream.ToArray();
            }
        }
        catch (HOperatorException error) { Trace.WriteLine(error); cancellation.ThrowIfCancellationRequested(); throw new InvalidOperationException("HALCON模板训练失败：请检查模板区域、对比度、金字塔层数及运行许可。", error); }
        model.Features = Sample(model.Contours.SelectMany(contour => contour).Select(point => new ContourTemplateFeature
        { X = point.X, Y = point.Y, Nx = 1, Ny = 0 }).ToArray(), 100000).ToList();
        cancellation.ThrowIfCancellationRequested();
    }

    public static ContourMatchResult Match(Mat image, ContourTemplateModel model, ContourMatchOptions options, Mat searchMask = null, CancellationToken cancellation = default)
    {
        if (model?.Version == 1) return MatchShape(image, model, options, searchMask, cancellation);
        ValidateImage(image); ValidateModel(model); ValidateOptions(options); cancellation.ThrowIfCancellationRequested();
        if (model.NativeModels?.Count == 0) throw new InvalidOperationException("这是旧版模板，请点击“创建”重新训练HALCON模板。");
        if (model.Backend == "HALCON.NCC") throw new InvalidOperationException("灰度模板已停用，请点击“创建”重新训练形状模板。");
        if (searchMask != null && (searchMask.IsDisposed || searchMask.Empty() || searchMask.Type() != MatType.CV_8UC1 || searchMask.Size() != image.Size()))
            throw new ArgumentException("搜索区域遮罩必须是与输入图像等大的8位单通道图像。");
        if (searchMask != null && Cv2.CountNonZero(searchMask) < 25) throw new ArgumentException("搜索区域为空或过小，请重新绘制搜索区域。");
        string metric = options.Polarity switch { "Ignore Global Polarity" => "ignore_global_polarity", "Ignore Local Polarity" => "ignore_local_polarity", _ => "use_polarity" };
        if (!model.NativeModels.TryGetValue(metric, out var bytes)) throw new ArgumentException("模板不包含所选极性的模型，请重新训练模板。");
        double scaleMin = Math.Max(model.ScaleMin, options.ScaleMin), scaleMax = Math.Min(model.ScaleMax, options.ScaleMax);
        var angles = Enumerable.Range(-2, 5).Select(turn => (Min: Math.Max(options.AngleMin, model.AngleMin + 360 * turn), Max: Math.Min(options.AngleMax, model.AngleMax + 360 * turn)))
            .Where(range => range.Min <= range.Max).Distinct().ToArray();
        angles = angles.Where(range => !angles.Any(other => other != range && other.Min <= range.Min && other.Max >= range.Max)).ToArray();
        if (angles.Length == 0 || scaleMin > scaleMax) throw new ArgumentException("匹配角度或缩放范围与模板训练范围没有交集，请重新训练或调整范围。");
        _ = HalconRuntime.Version;
        var watch = Stopwatch.StartNew(); var result = new ContourMatchResult();
        using var full = NativeImage(image);
        using var region = searchMask == null ? full.GetDomain() : NativeRegion(searchMask);
        using var reduced = full.ReduceDomain(region);
        try
        {
            using var stream = new MemoryStream(bytes, false);
            using var shape = (HShapeModel)HShapeModel.Deserialize(stream);
            foreach (var range in angles)
            {
                cancellation.ThrowIfCancellationRequested();
                int remaining = options.TimeoutMilliseconds == 0 ? -1 : Math.Max(1, options.TimeoutMilliseconds - (int)watch.ElapsedMilliseconds);
                if (options.TimeoutMilliseconds > 0 && watch.ElapsedMilliseconds >= options.TimeoutMilliseconds) { result.TimedOut = true; break; }
                HTuple rows, cols, rotations, scores, scales;
                {
                    shape.SetShapeModelParam("timeout", remaining);
                    shape.FindScaledShapeModel(reduced, -range.Max * Math.PI / 180, (range.Max - range.Min) * Math.PI / 180,
                        scaleMin, scaleMax, options.MinScore, options.MaxMatches, options.MaxOverlap, options.SubPixel ? "least_squares" : "none",
                        options.NumLevels, options.Greediness, out rows, out cols, out rotations, out scales, out scores);
                }
                using (rows) using (cols) using (rotations) using (scales) using (scores)
                    for (int i = 0; i < rows.Length; i++)
                    {
                        double rotation = NormalizeAngle(-rotations[i].D * 180 / Math.PI);
                        if (result.Matches.Any(match => Math.Abs(match.X - cols[i].D) < .5 && Math.Abs(match.Y - rows[i].D) < .5 && Math.Abs(NormalizeAngle(match.Rotation - rotation)) < .5)) continue;
                        result.Matches.Add(new() { X = cols[i].D, Y = rows[i].D, Rotation = rotation, Angle = NormalizeAngle(rotation + model.OriginAngle), Scale = scales[i].D, Score = scores[i].D });
                    }
            }
        }
        catch (HOperatorException error) when (error.GetErrorCode() == 9400) { result.TimedOut = true; }
        catch (HOperatorException error) { Trace.WriteLine(error); cancellation.ThrowIfCancellationRequested(); throw new InvalidOperationException("HALCON模板匹配失败：请检查模板、输入图像、搜索参数及运行许可。", error); }
        cancellation.ThrowIfCancellationRequested();
        if (result.TimedOut) result.Matches.Clear();
        else
        {
            result.Matches = result.Matches.OrderByDescending(match => match.Score).Take(options.MaxMatches == 0 ? int.MaxValue : options.MaxMatches).ToList();
        }
        result.ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds;
        if (options.TimeoutMilliseconds > 0 && result.ElapsedMilliseconds >= options.TimeoutMilliseconds)
        { result.TimedOut = true; result.Matches.Clear(); }
        return result;
    }

    private static double NormalizeAngle(double angle) { angle %= 360; if (angle > 180) angle -= 360; if (angle <= -180) angle += 360; return angle; }
    private static void ValidateOptions(ContourMatchOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        ValidateRanges(options.AngleMin, options.AngleMax, options.ScaleMin, options.ScaleMax);
        if (!Finite(options.MinScore, options.Greediness, options.MaxOverlap) || options.NumLevels < 1 || options.NumLevels > 10 || options.MinScore < 0 || options.MinScore > 1 || options.Greediness < 0 || options.Greediness > 1 || options.MaxOverlap < 0 || options.MaxOverlap > 1 || options.MaxMatches < 0 || options.MaxMatches > 1000 || options.TimeoutMilliseconds < 0)
            throw new ArgumentException("匹配参数无效：分数、贪心度、重叠率需在0～1，层数1～10，匹配数0～1000，超时不得为负数。");
        if (options.Polarity is not ("Use Polarity" or "Ignore Global Polarity" or "Ignore Local Polarity")) throw new ArgumentException("未知匹配极性。");
    }
}
