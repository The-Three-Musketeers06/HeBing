using System.IO;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Algorithm.Impl;
using OpenCvSharp;
using YamlDotNet.RepresentationModel;

internal static class ContourDisplayCases
{
    public static int Run()
    {
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); count++; }
        string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".build", "contour-display"));
        Directory.CreateDirectory(output);
        using var image = new Mat(260, 260, MatType.CV_8UC1, new Scalar(245));
        var hexagon = Enumerable.Range(0, 6).Select(index => new Point(130 + 100 * Math.Cos(index * Math.PI / 3), 130 + 100 * Math.Sin(index * Math.PI / 3))).ToArray();
        Cv2.FillPoly(image, [hexagon], new Scalar(70));
        var random = new Random(78);
        for (int index = 0; index < 180; index++)
        {
            int x = random.Next(65, 195), y = random.Next(65, 195);
            Cv2.Line(image, new Point(x, y), new Point(x + random.Next(-6, 7), y + random.Next(-6, 7)), new Scalar(random.Next(105, 185)), 1);
        }
        Cv2.Circle(image, new Point(130, 130), 40, new Scalar(245), -1);
        var model = ContourTemplateEngine.Train(image, Bag("Matching Method: Shape\n" + Region()));
        var serialized = ContourTemplateEngine.SerializeModel(model);
        var options = new ContourMatchOptions { AngleMin = 0, AngleMax = 0, TimeoutMilliseconds = 30000, SubPixel = true };
        var before = ContourTemplateEngine.Match(image, model, options);
        var drawing = ContourTemplateDisplay.GetContours(model);
        Console.WriteLine($"Synthetic textured shape: raw={model.Contours.Count}/{model.Contours.Sum(contour => contour.Count)} points, display={drawing.Count}/{drawing.Sum(contour => contour.Count)} points");
        Check(drawing.Count == 2, "有表面纹理的六角环应只显示外轮廓和真实内孔");
        Check(drawing.Sum(contour => contour.Count) < model.Contours.Sum(contour => contour.Count) / 3, "显示拟合必须显著消除纹理碎线与冗余点");
        Check(drawing.All(contour => Math.Abs(contour[0].X - contour[^1].X) < .01 && Math.Abs(contour[0].Y - contour[^1].Y) < .01), "完整轮廓拟合后应闭合");
        Check(ContourTemplateEngine.SerializeModel(model) == serialized, "生成显示轮廓不得修改任何训练数据或持久化模型");
        var after = ContourTemplateEngine.Match(image, model, options);
        Check(before.Matches.Count == after.Matches.Count && before.Matches.Zip(after.Matches).All(pair => pair.First.X == pair.Second.X && pair.First.Y == pair.Second.Y && pair.First.Angle == pair.Second.Angle && pair.First.Scale == pair.Second.Scale && pair.First.Score == pair.Second.Score), "显示调整前后定位与匹配分数必须完全一致");
        Render(image, model, model.Contours, Path.Combine(output, "textured-before.png"));
        Render(image, model, drawing, Path.Combine(output, "textured-after.png"));
        var masked = ContourTemplateEngine.Train(image, Bag("Matching Method: Shape\n" + Region() + "\nUse Shielding Area: true\nShielding Area: {Type: circle2d, circle2d: {Center: {X: 130,Y: 130}, Radius: 65}}\n"));
        var maskedDrawing = ContourTemplateDisplay.GetContours(masked);
        Check(maskedDrawing.Count == 1, "屏蔽内孔和表面纹理后应只显示主体外形");
        Check(maskedDrawing.SelectMany(contour => contour).All(point => Math.Pow(point.X + masked.OffsetX - 130, 2) + Math.Pow(point.Y + masked.OffsetY - 130, 2) > 65 * 65), "显示拟合不得补画被掩膜屏蔽的边界");
        var cut = ContourTemplateEngine.Train(image, Bag("Matching Method: Shape\n" + Region() + "\nUse Shielding Area: true\nShielding Area: {Type: rect2d, rect2d: {Center: {X: 130,Y: 45}, Size: {Width: 70,Height: 90}, Rotation: 0}}\n"));
        using (var mask = ContourTemplateEngine.DecodeMask(cut))
        {
            var clipped = ContourTemplateDisplay.GetContours(cut);
            Check(clipped.Count > 0 && clipped.SelectMany(contour => contour).All(point => mask.At<byte>((int)Math.Round(point.Y), (int)Math.Round(point.X)) != 0), "部分屏蔽时拟合轮廓必须在掩膜边界断开");
        }
        var context = new OpContext { Log = (_, _) => { } };
        try
        {
            context.Images.Set(0, image.Clone());
            var values = (YamlMappingNode)Yaml("IM In: 0\nAngle Range: {min: 0,max: 0}\nMatch Display: Contour\nTimeout: 30000\n");
            values.Add(ContourTemplateStorage.ModelKey, serialized); values.Add(ContourTemplateStorage.DirtyKey, "false");
            Check(new ShapeMatchOp(ContourTemplateEngine.ShapeDriver).Run(context, ParamBag.FromYaml(values)), "使用拟合显示后匹配算子仍应成功运行");
            Check(context.NativeOverlays.Count(overlay => overlay.Type == "trace2d") == drawing.Count, "匹配运行结果必须与训练窗口使用同一显示轮廓");
        }
        finally { context.Images.Dispose(); }

        string fixture = @"D:\Project\Images\nutsandbolts.bmp";
        if (File.Exists(fixture))
        {
            using var full = Cv2.ImRead(fixture, ImreadModes.Grayscale);
            using var nut = new Mat(full, new Rect(600, 0, 300, 280));
            var nutModel = ContourTemplateEngine.Train(nut, Bag("Matching Method: Shape\nRegion: {Type: rect2d, rect2d: {Center: {X: 150,Y: 140}, Size: {Width: 290,Height: 270}, Rotation: 0}}\nUse Shielding Area: true\nShielding Area: {Type: circle2d, circle2d: {Center: {X: 155,Y: 136}, Radius: 80}}\n"));
            string original = ContourTemplateEngine.SerializeModel(nutModel);
            var fitted = ContourTemplateDisplay.GetContours(nutModel);
            Console.WriteLine($"User nut fixture: raw={nutModel.Contours.Count}/{nutModel.Contours.Sum(contour => contour.Count)} points, display={fitted.Count}/{fitted.Sum(contour => contour.Count)} points");
            Check(fitted.Count is >= 1 and <= 3, "用户螺母图必须去除密集表面纹理，只保留少量主要轮廓");
            Check(fitted.Sum(contour => contour.Count) < nutModel.Contours.Sum(contour => contour.Count) / 3, "真实螺母显示必须显著减少纹理点");
            Check(ContourTemplateEngine.SerializeModel(nutModel) == original, "真实螺母的显示拟合也不能改变训练模板");
            Render(nut, nutModel, nutModel.Contours, Path.Combine(output, "nut-before.png"));
            Render(nut, nutModel, fitted, Path.Combine(output, "nut-after.png"));
        }
        Console.WriteLine($"Contour display cases passed: {count}"); return count;
    }

    private static string Region() => "Region: {Type: rect2d, rect2d: {Center: {X: 130,Y: 130}, Size: {Width: 240,Height: 240}, Rotation: 0}}\nParameters: {Edge Threshold: {Auto: false, Value: {min: 12,max: 35}}}\n";
    private static void Render(Mat image, ContourTemplateModel model, IEnumerable<IReadOnlyList<ContourTemplatePoint>> contours, string path)
    {
        using var display = new Mat(); Cv2.CvtColor(image, display, ColorConversionCodes.GRAY2BGR);
        foreach (var contour in contours)
            Cv2.Polylines(display, [contour.Select(point => new Point((int)Math.Round(point.X + model.OffsetX), (int)Math.Round(point.Y + model.OffsetY))).ToArray()], false, new Scalar(0, 255, 0), 1, LineTypes.AntiAlias);
        Cv2.ImWrite(path, display);
    }
    private static YamlNode Yaml(string text) { var yaml = new YamlStream(); yaml.Load(new StringReader(text)); return yaml.Documents[0].RootNode; }
    private static ParamBag Bag(string text) => ParamBag.FromYaml(Yaml(text));
}
