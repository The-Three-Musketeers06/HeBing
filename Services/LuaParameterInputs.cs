using AiVisionStudio.Algorithm;
using AiVisionStudio.Models;
using System.Data.Common;
using System.Reflection;
using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Services;

public partial class MainViewModel
{
    private bool IsLuaInputName(ParameterNode node)
        => SelectedOperation?.Driver == "Lua" && node.Path.Length == 2 && node.Path[0] == "_input_names";

    
    private string[] BindingPath(ParameterNode node) => IsLuaInputName(node) ? [node.Value] : node.Path;

    private static YamlMappingNode PrepareLuaInputBindings(YamlMappingNode root, YamlNode edited)
    {
        if (edited is not YamlSequenceNode sequence) throw new FormatException("脚本输入变量必须是名称列表");
        var names = sequence.Children.OfType<YamlScalarNode>().Select(x => x.Value ?? "").ToArray();
        if (names.Length != sequence.Children.Count || names.Any(string.IsNullOrWhiteSpace)) throw new FormatException("输入变量名不能为空");
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new FormatException("输入变量名不能重复");
        var reserved = new HashSet<string>("and break do else elseif end false for function goto if in local nil not or repeat return then true until while".Split(' '), StringComparer.Ordinal);
        if (names.Any(name => name.Length > 32 || !System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-zA-Z_][a-zA-Z0-9_]*$") || reserved.Contains(name)))
            throw new FormatException("Lua 输入变量名须以字母或下划线开头，最多32字符，且不能使用保留字");
        var previous = GetChild(GetChild(root, "value") as YamlMappingNode, "_input_names") as YamlSequenceNode;
        var oldNames = previous?.Children.OfType<YamlScalarNode>().Select(x => x.Value ?? "").ToArray() ?? [];
        var originalBindings = GetChild(root, "bind") as YamlMappingNode;
        var bindings = ProjectFile.Clone(originalBindings) as YamlMappingNode ?? new YamlMappingNode();
        foreach (var old in oldNames.Except(names, StringComparer.Ordinal)) bindings.Children.Remove(new YamlScalarNode(old));
        for (var i = 0; i < names.Length; i++)
        {
            if (GetChild(bindings, names[i]) != null) continue;
            
           
            var oldBinding = names.Length == oldNames.Length ? GetChild(originalBindings, oldNames[i]) : null;
            bindings.Children[new YamlScalarNode(names[i])] = ProjectFile.Clone(oldBinding) ?? new YamlMappingNode { { "_binded", "false" } };
        }
        return bindings;
    }
}
