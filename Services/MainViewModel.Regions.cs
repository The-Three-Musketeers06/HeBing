using System.Globalization;
using AiVisionStudio.Models;
using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Services;

public partial class MainViewModel
{
    private static bool IsEditableRectangle(ParameterNode node) => RegionRoot(node) != null
        && (node.Name == "rect2d" || node.Parent is { IsArray: true, Name: "rect2d array" });

    private void AddRectangleCoordinates(ParameterNode rectangle, YamlMappingNode data)
    {
        var corners = new EditableRegion2D("rect2d", data).RectangleCorners;
        var labels = new[] { "左上角", "右上角", "右下角", "左下角" };
        for (int i = 0; i < corners.Length; i++)
        {
            string key = EditableRegion2D.CornerKeys[i];
            var group = new ParameterNode { Name = "$" + key, DisplayName = labels[i], OperationId = rectangle.OperationId,
                Path = [.. rectangle.Path, "$" + key], Kind = "coordinates2d", Parent = rectangle, IsGroup = true, IsExpanded = true };
            foreach (var axis in new[] { "X", "Y" })
            {
                var coordinate = new ParameterNode { Name = axis, DisplayName = axis, OperationId = rectangle.OperationId,
                    Path = [.. group.Path, axis], Kind = "float", Parent = group, RegionCornerKey = key, Commit = CommitParameterNode,
                    Specification = new ParamSpec { Name = axis, Type = "float", Minimum = -30000, Maximum = 30000 } };
                coordinate.SetValueWithoutCommit((axis == "X" ? corners[i].X : corners[i].Y).ToString("G17", CultureInfo.InvariantCulture));
                group.Children.Add(coordinate);
            }
            rectangle.Children.Add(group);
        }
    }

    private RegionEditTarget RectangleCoordinateTarget(ParameterNode node)
    {
        var rectangle = node.Parent.Parent;
        bool array = rectangle.Parent?.IsArray == true;
        return RegionTarget(rectangle, array ? rectangle.Parent.Name : rectangle.Name,
            array ? int.Parse(rectangle.Path[^1], CultureInfo.InvariantCulture) : null);
    }

    private void CommitRectangleCoordinate(ParameterNode node, string value, ParamItem item)
    {
        var target = RectangleCoordinateTarget(node);
        var rectangle = ReadEditableRegion(target);
        var point = rectangle.RectangleCorners[Array.IndexOf(EditableRegion2D.CornerKeys, node.RegionCornerKey)];
        double number = double.Parse(value, CultureInfo.InvariantCulture);
        rectangle.MoveHandle(node.RegionCornerKey, node.Name == "X" ? new System.Windows.Point(number, point.Y) : new System.Windows.Point(point.X, number), 1);
        if (!rectangle.IsValid) throw new ArgumentException("请输入有效的矩形坐标");
        var schema = target.Specification.Children.FirstOrDefault(child => child.Name == target.TypeKey);
        ValidateRegionNumbers(rectangle.Data, schema);
        var top = ProjectFile.Clone(ReadItem(item));
        SetAtPath(top, node.Parent.Parent.Path.Skip(1).ToArray(), rectangle.Data);
        item.Value = ToYaml(top);
    }

    private void SynchronizeRectangleCoordinate(ParameterNode node, YamlNode values)
    {
        var data = AtPath(values, node.Parent.Parent.Path) as YamlMappingNode;
        if (data == null) return;
        node.IsVisible = node.Parent.IsVisible;
        node.IsEnabled = CanEdit && node.Parent.IsEnabled;
        try { CheckRegionEdit(RectangleCoordinateTarget(node)); node.IsBound = false; }
        catch (InvalidOperationException error) { node.IsBound = true; node.BindingLabel = error.Message; }
        if (_nodeDrafts.TryGetValue((node.OperationId, NodePathKey(node)), out var draft))
        { node.SetValueWithoutCommit(draft.Value); node.Error = draft.Error; return; }
        var point = new EditableRegion2D("rect2d", data).RectangleCorners[Array.IndexOf(EditableRegion2D.CornerKeys, node.RegionCornerKey)];
        node.SetValueWithoutCommit((node.Name == "X" ? point.X : point.Y).ToString("G17", CultureInfo.InvariantCulture));
        node.Error = "";
    }

