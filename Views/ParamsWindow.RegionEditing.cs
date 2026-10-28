using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AiVisionStudio.Models;
using AiVisionStudio.Algorithm;

namespace AiVisionStudio.Views;

public partial class ParamsWindow
{
    public bool UsesFixtureRegionEditing => _vm.CreateSelectedParameterBag().Bool("Position Fix");
    private string[] _parameterRegionPreviewPath;
    private string _parameterRegionPreviewOperation, _parameterRegionPreviewType;
    private int _parameterRegionPreviewSlot;
    private bool _parameterRegionPreviewWrapped;

    public void BindRegionPosition(ParameterNode node)
    {
        if (!_vm.CanEdit || node?.OperationId != _vm.SelectedOperation?.Uuid) return;
        var choices = _vm.GetPositionMatchSources();
        if (choices.Count == 0) { TestStatus.Text = "请先在此工具之前添加形状模板匹配"; return; }
        var menu = new ContextMenu { PlacementTarget = Preview };
        foreach (var source in choices)
        {
            var option = new MenuItem { Header = "跟随：" + source.Name };
            option.Click += (_, _) => TestStatus.Text = _vm.BindRegionPositionToMatch(source.Uuid) ? "已绑定定位；当前区域作为示教位置，运行时跟随匹配目标" : _vm.Status;
            menu.Items.Add(option);
        }
        menu.IsOpen = true;
    }

    public void BeginParameterRegionEdit(ParameterNode node)
    {
        if (node == null || !_vm.CanEdit || !node.CanEditValue || node.OperationId != _vm.SelectedOperation?.Uuid) return;
        if (_shapeBusy) { TestStatus.Text = "模板正在训练，请等待或停止训练后再编辑"; return; }
        EndRegionEditing(true);
        for (var parent = node.Parent; parent != null; parent = parent.Parent)
            if (parent.IsBound) { TestStatus.Text = "请先解除区域绑定"; return; }
        var raw = _vm.ReadParameterValue(node.Path);
        var map = raw as IReadOnlyDictionary<string, object>;
        var typeNode = node.Children.FirstOrDefault(child => child.Name == "Type");
        var types = typeNode?.Options.Select(option => option.Value).Where(IsEditableRegionType).Distinct().ToArray() ?? [];
        if (types.Length > 0)
        {
            var menu = new ContextMenu();
            foreach (var type in types)
            {
                var option = new MenuItem { Header = RegionTypeName(type) };
                AddRegionChoices(option.Items, node, map, type, true);
                menu.Items.Add(option);
            }
            menu.PlacementTarget = Preview;
            menu.IsOpen = true;
            return;
        }
        var inferred = InferRegionType(node, map);
        if (inferred == null) { TestStatus.Text = "请选择具体的区域形状后再画选"; return; }
        if (node.IsArray)
        {
            var menu = new ContextMenu { PlacementTarget = Preview };
            AddRegionChoices(menu.Items, node, map, inferred, false);
            menu.IsOpen = true;
        }
        else EditRegionShape(node, map, inferred, false, null);
    }

    private void AddRegionChoices(ItemCollection items, ParameterNode node, IReadOnlyDictionary<string, object> map, string type, bool wrapped)
    {
        var array = type.EndsWith(" array", StringComparison.Ordinal);
        var value = wrapped ? map?.GetValueOrDefault(type) : _vm.ReadParameterValue(node.Path);
        if (!array)
        {
            var draw = new MenuItem { Header = "绘制 / 编辑" };
            draw.Click += (_, _) => EditRegionShape(node, map, type, wrapped, null);
            items.Add(draw);
            return;
        }
        var entries = (value as IEnumerable)?.Cast<object>().OfType<IReadOnlyDictionary<string, object>>().ToArray() ?? [];
        for (var index = 0; index < entries.Length; index++)
        {
            var captured = index;
            var edit = new MenuItem { Header = "编辑区域 " + (index + 1) };
            edit.Click += (_, _) => EditRegionShape(node, map, type, wrapped, captured);
            items.Add(edit);
            var remove = new MenuItem { Header = "删除区域 " + (index + 1) };
            remove.Click += (_, _) =>
            {
                var list = entries.Where((_, i) => i != captured).Cast<object>().ToList();
                CommitRegion(node, map, type, wrapped, list);
            };
            items.Add(remove);
        }
        var add = new MenuItem { Header = "新增区域" };
        add.Click += (_, _) => EditRegionShape(node, map, type, wrapped, entries.Length);
        items.Add(add);
    }

