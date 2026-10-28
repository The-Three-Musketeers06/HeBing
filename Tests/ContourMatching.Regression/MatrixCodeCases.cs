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
using ZXing;
using YamlDotNet.RepresentationModel;
using Point = OpenCvSharp.Point;
using Rect = OpenCvSharp.Rect;
using Size = OpenCvSharp.Size;

public static class MatrixCodeCases
{
    public static int Run()
    {
        var count = 0;
        using var scene = Scene();
        var options = new MatrixCodeOptions { TimeoutMilliseconds = 10000 };
        var all = MatrixCodeEngine.Read(scene, options);
        Require(all.Codes.Count == 3 && all.Codes.Count(c => c.Content == "TEST-同内容-001") == 2 && all.Codes.Any(c => c.CodeType == "DataMatrix" && c.Content == "DM-LOCAL-123"), "应读取不同位置的重复内容和混合码制"); count++;
        Require(all.Codes[0].Corners.Min(p => p.X) < 160 && all.Codes[1].Corners.Min(p => p.X) > 200, "结果应按图像位置排列并保持原图坐标"); count++;
        Require(all.Codes.All(c => c.ModuleRows > 0 && c.ModuleColumns > 0), "模块数应来自实际解码元数据"); count++;
        options.MinimumModuleWidth = 100;
        Require(MatrixCodeEngine.Read(scene, options).Codes.Count == 0, "最小模块宽度过滤应生效"); options.MinimumModuleWidth = -1; count++;
        options.MaximumCodes = 1;
        var limited = MatrixCodeEngine.Read(scene, options);
        Require(limited.Codes.Count == 1 && limited.LimitReached, "最大读取数量必须生效"); count++;
        options.MaximumCodes = 0; options.DecodeType = "QRCode";
        Require(MatrixCodeEngine.Read(scene, options).Codes.Count == 2, "QRCode 类型只能返回 QR 码"); count++;
        options.QrModules = "Index177";
        Require(MatrixCodeEngine.Read(scene, options).Codes.Count == 0, "指定 QR 模块数应筛掉不同模块数的码"); options.QrModules = "IndexQrAuto"; count++;
        options.DecodeType = "DataMatrix";
        Require(MatrixCodeEngine.Read(scene, options).Codes.Single().Content == "DM-LOCAL-123", "DataMatrix 类型只能返回 DM 码"); count++;
        options.DecodeType = "Auto";
        using (var rectangular = RectangularDm())
        {
            options.CodeShape = "Rectangle";
            var rectangularResult = MatrixCodeEngine.Read(rectangular, options);
            Require(rectangularResult.Codes.Count == 1 && rectangularResult.Codes[0].ModuleRows != rectangularResult.Codes[0].ModuleColumns, "矩形 DataMatrix 应支持读取和模块数输出"); count++;
            options.CodeShape = "Square";
            Require(MatrixCodeEngine.Read(rectangular, options).Codes.Count == 0, "正方形条件不应返回矩形 DM"); count++;
            options.CodeShape = "Auto";
        }
        using var mask = new Mat(scene.Size(), MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(mask, new Rect(10, 15, 160, 150), Scalar.White, -1);
        var region = MatrixCodeEngine.Read(scene, options, mask);
        Require(region.Codes.Count == 1 && region.Codes[0].Content == "TEST-同内容-001" && region.Codes[0].Corners.Min(p => p.X) >= 20, "区域识别只返回选区内的码，坐标不得随裁剪偏移"); count++;
        mask.SetTo(Scalar.Black); Cv2.Rectangle(mask, new Rect(60, 35, 45, 60), Scalar.White, -1);
        Require(MatrixCodeEngine.Read(scene, options, mask).Codes.Count == 0, "选区不得返回区域外或被截断的完整结果"); count++;
        using var inverse = new Mat(); Cv2.BitwiseNot(scene, inverse);
        options.Polarity = "Light On Dark";
        var light = MatrixCodeEngine.Read(inverse, options);
        Require(light.Codes.Count == 3 && light.Codes.All(c => c.Inverted), "白码黑底应解出原始内容并记录反色"); count++;
        options.Polarity = "Both";
        Require(MatrixCodeEngine.Read(inverse, options).Codes.Count == 3, "自动极性应识别反色图像"); count++;
        options.Polarity = "Dark On Light";
        using var rotated = new Mat(); Cv2.Rotate(scene, rotated, RotateFlags.Rotate90Clockwise);
        Require(MatrixCodeEngine.Read(rotated, options).Codes.Count == 3, "旋转的二维码仍应读取"); count++;
        using var color = new Mat(); Cv2.CvtColor(scene, color, ColorConversionCodes.GRAY2BGRA);
        Require(MatrixCodeEngine.Read(color, options).Codes.Count == 3, "彩色和透明通道输入应正确转灰度"); count++;
        using var empty = new Mat(300, 400, MatType.CV_8UC1, Scalar.White);
        Require(MatrixCodeEngine.Read(empty, options).Codes.Count == 0, "空白图不得生成虚构内容"); count++;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Throws<OperationCanceledException>(() => MatrixCodeEngine.Read(scene, options, cancellation: cancellation.Token)); count++;
        options.MaximumCodes = -1; Throws<ArgumentException>(() => MatrixCodeEngine.Read(scene, options)); options.MaximumCodes = 0; count++;
        using var large = new Mat(2000, 2200, MatType.CV_8UC1, Scalar.White);
        options.TimeoutMilliseconds = 1;
        var timed = MatrixCodeEngine.Read(large, options);
        Require(timed.TimedOut && timed.Codes.Count == 0, "超时不得返回部分解码结果"); options.TimeoutMilliseconds = 10000; count++;

        var ctx = new OpContext();
        try
        {
            ctx.Images.Set(0, scene.Clone());
            var op = new MatrixCodeReadOp();
            var p = Bag("Time Out: 10000\nText Display: true\nResult Area: true\nCode Number: 0");
            Require(op.Run(ctx, p) && ctx.Vars.GetDouble("Code Count") == 3 && ctx.NativeOverlays.Count == 6 && ctx.Vars.Get("Codes") is List<object>, "算子应发布可绑定的多码明细与图形"); count++;
            var raw = (IReadOnlyDictionary<string, object>)((List<object>)ctx.Vars.Get("Codes"))[0];
            Require(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String((string)raw["Raw Bytes Base64"])) == "TEST-同内容-001", "原始字节输出应保留 UTF-8 内容"); count++;
            Require(Cv2.Norm(ctx.Images.Get(0), scene, NormTypes.INF) == 0, "解码和图形不得修改输入图像"); count++;
            ctx.Images.Set(0, empty.Clone());
            Require(!op.Run(ctx, p) && ctx.Vars.GetDouble("Code Count") == 0 && (string)ctx.Vars.Get("Content") == "" && ctx.NativeOverlays.Count == 0, "失败必须清理上次内容和图形"); count++;
            ctx.Images.Set(0, scene.Clone());
            ctx.Images.Set(3, new Mat(scene.Size(), MatType.CV_8UC1, Scalar.Black));
            Cv2.Rectangle(ctx.Images.Get(3), new Rect(10, 15, 160, 150), Scalar.White, -1);
            Require(op.Run(ctx, Bag("ROI Region Type: Image type\nMask Im In: 3\nTime Out: 10000")) && ctx.Vars.GetDouble("Code Count") == 1, "图像掩膜区域必须参与解码"); count++;
            Require(op.Run(ctx, Bag("Use Region: true\nRegion: {Type: rect2d, rect2d: {Center: {X: 90, Y: 90}, Size: {Width: 160, Height: 150}, Rotation: 0}}\nTime Out: 10000")) && ctx.Vars.GetDouble("Code Count") == 1, "手动画选区域必须参与解码"); count++;
            Require(op.Run(ctx, Bag("Use Region: true\nRegion: {Type: rect2d, rect2d: {Center: {X: 90, Y: 90}, Size: {Width: 160, Height: 150}, Rotation: 0}}\nPosition Fix: true\nPoints Information: {Base Point: {Point: {X: 90, Y: 90}, Angle: 0, ScaleX: 1, ScaleY: 1}, Run Point: {Point: {X: 300, Y: 90}, Angle: 0, ScaleX: 1, ScaleY: 1}}\nTime Out: 10000")) && ctx.Vars.GetDouble("Code Count") == 1, "读取区域必须能够跟随模板定位平移"); count++;
            var fixedCode = (IReadOnlyDictionary<string, object>)((List<object>)ctx.Vars.Get("Codes"))[0];
            Require(Convert.ToDouble(((IReadOnlyDictionary<string, object>)((object[])fixedCode["Corners"])[0])["X"]) > 200, "定位后的坐标仍应对应原图第二个码"); count++;
            Throws<NotSupportedException>(() => op.Run(ctx, Bag("Print Quality Type: ISO/IEC 15415"))); count++;
            Require((string)ctx.Vars.Get("Content") == "" && ctx.NativeOverlays.Count == 0, "无效配置不得保留旧结果"); count++;
        }
        finally { ctx.Images.Dispose(); }
        using (var dense = DenseScene())
        {
            var many = MatrixCodeEngine.Read(dense, new MatrixCodeOptions { DecodeType = "DataMatrix", TimeoutMilliseconds = 30000 });
            Require(many.Codes.Count == 256, $"不限数量不应被原生单次 255 个上限截断，实际 {many.Codes.Count}"); count++;
        }

        // Original samples are optional fixtures; they are read only, never rewritten.
        var sample = @"D:\Project\Images\2DSym-144x144.bmp";
        if (File.Exists(sample))
        {
            using var image = Cv2.ImRead(sample, ImreadModes.Grayscale);
            var decoded = MatrixCodeEngine.Read(image, options);
            Require(decoded.Codes.Count == 1 && decoded.Codes[0].Content.StartsWith("This is a demonstration of a 2D matrix code storing data in a 144x144 grid", StringComparison.Ordinal), "应成功读取用户提供的 144×144 DataMatrix 原图"); count++;
            Console.WriteLine($"User fixture: 144x144 DataMatrix => {decoded.Codes[0].Content.Length} characters");
        }
        var label = @"D:\Project\Images\OCRMax_label.bmp";
        if (File.Exists(label))
        {
            using var image = Cv2.ImRead(label, ImreadModes.Grayscale);
            var decoded = MatrixCodeEngine.Read(image, options);
            Console.WriteLine($"User fixture: OCRMax_label.bmp => {decoded.Codes.Count} decoded (no OCR substitution)");
        }
        count += Ui(scene);
        Console.WriteLine($"PASS {count} matrix code cases");
        return count;
    }

