using System.IO;
using System.Reflection;
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
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

public static class EditorCases
{
    public static int Run()
    {
        var count = 0;
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".build", "regression"));
        Directory.CreateDirectory(output);
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Themes/Light.xaml", UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Views/ToolEditorResources.xaml", UriKind.Relative) });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        DriverCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "driver_defs.json"));
        var vm = new MainViewModel(Path.Combine(output, "settings"));
        try
        {
            vm.AddTool(new ToolNode { Label = "Basic Shape Match", Cn = "形状模板匹配" });
            var source = vm.SelectedOperation;
            Require(vm.ReadParameterValue("Training Region") != null && vm.ReadParameterValue("Angle Range", "min") != null, "匹配工具必须包含示教与搜索参数"); count++;
            Require(!vm.CreateSelectedParameterBag().Bool("Use Region"), "形状模板匹配默认必须搜索全图"); count++;
            Require(vm.AllParameterNodes.First(node => node.Path.SequenceEqual(new[] { "Training Region", "Type" })).Options.Any(option => option.Value == "rect2d array"), "声明支持多区域的控件必须能选择区域数组"); count++;
            var rectangle = Wrap("rect2d", new Dictionary<string, object> { ["Center"] = XY(120, 90), ["Size"] = new Dictionary<string, object> { ["Width"] = 170d, ["Height"] = 130d }, ["Rotation"] = 0d });
            Require(vm.WriteParameterValue(rectangle, "Training Region"), "完整区域必须可以原子回填");
            Require(Convert.ToDouble(vm.ReadParameterValue("Training Region", "rect2d", "Size", "Width")) == 170, "区域尺寸必须回填参数树"); count++;
            vm.Undo(); Require(Convert.ToDouble(vm.ReadParameterValue("Training Region", "rect2d", "Size", "Width")) == 0, "一次撤销应恢复画选前完整区域");
            vm.Redo(); Require(Convert.ToDouble(vm.ReadParameterValue("Training Region", "rect2d", "Size", "Width")) == 170, "区域编辑应支持重做"); count++;

            using var image = new Mat(180, 240, MatType.CV_8UC3, new Scalar(25, 35, 45));
            Cv2.Rectangle(image, new OpenCvSharp.Rect(55, 45, 50, 90), new Scalar(80, 220, 250), 4);
            Cv2.Circle(image, new OpenCvSharp.Point(150, 70), 20, new Scalar(250, 90, 50), 4);
            Cv2.Line(image, new OpenCvSharp.Point(110, 110), new OpenCvSharp.Point(174, 130), Scalar.White, 5);
            vm.Images.Set(0, image.Clone());
            var window = new ParamsWindow(vm);
            var tabs = Field<TabControl>(window, "_shapeTabs");
            Require(tabs.Items.Count == 3, "模板窗口应包含三个页面"); count++;
            window.RefreshPreview();
            Render(window, output, "template-teaching.png", 1280, 800);
            var labels = Descendants<Button>((DependencyObject)window.Content).Select(button => button.Content?.ToString()).ToArray();
            Require(labels.Contains("创建") && !labels.Contains("掩膜编辑") && !labels.Contains("图片加载") && !labels.Contains("训练模板") && !labels.Contains("画笔屏蔽"), "示教页只保留创建入口，掩膜编辑放在创建窗口内"); count++;
            var editor = CreateTemplateEditor(window);
            Invoke(editor, "LoadImage", image);
            Require(Convert.ToDouble(vm.ReadParameterValue("Training Region", "rect2d", "Size", "Width")) == 170 && vm.ReadParameterValue(ContourTemplateStorage.ModelKey) == null, "弹窗加载图像只能修改草稿，不能清空工程模板或区域"); count++;
            Field<Dictionary<string, object>>(editor, "_draft")["Training Region"] = rectangle;
            Invoke(editor, "ReadControls"); Invoke(editor, "RefreshCanvas");
            Invoke(editor, "BeginRegion", "rect2d", true);
            var templateCanvas = Field<ImageView>(editor, "CanvasView");
            Require(templateCanvas.IsRegionEditing && Field<Border>(templateCanvas, "_regionChrome").Visibility == Visibility.Collapsed, "模板选区时不得显示浮动提示和应用按钮栏"); count++;
            Render(editor, output, "template-configuration.png", 1180, 760);
            Invoke(editor, "OnTrain", null, new RoutedEventArgs());
            PumpUntil(() => !editor.IsTraining);
            var trained = Field<ContourTemplateModel>(editor, "_model");
            Require(trained != null && trained.Features.Count > 12 && !templateCanvas.IsRegionEditing, "选好区域后直接训练必须自动应用区域并完成真实训练"); count++;
            Require(Field<ListBox>(editor, "TemplateResults").Items.Count == 1, "弹窗右侧列表必须显示成功训练的模板结果"); count++;
            using (var restored = ContourTemplateEngine.DecodeSourceImage(trained))
            { Require(restored.Channels() == 3 && Cv2.Norm(restored, image, NormTypes.INF) == 0, "模板应无损保留彩色训练图"); count++; }
            Render(editor, output, "template-configuration-trained.png", 1180, 760);
            var savedPayload = vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString();
            vm.Undo(); Pump();
            Require(vm.ReadParameterValue(ContourTemplateStorage.ModelKey) == null && Field<ContourTemplateModel>(window, "_shapeTemplate") == null, "一次撤销训练必须移除新模板和父窗口预览");
            vm.Redo(); Pump();
            Require(vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString() == savedPayload && Field<ContourTemplateModel>(window, "_shapeTemplate") != null, "重做训练必须恢复内嵌模板和父窗口预览"); count++;
            Invoke(editor, "FillMask"); Invoke(editor, "ApplyMask");
            Invoke(editor, "OnTrain", null, new RoutedEventArgs()); PumpUntil(() => !editor.IsTraining);
            Require(vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString() == savedPayload && vm.ReadParameterValue(ContourTemplateStorage.DirtyKey) is false, "全部屏蔽导致训练失败时必须保留原模板"); count++;
            Field<Dictionary<string, object>>(editor, "_draft")["Shielding Strokes"] = new object[] { new Dictionary<string, object>(rectangle) { ["Erase"] = true } };
            Invoke(editor, "OnTrain", null, new RoutedEventArgs()); PumpUntil(() => !editor.IsTraining);
            Require(vm.CreateSelectedParameterBag().Bool("Use Shielding Area") && Field<ContourTemplateModel>(editor, "_model")?.Features.Count > 12 && vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString() != savedPayload && (vm.ReadParameterValue("Shielding Strokes", "0") as IReadOnlyDictionary<string, object>)?.GetValueOrDefault("Erase") is true, "橡皮擦必须能恢复训练特征并持久化擦除操作"); count++;
            var erasedPayload = vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString();
            Field<Dictionary<string, object>>(editor, "_draft")["Shielding Strokes"] = new object[]
            {
                new Dictionary<string, object>(rectangle) { ["Erase"] = true },
                new Dictionary<string, object>(rectangle) { ["Erase"] = false }
            };
            Invoke(editor, "OnTrain", null, new RoutedEventArgs()); PumpUntil(() => !editor.IsTraining);
            Require(vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString() == erasedPayload, "恢复后的区域重新涂抹屏蔽必须再次屏蔽，失败时保留原模板"); count++;
            Invoke(editor, "ClearMask");
            Invoke(editor, "ApplyMask");
            Field<ComboBox>(editor, "ScaleMode").SelectedIndex = 1;
            Field<TextBox>(editor, "FeatureScale").Text = "3.0";
            Field<ComboBox>(editor, "ThresholdMode").SelectedIndex = 1;
            Field<TextBox>(editor, "ContrastThreshold").Text = "50";
            Field<CheckBox>(editor, "StoreImage").IsChecked = false;
            Invoke(editor, "OnTrain", null, new RoutedEventArgs()); PumpUntil(() => !editor.IsTraining);
            var compact = Field<ContourTemplateModel>(editor, "_model");
            Require(compact.PyramidLevels == 3 && compact.EdgeHigh == 50 && compact.SourceImagePng.Length == 0, "弹窗尺度、阈值和模板存图必须实际参与训练"); count++;
            using (var restored = ContourTemplateEngine.DecodeSourceImage(compact))
            { Require(restored.Width == image.Width && restored.Height == image.Height, "不存完整图像时仍须保留区域和原点所在的图像坐标系"); count++; }
            Field<CheckBox>(editor, "StoreImage").IsChecked = true;
            Invoke(editor, "OnTrain", null, new RoutedEventArgs()); PumpUntil(() => !editor.IsTraining);
            trained = Field<ContourTemplateModel>(editor, "_model");
            savedPayload = vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString();
            Invoke(editor, "LoadImage", image);
            Invoke(editor, "OnTrain", null, new RoutedEventArgs()); PumpUntil(() => !editor.IsTraining);
            Require(vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString() == savedPayload, "未绘制区域时训练失败必须保留已保存模板"); count++;
            editor.Close();
            Require(vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString() == savedPayload && Convert.ToDouble(vm.ReadParameterValue("Training Region", "rect2d", "Size", "Width")) == 170, "关闭弹窗必须放弃未训练草稿并保留最近训练的模板"); count++;
            var cancellingEditor = CreateTemplateEditor(window);
            Invoke(cancellingEditor, "OnTrain", null, new RoutedEventArgs());
            var closingEvent = new System.ComponentModel.CancelEventArgs();
            Invoke(cancellingEditor, "OnEditorClosing", cancellingEditor, closingEvent);
            Require(closingEvent.Cancel, "训练期间关闭弹窗必须先取消训练");
            PumpUntil(() => !cancellingEditor.IsTraining);
            Require(vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString() == savedPayload, "关闭时取消的训练不得覆盖工程模板"); count++;
            window.RefreshPreview(); Render(window, output, "template-trained.png", 1280, 800);
            tabs.SelectedIndex = 1; window.RefreshPreview(); Render(window, output, "template-runtime.png", 1280, 800);
            var runtimeContent = (DependencyObject)((TabItem)tabs.Items[1]).Content;
            var runtimeActions = Descendants<Button>(runtimeContent).Select(button => button.Content?.ToString()).ToArray();
            Require(!new[] { "绘制搜索矩形", "绘制搜索多边形", "全图搜索" }.Any(runtimeActions.Contains) && !Descendants<TextBlock>(runtimeContent).Any(text => text.Text.Contains("点击“创建”打开模板配置")), "运行参数页不得显示顶部提示或搜索操作栏"); count++;
            Require(!string.IsNullOrWhiteSpace(vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString()) && vm.ReadParameterValue(ContourTemplateStorage.DirtyKey) is false && string.IsNullOrWhiteSpace(vm.ReadParameterValue("Model File")?.ToString()), "训练完成必须自动生效，不依赖外部模板路径"); count++;
            vm.WriteParameterValue(new { min = 0d, max = 0d }, "Angle Range");
            Invoke(window, "OnTest", null, new RoutedEventArgs()); PumpUntil(() => !vm.IsRunning);
            Require(vm.SelectedOperation.State == OpState.Ok && Convert.ToInt32(vm.GetSelectedOutputValues()["Matched Num"]) == 1, "窗口测试按钮必须运行匹配流程并产生定位结果"); count++;
            Render(window, output, "template-results.png", 1280, 800);
            var portablePath = Path.Combine(output, "embedded-template.aivs");
            Require(ProjectFile.SaveAll(portablePath, vm.Programs), "工程必须能够保存内嵌模板");
            var reopened = new MainViewModel(Path.Combine(output, "reopened-settings"));
            try
            {
                reopened.OpenProject(portablePath); reopened.SelectedOperation = reopened.Operations.First(operation => operation.Driver == "Basic Shape Match");
                reopened.Images.Set(0, image.Clone());
                var restoredWindow = new ParamsWindow(reopened);
                Require(Field<ContourTemplateModel>(restoredWindow, "_shapeTemplate")?.Features.Count == trained.Features.Count && Field<Mat>(restoredWindow, "_shapeTrainingImage")?.Channels() == 3, "重新打开工程必须自动恢复模板和训练图"); count++;
                Invoke(restoredWindow, "OnTest", null, new RoutedEventArgs()); PumpUntil(() => !reopened.IsRunning);
                Require(reopened.SelectedOperation.State == OpState.Ok, "工程恢复后应直接匹配，不需要手动加载模板"); count++;
                reopened.WriteParameterValue(145d, "Training Region", "rect2d", "Size", "Width");
                reopened.WriteParameterValue(.5, "Min Score");
                Require(reopened.CreateSelectedParameterBag().Bool(ContourTemplateStorage.DirtyKey), "运行参数改动不得清除模板需要重训的状态"); count++;
                Invoke(restoredWindow, "OnTest", null, new RoutedEventArgs()); PumpUntil(() => !reopened.IsRunning);
                Require(reopened.SelectedOperation.State == OpState.Fail && reopened.GetSelectedOutputValues().GetValueOrDefault("Matches") == null, "区域改变后必须阻止使用旧模板"); count++;
                restoredWindow.Close();
            }
            finally { reopened.Dispose(); }

            var view = new ImageView(); view.ShowMat(image);
            var committed = 0;
            view.BeginRegionEdit("rect2d", rectangle, _ => committed++); view.CancelRegionEdit();
            Require(committed == 0 && !view.IsRegionEditing, "取消画选不得修改参数"); count++;
            view.BeginRegionEdit("rect2d", rectangle, _ => committed++); view.CompleteRegionEdit();
            Require(committed == 1 && !view.IsRegionEditing, "应用画选必须仅提交一次"); count++;
            view.BeginRegionEdit("polygon2d", null, _ => committed++); view.CompleteRegionEdit();
            Require(view.IsRegionEditing && committed == 1, "无效区域不得提交");
            Require(view.HandleRegionEditKey(Key.Escape) && !view.IsRegionEditing, "Esc必须取消当前画选"); count++;
            view.BeginRegionEdit("ellipse2d", Wrap("ellipse2d", new Dictionary<string, object> { ["Center"] = XY(100, 75), ["Long Axis"] = 80d, ["Short Axis"] = 40d, ["Rotation"] = 25d }), _ => committed++); view.CompleteRegionEdit();
            Require(committed == 2, "椭圆区域必须支持回填"); count++;
            Dictionary<string, object> sectorResult = null;
            view.BeginRegionEdit("sector2d", Wrap("sector2d", new Dictionary<string, object> { ["Center"] = XY(100, 90), ["Inner Radius"] = 0d, ["Outer Radius"] = 50d, ["Start Angle"] = 0d, ["Angle Range"] = 90d }), value => sectorResult = value);
            Field<TextBox>(view, "_sectorStartAngle").Text = "30"; Field<TextBox>(view, "_sectorSweepAngle").Text = "120";
            view.CompleteRegionEdit();
            Require(sectorResult != null && Convert.ToDouble(((IReadOnlyDictionary<string, object>)sectorResult["sector2d"])["Start Angle"]) == 30 && Convert.ToDouble(((IReadOnlyDictionary<string, object>)sectorResult["sector2d"])["Angle Range"]) == 120, "扇形输入的角度必须参与实际区域回填"); count++;
            view.BeginRegionEdit("sector2d", sectorResult, _ => committed++);
            Field<TextBox>(view, "_sectorSweepAngle").Text = "0"; view.CompleteRegionEdit();
            Require(view.IsRegionEditing && committed == 2, "无效的零度扇形不得提交"); view.CancelRegionEdit(); count++;
            view.Clear();

            var sectorEditor = CreateTemplateEditor(window);
            Invoke(sectorEditor, "LoadImage", image);
            Require(vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString() == savedPayload && vm.GetSelectedOutputValues().Count > 0, "弹窗重新载入图像不得清空工程模板和匹配结果"); count++;
            var sectorPreview = Wrap("sector2d", new Dictionary<string, object> { ["Center"] = XY(120, 90), ["Inner Radius"] = 0d, ["Outer Radius"] = 70d, ["Start Angle"] = 25d, ["Angle Range"] = 135d });
            Field<Dictionary<string, object>>(sectorEditor, "_draft")["Training Region"] = sectorPreview;
            Invoke(sectorEditor, "BeginRegion", "sector2d", true);
            Render(sectorEditor, output, "template-sector.png", 1180, 760);
            Field<ImageView>(sectorEditor, "CanvasView").CancelRegionEdit(); sectorEditor.Close();

            // Binding the two poses is an undoable action and uses declared outputs before the first run.
            vm.AddTool(new ToolNode { Label = "Crop Image" });
            var crop = vm.SelectedOperation;
            Require(vm.BindRegionPositionToMatch(source.Uuid), "后续区域应可一键绑定模板定位");
            var bag = vm.CreateSelectedParameterBag();
            Require(bag.Bool("Position Fix") && bag.Bindings.ContainsKey("Points Information.Base Point") && bag.Bindings.ContainsKey("Points Information.Run Point"), "定位绑定必须同时设置基准与运行位姿"); count++;
            vm.Undo(); Require(!vm.CreateSelectedParameterBag().Bool("Position Fix"), "定位绑定应支持一次撤销"); count++;
            vm.SelectedOperation = vm.Operations.First(operation => operation.Driver == "Basic Shape Match");
            tabs.SelectedIndex = 2; window.RefreshPreview();
            Invoke(window, "DisposeShapeEditor"); window.Close();
            var spatialType = typeof(ImageView).Assembly.GetType("AiVisionStudio.Views.SpatialRegionEditor")!;
            var profile = new ProfileData([0, 2, 4, 6, 8, 10], [1, 3, 6, 8, 6, 1]);
            var profileWindow = (System.Windows.Window)spatialType.GetMethod("ForProfile")!.Invoke(null,
                [profile, "window1d", new Dictionary<string, object> { ["Start"] = new Dictionary<string, object> { ["X"] = 2d, ["Z"] = 2d }, ["End"] = new Dictionary<string, object> { ["X"] = 8d, ["Z"] = 7d } }, (Action<Dictionary<string, object>>)(_ => { })]);
            using (var raster = (Mat)spatialType.GetMethod("RenderPoints", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(profileWindow, null)) Field<ImageView>(profileWindow, "_view").ShowMat(raster);
            Invoke(profileWindow, "Begin");
            var metric = Wrap("rect2d", new Dictionary<string, object> { ["Center"] = XY(5, 4.5), ["Size"] = new Dictionary<string, object> { ["Width"] = 6d, ["Height"] = 5d }, ["Rotation"] = 20d });
            var projected = spatialType.GetMethod("Transform", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(profileWindow, [metric, true]);
            var roundTrip = (Dictionary<string, object>)spatialType.GetMethod("Transform", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(profileWindow, [projected, false]);
            var roundTripBody = (IReadOnlyDictionary<string, object>)roundTrip["rect2d"];
            Require(Math.Abs(Convert.ToDouble(((IReadOnlyDictionary<string, object>)roundTripBody["Center"])["X"]) - 5) < 1e-6 && Math.Abs(Convert.ToDouble(roundTripBody["Rotation"]) - 20) < 1e-6, "轮廓投影必须能还原实际坐标和方向"); count++;
            Render(profileWindow, output, "profile-region.png", 1050, 780); profileWindow.Close();

            var cloud = new PointCloud(); for (var y = 0; y < 10; y++) for (var x = 0; x < 10; x++) cloud.Add(x, y, x + y);
            var cloudBody = new Dictionary<string, object> { ["Start"] = XYZ(2, 2, 0), ["End"] = XYZ(5, 5, 18) };
            var cloudWindow = (System.Windows.Window)spatialType.GetMethod("ForCloud")!.Invoke(null,
                [cloud, null, "box", cloudBody, (Action<Dictionary<string, object>>)(_ => { })]);
            using (var raster = (Mat)spatialType.GetMethod("RenderPoints", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cloudWindow, null)) Field<ImageView>(cloudWindow, "_view").ShowMat(raster);
            Invoke(cloudWindow, "Begin");
            var cloudRegion = Wrap("box", cloudBody); Invoke(cloudWindow, "SetCloudDepth", cloudRegion);
            Require(Convert.ToDouble(((IReadOnlyDictionary<string, object>)cloudBody["Start"])["Z"]) == 4 && Convert.ToDouble(((IReadOnlyDictionary<string, object>)cloudBody["End"])["Z"]) == 10, "三维框选的深度必须来自被选中点云"); count++;
            Render(cloudWindow, output, "cloud-region.png", 1350, 780); cloudWindow.Close();
            Console.WriteLine($"Editor cases passed: {count}; snapshots: {output}");
        }
        finally { vm.Dispose(); }
        return count;
    }
    private static Dictionary<string, object> XY(double x, double y) => new() { ["X"] = x, ["Y"] = y };
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static Dictionary<string, object> XYZ(double x, double y, double z) => new() { ["X"] = x, ["Y"] = y, ["Z"] = z };
    private static Dictionary<string, object> Wrap(string type, object body) => new() { ["Type"] = type, [type] = body };
    private static T Field<T>(object source, string name) => (T)source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source);
    private static TemplateConfigurationWindow CreateTemplateEditor(ParamsWindow window)
        => (TemplateConfigurationWindow)typeof(ParamsWindow).GetMethod("CreateShapeTemplateConfiguration", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    private static void Invoke(object source, string name, params object[] args) => source.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(source, args);
    private static void PumpUntil(Func<bool> done)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done()) { if (watch.ElapsedMilliseconds > 10000) throw new TimeoutException("UI action did not finish"); Pump(); Thread.Sleep(5); }
        Pump();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Render(System.Windows.Window window, string directory, string file, int width, int height)
    {
        Pump(); window.Width = width; window.Height = height;
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        Pump();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        using var stream = File.Create(Path.Combine(directory, file));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
