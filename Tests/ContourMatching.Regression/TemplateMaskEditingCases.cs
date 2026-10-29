using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

internal static partial class TemplateMaskEditingCases
{
    public static int Run()
    {
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); count++; }
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".build", "mask-editing"));
        Directory.CreateDirectory(output);
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.Resources.MergedDictionaries.Count == 0)
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Themes/Light.xaml", UriKind.Relative) });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Views/ToolEditorResources.xaml", UriKind.Relative) });
        }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        DriverCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "driver_defs.json"));
        using var image = new Mat(180, 240, MatType.CV_8UC1, new Scalar(20));
        Cv2.Rectangle(image, new OpenCvSharp.Rect(55, 55, 55, 70), Scalar.White, 5);
        Cv2.Circle(image, new OpenCvSharp.Point(155, 70), 15, new Scalar(180), 4);
        Cv2.Line(image, new OpenCvSharp.Point(130, 105), new OpenCvSharp.Point(185, 125), new Scalar(210), 5);
        using var region = new Mat(image.Size(), MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(region, new OpenCvSharp.Rect(30, 20, 181, 141), Scalar.White, -1);
        var vm = new MainViewModel(Path.Combine(output, "settings"));
        try
        {
            vm.AddTool(new ToolNode { Label = ContourTemplateEngine.ShapeDriver, Cn = "基于灰度匹配" });
            vm.Images.Set(0, image.Clone());
            vm.WriteParameterValue(Rectangle(120, 90, 180, 140), "Training Region");
            var parent = new ParamsWindow(vm);
            var editor = (TemplateConfigurationWindow)Call(parent, "CreateShapeTemplateConfiguration");
            try
            {
                Render(editor, output, "layout.png", 1180, 760);
                Check(editor.Title == "模板配置", "弹窗标题应与图二一致");
                Check(Field<StackPanel>(editor, "MaskTools").Children.OfType<Button>().Count() == 12, "掩膜工具栏应有五个绘图工具和填充、清空、反选、历史、应用取消按钮");
                var original = MaskState(Field<Dictionary<string, object>>(editor, "_draft"));
                Call(editor, "FillMask");
                Check(MaskState(Field<Dictionary<string, object>>(editor, "_draft")) == original && Pixels(editor, region, true) == 181 * 141, "全部填充只能改编辑预览且只作用于模板区域");
                Call(editor, "OnTrain", null, new RoutedEventArgs());
                Check(!editor.IsTraining && vm.ReadParameterValue(ContourTemplateStorage.ModelKey) == null && Field<TextBlock>(editor, "Status").Text.Contains("应用"), "未应用掩膜不得隐式进入训练或写入工程");
                Call(editor, "InvertMask"); Check(Pixels(editor, region, true) == 0, "反选全部填充应得到空掩膜");
                Call(editor, "UndoMask"); Check(Pixels(editor, region, true) == 181 * 141, "撤销应恢复完整掩膜");
                Call(editor, "RedoMask"); Check(Pixels(editor, region, true) == 0, "重做应恢复反选结果");
                Call(editor, "CancelMask");
                Check(Field<Dictionary<string, object>>(editor, "_maskWorking") == null && MaskState(Field<Dictionary<string, object>>(editor, "_draft")) == original, "取消修改必须完整恢复编辑前掩膜");
                foreach (var (type, shape) in new[]
                {
                    ("rect2d", Rectangle(35, 35, 80, 80)),
                    ("circle2d", Map(new { Type = "circle2d", circle2d = new { Center = new { X = 155d, Y = 70d }, Radius = 20d } })),
                    ("polygon2d", Map(new { Type = "polygon2d", polygon2d = new { Points = new[] { new { X = 120d, Y = 100d }, new { X = 190d, Y = 100d }, new { X = 190d, Y = 140d } } } }))
                })
                {
                    Call(editor, "BeginMaskRegion", type);
                    var canvas = Field<ImageView>(editor, "CanvasView");
                    var commit = Field<Action<Dictionary<string, object>>>(canvas, "_regionCommit");
                    canvas.BeginRegionEdit(type, shape, commit); canvas.CompleteRegionEdit();
                    Check(Pixels(editor, region, true) > 0, type + "必须生成真实掩膜");
                    using var mask = Mask(editor, region, true);
                    Check(mask.At<byte>(0, 0) == 0 && mask.At<byte>(179, 239) == 0, "绘图不能屏蔽模板区域外的像素");
                    Call(editor, "CancelMask");
                }
                Call(editor, "BeginPaint", false);
                PaintStroke(editor, new System.Windows.Point(60, 70), new System.Windows.Point(90, 70));
                int painted = Pixels(editor, region, true);
                Check(painted > 0 && Field<ImageView>(editor, "CanvasView").IsRegionEditing, "连续画笔每笔应进入预览并保持绘制模式");
                var canvasViewForErase = Field<ImageView>(editor, "CanvasView");
                var paintedColor = RenderPixel(canvasViewForErase, new System.Windows.Point(80, 70));
                Check(paintedColor.R > paintedColor.G, "掩膜画笔应正常显示红色屏蔽区域");
                Call(editor, "BeginPaint", true);
                BeginPreviewStroke(editor, new System.Windows.Point(60, 70), new System.Windows.Point(90, 70), new System.Windows.Point(180, 145));
                var erasingColor = RenderPixel(canvasViewForErase, new System.Windows.Point(80, 70));
                var unmaskedColor = RenderPixel(canvasViewForErase, new System.Windows.Point(180, 145));
                Check(erasingColor == (20, 20, 20), "按住橡皮擦时必须直接露出原图，不能覆盖红色或其他颜色笔迹");
                Check(unmaskedColor == (20, 20, 20), "橡皮擦经过没有掩膜的区域时不能增加任何颜色");
                Check(Pixels(editor, region, true) == painted, "橡皮擦拖动预览不能提前提交或破坏掩膜编辑历史");
                Render(editor, output, "eraser-in-progress.png", 1180, 760);
                canvasViewForErase.CancelRegionEdit();
                Check(RenderPixel(canvasViewForErase, new System.Windows.Point(80, 70)) == paintedColor, "取消当前橡皮擦笔画必须恢复擦除前的掩膜显示");
                Call(editor, "BeginPaint", true);
                BeginPreviewStroke(editor, new System.Windows.Point(60, 70), new System.Windows.Point(90, 70));
                var liveAlpha = MaskAlpha(canvasViewForErase);
                FinishPreviewStroke(editor);
                Check(Pixels(editor, region, true) == 0, "橡皮擦应恢复同一区域的特征");
                Check(liveAlpha.SequenceEqual(MaskAlpha(canvasViewForErase)), "橡皮擦拖动预览与松开后的所有掩膜像素必须完全一致，不能留下边缘残色");
                Check(Field<Image>(canvasViewForErase, "_templateMaskImage").Clip == null && RenderPixel(canvasViewForErase, new System.Windows.Point(80, 70)) == (20, 20, 20), "松开橡皮擦后显示应与擦除预览一致，且不能留下临时裁剪");
                Call(editor, "UndoMask"); Check(Pixels(editor, region, true) == painted, "每笔橡皮擦必须可独立撤销");
                Check(RenderPixel(canvasViewForErase, new System.Windows.Point(80, 70)) == paintedColor, "撤销橡皮擦必须同时恢复原来的红色掩膜显示");
                Call(editor, "CancelMask");

                Call(editor, "FillMask"); Call(editor, "ApplyMask");
                Call(editor, "OnTrain", null, new RoutedEventArgs()); Wait(editor);
                Check(vm.ReadParameterValue(ContourTemplateStorage.ModelKey) == null, "全部屏蔽时训练失败不得生成伪模板");
                Call(editor, "ClearMask");
                var patch = Rectangle(45, 40, 80, 60);
                Call(editor, "AddMaskStrokes", new Dictionary<string, object>[] { patch }, false);
                int partial = Pixels(editor, region, true);
                Call(editor, "InvertMask");
                Check(Pixels(editor, region, true) == 181 * 141 - partial, "局部掩膜反选必须是模板区域内的布尔补集");
                Call(editor, "InvertMask"); Check(Pixels(editor, region, true) == partial, "反选两次必须逐像素恢复");
                Render(editor, output, "mask-pending.png", 1180, 760);
                Call(editor, "ApplyMask");
                Check(Pixels(editor, region, false) == partial && vm.ReadParameterValue(ContourTemplateStorage.ModelKey) == null, "应用只提交训练草稿，不提前写工程");
                Call(editor, "OnTrain", null, new RoutedEventArgs()); Wait(editor);
                var model = Field<ContourTemplateModel>(editor, "_model");
                Check(model?.Backend == ContourTemplateEngine.ShapeBackend && vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString()?.Length > 0, "训练成功必须保存真实形状模板");
                using (var mask = Mask(editor, region, false))
                    Check(model.Features.All(feature => mask.At<byte>((int)Math.Round(feature.Y + model.OffsetY), (int)Math.Round(feature.X + model.OffsetX)) == 0), "训练特征不得落入红色掩膜");
                var canvasView = Field<ImageView>(editor, "CanvasView");
                var imageSize = Field<ComboBox>(editor, "ImageSize"); imageSize.SelectedIndex = 3;
                Check(Math.Abs(canvasView.TemplateZoom - 1) < 1e-8 && !canvasView.TemplateFitsWindow, "100%必须按原始像素显示");
                canvasView.ZoomTemplateCanvas(1.2);
                Check(imageSize.SelectedIndex == 6, "手动缩放后下拉框必须同步为自定义");
                imageSize.SelectedIndex = 0; Check(canvasView.TemplateFitsWindow, "适应窗口必须恢复自动适配");
                Render(editor, output, "mask-trained.png", 1180, 760);
                var path = Path.Combine(output, "mask-project.aivs");
                Check(ProjectFile.SaveAll(path, vm.Programs), "工程必须可保存掩膜");
                var reopened = new MainViewModel(Path.Combine(output, "reopened"));
                try
                {
                    reopened.OpenProject(path); reopened.SelectedOperation = reopened.Operations.Single(); reopened.Images.Set(0, image.Clone());
                    var restoredParent = new ParamsWindow(reopened);
                    var restoredEditor = (TemplateConfigurationWindow)Call(restoredParent, "CreateShapeTemplateConfiguration");
                    try { Check(Pixels(restoredEditor, region, false) == partial, "工程重开后必须完整保留反选和擦除顺序"); }
                    finally { restoredEditor.Close(); restoredParent.Close(); }
                }
                finally { reopened.Dispose(); }
            }
            finally { editor.Close(); parent.Close(); }
        }
        finally { vm.Dispose(); }
        Console.WriteLine($"Template mask editing cases passed: {count}"); return count;
    }

    private static void PaintStroke(TemplateConfigurationWindow editor, params System.Windows.Point[] points)
    {
        BeginPreviewStroke(editor, points);
        FinishPreviewStroke(editor);
    }
    private static void FinishPreviewStroke(TemplateConfigurationWindow editor)
    {
        var canvas = Field<ImageView>(editor, "CanvasView");
        Call(canvas, "RegionMouseUp", null, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseUpEvent });
    }
    private static void BeginPreviewStroke(TemplateConfigurationWindow editor, params System.Windows.Point[] points)
    {
        var canvas = Field<ImageView>(editor, "CanvasView");
        Field<List<List<System.Windows.Point>>>(canvas, "_maskStrokes").Add(points.ToList());
        canvas.GetType().GetField("_regionPointerDown", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(canvas, true);
        Call(canvas, "UpdateRegionEditor");
    }
    private static (int R, int G, int B) RenderPixel(ImageView canvas, System.Windows.Point imagePoint)
    {
        Pump();
        var host = Field<Grid>(canvas, "Host");
        var transform = Field<MatrixTransform>(canvas, "Mat");
        var point = transform.Matrix.Transform(imagePoint);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)Math.Round(point.X), (int)Math.Round(point.Y), 1, 1), pixel, 4, 0);
        return (pixel[2], pixel[1], pixel[0]);
    }
    private static byte[] MaskAlpha(ImageView canvas)
    {
        var bitmap = (BitmapSource)Field<Image>(canvas, "_templateMaskImage").Source;
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return Enumerable.Range(0, bitmap.PixelWidth * bitmap.PixelHeight).Select(index => pixels[index * 4 + 3]).ToArray();
    }
    private static Dictionary<string, object> Rectangle(double x, double y, double width, double height)
        => Map(new { Type = "rect2d", rect2d = new { Center = new { X = x, Y = y }, Size = new { Width = width, Height = height }, Rotation = 0d } });
    private static Dictionary<string, object> Map(object value)
    {
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!.ToDictionary(pair => pair.Key, pair => ConvertElement(pair.Value));
    }
    private static object ConvertElement(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(property => property.Name, property => ConvertElement(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().Select(ConvertElement).ToList(),
        JsonValueKind.Number => value.GetDouble(), JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.String => value.GetString(), _ => null
    };
    private static Mat Mask(TemplateConfigurationWindow editor, Mat region, bool working)
    {
        var draft = Field<Dictionary<string, object>>(editor, working ? "_maskWorking" : "_draft");
        var yaml = new YamlStream(); yaml.Load(new StringReader(new YamlDotNet.Serialization.SerializerBuilder().Build().Serialize(draft)));
        return TemplateShieldingMask.Create(region.Size(), ParamBag.FromYaml(yaml.Documents[0].RootNode), region);
    }
    private static int Pixels(TemplateConfigurationWindow editor, Mat region, bool working) { using var mask = Mask(editor, region, working); return Cv2.CountNonZero(mask); }
    private static string MaskState(Dictionary<string, object> draft) => JsonSerializer.Serialize(new[] { "Use Shielding Area", "Shielding Area", "Shielding Strokes" }.ToDictionary(key => key, key => draft.GetValueOrDefault(key)));
    private static T Field<T>(object source, string name) => (T)source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source);
    private static object Call(object source, string name, params object[] args) => source.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(source, args);
    private static void Wait(TemplateConfigurationWindow editor)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (editor.IsTraining) { if (watch.ElapsedMilliseconds > 10000) throw new TimeoutException(); Pump(); Thread.Sleep(5); }
        Pump();
    }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static void Render(System.Windows.Window window, string directory, string name, int width, int height)
    {
        var root = (FrameworkElement)window.Content; root.Measure(new System.Windows.Size(width, height)); root.Arrange(new System.Windows.Rect(0, 0, width, height)); root.UpdateLayout(); Pump();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        using var stream = File.Create(Path.Combine(directory, name)); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
}
