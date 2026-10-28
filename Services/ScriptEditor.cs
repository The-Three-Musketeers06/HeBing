using AiVisionStudio.Models;
using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Services;

public partial class MainViewModel
{
    public ParameterNode ScriptParameter => AllParameterNodes.FirstOrDefault(node => node.Path.Length == 1 && node.Kind == "script");
    public ParameterNode ScriptInputNames => AllParameterNodes.FirstOrDefault(node => node.Path.Length == 1 && node.Name == "_input_names");
    public IEnumerable<ParameterNode> ScriptInputs => ScriptInputNames?.Children ?? Enumerable.Empty<ParameterNode>();
    public RelayCommand AddScriptInputCommand => ScriptInputNames?.AddItemCommand;
    private ParameterNode _selectedExpressionParameter;
    public ParameterNode SelectedExpressionParameter
    {
        get => _selectedExpressionParameter ?? AllParameterNodes.FirstOrDefault(node => node.Name == "Expression");
        set { _selectedExpressionParameter = value; OnPropertyChanged(); }
    }
    public object GetSelectedOutputObject(string name)
        => SelectedOperation != null && _engine.Context.OpOutputs.TryGetValue(SelectedOperation.Uuid, out var output) ? output.Get(name) : null;
    private string _scriptOutputText = "";
    private string _scriptOutputOperation;
    public string ScriptOutputText { get => _scriptOutputText; private set { _scriptOutputText = value; OnPropertyChanged(); } }

    private void RefreshScriptEditor()
    {
        if (_scriptOutputOperation != SelectedOperation?.Uuid) { _scriptOutputOperation = SelectedOperation?.Uuid; ScriptOutputText = ""; }
        _selectedExpressionParameter = null;
        OnPropertyChanged(nameof(SelectedExpressionParameter));
        OnPropertyChanged(nameof(ScriptParameter));
        OnPropertyChanged(nameof(ScriptInputNames));
        OnPropertyChanged(nameof(ScriptInputs));
        OnPropertyChanged(nameof(AddScriptInputCommand));
    }

    public bool RenameParameterListMember(ParameterNode list, string previous, string name, out string error)
    {
        error = "";
        if (!CanEdit || HasParameterErrors || SelectedOperation?.Uuid != list.OperationId) { error = "参数不可编辑"; return false; }
        name = name?.Trim() ?? "";
        if (name.Length is 0 or > 32) { error = "名称长度须为 1 至 32 个字符"; return false; }
        var root = ProjectFile.Clone(SelectedOperation.ParametersRaw as YamlNode) as YamlMappingNode;
        var values = GetChild(root, "value") as YamlMappingNode;
        if (values == null || GetChild(values, list.Name) is not YamlSequenceNode names) return false;
        if (name == previous) return true;
        if (GetChild(values, name) != null || names.Children.Any(node => node.ToString() == name)) { error = "名称已存在"; return false; }
        var index = names.Children.ToList().FindIndex(node => node.ToString() == previous);
        if (index < 0) return false;
        names.Children[index] = new YamlScalarNode(name);
        Rename(values);
        Rename(GetChild(root, "bind") as YamlMappingNode);
        BeforeEdit(); SelectedOperation.ParametersRaw = root; AfterEdit(); LoadParams();
        return true;
        void Rename(YamlMappingNode mapping)
        {
            if (mapping == null || !mapping.Children.Remove(new YamlScalarNode(previous), out var value)) return;
            mapping.Children[new YamlScalarNode(name)] = value;
        }
    }

    public void MoveParameterListMember(ParameterNode list, string name, int direction)
    {
        if (!CanEdit || HasParameterErrors || SelectedOperation?.Uuid != list.OperationId) return;
        var root = ProjectFile.Clone(SelectedOperation.ParametersRaw as YamlNode) as YamlMappingNode;
        if (GetChild(GetChild(root, "value") as YamlMappingNode, list.Name) is not YamlSequenceNode names) return;
        var index = names.Children.ToList().FindIndex(node => node.ToString() == name);
        var next = index + direction;
        if (index < 0 || next < 0 || next >= names.Children.Count) return;
        (names.Children[index], names.Children[next]) = (names.Children[next], names.Children[index]);
        BeforeEdit(); SelectedOperation.ParametersRaw = root; AfterEdit(); LoadParams();
    }
}
