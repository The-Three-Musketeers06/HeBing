using System.Collections.Generic;
using System.IO;
using OpenCvSharp;
using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Algorithm;

/// <summary>图像池：对应原版的 IM0 / IM1 / IM2 …</summary>
public partial class ImagePool : IDisposable
{
    internal Action<int> CheckWrite { get; set; }
    internal event Action<int> VisualWritten;
    internal event Action<int> VisualRead;
    private readonly Dictionary<int, Mat> _images = new();
    private readonly Dictionary<int, DepthImageGeometry> _depthGeometry = new();
    public DepthImageGeometry GetDepthGeometry(int index)
        => _depthGeometry.TryGetValue(index, out var geometry) ? geometry : null;
    public void SetDepthGeometry(int index, DepthImageGeometry geometry)
    {
        if (!Has(index) || Get(index).Cols != geometry.Raster.Width || Get(index).Rows != geometry.Raster.Height)
            throw new ArgumentException("深度图尺寸与点云映射不一致");
        _depthGeometry[index] = geometry;
    }

    public Mat Get(int index) { VisualRead?.Invoke(index); return _inputImages.TryGetValue(index, out var input) ? input.Image : _images.TryGetValue(index, out var m) ? m : null; }

    public void Set(int index, Mat mat)
    {
        CheckWrite?.Invoke(index);
        _inputImages.Remove(index);
        _depthGeometry.Remove(index);
        if (_images.TryGetValue(index, out var old) && !ReferenceEquals(old, mat))
        {
            old.Dispose();
        }
        _images[index] = mat;
        //数据写入完成后，在登记这次写入
        VisualWritten?.Invoke(index);
    }

    public bool Has(int index) => _inputImages.ContainsKey(index) || _images.ContainsKey(index) && _images[index] != null;
    public void Remove(int index)
    {if(_images.ContainsKey(index))CheckWrite?.Invoke(index);_inputImages.Remove(index);_depthGeometry.Remove(index);if(_images.Remove(index,out var image))image?.Dispose();}

    public IReadOnlyDictionary<int, Mat> All => _images;

    public void Dispose()
    {
        foreach (var m in _images.Values) m?.Dispose();
        _images.Clear();
        _depthGeometry.Clear();
    }
}

/// <summary>变量池：算子输出（结果、测量值）供后续算子引用。</summary>
public class VarPool
{
    private readonly Dictionary<string, object> _vars;

    public VarPool(bool caseSensitive = false)
        => _vars = new Dictionary<string, object>(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    public void Set(string key, object value) => _vars[key] = value;

    public void Clear() => _vars.Clear();

    public object Get(string key) => _vars.TryGetValue(key, out var v) ? v : null;

    public bool TryGet(string key, out object value) => _vars.TryGetValue(key, out value);

    public double GetDouble(string key, double def = 0)
        => _vars.TryGetValue(key, out var v) && v != null && double.TryParse(v.ToString(), out var d) ? d : def;

    public IReadOnlyDictionary<string, object> All => _vars;
}

/// <summary>参数包：把 .aivs 里的 parameters.value 转成字典访问。</summary>
public class ParamBag
{
    private readonly Dictionary<string, object> _values = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>参数绑定：参数名 -> (源算子 uuid, 变量名)。对应原版 parameters.bind。</summary>
    public Dictionary<string, (string Uuid, string Var)> Bindings { get; } =
        new(System.StringComparer.Ordinal);

    public Dictionary<string, IReadOnlyList<OutputPathPart>> BindingPaths { get; } =
        new(System.StringComparer.Ordinal);

    public Dictionary<string, AiVisionStudio.Models.ImageInputReference> ImageInputs { get; } = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _imageInputIndices = new(StringComparer.Ordinal);
    internal void SetImageInputIndex(string path, int index) => _imageInputIndices[path] = index;

    public OpContext Context { get; set; }

    public static ParamBag FromYaml(YamlNode parameters)
        => FromYaml(parameters, null, null);

    public static ParamBag FromYaml(YamlNode parameters, YamlNode bindNode, OpContext ctx)
    {
        var bag = new ParamBag { Context = ctx };
        if (parameters is YamlMappingNode root)
        {
            var value = Child(root, "value") as YamlMappingNode ?? root;
            foreach (var kv in value.Children)
                bag._values[kv.Key.ToString()] = Convert(kv.Value);

            // parameters.bind：参数 -> 源算子输出（支持嵌套，如 data0.Content）
            var bind = bindNode as YamlMappingNode ?? Child(root, "bind") as YamlMappingNode;
            if (bind != null) WalkBind(bind, bag, "");
            if (Child(root, "image_inputs") is YamlMappingNode images)
                foreach (var pair in images.Children)
                    if (pair.Value is YamlMappingNode source)
                        bag.ImageInputs[pair.Key.ToString()] = new(ScalarString(source, "operation"), ScalarString(source, "output") ?? "", ScalarString(source, "parameter") ?? "");
        }
        return bag;
    }

