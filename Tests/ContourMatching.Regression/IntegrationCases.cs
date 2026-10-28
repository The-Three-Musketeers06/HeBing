using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AiVisionStudio.Models;
using AiVisionStudio.Services;
using AiVisionStudio.Views;
using OpenCvSharp;

public static class IntegrationCases
{
    public static int Run()
    {
        int count = 0;
        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "case-output", "integration");
        var directory = Path.Combine(outputDirectory, "inputs");
        Directory.CreateDirectory(directory);
        var a = Path.Combine(directory, "01.png"); var b = Path.Combine(directory, "02.png");
        using (var image = new Mat(80, 100, MatType.CV_8UC1, new Scalar(30))) Cv2.ImWrite(a, image);
        using (var image = new Mat(80, 100, MatType.CV_8UC1, new Scalar(210))) Cv2.ImWrite(b, image);
        var vm = new MainViewModel(Path.Combine(directory, "settings"));
        void Check(bool valid, string text) { if (!valid) throw new InvalidOperationException(text); count++; }
        void Set(string name, object value) => Check(vm.WriteParameterValue(value, name), "参数写入失败：" + name);
        Operation Add(string driver, string name) { vm.AddTool(new ToolNode { Label = driver, Cn = name }); return vm.SelectedOperation; }
        Mat Snapshot(VisualSourceKey key)
        {
            Mat image = null; vm.UseVisualSnapshot(key, value => image = value.Image); return image;
        }
        try
        {
            var source = Add("LoadImage", "源一"); Set("Type", "Directory"); Set("Directory", directory); Set("IM Out", 0);
            var filter = Add("Basic Mean Filter", "均值滤波"); Set("IM In", 0); Set("IM Out", 1);
            Wait(vm.RunInteractiveAsync());
            Check(vm.Operations.All(operation => operation.State == OpState.Ok), "合并后流程执行失败");
            Check(vm.GetVisualSources().Count == 2, "每个工具只能有一个显示来源");
            var browser = vm.GetImageBrowserSource(1, filter.Uuid);
            Check(browser?.OperationId == source.Uuid && browser.Files.Length == 2, "处理图层必须沿依赖查找图像源");
            Check(ReferenceEquals(browser.Files, vm.GetImageBrowserSource(1, filter.Uuid).Files), "重复刷新不能重新扫描相同目录");
            var sourceKey = vm.GetVisualSources().First(item => item.Key.OperationId == source.Uuid).Key;
            var filterKey = vm.GetVisualSources().First(item => item.Key.OperationId == filter.Uuid).Key;
            var oldSource = Snapshot(sourceKey); var oldFilter = Snapshot(filterKey);
            Wait(vm.SelectImageAndRunAsync(source.Uuid, b));
            Check(oldSource.IsDisposed && oldFilter.IsDisposed, "选图运行必须释放旧输入及其处理快照");
            Check(Math.Abs(Cv2.Mean(vm.Images.Get(1)).Val0 - 210) < .1, "选图后必须执行下游处理，不能只替换预览");
            Check(vm.GetImageName(1, filter.Uuid) == "02.png", "处理图层名称必须来自实际输入文件");
            var parameters = new ParamsWindow(vm); parameters.RefreshPreview();
            var bar = (WrapPanel)parameters.FindName("ImagePagingBar");
            Check(bar.Visibility == Visibility.Visible, "参数窗口必须显示图片翻页栏");
            Check(((TextBlock)parameters.FindName("ImagePageText")).Text == "2 / 2", "翻页页码必须与实际运行图片同步");
            Check(!((Button)parameters.FindName("NextImageButton")).IsEnabled, "最后一张不能继续向后切换");
            var previous = typeof(ParamsWindow).GetMethod("ChangeImagePageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Wait((Task)previous.Invoke(parameters, [-1]));
            Check(Math.Abs(Cv2.Mean(vm.Images.Get(1)).Val0 - 30) < .1, "上一张按钮必须运行流程");
            parameters.Close();

            var secondSource = Add("LoadImage", "源二"); Set("Type", "Directory"); Set("Directory", directory); Set("IM Out", 0);
            var secondFilter = Add("Basic Mean Filter", "第二路"); Set("IM In", 0); Set("IM Out", 2);
            Wait(vm.RunInteractiveAsync());
            Check(vm.GetImageBrowserSource(0, source.Uuid)?.OperationId == source.Uuid
                && vm.GetImageBrowserSource(0, secondSource.Uuid)?.OperationId == secondSource.Uuid, "共用IM0的两个控件不能混淆图片来源");
            var kept = vm.GetImageBrowserSource(0, secondSource.Uuid).CurrentFile;
            Wait(vm.SelectImageAndRunAsync(source.Uuid, b));
            Check(vm.GetImageBrowserSource(0, secondSource.Uuid).CurrentFile == kept, "切换一个来源时其他来源不能自动下一张");
            Check(vm.GetVisualSources().Count == 4, "多次切图不能增加IM来源数量");
            vm.SelectedOperation = secondFilter;
            var removedKey = vm.GetVisualSources().First(item => item.Key.OperationId == secondFilter.Uuid).Key;
            var removedImage = Snapshot(removedKey);
            vm.RemoveOperation(secondFilter);
            Check(removedImage.IsDisposed && !vm.Images.All.ContainsKey(2), "删除工具必须释放输出及其快照");
            Check(vm.SelectedOperation == secondSource, "删除后必须选择上一个工具");
            Check(vm.GetFallbackVisualSources(removedKey).First().Key.OperationId == secondSource.Uuid, "删除图层必须回退到上一个有效图像来源");
            vm.Undo();
            Check(!vm.GetVisualSources().Any(item => item.Key.OperationId == secondFilter.Uuid), "撤销配置不能恢复删除的处理结果");
            source = vm.Operations.First(operation => operation.Uuid == source.Uuid);
            filter = vm.Operations.First(operation => operation.Uuid == filter.Uuid);
            secondSource = vm.Operations.First(operation => operation.Uuid == secondSource.Uuid);
            Wait(vm.RunInteractiveAsync());
            var clear = vm.GetVisualSources().First(item => item.Key.OperationId == filter.Uuid).Key;
            var cleared = Snapshot(clear);
            vm.ClearVisualResult(clear);
            Check(cleared.IsDisposed && !vm.Images.All.ContainsKey(1), "手动清除不能仅隐藏画布");
            Check(!vm.UseVisualSnapshot(clear, _ => { }), "清除后的快照不能重新显示");
            vm.SelectedOperation = source;
            Set("Type", "File"); Set("File", a); Wait(vm.RunInteractiveAsync());
            var replaced = vm.GetVisualSources().Where(item => item.Key.OperationId == source.Uuid || item.Key.OperationId == filter.Uuid).Select(item => Snapshot(item.Key)).ToArray();
            Set("File", b);
            Check(replaced.All(image => image.IsDisposed), "修改输入文件必须立即清除相关旧快照");
            Check(!vm.GetVisualSources().Any(item => item.Key.OperationId == source.Uuid || item.Key.OperationId == filter.Uuid), "旧输入和处理图层不能继续保留");
            Wait(vm.RunInteractiveAsync());
            Set("File", Path.Combine(directory, "missing.png")); Wait(vm.RunInteractiveAsync());
            Check(source.State == OpState.Fail && !vm.GetVisualSources().Any(item => item.Key.OperationId == source.Uuid), "加载失败不能恢复旧输入图片");
            Check(filter.State == OpState.Fail && !vm.Images.All.ContainsKey(1), "失败加载不能误用另一控件上轮写入的同编号图片");
            var firstProgram = vm.SelectedProgram;
            var program = vm.CreateProgram("独立流程"); vm.SelectedProgram = program;
            Check(!vm.GetVisualSources().Any(item => item.Key.ProgramName == program.Name), "新流程不能继承其他流程的显示结果");
            vm.SelectedProgram = firstProgram;
            Check(vm.GetVisualSources().Any(item => item.Key.OperationId == secondSource.Uuid), "切换流程不能清理其他独立来源");

            var view = new ImageView();
            using var visual = new Mat(80, 100, MatType.CV_8UC1, new Scalar(20));
            view.ShowMat(visual); view.SetThumbnailSource("source", [a,b], a, source.Uuid);
            var list = (ListBox)view.FindName("ThumbnailList");
            string selectedId = null, selectedFile = null;
            view.ImageSelectionRequested += (id, file) => { selectedId = id; selectedFile = file; };
            list.SelectedIndex = 1;
            Check(selectedId == source.Uuid && selectedFile == b, "缩略图必须传递控件身份及文件路径");
            view.ClearDisplay();
            Check(list.Items.Count == 0 && !view.HasImage, "清空画布必须释放缩略图引用");
            Check(File.Exists(a) && File.Exists(b), "运行结果清理不能删除磁盘原图");
        }
        finally { vm.Dispose(); }
        var main = new MainWindow();
        main.Closing -= (System.ComponentModel.CancelEventHandler)typeof(MainWindow)
            .GetMethod("OnWindowClosing", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(System.ComponentModel.CancelEventHandler), main);
        var mainVm = (MainViewModel)typeof(MainWindow).GetField("_vm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
        try
        {
            mainVm.AddTool(new ToolNode { Label = "LoadImage", Cn = "图像源" });
            var source = mainVm.SelectedOperation;
            mainVm.WriteParameterValue("Directory", "Type"); mainVm.WriteParameterValue(directory, "Directory"); mainVm.WriteParameterValue(0, "IM Out");
            mainVm.AddTool(new ToolNode { Label = "Basic Mean Filter", Cn = "图像处理" });
            var processor = mainVm.SelectedOperation;
            mainVm.WriteParameterValue(0, "IM In"); mainVm.WriteParameterValue(1, "IM Out");
            Wait(mainVm.RunInteractiveAsync());
            var refresh = typeof(MainWindow).GetMethod("RefreshImageViews", BindingFlags.Instance | BindingFlags.NonPublic)!;
            refresh.Invoke(main, [true]);
            var workspace = (ImageWorkspace)main.FindName("ImageWorkspace");
            var first = workspace.Viewers.First();
            Check(first.Value.HasImage && ((ListBox)first.Value.FindName("ThumbnailList")).Items.Count == 2, "主窗口图层和缩略图必须同时显示");
            var filterInfo = mainVm.GetVisualSources().First(item => item.Key.OperationId == processor.Uuid);
            int pane = workspace.OpenSource(filterInfo.Key, "图像处理", 1);
            refresh.Invoke(main, [true]);
            Check(workspace.Viewers[pane].HasImage, "打开处理图层必须显示处理后的图片");
            int panes = workspace.Viewers.Count;
            workspace.OpenSource(filterInfo.Key, "重复处理图层", 1);
            Check(workspace.Viewers.Count == panes, "相同来源不能重复添加画布");
            mainVm.RemoveOperation(processor); refresh.Invoke(main, [true]);
            Check(workspace.Viewers.Values.Count(viewer => viewer.HasImage) == 1, "删除已显示的处理工具必须清理对应画布");
            var root = (FrameworkElement)main.Content;
            root.Measure(new System.Windows.Size(1280, 800)); root.Arrange(new System.Windows.Rect(0, 0, 1280, 800)); root.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            using (var stream = File.Create(Path.Combine(outputDirectory, "merged-main.png")))
            { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); }
            var clearMethod = typeof(MainWindow).GetMethod("ClearDisplayedVisual", BindingFlags.Instance | BindingFlags.NonPublic)!;
            clearMethod.Invoke(main, [first.Value]); refresh.Invoke(main, [true]);
            Check(!first.Value.HasImage && ((ListBox)first.Value.FindName("ThumbnailList")).Items.Count == 0, "主窗口清除必须同时清掉图片和缩略图");
            Check(!mainVm.GetVisualSources().Any(), "主窗口清除必须移除实际运行结果");
            var other = mainVm.CreateProgram("新流程"); mainVm.SelectedProgram = other; refresh.Invoke(main, [true]);
            Check(workspace.TryGetSingleOpenPane(out _) && !workspace.Viewers.Values.Any(viewer => viewer.HasImage), "新流程必须只有一个等待运行的空画布");
        }
        finally { main.Close(); mainVm.Dispose(); }
        Console.WriteLine($"Integration cases passed: {count}");
        return count;
    }

    private static void Wait(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("集成测试超时");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
    }
}
