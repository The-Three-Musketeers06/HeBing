using System.IO;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Algorithm.Impl;
using AiVisionStudio.Models;
using AiVisionStudio.Services;
using OpenCvSharp;
using YamlDotNet.RepresentationModel;

public static class HalconGeometryCases
{
    public static int Run()
    {
        int count = 0;
        var log = new List<string>();
        var context = new OpContext { Log = (text, _) => log.Add(text) };
        void Check(bool valid, string text) { if (!valid) throw new InvalidOperationException(text); count++; }
        void Input(Mat image) => context.Images.Set(0, image.Clone());
        bool Run(IOperator op, string yaml) => op.Run(context, Bag(yaml));
        var edge = new EdgePointFind2DOp(); var line = new LineFindOp(); var circle = new CircleFindOp();
        const string edgeRegion = "Region: {Type: rect2d, rect2d: {Center: {X: 170, Y: 120}, Size: {Width: 180, Height: 12}, Rotation: 0}}";
        const string lineRegion = "Region: {Type: line_caliper, line_caliper: {Start: {X: 50, Y: 104}, End: {X: 270, Y: 104}, Caliper Count: 24, Caliper Height: 40, Caliper Width: 5}}";
        const string circleRegion = "Region: {Type: circle_caliper, circle_caliper: {Center: {X: 158, Y: 121}, Radius: 61, Caliper Count: 48, Caliper Height: 20, Caliper Width: 3}}";
        PointObj[] Points() => ((List<object>)context.Vars.Get("Points")).Cast<PointObj>().ToArray();
        try
        {
            using var bar = new Mat(240, 320, MatType.CV_8UC1, Scalar.Black);
            Cv2.Rectangle(bar, new Rect(120, 0, 100, 240), Scalar.White, -1);
            Input(bar);
            Check(Run(edge, edgeRegion + "\nSelection: First"), "HALCON边缘点默认搜索应成功");
            Check(Points().Length == 12 && Points().All(point => Math.Abs(point.X - 119.5) < .15), "X/Y映射及亚像素位置必须正确");
            Check(Run(edge, edgeRegion + "\nSelection: Last"), "最后一条边缘应可读取");
            Check(Points().All(point => Math.Abs(point.X - 219.5) < .15), "Last必须选择另一条边缘");
            Check(Run(edge, edgeRegion + "\nSelection: All"), "All应读取全部边缘");
            Check(Points().Length == 24, "All不能仅返回最强边缘");
            Check(Run(edge, edgeRegion + "\nPolarity: Dark -> Bright"), "正向黑到白极性应成功");
            Check(Points().All(point => point.X < 150), "黑到白应选择左边缘");
            Check(Run(edge, edgeRegion + "\nPolarity: Bright -> Dark"), "正向白到黑极性应成功");
            Check(Points().All(point => point.X > 200), "白到黑应选择右边缘");
            Check(Run(edge, edgeRegion + "\nSearch Direction: Backward\nPolarity: Dark -> Bright"), "反向搜索必须正确转换极性");
            Check(Points().All(point => Math.Abs(point.X - 219.5) < .15), "Backward的First应从右侧开始");
            Check(Run(edge, edgeRegion + """

Use Shielding Area: true
Shielding Area: {Type: polygon2d, polygon2d: {Points: [{X: 110,Y: 90},{X: 130,Y: 90},{X: 130,Y: 150},{X: 110,Y: 150}]}}
"""), "屏蔽左边缘后仍应找到右边缘");
            Check(Points().All(point => point.X > 200), "屏蔽区域中的边缘不能参与选择");
            Check(Run(edge, edgeRegion.Replace("X: 170", "X: 120") + """

Position Fix: true
Points Information:
  Base Point: {Point: {X: 0,Y: 0}, Angle: 0, ScaleX: 1, ScaleY: 1}
  Run Point: {Point: {X: 50,Y: 0}, Angle: 0, ScaleX: 1, ScaleY: 1}
"""), "边缘搜索区域应跟随定位平移");
            Check(Points().All(point => Math.Abs(point.X - 119.5) < .15), "定位修正后输出必须为实际图像坐标");
            using var uneven = new Mat(240, 320, MatType.CV_8UC1, Scalar.Black);
            Cv2.Rectangle(uneven, new Rect(120, 0, 200, 240), new Scalar(180), -1);
            Cv2.Rectangle(uneven, new Rect(220, 0, 100, 240), Scalar.White, -1);
            Input(uneven);
            Check(Run(edge, edgeRegion + "\nSelection: Strongest"), "最强边缘选择应成功");
            Check(Points().All(point => point.X < 150), "Strongest必须按HALCON边缘幅度选择");

            using var horizontal = new Mat(240, 320, MatType.CV_8UC1, Scalar.Black);
            Cv2.Rectangle(horizontal, new Rect(0, 100, 320, 140), Scalar.White, -1); Input(horizontal);
            Check(Run(line, lineRegion + "\nDisplay Points: true"), "HALCON直线卡尺应成功");
            var segment = (SegmentObj)context.Vars.Get("Segment");
            Check(Math.Abs(segment.Y1 - 99.5) < .15 && Math.Abs(segment.Y2 - 99.5) < .15, "直线拟合必须保留亚像素精度");
            Check(Math.Abs(segment.X1 - 50) < .01 && Math.Abs(segment.X2 - 270) < .01 && Math.Abs(segment.Length - 220) < .1, "端点必须按原始卡尺范围投影");
            Check(Math.Abs(segment.Angle) < .01 && Convert.ToInt32(context.Vars.Get("EdgePointCount")) == 24, "直线角度和点数输出必须兼容现有绑定");
            Check(context.NativeOverlays.Count == 25, "直线及24个边缘点应显示在现有画布中");
            Check(Run(line, lineRegion + "\nFit Method: Auto Ignore"), "自动剔除必须调用HALCON鲁棒拟合");
            Check(Run(line, lineRegion + "\nFit Method: Ignore Exception\nIgnore Point Count: 2"), "指定剔除数量后必须重新拟合");
            Check(Convert.ToInt32(context.Vars.Get("EdgePointCount")) == 22, "指定剔除数量必须生效");
            Check(!Run(line, lineRegion + "\nSearch Direction: Backward\nPolarity: Dark -> Bright"), "反向错误极性必须失败，不能偷偷改为全极性");
            Check(context.Vars.Get("Segment") == null && context.NativeOverlays.Count == 0, "直线失败必须清除旧坐标和图形");
            Check(Run(line, lineRegion.Replace("Y: 104", "Y: 74") + """

Position Fix: true
Points Information:
  Base Point: {Point: {X: 0,Y: 0}, Angle: 0, ScaleX: 1, ScaleY: 1}
  Run Point: {Point: {X: 0,Y: 30}, Angle: 0, ScaleX: 1, ScaleY: 1}
"""), "直线卡尺必须支持定位平移");
            Check(Math.Abs(((SegmentObj)context.Vars.Get("Segment")).Y1 - 99.5) < .15, "定位后的直线不能回传示教坐标");
            using var diagonal = new Mat(240, 320, MatType.CV_8UC1, Scalar.Black);
            Cv2.FillPoly(diagonal, [new[] { new Point(0, 40), new Point(319, 200), new Point(319, 239), new Point(0, 239) }], Scalar.White); Input(diagonal);
            Check(Run(line, "Region: {Type: line_caliper, line_caliper: {Start: {X: 40,Y: 65}, End: {X: 280,Y: 185}, Caliper Count: 30, Caliper Height: 30, Caliper Width: 5}}"), "倾斜直线必须正确转换HALCON角度");
            Check(Math.Abs(((SegmentObj)context.Vars.Get("Segment")).Angle - 26.565) < .4, "角度应保持原有图像顺时针角度约定");
            using var outlierImage = horizontal.Clone();
            Cv2.Rectangle(outlierImage, new Rect(120, 100, 30, 8), Scalar.Black, -1); Input(outlierImage);
            Check(Run(line, lineRegion + "\nFit Method: Auto Ignore"), "存在异常边缘点时鲁棒拟合应成功");
            Check(Math.Abs(((SegmentObj)context.Vars.Get("Segment")).Y1 - 99.5) < .3, "鲁棒拟合不能被局部异常边缘带偏");
            Check(Run(line, lineRegion + "\nFit Method: Ignore Exception\nIgnore Point Count: 100\nReduce Ignore Nums: true"), "自动减少剔除数量应避免点数不足");
            Check(Convert.ToInt32(context.Vars.Get("EdgePointCount")) == 2, "自动减少应保留足够的拟合点");
            bool tooMany = false;
            try { Run(line, lineRegion + "\nFit Method: Ignore Exception\nIgnore Point Count: 100"); } catch (ArgumentException) { tooMany = true; }
            Check(tooMany && context.Vars.Get("Segment") == null, "不允许自动减少时，过量剔除必须明确失败");

            using var disk = new Mat(240, 320, MatType.CV_8UC1, Scalar.Black);
            Cv2.Circle(disk, new Point(160, 120), 60, Scalar.White, -1, LineTypes.AntiAlias); Input(disk);
            Check(Run(circle, circleRegion + "\nPolarity: Bright -> Dark\nDisplay Points: true"), "HALCON圆卡尺应成功");
            var result = (CircleObj)context.Vars.Get("Circle");
            Check(Math.Abs(result.CenterX - 160) < .4 && Math.Abs(result.CenterY - 120) < .4 && Math.Abs(result.Radius - 60) < 1, "圆心X/Y及半径应与真实图像一致");
            Check(Convert.ToInt32(context.Vars.Get("EdgePointCount")) == 48, "圆卡尺数量必须生效");
            Check(Run(circle, circleRegion + "\nFit Method: Auto Ignore"), "圆应支持HALCON鲁棒拟合");
            Check(Run(circle, circleRegion.Replace("Caliper Count: 48", "Caliper Count: 3") + "\nFilter Width: 1"), "最小圆卡尺数量及最小滤波宽度应受支持");
            Check(Run(circle, circleRegion + "\nFit Method: Ignore Exception\nIgnore Point Count: 3"), "圆应支持指定剔除数量");
            Check(Convert.ToInt32(context.Vars.Get("EdgePointCount")) == 45, "圆的剔除数量必须生效");
            Check(!Run(circle, circleRegion + "\nPolarity: Dark -> Bright"), "圆的错误极性应失败");
            Check(context.Vars.Get("Circle") == null && context.Vars.Get("Radius") == null
                && context.NativeOverlays.All(overlay => !Equals(overlay.Values.GetValueOrDefault("_owned_operation"), circle.Driver)), "失败圆不能残留自己的成功结果或叠加图形");
            Check(Run(circle, circleRegion + "\nSearch Direction: Backward\nPolarity: Dark -> Bright"), "圆卡尺反向搜索应正确转换极性");
            Check(Run(circle, """
Region: {Type: circle_caliper, circle_caliper: {Center: {X: 80,Y: 60}, Radius: 30, Caliper Count: 36, Caliper Height: 12, Caliper Width: 3}}
Position Fix: true
Points Information:
  Base Point: {Point: {X: 0,Y: 0}, Angle: 0, ScaleX: 1, ScaleY: 1}
  Run Point: {Point: {X: 0,Y: 0}, Angle: 0, ScaleX: 2, ScaleY: 2}
"""), "圆卡尺应支持定位缩放");
            Check(Math.Abs(((CircleObj)context.Vars.Get("Circle")).Radius - 60) < 1, "缩放不能重复作用于圆输出");
            using var color = new Mat(); Cv2.CvtColor(disk, color, ColorConversionCodes.GRAY2BGR); Input(color);
            Check(Run(circle, circleRegion), "彩色输入应能适配HALCON测量");
            using var depth = new Mat(); disk.ConvertTo(depth, MatType.CV_16U, 257); Input(depth);
            Check(Run(circle, circleRegion), "16位输入应保持灰度归一化兼容");
            using var blank = new Mat(240, 320, MatType.CV_8UC1, Scalar.Black); Input(blank);
            Check(!Run(edge, edgeRegion) && Points().Length == 0, "空图像不能返回旧边缘点");
            Check(!Run(circle, circleRegion) && context.Vars.Get("Circle") == null, "空图像不能返回旧圆");
            Check(!Run(line, lineRegion) && context.Vars.Get("Segment") == null, "空图像不能返回旧直线");
            bool invalid = false;
            try { Run(circle, circleRegion.Replace("Radius: 61", "Radius: 0")); } catch (ArgumentException) { invalid = true; }
            Check(invalid, "未绘制有效搜索区域时必须提示，不能切换为其他算法");
            context.CancellationToken = new CancellationToken(true);
            bool cancelled = false;
            try { Run(line, lineRegion); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "停止操作必须能取消HALCON采样流程");
            context.CancellationToken = CancellationToken.None;
            Check(log.Any(text => text.StartsWith("HALCON 查找直线")) && log.Any(text => text.StartsWith("HALCON 查找圆")) && log.Any(text => text.StartsWith("HALCON 查找边缘点")), "三个入口必须实际使用HALCON后端");
            Check(!context.Images.Get(0).IsDisposed, "检测结束不能释放上游原图");
        }
        finally { context.Images.Dispose(); }
        DriverCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "driver_defs.json"));
        var vm = new MainViewModel(Path.Combine(AppContext.BaseDirectory, "case-output", "halcon-settings"));
        try
        {
            vm.AddTool(new ToolNode { Label = "Basic Line Find", Cn = "HALCON直线测试" });
            Check(vm.WriteParameterValue(Bag(lineRegion).Raw("Region"), "Region"), "HALCON卡尺参数必须通过现有参数编辑器写入");
            using var image = new Mat(240, 320, MatType.CV_8UC1, Scalar.Black);
            Cv2.Rectangle(image, new Rect(0, 100, 320, 140), Scalar.White, -1);
            vm.Images.Set(0, image.Clone()); vm.RunAll();
            Check(vm.SelectedOperation.State == OpState.Ok && vm.GetSelectedOutputValues().GetValueOrDefault("Segment") is SegmentObj, "现有执行引擎必须发布HALCON结果");
            var key = vm.GetVisualSources().Single().Key;
            Mat previous = null;
            vm.UseVisualSnapshot(key, snapshot => previous = snapshot.Image);
            Check(previous != null && !previous.IsDisposed, "HALCON结果必须能进入逐工具画布缓存");
            vm.WriteParameterValue(false, "Display Segment");
            Check(!previous.IsDisposed, "修改显示参数但未运行时必须保留画面");
            vm.RunAll();
            Check(previous.IsDisposed && vm.GetVisualSources().Count == 1, "重新运行必须替换旧快照而不能增加图层编号");
            vm.UseVisualSnapshot(key, snapshot => previous = snapshot.Image);
            Check(vm.SelectedOperation.State == OpState.Ok, "关闭标注后仍应执行HALCON检测");
            vm.Images.Set(0, new Mat(240, 320, MatType.CV_8UC1, Scalar.Black)); vm.RunAll();
            Check(vm.SelectedOperation.State == OpState.Fail && previous.IsDisposed && vm.GetSelectedOutputValues().GetValueOrDefault("Segment") == null, "失败检测必须删除旧的成功结果和图像快照");
            Check(vm.GetVisualSources().Count == 1, "失败检测应保留本轮输入图像而不是旧的检测图像");
            vm.UseVisualSnapshot(key, snapshot =>
            {
                Check(snapshot.Overlays.Count == 0 && Cv2.CountNonZero(snapshot.Image) == 0, "失败画布必须显示本轮干净输入，不能残留旧线");
                previous = snapshot.Image;
            });
            vm.Images.Remove(0); vm.RunAll();
            Check(vm.GetVisualSources().Count == 0 && previous.IsDisposed, "输入消失时必须彻底清除图层缓存");
        }
        finally { vm.Dispose(); }
        Console.WriteLine($"HALCON geometry cases passed: {count}");
        return count;
    }
    private static ParamBag Bag(string yaml)
    {
        var stream = new YamlStream(); stream.Load(new StringReader(yaml));
        return ParamBag.FromYaml(new YamlMappingNode { { "value", stream.Documents[0].RootNode } });
    }
}
