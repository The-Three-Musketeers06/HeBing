using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AiVisionStudio.Models;
using AiVisionStudio.Views;
using OpenCvSharp;
using YamlDotNet.RepresentationModel;
using Point = System.Windows.Point;

public static class PanCases
{
    public static int Run()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.Resources.MergedDictionaries.Count == 0)
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Themes/Light.xaml", UriKind.Relative) });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Views/ToolEditorResources.xaml", UriKind.Relative) });
        }
        int count = 0, commits = 0;
        void Check(bool valid, string text) { if (!valid) throw new InvalidOperationException(text); count++; }
        using var source = new HwndSource(new HwndSourceParameters("RegionPanRegression")
            { Width = 850, Height = 650, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000) });
        var view = new ImageView();
        var root = new Grid(); root.Children.Add(view); source.RootVisual = root;
        root.Measure(new System.Windows.Size(850, 650)); root.Arrange(new System.Windows.Rect(0, 0, 850, 650)); root.UpdateLayout();
        Pump();
        using var image = new Mat(480, 640, MatType.CV_8UC1, Scalar.White);
        view.ShowMat(image);
        var data = new YamlMappingNode
        {
            { "Center", new YamlMappingNode { { "X", "300" }, { "Y", "240" } } },
            { "Size", new YamlMappingNode { { "Width", "180" }, { "Height", "120" } } },
            { "Rotation", "25" }
        };
        var region = new EditableRegion2D("rect2d", data);
        view.EditRegion(region);
        view.RegionEditor.Commit = _ => { commits++; return true; };
        var before = Field<EditableRegion2D>(view.RegionEditor, "_region").Data.ToString();
        var transform = (MatrixTransform)view.FindName("Mat");
        var host = (Grid)view.FindName("Host");
        var initial = new Matrix(2.5, 0, 0, 2.5, 100, 80); transform.Matrix = initial;
        bool Wants(MouseButton button, ModifierKeys keys) => (bool)Invoke(view, "WantsRegionPan", button, keys);
        bool Begin(Point point, MouseButton button) => (bool)Invoke(view, "BeginRegionPan", point, button);
        try
        {
            Check(Wants(MouseButton.Left, ModifierKeys.Alt), "ROI编辑时必须支持Alt加左键平移");
            Check(Wants(MouseButton.Middle, ModifierKeys.None), "ROI编辑时必须支持中键平移");
            Check(!Wants(MouseButton.Left, ModifierKeys.None), "普通左键必须继续编辑选框");
            Check(!Wants(MouseButton.Right, ModifierKeys.None), "右键不能抢占多边形完成操作");
            Check(Begin(new Point(120, 90), MouseButton.Left), "框选状态必须能够捕获平移操作");
            Invoke(view, "UpdateRegionPan", new Point(150, 110));
            Invoke(view, "UpdateRegionPan", new Point(175, 125));
            Check(transform.Matrix == initial, "平移必须合并到渲染帧，不能每个鼠标事件立即重绘");
            Invoke(view, "EndRegionPan", true);
            Check(Math.Abs(transform.Matrix.OffsetX - 155) < 1e-9 && Math.Abs(transform.Matrix.OffsetY - 115) < 1e-9, "平移必须按起点计算并落在最后一个鼠标位置");
            Check(transform.Matrix.M11 == 2.5 && transform.Matrix.M22 == 2.5, "平移不能改变图片缩放比例");
            Check(commits == 0 && Field<EditableRegion2D>(view.RegionEditor, "_region").Data.ToString() == before, "平移图片不能修改ROI坐标或提交参数");
            Check(view.RegionEditor.IsEditing && !host.IsMouseCaptured && !host.ForceCursor, "平移结束后必须继续支持选框编辑并释放鼠标");
            var hand = Field<Dictionary<string, Button>>(view, "_tools")["pan"];
            hand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Wants(MouseButton.Left, ModifierKeys.None) && host.ForceCursor, "手形按钮必须允许直接左键拖动图片");
            var down = MouseEvent(view.RegionEditor, Mouse.PreviewMouseDownEvent, MouseButton.Left);
            Check(down.Handled && Field<bool>(view, "_regionPanning"), "手形模式下左键事件必须优先于ROI编辑层处理");
            MouseEvent(view.RegionEditor, Mouse.PreviewMouseUpEvent, MouseButton.Middle);
            Check(Field<bool>(view, "_regionPanning"), "无关鼠标按键抬起不能结束当前平移");
            Mouse.Capture(null);
            Check(!Field<bool>(view, "_regionPanning"), "丢失鼠标捕获后不能残留平移状态");
            hand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!Wants(MouseButton.Left, ModifierKeys.None) && !host.ForceCursor, "再次点击手形按钮必须回到选框模式");
            down = MouseEvent(view.RegionEditor, Mouse.PreviewMouseDownEvent, MouseButton.Middle);
            Check(down.Handled && Field<bool>(view, "_regionPanning"), "中键事件必须能穿过ROI交互层开始平移");
            MouseEvent(view.RegionEditor, Mouse.PreviewMouseUpEvent, MouseButton.Middle);
            Check(!Field<bool>(view, "_regionPanning") && !host.IsMouseCaptured, "中键抬起必须正确结束平移");
            Check(commits == 0 && Field<EditableRegion2D>(view.RegionEditor, "_region").Data.ToString() == before, "切换平移模式不能移动或重画选框");
            Check(view.RegionEditor.CaptureMouse(), "ROI交互层必须能保持自己的拖动捕获");
            Check(!Begin(new Point(10, 10), MouseButton.Middle), "调整ROI期间不能被另一个按键抢走鼠标");
            view.RegionEditor.ReleaseMouseCapture();
            Check(Begin(new Point(10, 10), MouseButton.Middle), "完成ROI手势后必须可以继续平移");
            view.EndRegionEdit();
            Check(!Field<bool>(view, "_regionPanning") && !host.IsMouseCaptured, "结束框选必须结束正在进行的平移");
            var circle = new Dictionary<string, object>
            {
                ["Type"] = "circle2d",
                ["circle2d"] = new Dictionary<string, object>
                { ["Center"] = new Dictionary<string, object> { ["X"] = 200d, ["Y"] = 150d }, ["Radius"] = 60d }
            };
            view.BeginRegionEdit("circle2d", circle, _ => commits++);
            Check(Wants(MouseButton.Left, ModifierKeys.Alt), "模板和通用区域编辑也必须支持Alt平移");
            Check(Begin(new Point(40, 30), MouseButton.Middle), "通用区域编辑必须支持中键平移");
            Invoke(view, "UpdateRegionPan", new Point(75, 60));
            view.CancelRegionEdit();
            Check(!host.IsMouseCaptured && !Field<bool>(view, "_viewTransformPending") && commits == 0, "取消区域编辑必须释放捕获、提交最后的视图位置且不提交ROI");
            view.EditRegion(region);
            Check(Begin(new Point(10, 10), MouseButton.Middle), "重新进入框选不能受上次平移影响");
            view.Clear();
            Check(!Field<bool>(view, "_regionPanning") && !host.IsMouseCaptured && !host.ForceCursor, "清空图片不能遗留鼠标捕获或手形状态");
            Check(!Wants(MouseButton.Middle, ModifierKeys.None), "空画布不能开始图片平移");
        }
        finally { Mouse.Capture(null); view.Clear(); source.RootVisual = null; }
        Console.WriteLine($"ROI pan cases passed: {count}");
        return count;
    }

    private static MouseButtonEventArgs MouseEvent(UIElement element, RoutedEvent routedEvent, MouseButton button)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button) { RoutedEvent = routedEvent };
        element.RaiseEvent(args);
        return args;
    }
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    private static object Invoke(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
