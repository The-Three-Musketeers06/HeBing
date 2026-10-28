using System.Diagnostics;
using System.IO;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Algorithm.Impl;
using OpenCvSharp;
using YamlDotNet.RepresentationModel;

public static class EngineCases
{
    public static int Run()
    {
        var count = 0;
        using var teaching = TeachingImage();
        var model = ContourTemplateEngine.Train(teaching, TrainingParameters());
        Require(model.Features.Count >= 30, "训练应提取足量轮廓特征"); count++;
        Require(Math.Abs(model.OriginX - 110) < .01 && Math.Abs(model.OriginY - 88) < .01 && Math.Abs(model.OriginAngle - 17) < .01, "自定义原点必须保留"); count++;
        using (var source = ContourTemplateEngine.DecodeSourceImage(model))
        {
            Require(source.Size() == teaching.Size() && Cv2.Norm(source, teaching, NormTypes.INF) == 0, "完整训练源图必须无损保存"); count++;
        }

        using (var rotated = Place(teaching, 330, 235, 33, 1.2))
        {
            var options = Options(); options.AngleMin = 20; options.AngleMax = 45; options.ScaleMin = 1.05; options.ScaleMax = 1.3;
            var result = ContourTemplateEngine.Match(rotated, model, options);
            Require(!result.TimedOut && result.Matches.Count == 1, "旋转缩放目标应成功匹配");
            var best = result.Matches[0];
            Require(Math.Abs(best.X - 330) < 2 && Math.Abs(best.Y - 235) < 2 && Math.Abs(best.Rotation - 33) < 1.5 && Math.Abs(best.Scale - 1.2) < .035, $"旋转缩放位姿不准确: {Describe(best)}");
            Require(Math.Abs(best.Angle - 50) < 1.5, "运行角度应包含训练原点角度");
            Console.WriteLine($"旋转/缩放：{Describe(best)}, {result.ElapsedMilliseconds:0.0} ms"); count++;
        }

        using (var first = Place(teaching, 145, 130, 0, 1))
        using (var second = Place(teaching, 420, 300, 0, 1))
        {
            Cv2.Max(first, second, first); var options = Options(); options.AngleMin = options.AngleMax = 0; options.ScaleMin = options.ScaleMax = 1; options.MaxMatches = 2;
            var result = ContourTemplateEngine.Match(first, model, options);
            Require(result.Matches.Count == 2 && result.Matches.Any(m => Math.Abs(m.X - 145) < 2 && Math.Abs(m.Y - 130) < 2) && result.Matches.Any(m => Math.Abs(m.X - 420) < 2 && Math.Abs(m.Y - 300) < 2), "应输出两个目标且抑制同一目标的重复候选"); count++;
            using var search = new Mat(first.Size(), MatType.CV_8UC1, Scalar.Black); Cv2.Rectangle(search, new Rect(320, 210, 210, 190), Scalar.White, -1);
            var filtered = ContourTemplateEngine.Match(first, model, options, search);
            Require(filtered.Matches.Count == 1 && Math.Abs(filtered.Matches[0].X - 420) < 2, "ROI应排除区域外目标"); count++;
        }

        using (var image = Place(teaching, 315, 230, 0, 1))
        {
            Cv2.BitwiseNot(image, image); var options = Options(); options.AngleMin = options.AngleMax = 0; options.ScaleMin = options.ScaleMax = 1;
            var signed = ContourTemplateEngine.Match(image, model, options);
            options.Polarity = "Ignore Global Polarity"; var global = ContourTemplateEngine.Match(image, model, options);
            Require(signed.Matches.Count == 0 && global.Matches.Count == 1, "极性反转应仅在忽略极性时命中"); count++;
        }

        using (var blank = new Mat(480, 640, MatType.CV_8UC1, new Scalar(25)))
        {
            Require(ContourTemplateEngine.Match(blank, model, Options()).Matches.Count == 0, "空白图像不得产生匹配"); count++;
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            Expect<OperationCanceledException>(() => ContourTemplateEngine.Match(blank, model, Options(), cancellation: cancellation.Token)); count++;
        }
        using (var image = Place(teaching, 315, 230, 0, 1))
        {
            var options = Options(); options.TimeoutMilliseconds = 1; options.AngleStep = .2;
            var timeout = ContourTemplateEngine.Match(image, model, options);
            Require(timeout.TimedOut && timeout.Matches.Count == 0, "超时必须显式返回并清空未完成匹配"); count++;
        }
        using (var source = Place(teaching, 315, 230, 0, 1))
        using (var large = new Mat(2048, 2448, MatType.CV_8UC1, new Scalar(25)))
        {
            using (var destination = new Mat(large, new Rect(900, 600, source.Width, source.Height))) source.CopyTo(destination);
            var result = ContourTemplateEngine.Match(large, model, new ContourMatchOptions { SubPixel = true });
            Require(!result.TimedOut && result.Matches.Count == 1 && Math.Abs(result.Matches[0].X - 1215) < 2 && Math.Abs(result.Matches[0].Y - 830) < 2, "2448×2048图像应在默认搜索参数下完成定位");
            Console.WriteLine($"2448×2048 默认搜索：{result.ElapsedMilliseconds:0.0} ms"); count++;
        }

        var masked = ContourTemplateEngine.Train(teaching, TrainingParameters(shield: true));
        Require(masked.Features.All(f => f.X + masked.OffsetX < 133 || f.X + masked.OffsetX > 173 || f.Y + masked.OffsetY < 51 || f.Y + masked.OffsetY > 91), "涂抹屏蔽区域不应产生模型特征"); count++;
        var directory = Path.Combine(AppContext.BaseDirectory, "case-output", "ESVision.ContourSmoke." + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "model.aiv2dmodel"); ContourTemplateEngine.Save(path, model); var loaded = ContourTemplateEngine.Load(path);
            Require(loaded.Version == 2 && loaded.Features.Count == model.Features.Count && loaded.OriginX == model.OriginX && loaded.SourceImagePng.Length == model.SourceImagePng.Length, "模型保存重载必须保留特征、原点和原图"); count++;
            var legacy = Path.Combine(directory, "old.aiv2dmodel"); Cv2.ImEncode(".bmp", teaching, out var bytes); File.WriteAllBytes(legacy, bytes);
            Require(ContourTemplateEngine.Load(legacy).ImportedLegacyImage, "旧BMP模板需要兼容导入"); count++;
            File.WriteAllText(path, "{invalid"); Expect<InvalidDataException>(() => ContourTemplateEngine.Load(path)); count++;
        }
        finally { foreach (var path in Directory.EnumerateFiles(directory)) File.Delete(path); Directory.Delete(directory); }
        Console.WriteLine($"Contour engine cases passed: {count}"); return count;
    }

    private static Mat TeachingImage()
    {
        var image = new Mat(180, 240, MatType.CV_8UC1, new Scalar(25));
        Cv2.Polylines(image, new[] { new[] { new Point(50, 55), new Point(50, 125), new Point(130, 125), new Point(130, 105), new Point(90, 105), new Point(90, 55) } }, true, new Scalar(225), 5);
        Cv2.Circle(image, new Point(153, 71), 13, new Scalar(200), 4); Cv2.Line(image, new Point(132, 100), new Point(169, 124), new Scalar(180), 4);
        return image;
    }
    private static Mat Place(Mat source, double x, double y, double angle, double scale)
    {
        var radians = angle * Math.PI / 180; var cos = Math.Cos(radians) * scale; var sin = Math.Sin(radians) * scale;
        using var matrix = new Mat(2, 3, MatType.CV_64FC1); matrix.SetArray(new[] { cos, -sin, x - cos * 110 + sin * 88, sin, cos, y - sin * 110 - cos * 88 });
        var target = new Mat(); Cv2.WarpAffine(source, target, matrix, new Size(640, 480), InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(25)); return target;
    }
    private static ParamBag TrainingParameters(bool shield = false)
    {
        var text = """
Region:
  Type: rect2d
  rect2d:
    Center: { X: 112, Y: 90 }
    Size: { Width: 150, Height: 110 }
    Rotation: 0
Model Origin: { Auto: false, X: 110, Y: 88, Angle: 17 }
Create Model:
  Num Levels: { Value: 4 }
  Angle Range: { Value: { min: -180, max: 180 } }
  Scale Range: { Value: { min: 0.7, max: 1.5 } }
Parameters:
  Edge Threshold: { Auto: false, Value: { min: 25, max: 80 } }
  Min Contrast: { Auto: false, Value: 4 }
  Min Size: { Auto: false, Value: 5 }
""";
        if (shield) text += "\nShielding Strokes:\n  - Type: polygon2d\n    polygon2d:\n      Points: [{X: 133, Y: 51}, {X: 173, Y: 51}, {X: 173, Y: 91}, {X: 133, Y: 91}]\n";
        var yaml = new YamlStream(); yaml.Load(new StringReader(text)); return ParamBag.FromYaml(yaml.Documents[0].RootNode);
    }
    private static ContourMatchOptions Options() => new() { AngleMin = -180, AngleMax = 180, ScaleMin = 1, ScaleMax = 1, AngleStep = 5, ScaleStep = .05, MinScore = .65, MaxMatches = 1, NumLevels = 4, SubPixel = true, TimeoutMilliseconds = 30000, MaxOverlap = .35 };
    private static string Describe(ContourMatch m) => $"({m.X:0.00},{m.Y:0.00}) rot={m.Rotation:0.00} scale={m.Scale:0.000} score={m.Score:0.000}";
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
