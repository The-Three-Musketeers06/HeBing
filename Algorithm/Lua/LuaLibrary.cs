using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace AiVisionStudio.Algorithm.Lua;

/// <summary>The deterministic subset of Lua standard libraries used by project scripts.</summary>
public static class LuaLibrary
{
    public static void Install(LuaEnv env)
    {
        var math = new LuaTable();
        void Unary(string name, Func<double, double> fn) => math.Set(name, new LuaFunc(a => fn(LuaLite.Num(a[0]))));
        Unary("abs", Math.Abs); Unary("sqrt", Math.Sqrt); Unary("floor", Math.Floor); Unary("ceil", Math.Ceiling);
        Unary("sin", Math.Sin); Unary("cos", Math.Cos); Unary("tan", Math.Tan); Unary("asin", Math.Asin);
        Unary("acos", Math.Acos); Unary("exp", Math.Exp); Unary("deg", x => x * 180 / Math.PI);
        Unary("rad", x => x * Math.PI / 180);
        math.Set("atan", new LuaFunc(a => a.Length > 1 ? Math.Atan2(LuaLite.Num(a[0]), LuaLite.Num(a[1])) : Math.Atan(LuaLite.Num(a[0]))));
        math.Set("atan2", new LuaFunc(a => Math.Atan2(LuaLite.Num(a[0]), LuaLite.Num(a[1]))));
        math.Set("log", new LuaFunc(a => a.Length > 1 ? Math.Log(LuaLite.Num(a[0]), LuaLite.Num(a[1])) : Math.Log(LuaLite.Num(a[0]))));
        math.Set("max", new LuaFunc(a => a.Select(LuaLite.Num).Max()));
        math.Set("min", new LuaFunc(a => a.Select(LuaLite.Num).Min()));
        math.Set("pi", Math.PI); math.Set("huge", double.PositiveInfinity);
        env.Set("math", math);

        var table = new LuaTable();
        table.Set("insert", new LuaFunc(a =>
        {
            var t = RequireTable(a[0]);
            var index = a.Length > 2 ? (int)LuaLite.Num(a[1]) : t.Length + 1;
            if (index < 1 || index > t.Length + 1) throw new InvalidOperationException("table.insert 索引越界");
            for (int i = t.Length; i >= index; i--) t.Set((double)i + 1, t.Get((double)i));
            t.Set((double)index, a[^1]);
            return null;
        }));
        table.Set("remove", new LuaFunc(a =>
        {
            var t = RequireTable(a[0]);
            var index = a.Length > 1 ? (int)LuaLite.Num(a[1]) : t.Length;
            var length = t.Length;
            if (index < 1 || index > length) return null;
            var value = t.Get((double)index);
            for (var i = index; i < length; i++) t.Set((double)i, t.Get((double)i + 1));
            t.Set((double)length, null);
            return value;
        }));
        table.Set("concat", new LuaFunc(a =>
        {
            var t = RequireTable(a[0]);
            var first = a.Length > 2 ? (int)LuaLite.Num(a[2]) : 1;
            var last = a.Length > 3 ? (int)LuaLite.Num(a[3]) : t.Length;
            return string.Join(a.Length > 1 ? LuaLite.Str(a[1]) : "", Enumerable.Range(first, Math.Max(0, last - first + 1)).Select(i => LuaLite.Str(t.Get((double)i))));
        }));
        env.Set("table", table);
        env.Set("ipairs", new LuaFunc(a => Sequential(RequireTable(a[0]))));
        env.Set("pairs", new LuaFunc(a => RequireTable(a[0]).Map.Select(kv => new[] { kv.Key, kv.Value }).ToArray()));
        env.Set("type", new LuaFunc(a => a[0] switch { null => "nil", bool => "boolean", string => "string", LuaTable => "table", LuaFunc => "function", _ => "number" }));
        env.Set("tonumber", new LuaFunc(a => a.Length > 0 && double.TryParse(LuaLite.Str(a[0]), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null));
        env.Set("tostring", new LuaFunc(a => LuaLite.Str(a.Length > 0 ? a[0] : null)));
        env.Set("error", new LuaFunc(a => throw new InvalidOperationException(a.Length > 0 ? LuaLite.Str(a[0]) : "Lua error")));
        env.Set("assert", new LuaFunc(a => LuaLite.Truthy(a[0]) ? a[0] : throw new InvalidOperationException(a.Length > 1 ? LuaLite.Str(a[1]) : "Lua assert failed")));
        var strings = new LuaTable();
        strings.Set("format", new LuaFunc(a => Format(a)));
        strings.Set("len", new LuaFunc(a => (double)LuaLite.Str(a[0]).Length));
        strings.Set("lower", new LuaFunc(a => LuaLite.Str(a[0]).ToLowerInvariant()));
        strings.Set("upper", new LuaFunc(a => LuaLite.Str(a[0]).ToUpperInvariant()));
        strings.Set("sub", new LuaFunc(a =>
        {
            var s = LuaLite.Str(a[0]);
            int Position(object value) { var i = (int)LuaLite.Num(value); return i < 0 ? s.Length + i + 1 : i; }
            var first = Math.Clamp(Position(a[1]), 1, s.Length + 1);
            var last = Math.Clamp(a.Length > 2 ? Position(a[2]) : s.Length, 0, s.Length);
            return last < first ? "" : s.Substring(first - 1, last - first + 1);
        }));
        env.Set("string", strings);
        var os = new LuaTable();
        os.Set("time", new LuaFunc(a => (double)DateTimeOffset.Now.ToUnixTimeSeconds()));
        os.Set("date", new LuaFunc(a =>
        {
            var format = a.Length > 0 ? LuaLite.Str(a[0]) : "%c";
            var utc = format.StartsWith('!');
            if (utc) format = format[1..];
            var timestamp = a.Length > 1 ? DateTimeOffset.FromUnixTimeSeconds((long)LuaLite.Num(a[1])) : DateTimeOffset.Now;
            var date = utc ? timestamp.UtcDateTime : timestamp.LocalDateTime;
            if (format == "*t")
            {
                var result = new LuaTable();
                foreach (var item in new[] { ("year",date.Year), ("month",date.Month), ("day",date.Day), ("hour",date.Hour), ("min",date.Minute), ("sec",date.Second), ("wday",(int)date.DayOfWeek+1), ("yday",date.DayOfYear) }) result.Set(item.Item1,(double)item.Item2);
                result.Set("isdst", !utc && TimeZoneInfo.Local.IsDaylightSavingTime(date)); return result;
            }
            return Regex.Replace(format, "%([%YymdHMSjcFxX])", m => m.Groups[1].Value switch {
                "%" => "%", "Y" => date.ToString("yyyy"), "y" => date.ToString("yy"), "m" => date.ToString("MM"),
                "d" => date.ToString("dd"), "H" => date.ToString("HH"), "M" => date.ToString("mm"), "S" => date.ToString("ss"),
                "j" => date.DayOfYear.ToString("D3"), "F" => date.ToString("yyyy-MM-dd"), "X" => date.ToString("HH:mm:ss"),
                "x" => date.ToString("MM/dd/yy"), _ => date.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture)
            });
        }));
        env.Set("os", os);
    }

