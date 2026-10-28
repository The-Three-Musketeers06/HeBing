using System.Collections;
using System.Globalization;
using System.IO;
using System.Text.Json;
using AiVisionStudio.Algorithm;
using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Services;

public partial class MainViewModel
{
    /// <summary>Includes schema defaults for older projects, plus live binding resolution.</summary>
    public ParamBag CreateSelectedParameterBag()
    {
        if (SelectedOperation == null) throw new InvalidOperationException("请先选择工具");
        if (!ApplyParameters()) throw new InvalidOperationException(Status);
        var root = ProjectFile.Clone(SelectedOperation.ParametersRaw as YamlNode) as YamlMappingNode ?? new();
        root.Children[new YamlScalarNode("value")] = SelectedParameterValues();
        return ParamBag.FromYaml(root, null, _engine.Context);
    }

    public object ReadParameterValue(params string[] path)
    {
        if (SelectedOperation == null || path == null || path.Length == 0) return null;
        if (Algorithm.Impl.ContourTemplateStorage.IsInternal(path[0]))
        {
            var stored = ParamBag.FromYaml(SelectedOperation.ParametersRaw as YamlNode);
            return stored.Raw(path[0]);
        }
        var item = Params.FirstOrDefault(p => p.Name == path[0]);
        if (item == null) return null;
        var bag = ParamBag.FromYaml(new YamlMappingNode { { path[0], ReadItem(item) } });
        object value = bag.Raw(path[0]);
        foreach (var segment in path.Skip(1))
        {
            if (value is IReadOnlyDictionary<string, object> map) value = map.GetValueOrDefault(segment);
            else if (value is IList list && int.TryParse(segment, out var index) && index >= 0 && index < list.Count) value = list[index];
            else return null;
        }
        return value;
    }

    /// <summary>Commits a complete graphical edit as one undoable transaction.</summary>
    public bool WriteParameterValue(object value, params string[] path)
    {
        if (!CanEdit || SelectedOperation == null || path == null || path.Length == 0 || !ApplyParameters()) return false;
        var target = AllParameterNodes.FirstOrDefault(node => node.Path.SequenceEqual(path));
        if (target != null && (!target.CanEditValue || AncestorBound(target)) || AllParameterNodes.Any(node => node.IsBound && node.Path.Length >= path.Length && node.Path.Take(path.Length).SequenceEqual(path)))
        {
            Status = "此参数已绑定，请先解除绑定后再编辑区域";
            return false;
        }
        try
        {
            var replacement = JsonToYaml(JsonSerializer.SerializeToElement(value));
            var root = ProjectFile.Clone(SelectedOperation.ParametersRaw as YamlNode) as YamlMappingNode ?? new();
            var values = SelectedParameterValues();
            SetAtPath(values, path, replacement);
            InvalidateContourTeaching(values, path[0]);
            root.Children[new YamlScalarNode("value")] = values;
            var before = ToYaml(SelectedOperation.ParametersRaw as YamlNode ?? new YamlMappingNode());
            if (before == ToYaml(root)) return true;
            var inputSignature = VisualInputSignature(SelectedOperation);
            BeforeEdit();
            SelectedOperation.ParametersRaw = root;
            InvalidateChangedVisualInput(SelectedOperation, inputSignature);
            AfterEdit();
            LoadParams();
            Status = "参数已更新：" + string.Join(" / ", path);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        {
            Status = "区域参数未保存：" + exception.Message;
            return false;
        }
        static bool AncestorBound(Models.ParameterNode node)
        {
            for (var parent = node.Parent; parent != null; parent = parent.Parent)
                if (parent.IsBound) return true;
            return false;
        }
    }

    public string ResolveProjectFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var directory = string.IsNullOrEmpty(ProjectPath) ? AppContext.BaseDirectory : Path.GetDirectoryName(Path.GetFullPath(ProjectPath));
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(directory!, path));
    }

    public IReadOnlyDictionary<string, object> GetSelectedOutputValues()
        => SelectedOperation != null && _engine.Context.OpOutputs.TryGetValue(SelectedOperation.Uuid, out var values)
            ? new Dictionary<string, object>(values.All, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>();

    public IReadOnlyList<Models.Operation> GetPositionMatchSources()
        => Operations.TakeWhile(operation => operation != SelectedOperation).Where(operation => Algorithm.Impl.ContourTemplateEngine.IsMatcher(operation.Driver)).ToArray();

    public bool BindRegionPositionToMatch(string sourceId)
    {
        if (!CanEdit || SelectedOperation == null || !ApplyParameters()) return false;
        if (!GetPositionMatchSources().Any(operation => operation.Uuid == sourceId)) return false;
        if (!Params.Any(item => item.Name == "Points Information") || !Params.Any(item => item.Name == "Position Fix")) return false;
        var root = ProjectFile.Clone(SelectedOperation.ParametersRaw as YamlNode) as YamlMappingNode ?? new();
        var values = SelectedParameterValues();
        var bindings = GetChild(root, "bind") as YamlMappingNode ?? new();
        var position = GetChild(bindings, "Points Information") as YamlMappingNode ?? new();
        foreach (var name in new[] { "Base Point", "Run Point" })
            position.Children[new YamlScalarNode(name)] = new YamlMappingNode
            {
                { "_binded", "true" }, { "_global", "false" },
                { "_target", new YamlMappingNode { { "_uuid", sourceId }, { "_variable", new YamlSequenceNode(new YamlMappingNode { { "_name", name } }) } } }
            };
        position.Children[new YamlScalarNode("_binded")] = new YamlScalarNode("false");
        position.Children[new YamlScalarNode("_global")] = new YamlScalarNode("false");
        bindings.Children[new YamlScalarNode("Points Information")] = position;
        values.Children[new YamlScalarNode("Position Fix")] = new YamlScalarNode("true");
        root.Children[new YamlScalarNode("value")] = values; root.Children[new YamlScalarNode("bind")] = bindings;
        BeforeEdit(); SelectedOperation.ParametersRaw = root; AfterEdit(); LoadParams();
        Status = "检测区域已绑定模板匹配定位";
        return true;
    }
}