    private static int Ui(Mat image)
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (!app.Resources.MergedDictionaries.Any())
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Themes/Light.xaml", UriKind.Relative) });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Views/ToolEditorResources.xaml", UriKind.Relative) });
        }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        DriverCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "driver_defs.json"));
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".build", "regression"));
        Directory.CreateDirectory(output);
        var vm = new MainViewModel(Path.Combine(output, "matrix-settings"));
        try
        {
        vm.AddTool(new ToolNode { Label = "Basic 2D Code Reader", Cn = "二维码识别" });
        vm.Images.Set(0, image.Clone());
        Require(vm.ReadParameterValue("Code Number") != null && vm.AllParameterNodes.Any(n => n.Name == "Region" && n.Children.Any(c => c.Name == "Type")), "二维码界面应有数量限制和几何区域参数");
        var test = vm.TestSelectedAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!test.IsCompleted) { if (watch.ElapsedMilliseconds > 15000) throw new TimeoutException("二维码界面测试超时"); Pump(); Thread.Sleep(5); }
        test.GetAwaiter().GetResult(); Pump();
        Require(vm.SelectedOperation.State == OpState.Ok && Convert.ToInt32(vm.GetSelectedOutputValues()["Code Count"]) == 3, "测试按钮应使用本地算子并显示解码结果");
        var window = new ParamsWindow(vm); window.RefreshPreview();
        var root = (FrameworkElement)window.Content;
        root.Measure(new System.Windows.Size(1280, 900)); root.Arrange(new System.Windows.Rect(0, 0, 1280, 900)); root.UpdateLayout(); Pump();
        var bitmap = new RenderTargetBitmap(1280, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        using (var stream = File.Create(Path.Combine(output, "matrix-code-reader.png")))
        { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); }
        window.Close();
        // Re-running through the engine must remove the old bound output when it fails.
        vm.Images.Set(0, new Mat(image.Size(), MatType.CV_8UC1, Scalar.White));
        var failed = vm.TestSelectedAsync(); watch.Restart();
        while (!failed.IsCompleted) { if (watch.ElapsedMilliseconds > 15000) throw new TimeoutException(); Pump(); Thread.Sleep(5); }
        failed.GetAwaiter().GetResult(); Pump();
        Require(vm.SelectedOperation.State == OpState.Fail && !vm.GetSelectedOutputValues().ContainsKey("Codes") && vm.NativeOverlays.Count == 0, "引擎失败后不得暴露上次成功的绑定内容");
        return 3;
        }
        finally { vm.Dispose(); }
    }

    public static Mat Scene()
    {
        var scene = new Mat(410, 620, MatType.CV_8UC1, Scalar.White);
        foreach (var (text, format, x, y) in new[]
        {
            ("TEST-同内容-001", BarcodeFormat.QR_CODE, 20, 30), ("TEST-同内容-001", BarcodeFormat.QR_CODE, 230, 30),
            ("DM-LOCAL-123", BarcodeFormat.DATA_MATRIX, 30, 230), ("ONE-D-IGNORE", BarcodeFormat.CODE_128, 230, 250)
        })
        {
            var matrix = new MultiFormatWriter().encode(text, format, format == BarcodeFormat.CODE_128 ? 280 : 120, 120,
                new Dictionary<EncodeHintType, object> { [EncodeHintType.CHARACTER_SET] = "UTF-8", [EncodeHintType.MARGIN] = 4 });
            for (var yy = 0; yy < matrix.Height; yy++) for (var xx = 0; xx < matrix.Width; xx++) scene.Set(y + yy, x + xx, matrix[xx, yy] ? (byte)0 : (byte)255);
        }
        return scene;
    }
    private static Mat RectangularDm()
    {
        var bits = new MultiFormatWriter().encode("RECTANGULAR-DM", BarcodeFormat.DATA_MATRIX, 200, 100,
            new Dictionary<EncodeHintType, object> { [EncodeHintType.DATA_MATRIX_SHAPE] = ZXing.Datamatrix.Encoder.SymbolShapeHint.FORCE_RECTANGLE });
        var result = new Mat(140, 240, MatType.CV_8UC1, Scalar.White);
        for (var y = 0; y < bits.Height; y++) for (var x = 0; x < bits.Width; x++) result.Set(y + 20, x + 20, bits[x, y] ? (byte)0 : (byte)255);
        return result;
    }
    private static Mat DenseScene()
    {
        var bits = new MultiFormatWriter().encode("DUP", BarcodeFormat.DATA_MATRIX, 40, 40);
        var scene = new Mat(970, 970, MatType.CV_8UC1, Scalar.White);
        for (var row = 0; row < 16; row++) for (var column = 0; column < 16; column++)
            for (var y = 0; y < bits.Height; y++) for (var x = 0; x < bits.Width; x++) scene.Set(row * 60 + y + 15, column * 60 + x + 15, bits[x, y] ? (byte)0 : (byte)255);
        return scene;
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
    }
    private static ParamBag Bag(string text)
    { var yaml = new YamlStream(); yaml.Load(new StringReader(text)); return ParamBag.FromYaml(yaml.Documents[0].RootNode); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("预期异常: " + typeof(T).Name); }
}
