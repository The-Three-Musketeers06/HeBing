using System.Globalization;
using System.IO;
using System.Text.Json;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

public sealed class ContourTemplatePoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class ContourTemplateFeature
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Nx { get; set; }
    public double Ny { get; set; }
}

/// <summary>A portable, versioned model. Geometry uses crop coordinates; its origin uses training-image coordinates.</summary>
public sealed class ContourTemplateModel
{
    public string Format { get; set; } = "ESVision.ContourTemplate";
    public int Version { get; set; } = 2;
    public string Backend { get; set; } = "";
    public string SourceFingerprint { get; set; } = "";
    public string InputFingerprint { get; set; } = "";
    public Dictionary<string, byte[]> NativeModels { get; set; } = new();
    public int Width { get; set; }
    public int Height { get; set; }
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public double OriginX { get; set; }
    public double OriginY { get; set; }
    public double OriginAngle { get; set; }
    public double EdgeLow { get; set; } = 30;
    public double EdgeHigh { get; set; } = 80;
    public double MinContrast { get; set; }
    public int MinContourSize { get; set; } = 6;
    public int PyramidLevels { get; set; } = 4;
    public double AngleMin { get; set; } = -180;
    public double AngleMax { get; set; } = 180;
    public double ScaleMin { get; set; } = 1;
    public double ScaleMax { get; set; } = 1;
    public double TrainingAngleStep { get; set; }
    public double TrainingScaleStep { get; set; }
    public byte[] TrainingImagePng { get; set; } = Array.Empty<byte>();
    public int TrainingImageWidth { get; set; }
    public int TrainingImageHeight { get; set; }
    public byte[] SourceImagePng { get; set; } = Array.Empty<byte>();
    public string TrainingRegionJson { get; set; }
    public string ShieldingAreaJson { get; set; }
    public string ShieldingStrokesJson { get; set; }
    public byte[] MaskPng { get; set; } = Array.Empty<byte>();
    public List<List<ContourTemplatePoint>> Contours { get; set; } = new();
    public List<ContourTemplateFeature> Features { get; set; } = new();
    public bool ImportedLegacyImage { get; set; }
}

public sealed class ContourMatchOptions
{
    public double AngleMin { get; set; } = -180;
    public double AngleMax { get; set; } = 180;
    public double ScaleMin { get; set; } = 1;
    public double ScaleMax { get; set; } = 1;
    public double AngleStep { get; set; } = 5;
    public double ScaleStep { get; set; } = .05;
    public int NumLevels { get; set; } = 4;
    public double Greediness { get; set; } = .9;
    public double MinScore { get; set; } = .6;
    public double MaxOverlap { get; set; } = .5;
    public int MaxMatches { get; set; } = 1;
    public string Polarity { get; set; } = "Use Polarity";
    public bool SubPixel { get; set; }
    public int TimeoutMilliseconds { get; set; } = 3000;

    public static ContourMatchOptions FromParameters(ParamBag p) => new()
    {
        AngleMin = ContourTemplateEngine.Number(p.Group("Angle Range"), "min", -180),
        AngleMax = ContourTemplateEngine.Number(p.Group("Angle Range"), "max", 180),
        ScaleMin = ContourTemplateEngine.Number(p.Group("Scale Range"), "min", 1),
        ScaleMax = ContourTemplateEngine.Number(p.Group("Scale Range"), "max", 1),
        AngleStep = p.Dbl("Angle Step", 5), ScaleStep = p.Dbl("Scale Step", .05),
        NumLevels = p.Int("Num Levels", 4), Greediness = p.Dbl("Greediness", .9),
        MinScore = p.Dbl("Min Score", .6), MaxOverlap = p.Dbl("Max Overlap", .5),
        MaxMatches = p.Int("Num Matches", 1), Polarity = p.Str("Polarity", "Use Polarity"),
        SubPixel = p.Bool("Sub Pixel"), TimeoutMilliseconds = p.Int("Timeout", 3000)
    };
}

public sealed class ContourMatch
{
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Absolute running origin orientation, in clockwise image degrees.</summary>
    public double Angle { get; set; }
    /// <summary>Rotation relative to the training image, in clockwise image degrees.</summary>
    public double Rotation { get; set; }
    public double Scale { get; set; } = 1;
    public double Score { get; set; }
}

public sealed class ContourMatchResult
{
    public List<ContourMatch> Matches { get; set; } = new();
    public double ElapsedMilliseconds { get; set; }
    public bool TimedOut { get; set; }
}

public static partial class ContourTemplateEngine
{
    public static string SerializeModel(ContourTemplateModel model)
    { ValidateModel(model); return JsonSerializer.Serialize(model); }

    public static ContourTemplateModel DeserializeModel(string json)
    {
        try
        {
            var model = JsonSerializer.Deserialize<ContourTemplateModel>(json) ?? throw new InvalidDataException("工程内的模板为空，请重新训练");
            ValidateModel(model); return model;
        }
        catch (JsonException exception) { throw new InvalidDataException("工程内的模板数据损坏，请重新训练", exception); }
    }

