using System.Collections;
using System.IO;
using System.Reflection;
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

internal static class PostTrainingMatchCases
{
    public static int Run()
    {
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); count++; }
        string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".build", "post-training"));
        Directory.CreateDirectory(output);
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.Resources.MergedDictionaries.Count == 0)
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Themes/Light.xaml", UriKind.Relative) });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Views/ToolEditorResources.xaml", UriKind.Relative) });
        }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        DriverCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "driver_defs.json"));
        using var tile = new Mat(100, 100, MatType.CV_8UC1, new Scalar(20));
        Cv2.Polylines(tile, [new[] { new OpenCvSharp.Point(23, 20), new OpenCvSharp.Point(23, 80), new OpenCvSharp.Point(68, 80), new OpenCvSharp.Point(68, 65), new OpenCvSharp.Point(43, 65), new OpenCvSharp.Point(43, 20) }], true, Scalar.White, 5);
        Cv2.Circle(tile, new OpenCvSharp.Point(73, 32), 10, new Scalar(180), 4);
        using var scene = Place(tile, 145, 165, 0);
        using (var second = Place(tile, 445, 190, 27)) Cv2.Max(scene, second, scene);
        var vm = new MainViewModel(Path.Combine(output, "settings"));
        try
        {
            // This unconfigured acquisition step would fail if the normal TestThrough path ran it.
            vm.AddTool(new ToolNode { Label = "LoadImage", Cn = "前置采集不得重跑" });
            var source = vm.SelectedOperation;
            vm.AddTool(new ToolNode { Label = ContourTemplateEngine.ShapeDriver, Cn = "基于灰度匹配" });
            vm.Images.Set(0, scene.Clone());
            vm.WriteParameterValue(new { Type = "rect2d", rect2d = new { Center = new { X = 145d, Y = 165d }, Size = new { Width = 130d, Height = 130d }, Rotation = 0d } }, "Training Region");
            vm.WriteParameterValue(30000, "Timeout");
            Check(Convert.ToInt32(vm.ReadParameterValue("Num Matches")) == 1, "新建工具应默认使用明确的最大匹配个数");
            vm.WriteParameterValue(2, "Num Matches");
            int completed = 0;
            var engine = Field<ExecutionEngine>(vm, "_engine");
            engine.OperationCompleted += operation => { if (operation == vm.SelectedOperation) Interlocked.Increment(ref completed); };
            var window = new ParamsWindow(vm);
            try
            {
                var editor = (TemplateConfigurationWindow)Call(window, "CreateShapeTemplateConfiguration");
                Check(!editor.HasSavedTraining, "仅打开创建窗口不得误报本次已训练");
                Call(editor, "OnTrain", null, new RoutedEventArgs()); Wait(() => !editor.IsTraining);
                Check(editor.HasSavedTraining, "训练成功必须保留已保存状态");
                Check(Convert.ToInt32(vm.ReadParameterValue("Num Matches")) == 2 && vm.ReadParameterValue("Match Display")?.ToString() == "Rectangle" && vm.ReadParameterValue("Display Center Point") is true, "训练必须保留手动匹配个数，只更新外框和中心十字显示");
                var trained = Field<ContourTemplateModel>(editor, "_model");
                editor.Close();
                PumpFor(700);
                Check(completed == 0 && !vm.IsRunning && !vm.GetSelectedOutputValues().ContainsKey("Matches"), "训练和关闭创建窗口后不能自动运行匹配");
                var tabs = Field<TabControl>(window, "_shapeTabs");
                Check(tabs.SelectedIndex == 0 && Field<TextBlock>(window, "PreviewMode").Text == "灰度模板示教", "示教页应只显示创建时的模板区域");
                var teaching = Field<NativeOverlay3D[]>(Field<ImageView>(window, "Preview"), "_resultOverlays");
                Check(teaching.Count(item => item.Type == "rect2d") == 1 && teaching.All(item => item.Type is "rect2d" or "point2d"), "示教页不得显示其他匹配位置、纹理或掩膜");
                tabs.SelectedIndex = 1;
                tabs.SelectedIndex = 2;
                tabs.SelectedIndex = 1;
                PumpFor(700);
                Check(completed == 0 && !vm.IsRunning, "切换运行参数和结果页面也不能自动匹配");
                RunMatch(window, vm);
                var results = vm.GetSelectedOutputValues();
                Check(vm.SelectedOperation.State == OpState.Ok && Convert.ToInt32(results["Matched Num"]) == 2, "点击测试后才按设置的数量匹配两个目标");
                Check(source.State == OpState.None && source.ElapsedMs == 0 && source.ErrorMessage == null, "手动匹配当前图像也不得触发前置采集");
                Check(Cv2.Norm(scene, vm.Images.Get(0), NormTypes.INF) == 0, "手动匹配不能重载输入图像或将框烧入像素");
                var overlays = Field<NativeOverlay3D[]>(Field<ImageView>(window, "Preview"), "_resultOverlays");
                Check(overlays.Count(item => item.Type == "rect2d") == 2 && overlays.Count(item => item.Type == "point2d") == 2 && overlays.All(item => item.Type is "rect2d" or "point2d"), "外部只显示每个目标的旋转外框和中心十字，不能残留示教框、轮廓或掩膜");
                Check(overlays.All(item => item.Values["_color"].ToString() == "#00FF00") && overlays.Where(item => item.Type == "point2d").All(item => item.Values["_marker"].ToString() == "cross"), "标注必须为绿色并采用中心十字");
                Check(overlays.Where(item => item.Type == "rect2d").Any(item => Math.Abs(Convert.ToDouble(item.Values["Rotation"]) - 27) < 2), "旋转目标的框必须随实际匹配角度旋转");
                Check(((ICollection)Field<object>(window, "_shapeMatchRows")).Count == 2, "所有匹配行必须同步更新");
                var table = Field<DataGrid>(window, "_shapeMatchTable");
                Check(table.SelectedItem == null && Field<int>(window, "_shapeSelectedMatchIndex") == -1, "匹配完成后不得默认选择或突出显示任何目标");
                Call(window, "OnShapeImagePointClicked", Field<ImageView>(window, "Preview"), new ImagePointClickedEventArgs(new System.Windows.Point(445, 190)));
                Check(tabs.SelectedIndex == 2 && table.SelectedIndex == 1 && Field<int>(window, "_shapeSelectedMatchIndex") == 1, "点击第二个绿框必须切换到结果页并选择对应匹配信息");
                var selected = Field<NativeOverlay3D[]>(Field<ImageView>(window, "Preview"), "_resultOverlays");
                Check(selected.Where(item => Equals(item.Values.GetValueOrDefault("_match_index"), 1)).All(item => Convert.ToDouble(item.Values["_thickness"]) >= 3)
                    && vm.NativeOverlays.Where(item => Equals(item.Values.GetValueOrDefault("_match_index"), 1)).All(item => Convert.ToDouble(item.Values["_thickness"]) < 3), "选中绿框只在当前预览加粗，不能污染算法输出或其他视图");
                Check(Field<TextBlock>(window, "_shapeSelectionInfo").Text.Contains("#2") && Field<TextBlock>(window, "_shapeSelectionInfo").Text.Contains("445"), "选中框应显示该目标的位置、角度、尺度与分数");
                Call(window, "OnShapeImagePointClicked", Field<ImageView>(window, "Preview"), new ImagePointClickedEventArgs(new System.Windows.Point(610, 10)));
                Check(table.SelectedItem == null && Field<int>(window, "_shapeSelectedMatchIndex") == -1, "点击图像空白处必须取消选择和突出显示");
                table.SelectedIndex = 0;
                Check(Field<int>(window, "_shapeSelectedMatchIndex") == 0 && Field<NativeOverlay3D[]>(Field<ImageView>(window, "Preview"), "_resultOverlays")
                    .Where(item => item.Type == "rect2d" && Equals(item.Values.GetValueOrDefault("_match_index"), 0)).All(item => Convert.ToDouble(item.Values["_thickness"]) >= 3), "选择匹配信息行必须突出对应的绿框");
                Render(window, Path.Combine(output, "selected-target.png"));
                tabs.SelectedIndex = 0;
                Check(Field<int>(window, "_shapeSelectedMatchIndex") == -1 && Field<NativeOverlay3D[]>(Field<ImageView>(window, "Preview"), "_resultOverlays").Count(item => item.Type == "rect2d") == 1, "切回示教页必须只显示原模板框，并清除结果选择");
                Render(window, Path.Combine(output, "teaching-only.png"));
                tabs.SelectedIndex = 1;
                var preview = Field<ImageView>(window, "Preview");
                var transform = Field<MatrixTransform>(preview, "Mat");
                transform.Matrix = new Matrix(1.6, 0, 0, 1.6, -300, 70);
                var pixel = new System.Windows.Point(445, 190);
                Check(preview.TryGetImagePoint(transform.Matrix.Transform(pixel), out var mapped) && (mapped - pixel).Length < .00001
                    && (int)Call(window, "HitTestShapeMatch", mapped) == 1, "图像平移缩放后点击位置仍必须定位到同一个绿框");
                Check((int)Call(window, "HitTestShapeMatch", new System.Windows.Point(525, 190)) == -1, "旋转框的命中测试必须使用实际旋转矩形，不能误选其外接矩形的空角");
                vm.WriteParameterValue(1, "Num Matches");
                int previous = completed;
                PumpFor(700);
                Check(completed == previous && !vm.IsRunning && table.Items.Count == 0 && Field<NativeOverlay3D[]>(preview, "_resultOverlays").Length == 0, "修改匹配个数只能清除旧显示，不得自动执行");
                RunMatch(window, vm);
                Check(Convert.ToInt32(vm.GetSelectedOutputValues()["Matched Num"]) == 1 && table.SelectedItem == null, "点击测试后必须使用手动设置的匹配个数且不默认选中");
                vm.WriteParameterValue(0, "Num Matches");
                RunMatch(window, vm);
                Check(Field<NativeOverlay3D[]>(preview, "_resultOverlays").Count(item => item.Type == "rect2d") == 2 && Convert.ToInt32(vm.ReadParameterValue("Num Matches")) == 0, "手动设为0并测试后才显示全部目标");
                vm.WriteParameterValue(new { min = 0d, max = 0d }, "Angle Range");
                previous = completed; PumpFor(600);
                Check(completed == previous && table.Items.Count == 0, "修改搜索角度不得自动匹配");
                RunMatch(window, vm);
                Check(Convert.ToInt32(vm.GetSelectedOutputValues()["Matched Num"]) == 1, "点击测试后必须按搜索角度排除范围外的目标");
                vm.WriteParameterValue(new { min = -45d, max = 45d }, "Angle Range");
                RunMatch(window, vm);
                var scoreNode = vm.AllParameterNodes.Single(node => node.Path.SequenceEqual(new[] { "Min Score" }));
                scoreNode.Value = "abc";
                previous = completed; PumpFor(600);
                Check(Field<NativeOverlay3D[]>(preview, "_resultOverlays").Length == 0 && table.Items.Count == 0, "运行参数无效时必须隐藏旧框和旧匹配信息");
                RunMatch(window, vm);
                Check(completed == previous && Field<TextBlock>(window, "TestStatus").Text.Contains("修正无效参数"), "参数无效时点击测试必须阻止执行并提示修正");
                vm.AllParameterNodes.Single(node => node.Path.SequenceEqual(new[] { "Min Score" })).Value = "0.6";
                vm.AllParameterNodes.Single(node => node.Path.SequenceEqual(new[] { "Num Matches" })).Value = "1";
                vm.AllParameterNodes.Single(node => node.Path.SequenceEqual(new[] { "Num Matches" })).Value = "0";
                Check(!vm.HasParameterErrors, "参数输入框必须允许手动设置0表示全部");
                PumpFor(800);
                Check(completed == previous && !vm.IsRunning && table.Items.Count == 0, "修正输入或连续修改参数后仍不能自动匹配");
                RunMatch(window, vm);
                Check(table.Items.Count == 2 && completed == previous + 1, "点击一次测试只能执行一次，并使用最终设置");
                Check(source.State == OpState.None && source.ElapsedMs == 0, "连续手动测试不得重新执行上游采集");
                tabs.SelectedIndex = 2;
                Check(table.SelectedItem == null && Field<int>(window, "_shapeSelectedMatchIndex") == -1, "返回结果页时也不得自动突出任何目标");
                Render(window, Path.Combine(output, "all-targets.png"));
                using var blank = new Mat(scene.Size(), scene.Type(), new Scalar(20));
                vm.Images.Set(0, blank.Clone()); vm.StoreContourTemplate(trained); window.RefreshPreview();
                RunMatch(window, vm);
                Check(vm.SelectedOperation.State == OpState.Fail && vm.GetSelectedOutputValues().GetValueOrDefault("Run Point") == null, "没有匹配时不得保留上次的位姿");
                Check(Field<NativeOverlay3D[]>(Field<ImageView>(window, "Preview"), "_resultOverlays").Length == 0 && ((ICollection)Field<object>(window, "_shapeMatchRows")).Count == 0, "运行页匹配失败必须清空全部外框、十字和列表");
            }
            finally { if (vm.HasParameterErrors) vm.DiscardInvalidParameterEdits(); window.Close(); }
        }
        finally { vm.Dispose(); }
        Console.WriteLine($"Manual matching, parameter persistence and selection cases passed: {count}"); return count;
    }

    private static Mat Place(Mat tile, double x, double y, double angle)
    {
        double a = angle * Math.PI / 180, cosine = Math.Cos(a), sine = Math.Sin(a);
        using var affine = new Mat(2, 3, MatType.CV_64FC1);
        affine.SetArray(new[] { cosine, -sine, x - cosine * 50 + sine * 50, sine, cosine, y - sine * 50 - cosine * 50 });
        var result = new Mat(); Cv2.WarpAffine(tile, result, affine, new OpenCvSharp.Size(620, 360), InterpolationFlags.Linear, BorderTypes.Constant, new Scalar(20)); return result;
    }
    private static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void Wait(Func<bool> done)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done()) { if (watch.ElapsedMilliseconds > 35000) throw new TimeoutException("手动测试未完成"); Pump(); Thread.Sleep(5); }
        Pump();
    }
    private static void RunMatch(ParamsWindow window, MainViewModel vm)
    {
        Call(window, "OnTest", null, new RoutedEventArgs());
        Wait(() => !vm.IsRunning);
    }
    private static void PumpFor(int milliseconds)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Pump(); Thread.Sleep(5); }
    }
    private static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static void Render(System.Windows.Window window, string path)
    {
        var root = (FrameworkElement)window.Content; root.Measure(new System.Windows.Size(1280, 800)); root.Arrange(new System.Windows.Rect(0, 0, 1280, 800)); root.UpdateLayout(); Pump();
        Field<ImageView>(window, "Preview").FitView();
        var bitmap = new RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        using var stream = File.Create(path); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
}
