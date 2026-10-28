using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AiVisionStudio.Algorithm.Lua;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>分组容器（Group / End Group）：本身不执行，只用于界面缩进与计时。</summary>
public class GroupOp : IOperator
{
    private readonly string _driver;
    public GroupOp(string driver) => _driver = driver;
    public string Driver => _driver;
    public bool Run(OpContext ctx, ParamBag p) => throw new InvalidOperationException($"{Driver} 必须由运行引擎处理控制流");
}

/// <summary>Resolves condition inputs without silently replacing a broken binding with its default.</summary>
public static class ControlValues
{
    public static object Read(ParamBag p, string key, object fallback = null)
    {
        if (p.Bindings.ContainsKey(key) || p.Globals.ContainsKey(key))
            return p.Bound(key) ?? throw new InvalidOperationException($"绑定输入不存在：{key}");
        var parts = key.Split('.', 2);
        if (parts.Length == 2 && p.Group(parts[0]) is { } group && group.TryGetValue(parts[1], out var value)) return value;
        return p.Raw(key) ?? fallback;
    }

    public static bool Bool(object value) => value switch
    {
        bool b => b,
        string s when bool.TryParse(s, out var b) => b,
        null => false,
        _ => Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0
    };

    public static bool Evaluate(ParamBag p)
    {
        var values = new List<bool>();
        foreach (var name in p.StringList("_input_list"))
        {
            if (!Bool(Read(p, name + ".Enable", true))) continue;
            var type = Convert.ToString(Read(p, name + ".Type", "Bool"));
            var left = Read(p, name + "." + type + " Value");
            var right = Read(p, name + "." + type + " Compare Value");
            var comparison = Convert.ToString(Read(p, name + "." + (type is "Int" or "Float" ? "Number" : type) + " Compare", "Equal"));
            int order = type switch
            {
                "Bool" => Bool(left).CompareTo(Bool(right)),
                "Int" or "Float" => Convert.ToDouble(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(right, CultureInfo.InvariantCulture)),
                "String" => string.CompareOrdinal(Convert.ToString(left), Convert.ToString(right)),
                _ => throw new InvalidOperationException($"不支持的条件类型：{type}")
            };
            bool result = comparison switch
            {
                "Equal" or "Equal To" => order == 0,
                "Not Equal" or "Not Equal To" => order != 0,
                "Less Than" => order < 0,
                "Less Than Or Equal" or "Less Than or Equal" or "Less Than Or Equal To" => order <= 0,
                "Greater Than" => order > 0,
                "Greater Than Or Equal" or "Greater Than or Equal" or "Greater Than Or Equal To" => order >= 0,
                "Contains" => Convert.ToString(left).Contains(Convert.ToString(right), StringComparison.Ordinal),
                _ => throw new InvalidOperationException($"不支持的比较方式：{comparison}")
            };
            values.Add(result ^ Bool(Read(p, name + ".Invert", false)));
        }
        if (values.Count == 0) throw new InvalidOperationException("条件分支没有启用的条件");
        var relation = p.Str("Relationship of Conditions", "And");
        var combined = relation.Equals("And", StringComparison.OrdinalIgnoreCase) ? values.All(v => v)
            : relation.Equals("Or", StringComparison.OrdinalIgnoreCase) ? values.Any(v => v)
            : throw new InvalidOperationException($"不支持的条件关系：{relation}");
        return combined ^ p.Bool("Invert");
    }
}

public sealed class JudgeOp : IOperator
{
    public string Driver => "Judge";
    public bool Run(OpContext ctx, ParamBag p)
    {
        bool result, warning = false;
        switch (p.Str("Type", "Number"))
        {
            case "Bool": result = ControlValues.Bool(ControlValues.Read(p, "Target Bool")) == p.Bool("Expect Bool"); break;
            case "String": result = Convert.ToString(ControlValues.Read(p, "Target String")) == p.Str("Expect String"); break;
            case "Number":
                var target = Convert.ToDouble(ControlValues.Read(p, "Target Number"), CultureInfo.InvariantCulture);
                var expected = p.GroupDbl("Expect Number", "Number");
                var group = p.Group("Expect Number");
                var tolerance = group?["Tolerance"] as IReadOnlyDictionary<string, object>;
                double Number(IReadOnlyDictionary<string, object> map, string key) => map != null && map.TryGetValue(key, out var v) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0;
                result = target >= expected - Number(tolerance, "-") && target <= expected + Number(tolerance, "+");
                if (group != null && group.TryGetValue("Warn Tolerance", out var wt) && wt is IReadOnlyDictionary<string, object> warn
                    && warn.TryGetValue("Enable", out var enable) && ControlValues.Bool(enable))
                    warning = result && (target < expected - Number(warn, "-") || target > expected + Number(warn, "+"));
                break;
            default: throw new InvalidOperationException("不支持的判断类型");
        }
        ctx.Vars.Set("Result", result);
        ctx.Vars.Set("Warning", warning);
        ctx.Log($"判断：{(result ? "OK" : "NG")}{(warning ? " WARN" : "")}", false);
        return true;
    }
}

