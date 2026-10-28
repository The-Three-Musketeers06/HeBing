using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using AiVisionStudio.Models;
using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Services;

public partial class MainViewModel
{
    public ObservableCollection<ParameterGroup> ParameterGroups { get; } = new();
    public ObservableCollection<VarRow> SelectedOutputs { get; } = new();
    public bool HasParameterErrors => _invalidParameters.Count > 0 || _nodeDrafts.Count > 0;
    private readonly Dictionary<(string Operation, string Path), (string Value, string Error)> _nodeDrafts = new();
    public string ToolTitle => SelectedOperation == null ? "工具参数" : DriverCatalog.Translate(SelectedOperation.Driver);
    public string ToolWindowKind => DriverCatalog.Get(SelectedOperation?.Driver)?.Window ?? "image";
    public bool Is3DTool => DriverCatalog.Get(SelectedOperation?.Driver)?.Is3D == true
        || (SelectedOperation?.Driver?.Contains("PointCloud", StringComparison.OrdinalIgnoreCase) ?? false);
    public string SelectedResult => SelectedOperation?.State switch
    {
        OpState.Ok => "OK", OpState.Fail => "NG", OpState.Running => "运行中", OpState.Disabled => "已禁用", _ => "未测试"
    };

    private void RebuildParameterTree()
    {
        ParameterGroups.Clear();
        OnPropertyChanged(nameof(ToolTitle));
        OnPropertyChanged(nameof(ToolWindowKind));
        OnPropertyChanged(nameof(Is3DTool));
        OnPropertyChanged(nameof(SelectedResult));
        if (SelectedOperation == null) return;
        var schema = DriverCatalog.Get(SelectedOperation.Driver);
        var listMembers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in schema?.Params.Where(p => p.Type == "list") ?? [])
            if (Params.FirstOrDefault(p => p.Name == list.Name) is {} item && ReadItem(item) is YamlSequenceNode sequence)
                foreach (var member in sequence.Children.OfType<YamlScalarNode>()) listMembers.Add(member.Value);
        foreach (var (field, title) in new[] { ("input", "输入参数"), ("basic", "计算参数"), ("render", "结果显示") })
        {
            var group = new ParameterGroup { Title = title, Field = field };
            foreach (var item in Params)
            {
                if (listMembers.Contains(item.Name) || VisualResultNames.IsOutputRoute(SelectedOperation.Driver, item.Name) || ContourTemplateSchema.IsHidden(SelectedOperation.Driver, [item.Name])) continue;
                var spec = schema?.Params.FirstOrDefault(p => p.Name == item.Name);
                var actualField = spec?.Field ?? (item.Name.StartsWith("IM ", StringComparison.Ordinal) ? "input" : "basic");
                if (actualField is not ("input" or "basic" or "advance" or "render")) actualField = "basic";
                if (actualField != field && !(field == "basic" && actualField == "advance")) continue;
                var destination = actualField == "advance" ? group.AdvancedItems : group.Items;
                destination.Add(BuildParameterNode(item.Name, [item.Name], ReadItem(item), spec, null));
            }
            if (group.Items.Count > 0 || group.AdvancedItems.Count > 0) ParameterGroups.Add(group);
        }
        SynchronizeParameterTree();
        RefreshScriptEditor();
    }

    private static YamlNode ReadItem(ParamItem item)
    {
        if (!item.IsComplex) return new YamlScalarNode(item.Value);
        try { var stream = new YamlStream(); stream.Load(new StringReader(item.Value)); return stream.Documents[0].RootNode; }
        catch { return new YamlMappingNode(); }
    }

    private ParameterNode BuildParameterNode(string name, string[] path, YamlNode value, ParamSpec spec, ParameterNode parent)
    {
        var kind = spec?.Type ?? (value is YamlMappingNode ? "group" : value is YamlSequenceNode ? "array" : GuessKind(name, (value as YamlScalarNode)?.Value ?? ""));
        if (kind == "select") kind = "string";
        var node = new ParameterNode
        {
            OperationId = SelectedOperation.Uuid, Name = name, Path = path, Parent = parent, Kind = kind,
            DisplayName = string.IsNullOrEmpty(spec?.DisplayName) ? DriverCatalog.Translate(name, SelectedOperation.Driver) : spec.DisplayName,
            Specification = spec, IsGroup = value is YamlMappingNode or YamlSequenceNode,
            IsArray = value is YamlSequenceNode, IsExpanded = path.Length == 1 && name is not ("Region" or "Shielding Area" or "Output settings" or "Sort Settings"),
            IsImageInput = kind == "im" && !UseNativeKernel && !Is3DTool && !name.Contains("Out", StringComparison.OrdinalIgnoreCase),
            GetImageInputChoices = () => GetImageInputChoices(SelectedOperation),
            SelectImageInput = SetImageInput,
            Commit = CommitParameterNode
        };
        node.SetValueWithoutCommit(value is YamlScalarNode scalar ? scalar.Value ?? "" : "");
        var translateOptions = !(spec?.Raw is {} optionMetadata && optionMetadata.TryGetProperty("translate", out var translate) && translate.ValueKind == JsonValueKind.False);
        foreach (var option in spec?.Items ?? []) node.Options.Add(new OptionItem { Value = option, Label = translateOptions ? DriverCatalog.Translate(option, SelectedOperation.Driver) : option });
        if (kind == "enum" && node.Options.Count == 0 && (SelectedOperation.Driver == "Call" && name == "Target" || SelectedOperation.Driver == "Parallel Call" && name == "Program"))
            foreach (var program in Programs) node.Options.Add(new OptionItem { Value = program.Name, Label = program.Name });
        node.IsFreeformEnum = kind == "enum" && node.Options.Count == 0;
        if(kind=="enum"&&name=="Recipe"&&SelectedOperation.Driver=="SwitchRecipe")
            foreach(var recipe in ReadRecipes())node.Options.Add(new OptionItem{Value=recipe.Name,Label=recipe.Name});
        if(kind=="enum"&&name=="Device")
            foreach(var device in ReadDevices().Where(d=>d.Enabled))node.Options.Add(new OptionItem{Value=device.Name,Label=device.Name+" · "+device.Protocol});
        if(kind=="enum"&&name=="Target"&&SelectedOperation.Driver is "Read I/O" or "Write I/O" or "Wait I/O")
            foreach(var device in ReadDevices().Where(d=>d.Enabled&&d.Category=="I/O"))node.Options.Add(new OptionItem{Value=device.Name,Label=device.Name+" · "+device.Protocol});
        if(kind=="enum"&&name=="Sensor"&&SelectedOperation.Driver=="Capture")
            foreach(var device in ReadDevices().Where(d=>d.Enabled&&d.Category=="相机"))node.Options.Add(new OptionItem{Value=device.Name,Label=device.Name+" · "+device.Protocol});
        node.IsFreeformEnum = kind == "enum" && node.Options.Count == 0;
        if (kind == "im" && !node.IsImageInput)
            for (var i = 0; i < 32; i++) node.Options.Add(new OptionItem { Value = i.ToString(), Label = i.ToString() });
        if (node.IsEnum && !node.IsImageInput && node.Options.All(o => o.Value != node.Value) && !string.IsNullOrEmpty(node.Value))
            node.Options.Add(new OptionItem { Value = node.Value, Label = translateOptions ? DriverCatalog.Translate(node.Value, SelectedOperation.Driver) : node.Value });
        if (value is YamlMappingNode mapping)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var child in spec?.Children ?? [])
            {
                var childValue = GetChild(mapping, child.Name) ?? DefaultNode(child);
                node.Children.Add(BuildParameterNode(child.Name, [..path, child.Name], childValue, child, node));
                seen.Add(child.Name);
            }
            foreach (var pair in mapping.Children)
                if (!ContourTemplateSchema.IsHidden(SelectedOperation.Driver, [..path, pair.Key.ToString()]) && seen.Add(pair.Key.ToString())) node.Children.Add(BuildParameterNode(pair.Key.ToString(), [..path, pair.Key.ToString()], pair.Value,
                    kind == "range" ? new ParamSpec { Name = pair.Key.ToString(), Type = "float" } : null, node));
        }
        else if (value is YamlSequenceNode sequence)
        {
            for (var i = 0; i < sequence.Children.Count; i++)
            {
                if (kind == "list" && sequence.Children[i] is YamlScalarNode member)
                {
                    var memberName = member.Value;
                    var memberItem = Params.FirstOrDefault(p => p.Name == memberName);
                    var memberDefault = spec?.Raw is {} raw && raw.TryGetProperty("value", out var template) ? template.Clone() : (JsonElement?)null;
                    var memberSpec = new ParamSpec { Name = memberName, Type = "group", Children = spec?.Children ?? [], Default = memberDefault };
                    var memberNode = BuildParameterNode(memberName, [memberName], memberItem == null ? DefaultNode(memberSpec) : ReadItem(memberItem), memberSpec, node);
                    memberNode.RemoveItemCommand = new RelayCommand(_ => EditNamedList(node, memberName), _ => node.CanEditValue);
                    node.Children.Add(memberNode);
                    continue;
                }
                var elementSpec = spec == null ? null : new ParamSpec { Name = $"[{i}]", Type = kind == "matrix" ? "float" : string.IsNullOrEmpty(spec.ArrayType) ? "group" : spec.ArrayType, Children = spec.Children };
                var child = BuildParameterNode($"[{i}]", [..path, i.ToString(CultureInfo.InvariantCulture)], sequence.Children[i], elementSpec, node);
                var index = i;
                if (kind != "matrix") child.RemoveItemCommand = new RelayCommand(_ => EditArray(node, index), _ => node.CanEditValue);
                node.Children.Add(child);
            }
            if (kind != "matrix") node.AddItemCommand = new RelayCommand(_ => { if (kind == "list") EditNamedList(node, null); else EditArray(node, null); }, _ => node.CanEditValue);
        }
        if (value is YamlMappingNode rectangle && IsEditableRectangle(node)) AddRectangleCoordinates(node, rectangle);
        return node;
    }

    private static YamlNode DefaultNode(ParamSpec spec)
    {
        if (spec.Default is {} json && json.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) return JsonToYaml(json);
        if (spec.Type is "array" or "list") return new YamlSequenceNode();
        if (spec.Children.Count > 0) return new YamlMappingNode(spec.Children.Select(child => new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode(child.Name), DefaultNode(child))));
        return new YamlScalarNode(spec.Type switch { "bool" => "false", "int" or "float" or "double" or "im" => "0", "enum" => spec.Items.FirstOrDefault() ?? "", _ => "" });
    }

    private static string NodePathKey(ParameterNode node) => JsonSerializer.Serialize(node.Path);

    private void CommitParameterNode(ParameterNode node, string value)
    {
        if ((!node.CanEditValue && !IsLuaInputName(node)) || !node.IsEnabled || SelectedOperation?.Uuid != node.OperationId) return;
        var draftKey = (node.OperationId, NodePathKey(node));
        try
        {
            if (node.IsNumeric || node.Kind == "im")
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) throw new FormatException("请输入有效数字");
                if (node.Kind is "int" or "integer" or "im" && (number != Math.Truncate(number) || number > int.MaxValue || number < int.MinValue)) throw new FormatException("请输入有效整数");
                if (node.Specification?.Minimum is {} min && number < min) throw new FormatException($"最小值为 {min}");
                if (node.Specification?.Maximum is {} max && number > max) throw new FormatException($"最大值为 {max}");
            }
            if (node.IsBoolean && !bool.TryParse(value, out _)) throw new FormatException("请输入 true 或 false");
            if (node.Kind == "color" && !System.Text.RegularExpressions.Regex.IsMatch(value, "^#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$"))
                throw new FormatException("请输入十六进制颜色，例如 #00FF00 或 #00FF0080");
            if (node.Parent?.Kind == "range" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var endpoint))
            {
                var other = node.Parent.Children.FirstOrDefault(x => x != node);
                if (other != null && double.TryParse(other.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var limit)
                    && (node.Name == "min" && endpoint > limit || node.Name == "max" && endpoint < limit))
                    throw new FormatException("最小值不能大于最大值");
            }
            var item = Params.First(p => p.Name == node.Path[0]);
            _nodeDrafts.Remove(draftKey);
            node.Error = "";
            if (node.RegionCornerKey != null) CommitRectangleCoordinate(node, value, item);
            else if (node.Path.Length == 1) item.Value = value;
            else
            {
                var top = ProjectFile.Clone(ReadItem(item));
                SetAtPath(top, node.Path.Skip(1).ToArray(), new YamlScalarNode(value));
                item.Value = ToYaml(top);
            }
            if (item.HasError) throw new FormatException(item.Error);
            SynchronizeParameterTree();
        }
        catch (Exception ex) when (ex is FormatException or YamlDotNet.Core.YamlException or InvalidOperationException or ArgumentException)
        {
            node.Error = ex.Message;
            _nodeDrafts[draftKey] = (value, ex.Message);
            IsDirty = true;
            Status = $"{node.DisplayName}：{ex.Message}";
        }
    }

    private static YamlNode AtPath(YamlNode root, IEnumerable<string> path)
    {
        var current = root;
        foreach (var key in path)
        {
            if (current is YamlMappingNode mapping) current = GetChild(mapping, key);
            else if (current is YamlSequenceNode sequence && int.TryParse(key, out var index) && index >= 0 && index < sequence.Children.Count) current = sequence.Children[index];
            else return null;
        }
        return current;
    }

    private static void SetAtPath(YamlNode root, IReadOnlyList<string> path, YamlNode value)
    {
        var current = root;
        for (var i = 0; i < path.Count - 1; i++)
        {
            if (current is YamlMappingNode map)
            {
                var child = GetChild(map, path[i]);
                if (child == null) { child = new YamlMappingNode(); map.Add(path[i], child); }
                current = child;
            }
            else if (current is YamlSequenceNode sequence && int.TryParse(path[i], out var index) && index >= 0 && index < sequence.Children.Count) current = sequence.Children[index];
            else throw new InvalidOperationException("参数路径不存在");
        }
        var last = path[^1];
        if (current is YamlMappingNode mapping) mapping.Children[new YamlScalarNode(last)] = value;
        else if (current is YamlSequenceNode array && int.TryParse(last, out var index) && index >= 0 && index < array.Children.Count) array.Children[index] = value;
        else throw new InvalidOperationException("参数路径不存在");
    }

    private void EditArray(ParameterNode node, int? removeIndex)
    {
        if (!node.CanEditValue || SelectedOperation?.Uuid != node.OperationId || HasParameterErrors) return;
        var item = Params.First(p => p.Name == node.Path[0]);
        var top = ProjectFile.Clone(ReadItem(item));
        var array = AtPath(top, node.Path.Skip(1)) as YamlSequenceNode;
        if (array == null)
        {
            if (node.Path.Length < 2) return;
            array = new YamlSequenceNode();
            SetAtPath(top, node.Path.Skip(1).ToArray(), array);
        }
        if (removeIndex is {} i) array.Children.RemoveAt(i);
        else if (SelectedOperation.Driver == "Lua" && node.Name == "_input_names")
        {
            var names = array.Children.Select(x => x.ToString()).ToHashSet(StringComparer.Ordinal);
            var index = 0;
            while (names.Contains("input" + index)) index++;
            array.Add("input" + index);
        }
        else if (array.Children.Count > 0) array.Add(ProjectFile.Clone(array.Children[^1]));
        else array.Add(DefaultNode(new ParamSpec { Type = node.Specification?.ArrayType ?? "group", Children = node.Specification?.Children ?? [] }));
        item.Value = ToYaml(top);
        RebuildParameterTree();
    }

    private void EditNamedList(ParameterNode node, string removeName)
    {
        if (!node.CanEditValue || SelectedOperation?.Uuid != node.OperationId || HasParameterErrors) return;
        var root = ProjectFile.Clone(SelectedOperation.ParametersRaw as YamlNode) as YamlMappingNode ?? new YamlMappingNode();
        var values = GetChild(root, "value") as YamlMappingNode ?? new YamlMappingNode();
        var bindings = GetChild(root, "bind") as YamlMappingNode ?? new YamlMappingNode();
        var list = GetChild(values, node.Name) as YamlSequenceNode ?? new YamlSequenceNode();
        if (removeName != null)
        {
            foreach (var member in list.Children.Where(x => x.ToString() == removeName).ToArray()) list.Children.Remove(member);
            values.Children.Remove(new YamlScalarNode(removeName));
            bindings.Children.Remove(new YamlScalarNode(removeName));
        }
        else
        {
            var raw = node.Specification?.Raw;
            var prefix = raw is {} metadata && metadata.TryGetProperty("prefix", out var prefixNode) ? prefixNode.GetString() : "item";
            var index = 0;
            while (GetChild(values, prefix + index) != null) index++;
            var name = prefix + index;
            var value = raw is {} schema && schema.TryGetProperty("value", out var template) ? JsonToYaml(template) : DefaultNode(new ParamSpec { Type = "group", Children = node.Specification?.Children ?? [] });
            if (value is YamlScalarNode { Value: null or "" } && node.Specification?.Children.Count > 0)
                value = DefaultNode(new ParamSpec { Type = "group", Children = node.Specification.Children });
            values.Add(name, value); list.Add(name);
            if (raw is {} data && data.TryGetProperty("bind", out var bindList) && bindList.ValueKind == JsonValueKind.Array)
            {
                var memberBindings = new YamlMappingNode();
                foreach (var bindName in bindList.EnumerateArray()) memberBindings.Add(bindName.GetString(), new YamlMappingNode { { "_binded", "false" } });
                bindings.Add(name, memberBindings);
            }
        }
        values.Children[new YamlScalarNode(node.Name)] = list;
        root.Children[new YamlScalarNode("value")] = values;
        root.Children[new YamlScalarNode("bind")] = bindings;
        BeforeEdit(); SelectedOperation.ParametersRaw = root; AfterEdit(); LoadParams();
    }

    public IEnumerable<ParameterNode> AllParameterNodes => ParameterGroups.SelectMany(g => Walk(g.Items.Concat(g.AdvancedItems)));
    private static IEnumerable<ParameterNode> Walk(IEnumerable<ParameterNode> nodes)
    {
        foreach (var node in nodes) { yield return node; foreach (var child in Walk(node.Children)) yield return child; }
    }

    private void SynchronizeParameterTree()
    {
        if (SelectedOperation == null) return;
        var values = new YamlMappingNode(Params.Select(p => new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode(p.Name), ReadItem(p))));
        var defaults = DriverCatalog.Get(SelectedOperation.Driver)?.ParameterBind is {} template ? JsonToYaml(template) : null;
        var imageChoices = AllParameterNodes.Any(node => node.IsImageInput) ? GetImageInputChoices(SelectedOperation) : [];
        foreach (var node in AllParameterNodes)
        {
            if (node.RegionCornerKey != null) { SynchronizeRectangleCoordinate(node, values); continue; }
            var data = AtPath(values, node.Path);
            if (_nodeDrafts.TryGetValue((node.OperationId, NodePathKey(node)), out var draft))
            {
                node.SetValueWithoutCommit(draft.Value); node.Error = draft.Error;
            }
            else if (!node.IsGroup && data is YamlScalarNode scalar)
            {
                node.SetValueWithoutCommit(scalar.Value);
                node.Error = node.Path.Length == 1 ? Params.FirstOrDefault(p => p.Name == node.Name)?.Error ?? "" : "";
            }
            node.IsVisible = (node.Parent?.IsVisible ?? true) && MatchDependencies(values, node.Specification?.Depends, node);
            var bindPath = BindingPath(node);
            var binding = AtPath(GetChild(SelectedOperation.ParametersRaw as YamlMappingNode, "bind"), bindPath) as YamlMappingNode;
            var declared = AtPath(defaults, bindPath) as YamlMappingNode;
            node.CanBind = !node.IsImageInput && (IsLuaInputName(node) || node.Kind == "bind" || GetChild(binding, "_binded") != null || GetChild(binding, "_global") != null || GetChild(declared, "_binded") != null);
            for (var ancestor = node.Parent; !node.CanBind && ancestor != null; ancestor = ancestor.Parent)
                if (ancestor.Kind == "list" && ancestor.Specification?.Raw is {} listMetadata && listMetadata.TryGetProperty("bind", out var bindNames)
                    && bindNames.ValueKind == JsonValueKind.Array && node.Path.Length == 2)
                    node.CanBind = bindNames.EnumerateArray().Any(x => x.GetString() == node.Path[1]);
            node.IsBound = ReadBoolean(binding, "_binded") || ReadBoolean(binding, "_global");
            node.IsEnabled = CanEdit && !(node.Parent?.IsBound ?? false) && (node.Parent?.IsEnabled ?? true);
            node.BindingLabel = node.IsBound ? DescribeBinding(binding) : "绑定参数";
            if (node.IsImageInput) node.ImageInputLabel = DescribeImageInput(node, imageChoices);
        }
    }

    private static bool ReadBoolean(YamlMappingNode node, string key) => bool.TryParse((GetChild(node, key) as YamlScalarNode)?.Value, out var flag) && flag;

    private static bool MatchDependencies(YamlNode root, JsonElement? rules, ParameterNode node)
    {
        if (rules is not {} element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return true;
        if (element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return true;
        return Matches(root, element);
        bool Matches(YamlNode actual, JsonElement expected)
        {
            if (expected.ValueKind == JsonValueKind.Object)
                return expected.EnumerateObject().All(p =>
                {
                    var child = GetChild(actual as YamlMappingNode, p.Name);
                    if (ReferenceEquals(root, actual) && child is YamlSequenceNode list && p.Value.ValueKind == JsonValueKind.Object
                        && list.Children.OfType<YamlScalarNode>().Any(x => x.Value == node.Path[0])) child = GetChild(actual as YamlMappingNode, node.Path[0]);
                    return Matches(child, p.Value);
                });
            if (expected.ValueKind == JsonValueKind.Array) return expected.EnumerateArray().Any(x => Matches(actual, x));
            var text = expected.ValueKind == JsonValueKind.String ? expected.GetString() : expected.GetRawText();
            return string.Equals((actual as YamlScalarNode)?.Value, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string DescribeBinding(YamlMappingNode binding)
    {
        if (ReadBoolean(binding, "_global")) return "全局变量 · " + GetChild(binding, "_variable");
        var target = GetChild(binding, "_target") as YamlMappingNode;
        var path = GetChild(target, "_variable") as YamlSequenceNode;
        return "算子输出 · " + string.Join(" / ", path?.Children.OfType<YamlMappingNode>().Select(x => GetChild(x, "_name")?.ToString()) ?? []);
    }

    public IReadOnlyList<ParameterBindingChoice> GetBindingChoices(ParameterNode node)
    {
        var choices = new List<ParameterBindingChoice> { new() { Label = "不绑定（使用参数值）", IsNone = true } };
        foreach (var variable in Programs.SelectMany(p => p.Variables).DistinctBy(v => v.Name))
            choices.Add(new() { Label = "全局变量 / " + variable.Name, Variable = variable.Name, IsGlobal = true });
        foreach (var operation in Operations.TakeWhile(o => o != SelectedOperation))
        {
            var names = new HashSet<string>(DriverCatalog.Get(operation.Driver)?.Outputs.Select(p => p.Name) ?? [], StringComparer.Ordinal);
            if (_engine.Context.OpOutputs.TryGetValue(operation.Uuid, out var values)) names.UnionWith(values.All.Keys);
            if (operation.Driver == ChannelSplitChannels.Driver)
            {
                string mode = values?.Get("Split Mode") as string ?? Algorithm.ParamBag.FromYaml(operation.ParametersRaw as YamlNode).Str("Split Mode", "RGB");
                var active = ChannelSplitChannels.Names(mode).Select(name => "IM Out " + name).ToHashSet(StringComparer.Ordinal);
                names.RemoveWhere(name => name.StartsWith("IM Out ", StringComparison.Ordinal) && !active.Contains(name));
                if (!string.Equals(mode, "HSV", StringComparison.OrdinalIgnoreCase)) names.Remove("Hue Range");
            }
            foreach (var name in names.Where(name => !IsTemplateOutputAlias(operation.Driver, name))) choices.Add(new() { Label = operation.Name + " / " + OutputLabels.Translate(name, operation.Driver), OperationId = operation.Uuid, Variable = name });
        }
        return choices;
    }

    public void SetParameterBinding(ParameterNode node, ParameterBindingChoice choice)
    {
        if (!CanEdit || node == null || choice == null || SelectedOperation?.Uuid != node.OperationId) return;
        var previousInput = VisualInputSignature(SelectedOperation);
        BeforeEdit();
        var root = SelectedOperation.ParametersRaw as YamlMappingNode ?? new YamlMappingNode();
        var bindings = GetChild(root, "bind") as YamlMappingNode ?? new YamlMappingNode();
        var bindPath = BindingPath(node);
        var entry = ProjectFile.Clone(AtPath(bindings, bindPath)) as YamlMappingNode ?? new YamlMappingNode();
        entry.Children[new YamlScalarNode("_binded")] = new YamlScalarNode(!choice.IsNone && !choice.IsGlobal ? "true" : "false");
        entry.Children[new YamlScalarNode("_global")] = new YamlScalarNode(choice.IsGlobal ? "true" : "false");
        if (choice.IsGlobal) entry.Children[new YamlScalarNode("_variable")] = new YamlScalarNode(choice.Variable);
        else if (!choice.IsNone) entry.Children[new YamlScalarNode("_target")] = new YamlMappingNode
        {
            { "_uuid", choice.OperationId },
            { "_variable", new YamlSequenceNode(new YamlMappingNode { { "_name", choice.Variable } }) }
        };
        SetAtPath(bindings, bindPath, entry);
        root.Children[new YamlScalarNode("bind")] = bindings;
        SelectedOperation.ParametersRaw = root;
        InvalidateChangedVisualInput(SelectedOperation, previousInput);
        AfterEdit(); SynchronizeParameterTree();
    }

    public int GetSelectedPreviewIndex()
    {
        var bag = Algorithm.ParamBag.FromYaml(SelectedOperation?.ParametersRaw as YamlNode);
        var input = bag.Int("IM In", 0);
        if (SelectedOperation?.Driver == "Basic Create ROI") return input;
        if (SelectedOperation?.Driver == ChannelSplitChannels.Driver
            && _engine.Context.OpOutputs.TryGetValue(SelectedOperation.Uuid, out var splitOutput)
            && splitOutput.Get("Split Mode") is string splitMode)
        {
            var key = "IM Out " + ChannelSplitChannels.Names(splitMode)[0];
            if (splitOutput.Get(key) is int index && Images.Has(index)) return index;
        }
        var output = bag.Int("IM Out", input);
        return SelectedOperation?.State == OpState.Ok && (Images.Has(output) || Clouds.Get(output) != null) ? output : input;
    }

    public void DiscardInvalidParameterEdits()
    {
        if (!CanEdit || SelectedOperation == null) return;
        foreach (var key in _nodeDrafts.Keys.Where(k => k.Operation == SelectedOperation.Uuid).ToArray()) _nodeDrafts.Remove(key);
        foreach (var key in _invalidParameters.Keys.Where(k => k.Uuid == SelectedOperation.Uuid).ToArray()) _invalidParameters.Remove(key);
        LoadParams();
        IsDirty = HasParameterErrors || ProjectFile.Serialize(Programs) != _savedDocument;
    }

    public async Task TestSelectedAsync()
    {
        if (IsRunning || SelectedOperation == null || !ApplyParameters()) return;
        var target = SelectedOperation;
        var operations = Operations.ToArray();
        if (!operations.Contains(target)) return;
        using var cancellation = new CancellationTokenSource();
        _interactiveCancellation = cancellation;
        IsRunning = true;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        var scriptPrint = new List<string>();
        void DispatchLog(string message, bool error)
        {
            if (target.Driver == "Lua" && target.State == OpState.Running && !message.StartsWith("Lua：执行完成", StringComparison.Ordinal)) scriptPrint.Add(message);
            if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.InvokeAsync(() => Log(message, error));
            else Log(message, error);
        }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _engine.InvalidateExecution();
            PrepareRun();
            Status = $"正在测试：{target.Name}";
            if (UseNativeKernel) await TestNativeAsync(target, DispatchLog, cancellation.Token);
            else await Task.Run(() => _engine.TestThrough(operations, target, DispatchLog, cancellation.Token));
            await OnUiAsync(() =>
            {
                RefreshGlobalVars();
                RefreshSelectedOutputs();
                if (target.Driver == "Lua") ScriptOutputText = string.Join(Environment.NewLine, scriptPrint);
                RunTimeText = $"测试时间:{timer.Elapsed.TotalMilliseconds:0.000}ms";
                Status = cancellation.IsCancellationRequested ? "测试已停止" : target.State switch
                {
                    OpState.Ok => $"{target.Name}：OK", OpState.Fail => $"{target.Name}：{target.ErrorMessage}",
                    _ => $"{target.Name}未执行（检查前置步骤、分支与启用状态）"
                };
                ImageChanged?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception ex) { await OnUiAsync(() => { Status = "测试失败：" + ex.Message; Log(Status, true); }); }
        finally { await OnUiAsync(() => { _interactiveCancellation = null; IsRunning = false; }); }
    }

    private void RefreshSelectedOutputs()
    {
        SelectedOutputs.Clear();
        RefreshImageInputLabels();
        if (UseNativeKernel && SelectedOperation != null && _nativeRawOutputs.TryGetValue(SelectedOperation.Uuid, out var raw)
            && raw.TryGetProperty("outputs", out var fields))
        {
            foreach (var field in fields.EnumerateArray())
            {
                var row = VarRow.FromNative(field, outputDriver: SelectedOperation.Driver);
                if (ShowSelectedOutput(row.Name)) SelectedOutputs.Add(row);
            }
            OnPropertyChanged(nameof(SelectedResult));
            return;
        }
        if (SelectedOperation != null && _engine.Context.OpOutputs.TryGetValue(SelectedOperation.Uuid, out var output))
            foreach (var pair in output.All.Where(pair => ShowSelectedOutput(pair.Key)))
                SelectedOutputs.Add(VarRow.FromOwned(pair.Key,pair.Value,SelectedOperation.Driver));
        OnPropertyChanged(nameof(SelectedResult));
    }

    private static string FormatOutput(object value)
    {
        if (value is ICollection collection) return $"{collection.Count} 项";
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        return text.Length > 300 ? text[..300] + "…" : text;
    }
}
