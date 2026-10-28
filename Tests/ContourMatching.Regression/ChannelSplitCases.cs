using System.IO;
using System.Text.Json;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Algorithm.Impl;
using AiVisionStudio.Models;
using AiVisionStudio.Services;
using OpenCvSharp;
using YamlDotNet.RepresentationModel;

public static class ChannelSplitCases
{
    public static int Run()
    {
        int count = 0;
        void Check(bool valid, string text) { if (!valid) throw new InvalidOperationException(text); count++; }
        var context = new OpContext { Log = (_, _) => { } };
        var operation = new ChannelSplitOp();
        bool Split(string values = "Split Mode: RGB") => operation.Run(context, Bag(values));
        void Input(Mat image) => context.Images.Set(0, image.Clone());
        bool Rejected(string values, string message)
        {
            try { Split(values); return false; }
            catch (ArgumentException exception) { return exception.Message.Contains(message); }
        }
        byte Pixel(int slot) => context.Images.Get(slot).At<byte>(0, 0);
        try
        {
            using var bgr = new Mat(12, 14, MatType.CV_8UC3, new Scalar(10, 20, 30)); Input(bgr);
            Check(Split(), "RGB拆分必须成功");
            Check(Pixel(1) == 30 && Pixel(2) == 20 && Pixel(3) == 10, "默认输出必须为R1/G2/B3，不能颠倒BGR内存顺序");
            Check(context.Images.Get(1).Type() == MatType.CV_8UC1, "每一路必须为独立单通道图像");
            Check(context.Images.Get(0).At<Vec3b>(0, 0) == new Vec3b(10, 20, 30), "拆分不能修改原始彩色图像");
            Check(Convert.ToInt32(context.Vars.Get("Channel Count")) == 3 && Convert.ToInt32(context.Vars.Get("Input Channel Count")) == 3, "输出表必须发布输入输出通道数");
            Check(Equals(context.Vars.Get("IM Out R"), 1) && Equals(context.Vars.Get("Split Mode"), "RGB"), "通道编号输出必须能用于后续结果绑定");
            Check(Split("Split Mode: RGB\nIM Out R: 8\nIM Out G: 2\nIM Out B: 6"), "自定义输出编号应受支持");
            Check(Pixel(8) == 30 && Pixel(2) == 20 && Pixel(6) == 10, "输出顺序不能由IM编号排序决定");
            Check(Rejected("Split Mode: RGB\nIM Out G: 1", "互不相同"), "重复输出编号不能覆盖其他通道");
            Check(Rejected("Split Mode: RGB\nIM Out R: -1", "负数"), "非法输出编号必须明确拒绝");
            Check(Rejected("Split Mode: Lab", "RGB或HSV"), "未知模式不能静默执行RGB");
            Check(context.Vars.All.Count == 0, "异常不能残留上一轮输出参数");
            using var primaries = new Mat(1, 4, MatType.CV_8UC3);
            primaries.Set(0, 0, new Vec3b(0, 0, 255)); primaries.Set(0, 1, new Vec3b(0, 255, 0));
            primaries.Set(0, 2, new Vec3b(255, 0, 0)); primaries.Set(0, 3, new Vec3b(80, 80, 80)); Input(primaries);
            Check(Split("Split Mode: HSV"), "HSV转换必须通过HALCON成功执行");
            var hue = context.Images.Get(1); var saturation = context.Images.Get(2); var value = context.Images.Get(3);
            Check(hue.At<byte>(0, 0) == 0 && Math.Abs(hue.At<byte>(0, 1) - 85) <= 1 && Math.Abs(hue.At<byte>(0, 2) - 170) <= 1, "HALCON的8位色相必须按0到255编码");
            Check(saturation.At<byte>(0, 0) == 255 && value.At<byte>(0, 0) == 255, "纯色饱和度及明度必须正确");
            Check(saturation.At<byte>(0, 3) == 0 && value.At<byte>(0, 3) == 80, "三通道灰色像素仍应生成有效HSV");
            Check(Equals(context.Vars.Get("Hue Range"), "0～255") && context.Vars.Get("IM Out R") == null, "HSV输出不能混入RGB字段");
            using var bgra = new Mat(3, 4, MatType.CV_8UC4, new Scalar(7, 11, 19, 0)); Input(bgra); Split();
            Check(Pixel(1) == 19 && Pixel(2) == 11 && Pixel(3) == 7, "BGRA必须正确忽略透明通道");
            Check(Equals(context.Vars.Get("Input Channel Count"), 4), "四通道输入计数必须真实");
            using var wide = new Mat(3, 4, MatType.CV_16UC3, new Scalar(1000, 32000, 65000)); Input(wide); Split();
            Check(context.Images.Get(1).Type() == MatType.CV_16UC1 && context.Images.Get(1).At<ushort>(0, 0) == 65000 && context.Images.Get(3).At<ushort>(0, 0) == 1000, "16位输入不能被压成8位");
            using var wideGreen = new Mat(3, 4, MatType.CV_16UC3, new Scalar(0, 65535, 0)); Input(wideGreen); Split("Split Mode: HSV");
            Check(Math.Abs(context.Images.Get(1).At<ushort>(0, 0) - 7200) <= 1 && context.Images.Get(3).At<ushort>(0, 0) == 65535, "16位HSV色相必须使用HALCON角分编码");
            using var wideAlpha = new Mat(3, 4, MatType.CV_16UC4, new Scalar(300, 16000, 63000, 0)); Input(wideAlpha); Split();
            Check(context.Images.Get(1).At<ushort>(0, 0) == 63000 && context.Images.Get(2).At<ushort>(0, 0) == 16000, "16位BGRA顺序必须正确");
            using var real = new Mat(3, 4, MatType.CV_32FC3, new Scalar(.1, .2, .9)); Input(real); Split();
            Check(context.Images.Get(1).Type() == MatType.CV_32FC1 && Math.Abs(context.Images.Get(1).At<float>(0, 0) - .9) < 1e-6, "浮点拆分必须保留精度");
            using var realGreen = new Mat(3, 4, MatType.CV_32FC4, new Scalar(0, 1, 0, .5)); Input(realGreen); Split("Split Mode: HSV");
            Check(Math.Abs(context.Images.Get(1).At<float>(0, 0) - 2 * Math.PI / 3) < 1e-5 && context.Images.Get(2).At<float>(0, 0) == 1, "浮点HSV应使用弧度及归一化饱和度");
            using var roi = new Mat(bgr, new Rect(2, 1, 5, 6)); context.Images.Set(0, roi.Clone()); Split();
            Check(context.Images.Get(1).Size() == new Size(5, 6) && Pixel(1) == 30, "子图尺寸和像素必须保持正确");
            context.Images.Set(0, new Mat(bgr, new Rect(2, 1, 5, 6))); Split();
            Check(Pixel(2) == 20 && Pixel(3) == 10, "非连续内存子图也必须正确拆分");
            Input(bgr); Check(Split("Split Mode: RGB\nIM Out R: 0"), "输入输出同编号必须先完成全部通道复制");
            Check(Pixel(0) == 30 && Pixel(2) == 20 && Equals(context.Vars.Get("Input Channel Count"), 3), "原地输出不能读已释放的输入内存");
            using var gray = new Mat(10, 10, MatType.CV_8UC1, Scalar.White); Input(gray);
            Check(Rejected("Split Mode: RGB", "灰度图") && Rejected("Split Mode: HSV", "灰度图"), "灰度图必须明确提示，不能仅写一路后报成功");
            using var unsupported = new Mat(3, 4, MatType.CV_64FC3, Scalar.All(1)); Input(unsupported);
            Check(Rejected("Split Mode: RGB", "32位浮点"), "不支持的深度不能静默损失精度");
            context.Images.Remove(0); Check(Rejected("Split Mode: RGB", "为空"), "无输入图像必须返回明确错误");
            Input(bgr); context.CancellationToken = new CancellationToken(true);
            bool cancelled = false; try { Split(); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "拆分必须响应停止请求"); context.CancellationToken = default;
        }
        finally { context.Images.Dispose(); }

        DriverCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "driver_defs.json"));
        Check(DriverCatalog.Get(ChannelSplitChannels.Driver).Params.Where(p => p.Name.StartsWith("IM Out ")).All(p => p.Field == "basic" && p.DisplayName.Contains("通道图像")), "三路输出设置必须在计算参数中显示中文");
        Check(VarRow.FromOwned("Result", true, ChannelSplitChannels.Driver).DisplayName == "结果", "输出状态必须汉化");
        Check(VarRow.FromOwned("Result", true).DisplayName == "Result", "自定义全局变量不能被误翻译");
        var nested = VarRow.FromOwned("ROI", new Dictionary<string, object> { ["ShapeCount"] = 3 }, ChannelSplitChannels.Driver); nested.Expand();
        Check(nested.Children.Single().DisplayName == "图形数量" && nested.Children.Single().Name == "ShapeCount", "嵌套输出只翻译显示文字，不能修改绑定键");
        Check(OutputLabels.Translate("ROI.ShapeCount") == "感兴趣区域.图形数量" && OutputLabels.Translate("我的结果") == "我的结果", "点路径及自定义中文名应正确处理");
        using var json = JsonDocument.Parse("{\"name\":\"Radius\",\"type\":\"float\",\"value\":2.5}");
        Check(VarRow.FromNative(json.RootElement, outputDriver: "Basic Circle Find").DisplayName == "半径", "原生输出显示也必须汉化");
        var a = new VisualSourceKey("main", "one", "IM1", VisualResultKind.Image);
        var b = a with { OutputKey = "IM2", Kind = VisualResultKind.Cloud };
        var r = a with { OutputKey = "channel:R" }; var g = a with { OutputKey = "channel:G" };
        Check(a == b && a.GetHashCode() == b.GetHashCode(), "普通控件仍然只能对应一个来源");
        Check(r != g && r == (r with { Kind = VisualResultKind.Cloud }) && r.GetHashCode() == (r with { Kind = VisualResultKind.Cloud }).GetHashCode(), "真实通道应各有独立稳定身份");

        var vm = new MainViewModel(Path.Combine(AppContext.BaseDirectory, "case-output", "channel-settings"));
        try
        {
            vm.AddTool(new ToolNode { Label = ChannelSplitChannels.Driver, Cn = "通道拆分" }); var split = vm.SelectedOperation;
            using var image = new Mat(10, 12, MatType.CV_8UC3, new Scalar(12, 50, 230)); vm.Images.Set(0, image.Clone()); vm.RunAll();
            Check(split.State == OpState.Ok && vm.GetVisualSources().Count == 3, "引擎必须发布三路独立图像快照");
            Check(vm.GetVisualSources().Select(source => source.Key.OutputKey).ToHashSet().SetEquals(new[] { "channel:R", "channel:G", "channel:B" }), "RGB快照不能重用普通IM身份");
            Check(vm.GetSelectedPreviewIndex() == 1, "参数窗口默认预览红色通道");
            Check(!vm.SelectedOutputs.Any(row => row.Name == "IM Out R") && vm.GetSelectedOutputValues().ContainsKey("IM Out R"), "内部通道编号应保留兼容，但不显示在本控件输出表中");
            var previous = new List<Mat>(); foreach (var info in vm.GetVisualSources()) vm.UseVisualSnapshot(info.Key, snap => previous.Add(snap.Image));
            Check(vm.WriteParameterValue("HSV", "Split Mode") && previous.All(mat => !mat.IsDisposed), "修改模式但未执行不能清空画布");
            vm.RunAll(); Check(previous.All(mat => mat.IsDisposed), "重新执行必须释放旧通道快照");
            Check(split.State == OpState.Ok && vm.GetVisualSources().Count == 3 && vm.GetVisualSources().All(source => new[] { "channel:H", "channel:S", "channel:V" }.Contains(source.Key.OutputKey)), "HSV必须替换RGB，不能累积六个图层");
            Check(!vm.GetSelectedOutputValues().ContainsKey("IM Out R") && vm.GetSelectedOutputValues().ContainsKey("IM Out H"), "模式切换必须删除过期输出字段");
            vm.RunAll(); Check(vm.GetVisualSources().Count == 3, "同一来源重复运行不能增加图层数量");
            Check(vm.WriteParameterValue(7, "IM Out H") && vm.GetVisualSources().Count == 0, "改输出路由应清除旧通道结果");
            vm.RunAll(); Check(vm.GetSelectedPreviewIndex() == 7 && vm.GetVisualSources().Count == 3 && !vm.Images.Has(1), "新路由必须生效并释放旧IM图像");
            vm.AddTool(new ToolNode { Label = "Basic Mean Filter", Cn = "后续滤波" }); var filter = vm.SelectedOperation;
            var bindingChoices = vm.GetBindingChoices(null);
            Check(bindingChoices.Any(choice => choice.OperationId == split.Uuid && choice.Variable == "IM Out H" && choice.Label.Contains("色相通道")), "绑定选择汉化后仍需传递原始输出键");
            Check(vm.WriteParameterValue(7, "IM In") && vm.WriteParameterValue(9, "IM Out"), "后续控件应能读取拆分通道"); vm.RunAll();
            Check(filter.State == OpState.Ok && vm.GetVisualSources().Count == 4, "后续滤波和三路通道必须共存");
            previous.Clear(); foreach (var info in vm.GetVisualSources()) vm.UseVisualSnapshot(info.Key, snap => previous.Add(snap.Image));
            vm.Images.Set(0, new Mat(10, 12, MatType.CV_8UC1, Scalar.White)); vm.RunAll();
            Check(split.State == OpState.Fail && split.ErrorMessage.Contains("灰度图"), "引擎应正确显示灰度输入失败原因");
            Check(previous.All(mat => mat.IsDisposed) && vm.GetVisualSources().Count == 0 && !vm.Images.Has(7) && !vm.Images.Has(2) && !vm.Images.Has(3) && !vm.Images.Has(9), "拆分失败必须清除三路旧图像以及依赖结果");
            Check(vm.Images.Has(0), "拆分失败不能删除原始输入");
            vm.Images.Set(0, image.Clone()); vm.RunAll(); vm.RemoveOperation(split);
            Check(vm.GetVisualSources().Count == 0 && !vm.Images.Has(7) && !vm.Images.Has(9) && vm.Images.Has(0), "删除算子必须释放所有通道及其依赖图像");
        }
        finally { vm.Dispose(); }
        Console.WriteLine($"HALCON channel split cases passed: {count}");
        return count;
    }

    private static ParamBag Bag(string yaml)
    {
        var stream = new YamlStream(); stream.Load(new StringReader(yaml));
        return ParamBag.FromYaml(new YamlMappingNode { { "value", stream.Documents[0].RootNode } });
    }
}
