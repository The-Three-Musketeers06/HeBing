using System.IO;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Algorithm.Impl;
using OpenCvSharp;
using YamlDotNet.RepresentationModel;

public static class WorkflowCases
{
    public static int Run()
    {
        var count = 0;
        using var image = new Mat(200, 240, MatType.CV_8UC1, new Scalar(20));
        Cv2.Rectangle(image, new Rect(70, 55, 60, 90), new Scalar(230), 4);
        Cv2.Circle(image, new Point(160, 80), 15, new Scalar(180), 4);
        var model = ContourTemplateEngine.Train(image, Bag("Region: {Type: rect2d, rect2d: {Center: {X: 125,Y: 100}, Size: {Width: 170, Height: 150}, Rotation: 0}}"));
        var directory = Path.Combine(AppContext.BaseDirectory, "case-output"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "workflow.aiv2dmodel"); ContourTemplateEngine.Save(path, model);
        var context = new OpContext { ProjectDir = directory, Log = (_, _) => { } };
        try
        {
            context.Images.Set(0, image.Clone());
            var parameters = Bag("""
Model File: workflow.aiv2dmodel
Angle Range: {min: 0, max: 0}
Scale Range: {min: 1, max: 1}
Min Score: 0.65
Sub Pixel: true
Display Contours: true
Timeout: 30000
""");
            Require(new ShapeMatchOp().Run(context, parameters), "匹配算子应使用真实模型");
            Require(context.Vars.Get("Base Point") is IReadOnlyDictionary<string, object> && context.Vars.Get("Run Point") is IReadOnlyDictionary<string, object> && context.NativeOverlays.Count > 0, "匹配算子应发布可绑定位姿和结果图形"); count++;
            context.Images.Set(0, new Mat(image.Size(), MatType.CV_8UC1, Scalar.Black));
            Require(!new ShapeMatchOp().Run(context, parameters) && context.Vars.GetDouble("Matched Num") == 0 && context.Vars.Get("Run Point") == null && context.NativeOverlays.Count == 0, "失败不得残留成功匹配的位置或轮廓"); count++;

            context.Images.Set(0, new Mat(200, 240, MatType.CV_8UC1, Scalar.White));
            var crop = Bag("""
IM In: 0
IM Out: 1
Region: {Type: rect2d, rect2d: {Center: {X: 100,Y: 100}, Size: {Width: 60, Height: 30}, Rotation: 30}}
Rotate To Straight: true
""");
            Require(new CropImageOp().Run(context, crop) && context.Images.Get(1).Size() == new Size(60, 30), "旋转裁剪必须输出校正后的宽高"); count++;
            var fixedCrop = Bag("""
IM In: 0
IM Out: 1
Region: {Type: rect2d, rect2d: {Center: {X: 100,Y: 100}, Size: {Width: 60, Height: 30}, Rotation: 30}}
Rotate To Straight: true
Position Fix: true
Points Information:
  Base Point: {Point: {X: 100, Y: 100}, Angle: 30, ScaleX: 1, ScaleY: 1}
  Run Point: {Point: {X: 120, Y: 120}, Angle: 60, ScaleX: 1.2, ScaleY: 1.2}
""");
            Require(new CropImageOp().Run(context, fixedCrop) && context.Images.Get(1).Size() == new Size(72, 36) && Cv2.Mean(context.Images.Get(1)).Val0 > 250, "裁剪区域应按基准/运行位姿正确旋转缩放"); count++;

            var arrayTraining = Bag("""
Region:
  Type: rect2d array
  rect2d array:
    - {Center: {X: 98,Y: 100}, Size: {Width: 86, Height: 120}, Rotation: 0}
    - {Center: {X: 160,Y: 80}, Size: {Width: 42, Height: 42}, Rotation: 0}
""");
            var twoRegions = ContourTemplateEngine.Train(image, arrayTraining);
            using var mask = ContourTemplateEngine.DecodeMask(twoRegions);
            Require(twoRegions.Features.Count > 12 && Cv2.CountNonZero(mask) > 1000, "二维区域数组应产生联合遮罩并可训练"); count++;
            foreach (var region in new[]
            {
                "{Type: rect2d, rect2d: {Center: {X: 125, Y: 100}, Size: {Width: 170, Height: 150}, Rotation: 0}}",
                "{Type: circle2d, circle2d: {Center: {X: 120, Y: 100}, Radius: 105}}",
                "{Type: sector2d, sector2d: {Center: {X: 40, Y: 20}, Inner Radius: 0, Outer Radius: 185, Start Angle: 0, Angle Range: 90}}",
                "{Type: ring2d, ring2d: {Center: {X: 120, Y: 100}, Inner Radius: 25, Outer Radius: 110}}",
                "{Type: polygon2d, polygon2d: {Points: [{X: 40, Y: 25}, {X: 200, Y: 25}, {X: 200, Y: 180}, {X: 40, Y: 180}]}}"
            })
            {
                var shaped = ContourTemplateEngine.Train(image, Bag("Region: " + region));
                Require(shaped.Features.Count > 12, "五种区域均应支持真实轮廓训练");
            }
            count++;
        }
        finally { context.Images.Dispose(); if (File.Exists(path)) File.Delete(path); }
        Console.WriteLine($"Workflow cases passed: {count}"); return count;
    }
    private static ParamBag Bag(string text) { var yaml = new YamlStream(); yaml.Load(new StringReader(text)); return ParamBag.FromYaml(yaml.Documents[0].RootNode); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
