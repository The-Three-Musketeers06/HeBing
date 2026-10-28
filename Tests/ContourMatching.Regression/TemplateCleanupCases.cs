using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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

internal static class TemplateCleanupCases
{
    public static int Run()
    {
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); count++; }
        DriverCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "driver_defs.json"));
        using var image = new Mat(120, 220, MatType.CV_8UC1, Scalar.Black);
        foreach (int x in new[] { 30, 150 }) { Cv2.Rectangle(image, new OpenCvSharp.Rect(x, 30, 40, 40), Scalar.White, -1); Cv2.Circle(image, new OpenCvSharp.Point(x + 20, 50), 10, new Scalar(50), -1); }
        var training = Bag("Matching Method: Gray\nRegion: {Type: rect2d, rect2d: {Center: {X: 50,Y: 50}, Size: {Width: 60,Height: 60}, Rotation: 0}}\nCreate Model: {Angle Range: {Value: {min: 0,max: 0}}}");
        var model = ContourTemplateEngine.Train(image, training);
        string payload = ContourTemplateEngine.SerializeModel(model);
        using var left = new Mat(image.Size(), MatType.CV_8UC1, Scalar.Black); Cv2.Rectangle(left, new OpenCvSharp.Rect(0, 0, 105, 120), Scalar.White, -1);
        using var right = new Mat(); Cv2.BitwiseNot(left, right);
        var context = new OpContext { Log = (_, _) => { } };
        try
        {
            context.Images.Set(0, image.Clone()); context.Images.Set(1, left.Clone()); context.Images.Set(2, right.Clone());
            var op = new ShapeMatchOp(ContourTemplateEngine.GrayDriver);
            Check(op.Run(context, Parameters(payload, 2)) && Math.Abs(context.Vars.GetDouble("ShapeMatch.X") - 170) < 1, "掩膜必须限制NCC只匹配右侧目标");
            Check(context.NativeOverlays.Count(item => item.Type == "rect2d") == 1 && context.NativeOverlays.All(item => item.Type != "trace2d") && context.NativeOverlays.Count(item => item.Type == "point2d") == 1, "灰度结果即使带旧显示开关也只能有一个框和一个中心");
            var badRoot = Root(payload, 1); var badValues = (YamlMappingNode)badRoot.Children[new YamlScalarNode("value")];
            badValues.Add("Use Region", "true"); badValues.Add("Region", Yaml("Type: rect2d\nrect2d: {Center: {X: 170, Y: 50}, Size: {Width: 70, Height: 70}, Rotation: 0}"));
            var bad = ParamBag.FromYaml(badRoot);
            try { op.Run(context, bad); throw new InvalidOperationException("掩膜与搜索区域交集为空时必须拒绝运行"); }
            catch (ArgumentException error) { Check(error.Message.Contains("交集"), "空交集错误必须是清楚的中文提示"); }
            Check(context.Vars.Get("Run Point") == null && context.NativeOverlays.Count == 0, "无效掩膜不得残留上次的位姿和框");
            context.Images.Set(1, new Mat(20, 20, MatType.CV_8UC1, Scalar.White));
            try { op.Run(context, Parameters(payload, 1)); throw new InvalidOperationException("错误尺寸必须拒绝"); }
            catch (ArgumentException error) { Check(error.Message.Contains("尺寸"), "掩膜尺寸错误需要中文指导"); }
        }
        finally { context.Images.Dispose(); }
        if (Application.Current == null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Themes/Light.xaml", UriKind.Relative) });
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Views/ToolEditorResources.xaml", UriKind.Relative) });
        }
        var vm = new MainViewModel(Path.Combine(AppContext.BaseDirectory, "case-output", "cleanup-settings"));
        try
        {
            vm.AddTool(new() { Label = ContourTemplateEngine.GrayDriver }); var first = vm.SelectedOperation; first.ParametersRaw = Root(payload, 1);
            vm.AddTool(new() { Label = ContourTemplateEngine.GrayDriver }); var second = vm.SelectedOperation; second.ParametersRaw = Root(payload, 2);
            vm.SelectedOperation = first; vm.SelectedOperation = second;
            vm.Images.Set(0, image.Clone()); vm.Images.Set(1, left.Clone()); vm.Images.Set(2, right.Clone());
            vm.RunAll();
            Check(first.State == OpState.Ok && second.State == OpState.Ok, "连续两个NCC算子应分别使用自己的掩膜");
            Check(vm.NativeOverlays.Count(item => item.Type == "rect2d") == 2 && vm.GetSelectedPreviewOverlays(0).Count(item => item.Type == "rect2d") == 1, "窗口应隔离同一图层上其他算子的框");
            foreach (var source in vm.GetVisualSources())
                vm.UseVisualSnapshot(source.Key, snapshot => Check(snapshot.Overlays.Count(item => item.Type == "rect2d") == 1 && snapshot.Overlays.All(item => Equals(item.Values.GetValueOrDefault("_owned_operation"), source.Key.OperationId)), "结果快照必须只保存本算子的匹配框"));
            Check(vm.SelectedOutputs.All(row => row.Name != "Elapsed Time" && row.Name != "ShapeMatch.Scale") && vm.SelectedOutputs.Where(row => row.Name.StartsWith("ShapeMatch.")).All(row => !row.DisplayName.Contains("ShapeMatch") && !row.DisplayName.Contains("ElapsedMs")), "输出应汉化并隐藏重复耗时、灰度固定尺度");
            var flag = VarRow.FromOwned("ShapeMatch.TimedOut", false, ContourTemplateEngine.GrayDriver);
            Check(flag.DisplayName == "匹配超时" && flag.DisplayType == "布尔" && flag.DisplayValue == "否", "输出名称类型和值都应为中文");
            Check(vm.AllParameterNodes.Where(node => node.Parent == null).All(node => node.Name is not ("Model File" or "Display Contours" or "Display Rect Region" or "Base Point" or "Running Point" or "Scale Range")), "过时输入与冲突显示开关不得回到参数页");
            var window = new ParamsWindow(vm);
            try
            {
                Check(((System.Collections.ICollection)typeof(ParamsWindow).GetField("_shapeMatchRows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)).Count == 1, "重新打开参数窗口必须显示已有的匹配列表");
                var editor = (TemplateConfigurationWindow)typeof(ParamsWindow).GetMethod("CreateShapeTemplateConfiguration", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                Check(Children<Button>((DependencyObject)editor.Content).Any(button => Equals(button.Content, "涂抹屏蔽")) && Children<Button>((DependencyObject)editor.Content).Any(button => Equals(button.Content, "橡皮擦")), "模板创建窗口必须提供可见的掩膜画笔与橡皮擦入口");
                Render(editor, "mask-editor.png", 1180, 760);
                editor.Close();
                var tabs = (TabControl)typeof(ParamsWindow).GetField("_shapeTabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window); tabs.SelectedIndex = 2;
                window.RefreshPreview(); Render(window, "matching-result.png", 1280, 800);
                Check(((StackPanel)((ScrollViewer)((TabItem)((TabControl)typeof(ParamsWindow).GetField("_shapeTabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)).Items[2]).Content).Content).Children.OfType<DataGrid>().Single().Columns.All(column => !Equals(column.Header, "尺度")), "灰度匹配结果表不应显示固定尺度列");
            }
            finally { window.Close(); }
            string old = vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString();
            vm.WriteParameterValue(false, "Use Mask"); vm.WriteParameterValue(true, "Use Mask");
            Check(vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString() == old, "切换运行掩膜不能清掉已训练模板");
            vm.AddTool(new() { Label = "Basic Create ROI" }); var roi = vm.SelectedOperation;
            roi.ParametersRaw = Yaml("value:\n  IM In: 0\n  IM Out: 3\n  Enable Rect Region: true\n  Rect Region:\n    rect2d array:\n      - {Center: {X: 170,Y: 50}, Size: {Width: 70,Height: 70}, Rotation: 0}\n");
            vm.SelectedOperation = second; vm.SelectedOperation = roi;
            vm.RunAll();
            var outputs = vm.GetVisualSources().Where(source => source.Key.OperationId == roi.Uuid).ToArray();
            Check(outputs.Length == 2 && outputs.Any(source => source.OutputName == "图像") && outputs.Any(source => source.OutputName == "掩膜"), "创建ROI必须同时提供原图预览与真正的掩膜输出");
            vm.AddTool(new() { Label = ContourTemplateEngine.GrayDriver });
            var choice = vm.GetImageInputChoices(vm.SelectedOperation).Single(source => source.Reference.OperationId == roi.Uuid && source.OutputName == "掩膜");
            var maskNode = vm.AllParameterNodes.Single(node => node.Name == "Mask Im In");
            vm.WriteParameterValue(true, "Use Mask"); vm.StoreContourTemplate(model);
            old = vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString();
            vm.SetImageInput(maskNode, choice);
            Check(vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString() == old && vm.CreateSelectedParameterBag().ImageInputs["Mask Im In"].OutputParameter == "IM Out", "选择上游掩膜必须绑定正确输出且保留模板");
        }
        finally { vm.Dispose(); }
        Console.WriteLine($"Template display, mask and localization cases passed: {count}");
        return count;
    }
    private static void Render(System.Windows.Window window, string name, int width, int height)
    {
        window.Width = width; window.Height = height;
        var root = (FrameworkElement)window.Content; root.Measure(new System.Windows.Size(width, height)); root.Arrange(new System.Windows.Rect(0, 0, width, height)); root.UpdateLayout();
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
        if (window is ParamsWindow parameters) { parameters.RefreshPreview(); ((ImageView)parameters.FindName("Preview")).FitView(); }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".build", "matching-cleanup")); Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, name)); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
    private static ParamBag Parameters(string payload, int mask) => ParamBag.FromYaml(Root(payload, mask));
    private static YamlMappingNode Root(string payload, int mask)
    {
        var root = Yaml("value:\n  IM In: 0\n  Mask Im In: " + mask + "\n  Use Mask: true\n  Angle Range: {min: 0,max: 0}\n  Scale Range: {min: 1,max: 1}\n  Num Matches: 1\n  Display Center Point: true\n  Display Rect Region: true\n  Display Contours: true\n  Base Point: true\n  Running Point: true\n");
        var values = (YamlMappingNode)root.Children[new YamlScalarNode("value")]; values.Add(ContourTemplateStorage.ModelKey, payload); values.Add(ContourTemplateStorage.DirtyKey, "false"); return root;
    }
    private static ParamBag Bag(string text) => ParamBag.FromYaml(Yaml(text));
    private static YamlMappingNode Yaml(string text) { var yaml = new YamlStream(); yaml.Load(new StringReader(text)); return (YamlMappingNode)yaml.Documents[0].RootNode; }
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var item in Children<T>(child)) yield return item; } }
}