    /// <summary>bind 里声明为全局变量的参数名。</summary>
    public Dictionary<string, string> Globals { get; } = new(System.StringComparer.Ordinal);
    public Dictionary<string, int> GlobalIndices { get; } = new(System.StringComparer.Ordinal);

    private static void WalkBind(YamlMappingNode node, ParamBag bag, string prefix)
    {
        foreach (var kv in node.Children)
        {
            var name = prefix + kv.Key.ToString();
            if (kv.Value is not YamlMappingNode m) continue;

            if (ScalarBool(m, "_global"))
            {
                bag.Globals[name] = ScalarString(m, "_variable");
                if (int.TryParse(ScalarString(m, "_index"), out var globalIndex) && globalIndex >= 0)
                    bag.GlobalIndices[name] = globalIndex;
            }
            else if (ScalarBool(m, "_binded"))
            {
                var target = Child(m, "_target") as YamlMappingNode;
                var uuid = ScalarString(target, "_uuid");
                var vars = Child(target, "_variable") as YamlSequenceNode;
                var path = new List<OutputPathPart>();
                if (vars != null)
                    foreach (var child in vars.Children)
                    {
                        if (child is not YamlMappingNode part || string.IsNullOrEmpty(ScalarString(part, "_name")))
                            throw new InvalidOperationException($"绑定路径无效：{name}");
                        int? index = null;
                        if (ScalarBool(part, "_array"))
                        {
                            if (!int.TryParse(ScalarString(part, "_index"), out var parsed) || parsed < 0)
                                throw new InvalidOperationException($"绑定数组索引无效：{name}");
                            index = parsed;
                        }
                        path.Add(new OutputPathPart(ScalarString(part, "_name"), index));
                    }
                if (string.IsNullOrEmpty(uuid) || path.Count == 0)
                    throw new InvalidOperationException($"绑定目标无效：{name}");
                bag.Bindings[name] = (uuid, path[0].Name);
                bag.BindingPaths[name] = path;
            }
            else
            {
                WalkBind(m, bag, name + ".");
            }
        }
    }

    private static bool ScalarBool(YamlMappingNode node, string key)
    {
        var v = Child(node, key) as YamlScalarNode;
        return v != null && bool.TryParse(v.Value, out var b) && b;
    }

    private static string ScalarString(YamlMappingNode node, string key)
        => (Child(node, key) as YamlScalarNode)?.Value;

    /// <summary>若该参数绑定了某个算子的输出（或全局变量），取出该对象。</summary>
    public object Bound(string key)
    {
        if (Context == null) return null;
        if (Bindings.TryGetValue(key, out var b))
        {
            if (!Context.OpOutputs.ContainsKey(b.Uuid))
                throw new InvalidOperationException($"绑定源算子未产生输出：{key} <- {b.Uuid}.{b.Var}");
            var value = BindingPaths.TryGetValue(key, out var path)
                ? Context.GetBoundOutput(b.Uuid, path) : Context.GetBoundOutput(b.Uuid, b.Var);
            return value;
        }
        if (Globals.TryGetValue(key, out var gname))
        {
            if (!Context.TryGetVariable(gname, out var value)) throw new InvalidOperationException($"全局绑定输入不存在：{key} <- {gname}");
            if (!GlobalIndices.TryGetValue(key, out var index)) return value;
            if (value is not System.Collections.IList array || index >= array.Count)
                throw new InvalidOperationException($"全局绑定数组索引越界：{key} <- {gname}[{index}]");
            return array[index];
        }
        return null;
    }

    /// <summary>取输入值：依次尝试 参数名 / name.Value / name.Content / 全局变量。</summary>
    public object Input(string name)
    {
        foreach (var key in new[] { name, name + ".Value", name + ".Content" })
        {
            if (Bindings.ContainsKey(key) || Globals.ContainsKey(key)) return Bound(key);
        }
        if (Context != null && Context.Globals.TryGet(name, out var global)) return global;
        var raw = Raw(name);
        if (raw is IReadOnlyDictionary<string, object> group)
        {
            if (group.TryGetValue("Value", out var value)) return value;
            if (group.TryGetValue("Content", out var content)) return content;
        }
        return raw;
    }

    /// <summary>读取字符串数组参数（如 _input_list）。</summary>
    public List<string> StringList(string key)
    {
        var r = new List<string>();
        if (Raw(key) is List<object> list)
            foreach (var o in list)
                if (o != null) r.Add(o.ToString());
        return r;
    }