    private static LuaTable RequireTable(object value) => value as LuaTable ?? throw new InvalidOperationException("Lua：参数必须为 table");
    private static IEnumerable<object[]> Sequential(LuaTable t)
    {
        for (var i = 1; t.Get((double)i) is { } value; i++) yield return new object[] { (double)i, value };
    }

    private static string Format(object[] args)
    {
        int index = 1;
        return Regex.Replace(LuaLite.Str(args[0]), @"%([+\- 0]*)(\d+)?(?:\.(\d+))?([%sdfgeExX])", match =>
        {
            var type = match.Groups[4].Value[0];
            if (type == '%') return "%";
            if (index >= args.Length) throw new InvalidOperationException("string.format 参数不足");
            var value = args[index++];
            var precision = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 6;
            if (precision > 99) throw new InvalidOperationException("string.format 精度过大");
            string text = type switch
            {
                's' => LuaLite.Str(value),
                'd' => ((long)LuaLite.Num(value)).ToString(CultureInfo.InvariantCulture),
                'x' or 'X' => ((long)LuaLite.Num(value)).ToString(type.ToString(), CultureInfo.InvariantCulture),
                _ => LuaLite.Num(value).ToString(type + precision.ToString(), CultureInfo.InvariantCulture)
            };
            var flags = match.Groups[1].Value;
            if (type != 's' && LuaLite.Num(value) >= 0 && flags.Contains('+')) text = "+" + text;
            var width = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
            if (width > 10000) throw new InvalidOperationException("string.format 宽度过大");
            return flags.Contains('-') ? text.PadRight(width) : text.PadLeft(width, flags.Contains('0') ? '0' : ' ');
        });
    }
}