    public void ExpandRegionParameters(RegionEditTarget target)
    {
        string[] path = target.Index is { } index ? [.. target.RootPath, target.TypeKey, index.ToString(CultureInfo.InvariantCulture)] : [.. target.RootPath, target.TypeKey];
        foreach (var node in AllParameterNodes.Where(node => node.IsGroup && node.Path.Length <= path.Length && path.Take(node.Path.Length).SequenceEqual(node.Path)))
            node.IsExpanded = true;
    }
    public static ParameterNode RegionRoot(ParameterNode node)
    {
        for (var current = node; current != null; current = current.Parent)
            if (current.Specification?.GroupType is "region" or "mask"
                && current.Specification.Support.Any(type => EditableRegion2D.SupportedKinds.Contains(type.Replace(" array", "")))) return current;
        return null;
    }

    public static bool CanDrawRegion(ParameterNode node) => node?.IsGroup == true && RegionRoot(node) is { } root
        && (ReferenceEquals(node, root) || node.Parent?.IsArray == true && ReferenceEquals(node.Parent.Parent, root)
            || root.Children.Contains(node) && node.Name != "Type");

    public RegionEditTarget RegionTarget(ParameterNode node, string typeKey, int? index)
    {
        var root = RegionRoot(node) ?? throw new InvalidOperationException("该参数没有二维区域定义");
        if (SelectedOperation?.Uuid != root.OperationId || !root.Specification.Support.Contains(typeKey)
            || !EditableRegion2D.SupportedKinds.Contains(typeKey.Replace(" array", ""))) throw new InvalidOperationException("区域类型无效");
        return new RegionEditTarget(root.OperationId, root.Path.ToArray(), typeKey, index, root.Specification);
    }

    private YamlMappingNode RegionRootValue(RegionEditTarget target)
    {
        var values = GetChild(SelectedOperation?.ParametersRaw as YamlMappingNode, "value");
        return AtPath(values, target.RootPath) as YamlMappingNode ?? DefaultNode(target.Specification) as YamlMappingNode ?? new YamlMappingNode();
    }

    private void CheckRegionEdit(RegionEditTarget target)
    {
        if (!CanEdit || SelectedOperation?.Uuid != target.OperationId) throw new InvalidOperationException("当前控件不能编辑区域");
        var bindings = GetChild(SelectedOperation.ParametersRaw as YamlMappingNode, "bind");
        static bool IsBinding(YamlMappingNode map) => new[] { "_binded", "_global" }
            .Any(key => bool.TryParse(GetChild(map, key)?.ToString(), out var enabled) && enabled);
        static bool Bound(YamlNode node) => node switch
        {
            YamlMappingNode map => IsBinding(map) || map.Children.Values.Any(Bound),
            YamlSequenceNode array => array.Children.Any(Bound),
            _ => false
        };
        for (int i = 1; i <= target.RootPath.Length; i++)
        {
            var current = AtPath(bindings, target.RootPath.Take(i)) as YamlMappingNode;
            if (current != null && (i == target.RootPath.Length ? Bound(current)
                : IsBinding(current)))
                throw new InvalidOperationException("该区域包含绑定参数，请先解除绑定再手动绘制");
        }
    }

    public int RegionItemCount(RegionEditTarget target) => GetChild(RegionRootValue(target), target.TypeKey) is YamlSequenceNode array ? array.Children.Count : 0;
    public string CurrentRegionType(RegionEditTarget target) => GetChild(RegionRootValue(target), "Type")?.ToString();
    public int RegionInputIndex => Algorithm.ParamBag.FromYaml(SelectedOperation?.ParametersRaw as YamlNode, null, _engine.Context).Int("IM In", 0);

    public EditableRegion2D ReadEditableRegion(RegionEditTarget target)
    {
        CheckRegionEdit(target);
        var root = RegionRootValue(target);
        var value = GetChild(root, target.TypeKey);
        YamlMappingNode shape = value as YamlMappingNode;
        if (value is YamlSequenceNode array && target.Index is { } index)
            shape = array.Children.ElementAtOrDefault(index) as YamlMappingNode ?? array.Children.FirstOrDefault() as YamlMappingNode;
        if (shape == null)
        {
            var defaults = DefaultNode(target.Specification) as YamlMappingNode;
            var initial = GetChild(defaults, target.TypeKey);
            shape = initial as YamlMappingNode ?? (initial as YamlSequenceNode)?.Children.FirstOrDefault() as YamlMappingNode;
        }
        if (shape == null)
        {
            var schema = target.Specification.Children.FirstOrDefault(child => child.Name == target.TypeKey);
            shape = DefaultNode(new ParamSpec { Type = "group", Children = schema?.Children ?? [] }) as YamlMappingNode;
        }
        return new EditableRegion2D(target.Kind, shape ?? new YamlMappingNode());
    }

