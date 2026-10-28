using System.IO;
using System.Windows;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Algorithm.Impl;
using AiVisionStudio.Models;
using AiVisionStudio.Services;
using YamlDotNet.RepresentationModel;
using OpenCvSharp;

public static class ColorConversionCases
{
    public static int Run()
    {
        int count = 0;
        void Check(bool valid, string text) { if (!valid) throw new InvalidOperationException(text); count++; }
        var errors = new List<string>();
        var context = new OpContext { Log = (text, error) => { if (error) errors.Add(text); } };
        var conversion = new ColorConvertOp();
        bool Convert(string values = "Convert Type: Color to Gray") { errors.Clear(); return conversion.Run(context, Bag(values)); }
        void Input(Mat image) => context.Images.Set(0, image.Clone());
        try
        {
            using var color = new Mat(10, 12, MatType.CV_8UC3, new Scalar(10, 20, 200)); Input(color);
            Check(Convert(), "三通道彩色图转灰度必须成功，不能误调用灰度转彩色");
            Check(context.Images.Get(1).Type() == MatType.CV_8UC1 && Math.Abs(context.Images.Get(1).At<byte>(0, 0) - 73) <= 1, "灰度结果必须为正确亮度权重的单通道图像");
            Check(context.Images.Get(0).At<Vec3b>(0, 0) == new Vec3b(10, 20, 200), "转换不能修改上游原图");
            Check(Equals(context.Vars.Get("Input Channel Count"), 3) && Equals(context.Vars.Get("Channel Count"), 1) && Equals(context.Vars.Get("Convert Type"), "彩色转灰度"), "输出信息应反映真实通道数并使用中文转换名称");
            Check(Convert("Convert Type: 彩色转灰度"), "中文转换类型也应正确识别");
            using var alpha = new Mat(6, 8, MatType.CV_8UC4, new Scalar(0, 0, 255, 0)); Input(alpha);
            Check(Convert() && Math.Abs(context.Images.Get(1).At<byte>(0, 0) - 76) <= 1, "四通道彩色图应忽略透明度后正确转灰度");
            using var gray = new Mat(6, 8, MatType.CV_8UC1, new Scalar(83)); Input(gray);
            Check(Convert() && context.Images.Get(1).At<byte>(0, 0) == 83 && context.Vars.Get("Message").ToString().Contains("已经是灰度"), "已经是灰度图时应保留结果并给出清楚提示");
            Check(Convert("Convert Type: Gray to Color") && context.Images.Get(1).Type() == MatType.CV_8UC3 && context.Images.Get(1).At<Vec3b>(0, 0) == new Vec3b(83, 83, 83), "灰度转彩色必须复制成三个通道");
            Input(color);
            Check(!Convert("Convert Type: Gray to Color") && errors.Single().Contains("只接受单通道灰度图"), "错误转换方向必须用中文说明原因和正确选项");
            Check(errors.All(text => !text.Contains("VScn") && !text.Contains("scn") && !text.Contains("Invalid number")), "用户报错不能显示OpenCV通道断言");
            using var primary = new Mat(1, 3, MatType.CV_8UC3);
            primary.Set(0, 0, new Vec3b(0, 0, 255)); primary.Set(0, 1, new Vec3b(0, 255, 0)); primary.Set(0, 2, new Vec3b(255, 0, 0)); Input(primary);
            Check(Convert("Convert Type: Color to HSV"), "彩色转HSV也应使用HALCON正确执行");
            var hsv = context.Images.Get(1);
            Check(hsv.At<Vec3b>(0, 0) == new Vec3b(0, 255, 255) && Math.Abs(hsv.At<Vec3b>(0, 1).Item0 - 85) <= 1 && Math.Abs(hsv.At<Vec3b>(0, 2).Item0 - 170) <= 1, "HSV结果应按H/S/V顺序及HALCON范围编码");
            Input(gray);
            Check(!Convert("Convert Type: Color to HSV") && errors.Single().Contains("重新选择彩色图像来源"), "灰度输入转HSV失败应提示如何处理");
            using var wide = new Mat(6, 8, MatType.CV_16UC3, new Scalar(1000, 32000, 65000)); Input(wide);
            Check(Convert() && context.Images.Get(1).Type() == MatType.CV_16UC1 && Math.Abs(context.Images.Get(1).At<ushort>(0, 0) - 38333) <= 4, "16位灰度应保留位深，允许HALCON整数计算的量化误差");
            using var wideGray = new Mat(6, 8, MatType.CV_16UC1, new Scalar(41000)); Input(wideGray);
            Check(Convert("Convert Type: Gray to Color") && context.Images.Get(1).Type() == MatType.CV_16UC3 && context.Images.Get(1).At<Vec3w>(0, 0) == new Vec3w(41000, 41000, 41000), "16位灰度转彩色也必须保留位深");
            using var real = new Mat(6, 8, MatType.CV_32FC3, new Scalar(.1, .2, .9)); Input(real);
            Check(Convert() && context.Images.Get(1).Type() == MatType.CV_32FC1 && Math.Abs(context.Images.Get(1).At<float>(0, 0) - .3979) < 1e-5, "浮点彩色转灰度必须保留精度");
            context.Images.Set(0, new Mat(color, new OpenCvSharp.Rect(2, 1, 5, 6)));
            Check(Convert() && context.Images.Get(1).Size() == new OpenCvSharp.Size(5, 6), "非连续子图也必须支持转换");
            Input(color);
            Check(Convert("Convert Type: Color to Gray\nIM Out: 0") && context.Images.Get(0).Type() == MatType.CV_8UC1 && Equals(context.Vars.Get("Width"), 12), "原地转换不能访问已释放的输入内存");
            Input(color);
            Check(!Convert("Convert Type: Other") && errors.Single().Contains("转换类型无效"), "未知模式必须明确拒绝");
            using var unsupported = new Mat(3, 4, MatType.CV_64FC3, Scalar.All(1)); Input(unsupported);
            Check(!Convert() && errors.Single().Contains("像素类型不受支持"), "不支持的像素类型应给出中文说明");
            context.Images.Remove(0);
            Check(!Convert() && errors.Single().Contains("没有输入图像"), "输入为空应说明需要选择来源并运行前置步骤");
            Input(color); context.CancellationToken = new CancellationToken(true);
            bool cancelled = false; try { Convert(); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "转换应响应停止请求"); context.CancellationToken = default;
        }
        finally { context.Images.Dispose(); }

        DriverCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "driver_defs.json"));
        var directory = Path.Combine(AppContext.BaseDirectory, "case-output", "color-convert"); Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "color.png");
        using (var image = new Mat(20, 30, MatType.CV_8UC3, new Scalar(10, 20, 200))) Cv2.ImWrite(file, image);
        var vm = new MainViewModel(Path.Combine(directory, "settings"));
        try
        {
            vm.AddTool(new ToolNode { Label = "LoadImage", Cn = "加载图片" }); var source = vm.SelectedOperation;
            vm.WriteParameterValue("File", "Type"); vm.WriteParameterValue(file, "File");
            Check(!vm.AllParameterNodes.Any(node => VisualResultNames.IsOutputRoute(source.Driver, node.Name)), "取图控件自身不应显示输出图像数字选择框");
            vm.AddTool(new ToolNode { Label = "Basic Color Convert", Cn = "彩色转换" }); var convert = vm.SelectedOperation;
            Check(!vm.AllParameterNodes.Any(node => node.Name == "IM Out"), "彩色转换自身也必须隐藏输出编号");
            var input = vm.AllParameterNodes.Single(node => node.Name == "IM In");
            vm.SetImageInput(input, vm.GetImageInputChoices(convert).Single(choice => choice.Reference.OperationId == source.Uuid));
            vm.RunAll();
            Check(convert.State == OpState.Ok && vm.Images.Get(1).Channels() == 1, "从命名来源读取的彩色图必须成功转灰度");
            var key = vm.GetVisualSources().Single(info => info.Key.OperationId == convert.Uuid).Key;
            Mat oldImage = null; vm.UseVisualSnapshot(key, snapshot => oldImage = snapshot.Image);
            Check(vm.GetVisualSources().Single(info => info.Key == key).OutputName == "图像", "结果来源应使用结果名称，而非输出编号");
            vm.WriteParameterValue("Gray to Color", "Convert Type");
            Check(!oldImage.IsDisposed, "修改转换选项但未运行时仍应保留上次结果");
            vm.RunAll();
            Check(convert.State == OpState.Fail && convert.ErrorMessage.Contains("只接受单通道") && !convert.ErrorMessage.Contains("ArgumentException"), "错误方向应在实际窗口输出中文提示，不能带技术异常前缀");
            Check(oldImage.IsDisposed && !vm.Images.Has(1) && !vm.GetVisualSources().Any(info => info.Key.OperationId == convert.Uuid), "转换失败必须清除旧结果及快照");
            vm.WriteParameterValue("Color to Gray", "Convert Type"); vm.RunAll(); Check(convert.State == OpState.Ok, "改回正确方向后应恢复转换");
            vm.AddTool(new ToolNode { Label = "Basic Mean Filter", Cn = "后续滤波" }); var consumer = vm.SelectedOperation;
            var choice = vm.GetImageInputChoices(consumer).Single(choice => choice.Reference.OperationId == convert.Uuid);
            Check(choice.Label == "彩色转换 / 图像", "其他控件选择时应显示控件名称加结果");
            vm.SetImageInput(vm.AllParameterNodes.Single(node => node.Name == "IM In"), choice); vm.RunAll();
            Check(consumer.State == OpState.Ok && vm.Images.Get(1).Channels() == 1, "即使内部使用相同输出编号，也应正确处理所选控件的结果");
            vm.SelectedOperation = convert; vm.RenameOperation(convert, "灰度预处理"); vm.SelectedOperation = consumer;
            Check(vm.AllParameterNodes.Single(node => node.Name == "IM In").ImageInputLabel == "灰度预处理 / 图像", "重命名应同步更新自动结果名，不改变绑定身份");
            vm.AddTool(new ToolNode { Label = ChannelSplitChannels.Driver, Cn = "通道拆分" }); var split = vm.SelectedOperation;
            Check(!vm.AllParameterNodes.Any(node => VisualResultNames.IsOutputRoute(split.Driver, node.Name)), "通道拆分的三路输出数字也应全部隐藏");
            vm.SetImageInput(vm.AllParameterNodes.Single(node => node.Name == "IM In"), vm.GetImageInputChoices(split).Single(choice => choice.Reference.OperationId == source.Uuid));
            vm.RunAll(); Check(split.State == OpState.Ok && !vm.SelectedOutputs.Any(row => VisualResultNames.IsOutputRoute(split.Driver, row.Name)), "控件输出表不能再显示内部通道编号");
            Check(vm.GetSelectedOutputValues().ContainsKey("IM Out R"), "隐藏内部编号时必须保留旧工程及结果绑定兼容");
        }
        finally { vm.Dispose(); }
        Console.WriteLine($"Color conversion and result naming cases passed: {count}");
        return count;
    }

    private static ParamBag Bag(string yaml)
    {
        var stream = new YamlStream(); stream.Load(new StringReader(yaml));
        return ParamBag.FromYaml(new YamlMappingNode { { "value", stream.Documents[0].RootNode } });
    }
}