    private static object Convert(YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode s:
                var v = s.Value ?? "";
                if (bool.TryParse(v, out var b)) return b;
                if (long.TryParse(v, out var l)) return l;
                if (double.TryParse(v, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
                return v;
            case YamlMappingNode m:
                var dict = new Dictionary<string, object>(System.StringComparer.OrdinalIgnoreCase);
                foreach (var kv in m.Children) dict[kv.Key.ToString()] = Convert(kv.Value);
                return dict;
            case YamlSequenceNode seq:
                var list = new List<object>();
                foreach (var c in seq.Children) list.Add(Convert(c));
                return list;
            default:
                return null;
        }
    }

    private static YamlNode Child(YamlMappingNode node, string key)
    {
        if (node == null) return null;
        foreach (var kv in node.Children)
            if (kv.Key.ToString() == key) return kv.Value;
        return null;
    }

    public bool Has(string key) => _values.ContainsKey(key) || Bindings.ContainsKey(key) || Globals.ContainsKey(key);

    public object Raw(string key) => ResolveValue(key, _values.TryGetValue(key, out var value) ? value : null);

    private object ResolveValue(string path, object fallback)
    {
        if (_imageInputIndices.TryGetValue(path, out var imageIndex)) return imageIndex;
        if (Bindings.ContainsKey(path) || Globals.ContainsKey(path)) return Bound(path);
        if (!Bindings.Keys.Any(k => k.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase))
            && !Globals.Keys.Any(k => k.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase))) return fallback;
        if (fallback is IReadOnlyDictionary<string, object> group)
            return group.ToDictionary(kv => kv.Key, kv => ResolveValue(path + "." + kv.Key, kv.Value), StringComparer.OrdinalIgnoreCase);
        if (fallback is List<object> list)
            return list.Select((value, index) => ResolveValue(path + "." + index, value)).ToList();
        return fallback;
    }

    public string Str(string key, string def = "")
        => Raw(key)?.ToString() ?? def;

    public int Int(string key, int def = 0)
        => Raw(key) is {} v && int.TryParse(v.ToString(), out var i) ? i : def;

    public double Dbl(string key, double def = 0)
        => Raw(key) is {} v &&
           double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any,
               System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : def;

    public bool Bool(string key, bool def = false)
        => Raw(key) is {} v && bool.TryParse(v.ToString(), out var b) ? b : def;

    public IReadOnlyDictionary<string, object> Group(string key)
        => Raw(key) as IReadOnlyDictionary<string, object>;

    public double GroupDbl(string group, string key, double def = 0)
    {
        var g = Group(group);
        if (g == null || !g.TryGetValue(key, out var v) || v == null) return def;
        return double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : def;
    }
}

/// <summary>运行上下文。</summary>
public class OpContext
{
    public IReadOnlyList<AiVisionStudio.Services.DeviceProfile> Devices { get; set; } = Array.Empty<AiVisionStudio.Services.DeviceProfile>();
    public AiVisionStudio.Services.DeviceRuntime DeviceRuntime {get;internal set;}=new();
    public ImagePool Images { get; } = new();
    /// <summary>点云池（3D 算子用，编号与图像独立）。</summary>
    public CloudPool Clouds { get; } = new();
    public void SetImage(int index,OpenCvSharp.Mat image)
    {Images.Set(index,image);Clouds.Remove(index);Profiles.Remove(index);}
    public Dictionary<int, ProfileData> Profiles { get; } = new();
    public VarPool Vars { get; set; } = new();
    /// <summary>工程级全局变量（programs.variables）。</summary>
    public VarPool Globals { get; set; } = new(caseSensitive: true);

    /// <summary>Lua DrawText3D 写入的叠加文本（跨算子保留）。</summary>
    public List<string> DrawTexts { get; } = new();

    /// <summary>DrawText3D 的完整标注（位置/字号/颜色），供 3D 视图渲染。</summary>
    public List<Text3D> Texts { get; } = new();