public sealed class ValueSettingOp : IOperator
{
    public string Driver => "Value Setting";
    public bool Run(OpContext ctx, ParamBag p)
    {
        var results = new List<object>();
        foreach (var name in p.StringList("_input_list"))
        {
            var script = Convert.ToString(ControlValues.Read(p, name + ".Expression", ""));
            if (!LuaOp.Execute(ctx, p, script)) return false;
            results.Add(p.Input(name + ".Value"));
        }
        ctx.Vars.Set("ResultArray",results);
        return true;
    }
}

/// <summary>Lua 脚本算子：执行判定/计算，结果写全局变量。</summary>
public class LuaOp : IOperator
{
    public string Driver => "Lua";

    public bool Run(OpContext ctx, ParamBag p)
    {
        var script = p.Str("Script");
        return Execute(ctx, p, script);
    }

    public static bool Execute(OpContext ctx, ParamBag p, string script)
    {
        // Empty Lua chunks are valid no-ops (the original logic examples use them as spacers).
        script ??= "";

        var env = new LuaEnv();
        env.CheckCancellation = () => ctx.CancellationToken.ThrowIfCancellationRequested();
        LuaLibrary.Install(env);
        LuaDataFunctions.Install(ctx,env);
        foreach (var kv in ctx.Globals.All) env.Set(kv.Key, LuaLite.From(kv.Value));

        foreach (var name in p.StringList("_input_names"))
        {
            // Valid inputs retain their entry snapshot. A stale, unused binding must not
            // prevent the chunk from running; reading it still reports the original error.
            try{env.Set(name,LuaLite.From(p.Input(name)));}
            catch(InvalidOperationException error)
            {env.Set(name,new Lazy<object>(()=>throw new InvalidOperationException("脚本输入 "+name+"："+error.Message,error)));}
        }

        var texts = new List<object>();
        if (ctx.Vars.Get("Draw.Texts") is List<object> existing) texts = existing;

        void SetVar(string kind, object[] a)
        {
            if (a.Length < 2) return;
            var name = a[0]?.ToString() ?? "";
            var val = kind switch
            {
                "int" => (object)Math.Truncate(LuaLite.Num(a[1])),
                "float" => LuaLite.Num(a[1]),
                "bool" => LuaLite.Truthy(a[1]),
                _ => a[1]?.ToString() ?? ""
            };
            ctx.Globals.Set(name, val);
            env.Set(name, val);
        }

        env.Set("print", new LuaFunc(a => { ctx.Log(Join(a), false); return null; }));
        env.Set("println", new LuaFunc(a => { ctx.Log(Join(a), false); return null; }));
        env.Set("SetIntVariable", new LuaFunc(a => { SetVar("int", a); return null; }));
        env.Set("SetFloatVariable", new LuaFunc(a => { SetVar("float", a); return null; }));
        env.Set("SetStringVariable", new LuaFunc(a => { SetVar("string", a); return null; }));
        env.Set("SetBoolVariable", new LuaFunc(a => { SetVar("bool", a); return null; }));
        object GetVariable(object[] a) => a.Length > 0 && ctx.TryGetVariable(LuaLite.Str(a[0]),out var value)?LuaLite.From(value):null;
        env.Set("GetIntVariable", new LuaFunc(GetVariable));
        env.Set("GetFloatVariable", new LuaFunc(GetVariable));
        env.Set("GetStringVariable", new LuaFunc(GetVariable));
        env.Set("GetBoolVariable", new LuaFunc(GetVariable));
        foreach (var kind in new[] { "Int", "Float", "String", "Bool" })
        {
            var currentKind = kind;
            env.Set("Set" + kind, new LuaFunc(a =>
            {
                if (a.Length < 2) throw new InvalidOperationException("Set" + currentKind + " 需要名称和值");
                ctx.Vars.Set(LuaLite.Str(a[0]), a[1]);
                return null;
            }));
            env.Set("Get" + kind + "ArrayVariable", new LuaFunc(GetVariable));
            env.Set("Set" + kind + "ArrayVariable", new LuaFunc(a =>
            {
                if (a.Length < 2 || a[1] is not LuaTable array) throw new InvalidOperationException("数组变量需要 table 值");
                var list = Enumerable.Range(1, array.Length).Select(i => array.Get((double)i)).ToList();
                ctx.Globals.Set(LuaLite.Str(a[0]), list);
                env.Set(LuaLite.Str(a[0]), array);
                return null;
            }));
        }
        env.Set("Jump", new LuaFunc(a => { ctx.Vars.Set("__JumpTarget", LuaLite.Str(a[0])); return null; }));
        env.Set("GetRecipeSet", new LuaFunc(a =>
        {
            var i = a.Length > 0 ? (int)LuaLite.Num(a[0]) : 0;
            return RecipeTable(ctx, i);
        }));
        env.Set("SwitchRecipe", new LuaFunc(a => {
            if (a.Length != 1) throw new ArgumentException("SwitchRecipe 需要配方名");
            RecipeRuntime.Switch(ctx, LuaLite.Str(a[0])); return null;
        }));
        env.Set("DrawText3D", new LuaFunc(a =>
        {
            var text = a.Length > 0 ? a[^1]?.ToString() ?? "" : "";
            texts.Add(text);
            ctx.Vars.Set("Draw.Texts", texts);
            ctx.DrawTexts.Add(text);
            // 参数顺序（原版）：名称, 颜色, z, x, y, 字号, 文本
            double Num(int i) => i < a.Length && a[i] is double d ? d : (i < a.Length && double.TryParse(Convert.ToString(a[i]), out var v) ? v : 0);
            ctx.Texts.Add(new Text3D
            {
                Z = Num(2), X = Num(3), Y = Num(4), Size = Num(5),
                Text = text,
                Color = a.Length > 1 ? Convert.ToString(a[1]) ?? "green" : "green"
            });
            return null;
        }));
        env.Set("SetCylinder",new LuaFunc(a=>
        {
            if(a.Length<8)throw new ArgumentException("SetCylinder 需要名称、起点、终点和半径");
            var name=LuaLite.Str(a[0]);var values=a.Skip(1).Take(7).Select(LuaLite.Num).ToArray();
            if(values.Any(v=>!double.IsFinite(v))||values[6]<=0)throw new ArgumentException("圆柱坐标必须有限，半径必须为正");
            var start=new MeasureVector3(values[0],values[1],values[2]);var end=new MeasureVector3(values[3],values[4],values[5]);
            if((end-start).Length<=1e-12)throw new ArgumentException("圆柱轴线不能为零");
            ctx.NativeOverlays.RemoveAll(o=>o.Type=="cylinder"&&o.Values.TryGetValue("_name",out var n)&&Equals(n,name));
            ctx.NativeOverlays.Add(new(){Type="cylinder",ViewIndex=0,Values=new(){["Bottom"]=start.Native(),["Top"]=end.Native(),["Radius"]=values[6],["_name"]=name,["_color"]="#00FF00"}});
            return null;
        }));
        env.Set("SetBox", new LuaFunc(a =>
        {
            if (a.Length != 7) throw new ArgumentException("SetBox 需要名称和起终点的六个坐标");
            var coordinates = a.Skip(1).Select(LuaLite.Num).ToArray();
            if (coordinates.Any(v => !double.IsFinite(v))) throw new ArgumentException("SetBox 坐标必须为有限数值");
            ctx.Vars.Set(LuaLite.Str(a[0]), new Dictionary<string, object> {
                ["Type"] = "box", ["_type"] = "box",
                ["Start"] = new Dictionary<string, object> { ["X"] = coordinates[0], ["Y"] = coordinates[1], ["Z"] = coordinates[2] },
                ["End"] = new Dictionary<string, object> { ["X"] = coordinates[3], ["Y"] = coordinates[4], ["Z"] = coordinates[5] }
            });
            return null;
        }));
        env.Set("DrawBox", new LuaFunc(a =>
        {
            if (a.Length != 8) throw new ArgumentException("DrawBox 需要 IM、颜色和六个坐标");
            var values = a.Skip(2).Select(LuaLite.Num).ToArray();
            if (values.Any(v => !double.IsFinite(v))) throw new ArgumentException("DrawBox 坐标必须为有限数值");
            DetectionOverlay2D.Add(ctx, p, "Lua", "orientedbox", new() {
                ["Center"] = CreateRoiArrayOp.Point((values[0]+values[3])/2,(values[1]+values[4])/2,(values[2]+values[5])/2),
                ["Size"] = CreateRoiArrayOp.Point(Math.Abs(values[3]-values[0]),Math.Abs(values[4]-values[1]),Math.Abs(values[5]-values[2])),
                ["Rotation"] = CreateRoiArrayOp.Point(0,0,0)
            }, view: (int)LuaLite.Num(a[0]));
            ctx.NativeOverlays[^1].Values["_color"] = LuaLite.Str(a[1]);
            return null;
        }));
        env.Set("PrintToFile", new LuaFunc(a =>
        {
            if (a.Length < 2) throw new ArgumentException("PrintToFile 需要文件名和内容");
            var file = ctx.ResolveOutputPath(LuaLite.Str(a[0])) ?? throw new ArgumentException("PrintToFile 文件名为空");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, Join(a.Skip(1).ToArray()), Encoding.UTF8);
            return null;
        }));
        foreach (var function in new[] { "WriteToFile", "WriteLineToFile" })
            env.Set(function, new LuaFunc(a => {
                if (a.Length < 2) throw new ArgumentException(function + " 需要文件名和内容");
                var file = ctx.ResolveOutputPath(LuaLite.Str(a[0])) ?? throw new ArgumentException("文件名为空");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, Join(a.Skip(1).ToArray()) + (function == "WriteToFile" ? Environment.NewLine : ""), Encoding.UTF8);
                return null;
            }));
        env.Set("FileExists", new LuaFunc(a => {
            var path = a.Length == 1 ? ctx.ResolvePath(LuaLite.Str(a[0])) : null;
            if (path == null) return false;
            try { using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }));
        env.Set("FileSize", new LuaFunc(a => (double)new FileInfo(ctx.ResolvePath(LuaLite.Str(a[0])) ?? throw new ArgumentException("文件名为空")).Length));
        env.Set("ReadFromFile", new LuaFunc(a => File.ReadAllText(ctx.ResolvePath(LuaLite.Str(a[0])) ?? throw new ArgumentException("文件名为空"))));

        LuaManualFunctions.Install(ctx,p,env);
        try
        {
            LuaLite.Run(script, env);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ctx.Log($"Lua 执行失败：{ex.Message}", true);
            return false;
        }

        ctx.Log($"Lua：执行完成（{script.Split('\n').Length} 行）", false);
        return true;
    }

    private static string Join(object[] a) => string.Join("\t", a.Select(LuaLite.Str));

    /// <summary>把工程 recipe 文件（json）转成 Lua 表。</summary>
    internal static LuaTable RecipeTable(OpContext ctx, int index)
    {
        var path = ctx.RecipePath;
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            var rows = RecipeRuntime.Read(ctx.ResolvePath(path));
            return index >= 0 && index < rows.Count ? LuaLite.From(rows[index]) as LuaTable : null;
        }
        catch (Exception e) when (e is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>写 CSV：按 _input_list 的列追加一行。</summary>
public class WriteCsvOp : IOperator
{
    public string Driver => "Write CSV";

    public bool Run(OpContext ctx, ParamBag p)
    {
        var file = ctx.ResolveOutputPath(string.IsNullOrWhiteSpace(p.Str("File")) ? "data.csv" : p.Str("File"));
        var cols = p.StringList("_input_list");
        var decimals = (int)p.Dbl("Decimal Places", 3);
        var first = p.Str("First Column", "TimeStamp");

        var sb = new StringBuilder();
        var needHeader = !File.Exists(file) || new FileInfo(file).Length == 0;
        if (needHeader && !string.IsNullOrEmpty(first))
            sb.AppendLine(first + "," + string.Join(",", cols));

        var vals = new List<string> { "\"" + DateTime.Now.ToString("yyyy_MM_dd HH:mm:ss.fff") + "\"" };
        foreach (var c in cols)
        {
            var v = p.Input(c);
            vals.Add(v switch
            {
                null => "",
                double d => d.ToString("F" + Math.Clamp(decimals, 0, 8), CultureInfo.InvariantCulture),
                bool b => b ? "OK" : "NG",
                _ => v.ToString().Replace(",", ";")
            });
        }
        sb.AppendLine(string.Join(",", vals));

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.AppendAllText(file, sb.ToString(), Encoding.UTF8);
        ctx.Log($"写 CSV：{Path.GetFileName(file)} 追加 {cols.Count} 列", false);
        return true;
    }
}