    private void EditRegionShape(ParameterNode node, IReadOnlyDictionary<string, object> original, string type, bool wrapped, int? index)
    {
        var operation = node.OperationId;
        var path = node.Path.ToArray();
        var array = type.EndsWith(" array", StringComparison.Ordinal);
        var shapeType = array ? type[..^6] : type;
        var current = wrapped ? original?.GetValueOrDefault(type) : _vm.ReadParameterValue(path);
        var list = array ? (current as IEnumerable)?.Cast<object>().ToList() ?? [] : null;
        var body = array ? index < list!.Count ? list[index!.Value] as IReadOnlyDictionary<string, object> : null : current as IReadOnlyDictionary<string, object>;
        if (body == null && wrapped) body = new Dictionary<string, object>();
        if (shapeType == "caliper") shapeType = "window2d";
        void Commit(Dictionary<string, object> changed)
        {
            if (_vm.SelectedOperation?.Uuid != operation || !_vm.CanEdit) { TestStatus.Text = "工具已切换，区域未保存"; return; }
            var updatedBody = changed.GetValueOrDefault(shapeType) ?? changed;
            if (array)
            {
                if (index < list!.Count) list[index!.Value] = updatedBody;
                else list!.Add(updatedBody);
                CommitRegion(node, original, type, wrapped, list);
            }
            else CommitRegion(node, original, type, wrapped, updatedBody);
        }
        var slot = _vm.CreateSelectedParameterBag().Int("IM In");
        if (_vm.Profiles.TryGetValue(slot, out var profile) && profile.Count > 0)
        {
            try { var dialog = SpatialRegionEditor.ForProfile(profile, shapeType, body, Commit); dialog.Owner = this; dialog.ShowDialog(); }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { TestStatus.Text = exception.Message; }
            return;
        }
        if (_vm.Clouds.Get(slot) is { Count: > 0 } cloud && (_vm.Is3DTool || !_vm.Images.Has(slot)))
        {
            try
            {
                var reference = _vm.ReadParameterValue("Reference") as IReadOnlyDictionary<string, object>;
                var dialog = SpatialRegionEditor.ForCloud(cloud, reference, shapeType, body, Commit); dialog.Owner = this; dialog.ShowDialog();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { TestStatus.Text = exception.Message; }
            return;
        }
        _previewIndex = slot; _previewIsCloud = false;
        RefreshPreview();
        if (!Preview.HasImage) { TestStatus.Text = "请先加载或运行上游步骤，显示输入图像后再画选"; return; }
        var editType = GraphicRegionValue.EditType(shapeType);
        if (editType == null) { TestStatus.Text = "此几何类型不支持区域画选"; return; }
        try
        {
            var parameters = _vm.CreateSelectedParameterBag();
            var fixture = parameters.Bool("Position Fix") ? parameters.Group("Points Information") : null;
            var initial = GraphicRegionValue.ToEditor(shapeType, body);
            if (fixture != null) initial = FixtureRegionTransform.Apply(initial, fixture, false);
            Preview.BeginRegionEdit(editType, initial, result =>
            {
                if (fixture != null) result = FixtureRegionTransform.Apply(result, fixture, true);
                Commit(GraphicRegionValue.FromEditor(shapeType, body, result));
            }, node.DisplayName);
        }
        catch (InvalidOperationException exception) { TestStatus.Text = exception.Message; return; }
        TestStatus.Text = "拖动画选，Enter 应用，Esc 取消；中键拖动画面";
    }

    private void CommitRegion(ParameterNode node, IReadOnlyDictionary<string, object> original, string type, bool wrapped, object value)
    {
        object replacement = value;
        if (wrapped)
        {
            var map = original == null ? new Dictionary<string, object>() : new Dictionary<string, object>(original);
            map["Type"] = type; map[type] = value; replacement = map;
        }
        if (!_vm.WriteParameterValue(replacement, node.Path)) { TestStatus.Text = _vm.Status; return; }
        _parameterRegionPreviewPath = node.Path.ToArray(); _parameterRegionPreviewOperation = node.OperationId;
        _parameterRegionPreviewType = type; _parameterRegionPreviewWrapped = wrapped;
        _parameterRegionPreviewSlot = _vm.CreateSelectedParameterBag().Int("IM In");
        RefreshPreview();
        TestStatus.Text = "区域已保存，可点击测试";
    }

    private void ApplyParameterRegionPreview()
    {
        if (IsShapeTool) { ApplyShapeRuntimeRegionPreview(); return; }
        if (_parameterRegionPreviewPath == null || _parameterRegionPreviewOperation != _vm.SelectedOperation?.Uuid
            || !Preview.HasImage || (_previewIndex ?? _vm.GetSelectedPreviewIndex()) != _parameterRegionPreviewSlot) return;
        var raw = _vm.ReadParameterValue(_parameterRegionPreviewPath);
        var type = _parameterRegionPreviewType;
        if (_parameterRegionPreviewWrapped && raw is IReadOnlyDictionary<string, object> map)
        { type = map.GetValueOrDefault("Type")?.ToString() ?? type; raw = map.GetValueOrDefault(type); }
        var array = type.EndsWith(" array", StringComparison.Ordinal);
        var shape = array ? type[..^6] : type;
        var values = array ? (raw as IEnumerable)?.Cast<object>().OfType<IReadOnlyDictionary<string, object>>().ToArray() ?? []
            : raw is IReadOnlyDictionary<string, object> body ? [body] : Array.Empty<IReadOnlyDictionary<string, object>>();
        var overlays = new List<NativeOverlay3D>(_showOverlays ? _vm.GetSelectedPreviewOverlays(_parameterRegionPreviewSlot) : []);
        foreach (var value in values)
        {
            var editable = GraphicRegionValue.ToEditor(shape, value);
            try
            {
                var parameters = _vm.CreateSelectedParameterBag();
                if (parameters.Bool("Position Fix")) editable = FixtureRegionTransform.Apply(editable, parameters.Group("Points Information"), false);
            }
            catch (InvalidOperationException) { continue; }
            var editType = editable["Type"].ToString();
            if (GraphicRegionValue.Group(editable, editType) is not { } geometry) continue;
            var data = new Dictionary<string, object>(geometry) { ["_color"] = "#00CFE8", ["_thickness"] = 2 };
            if (editType == "origin")
            {
                editType = "point2d"; data["_marker"] = "cross"; data["_size"] = 8;
                if (GraphicRegionValue.Group(geometry, "Point") is { } point) { data["X"] = point.GetValueOrDefault("X"); data["Y"] = point.GetValueOrDefault("Y"); }
            }
            overlays.Add(new NativeOverlay3D { Type = editType, Values = data, ViewIndex = _parameterRegionPreviewSlot });
        }
        Preview.SetNativeOverlays(overlays);
    }

    private static readonly HashSet<string> RegionTypes = new(StringComparer.Ordinal)
    { "rect2d", "circle2d", "ellipse2d", "polygon2d", "trace2d", "ring2d", "sector2d", "segment2d", "point2d", "window2d", "circlewindow2d", "window1d", "box", "orientedbox", "cylinderbox", "line_caliper", "circle_caliper", "arc_caliper", "origin", "fiducialpoint2d" };
    private static bool IsEditableRegionType(string type) => RegionTypes.Contains(type.EndsWith(" array", StringComparison.Ordinal) ? type[..^6] : type);
    private static string InferRegionType(ParameterNode node, IReadOnlyDictionary<string, object> raw)
    {
        if (IsEditableRegionType(node.Name)) return node.Name;
        if (node.Name == "Model Origin") return "origin";
        if (node.Specification?.GroupType == "fiducialpoint2d") return "fiducialpoint2d";
        if (node.Specification?.GroupType == "trace") return "trace2d";
        if (node.Specification?.GroupType == "caliper") return "caliper";
        if (node.Parent is { IsArray: true } parent && IsEditableRegionType(parent.Name)) return parent.Name.Replace(" array", "");
        if (raw?.ContainsKey("Start") == true && raw.ContainsKey("End"))
        {
            var start = raw["Start"] as IReadOnlyDictionary<string, object>;
            return start?.ContainsKey("Z") == true ? start.ContainsKey("Y") ? "box" : "window1d" : "window2d";
        }
        if (raw?.ContainsKey("Points") == true) return "polygon2d";
        return null;
    }
    private static string RegionTypeName(string type) => (type.Replace(" array", "") switch
    {
        "rect2d" => "矩形 / 旋转矩形", "circle2d" or "circlewindow2d" => "圆形", "ellipse2d" => "椭圆", "polygon2d" => "多边形", "ring2d" => "圆环", "sector2d" => "扇环", "segment2d" => "线段", "point2d" => "点", "window2d" => "矩形窗口", "window1d" => "轮廓窗口", "box" => "三维长方体", "orientedbox" => "有向包围盒", "cylinderbox" => "圆柱区域", "line_caliper" => "直线卡尺", "circle_caliper" => "圆卡尺", "arc_caliper" => "圆弧卡尺", _ => type
    }) + (type.EndsWith(" array", StringComparison.Ordinal) ? "（多区域）" : "");
}

internal static class GraphicRegionValue
{
    public static double Number(IReadOnlyDictionary<string, object> map, string key, double fallback = 0)
        => map != null && map.TryGetValue(key, out var raw) && double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : fallback;
    public static Dictionary<string, object> XY(double x, double y) => new() { ["X"] = x, ["Y"] = y };
    public static IReadOnlyDictionary<string, object> Group(IReadOnlyDictionary<string, object> map, string key) => map?.GetValueOrDefault(key) as IReadOnlyDictionary<string, object>;
    public static string EditType(string type) => type switch
    {
        "window2d" or "window1d" or "box" or "orientedbox" => "rect2d",
        "circlewindow2d" or "cylinderbox" or "circle_caliper" => "circle2d",
        "line_caliper" => "segment2d", "arc_caliper" => "sector2d",
        "fiducialpoint2d" => "origin",
        "rect2d" or "ellipse2d" or "circle2d" or "polygon2d" or "trace2d" or "ring2d" or "sector2d" or "segment2d" or "point2d" or "origin" => type,
        _ => null
    };
    public static Dictionary<string, object> Wrap(string type, object body) => new() { ["Type"] = type, [type] = body };
    public static Dictionary<string, object> ToEditor(string type, IReadOnlyDictionary<string, object> body)
    {
        body ??= new Dictionary<string, object>();
        var edit = EditType(type); object result = body;
        if (type is "window2d" or "window1d" or "box")
        {
            var a = Group(body, "Start"); var b = Group(body, "End"); var axis = type == "window1d" ? "Z" : "Y";
            result = new Dictionary<string, object> { ["Center"] = XY((Number(a, "X") + Number(b, "X")) / 2, (Number(a, axis) + Number(b, axis)) / 2), ["Size"] = new Dictionary<string, object> { ["Width"] = Math.Abs(Number(a, "X") - Number(b, "X")), ["Height"] = Math.Abs(Number(a, axis) - Number(b, axis)) }, ["Rotation"] = 0d };
        }
        else if (type == "orientedbox")
            result = new Dictionary<string, object> { ["Center"] = Group(body, "Center") ?? XY(0, 0), ["Size"] = new Dictionary<string, object> { ["Width"] = Number(Group(body, "Size"), "X"), ["Height"] = Number(Group(body, "Size"), "Y") }, ["Rotation"] = Number(Group(body, "Rotation"), "Z") };
        else if (type == "cylinderbox")
            result = new Dictionary<string, object> { ["Center"] = Group(body, "Bottom") ?? XY(0, 0), ["Radius"] = Number(body, "Radius") };
        else if (type == "arc_caliper")
        {
            var a = Group(body, "Start"); var b = Group(body, "End"); var dx = Number(b, "X") - Number(a, "X"); var dy = Number(b, "Y") - Number(a, "Y");
            var chord = Math.Sqrt(dx * dx + dy * dy); var radius = Number(body, "Radius");
            var cx = (Number(a, "X") + Number(b, "X")) / 2; var cy = (Number(a, "Y") + Number(b, "Y")) / 2;
            var start = 0d; var range = 180d;
            if (chord > 1e-8 && radius >= chord / 2)
            {
                var offset = Math.Sqrt(Math.Max(0, radius * radius - chord * chord / 4));
                var anticlockwise = body.GetValueOrDefault("AntiClockWise") is true; var large = body.GetValueOrDefault("Large Arc") is true;
                foreach (var direction in new[] { 1, -1 })
                {
                    var x = cx - direction * dy * offset / chord; var y = cy + direction * dx * offset / chord;
                    var angleA = Math.Atan2(Number(a, "Y") - y, Number(a, "X") - x) * 180 / Math.PI;
                    var angleB = Math.Atan2(Number(b, "Y") - y, Number(b, "X") - x) * 180 / Math.PI;
                    var sweep = (angleB - angleA + 720) % 360; if (anticlockwise) sweep -= 360;
                    if ((Math.Abs(sweep) > 180 + 1e-8) != large && offset > 1e-8) continue;
                    cx = x; cy = y; start = angleA; range = sweep; break;
                }
            }
            result = new Dictionary<string, object> { ["Center"] = XY(cx, cy), ["Inner Radius"] = Math.Max(0, radius - Number(body, "Caliper Height", 20) / 2), ["Outer Radius"] = radius <= 0 ? 0 : radius + Number(body, "Caliper Height", 20) / 2, ["Start Angle"] = start, ["Angle Range"] = range };
        }
        return Wrap(edit ?? type, result);
    }
    public static Dictionary<string, object> FromEditor(string type, IReadOnlyDictionary<string, object> original, IReadOnlyDictionary<string, object> edited)
    {
        var edit = EditType(type); var body = Group(edited, edit) ?? edited;
        var result = original == null ? new Dictionary<string, object>() : new Dictionary<string, object>(original);
        if (type is "window2d" or "window1d" or "box")
        {
            var center = Group(body, "Center"); var size = Group(body, "Size"); var axis = type == "window1d" ? "Z" : "Y";
            var radians = Number(body, "Rotation") * Math.PI / 180;
            var width = Math.Abs(Number(size, "Width") * Math.Cos(radians)) + Math.Abs(Number(size, "Height") * Math.Sin(radians));
            var height = Math.Abs(Number(size, "Width") * Math.Sin(radians)) + Math.Abs(Number(size, "Height") * Math.Cos(radians));
            var start = new Dictionary<string, object> { ["X"] = Number(center, "X") - width / 2, [axis] = Number(center, "Y") - height / 2 };
            var end = new Dictionary<string, object> { ["X"] = Number(center, "X") + width / 2, [axis] = Number(center, "Y") + height / 2 };
            if (type == "box") { start["Z"] = Number(Group(original, "Start"), "Z"); end["Z"] = Number(Group(original, "End"), "Z"); }
            result["Start"] = start; result["End"] = end;
        }
        else if (type is "origin" or "fiducialpoint2d")
        {
            var point = Group(body, "Point") ?? body;
            if (type == "origin") { result["Auto"] = false; result["X"] = Number(point, "X"); result["Y"] = Number(point, "Y"); }
            else result["Point"] = XY(Number(point, "X"), Number(point, "Y"));
            result["Angle"] = Number(body, "Angle");
        }
        else if (type == "orientedbox")
        {
            var center = Group(body, "Center"); var oldCenter = Group(original, "Center");
            result["Center"] = new Dictionary<string, object> { ["X"] = Number(center, "X"), ["Y"] = Number(center, "Y"), ["Z"] = Number(oldCenter, "Z") };
            result["Size"] = new Dictionary<string, object> { ["X"] = Number(Group(body, "Size"), "Width"), ["Y"] = Number(Group(body, "Size"), "Height"), ["Z"] = Math.Max(.001, Number(Group(original, "Size"), "Z", 1)) };
            result["Rotation"] = new Dictionary<string, object> { ["X"] = 0d, ["Y"] = 0d, ["Z"] = Number(body, "Rotation") };
        }
        else if (type == "cylinderbox") { result["Bottom"] = Group(body, "Center"); result["Radius"] = Number(body, "Radius"); }
        else if (type == "arc_caliper")
        {
            var center = Group(body, "Center"); var radius = (Number(body, "Inner Radius") + Number(body, "Outer Radius")) / 2;
            var startAngle = Number(body, "Start Angle"); var range = Number(body, "Angle Range", 180);
            Dictionary<string, object> At(double angle) => XY(Number(center, "X") + radius * Math.Cos(angle * Math.PI / 180), Number(center, "Y") + radius * Math.Sin(angle * Math.PI / 180));
            result["Start"] = At(startAngle); result["End"] = At(startAngle + range); result["Radius"] = radius; result["Caliper Height"] = Math.Max(3, Number(body, "Outer Radius") - Number(body, "Inner Radius")); result["AntiClockWise"] = range < 0; result["Large Arc"] = Math.Abs(range) > 180;
        }
        else foreach (var pair in body) result[pair.Key] = pair.Value;
        return Wrap(type, result);
    }
}
