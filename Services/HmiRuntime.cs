using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Algorithm.Lua;
using AiVisionStudio.Models;
using AiVisionStudio.Views;
using OpenCvSharp;
using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Services;

public partial class MainViewModel
{
    private CancellationTokenSource _cycleCancellation;
    public bool IsCycling => _cycleCancellation != null;
    internal OpContext HmiContext => _engine.Context;

    public async Task RunContinuouslyAsync()
    {
        if (IsRunning || IsCycling || Operations.Count == 0 || !ApplyParameters()) return;
        using var cancellation = new CancellationTokenSource();
        _cycleCancellation = cancellation;
        OnPropertyChanged(nameof(IsCycling));
        OnPropertyChanged(nameof(CanEdit));
        SynchronizeParameterTree();
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await RunInteractiveAsync();
                if (IsExecutionPaused || Status.StartsWith("运行失败", StringComparison.Ordinal)) break;
                await Task.Delay(30, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            await OnUiAsync(() =>
            {
                _cycleCancellation = null;
                OnPropertyChanged(nameof(IsCycling));
                OnPropertyChanged(nameof(CanEdit));
                SynchronizeParameterTree();
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            });
        }
    }

    internal void NotifyHmiImageChanged() => ImageChanged?.Invoke(this, EventArgs.Empty);

    internal void ClearHmiResults()
    {
        if (!CanEdit) throw new InvalidOperationException("请先停止运行再清空结果");
        var globals = _engine.Context.Globals.All.ToArray();
        _engine.Reset();
        ClearVisualSnapshots();
        InvalidateNativeExecution();
        foreach (var value in globals) _engine.Context.Globals.Set(value.Key, value.Value);
        foreach (var op in Programs.SelectMany(p => p.Operations))
        { op.State = op.Enabled ? OpState.None : OpState.Disabled; op.ErrorMessage = null; op.ElapsedMs = 0; }
        RefreshGlobalVars(); RefreshSelectedOutputs();
        RunTimeMilliseconds = 0;
        RunTimeText = "运行时间:0 ms";
        Status = "运行结果已清空";
        NotifyHmiImageChanged();
    }

    internal bool SetHmiVariable(string name, object value, out string error)
    {
        error = "";
        if (!CanEdit) { error = "请先暂停运行再修改变量"; return false; }
        var variable = SelectedProgram?.Variables.FirstOrDefault(v => v.Name == name);
        if (variable == null && !_engine.Context.Globals.All.ContainsKey(name))
        { error = "绑定的全局变量不存在：" + name; return false; }
        var previous = _engine.Context.Globals.Get(name);
        var type = variable?.Type ?? GlobalValue.InferType(previous);
        if(type==null){error="变量类型无法确定，请先在全局变量中声明类型："+name;return false;}
        var text = value is IList ? GlobalValue.Text(value) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        object parsed;
        if (type.EndsWith("[]", StringComparison.Ordinal))
        {
            try { parsed=GlobalValue.Parse(type,text); }
            catch(ArgumentException ex) { error=ex.Message; return false; }
        }
        else
        switch (type.ToLowerInvariant())
        {
            case "int":
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                { error = "请输入 32 位整数"; return false; }
                parsed = integer; break;
            case "float": case "double":
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                { error = "请输入有限数值"; return false; }
                parsed = number; break;
            case "bool":
                if (text is "1" or "0") parsed = text == "1";
                else if (bool.TryParse(text, out var boolean)) parsed = boolean;
                else { error = "请输入 true 或 false"; return false; }
                break;
            default: parsed = text; break;
        }
        if (UseNativeKernel && !WriteNativeGlobal(name, type, parsed, out error)) return false;
        if (UseNativeKernel && variable != null)
        {
            var defaultText = parsed is bool boolean ? boolean ? "true" : "false" : Convert.ToString(parsed, CultureInfo.InvariantCulture) ?? "";
            if (variable.Default != defaultText)
            {
                BeforeEdit();
                foreach (var program in Programs)
                {
                    foreach (var definition in program.Variables.Where(item => item.Name == name)) definition.Default = defaultText;
                    if (HmiYamlGet(program.Root, "programs") is YamlMappingNode rawPrograms && HmiYamlGet(rawPrograms, "variables") is YamlSequenceNode definitions)
                        foreach (var definition in definitions.Children.OfType<YamlMappingNode>())
                            if ((HmiYamlGet(definition, "name") as YamlScalarNode)?.Value == name)
                                definition.Children[new YamlScalarNode("default")] = new YamlScalarNode(defaultText);
                }
                AfterEdit();
            }
        }
        _engine.Context.Globals.Set(name, parsed);
        RefreshGlobalVars();
        return true;
    }

