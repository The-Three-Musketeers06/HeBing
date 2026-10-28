using System.Data;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiVisionStudio.Services;

public sealed class RecipeField
{
    public string Name { get; set; }="";
    public string Type { get; set; }="float";
    public string Default { get; set; }="0";
}

/// <summary>Editable compatible recipe JSON. Unknown metadata and untouched cell nodes survive saves.</summary>
public sealed class RecipeDocument
{
    private JsonObject _root;
    private string _hash;
    private readonly Dictionary<DataRow,JsonObject> _originals=new();
    public string Path { get; private set; }="";
    public DataTable Table { get; }=new(){Locale=CultureInfo.InvariantCulture};
    public List<RecipeField> Fields { get; }=new();
    public static RecipeDocument Load(string path)
    {
        var full=System.IO.Path.GetFullPath(path);var bytes=File.ReadAllBytes(full);
        var root=JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')) as JsonObject??throw new InvalidDataException("配方根节点必须为 JSON 对象");
        if(root["recipe"] is not JsonArray recipe||recipe.Any(r=>r is not JsonObject))throw new InvalidDataException("配方必须包含 recipe 对象数组");
        var document=new RecipeDocument{Path=full,_root=root,_hash=Convert.ToHexString(SHA256.HashData(bytes))};
        var names=recipe.OfType<JsonObject>().SelectMany(r=>r.Select(p=>p.Key)).Distinct(StringComparer.Ordinal).ToList();
        var schema=(root["_es_schema"] as JsonArray)?.OfType<JsonObject>().Where(n=>n["name"]!=null).GroupBy(n=>n["name"].ToString()).ToDictionary(g=>g.Key,g=>g.First());
        if(schema!=null)foreach(var name in schema.Keys)if(!names.Contains(name,StringComparer.Ordinal))names.Add(name);
        foreach(var name in names)
        {
            var cells=recipe.OfType<JsonObject>().Select(r=>r[name]).Where(n=>n!=null).ToArray();
            var type=cells.Length==0?"string":cells.All(n=>n.GetValueKind() is JsonValueKind.True or JsonValueKind.False)?"bool"
                :cells.All(n=>n.GetValueKind()==JsonValueKind.Number)?cells.All(n=>int.TryParse(n.ToJsonString(),NumberStyles.Integer,CultureInfo.InvariantCulture,out _))?"int":"float"
                :cells.All(n=>n.GetValueKind()==JsonValueKind.String)?"string":"json";
            var known=schema?.GetValueOrDefault(name);
            var declared=known?["type"]?.ToString();if(GlobalValue.Types.Contains(declared??"")||declared=="json")type=declared;
            document.Fields.Add(new(){Name=name,Type=type,Default=known?["default"]?.ToString()??(type=="string"?"":type=="bool"?"false":type=="json"?"null":"0")});
            document.Table.Columns.Add(name,typeof(string));
        }
        foreach(var source in recipe.OfType<JsonObject>())
        {
            var row=document.Table.NewRow();foreach(var name in names)row[name]=CellText(source[name]);document.Table.Rows.Add(row);document._originals[row]=(JsonObject)source.DeepClone();
        }
        document.Table.AcceptChanges();return document;
    }
    public static RecipeDocument Create()
    {
        var document=new RecipeDocument{_root=new JsonObject{["recipe"]=new JsonArray()}};
        document.AddField("name","string","默认检测");document.AddField("threshold","float","127");document.AddField("enabled","bool","true");document.AddRow();return document;
    }
    public void AddField(string name,string type,string defaultValue)
    {
        if(string.IsNullOrWhiteSpace(name)||Fields.Any(f=>f.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("字段名为空或重复");
        if(type=="json")JsonNode.Parse(defaultValue);else GlobalValue.Parse(type,defaultValue);
        Fields.Add(new(){Name=name,Type=type,Default=defaultValue});Table.Columns.Add(name,typeof(string));
        foreach(DataRow row in Table.Rows)if(row.RowState!=DataRowState.Deleted)row[name]=defaultValue;
    }
    public void RemoveField(string name)
    {var field=Fields.FirstOrDefault(f=>f.Name==name)??throw new ArgumentException("字段不存在");Fields.Remove(field);Table.Columns.Remove(name);}
    public DataRow AddRow(DataRow copy=null)
    {
        var row=Table.NewRow();foreach(var field in Fields)row[field.Name]=copy==null?field.Default:copy[field.Name];
        if(Table.Columns.Contains("key")&&Fields.First(f=>f.Name=="key").Type=="int")
        {
            var values=Table.Rows.Cast<DataRow>().Where(r=>r.RowState!=DataRowState.Deleted).Select(r=>int.TryParse(r["key"].ToString(),out var n)?n:-1);row["key"]=(values.DefaultIfEmpty(-1).Max()+1).ToString(CultureInfo.InvariantCulture);
        }
        Table.Rows.Add(row);return row;
    }
    public JsonObject Build()
    {
        var result=(JsonObject)_root.DeepClone();var records=new JsonArray();var index=0;
        foreach(DataRow row in Table.Rows)
        {
            if(row.RowState==DataRowState.Deleted)continue;index++;
            var original=_originals.GetValueOrDefault(row);var item=original==null?new JsonObject():(JsonObject)original.DeepClone();
            foreach(var key in item.Select(p=>p.Key).Where(k=>Fields.All(f=>f.Name!=k)).ToArray())item.Remove(key);
            foreach(var field in Fields)
            {
                var text=row[field.Name]?.ToString()??"";
                if(original!=null&&original.ContainsKey(field.Name)&&text==CellText(original[field.Name]))continue;
                try{item[field.Name]=ParseCell(field.Type,text);}
                catch(Exception e)when(e is ArgumentException or JsonException){throw new InvalidDataException($"第 {index} 行 / {field.Name}：{e.Message}",e);}
            }
            records.Add(item);
        }
        result["recipe"]=records;
        result["_es_schema"]=new JsonArray(Fields.Select(f=>(JsonNode)new JsonObject{["name"]=f.Name,["type"]=f.Type,["default"]=f.Default}).ToArray());
        return result;
    }
    public void Save(string path=null,bool checkExternalChange=true)
    {
        path=System.IO.Path.GetFullPath(path??Path);
        var document=Build();
        if(checkExternalChange&&!string.IsNullOrEmpty(Path)&&string.Equals(path,Path,StringComparison.OrdinalIgnoreCase)
            &&(!File.Exists(path)||Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))!=_hash))
            throw new IOException("配方已被外部修改或删除，请重新加载后再保存");
        Write(path,document);Path=path;_root=document;_hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        _originals.Clear();var records=(JsonArray)_root["recipe"];var i=0;foreach(DataRow row in Table.Rows)if(row.RowState!=DataRowState.Deleted)_originals[row]=(JsonObject)records[i++].DeepClone();Table.AcceptChanges();
    }
    public void Export(string path)
    {
        var full=System.IO.Path.GetFullPath(path);
        if(string.Equals(full,Path,StringComparison.OrdinalIgnoreCase))throw new IOException("导出副本请选择其他文件；写入当前配方请使用“保存配方文件”。");
        Write(full,Build());
    }
    private static void Write(string path,JsonObject root)
    {
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temporary,root.ToJsonString(new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));File.Move(temporary,path,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private static string CellText(JsonNode node)=>node==null?"":node.GetValueKind()==JsonValueKind.String?node.GetValue<string>():node.ToJsonString();
    private static JsonNode ParseCell(string type,string text)
    {
        if(type=="json")return JsonNode.Parse(text);
        var value=GlobalValue.Parse(type,text);
        return value switch{string s=>JsonValue.Create(s),bool b=>JsonValue.Create(b),int i=>JsonValue.Create(i),double d=>JsonValue.Create(d),List<object> a=>JsonSerializer.SerializeToNode(a),_=>throw new ArgumentException("字段类型无效")};
    }
}
