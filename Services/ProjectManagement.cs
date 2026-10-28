using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiVisionStudio.Algorithm;
using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Services;

public sealed class GlobalVariableEdit
{
    public string OriginalName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "float";
    public string Default { get; set; } = "0";
    public string Current { get; set; } = "";
    public string Description { get; set; } = "";
    public bool RuntimeOnly { get; set; }
}

public sealed class RecipeEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Selected { get; set; }
    public string State => Selected ? "使用中" : "待用";
}

public static class GlobalValue
{
    public static readonly string[] Types = { "int", "float", "double", "bool", "string", "int[]", "float[]", "double[]", "bool[]", "string[]" };
    public static object Parse(string type, string text)
    {
        text ??= "";
        if (type?.EndsWith("[]", StringComparison.Ordinal) == true)
        {
            if (!Types.Contains(type)) throw new ArgumentException("不支持的数组类型：" + type);
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind != JsonValueKind.Array) throw new ArgumentException("数组请使用 JSON 格式，例如 [1,2,3]");
                return document.RootElement.EnumerateArray().Select(item => {
                    var elementType=type[..^2];
                    var kind=elementType=="string"?JsonValueKind.String:elementType=="bool"?JsonValueKind.True:JsonValueKind.Number;
                    if (item.ValueKind!=kind && !(elementType=="bool"&&item.ValueKind==JsonValueKind.False)) throw new ArgumentException("数组成员类型与 "+type+" 不符");
                    return Parse(elementType,item.ValueKind==JsonValueKind.String?item.GetString():item.GetRawText());
                }).ToList();
            }
            catch(JsonException ex){throw new ArgumentException("数组 JSON 格式错误："+ex.Message,ex);}
        }
        return type?.ToLowerInvariant() switch
        {
            "int" => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : throw new ArgumentException("请输入 32 位整数"),
            "float" or "double" => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : throw new ArgumentException("请输入有限数值"),
            "bool" => text is "1" or "0" ? text == "1" : bool.TryParse(text, out var b) ? b : throw new ArgumentException("请输入 true / false 或 1 / 0"),
            "string" => text,
            _ => throw new ArgumentException("不支持的变量类型：" + type)
        };
    }
    public static string Text(object value) => value is System.Collections.IList ? JsonSerializer.Serialize(value) : value is bool b ? b ? "true" : "false" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    public static string InferType(object value)
    {
        if(value is System.Collections.IList list)
        {
            var types=list.Cast<object>().Select(InferType).Distinct().ToArray();
            if(types.Length==1 && types[0]!=null && !types[0].EndsWith("[]"))return types[0]+"[]";
            if(types.Length>0 && types.All(t=>t is "int" or "float"))return "float[]";
            return null;
        }
        return value switch {bool=>"bool",int or short or byte=>"int",float or double=>"float",string=>"string",_=>null};
    }

    public static string DefinitionType(YamlMappingNode node)
    {
        string Field(string key)=>node.Children.TryGetValue(new YamlScalarNode(key),out var v)?(v as YamlScalarNode)?.Value??"":"";
        return Field("type")=="array" ? Field("array_type")+"[]" : Field("type");
    }
    public static string DefinitionDefault(YamlMappingNode node)
    {
        var type=DefinitionType(node);
        if(!node.Children.TryGetValue(new YamlScalarNode("default"),out var value))return type.EndsWith("[]")?"[]":"";
        if(value is YamlScalarNode scalar)return scalar.Value??(type.EndsWith("[]")?"[]":"");
        if(value is YamlSequenceNode list&&type.EndsWith("[]"))return Text(list.Children.Select(n=>Parse(type[..^2],(n as YamlScalarNode)?.Value??throw new ArgumentException("数组默认值成员无效"))).ToList());
        throw new ArgumentException("全局变量默认值格式无效");
    }
}