    private static void ValidateRegionNumbers(YamlNode value, ParamSpec schema)
    {
        if (value is YamlMappingNode map)
        {
            foreach (var pair in map.Children) ValidateRegionNumbers(pair.Value, schema?.Children.FirstOrDefault(child => child.Name == pair.Key.ToString()));
        }
        else if (value is YamlSequenceNode array)
        {
            foreach (var child in array.Children) ValidateRegionNumbers(child, new ParamSpec { Type = schema?.ArrayType ?? "group", Children = schema?.Children ?? [] });
        }
        else if (schema?.Type is "float" or "double" or "int" or "integer")
        {
            if (!double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) throw new ArgumentException("区域坐标必须是有效数字");
            if (schema.Minimum is { } min && number < min || schema.Maximum is { } max && number > max)
                throw new ArgumentException($"{schema.DisplayName ?? schema.Name} 超出允许范围");
        }
    }

    public void CommitRegion(RegionEditTarget target, EditableRegion2D region)
    {
        CheckRegionEdit(target);
        if (!region.IsValid || region.Kind != target.Kind) throw new ArgumentException("请绘制有效区域");
        if (target.TypeKey.EndsWith(" array", StringComparison.Ordinal) && target.Index == null) throw new ArgumentException("请选择区域数组中的一项");
        if (!ApplyParameters()) throw new InvalidOperationException("请先修正无效参数");
        var schema = target.Specification.Children.FirstOrDefault(child => child.Name == target.TypeKey);
        ValidateRegionNumbers(region.Data, schema);
        var parameters = ProjectFile.Clone(SelectedOperation.ParametersRaw as YamlNode) as YamlMappingNode ?? new YamlMappingNode();
        var values = GetChild(parameters, "value") as YamlMappingNode ?? new YamlMappingNode();
        if (GetChild(values, target.RootPath[0]) == null && Params.FirstOrDefault(item => item.Name == target.RootPath[0]) is { } parameter)
            values.Add(target.RootPath[0], ProjectFile.Clone(ReadItem(parameter)));
        var root = (YamlMappingNode)EditableRegion2D.Copy(RegionRootValue(target));
        root.Children[new YamlScalarNode("Type")] = new YamlScalarNode(target.TypeKey);
        if (target.Index is { } index)
        {
            var array = GetChild(root, target.TypeKey) as YamlSequenceNode ?? new YamlSequenceNode();
            if (index < 0 || index > array.Children.Count) throw new InvalidOperationException("区域数组已改变，请重新选择绘制项");
            if (index == array.Children.Count) array.Add(EditableRegion2D.Copy(region.Data));
            else array.Children[index] = EditableRegion2D.Copy(region.Data);
            root.Children[new YamlScalarNode(target.TypeKey)] = array;
        }
        else root.Children[new YamlScalarNode(target.TypeKey)] = EditableRegion2D.Copy(region.Data);
        SetAtPath(values, target.RootPath, root);
        parameters.Children[new YamlScalarNode("value")] = values;
        if (parameters.ToString() == SelectedOperation.ParametersRaw?.ToString()) return;
        if (CurrentRegionType(target) == target.TypeKey && (target.Index == null || target.Index < RegionItemCount(target))
            && Params.FirstOrDefault(item => item.Name == target.RootPath[0]) is { } existing)
        {
            existing.Value = ToYaml(GetChild(values, existing.Name));
            if (existing.HasError) throw new InvalidOperationException(existing.Error);
            Status = "区域已更新，点击测试执行";
            return;
        }
        BeforeEdit();
        SelectedOperation.ParametersRaw = parameters;
        var expanded = AllParameterNodes.ToDictionary(NodePathKey, node => node.IsExpanded);
        LoadParams();
        foreach (var node in AllParameterNodes)
            if (expanded.TryGetValue(NodePathKey(node), out var state)) node.IsExpanded = state;
        ExpandRegionParameters(target);
        AfterEdit();
        Status = "区域已更新，点击测试执行";
    }
}
