using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AiVisionStudio.Algorithm.Lua;

/// <summary>Lua 表：数字键（1 起）与字符串键混用。</summary>
public class LuaTable
{
    public readonly Dictionary<object, object> Map = new();

    public object Get(object key)
        => Map.TryGetValue(Norm(key), out var v) ? v : null;

    public void Set(object key, object value)
    {
        var k = Norm(key);
        if (value == null) Map.Remove(k);
        else Map[k] = value;
    }

    public int Length
    {
        get
        {
            int n = 0;
            while (Map.ContainsKey((double)(n + 1))) n++;
            return n;
        }
    }

    private static object Norm(object k)
        => k is double d && d == Math.Floor(d) && Math.Abs(d) < 1e15 ? d : (k?.ToString() ?? "");
}

/// <summary>内建函数类型。</summary>
public class LuaFunc
{
    [ThreadStatic] private static int _depth;
    public Func<object[], object> Body { get; }
    public LuaFunc(Func<object[], object> body) => Body = body;
    public object Invoke(object[] a)
    {
        if (++_depth > 200) { _depth--; throw new InvalidOperationException("Lua 函数调用嵌套超过 200 层"); }
        try { return Body(a); } finally { _depth--; }
    }
}

/// <summary>环境（作用域链）。</summary>
public class LuaEnv
{
    private readonly Dictionary<string, object> _vars = new(StringComparer.Ordinal);
    private readonly LuaEnv _parent;
    private int _remainingSteps = 1000000;

    public Action CheckCancellation { get; set; }

    public void Tick()
    {
        if (_parent != null) { _parent.Tick(); return; }
        CheckCancellation?.Invoke();
        if (--_remainingSteps < 0) throw new InvalidOperationException("Lua 执行超过 1000000 步，已中止");
    }

    public void ResetBudget() => _remainingSteps = 1000000;

    public LuaEnv(LuaEnv parent = null) => _parent = parent;

    public object Get(string name)
    {
        if (_vars.TryGetValue(name, out var v)) return v is Lazy<object> deferred?deferred.Value:v;
        return _parent?.Get(name);
    }

    public void Set(string name, object value)
    {
        if (_vars.ContainsKey(name) || _parent == null) _vars[name] = value;
        else _parent.Set(name, value);
    }

    public void SetLocal(string name, object value) => _vars[name] = value;
}

/// <summary>精简 Lua 解释器：支持变量、算术/比较/逻辑、if/for/while、表、下标、函数调用、字符串拼接。</summary>
public static class LuaLite
{
    public static object Run(string code, LuaEnv env)
    {
        var tokens = Lex(code);
        var p = new Parser(tokens);
        var block = p.ParseBlock();
        p.ExpectEnd();
        env.ResetBudget();
        try
        {
            block(env);
        }
        catch (ReturnSignal returned) { return returned.Value; }
        catch (BreakSignal) { throw new InvalidOperationException("Lua break 只能用于循环内"); }
        return null;
    }

    // ---------------- 词法 ----------------

    private enum T { Name, Number, String, Op, Eof }

    private readonly struct Tok
    {
        public readonly T Kind;
        public readonly string Text;
        public Tok(T k, string t) { Kind = k; Text = t; }
    }

