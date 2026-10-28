using System.Globalization;
using System.IO;
using System.Text.Json;
using AiVisionStudio.Algorithm.Lua;

namespace AiVisionStudio.Algorithm.Impl;

internal static class LuaDataFunctions
{
    public static void Install(OpContext ctx,LuaEnv env)
    {
        env.Set("ConvertTableToJson",new LuaFunc(a=>{
            if(a.Length!=1||a[0] is not LuaTable table)throw new ArgumentException("ConvertTableToJson 需要 table");
            return JsonSerializer.Serialize(Plain(table,new HashSet<LuaTable>(),0));
        }));
        env.Set("ConvertJsonToTable",new LuaFunc(a=>{
            if(a.Length!=1)throw new ArgumentException("ConvertJsonToTable 需要 JSON 字符串");
            using var doc=JsonDocument.Parse(LuaLite.Str(a[0]));return JsonValue(doc.RootElement);
        }));
        env.Set("WriteHmiData",new LuaFunc(a=>{
            if(a.Length!=3||a[1] is not LuaTable columns||a[2] is not LuaTable values)throw new ArgumentException("WriteHmiData 需要表名、列名数组和数据数组");
            var names=Array(columns).Select(v=>v as string??throw new ArgumentException("数据列名称必须为字符串")).ToArray();var data=Array(values);
            if(names.Length==0||names.Length!=data.Length||names.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=names.Length)throw new ArgumentException("写入 HMI 的列和值数量不一致、为空或列名重复");
            if(data.Any(v=>v is not(null or string or bool or double or float or int or long)))throw new ArgumentException("HMI 数据库单元格必须为标量");
            using var db=new ProjectDatabase(ctx.ResolveOutputPath("data.db"));var quoted=ProjectDatabase.Quote(LuaLite.Str(a[0]));
            db.Execute("BEGIN IMMEDIATE");
            try
            {
                var schema=db.Execute("PRAGMA table_info("+quoted+")");if(schema.Count==0)throw new InvalidOperationException("HMI 数据表不存在："+a[0]);
                foreach(var name in names)if(!schema.Any(c=>string.Equals(Convert.ToString(c["name"]),name,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("HMI 数据列不存在："+name);
                var fields=names.Select(ProjectDatabase.Quote).ToList();var parameters=data.ToList();
                if(schema.Any(c=>string.Equals(Convert.ToString(c["name"]),"InsertTime",StringComparison.OrdinalIgnoreCase))&&!names.Contains("InsertTime",StringComparer.OrdinalIgnoreCase))
                {fields.Add("\"InsertTime\"");parameters.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff",CultureInfo.InvariantCulture));}
                ctx.CancellationToken.ThrowIfCancellationRequested();
                db.Execute("INSERT INTO "+quoted+" ("+string.Join(",",fields)+") VALUES ("+string.Join(",",fields.Select(_=>"?"))+")",parameters.ToArray());db.Execute("COMMIT");
            }
            catch{db.Execute("ROLLBACK");throw;}
            return true;
        }));
    }
    private static object[] Array(LuaTable table)
    {
        if(table.Map.Count!=table.Length)throw new ArgumentException("需要从 1 开始且连续的 Lua 数组");
        return Enumerable.Range(1,table.Length).Select(i=>table.Get((double)i)).ToArray();
    }
    private static object Plain(object value,HashSet<LuaTable> path,int depth)
    {
        if(value is not LuaTable table)
            return value is null or bool or string or double ? value : throw new ArgumentException("JSON 不支持函数或该值类型");
        if(depth>64||!path.Add(table))throw new ArgumentException("JSON 不支持循环引用或超过 64 层的表");
        try
        {
            if(table.Map.Count>0&&table.Map.Keys.All(k=>k is double))return Array(table).Select(v=>Plain(v,path,depth+1)).ToArray();
            if(table.Map.Keys.Any(k=>k is not string))throw new ArgumentException("JSON 对象的键必须全为字符串");
            return table.Map.ToDictionary(k=>(string)k.Key,k=>Plain(k.Value,path,depth+1),StringComparer.Ordinal);
        }
        finally{path.Remove(table);}
    }
    private static object JsonValue(JsonElement value)=>value.ValueKind switch {
        JsonValueKind.Object=>LuaLite.From(value.EnumerateObject().ToDictionary(p=>p.Name,p=>JsonValue(p.Value))),
        JsonValueKind.Array=>LuaLite.From(value.EnumerateArray().Select(JsonValue).ToList()),
        JsonValueKind.String=>value.GetString(),JsonValueKind.Number=>value.GetDouble(),JsonValueKind.True=>true,JsonValueKind.False=>false,_=>null
    };
}