    /// <summary>3D 区域包围盒，供 3D 视图画线框。</summary>
    public List<Box3> Boxes { get; } = new();
    /// <summary>Lossless native typed overlay records, including shapes without a local renderer yet.</summary>
    public List<NativeOverlay3D> NativeOverlays { get; } = new();
    public Dictionary<string, VarPool> OpOutputs { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string,string> NamedOutputSources { get; } = new(StringComparer.Ordinal);
    public bool TryGetVariable(string name,out object value)
    {
        if(Globals.TryGet(name,out value))return true;
        foreach(var entry in NamedOutputSources.OrderByDescending(k=>k.Key.Length))
        {
            if(!name.StartsWith(entry.Key+".",StringComparison.Ordinal)||!OpOutputs.TryGetValue(entry.Value,out var output))continue;
            OutputRead?.Invoke(entry.Value);
            var path=name[(entry.Key.Length+1)..];
            if(output.TryGet(path,out value))return true;
            value=OutputBindingResolver.Resolve(output,path.Split('.').Select(s=>new OutputPathPart(s)).ToArray());return true;
        }
        value=null;return false;
    }
    public string ProjectDir { get; set; } = "";
    internal string OperationUuid { get; set; } = "";
    internal event Action<string> OutputRead;
    internal Dictionary<string,string> InputFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string,string> SelectedInputFiles { get; } = new(StringComparer.Ordinal);
    /// <summary>Optional write boundary for verification against copied projects.</summary>
    public string WriteRoot { get; set; }
    /// <summary>当前工程的 recipe 文件（json，供 Lua GetRecipeSet 使用）。</summary>
    public string RecipePath { get; set; } = "";
    public Dictionary<string, string> Recipes { get; } = new(StringComparer.Ordinal);
    public Action<string, bool> Log { get; set; } = (_, _) => { };
    public System.Threading.CancellationToken CancellationToken { get; set; }

    /// <summary>取某个算子的输出变量。</summary>
    public object GetBoundOutput(string uuid, string varName)
    {
        OutputRead?.Invoke(uuid);
        if (string.IsNullOrEmpty(uuid) || !OpOutputs.TryGetValue(uuid, out var pool)) return null;
        return pool.TryGet(varName, out var value) ? value : throw new InvalidOperationException($"绑定输出不存在：{varName}");
    }

    public object GetBoundOutput(string uuid, IReadOnlyList<OutputPathPart> path)
    {
        OutputRead?.Invoke(uuid);
        if (string.IsNullOrEmpty(uuid) || !OpOutputs.TryGetValue(uuid, out var pool)) return null;
        return OutputBindingResolver.Resolve(pool, path);
    }

    /// <summary>所有算子的输出合并（全局变量面板用）。</summary>
    public IEnumerable<KeyValuePair<string, object>> AllVars()
    {
        foreach (var pool in OpOutputs.Values)
            foreach (var kv in pool.All)
                yield return kv;
    }

    public string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(ProjectDir, path));
    }

    public string ResolveOutputPath(string path)
    {
        var resolved = ResolvePath(path);
        if (resolved == null || string.IsNullOrEmpty(WriteRoot)) return resolved;
        var full = Path.GetFullPath(resolved);
        var boundary = Path.GetFullPath(WriteRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"输出路径超出验证目录：{full}");
        return full;
    }
}

/// <summary>算子输出接口。</summary>
public interface IOperator
{
    string Driver { get; }
    bool Run(OpContext ctx, ParamBag p);
}

// ---------------- 几何对象（算子间传递用） ----------------

/// <summary>线段输出（对应原版变量 Segment）。</summary>
public class SegmentObj
{
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public double Angle => Math.Atan2(Y2 - Y1, X2 - X1) * 180 / Math.PI;
    public double Length => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1));
    public override string ToString() => $"({X1:0.00},{Y1:0.00})-({X2:0.00},{Y2:0.00})";
}

/// <summary>圆输出（对应原版变量 Circle）。</summary>
public class CircleObj
{
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double Radius { get; set; }
    public override string ToString() => $"C({CenterX:0.00},{CenterY:0.00}) R={Radius:0.00}";
}

/// <summary>点输出（对应原版变量 Point）。</summary>
public class PointObj
{
    public double X { get; set; }
    public double Y { get; set; }
    public override string ToString() => $"({X:0.00},{Y:0.00})";
}

/// <summary>3D 场景里的文字标注。</summary>
public sealed class Text3D
{
    public string Owner = "";
    public double X, Y, Z, Size;
    public bool SizeIsPixels;
    public string Text = "";
    public string Color = "green";
    /// <summary>Target IM slot; -1 preserves legacy annotations shared by all slots.</summary>
    public int ViewIndex = -1;
}

/// <summary>3D 包围盒（区域 / 斑点）。</summary>
public sealed class Box3
{
    public double X1, Y1, Z1, X2, Y2, Z2;
    public string Color = "yellow";
    public int ViewIndex = -1;
    public string Label = "";
    /// <summary>Face opacity, 0 for a wireframe. Detection results may request translucent faces.</summary>
    public double FillOpacity;
}

/// <summary>The original renderer's decoded fields, retained alongside rendered boxes/text.</summary>
public sealed class NativeOverlay3D
{
    public string Type { get; set; } = "";
    public int ViewIndex { get; set; } = -1;
    public Dictionary<string, object> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public PointCloud Cloud { get; set; }
}