    private static List<Tok> Lex(string s)
    {
        var list = new List<Tok>();
        int i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // 注释
            if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
            {
                if (i + 3 < s.Length && s.Substring(i + 2, 2) == "[[")
                {
                    var end = s.IndexOf("]]", i + 4, StringComparison.Ordinal);
                    if (end < 0) throw new FormatException("Lua 块注释未闭合");
                    i = end + 2;
                    continue;
                }
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int st = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                list.Add(new Tok(T.Name, s.Substring(st, i - st)));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                int st = i;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                if (i < s.Length && s[i] == '.' && !(i + 1 < s.Length && s[i + 1] == '.'))
                {
                    i++;
                    while (i < s.Length && char.IsDigit(s[i])) i++;
                }
                if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                {
                    i++;
                    if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                    while (i < s.Length && char.IsDigit(s[i])) i++;
                }
                list.Add(new Tok(T.Number, s.Substring(st, i - st)));
                continue;
            }

            if (c == '"' || c == '\'')
            {
                var q = c;
                i++;
                var sb = new StringBuilder();
                while (i < s.Length && s[i] != q)
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        i++;
                        sb.Append(s[i] switch
                        {
                            'n' => '\n', 't' => '\t', 'r' => '\r', '\\' => '\\', '"' => '"', '\'' => '\'',
                            _ => s[i]
                        });
                    }
                    else sb.Append(s[i]);
                    i++;
                }
                if (i >= s.Length) throw new FormatException("Lua 字符串未闭合");
                i++; // 收尾引号
                list.Add(new Tok(T.String, sb.ToString()));
                continue;
            }

            // 多字符运算符
            string[] ops3 = { "..." };
            string[] ops2 = { "==", "~=", "<=", ">=", "..", "//", "::" };
            var three = i + 3 <= s.Length ? s.Substring(i, 3) : null;
            if (three != null && ops3.Contains(three)) { list.Add(new Tok(T.Op, three)); i += 3; continue; }
            var two = i + 2 <= s.Length ? s.Substring(i, 2) : null;
            if (two != null && ops2.Contains(two)) { list.Add(new Tok(T.Op, two)); i += 2; continue; }

            list.Add(new Tok(T.Op, c.ToString()));
            i++;
        }
        list.Add(new Tok(T.Eof, ""));
        return list;
    }

    // ---------------- 语法 ----------------

    private sealed class BreakSignal : Exception { }
    private sealed class ReturnSignal(object value) : Exception { public object Value { get; } = value; }

    private sealed class Parser
    {
        private readonly List<Tok> _t;
        private int _i;

        public Parser(List<Tok> t) => _t = t;
        public void ExpectEnd()
        {
            if (Cur.Kind != T.Eof) throw new FormatException($"Lua 语法错误：多余的 '{Cur.Text}'");
        }

        private Tok Cur => _t[_i];
        private Tok Peek(int k = 1) => _i + k < _t.Count ? _t[_i + k] : _t[^1];
        private void Next() => _i++;
        private bool Is(string text) => Cur.Text == text && Cur.Kind != T.String;
        private bool Accept(string text) { if (Is(text)) { Next(); return true; } return false; }
        private void Expect(string text)
        {
            if (!Accept(text)) throw new Exception($"Lua 语法错误：期望 '{text}'，实际 '{Cur.Text}'");
        }

        private static readonly HashSet<string> BlockEnd = new() { "end", "else", "elseif", "until" };

        public Action<LuaEnv> ParseBlock()
        {
            var stmts = new List<Action<LuaEnv>>();
            while (Cur.Kind != T.Eof && !(Cur.Kind == T.Name && BlockEnd.Contains(Cur.Text)))
            {
                var st = ParseStatement();
                if (st != null) stmts.Add(st);
            }
            return env =>
            {
                foreach (var s in stmts) { env.Tick(); s(env); }
            };
        }

        private Action<LuaEnv> ParseStatement()
        {
            if (Accept(";")) return null;

            if (Is("if"))
            {
                Next();
                var branches = new List<(Func<LuaEnv, object> cond, Action<LuaEnv> body)>();
                var c = ParseExpr();
                Expect("then");
                branches.Add((c, ParseBlock()));

                Action<LuaEnv> elseBody = null;
                while (true)
                {
                    if (Accept("elseif"))
                    {
                        var cc = ParseExpr();
                        Expect("then");
                        branches.Add((cc, ParseBlock()));
                        continue;
                    }
                    if (Accept("else"))
                    {
                        elseBody = ParseBlock();
                    }
                    Expect("end");
                    break;
                }
                return env =>
                {
                    foreach (var (cond, body) in branches)
                        if (Truthy(cond(env))) { body(new LuaEnv(env)); return; }
                    elseBody?.Invoke(new LuaEnv(env));
                };
            }

            if (Is("for"))
            {
                Next();
                var name = Cur.Text;
                Next();
                if (Is(",") || Is("in"))
                {
                    var names = new List<string> { name };
                    while (Accept(",")) { names.Add(Cur.Text); Next(); }
                    Expect("in");
                    var iterator = ParseExpr();
                    Expect("do");
                    var eachBody = ParseBlock();
                    Expect("end");
                    return env =>
                    {
                        if (iterator(env) is not IEnumerable<object[]> rows)
                            throw new InvalidOperationException("Lua for 需要 pairs/ipairs 迭代器");
                        foreach (var row in rows)
                        {
                            env.Tick();
                            var scope = new LuaEnv(env);
                            for (var k = 0; k < names.Count; k++) scope.SetLocal(names[k], k < row.Length ? row[k] : null);
                            try { eachBody(scope); } catch (BreakSignal) { break; }
                        }
                    };
                }
                Expect("=");
                var from = ParseExpr();
                Expect(",");
                var to = ParseExpr();
                Func<LuaEnv, object> step = null;
                if (Accept(",")) step = ParseExpr();
                Expect("do");
                var body = ParseBlock();
                Expect("end");
                return env =>
                {
                    var a = Num(from(env));
                    var b = Num(to(env));
                    var s = step == null ? 1.0 : Num(step(env));
                    if (s == 0) throw new InvalidOperationException("Lua for 步长不能为 0");
                    for (double v = a; s > 0 ? v <= b : v >= b; v += s)
                    {
                        env.Tick();
                        var scope = new LuaEnv(env);
                        scope.SetLocal(name, v);
                        try { body(scope); } catch (BreakSignal) { break; }
                    }
                };
            }

            if (Is("while"))
            {
                Next();
                var cond = ParseExpr();
                Expect("do");
                var body = ParseBlock();
                Expect("end");
                return env =>
                {
                    while (Truthy(cond(env)))
                    {
                        env.Tick();
                        try { body(new LuaEnv(env)); } catch (BreakSignal) { break; }
                    }
                };
            }

            if (Accept("repeat"))
            {
                var body = ParseBlock();
                Expect("until");
                var cond = ParseExpr();
                return env =>
                {
                    while (true)
                    {
                        env.Tick();
                        var scope = new LuaEnv(env);
                        try { body(scope); } catch (BreakSignal) { break; }
                        if (Truthy(cond(scope))) break;
                    }
                };
            }

            if (Accept("function"))
            {
                var name = Cur.Text;
                Next();
                var function = ParseFunction();
                return env => env.Set(name, function(env));
            }

            if (Is("do"))
            {
                Next();
                var body = ParseBlock();
                Expect("end");
                return env => body(new LuaEnv(env));
            }

            if (Is("break")) { Next(); return _ => throw new BreakSignal(); }

            if (Is("return"))
            {
                Next();
                if (!(Cur.Kind == T.Name && BlockEnd.Contains(Cur.Text)) && Cur.Kind != T.Eof && !Is(";"))
                {
                    var e = ParseExprList();
                    return env => throw new ReturnSignal(e(env));
                }
                return _ => throw new ReturnSignal(null);
            }

            if (Is("local"))
            {
                Next();
                if (Accept("function"))
                {
                    var functionName = Cur.Text;
                    Next();
                    var function = ParseFunction();
                    return env => env.SetLocal(functionName, function(env));
                }
                // local a, b = 1, 2
                var names = new List<string> { Cur.Text };
                Next();
                while (Accept(",")) { names.Add(Cur.Text); Next(); }
                if (!Accept("=")) return env => { foreach (var n in names) env.SetLocal(n, null); };
                var vals = ParseExprList();
                return env =>
                {
                    var v = vals(env);
                    var arr = v as object[] ?? new[] { v };
                    for (int k = 0; k < names.Count; k++)
                        env.SetLocal(names[k], k < arr.Length ? arr[k] : null);
                };
            }

            // 函数调用语句 或 赋值
            var first = ParseSuffixed();
            if (Is("=") || Is(","))
            {
                var targets = new List<Func<LuaEnv, Action<object>>>();
                targets.Add(first.setter ?? throw new Exception("Lua 语法错误：赋值左侧不是变量"));
                while (Accept(","))
                {
                    var more = ParseSuffixed();
                    targets.Add(more.setter ?? throw new Exception("Lua 语法错误：赋值左侧不是变量"));
                }
                Expect("=");
                var vals = ParseExprList();
                return env =>
                {
                    var v = vals(env);
                    var arr = v as object[] ?? new[] { v };
                    for (int k = 0; k < targets.Count; k++)
                        targets[k](env)(k < arr.Length ? arr[k] : null);
                };
            }

            // 纯表达式/调用
            var getter = first.getter;
            return env => { getter(env); };
        }

        private Func<LuaEnv, object> ParseExprList()
        {
            var list = new List<Func<LuaEnv, object>> { ParseExpr() };
            while (Accept(",")) list.Add(ParseExpr());
            if (list.Count == 1) return list[0];
            return env => list.Select(f => f(env)).ToArray();
        }

        private Func<LuaEnv, object> ParseExpr() => ParseOr();

        private Func<LuaEnv, object> ParseOr()
        {
            var l = ParseAnd();
            while (Is("or"))
            {
                Next();
                var r = ParseAnd();
                var ll = l;
                l = env => { var a = ll(env); return Truthy(a) ? a : r(env); };
            }
            return l;
        }

        private Func<LuaEnv, object> ParseAnd()
        {
            var l = ParseCmp();
            while (Is("and"))
            {
                Next();
                var r = ParseCmp();
                var ll = l;
                l = env => { var a = ll(env); return Truthy(a) ? r(env) : a; };
            }
            return l;
        }

        private Func<LuaEnv, object> ParseCmp()
        {
            var l = ParseConcat();
            while (Is("==") || Is("~=") || Is("<") || Is(">") || Is("<=") || Is(">="))
            {
                var op = Cur.Text;
                Next();
                var r = ParseConcat();
                var ll = l;
                l = env => Compare(op, ll(env), r(env));
            }
            return l;
        }

        private Func<LuaEnv, object> ParseConcat()
        {
            var l = ParseAdd();
            if (Is(".."))
            {
                Next();
                var r = ParseConcat(); // 右结合
                var ll = l;
                return env => Str(ll(env)) + Str(r(env));
            }
            return l;
        }

        private Func<LuaEnv, object> ParseAdd()
        {
            var l = ParseMul();
            while (Is("+") || Is("-"))
            {
                var op = Cur.Text;
                Next();
                var r = ParseMul();
                var ll = l;
                l = env => op == "+" ? Num(ll(env)) + Num(r(env)) : Num(ll(env)) - Num(r(env));
            }
            return l;
        }

        private Func<LuaEnv, object> ParseMul()
        {
            var l = ParseUnary();
            while (Is("*") || Is("/") || Is("%") || Is("//"))
            {
                var op = Cur.Text;
                Next();
                var r = ParseUnary();
                var ll = l;
                l = env =>
                {
                    var a = Num(ll(env)); var b = Num(r(env));
                    return op switch
                    {
                        "*" => a * b,
                        "/" => a / b,
                        "%" => a - Math.Floor(a / b) * b,
                        _ => Math.Floor(a / b)
                    };
                };
            }
            return l;
        }

        private Func<LuaEnv, object> ParseUnary()
        {
            if (Accept("-")) { var e = ParseUnary(); return env => -Num(e(env)); }
            if (Is("not")) { Next(); var e = ParseUnary(); return env => !Truthy(e(env)); }
            if (Accept("#")) { var e = ParseUnary(); return env => (double)Len(e(env)); }
            var value = ParseSuffixed().getter;
            if (!Accept("^")) return value;
            var exponent = ParseUnary();
            return env => Math.Pow(Num(value(env)), Num(exponent(env)));
        }

        private (Func<LuaEnv, object> getter, Func<LuaEnv, Action<object>> setter) ParseSuffixed()
        {
            Func<LuaEnv, object> cur;
            Func<LuaEnv, Action<object>> setter = null;

            if (Cur.Kind == T.Number)
            {
                var text = Cur.Text;
                Next();
                var d = double.Parse(text, CultureInfo.InvariantCulture);
                cur = _ => d;
            }
            else if (Cur.Kind == T.String)
            {
                var str = Cur.Text;
                Next();
                cur = _ => str;
            }
            else if (Cur.Kind == T.Name && !IsKeyword(Cur.Text))
            {
                var name = Cur.Text;
                Next();
                cur = env => env.Get(name);
                setter = env => v => env.Set(name, v);
            }
            else if (Accept("("))
            {
                cur = ParseExpr();
                Expect(")");
            }
            else if (Is("{"))
            {
                cur = ParseTable();
            }
            else if (Accept("function")) { cur = ParseFunction(); }
            else if (Is("nil")) { Next(); cur = _ => null; }
            else if (Is("true")) { Next(); cur = _ => true; }
            else if (Is("false")) { Next(); cur = _ => false; }
            else
            {
                throw new Exception($"Lua 语法错误：意外的 '{Cur.Text}'");
            }

            while (true)
            {
                if (Accept("."))
                {
                    var field = Cur.Text;
                    Next();
                    var obj = cur;
                    cur = env => Index(obj(env), field);
                    var o2 = obj;
                    setter = env => v => SetIndex(o2(env), field, v);
                }
                else if (Accept("["))
                {
                    var key = ParseExpr();
                    Expect("]");
                    var obj = cur;
                    cur = env => Index(obj(env), key(env));
                    var o2 = obj; var k2 = key;
                    setter = env => v => SetIndex(o2(env), k2(env), v);
                }
                else if (Is("(") || Cur.Kind == T.String || Is("{"))
                {
                    Func<LuaEnv, object[]> args;
                    if (Accept("("))
                    {
                        if (Accept(")")) args = _ => Array.Empty<object>();
                        else
                        {
                            var list = new List<Func<LuaEnv, object>> { ParseExpr() };
                            while (Accept(",")) list.Add(ParseExpr());
                            Expect(")");
                            args = env => list.Select(f => f(env)).ToArray();
                        }
                    }
                    else if (Cur.Kind == T.String)
                    {
                        var sv = Cur.Text;
                        Next();
                        args = _ => new object[] { sv };
                    }
                    else
                    {
                        var t = ParseTable();
                        args = env => new[] { t(env) };
                    }

                    var fn = cur;
                    cur = env =>
                    {
                        var f = fn(env);
                        var a = args(env);
                        if (f is LuaFunc lf) return lf.Invoke(a);
                        throw new Exception("Lua 运行时：尝试调用非函数");
                    };
                    setter = null;
                }
                else break;
            }

            return (cur, setter);
        }

        private Func<LuaEnv, object> ParseFunction()
        {
            Expect("(");
            var names = new List<string>();
            if (!Accept(")"))
            {
                do { names.Add(Cur.Text); Next(); } while (Accept(","));
                Expect(")");
            }
            var body = ParseBlock();
            Expect("end");
            return closure => new LuaFunc(args =>
            {
                closure.Tick();
                var scope = new LuaEnv(closure);
                for (var i = 0; i < names.Count; i++) scope.SetLocal(names[i], i < args.Length ? args[i] : null);
                try { body(scope); } catch (ReturnSignal returned) { return returned.Value; }
                return null;
            });
        }

        private Func<LuaEnv, object> ParseTable()
        {
            Expect("{");
            var items = new List<(Func<LuaEnv, object> key, Func<LuaEnv, object> val, bool positional)>();
            while (!Is("}"))
            {
                if (Cur.Kind == T.Name && Peek().Text == "=" && Peek().Kind == T.Op)
                {
                    var k = Cur.Text; Next(); Next();
                    items.Add((_ => k, ParseExpr(), false));
                }
                else if (Is("["))
                {
                    Next();
                    var k = ParseExpr();
                    Expect("]");
                    Expect("=");
                    items.Add((k, ParseExpr(), false));
                }
                else
                {
                    items.Add((null, ParseExpr(), true));
                }

                if (!Accept(",") && !Accept(";")) break;
            }
            Expect("}");
            return env =>
            {
                var t = new LuaTable();
                double idx = 1;
                foreach (var (k, v, pos) in items)
                {
                    if (pos) t.Set(idx++, v(env));
                    else t.Set(k(env), v(env));
                }
                return t;
            };
        }

        private static bool IsKeyword(string s) => s switch
        {
            "if" or "then" or "else" or "elseif" or "end" or "for" or "while" or "do" or "break"
                or "return" or "local" or "function" or "and" or "or" or "not" or "nil" or "true"
                or "false" or "repeat" or "until" or "in" => true,
            _ => false
        };
    }

    // ---------------- 运行时辅助 ----------------

    public static bool Truthy(object v) => v switch
    {
        null => false,
        bool b => b,
        _ => true
    };

    public static double Num(object v) => v switch
    {
        double d => d,
        int i => i,
        long l => l,
        float f => f,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) => r,
        _ => throw new InvalidOperationException($"Lua：{Str(v)} 不能转换为数字")
    };

    public static string Str(object v) => v switch
    {
        null => "nil",
        bool b => b ? "true" : "false",
        double d => d == Math.Floor(d) && Math.Abs(d) < 1e15
            ? ((long)d).ToString(CultureInfo.InvariantCulture)
            : d.ToString("0.######", CultureInfo.InvariantCulture),
        _ => v.ToString()
    };

    private static int Len(object v) => v switch
    {
        string s => s.Length,
        LuaTable t => t.Length,
        _ => 0
    };

    private static object Index(object obj, object key)
    {
        if (obj is LuaTable t) return t.Get(key);
        throw new InvalidOperationException("Lua：尝试索引非 table 值");
    }

    private static void SetIndex(object obj, object key, object val)
    {
        if (obj is LuaTable t) t.Set(key, val);
        else throw new InvalidOperationException("Lua：尝试写入非 table 值");
    }

    private static object Compare(string op, object a, object b)
    {
        if (op == "==") return Eq(a, b);
        if (op == "~=") return !Eq(a, b);

        // 数字优先，其次字符串
        if (a is string sa && b is string sb)
        {
            var c = string.CompareOrdinal(sa, sb);
            return op switch { "<" => c < 0, ">" => c > 0, "<=" => c <= 0, ">=" => c >= 0, _ => false };
        }
        var x = Num(a); var y = Num(b);
        return op switch { "<" => x < y, ">" => x > y, "<=" => x <= y, ">=" => x >= y, _ => false };
    }

    private static bool Eq(object a, object b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a is string || b is string)
        {
            if (a is string && b is string) return a.ToString() == b.ToString();
            return false;
        }
        if (a is bool || b is bool) return a is bool ba && b is bool bb && ba == bb;
        if (a is LuaTable || b is LuaTable || a is LuaFunc || b is LuaFunc) return ReferenceEquals(a, b);
        return Num(a) == Num(b);
    }

    /// <summary>把 C# 对象转换成 Lua 值（Dictionary/List 递归）。</summary>
    public static object From(object v)
    {
        switch (v)
        {
            case null: return null;
            case LuaTable _: return v;
            case string _: return v;
            case bool _: return v;
            case double _: return v;
            case float f: return (double)f;
            case int i: return (double)i;
            case long l: return (double)l;
            case IReadOnlyDictionary<string, object> d:
            {
                var t = new LuaTable();
                foreach (var kv in d) t.Set(kv.Key, From(kv.Value));
                return t;
            }
            case System.Collections.IDictionary d2:
            {
                var t = new LuaTable();
                foreach (System.Collections.DictionaryEntry kv in d2) t.Set(kv.Key?.ToString() ?? "", From(kv.Value));
                return t;
            }
            case System.Collections.IEnumerable e:
            {
                var t = new LuaTable();
                double idx = 1;
                foreach (var item in e) t.Set(idx++, From(item));
                return t;
            }
            default:
                // Geometry results are plain C# objects; Lua accesses their named coordinates.
                bool ScalarType(Type type) => type.IsPrimitive || type == typeof(string) || type == typeof(decimal);
                var properties = v.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && ScalarType(p.PropertyType)).ToArray();
                var fields = v.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .Where(f => ScalarType(f.FieldType)).ToArray();
                if (properties.Length == 0 && fields.Length == 0) return v.ToString();
                var result = new LuaTable();
                foreach (var property in properties) result.Set(property.Name, From(property.GetValue(v)));
                foreach (var field in fields) result.Set(field.Name, From(field.GetValue(v)));
                return result;
        }
    }
}