    internal bool SaveHmiParameters()
    {
        if (!CanEdit || !ApplyParameters()) return false;
        BeforeEdit();
        foreach (var program in Programs)
        {
            foreach (var variable in program.Variables)
            {
                var value = _engine.Context.Globals.Get(variable.Name);
                variable.Default = value is bool b ? (b ? "true" : "false") : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            }
            if (HmiYamlGet(program.Root, "programs") is not YamlMappingNode programs ||
                HmiYamlGet(programs, "variables") is not YamlSequenceNode variables) continue;
            foreach (var node in variables.Children.OfType<YamlMappingNode>())
            {
                var variable = program.Variables.FirstOrDefault(v => v.Name == (HmiYamlGet(node, "name") as YamlScalarNode)?.Value);
                if (variable != null) node.Children[new YamlScalarNode("default")] = new YamlScalarNode(variable.Default);
            }
        }
        AfterEdit();
        return TrySave();
    }
    private static YamlNode HmiYamlGet(YamlMappingNode node, string key)
        => node != null && node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
}

/// <summary>Snapshots completed runs so WPF never reads a pool while an operator replaces its data.</summary>
public sealed class HmiRuntimeBridge : IHmiRuntimeContext, IDisposable
{
    private readonly MainViewModel _vm;
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Mat> _images = new();
    private readonly Dictionary<int, PointCloud> _clouds = new();
    private readonly Dictionary<int, ProfileData> _profiles = new();
    private readonly List<NativeOverlay3D> _nativeOverlays = new();
    private readonly List<Box3> _boxes = new();
    private readonly List<Text3D> _texts = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private bool _disposed;
    private string _disconnectedReason;
    private bool _capturePending;
    public HmiRuntimeBridge(MainViewModel vm)
    {
        _vm = vm;
        vm.ImageChanged += OnCompleted;
        vm.PropertyChanged += OnPropertyChanged;
        if (vm.IsRunning) _capturePending = true;
        else Capture();
    }
    public string Status => _disconnectedReason ?? _vm.Status;
    public bool IsRunning => !_disposed && (_vm.IsRunning || _vm.IsCycling);
    public IReadOnlyList<string> BindingReferences()
    {
        if (_disposed) return [];
        var references = new HashSet<string>(_values.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var program in _vm.Programs)
            foreach (var variable in program.Variables) references.Add(variable.Name);
        // Expand completed output arrays and records with the native zero-based selector notation.
        void Expand(string prefix, object value, int depth)
        {
            if (depth > 3 || references.Count >= 3000) return;
            if (value is IDictionary<string, object> map)
                foreach (var item in map.Take(100)) { var key = prefix + "." + item.Key; references.Add(key); Expand(key, item.Value, depth + 1); }
            else if (value is IList list)
                for (var i = 0; i < Math.Min(list.Count, 100); i++) { var key = prefix + "[" + i + "]"; references.Add(key); Expand(key, list[i], depth + 1); }
        }
        foreach (var item in _values) Expand(item.Key, item.Value, 0);
        return references.OrderBy(s => s.Contains('&')).ThenBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public event EventHandler Changed;
    public event Action<HmiElement> OperatorEditorRequested;
    public IReadOnlyList<string> ProgramNames() => _disposed ? [] : _vm.Programs.Select(program => program.Name).ToArray();
    public IReadOnlyList<(string Id, string Name)> ProgramOperators(string program)
        => _disposed ? [] : _vm.Programs.FirstOrDefault(p => p.Name == program)?.Operations.Select(op => (op.Uuid, op.Name)).ToArray() ?? [];
    private void OnCompleted(object sender, EventArgs e) { if (_disposed) return; Capture(); Changed?.Invoke(this, EventArgs.Empty); }
    private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.PropertyName == nameof(MainViewModel.IsRunning))
        {
            if (_vm.IsRunning) _capturePending = true;
            else if (_capturePending) Capture();
        }
        if (e.PropertyName is nameof(MainViewModel.IsRunning) or nameof(MainViewModel.IsCycling) or nameof(MainViewModel.Status))
            Changed?.Invoke(this, EventArgs.Empty);
    }
    private void Capture()
    {
        if (_disposed) return;
        _capturePending = false;
        _values.Clear();
        foreach (var pair in _vm.HmiContext.Globals.All) _values[pair.Key] = Snapshot(pair.Value);
        foreach (var pair in _vm.HmiContext.OpOutputs)
            foreach (var output in pair.Value.All) _values[pair.Key + "&" + output.Key] = Snapshot(output.Value);
        foreach (var image in _images.Values) image.Dispose();
        _images.Clear();
        foreach (var image in _vm.Images.All)
            if (image.Value != null && !image.Value.IsDisposed && !image.Value.Empty()) _images[image.Key] = image.Value.Clone();
        _clouds.Clear();
        foreach (var cloud in _vm.Clouds.All) _clouds[cloud.Key] = cloud.Value?.Clone();
        _profiles.Clear(); foreach (var profile in _vm.Profiles) _profiles[profile.Key] = profile.Value.Clone();
        _nativeOverlays.Clear();
        foreach (var overlay in _vm.NativeOverlays)
            _nativeOverlays.Add(new NativeOverlay3D { Type = overlay.Type, ViewIndex = overlay.ViewIndex,
                Values = (Dictionary<string, object>)Snapshot(overlay.Values), Cloud = overlay.Cloud?.Clone() });
        _boxes.Clear();
        foreach (var box in _vm.BoxOverlays)
            _boxes.Add(new Box3 { X1 = box.X1, Y1 = box.Y1, Z1 = box.Z1, X2 = box.X2, Y2 = box.Y2, Z2 = box.Z2,
                Color = box.Color, ViewIndex = box.ViewIndex, Label = box.Label, FillOpacity = box.FillOpacity });
        _texts.Clear();
        foreach (var text in _vm.TextOverlays)
            _texts.Add(new Text3D { X = text.X, Y = text.Y, Z = text.Z, Size = text.Size, Text = text.Text,
                Color = text.Color, ViewIndex = text.ViewIndex, SizeIsPixels = text.SizeIsPixels });
    }
    private static object Snapshot(object value)
    {
        if (value is JsonNode node) return JsonValueToObject(node);
        if (value is LuaTable lua)
        {
            if (lua.Length > 0) return Enumerable.Range(1, lua.Length).Select(i => Snapshot(lua.Get((double)i))).ToArray();
            return lua.Map.ToDictionary(p => p.Key.ToString(), p => Snapshot(p.Value));
        }
        if (value is IDictionary dictionary)
        {
            var copy = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary) copy[entry.Key.ToString()] = Snapshot(entry.Value);
            return copy;
        }
        if (value is IEnumerable sequence && value is not string) return sequence.Cast<object>().Select(Snapshot).ToArray();
        return value;
    }
    public object Read(string reference)
    {
        if (_disposed) return null;
        if (string.IsNullOrWhiteSpace(reference)) return null;
        if (reference == "$recipe") return LoadRecipe() is JsonObject recipe ? JsonValueToObject(recipe["recipe"]) : null;
        if (reference == "$runtime") return _vm.RunTimeMilliseconds;
        if (reference == "$cpu") return _vm.CpuText;
        if (reference == "$memory") return _vm.MemText;
        if (reference == "$logs") return _vm.Logs.Select(l => (object)new Dictionary<string, object> { ["Time"] = l.Time, ["Level"] = l.Level, ["Message"] = l.Message }).ToArray();
        if (_values.TryGetValue(reference, out var exact)) return exact;
        if (!_vm.UseNativeKernel)
        {
            var match = _values.FirstOrDefault(pair => string.Equals(pair.Key, reference, StringComparison.OrdinalIgnoreCase));
            if (match.Key != null) return match.Value;
        }
        // Selectors are native zero-based array indices, e.g. UUID&pin_position_x[0].
        var split = reference.IndexOfAny(['[', '.']);
        if (split < 0 || !_values.TryGetValue(reference[..split], out var result)) return null;
        var suffix = reference[split..];
        var matches = Regex.Matches(suffix, @"\[(\d+)\]|\.([^\.\[\]]+)");
        if (string.Concat(matches.Select(m => m.Value)) != suffix) return null;
        foreach (Match match in matches)
        {
            if (match.Groups[1].Success)
            {
                if (!int.TryParse(match.Groups[1].Value, out var i) || result is not IList list || i >= list.Count) return null;
                result = list[i];
            }
            else if (result is IDictionary<string, object> map && map.TryGetValue(match.Groups[2].Value, out var field)) result = field;
            else return null;
        }
        return result;
    }
    public bool Write(string reference, object value, out string error)
    {
        if (_disposed) { error = _disconnectedReason ?? "HMI 已关闭"; return false; }
        if (string.IsNullOrWhiteSpace(reference)) { error = "尚未绑定变量"; return false; }
        if (reference.Contains('&') || reference.Contains('[')) { error = "算子输出为只读，请绑定可写的全局变量"; return false; }
        if (!_vm.SetHmiVariable(reference, value, out error)) return false;
        _values[reference] = Snapshot(_vm.HmiContext.Globals.Get(reference));
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
    public Mat Image(int index) => _images.GetValueOrDefault(index);
    public PointCloud Cloud(int index) => _clouds.GetValueOrDefault(index);
    public ProfileData Profile(int index) => _profiles.GetValueOrDefault(index);
    public IReadOnlyList<NativeOverlay3D> NativeOverlays(int index) => _nativeOverlays.Where(o => o.ViewIndex < 0 || o.ViewIndex == index).ToArray();
    public IReadOnlyList<Box3> BoxOverlays(int index) => _boxes.Where(o => o.ViewIndex < 0 || o.ViewIndex == index).ToArray();
    public IReadOnlyList<Text3D> TextOverlays(int index) => _texts.Where(o => o.ViewIndex < 0 || o.ViewIndex == index).ToArray();

    public async Task ExecuteAsync(string action, HmiElement element = null)
    {
        if (_disconnectedReason != null) throw new InvalidOperationException(_disconnectedReason);
        if (_disposed) return;
        switch (action)
        {
            case "runProgram":
            {
                if (!_vm.CanEdit) throw new InvalidOperationException("请先停止当前运行。");
                var programName = element?.GetString("option.bindPrograms");
                var program = _vm.Programs.FirstOrDefault(p => p.Name == programName) ?? throw new InvalidOperationException("未找到绑定的子程序：" + programName);
                if (!_vm.ApplyParameters()) throw new InvalidOperationException("请先修正未完成的参数编辑。");
                var previousProgram = _vm.SelectedProgram; var previousOperation = _vm.SelectedOperation;
                try { _vm.SelectedProgram = program; await _vm.RunInteractiveAsync(); }
                finally { if (_vm.CanEdit) { _vm.SelectedProgram = previousProgram; _vm.SelectedOperation = previousOperation; } }
                break;
            }
            case "openOperator":
            {
                if (!_vm.CanEdit) throw new InvalidOperationException("请先停止当前运行。");
                var programName = element?.GetString("option.bindPrograms"); var id = element?.GetString("option.bindOperatorUUID");
                if (!_vm.Programs.Any(p => p.Name == programName && p.Operations.Any(op => op.Uuid == id))) throw new InvalidOperationException("未找到绑定的程序或算子。");
                if (OperatorEditorRequested == null) throw new InvalidOperationException("当前 HMI 没有连接工具参数编辑器。");
                OperatorEditorRequested.Invoke(element); break;
            }
            case "run": case "nextSolid": case "singleStepOperation":
                if (!_vm.IsCycling) await _vm.RunInteractiveAsync(); break;
            case "step": await _vm.RunInteractiveAsync(true); break;
            case "pause": case "stop": case "pauseCircleSolid": case "suspend": _vm.StopCommand.Execute(null); break;
            case "cycle": case "syncRounded": case "cycleOperation": await _vm.RunContinuouslyAsync(); break;
            case "save": case "saveParams":
                if (!_vm.SaveHmiParameters()) throw new InvalidOperationException(_vm.Status);
                break;
            case "clear": case "TrashAltRegular": case "clearProcedureState": _vm.ClearHmiResults(); break;
            case "importImage":
                if (IsRunning) throw new InvalidOperationException("请先停止运行再上传图片");
                var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "图像|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|所有文件|*.*" };
                if (dialog.ShowDialog() == true)
                {
                    var mat = Cv2.ImRead(dialog.FileName, ImreadModes.Unchanged);
                    if (mat.Empty()) { mat.Dispose(); throw new InvalidDataException("无法读取图像"); }
                    _vm.Images.Set(SceneIndex(element), mat);
                    _vm.NotifyHmiImageChanged();
                }
                break;
            case "saveRecipe":
                if (IsRunning) throw new InvalidOperationException("请先停止运行再保存配方");
                var recipe = LoadRecipe() ?? throw new InvalidDataException("工程未选择有效配方");
                var rows = element?.Option?["dataset"]?["data"] as JsonArray ?? throw new InvalidDataException("配方数据为空");
                recipe["recipe"] = MergeRecipeRows(recipe["recipe"] as JsonArray, rows);
                var path = _vm.HmiContext.RecipePath;
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, recipe.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    File.Move(temporary, path, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                _vm.Status = "配方已保存：" + Path.GetFileName(path);
                break;
            case "mes": case "MESButton": case "mesRequest": await SendMesAsync(element); break;
            case "lens": break;
            default: throw new NotSupportedException("尚未实现的 HMI 动作：" + action);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private static int SceneIndex(HmiElement element)
    {
        var value = element?.Option?["sceneIM"]?.ToString() ?? "0";
        return int.TryParse(value.Replace("IM", "", StringComparison.OrdinalIgnoreCase), out var index) ? Math.Clamp(index, 0, 31) : 0;
    }
    private JsonObject LoadRecipe()
    {
        var path = _vm.HmiContext.RecipePath;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
    }
    private static JsonArray MergeRecipeRows(JsonArray original, JsonArray edited)
    {
        if (original != null && edited.Count < original.Count)
            throw new InvalidDataException("配方行数不完整，保存已取消");
        var result = new JsonArray();
        for (int index = 0; index < edited.Count; index++)
        {
            if (edited[index] is not JsonObject values) throw new InvalidDataException("配方每行必须为字段集合");
            var previous = original?.ElementAtOrDefault(index) as JsonObject;
            var row = previous?.DeepClone() as JsonObject ?? new JsonObject();
            foreach (var field in values)
            {
                var kind = previous?[field.Key]?.GetValueKind();
                var text = field.Value?.ToString() ?? "";
                if (kind == JsonValueKind.Number)
                {
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                        throw new InvalidDataException($"配方第 {index + 1} 行 {field.Key} 必须为数值");
                    row[field.Key] = number;
                }
                else if (kind is JsonValueKind.True or JsonValueKind.False)
                {
                    if (!bool.TryParse(text, out var boolean)) throw new InvalidDataException($"配方第 {index + 1} 行 {field.Key} 必须为布尔值");
                    row[field.Key] = boolean;
                }
                else row[field.Key] = field.Value?.DeepClone();
            }
            result.Add(row);
        }
        return result;
    }
    public IReadOnlyList<IReadOnlyDictionary<string, object>> QueryTable(string name, DateTime? from = null, DateTime? to = null)
        => _disposed ? [] : HmiDatabase.Read(_vm.UseNativeKernel && _vm.NativeDatabasePath != null ? _vm.NativeDatabasePath
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_vm.ProjectPath.Length == 0 ? "new.aivs" : _vm.ProjectPath))!, "data.db"), name, from, to);

    private async Task SendMesAsync(HmiElement element)
    {
        var option = element?.Option ?? throw new InvalidDataException("MES配置为空");
        var address = option["requestURL"]?.ToString() ?? "";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidDataException("MES地址无效");
        var parameters = new JsonObject();
        if (option["requestParams"] is JsonArray array)
            foreach (var item in array.OfType<JsonObject>())
            {
                var key = item["key"]?.ToString();
                if (string.IsNullOrEmpty(key)) continue;
                var binding = item["bindParams"]?.ToString();
                parameters[key] = string.IsNullOrEmpty(binding) ? item["value"]?.DeepClone() : JsonSerializer.SerializeToNode(Read(binding));
            }
        // The bundled example addresses the original application's own variable-list endpoint.
        if (uri.IsLoopback && uri.AbsolutePath == "/api/dev/project/variables/global" && parameters["command"]?.ToString() == "list")
        {
            _vm.Status = "MES 全局变量：" + JsonSerializer.Serialize(_values.Where(p => !p.Key.Contains('&')).ToDictionary(p => p.Key, p => p.Value));
            return;
        }
        var method = new HttpMethod(option["requestType"]?.ToString() ?? "POST");
        if (method == HttpMethod.Get && parameters.Count > 0)
        {
            var builder = new UriBuilder(uri);
            var query = string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value?.ToString() ?? "")));
            builder.Query = string.IsNullOrEmpty(builder.Query) ? query : builder.Query.TrimStart('?') + "&" + query;
            uri = builder.Uri;
        }
        using var request = new HttpRequestMessage(method, uri);
        if (method != HttpMethod.Get) request.Content = new StringContent(parameters.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        _vm.Status = $"MES {(int)response.StatusCode}：{body[..Math.Min(body.Length, 2000)]}";
    }
    private static object JsonValueToObject(JsonNode node) => node switch
    {
        JsonObject obj => obj.ToDictionary(p => p.Key, p => JsonValueToObject(p.Value)),
        JsonArray arr => arr.Select(JsonValueToObject).ToArray(),
        JsonValue value when value.TryGetValue<bool>(out var boolean) => boolean,
        JsonValue value when value.TryGetValue<double>(out var number) => number,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node?.ToString()
    };
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vm.ImageChanged -= OnCompleted;
        _vm.PropertyChanged -= OnPropertyChanged;
        foreach (var image in _images.Values) image.Dispose();
        _images.Clear();
        _values.Clear();
        _clouds.Clear();
        _profiles.Clear();
        _nativeOverlays.Clear(); _boxes.Clear(); _texts.Clear();
        _http.Dispose();
    }
    public void Disconnect(string reason)
    {
        _disconnectedReason = reason;
        Dispose();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
