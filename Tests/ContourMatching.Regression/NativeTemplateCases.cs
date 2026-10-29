using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Algorithm.Impl;
using AiVisionStudio.Models;
using AiVisionStudio.Services;
using AiVisionStudio.Views;
using OpenCvSharp;
using YamlDotNet.RepresentationModel;

internal static class NativeTemplateCases
{
    public static int Run()
    {
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); count++; }
        using var image = new Mat(150, 190, MatType.CV_8UC3, new Scalar(20, 25, 30));
        Cv2.Rectangle(image, new OpenCvSharp.Rect(45, 35, 47, 70), new Scalar(210, 220, 240), -1);
        Cv2.Circle(image, new OpenCvSharp.Point(115, 65), 15, new Scalar(130, 100, 240), -1);
        Cv2.Line(image, new OpenCvSharp.Point(100, 100), new OpenCvSharp.Point(145, 112), new Scalar(180, 230, 100), 5);
        var parameters = Bag("""
Matching Method: Gray
Region: {Type: rect2d, rect2d: {Center: {X: 95,Y: 75}, Size: {Width: 130,Height: 110}, Rotation: 0}}
Model Origin: {Auto: false, X: 90, Y: 70, Angle: 12}
Create Model:
  Angle Range: {Value: {min: -90,max: 90}}
""");
        var gray = ContourTemplateEngine.Train(image, parameters);
        Check(gray.Version == 1 && gray.Backend == ContourTemplateEngine.ShapeBackend && gray.NativeModels.Count == 0 && gray.Features.Count >= 12, "原灰度入口必须训练AiVisionStudio-1形状特征，不再生成NCC模板");
        Check(gray.SourceFingerprint == ContourTemplateEngine.ImageFingerprint(image), "模板必须保存实际源图指纹");
        var match = ContourTemplateEngine.Match(image, gray, new() { AngleMin = 0, AngleMax = 0, SubPixel = true });
        Check(match.Matches.Count == 1 && Math.Abs(match.Matches[0].X - 90) < 1 && Math.Abs(match.Matches[0].Y - 70) < 1, "形状匹配必须正确返回自定义原点");
        using var affine = new Mat(2, 3, MatType.CV_64FC1);
        double angle = 25 * Math.PI / 180, cosine = Math.Cos(angle), sine = Math.Sin(angle);
        affine.SetArray(new[] { cosine, -sine, 220 - cosine * 90 + sine * 70, sine, cosine, 170 - sine * 90 - cosine * 70 });
        using var moved = new Mat(); Cv2.WarpAffine(image, moved, affine, new OpenCvSharp.Size(420, 340), InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(20, 25, 30));
        var rotated = ContourTemplateEngine.Match(moved, gray, new() { AngleMin = -45, AngleMax = 45, SubPixel = true });
        Check(rotated.Matches.Count == 1 && Math.Abs(rotated.Matches[0].X - 220) < 1 && Math.Abs(rotated.Matches[0].Y - 170) < 1 && Math.Abs(rotated.Matches[0].Rotation - 25) < 1 && Math.Abs(rotated.Matches[0].Angle - 37) < 1, "形状匹配旋转与原点方向必须遵循顺时针图像坐标");
        using var blank = new Mat(image.Size(), image.Type(), new Scalar(20, 25, 30));
        Check(ContourTemplateEngine.Match(blank, gray, new()).Matches.Count == 0, "空白图不得匹配出形状目标");
        using var inverse = new Mat(); Cv2.BitwiseNot(image, inverse);
        Check(ContourTemplateEngine.Match(inverse, gray, new() { AngleMin = 0, AngleMax = 0, Polarity = "Ignore Global Polarity" }).Matches.Count == 1, "形状匹配忽略全局极性必须支持反色图");
        var restored = ContourTemplateEngine.DeserializeModel(ContourTemplateEngine.SerializeModel(gray));
        Check(ContourTemplateEngine.Match(image, restored, new() { AngleMin = 0, AngleMax = 0 }).Matches.Count == 1, "形状模板序列化后必须仍可匹配");
        DriverCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "driver_defs.json"));
        var definition = DriverCatalog.Get(ContourTemplateEngine.GrayDriver);
        Check(definition != null && new[] { "Scale Range", "Scale Step", "Angle Step", "Greediness", "Parameters" }.All(name => definition.Params.Any(p => p.Name == name)), "形状工具必须提供完整的尺度搜索和轮廓训练参数");
        if (Application.Current == null)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Themes/Light.xaml", UriKind.Relative) });
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Views/ToolEditorResources.xaml", UriKind.Relative) });
        }
        var vm = new MainViewModel(Path.Combine(AppContext.BaseDirectory, "case-output", "native-template-settings"));
        try
        {
            Check(vm.Tools.SelectMany(Leaves).Any(node => node.Label == ContourTemplateEngine.ShapeDriver && node.Display == "基于灰度匹配")
                && !vm.Tools.SelectMany(Leaves).Any(node => node.Label == "Basic Shape Model"), "工具列表应替换成两个完整匹配入口");
            vm.AddTool(new() { Label = ContourTemplateEngine.ShapeDriver, Cn = "基于灰度匹配" });
            Check(vm.AllParameterNodes.Any(node => node.Name == "Scale Range") && vm.AllParameterNodes.Any(node => node.Name == "Parameters"), "形状参数树必须包含尺度搜索与轮廓训练设置");
            vm.Images.Set(0, image.Clone());
            Check(vm.StoreContourTemplate(gray), "形状模板必须可保存到当前工程");
            var window = new ParamsWindow(vm);
            window.RefreshPreview();
            Render(window, "gray-teaching.png");
            Check(Field<TabControl>(window, "_shapeTabs").Items.Count == 3, "灰度工具必须有完整示教运行结果页面");
            using (var editor = new WindowScope((TemplateConfigurationWindow)Invoke(window, "CreateShapeTemplateConfiguration")))
            {
                Check(Field<Mat>(editor.Window, "_image").Size() == image.Size(), "模板创建窗口应读取当前输入");
                Check(Field<string>(editor.Window, "_matchingMethod") == "Shape" && Field<ComboBox>(editor.Window, "ThresholdMode").Visibility == Visibility.Visible, "形状模板配置必须使用轮廓训练并显示边缘阈值设置");
            }
            vm.Images.Set(0, new Mat(100, 140, MatType.CV_8UC3, Scalar.White));
            window.RefreshPreview(); Pump();
            Check(string.IsNullOrEmpty(vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString()) && vm.ReadParameterValue(ContourTemplateStorage.DirtyKey) is true, "换图必须清除旧模板并标记待训练");
            Check(Convert.ToDouble(vm.ReadParameterValue("Training Region", "rect2d", "Size", "Width")) == 0 && vm.ReadParameterValue("Model Origin", "Auto") is true, "换图必须清除旧示教区域并恢复自动原点");
            Check(Field<ContourTemplateModel>(window, "_shapeTemplate") == null && Field<Mat>(window, "_shapeTrainingImage") == null, "旧模板轮廓和训练图缓存必须清除");
            Check(Field<BitmapSource>(Field<ImageView>(window, "Preview"), "_bitmap").PixelWidth == 140, "示教预览必须立即显示替换的新图");
            Render(window, "gray-replaced.png");
            window.Close();
            vm.Images.Set(0, blank.Clone()); vm.StoreContourTemplate(gray); vm.SynchronizeSelectedContourInput();
            Check(!string.IsNullOrEmpty(vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString()), "另选示教图训练不应与当前检测图混淆，只有检测输入变化才清除新模板");
            vm.Images.Set(0, image.Clone()); vm.StoreContourTemplate(gray);
            vm.Images.Set(0, blank.Clone()); vm.RunAll();
            Check(vm.SelectedOperation.State == OpState.Fail && string.IsNullOrEmpty(vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString()) && vm.GetSelectedOutputValues().GetValueOrDefault("Run Point") == null, "关闭参数窗时换图也必须清除模板及匹配位姿");
        }
        finally { vm.Dispose(); }
        Console.WriteLine($"Shape template and image replacement cases passed: {count}");
        return count;
    }
    private static void Render(System.Windows.Window window, string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".build", "native-template"); Directory.CreateDirectory(directory);
        var root = (FrameworkElement)window.Content;
        root.Measure(new System.Windows.Size(1280, 800)); root.Arrange(new System.Windows.Rect(0, 0, 1280, 800)); root.UpdateLayout();
        Pump(); ((ImageView)window.FindName("Preview")).FitView();
        var bitmap = new RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        using var stream = File.Create(Path.Combine(directory, name)); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
    private static IEnumerable<ToolNode> Leaves(ToolNode node) => node.IsLeaf ? [node] : node.Children.SelectMany(Leaves);
    private static object Invoke(object target, string method) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
    private static T Field<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static ParamBag Bag(string text) { var yaml = new YamlStream(); yaml.Load(new StringReader(text)); return ParamBag.FromYaml(yaml.Documents[0].RootNode); }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private sealed class WindowScope(TemplateConfigurationWindow window) : IDisposable { public TemplateConfigurationWindow Window => window; public void Dispose() => window.Close(); }
}
