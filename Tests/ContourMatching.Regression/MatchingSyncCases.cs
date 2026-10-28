using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Algorithm.Impl;
using AiVisionStudio.Models;
using AiVisionStudio.Services;
using AiVisionStudio.Views;
using OpenCvSharp;
using YamlDotNet.RepresentationModel;

internal static class MatchingSyncCases
{
    public static int Run()
    {
        int count = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); count++; }
        DriverCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "driver_defs.json"));
        using var image = new Mat(140, 260, MatType.CV_8UC1, Scalar.Black);
        foreach (int x in new[] { 35, 175 })
        {
            Cv2.Rectangle(image, new OpenCvSharp.Rect(x, 40, 50, 50), Scalar.White, -1);
            Cv2.Rectangle(image, new OpenCvSharp.Rect(x + 12, 52, 26, 26), Scalar.Black, -1);
        }
        string training = "Region: {Type: rect2d, rect2d: {Center: {X: 60,Y: 65}, Size: {Width: 70,Height: 70}, Rotation: 0}}\nCreate Model: {Angle Range: {Value: {min: -180,max: 180}}}\n";
        var gray = ContourTemplateEngine.Train(image, Bag("Matching Method: Gray\n" + training));
        foreach (int limit in new[] { 2, 4, 0 })
        {
            var result = ContourTemplateEngine.Match(image, gray, new() { MaxMatches = limit, MaxOverlap = 1, MinScore = .85, SubPixel = true });
            Check(result.Matches.Count == 2, $"对称模板数量{limit}应返回两个物理目标，实际{result.Matches.Count}");
            Check(result.Matches.Count(match => match.X < 120) == 1 && result.Matches.Count(match => match.X > 120) == 1, "每个目标只能有一个位置与角度候选");
        }
        var custom = ContourTemplateEngine.Train(image, Bag("Matching Method: Gray\nModel Origin: {Auto: false, X: 30,Y: 30,Angle: 15}\n" + training));
        var customMatches = ContourTemplateEngine.Match(image, custom, new() { MaxMatches = 4, MaxOverlap = 1, MinScore = .85, SubPixel = true });
        Check(customMatches.Matches.Count == 2, "自定义原点不能干扰按目标实际位置去重");
        Check(ContourTemplateEngine.Match(image, gray, new() { MaxMatches = 1, MinScore = .85 }).Matches.Count == 1, "数量必须仍然限制最多返回目标数");

        if (Application.Current == null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Themes/Light.xaml", UriKind.Relative) });
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Views/ToolEditorResources.xaml", UriKind.Relative) });
        }
        foreach (double angle in new[] { 0d, 35d })
        {
            var sizes = new List<(int Width, int Height)>();
            foreach (double scale in new[] { .4, 1d, 5d })
            {
                var overlay = new NativeOverlay2D { ImageTransform = new MatrixTransform(new Matrix(scale, 0, 0, scale, 80 - 60 * scale, 80 - 65 * scale)) };
                overlay.SetOverlays([new NativeOverlay3D { Type = "origin2d", Values = new() { ["X"] = 60d, ["Y"] = 65d, ["Angle"] = angle, ["_size"] = 12, ["_color"] = "#FFE050" } }], 260, 140);
                overlay.Measure(new System.Windows.Size(160, 160)); overlay.Arrange(new System.Windows.Rect(0, 0, 160, 160)); overlay.UpdateLayout();
                var bitmap = new RenderTargetBitmap(160, 160, 96, 96, PixelFormats.Pbgra32); bitmap.Render(overlay);
                var pixels = new byte[160 * 160 * 4]; bitmap.CopyPixels(pixels, 640, 0);
                var occupied = Enumerable.Range(0, 160 * 160).Where(i => pixels[i * 4 + 3] > 80).ToArray();
                Check(occupied.Length > 0 && overlay.UnsupportedTypes.Count == 0, "原点方向标记应正确绘制");
                sizes.Add((occupied.Max(i => i % 160) - occupied.Min(i => i % 160), occupied.Max(i => i / 160) - occupied.Min(i => i / 160)));
            }
            Check(sizes.All(size => size == sizes[0]) && sizes.All(size => Math.Abs(size.Width - size.Height) <= 1), "不同缩放下十字两轴长度必须相等且固定屏幕尺寸");
        }

        var contour = ContourTemplateEngine.Train(image, Bag(training));
        using var left = new Mat(image.Size(), MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(left, new OpenCvSharp.Rect(0, 0, 130, 140), Scalar.White, -1);
        using var right = new Mat(); Cv2.BitwiseNot(left, right);
        var vm = new MainViewModel(Path.Combine(AppContext.BaseDirectory, "case-output", "matching-sync-settings"));
        try
        {
            vm.AddTool(new() { Label = ContourTemplateEngine.GrayDriver }); var first = vm.SelectedOperation; first.ParametersRaw = Root(gray, 1);
            vm.AddTool(new() { Label = "Basic Shape Match" }); var second = vm.SelectedOperation; second.ParametersRaw = Root(contour, 2);
            vm.SelectedOperation = first; vm.SelectedOperation = second;
            vm.Images.Set(0, image.Clone()); vm.Images.Set(1, left.Clone()); vm.Images.Set(2, right.Clone()); vm.RunAll();
            Check(first.State == OpState.Ok && second.State == OpState.Ok, "灰度和轮廓工具应分别匹配不同目标");
            vm.AddTool(new() { Label = "Basic Create ROI" }); var roi = vm.SelectedOperation;
            var window = new ParamsWindow(vm);
            try
            {
                foreach (var sourceOperation in new[] { first, second, first })
                {
                    var source = vm.GetImageInputChoices(roi).Single(source => source.Reference.OperationId == sourceOperation.Uuid);
                    var node = vm.AllParameterNodes.Single(node => node.Name == "IM In"); vm.SetImageInput(node, source);
                    window.RefreshPreview(); Pump();
                    using var raw = vm.CopySelectedInputImage();
                    Check(Cv2.Norm(image, raw, NormTypes.INF) == 0, "下游算法必须读取完全相同的原始像素");
                    var overlays = vm.GetSelectedPreviewOverlays(vm.RegionInputIndex);
                    Check(overlays.Count > 0 && overlays.All(item => Equals(item.Values.GetValueOrDefault("_owned_operation"), sourceOperation.Uuid)), "切换输入必须只显示对应算子的匹配标注");
                    var preview = (ImageView)window.FindName("Preview");
                    var displayed = Field<NativeOverlay3D[]>(preview, "_resultOverlays");
                    Check(displayed.Length == overlays.Count && displayed.All(item => Equals(item.Values.GetValueOrDefault("_owned_operation"), sourceOperation.Uuid)), "窗口必须立即同步新的来源标注");
                }
                vm.RunAll();
                var own = vm.GetVisualSources().Single(source => source.Key.OperationId == roi.Uuid && source.OutputName == "图像");
                vm.UseVisualSnapshot(own.Key, snapshot =>
                {
                    Check(Cv2.Norm(image, snapshot.Image, NormTypes.INF) == 0, "下游快照不得将标注烧入像素");
                    Check(snapshot.Overlays.Any(item => Equals(item.Values.GetValueOrDefault("_owned_operation"), first.Uuid)) && snapshot.Overlays.All(item => !Equals(item.Values.GetValueOrDefault("_owned_operation"), second.Uuid)), "下游运行后仍只继承已选来源的标注");
                });

                var secondSource = vm.GetImageInputChoices(roi).Single(source => source.Reference.OperationId == second.Uuid);
                vm.SetImageInput(vm.AllParameterNodes.Single(node => node.Name == "IM In"), secondSource); window.RefreshPreview();
                Check(vm.GetSelectedPreviewOverlays(vm.RegionInputIndex).All(item => Equals(item.Values.GetValueOrDefault("_owned_operation"), second.Uuid)), "已有输出时切换来源不得显示旧结果快照");
                vm.SetImageInput(vm.AllParameterNodes.Single(node => node.Name == "IM In"), null); window.RefreshPreview();
                Check(vm.GetSelectedPreviewOverlays(vm.RegionInputIndex).Count == 0 && vm.CopySelectedInputImage() == null, "清除输入后不得退回其他算子的全局标注");
            }
            finally { window.Close(); }
        }
        finally { vm.Dispose(); }
        Console.WriteLine($"Matching deduplication, origin and downstream preview cases passed: {count}");
        return count;
    }
    private static YamlMappingNode Root(ContourTemplateModel model, int mask)
    {
        var root = Yaml("value:\n  IM In: 0\n  Use Mask: true\n  Mask Im In: " + mask + "\n  Angle Range: {min: 0,max: 0}\n  Num Matches: 1\n  Display Center Point: true\n");
        var values = (YamlMappingNode)root.Children[new YamlScalarNode("value")];
        values.Add(ContourTemplateStorage.ModelKey, ContourTemplateEngine.SerializeModel(model)); values.Add(ContourTemplateStorage.DirtyKey, "false");
        return root;
    }
    private static T Field<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static ParamBag Bag(string text) => ParamBag.FromYaml(Yaml(text));
    private static YamlMappingNode Yaml(string text) { var yaml = new YamlStream(); yaml.Load(new StringReader(text)); return (YamlMappingNode)yaml.Documents[0].RootNode; }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
}
