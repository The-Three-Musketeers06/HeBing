using System.Globalization;
using System.IO;
using System.Text.Json;
using AiVisionStudio.Services;
using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Algorithm.Impl;

public sealed class DelayOp : IOperator
{
    public string Driver => "Delay";
    public bool Run(OpContext ctx, ParamBag p)
    {
        var time = p.Dbl("Time", 1000);
        if (!double.IsFinite(time) || time < 0 || time > int.MaxValue || time != Math.Truncate(time))
            throw new ArgumentException("延时时间必须为非负整数毫秒");
        if (ctx.CancellationToken.WaitHandle.WaitOne((int)time)) ctx.CancellationToken.ThrowIfCancellationRequested();
        return true;
    }
}

public sealed class ValueCheckOp : IOperator
{
    public string Driver => "Value Check";
    public bool Run(OpContext ctx, ParamBag p)
    {
        var results = new List<bool>();
        var detail = new Dictionary<string,object>(StringComparer.Ordinal);
        foreach (var name in p.StringList("_input_list"))
        {
            if (!ControlValues.Bool(ControlValues.Read(p, name + ".Enable", true))) continue;
            double Number(string key)
            {
                var raw = ControlValues.Read(p, name + "." + key) ?? throw new ArgumentException($"数据检查 {name} 缺少 {key}");
                if (raw is bool || !double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n))
                    throw new ArgumentException($"数据检查 {name}.{key} 必须为有限数值");
                return n;
            }
            var value = Number("Value"); var low = Number("Lower Limit"); var high = Number("Upper Limit");
            if (low > high) throw new ArgumentException("数据检查下限不能大于上限");
            var mode = Convert.ToString(ControlValues.Read(p, name + ".Mode", "Inclusive"));
            var result = mode switch {
                "Inclusive" => value >= low && value <= high,
                "Exclusive" => value > low && value < high,
                "Inclusive Lower Limit" => value >= low && value < high,
                "Inclusive Upper Limit" => value > low && value <= high,
                _ => throw new ArgumentException("未知上下限模式：" + mode)
            };
            results.Add(result ^ ControlValues.Bool(ControlValues.Read(p, name + ".Invert", false)));
            detail[name]=results[^1];
        }
        if (results.Count == 0) throw new ArgumentException("数据检查没有启用的条件");
        var combined = p.Str("Relationship of Conditions", "And") switch {
            "And" => results.All(v => v), "Or" => results.Any(v => v),
            _ => throw new ArgumentException("未知条件关系")
        };
        ctx.Vars.Set("Result", combined ^ p.Bool("Invert"));
        ctx.Vars.Set("ResultAll", combined ^ p.Bool("Invert"));
        ctx.Vars.Set("ResultDetail", detail);
        ctx.Vars.Set("Conditions", results.Select(v => (object)v).ToList());
        // An out-of-tolerance part is a valid measurement with Result=false.
        return true;
    }
}

public static class RecipeRuntime
{
    public static List<Dictionary<string, object>> Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("配方文件路径为空");
        using var doc = JsonDocument.Parse(File.ReadAllText(path).TrimStart('\uFEFF'));
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("recipe", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("配方文件必须包含 recipe 数组");
        var rows = new List<Dictionary<string, object>>();
        HashSet<string> fields = null;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("配方的每一行必须为对象");
            var row = (Dictionary<string, object>)Value(item);
            if (fields == null) fields = row.Keys.ToHashSet(StringComparer.Ordinal);
            else if (!fields.SetEquals(row.Keys)) throw new InvalidDataException("配方每行的字段不一致");
            rows.Add(row);
        }
        return rows;
    }
    private static object Value(JsonElement item) => item.ValueKind switch {
        JsonValueKind.Object => item.EnumerateObject().ToDictionary(p => p.Name, p => Value(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => item.EnumerateArray().Select(Value).ToList(),
        JsonValueKind.Number => item.GetDouble(), JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.String => item.GetString(), JsonValueKind.Null => null,
        _ => throw new InvalidDataException("配方包含无效值")
    };
    public static void Configure(OpContext ctx, IEnumerable<ProgramModel> programs)
    {
        ctx.Recipes.Clear();
        var root = programs.FirstOrDefault()?.Root;
        if (root == null || !root.Children.TryGetValue(new YamlScalarNode("programs"), out var raw) || raw is not YamlMappingNode map
            || !map.Children.TryGetValue(new YamlScalarNode("recipe"), out var items) || items is not YamlSequenceNode list) return;
        foreach (var entry in list.Children.OfType<YamlMappingNode>())
        {
            string Field(string key) => entry.Children.TryGetValue(new YamlScalarNode(key), out var n) ? (n as YamlScalarNode)?.Value ?? "" : "";
            if (!ctx.Recipes.TryAdd(Field("name"), Field("path"))) throw new InvalidDataException("配方名称重复：" + Field("name"));
        }
    }
    public static void Switch(OpContext ctx, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !ctx.Recipes.TryGetValue(name, out var file)) throw new ArgumentException("配方不存在：" + name);
        var path = ctx.ResolvePath(file);
        _ = Read(path); // Validate before committing; failure preserves the previous active recipe.
        ctx.RecipePath = path;
        ctx.Log("当前运行配方：" + name, false);
    }
}

public sealed class ReadRecipeFileOp : IOperator
{
    public string Driver => "Read Recipe File";
    public bool Run(OpContext ctx, ParamBag p)
    {
        var rows = RecipeRuntime.Read(ctx.ResolvePath(p.Str("Recipe File")));
        ctx.Vars.Set("Recipe List", rows.Select(r => (object)r).ToList());
        return true;
    }
}

public sealed class SwitchRecipeOp : IOperator
{
    public string Driver => "SwitchRecipe";
    public bool Run(OpContext ctx, ParamBag p)
    {
        if (p.Bool("Active", true) ^ p.Bool("Reverse Active")) RecipeRuntime.Switch(ctx, p.Str("Recipe"));
        return true;
    }
}