public partial class MainViewModel
{
    public event EventHandler ProjectManagementChanged;
    public string ActiveRecipePath => _engine.Context.RecipePath;
    public void RefreshRecipeData(){RequireManagementEdit();NotifyManagement();}
    public IReadOnlyList<GlobalVariableEdit> ReadGlobalDefinitions()
    {
        var definitions = SelectedProgram?.Variables ?? new();
        var result = definitions.Select(v => new GlobalVariableEdit
        {
            OriginalName=v.Name,Name=v.Name,Type=v.Type,Default=v.Default,Description=v.Description,
            Current=CanEdit ? GlobalValue.Text(_engine.Context.Globals.Get(v.Name)) : GlobalVars.FirstOrDefault(r=>r.Name==v.Name)?.Value??""
        }).ToList();
        // The display collection is published on the UI thread after execution.
        foreach(var row in GlobalVars.Where(r=>!result.Any(v=>v.Name.Equals(r.Name,StringComparison.Ordinal))))
        {
            var type=GlobalValue.InferType(row.OwnedValue)??row.Type;
            var value=row.OwnedValue==null?row.Value:GlobalValue.Text(row.OwnedValue);
            if(GlobalValue.Types.Contains(type))result.Add(new(){Name=row.Name,OriginalName=row.Name,Type=type,Default=value,Current=value,RuntimeOnly=true});
        }
        return result;
    }
    public bool TryApplyGlobalDefinitions(IEnumerable<GlobalVariableEdit> edits,out string error)
    {
        error="";
        try
        {
            RequireManagementEdit();
            var rows=edits.ToList();var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var row in rows)
            {
                if(!Regex.IsMatch(row.Name??"",@"^[\p{L}_][\p{L}\p{Nd}_]*$"))throw new ArgumentException("变量名须以字母、中文或下划线开头，仅包含文字、数字和下划线");
                if(!names.Add(row.Name))throw new ArgumentException("变量名称重复："+row.Name);
                GlobalValue.Parse(row.Type,row.Default);
            }
            var previous=SelectedProgram?.Variables.ToList()??new();
            var renamed=rows.Where(r=>!string.IsNullOrEmpty(r.OriginalName)&&r.OriginalName!=r.Name).ToDictionary(r=>r.OriginalName,r=>r.Name,StringComparer.Ordinal);
            var removed=previous.Where(v=>!rows.Any(r=>r.OriginalName.Equals(v.Name,StringComparison.Ordinal))).Select(v=>v.Name).ToArray();
            foreach(var name in removed)
            {
                var uses=FindGlobalReferences(name);
                if(uses.Count>0)throw new ArgumentException($"变量 {name} 仍被引用：{string.Join("、",uses.Take(4))}；请先解除引用");
            }
            foreach(var name in renamed.Keys)
                if(FindGlobalReferences(name,scriptOnly:true).Count>0)throw new ArgumentException($"变量 {name} 被脚本引用，请先更新脚本中的变量名称");
            if(!ApplyParameters())throw new InvalidOperationException("请先修正算子中的无效参数");
            var programs=ManagementPrograms();
            var old=HmiYamlGet(programs,"variables") as YamlSequenceNode;
            var sequence=new YamlSequenceNode();var runtime=new VarPool(true);
            // Retain undeclared Lua variables; edited rows promote them to saved definitions.
            foreach(var pair in _engine.Context.Globals.All)
                if(!previous.Any(v=>v.Name.Equals(pair.Key,StringComparison.Ordinal))&&!rows.Any(r=>r.OriginalName.Equals(pair.Key,StringComparison.Ordinal)))runtime.Set(pair.Key,pair.Value);
            foreach(var row in rows)
            {
                var source=old?.Children.OfType<YamlMappingNode>().FirstOrDefault(n=>(HmiYamlGet(n,"name") as YamlScalarNode)?.Value==row.OriginalName);
                var node=ProjectFile.Clone(source) as YamlMappingNode??new YamlMappingNode{{"creator","Custom"}};
                Put(node,"name",row.Name);Put(node,"type",row.Type.EndsWith("[]")?"array":row.Type);
                if(row.Type.EndsWith("[]"))Put(node,"array_type",row.Type[..^2]);else node.Children.Remove(new YamlScalarNode("array_type"));
                var parsedDefault=GlobalValue.Parse(row.Type,row.Default);
                node.Children[new YamlScalarNode("default")]=parsedDefault is List<object> array?new YamlSequenceNode(array.Select(v=>(YamlNode)new YamlScalarNode(GlobalValue.Text(v)))):new YamlScalarNode(GlobalValue.Text(parsedDefault));
                Put(node,"description",row.Description??"");sequence.Add(node);
                var definition=previous.FirstOrDefault(v=>v.Name.Equals(row.OriginalName,StringComparison.Ordinal));
                runtime.Set(row.Name,definition!=null&&definition.Type==row.Type ? _engine.Context.Globals.Get(row.OriginalName)??GlobalValue.Parse(row.Type,row.Default) : GlobalValue.Parse(row.Type,row.Default));
            }
            BeforeEdit();programs.Children[new YamlScalarNode("variables")]=sequence;
            foreach(var program in Programs)
            {
                program.Variables.Clear();program.Variables.AddRange(rows.Select(r=>new GlobalVarDef{Name=r.Name,Type=r.Type,Default=GlobalValue.Text(GlobalValue.Parse(r.Type,r.Default)),Description=r.Description??""}));
                foreach(var op in program.Operations)RenameGlobalBindings(op.ParametersRaw as YamlNode,renamed);
            }
            _engine.Context.Globals=runtime;AfterEdit();LoadParams();RefreshGlobalVars();NotifyManagement();Status="变量定义已应用；保存工程后持久化";return true;
        }
        catch(Exception ex)when(ex is ArgumentException or InvalidOperationException or IOException){error=ex.Message;return false;}
    }
    public bool TryWriteGlobalValue(string name,string value,out string error)
    {
        var result=SetHmiVariable(name,value,out error);
        if(result){NotifyManagement();Status="已写入运行值："+name;}
        return result;
    }
    public IReadOnlyList<string> FindGlobalReferences(string name,bool scriptOnly=false)
    {
        var hits=new List<string>();
        foreach(var program in Programs)foreach(var op in program.Operations)
        {
            var hasBinding=!scriptOnly&&HasGlobalBinding(op.ParametersRaw as YamlNode,name);
            var script=string.Join("\n",YamlStrings(op.ParametersRaw as YamlNode));
            // Conservative: never rewrite source code literals without a parser-backed rename.
            var hasScript=op.Driver is "Lua" or "Expression" && Regex.IsMatch(script,@"(?:Get|Set)(?:Int|Float|Bool|String)(?:Array)?Variable\s*\(\s*[""']"+Regex.Escape(name)+@"[""']");
            if(hasBinding||hasScript)hits.Add(program.Name+" / "+op.Name);
        }
        return hits;
    }
    private static bool HasGlobalBinding(YamlNode node,string name)
    {
        if(node is YamlMappingNode map)
        {
            if((HmiYamlGet(map,"_global") as YamlScalarNode)?.Value=="true"&&(HmiYamlGet(map,"_variable") as YamlScalarNode)?.Value==name)return true;
            return map.Children.Values.Any(n=>HasGlobalBinding(n,name));
        }
        return node is YamlSequenceNode seq&&seq.Children.Any(n=>HasGlobalBinding(n,name));
    }
    private static IEnumerable<string> YamlStrings(YamlNode node)
    {
        if(node is YamlScalarNode scalar)yield return scalar.Value??"";
        else if(node is YamlMappingNode map)foreach(var child in map.Children.Values)foreach(var value in YamlStrings(child))yield return value;
        else if(node is YamlSequenceNode seq)foreach(var item in seq.Children)foreach(var value in YamlStrings(item))yield return value;
    }
    private static void RenameGlobalBindings(YamlNode node,Dictionary<string,string> names)
    {
        if(node is YamlMappingNode map)
        {
            if((HmiYamlGet(map,"_global") as YamlScalarNode)?.Value=="true"&&HmiYamlGet(map,"_variable") is YamlScalarNode value&&names.TryGetValue(value.Value??"",out var name))value.Value=name;
            foreach(var child in map.Children.Values)RenameGlobalBindings(child,names);
        }
        else if(node is YamlSequenceNode seq)foreach(var child in seq.Children)RenameGlobalBindings(child,names);
    }
    public IReadOnlyList<RecipeEntry> ReadRecipes()
    {
        if(HmiYamlGet(ManagementPrograms(),"recipe") is not YamlSequenceNode list)return Array.Empty<RecipeEntry>();
        return list.Children.OfType<YamlMappingNode>().Select(n=>new RecipeEntry{Name=(HmiYamlGet(n,"name") as YamlScalarNode)?.Value??"",Path=(HmiYamlGet(n,"path") as YamlScalarNode)?.Value??"",Selected=(HmiYamlGet(n,"selected") as YamlScalarNode)?.Value=="true"}).ToArray();
    }
    public string ResolveRecipePath(string path)
    {
        if(string.IsNullOrWhiteSpace(path))throw new ArgumentException("配方路径为空");
        return Path.GetFullPath(Path.IsPathRooted(path)?path:Path.Combine(ProjectDirectory,path));
    }
    public string ProjectDirectory=>string.IsNullOrEmpty(ProjectPath)?AppContext.BaseDirectory:Path.GetDirectoryName(Path.GetFullPath(ProjectPath));
    public void ApplyRecipeEntries(IEnumerable<RecipeEntry> entries)
    {
        RequireManagementEdit();var rows=entries.ToArray();
        if(rows.Count(r=>r.Selected)>1)throw new ArgumentException("只能启用一个配方");
        if(rows.Select(r=>ResolveRecipePath(r.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=rows.Length)throw new ArgumentException("配方文件路径重复");
        if(rows.Any(r=>string.IsNullOrWhiteSpace(r.Name)))throw new ArgumentException("配方名称不能为空");
        if(rows.Select(r=>r.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=rows.Length)throw new ArgumentException("配方名称重复");
        var selected=rows.SingleOrDefault(r=>r.Selected);if(selected!=null)RecipeDocument.Load(ResolveRecipePath(selected.Path));
        var programs=ManagementPrograms();var previous=HmiYamlGet(programs,"recipe") as YamlSequenceNode;var list=new YamlSequenceNode();
        foreach(var row in rows)
        {
            var old=previous?.Children.OfType<YamlMappingNode>().FirstOrDefault(n=>(HmiYamlGet(n,"path") as YamlScalarNode)?.Value==row.Path);
            var node=ProjectFile.Clone(old) as YamlMappingNode??new YamlMappingNode();Put(node,"name",row.Name.Trim());Put(node,"path",row.Path);Put(node,"selected",row.Selected?"true":"false");list.Add(node);
        }
        if(!ApplyParameters())throw new InvalidOperationException("请先修正算子中的无效参数");
        BeforeEdit();programs.Children[new YamlScalarNode("recipe")]=list;
        foreach(var program in Programs)program.RecipePath=selected?.Path??"";
        RefreshRecipePath();AfterEdit();NotifyManagement();Status=selected==null?"已更新配方列表":"当前配方："+selected.Name;
    }
    public string StoreRecipePath(string path)
    {
        var full=Path.GetFullPath(path);var relative=Path.GetRelativePath(ProjectDirectory,full);
        return relative.StartsWith(".."+Path.DirectorySeparatorChar)||Path.IsPathRooted(relative)?full:"./"+relative.Replace('\\','/');
    }
    private YamlMappingNode ManagementPrograms()=>HmiYamlGet(SelectedProgram?.Root??Programs.FirstOrDefault()?.Root,"programs") as YamlMappingNode??throw new InvalidOperationException("没有打开有效工程");
    private void RequireManagementEdit(){if(!CanEdit)throw new InvalidOperationException("请先停止运行，再修改工程数据");}
    private static void Put(YamlMappingNode node,string key,string value)=>node.Children[new YamlScalarNode(key)]=new YamlScalarNode(value);
    private void NotifyManagement(){OnPropertyChanged(nameof(ActiveRecipePath));ProjectManagementChanged?.Invoke(this,EventArgs.Empty);ImageChanged?.Invoke(this,EventArgs.Empty);}
    private void RestoreManagementAfterHistory(IReadOnlyList<GlobalVarDef> before)
    {
        var after=SelectedProgram?.Variables??new();
        if(!before.Select(v=>(v.Name,v.Type,v.Default)).SequenceEqual(after.Select(v=>(v.Name,v.Type,v.Default))))
        {
            var runtime=new VarPool(true);
            foreach(var pair in _engine.Context.Globals.All)if(!before.Any(v=>v.Name.Equals(pair.Key,StringComparison.Ordinal)))runtime.Set(pair.Key,pair.Value);
            foreach(var row in after)
            {
                var previous=before.FirstOrDefault(v=>v.Name==row.Name&&v.Type==row.Type);
                runtime.Set(row.Name,previous!=null?_engine.Context.Globals.Get(row.Name)??ParseDefault(row.Type,row.Default):ParseDefault(row.Type,row.Default));
            }
            _engine.Context.Globals=runtime;RefreshGlobalVars();
        }
        RefreshRecipePath();NotifyManagement();
    }
}
