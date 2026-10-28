using System.IO;
using AiVisionStudio.Models;
using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Services;

public class ProgramModel : System.ComponentModel.INotifyPropertyChanged
{
    internal Guid HistoryId { get; set; } = Guid.NewGuid();
    private string _name=""; private bool _default;
    public string Name { get=>_name; set{_name=value;PropertyChanged?.Invoke(this,new(nameof(Name)));} }
    public bool IsDefault { get=>_default; set{_default=value;PropertyChanged?.Invoke(this,new(nameof(IsDefault)));} }
    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
    public YamlMappingNode Definition { get; set; }
    public System.Collections.ObjectModel.ObservableCollection<Operation> Operations { get; } = new();
    /// <summary>工程级全局变量（programs.variables）。</summary>
    public List<GlobalVarDef> Variables { get; } = new();
    /// <summary>当前选中的 recipe 文件（programs.recipe）。</summary>
    public string RecipePath { get; set; } = "";
    /// <summary>operations 节点（保留原始 YAML，保存时原地更新）。</summary>
    public YamlMappingNode Raw { get; set; }
    /// <summary>整个文档根节点。</summary>
    public YamlMappingNode Root { get; set; }
}

/// <summary>全局变量定义。</summary>
public class GlobalVarDef
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Default { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>读写 .aivs（纯 YAML：layout + programs.define.*.operations）。</summary>
public static class ProjectFile
{
    public static List<ProgramModel> Load(string path)
    {
        return Parse(File.ReadAllText(path));
    }

    public static List<ProgramModel> Create()
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
        return Parse($"created: {timestamp}\ncreated_version: v4.5.1\nmodified: {timestamp}\nmodified_version: v4.5.1\nlayout:\n  - {{IM: 0, zIndex: 9, width: 100, height: 100, left: 0, top: 0}}\nprograms:\n  default: 工程1\n  define:\n    工程1:\n      operations:\n        order: []\n        define: {{}}\n  variables: []\n  recipe: []\nproject_version: 2\n");
    }

    public static List<ProgramModel> Parse(string text)
    {
        var stream = CompatibleProjectYaml.Load(text);
        if (stream.Documents.Count == 0) return new List<ProgramModel>();

        var root = stream.Documents[0].RootNode as YamlMappingNode;
        var result = new List<ProgramModel>();
        if (root == null) return result;

        var programs = Get(root, "programs") as YamlMappingNode;
        var define = programs == null ? null : Get(programs, "define") as YamlMappingNode;
        if (define == null) return result;

        foreach (var kv in define.Children)
        {
            var pm = new ProgramModel { Name = kv.Key.ToString(), Root = root, IsDefault = Scalar(programs, "default") == kv.Key.ToString() };

            // 全局变量（programs.variables）
            if (Get(programs, "variables") is YamlSequenceNode vars)
            {
                foreach (var vn in vars.Children)
                {
                    if (vn is not YamlMappingNode vm) continue;
                    pm.Variables.Add(new GlobalVarDef
                    {
                        Name = Scalar(vm, "name"),
                        Type = GlobalValue.DefinitionType(vm),
                        Default = GlobalValue.DefinitionDefault(vm),
                        Description = Scalar(vm, "description")
                    });
                }
            }

            // recipe：取 selected=true 的第一个
            if (Get(programs, "recipe") is YamlSequenceNode recipes)
            {
                foreach (var rn in recipes.Children)
                    if (rn is YamlMappingNode rm)
                    {
                        var sel = Get(rm, "selected") as YamlScalarNode;
                        if (sel != null && sel.Value == "true")
                        {
                            pm.RecipePath = Scalar(rm, "path");
                            break;
                        }
                    }
            }

            var ops = kv.Value is YamlMappingNode m ? Get(m, "operations") as YamlMappingNode : null;
            pm.Definition=kv.Value as YamlMappingNode;
            pm.Raw = ops;
            if (ops != null)
            {
                var defineOps = Get(ops, "define") as YamlMappingNode;
                var order = Get(ops, "order") as YamlSequenceNode;
                var seen = new HashSet<string>();

                if (order != null)
                {
                    foreach (var idNode in order.Children)
                    {
                        var id = idNode.ToString();
                        seen.Add(id);
                        if (defineOps != null && Get(defineOps, id) is YamlMappingNode opNode)
                            pm.Operations.Add(ReadOp(opNode, id));
                    }
                }
                if (defineOps != null)
                {
                    foreach (var okv in defineOps.Children)
                    {
                        var id = okv.Key.ToString();
                        if (seen.Contains(id)) continue;
                        if (okv.Value is YamlMappingNode opNode)
                            pm.Operations.Add(ReadOp(opNode, id));
                    }
                }
            }
            for (int i = 0; i < pm.Operations.Count; i++) pm.Operations[i].Index = i;
            result.Add(pm);
        }
        return result;
    }

    private static Operation ReadOp(YamlMappingNode node, string uuid)
    {
        var op = new Operation
        {
            Uuid = uuid,
            Driver = Scalar(node, "driver"),
            Version = int.TryParse(Scalar(node, "version"), out var version) ? version : 1,
            Name = Scalar(node, "name"),
            Comment = Scalar(node, "comment"),
            Enabled = ScalarBool(node, "enabled", true),
            Breakpoint = ScalarBool(node, "breakpoint", false),
            ParametersRaw = Get(node, "parameters")
            ,OriginalRaw = node
        };
        if (string.IsNullOrWhiteSpace(op.Name)) op.Name = op.Driver;
        return op;
    }

    private static YamlNode Get(YamlMappingNode node, string key)
    {
        if (node == null) return null;
        foreach (var kv in node.Children)
            if (kv.Key.ToString() == key) return kv.Value;
        return null;
    }

    private static string Scalar(YamlMappingNode node, string key)
    {
        var v = Get(node, key);
        return v is YamlScalarNode s ? s.Value ?? "" : v?.ToString() ?? "";
    }

    private static bool ScalarBool(YamlMappingNode node, string key, bool def)
    {
        var s = Scalar(node, key).Trim().ToLowerInvariant();
        return s switch { "true" => true, "false" => false, _ => def };
    }

    // ---------------- 保存 ----------------

    /// <summary>把当前编辑结果写回 .aivs（保留未知字段，仅更新 order / name / enabled / breakpoint / parameters）。</summary>
    public static bool SaveAll(string path, IEnumerable<ProgramModel> programs)
    {
        var content = Serialize(programs);
        if (content == null) return false;
        var full = Path.GetFullPath(path);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new System.Text.UTF8Encoding(false));
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return true;
    }

    public static string Serialize(IEnumerable<ProgramModel> programs)
    {
        var list=programs.ToList();
        ProgramModel first = null;
        foreach (var p in list)
        {
            if (p?.Raw == null || p.Root == null) continue;
            first = p;
            Apply(p);
        }
        if (first == null) return null;
        var group=Get(first.Root,"programs") as YamlMappingNode;
        var definitions=new YamlMappingNode();
        foreach(var program in list)
        {
            program.Definition??=new YamlMappingNode();
            program.Definition.Children[new YamlScalarNode("operations")]=program.Raw;
            definitions.Add(program.Name,program.Definition);
        }
        group.Children[new YamlScalarNode("define")]=definitions;
        Set(group,"default",list.FirstOrDefault(p=>p.IsDefault)?.Name??first.Name);

        var sw = new StringWriter();
        CompatibleProjectYaml.Save(first.Root, sw);
        return sw.ToString();
    }

    public static YamlNode Clone(YamlNode node)
    {
        if (node == null) return null;
        using var writer = new StringWriter();
        CompatibleProjectYaml.Save(node, writer);
        var stream = new YamlStream();
        stream.Load(new StringReader(writer.ToString()));
        return stream.Documents[0].RootNode;
    }

    private static void Apply(ProgramModel pm)
    {
        var ops = pm.Raw;
        var order = Get(ops, "order") as YamlSequenceNode;
        if (order == null)
        {
            order = new YamlSequenceNode();
            ops.Children[new YamlScalarNode("order")] = order;
        }
        var define = Get(ops, "define") as YamlMappingNode;
        if (define == null)
        {
            define = new YamlMappingNode();
            ops.Children[new YamlScalarNode("define")] = define;
        }

        var alive = new HashSet<string>(pm.Operations.Select(o => o.Uuid));
        var dead = define.Children.Keys.Where(k => !alive.Contains(k.ToString())).ToList();
        foreach (var k in dead) define.Children.Remove(k);

        foreach (var op in pm.Operations)
        {
            var key = new YamlScalarNode(op.Uuid);
            if (!define.Children.TryGetValue(key, out var node) || node is not YamlMappingNode mn)
            {
                mn = Clone(op.OriginalRaw) as YamlMappingNode ?? new YamlMappingNode
                {
                    { "driver", op.Driver },
                    { "version", op.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    { "enabled", op.Enabled ? "true" : "false" },
                    { "breakpoint", op.Breakpoint ? "true" : "false" },
                    { "name", op.Name },
                    { "comment", op.Comment ?? "" }
                };
                var pars = (YamlNode)(op.ParametersRaw as YamlNode)
                           ?? new YamlMappingNode { { "value", new YamlMappingNode() } };
                mn.Children[new YamlScalarNode("parameters")] = pars;
                define.Children[key] = mn;
            }
            else
            {
                Set(mn, "name", op.Name);
                Set(mn, "enabled", op.Enabled ? "true" : "false");
                Set(mn, "breakpoint", op.Breakpoint ? "true" : "false");
                if (op.ParametersRaw is YamlNode pn) mn.Children[new YamlScalarNode("parameters")] = pn;
            }
            Set(mn, "driver", op.Driver);
            Set(mn, "name", op.Name);
            Set(mn, "comment", op.Comment ?? "");
            Set(mn, "enabled", op.Enabled ? "true" : "false");
            Set(mn, "breakpoint", op.Breakpoint ? "true" : "false");
            op.OriginalRaw = mn;
        }

        order.Children.Clear();
        foreach (var op in pm.Operations) order.Add(new YamlScalarNode(op.Uuid));
    }

    private static void Set(YamlMappingNode node, string key, string value)
    {
        node.Children[new YamlScalarNode(key)] = new YamlScalarNode(value);
    }
}
