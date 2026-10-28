using System.Reflection;
using System.Windows;
using AiVisionStudio.Views;

public static class RegionCases
{
    public static int Run()
    {
        var count = 0;
        var type = typeof(ImageView).GetNestedType("RegionDraft", BindingFlags.NonPublic)!;
        object Read(string shape, object body) => type.GetMethod("Read")!.Invoke(null, [shape, new Dictionary<string, object> { ["Type"] = shape, [shape] = body }]);
        object Call(object draft, string method, params object[] args) => type.GetMethod(method)!.Invoke(draft, args);
        object Field(object draft, string name) => type.GetField(name)!.GetValue(draft);
        bool Valid(object draft) => (bool)type.GetProperty("IsValid")!.GetValue(draft);
        var rectangle = Read("rect2d", new Dictionary<string, object> { ["Center"] = XY(100, 100), ["Size"] = new Dictionary<string, object> { ["Width"] = 80d, ["Height"] = 40d }, ["Rotation"] = 30d });
        var corners = (IReadOnlyList<Point>)Call(rectangle, "Outline");
        Require(Math.Abs((corners[1] - corners[0]).Length - 80) < 1e-6 && Math.Abs((corners[2] - corners[1]).Length - 40) < 1e-6, "旋转矩形边长必须保持"); count++;
        var anchor = corners[2]; Call(rectangle, "Drag", 0, corners[0], corners[0] + new Vector(-10, -20), 1d);
        var changed = (IReadOnlyList<Point>)Call(rectangle, "Outline");
        Require((changed[2] - anchor).Length < 1e-6 && Valid(rectangle), "拖动矩形角点时对角锚点必须保持"); count++;
        var polygon = Read("polygon2d", new Dictionary<string, object> { ["Points"] = new object[] { XY(0, 0), XY(10, 0), XY(20, 0) } });
        Require(!Valid(polygon), "共线多边形必须拒绝提交"); count++;
        var ring = Read("ring2d", new Dictionary<string, object> { ["Center"] = XY(100, 100), ["Inner Radius"] = 20d, ["Outer Radius"] = 40d });
        Call(ring, "Drag", 1, new Point(120, 100), new Point(200, 100), 1d);
        Require((double)Field(ring, "InnerRadius") < (double)Field(ring, "Radius") && Valid(ring), "圆环内径不得越过外径"); count++;
        var sector = Read("sector2d", new Dictionary<string, object> { ["Center"] = XY(100, 100), ["Inner Radius"] = 0d, ["Outer Radius"] = 40d, ["Start Angle"] = 0d, ["Angle Range"] = 90d });
        Call(sector, "Draw", new Point(100, 100), new Point(140, 100));
        Require((double)Field(sector, "InnerRadius") == 0, "扇形应默认从圆心展开");
        Call(sector, "Drag", 3, new Point(100, 140), new Point(60, 100), 1d);
        Require(Math.Abs((double)Field(sector, "AngleRange") - 180) < 1e-6, "扇形角度应能通过拖动边界设置"); count++;
        var origin = Read("origin", new Dictionary<string, object> { ["X"] = 20d, ["Y"] = 30d, ["Angle"] = 45d });
        Call(origin, "Drag", 1, new Point(20, 30), new Point(40, 60), 1d);
        Require((Point)Field(origin, "Center") == new Point(40, 60), "手动原点必须按图像坐标移动"); count++;
        var transformType = typeof(ImageView).Assembly.GetType("AiVisionStudio.Views.FixtureRegionTransform")!;
        var geometry = new Dictionary<string, object> { ["Type"] = "rect2d", ["rect2d"] = new Dictionary<string, object> { ["Center"] = XY(50, 60), ["Size"] = new Dictionary<string, object> { ["Width"] = 80d, ["Height"] = 40d }, ["Rotation"] = 30d } };
        var fixture = new Dictionary<string, object>
        {
            ["Base Point"] = new Dictionary<string, object> { ["Point"] = XY(10, 20), ["Angle"] = 30d, ["ScaleX"] = 1d, ["ScaleY"] = 1d },
            ["Run Point"] = new Dictionary<string, object> { ["Point"] = XY(100, 150), ["Angle"] = 60d, ["ScaleX"] = 1.2, ["ScaleY"] = 1.2 }
        };
        var runtime = (Dictionary<string, object>)transformType.GetMethod("Apply")!.Invoke(null, [geometry, fixture, false]);
        var restored = (Dictionary<string, object>)transformType.GetMethod("Apply")!.Invoke(null, [runtime, fixture, true]);
        var restoredBody = (IReadOnlyDictionary<string, object>)restored["rect2d"];
        Require(Math.Abs(Convert.ToDouble(((IReadOnlyDictionary<string, object>)restoredBody["Center"])["X"]) - 50) < 1e-6 && Math.Abs(Convert.ToDouble(restoredBody["Rotation"]) - 30) < 1e-6, "定位后的手动画选必须逆变换保存，避免重复移动"); count++;
        var adapter = typeof(ImageView).Assembly.GetType("AiVisionStudio.Views.GraphicRegionValue")!;
        foreach (var anticlockwise in new[] { false, true })
        foreach (var large in new[] { false, true })
        {
            var arc = new Dictionary<string, object> { ["Start"] = XY(10, 0), ["End"] = XY(0, 10), ["Radius"] = 10d, ["Caliper Height"] = 4d, ["AntiClockWise"] = anticlockwise, ["Large Arc"] = large };
            var editorArc = adapter.GetMethod("ToEditor")!.Invoke(null, ["arc_caliper", arc]);
            var arcBack = (Dictionary<string, object>)adapter.GetMethod("FromEditor")!.Invoke(null, ["arc_caliper", arc, editorArc]);
            var arcBody = (IReadOnlyDictionary<string, object>)arcBack["arc_caliper"];
            Require(Math.Abs(Convert.ToDouble(((IReadOnlyDictionary<string, object>)arcBody["Start"])["X"]) - 10) < 1e-6 && Math.Abs(Convert.ToDouble(((IReadOnlyDictionary<string, object>)arcBody["End"])["Y"]) - 10) < 1e-6 && (bool)arcBody["AntiClockWise"] == anticlockwise && (bool)arcBody["Large Arc"] == large, "圆弧画选必须保留端点与方向");
        }
        count++;
        Console.WriteLine($"Region geometry cases passed: {count}"); return count;
    }
    private static Dictionary<string, object> XY(double x, double y) => new() { ["X"] = x, ["Y"] = y };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