    public static ContourTemplateModel Train(Mat image, ParamBag parameters, CancellationToken cancellation = default)
    {
        // Gray is accepted as a legacy teaching-mode alias; this entry now trains shape features.
        if (parameters?.Str("Matching Method") is "Shape" or "Gray") return TrainShape(image, parameters, cancellation);
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
        var model = new ContourTemplateModel
        {
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
        if (!IsAuto(p.Group("Parameters"), "Edge Threshold"))
        {
            var threshold = NestedGroup(p.Group("Parameters"), "Edge Threshold", "Value");
            model.EdgeLow = Number(threshold, "min", 30); model.EdgeHigh = Number(threshold, "max", 80);
            if (!Finite(model.EdgeLow, model.EdgeHigh) || model.EdgeLow < 0 || model.EdgeHigh <= model.EdgeLow || model.EdgeHigh > 255)
                throw new ArgumentException("边缘阈值需要满足0 ≤ 下限 < 上限 ≤ 255。");
        }
        TrainNative(image, region, model, p, cancellation);
        Cv2.ImEncode(".png", crop, out var training); Cv2.ImEncode(".png", mask, out var encodedMask);
        Cv2.ImEncode(".png", image.Depth() is MatType.CV_8U or MatType.CV_16U ? image : gray, out var source); model.SourceImagePng = source;
        model.TrainingImagePng = training; model.MaskPng = encodedMask;
        cancellation.ThrowIfCancellationRequested(); ValidateModel(model); return model;
    }

    public static void Save(string path, ContourTemplateModel model)
    {
        ValidateModel(model);
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请指定模板保存路径。");
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(model)); File.Move(temporary, full, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static ContourTemplateModel Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("模板文件不存在，请创建模板或重新选择模板文件。", path);
        var info = new FileInfo(path); if (info.Length > 128 * 1024 * 1024) throw new InvalidDataException("模板文件超过 128 MB，请检查所选文件。");
        var bytes = File.ReadAllBytes(path); if (bytes.Length == 0) throw new InvalidDataException("模板文件为空，请重新训练模板。");
        if (bytes[0] == (byte)'{' || bytes[0] == (byte)' ' || bytes[0] == (byte)'\n' || bytes[0] == (byte)'\r')
        {
            try { var model = JsonSerializer.Deserialize<ContourTemplateModel>(bytes) ?? throw new InvalidDataException("模板内容为空。"); ValidateModel(model); return model; }
            catch (JsonException ex) { throw new InvalidDataException("模板格式损坏，请重新训练并保存模板。", ex); }
        }
        using var legacy = Cv2.ImDecode(bytes, ImreadModes.Grayscale);
        if (legacy.Empty()) throw new InvalidDataException("无法读取该模板；请选择 ES-Vision 轮廓模板或原有图像模板。");
        var imported = Train(legacy, new ParamBag()); imported.ImportedLegacyImage = true; return imported;
    }

    public static Mat DecodeTrainingImage(ContourTemplateModel model) => Decode(model.TrainingImagePng, model.Width, model.Height);
    public static Mat DecodeMask(ContourTemplateModel model) => Decode(model.MaskPng, model.Width, model.Height);
    public static Mat DecodeSourceImage(ContourTemplateModel model)
    {
        if (model.SourceImagePng == null || model.SourceImagePng.Length <= 10)
        {
            using var crop = DecodeTrainingImage(model);
            // Retain source-image coordinates when the user opts out of embedding the complete image.
            var width = Math.Max(model.OffsetX + model.Width, model.TrainingImageWidth);
            var height = Math.Max(model.OffsetY + model.Height, model.TrainingImageHeight);
            if (width <= 0 || height <= 0 || (long)width * height > 100_000_000 || model.OffsetX < 0 || model.OffsetY < 0)
                throw new InvalidDataException("模板训练图像尺寸无效。");
            var restored = new Mat(height, width, crop.Type(), Scalar.Black);
            using var target = new Mat(restored, new OpenCvSharp.Rect(model.OffsetX, model.OffsetY, model.Width, model.Height));
            crop.CopyTo(target); return restored;
        }
        var image = Cv2.ImDecode(model.SourceImagePng, ImreadModes.Unchanged);
        if (image.Empty() || image.Width != model.TrainingImageWidth || image.Height != model.TrainingImageHeight)
        { image.Dispose(); throw new InvalidDataException("模板内嵌源图像无效，请重新训练模板。"); }
        return image;
    }
    private static Mat Decode(byte[] bytes, int width, int height)
    {
        var image = Cv2.ImDecode(bytes, ImreadModes.Grayscale);
        if (image.Empty() || image.Width != width || image.Height != height) { image.Dispose(); throw new InvalidDataException("模板内嵌图像无效，请重新训练模板。"); }
        return image;
    }

    public static Point2d TransformPoint(ContourTemplateModel model, ContourMatch match, double localX, double localY)
    {
        var angle = match.Rotation * Math.PI / 180; var x = (localX + model.OffsetX - model.OriginX) * match.Scale; var y = (localY + model.OffsetY - model.OriginY) * match.Scale;
        return new(match.X + x * Math.Cos(angle) - y * Math.Sin(angle), match.Y + x * Math.Sin(angle) + y * Math.Cos(angle));
    }

    internal static double Number(IReadOnlyDictionary<string, object> group, string key, double fallback = 0)
        => group != null && group.TryGetValue(key, out var raw) && double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private static bool Boolean(IReadOnlyDictionary<string, object> group, string key, bool fallback)
        => group != null && group.TryGetValue(key, out var raw) && bool.TryParse(raw?.ToString(), out var value) ? value : fallback;
    private static IReadOnlyDictionary<string, object> NestedGroup(IReadOnlyDictionary<string, object> group, string key, string child = null)
    {
        var nested = group?.GetValueOrDefault(key) as IReadOnlyDictionary<string, object>;
        return child == null ? nested : nested?.GetValueOrDefault(child) as IReadOnlyDictionary<string, object>;
    }
    private static double NestedValue(IReadOnlyDictionary<string, object> group, string key, double fallback) => Number(NestedGroup(group, key), "Value", fallback);
    private static bool IsAuto(IReadOnlyDictionary<string, object> group, string key) => Boolean(NestedGroup(group, key), "Auto", true);
    private static bool Finite(params double[] values) => values.All(double.IsFinite);
    private static void ValidateImage(Mat image)
    {
        if (image == null || image.IsDisposed || image.Empty()) throw new ArgumentException("输入图像为空，请先采集或加载图像。");
        if (image.Width < 5 || image.Height < 5 || (long)image.Width * image.Height > 100_000_000) throw new ArgumentException("输入图像尺寸无效或超过 1 亿像素。");
    }
    private static void ValidateRanges(double amin, double amax, double smin, double smax)
    {
        if (!Finite(amin, amax, smin, smax) || amin > amax || amin < -360 || amax > 360 || amax - amin > 360)
            throw new ArgumentException("角度范围需要满足 -360 ≤ 下限 ≤ 上限 ≤ 360，且跨度不超过 360°。");
        if (smin < .05 || smax > 5 || smin > smax) throw new ArgumentException("缩放范围需要满足 0.05 ≤ 下限 ≤ 上限 ≤ 5。");
    }
    private static void ValidateModel(ContourTemplateModel model)
    {
        if (model == null || model.Format != "ESVision.ContourTemplate" || model.Version is not (1 or 2)) throw new InvalidDataException("模板版本不支持，请重新训练模板。");
        if (model.Width < 5 || model.Height < 5 || (long)model.Width * model.Height > 100_000_000 || model.Features == null || model.Features.Count < (model.Version == 1 ? 12 : 1) || model.Features.Count > 100_000 || model.Contours == null || model.Contours.Sum(c => (long)(c?.Count ?? 0)) > 10_000_000)
            throw new InvalidDataException("模板几何数据无效，请重新训练模板。");
        if (!Finite(model.OriginX, model.OriginY, model.OriginAngle, model.EdgeLow, model.EdgeHigh, model.MinContrast) || model.EdgeLow < 0 || model.EdgeHigh <= model.EdgeLow || model.PyramidLevels < 1 || model.PyramidLevels > 10)
            throw new InvalidDataException("模板参数无效，请重新训练模板。");
        if (model.Features.Any(f => f == null || !Finite(f.X, f.Y, f.Nx, f.Ny) || model.Version == 1 && (f.X < 0 || f.Y < 0 || f.X >= model.Width || f.Y >= model.Height) || Math.Abs(f.Nx * f.Nx + f.Ny * f.Ny - 1) > .02))
            throw new InvalidDataException("模板特征坐标或方向无效，请重新训练模板。");
        if (model.TrainingImagePng == null || model.TrainingImagePng.Length < 10 || model.MaskPng == null || model.MaskPng.Length < 10) throw new InvalidDataException("模板缺少训练图像或遮罩，请重新训练模板。");
        if (model.Version == 2 && (model.Backend is not ("HALCON.Shape" or "HALCON.NCC") || model.NativeModels == null || model.NativeModels.Count == 0
            || model.NativeModels.Values.Any(bytes => bytes == null || bytes.Length < 16 || bytes.Length > 128 * 1024 * 1024)))
            throw new InvalidDataException("HALCON模板数据无效，请重新训练。");
        ValidateRanges(model.AngleMin, model.AngleMax, model.ScaleMin, model.ScaleMax);
    }
    private static T[] Sample<T>(IReadOnlyList<T> source, int maximum)
    {
        if (source.Count <= maximum) return source.ToArray();
        return Enumerable.Range(0, maximum).Select(i => source[(int)((long)i * source.Count / maximum)]).ToArray();
    }
}
