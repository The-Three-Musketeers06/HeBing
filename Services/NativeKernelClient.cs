using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Models;
using OpenCvSharp;
using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Services;

public sealed record NativeOperationResult(string Uuid, int Result, double Milliseconds, JsonElement Message);
public sealed class NativeRunResult
{
    public Dictionary<string, NativeOperationResult> Operations { get; } = new();
    public Dictionary<string, JsonElement> RawOutputs { get; } = new();
    public double Milliseconds { get; internal set; }
    public bool Paused { get; internal set; }
    public int ProgramCounter { get; internal set; }
    public bool Completed { get; internal set; }
    public JsonElement RawGlobals { get; internal set; }
}

/// <summary>Normal local API of the installed 4.5.1 application. Never substitutes local algorithms.</summary>
public sealed class NativeKernelClient : IDisposable
{
    private readonly NativeKernelHost _host;
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(3) };
    private string _loadedKey;
    private string _activeProgram;
    private string _resourceDirectory = AppContext.BaseDirectory;
    private bool _hasBreakpoints;
    private int _testing;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly string _sessionDirectory;
    public Uri Endpoint => _host.Endpoint;
    public TimeSpan RunTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public NativeRunResult LastRun { get; private set; }
    public string PreparedProjectPath { get; private set; }
    public event Action<string, JsonElement> ProtocolMessage;

    public NativeKernelClient(NativeKernelHostOptions options = null, string sessionDirectory = null)
    {
        _host = new(options);
        _host.StateChanged += (state, _) => { if (state == NativeKernelHostState.Starting) Invalidate(); };
        _sessionDirectory = Path.Combine(sessionDirectory ?? Path.Combine(AppContext.BaseDirectory, "UserData", "NativeSessions"), Guid.NewGuid().ToString("N"));
    }

    public async Task<JsonElement> PostAsync(string path, object body, CancellationToken token = default)
    {
        var endpoint = await _host.StartAsync(token).ConfigureAwait(false);
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await _http.PostAsync(new Uri(endpoint, path), content, token).ConfigureAwait(false);
        // The original server omits a UTF-8 charset on some JSON responses. Decode bytes explicitly.
        var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        if (bytes.Length == 0) throw new HttpRequestException($"原版内核 {path} 返回空响应，HTTP {(int)response.StatusCode}");
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.TryGetProperty("code", out var code) && code.GetInt32() != 0)
            throw new InvalidOperationException($"原版内核 {path}：{code} {root.GetProperty("message").GetString()}");
        response.EnsureSuccessStatusCode();
        return root.TryGetProperty("data", out var data) ? data.Clone() : JsonSerializer.SerializeToElement(new { });
    }

    public async Task LoadAsync(string path, string program, CancellationToken token = default)
    {
        await PostAsync("/api/dev/project/manage", new { command = "load", path = Path.GetFullPath(path) }, token).ConfigureAwait(false);
        await PostAsync("/api/dev/project/program", new { command = "activate", name = program }, token).ConfigureAwait(false);
        _activeProgram = program;
        _resourceDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        LastRun = null;
    }

    public void Invalidate() { _loadedKey = null; LastRun = null; }

    public async Task PrepareAsync(IReadOnlyList<ProgramModel> programs, string projectPath, string selectedProgram,
        CancellationToken token = default)
    {
        await _host.StartAsync(token).ConfigureAwait(false);
        _hasBreakpoints = programs.FirstOrDefault(program => program.Name == selectedProgram)?.Operations.Any(operation => operation.Enabled && operation.Breakpoint) == true;
        var source = ProjectFile.Serialize(programs) ?? throw new InvalidDataException("工程结构为空");
        var key = projectPath + "\n" + source;
        if (_loadedKey != key)
        {
            var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(projectPath ?? "untitled")))[..16];
            var snapshotDirectory = Path.Combine(_sessionDirectory, identity);
            Directory.CreateDirectory(snapshotDirectory);
            var cloned = ProjectFile.Parse(source);
            var directory = string.IsNullOrEmpty(projectPath) ? AppContext.BaseDirectory : Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
            ResolveRelativeResources(cloned[0].Root, directory);
            // The original loader opens the project's HMI database at load time. Copy it before
            // loading the isolated document; creating it afterwards leaves native WriteHmiData uninitialized.
            var database = Path.Combine(directory, "data.db");
            var snapshotDatabase = Path.Combine(snapshotDirectory, "data.db");
            if (File.Exists(database) && !File.Exists(snapshotDatabase)) File.Copy(database, snapshotDatabase);
            // Native project loading may retain a read handle. Each edit/reopen gets a new
            // document file in the same database directory instead of replacing an open file.
            var snapshot = Path.Combine(snapshotDirectory, (string.IsNullOrEmpty(projectPath) ? "untitled" : Path.GetFileNameWithoutExtension(projectPath)) + "." + Guid.NewGuid().ToString("N") + ".aivs");
            ProjectFile.SaveAll(snapshot, cloned);
            await LoadAsync(snapshot, selectedProgram, token).ConfigureAwait(false);
            _resourceDirectory = directory;
            PreparedProjectPath = snapshot;
            _loadedKey = key;
        }
        else if (_activeProgram != selectedProgram)
        {
            await PostAsync("/api/dev/project/program", new { command = "activate", name = selectedProgram }, token).ConfigureAwait(false);
            _activeProgram = selectedProgram;
            LastRun = null;
        }
    }

    private static void ResolveRelativeResources(YamlNode node, string directory, string key = "", HashSet<string> fields = null)
    {
        fields ??= ResourceFields;
        if (node is YamlMappingNode mapping)
        {
            if (mapping.Children.TryGetValue(new YamlScalarNode("driver"), out var driver)) fields = FieldsForDriver(driver.ToString());
            foreach (var pair in mapping.Children)
            {
                var field = pair.Key.ToString();
                if (field is "bind" or "globals" || field.StartsWith('_')) continue;
                ResolveRelativeResources(pair.Value, directory, field, fields);
            }
        }
        else if (node is YamlSequenceNode sequence)
            foreach (var child in sequence.Children) ResolveRelativeResources(child, directory, key, fields);
        else if (node is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value) && !Path.IsPathRooted(scalar.Value)
            && fields.Contains(key))
        {
            // Only actual file parameters, never Lua/source-code strings or identifiers.
            if (scalar.Value.IndexOfAny(['\r', '\n', '\0']) < 0)
                scalar.Value = Path.GetFullPath(Path.Combine(directory, scalar.Value));
        }
    }

    private static readonly HashSet<string> ResourceFields = new(StringComparer.OrdinalIgnoreCase)
    { "File", "Files", "Filename", "Filepath", "Fullpath", "Path", "Directory", "Folder", "Model File", "Calibration File", "Data File", "Recipe File",
      "Theoretical Circle File", "Theoretical Cylinder File", "Theoretical Line File", "Theoretical Plane File", "Theoretical Position File", "Points File", "Calibration Directory", "Image X", "Image Y", "Image Z" };
    private static readonly Lazy<Dictionary<string, HashSet<string>>> DriverResourceFields = new(() =>
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "driver_defs.json");
        if (!File.Exists(path)) return result;
        using var catalog = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var driver in catalog.RootElement.EnumerateArray())
        {
            var fields = new HashSet<string>(StringComparer.Ordinal) { "path" };
            void Walk(JsonElement node, string name = "")
            {
                if (node.ValueKind == JsonValueKind.Object)
                {
                    if (node.TryGetProperty("name", out var label)) name = label.GetString();
                    if (node.TryGetProperty("type", out var type) && type.GetString() is "openfile" or "savefile" or "opendirectory") fields.Add(name);
                    foreach (var property in node.EnumerateObject()) Walk(property.Value, property.Name);
                }
                else if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) Walk(child, name);
            }
            if (driver.TryGetProperty("params", out var parameters)) Walk(parameters);
            result[driver.GetProperty("driver").GetString()!] = fields;
        }
        return result;
    });
    private static HashSet<string> FieldsForDriver(string driver) => DriverResourceFields.Value.TryGetValue(driver, out var fields) ? fields : ResourceFields;

    /// <summary>The original synchronous test API returns the test outputs in its data object.</summary>
    public async Task<JsonElement> TestAsync(string driverName, string uuid, object parameters, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);
        if (parameters is YamlNode yaml) parameters = YamlValue(yaml);
        var resolved = JsonSerializer.SerializeToNode(parameters);
        ResolveJsonResources(resolved, _resourceDirectory, fields: FieldsForDriver(driverName));
        if (!await _runGate.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("原版内核已有检测正在运行");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(RunTimeout);
        Volatile.Write(ref _testing, 1);
        try
        {
            return await PostAsync("/api/dev/project/control", new { command = "test", driver_name = driverName, uuid, parameters = resolved }, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopAsync().ConfigureAwait(false);
            if (!token.IsCancellationRequested) throw new TimeoutException($"原版算子测试超过 {RunTimeout.TotalSeconds:0} 秒，已发送停止命令");
            throw;
        }
        finally { Volatile.Write(ref _testing, 0); _runGate.Release(); }
    }

    private static void ResolveJsonResources(JsonNode node, string directory, string key = "", HashSet<string> fields = null)
    {
        fields ??= ResourceFields;
        if (node is JsonObject map)
            foreach (var pair in map.ToList())
            {
                if (pair.Key is "bind" or "globals" || pair.Key.StartsWith('_')) continue;
                if (pair.Value is JsonValue value && fields.Contains(pair.Key) && value.TryGetValue<string>(out var path)
                    && !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && path.IndexOfAny(['\r', '\n', '\0']) < 0)
                    map[pair.Key] = Path.GetFullPath(Path.Combine(directory, path));
                else ResolveJsonResources(pair.Value, directory, pair.Key, fields);
            }
        else if (node is JsonArray array)
            for (int index = 0; index < array.Count; index++)
                if (array[index] is JsonValue value && fields.Contains(key) && value.TryGetValue<string>(out var path)
                    && !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && path.IndexOfAny(['\r', '\n', '\0']) < 0)
                    array[index] = Path.GetFullPath(Path.Combine(directory, path));
                else ResolveJsonResources(array[index], directory, key, fields);
    }

    private static object YamlValue(YamlNode node) => node switch
    {
        YamlMappingNode mapping => mapping.Children.ToDictionary(pair => pair.Key.ToString(), pair => YamlValue(pair.Value)),
        YamlSequenceNode sequence => sequence.Children.Select(YamlValue).ToArray(),
        YamlScalarNode scalar when scalar.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted or YamlDotNet.Core.ScalarStyle.Literal or YamlDotNet.Core.ScalarStyle.Folded => scalar.Value,
        YamlScalarNode scalar when scalar.Value is null or "null" or "~" => null,
        YamlScalarNode scalar when bool.TryParse(scalar.Value, out var boolean) => boolean,
        YamlScalarNode scalar when long.TryParse(scalar.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var integer) => integer,
        YamlScalarNode scalar when double.TryParse(scalar.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) => number,
        YamlScalarNode scalar => scalar.Value,
        _ => throw new InvalidDataException("无法转换原版测试参数")
    };

    public async Task<JsonElement> ListGlobalsAsync(CancellationToken token = default)
    {
        var data = await PostAsync("/api/dev/project/variables/global", new { command = "list" }, token).ConfigureAwait(false);
        if (!data.TryGetProperty("variables", out var variables) || variables.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("原版全局变量响应缺少 variables 数组");
        return variables.Clone();
    }

    public Task<JsonElement> SetGlobalAsync(int index, string type, object value, bool setDefault = false, CancellationToken token = default)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return PostAsync("/api/dev/project/variables/global", new { command = setDefault ? "set_default" : "set", index, type, value }, token);
    }

    public async Task<JsonElement> ReadGlobalsAsync(OpContext context, CancellationToken token = default)
    {
        var variables = await ListGlobalsAsync(token).ConfigureAwait(false);
        var decoded = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var variable in variables.EnumerateArray())
            decoded.Add(variable.GetProperty("name").GetString()!, NativeResultDecoder.DecodeField(variable));
        context.Globals = new VarPool(caseSensitive: true);
        foreach (var pair in decoded) context.Globals.Set(pair.Key, pair.Value);
        return variables;
    }

    public async Task ReadTestResultsAsync(JsonElement data, Operation operation, OpContext context,
        Action<string, bool> log, CancellationToken token = default)
    {
        int result = data.GetProperty("result").GetInt32();
        operation.ElapsedMs = data.GetProperty("duration").GetDouble() / 1000;
        operation.State = result is 1 or 2 ? OpState.Ok : OpState.Fail;
        operation.ErrorMessage = data.TryGetProperty("message", out var message) ? FormatStateMessage(message) : "";
        var outputs = NativeResultDecoder.DecodeOutputs(data);
        context.OpOutputs[operation.Uuid] = outputs;
        foreach (var pair in outputs.All) context.Vars.Set(pair.Key, pair.Value);
        if (operation.State == OpState.Fail || result == 2) log?.Invoke($"{operation.Name}：原版测试{(result == 2 ? "警告" : "失败")} {operation.ErrorMessage}", operation.State == OpState.Fail);
        await ReadGlobalsAsync(context, token).ConfigureAwait(false);
        await ReadRenderersAsync(context, token, test: true, input: true).ConfigureAwait(false);
    }

    public async Task<NativeRunResult> RunAsync(bool step, CancellationToken token = default)
    {
        if (!await _runGate.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("原版内核已有检测正在运行");
        try { return await RunCoreAsync(step, token).ConfigureAwait(false); }
        finally { _runGate.Release(); }
    }

    private async Task<NativeRunResult> RunCoreAsync(bool step, CancellationToken token)
    {
        var endpoint = await _host.StartAsync(token).ConfigureAwait(false);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(RunTimeout);
        using var stateSocket = new ClientWebSocket();
        using var busSocket = new ClientWebSocket();
        await stateSocket.ConnectAsync(WebSocketUri(endpoint, "state"), limit.Token).ConfigureAwait(false);
        await busSocket.ConnectAsync(WebSocketUri(endpoint, "bus"), limit.Token).ConfigureAwait(false);
        var result = new NativeRunResult();
        var results = new ConcurrentDictionary<string, NativeOperationResult>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stateInitial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busInitial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cycleComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int dispatched = 0;
        long lastState = Environment.TickCount64;
        var started = 0;
        var pc = 0;
        var cycle = 0;
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
        var stateTask = ReadSocketAsync(stateSocket, message =>
        {
            ProtocolMessage?.Invoke("state", message.Clone());
            Interlocked.Exchange(ref lastState, Environment.TickCount64);
            if (message.TryGetProperty("pc", out var initialCounter))
            {
                Volatile.Write(ref pc, initialCounter.GetInt32());
                stateInitial.TrySetResult();
            }
            if (Volatile.Read(ref dispatched) == 0) return;
            if (message.TryGetProperty("results", out var rows))
                foreach (var row in rows.EnumerateArray())
                {
                    var id = row.GetProperty("uuid").GetString();
                    results[id] = new(id, row.GetProperty("result").GetInt32(), row.GetProperty("duration").GetDouble() / 1000,
                        row.GetProperty("message").Clone());
                }
            if (message.TryGetProperty("pc", out var counter)) Volatile.Write(ref pc, counter.GetInt32());
            if (message.TryGetProperty("cycle_time", out var elapsed))
            { result.Milliseconds = elapsed.GetDouble() / 1000; Interlocked.Exchange(ref cycle, 1); cycleComplete.TrySetResult(); }
        }, fault, reading.Token);
        var busTask = ReadSocketAsync(busSocket, message =>
        {
            ProtocolMessage?.Invoke("bus", message.Clone());
            if (!message.TryGetProperty("notify", out var notify)) return;
            busInitial.TrySetResult();
            if (Volatile.Read(ref dispatched) == 0) return;
            if (notify.GetString() == "run") Interlocked.Exchange(ref started, 1);
            if (notify.GetString() == "stop" && Volatile.Read(ref started) == 1) stopped.TrySetResult();
        }, fault, reading.Token);
        try
        {
            await AwaitOrFaultAsync(Task.WhenAll(stateInitial.Task, busInitial.Task), fault.Task, limit.Token).ConfigureAwait(false);
            Volatile.Write(ref dispatched, 1);
            if (step) await PostAsync("/api/dev/project/control", new { command = "step" }, limit.Token).ConfigureAwait(false);
            else await PostAsync("/api/dev/project/control", new { command = "run", cycle = false, cycle_times = 1 }, limit.Token).ConfigureAwait(false);
            await AwaitOrFaultAsync(stopped.Task, fault.Task, limit.Token).ConfigureAwait(false);
            // State results are batched independently of bus.stop and cycle_time. Both can arrive
            // before the last operator result, especially in short programs. Drain until quiet.
            if (!step && !_hasBreakpoints)
                await AwaitOrFaultAsync(cycleComplete.Task, fault.Task, limit.Token).ConfigureAwait(false);
            long stoppedAt = Environment.TickCount64;
            while (Environment.TickCount64 - stoppedAt < 500 ||
                   Environment.TickCount64 - Interlocked.Read(ref lastState) < 350)
                await AwaitOrFaultAsync(Task.Delay(25, limit.Token), fault.Task, limit.Token).ConfigureAwait(false);
            result.ProgramCounter = Volatile.Read(ref pc);
            result.Completed = Volatile.Read(ref cycle) == 1;
            if (result.Completed) result.ProgramCounter = 0;
            result.Paused = !result.Completed;
            foreach (var pair in results) result.Operations[pair.Key] = pair.Value;
            LastRun = result;
            return result;
        }
        catch (OperationCanceledException)
        {
            await StopAsync().ConfigureAwait(false);
            if (!token.IsCancellationRequested) throw new TimeoutException($"原版内核运行超过 {RunTimeout.TotalSeconds:0} 秒，已发送停止命令");
            throw;
        }
        finally
        {
            // The native HTTP server keeps a worker for each upgraded connection. Send the
            // WebSocket close handshake so repeated runs release those workers promptly.
            using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await Task.WhenAll(CloseSocketAsync(stateSocket, closing.Token), CloseSocketAsync(busSocket, closing.Token)).ConfigureAwait(false);
                await Task.WhenAll(stateTask, busTask).WaitAsync(closing.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException) { }
            reading.Cancel(); stateSocket.Abort(); busSocket.Abort();
            await Task.WhenAll(stateTask, busTask).ConfigureAwait(false);
        }
    }

    private static Task CloseSocketAsync(ClientWebSocket socket, CancellationToken token)
        => socket.State is WebSocketState.Open or WebSocketState.CloseReceived
            ? socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "run collected", token) : Task.CompletedTask;

    private static async Task AwaitOrFaultAsync(Task operation, Task fault, CancellationToken token)
    {
        var completed = await Task.WhenAny(operation, fault).WaitAsync(token).ConfigureAwait(false);
        await completed.ConfigureAwait(false);
        if (fault.IsCompleted) await fault.ConfigureAwait(false);
    }

    private static Uri WebSocketUri(Uri endpoint, string channel) => new UriBuilder(endpoint)
    { Scheme = "ws", Path = "/api/dev/websocket/" + channel }.Uri;

    private static async Task ReadSocketAsync(ClientWebSocket socket, Action<JsonElement> receive,
        TaskCompletionSource completion, CancellationToken token)
    {
        var buffer = new byte[65536];
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var payload = new MemoryStream();
                WebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (part.MessageType == WebSocketMessageType.Close) throw new IOException("原版内核状态通道已关闭");
                    payload.Write(buffer, 0, part.Count);
                    if (payload.Length > 32 * 1024 * 1024) throw new InvalidDataException("原版状态消息过大");
                } while (!part.EndOfMessage);
                if (part.MessageType != WebSocketMessageType.Text || payload.Length == 0) continue;
                using var document = JsonDocument.Parse(payload.ToArray());
                receive(document.RootElement);
            }
        }
        catch (Exception ex) when (token.IsCancellationRequested || ex is OperationCanceledException) { }
        catch (Exception ex) { completion.TrySetException(ex); }
    }

    public async Task StopAsync()
    {
        if (_host.State != NativeKernelHostState.Ready) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await PostAsync("/api/dev/project/control", new { command = Volatile.Read(ref _testing) == 1 ? "stop_test" : "stop" }, timeout.Token).ConfigureAwait(false);
    }

    public async Task ReadResultsAsync(NativeRunResult run, IReadOnlyList<ProgramModel> programs, OpContext context,
        Action<string, bool> log, CancellationToken token = default)
    {
        foreach (var program in programs)
            foreach (var op in program.Operations)
            {
                if (!run.Operations.TryGetValue(op.Uuid, out var state)) continue;
                op.ElapsedMs = state.Milliseconds;
                op.State = state.Result is 1 or 2 ? OpState.Ok : OpState.Fail;
                op.ErrorMessage = FormatStateMessage(state.Message);
                var data = await PostAsync("/api/dev/project/variables/outputs", new { command = "load", program_name = program.Name, uuid = op.Uuid }, token).ConfigureAwait(false);
                run.RawOutputs[op.Uuid] = data;
                context.OpOutputs[op.Uuid] = NativeResultDecoder.DecodeOutputs(data);
                if (op.State == OpState.Fail || state.Result == 2) log($"{op.Name}：原版{(state.Result == 2 ? "警告" : "失败")} {op.ErrorMessage}", op.State == OpState.Fail);
            }
        context.Vars.Clear();
        foreach (var output in context.OpOutputs.Values)
            foreach (var pair in output.All) context.Vars.Set(pair.Key, pair.Value);
        run.RawGlobals = await ReadGlobalsAsync(context, token).ConfigureAwait(false);
        await ReadRenderersAsync(context, token).ConfigureAwait(false);
    }

    private static string FormatStateMessage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) return message.ToString();
        var code = message.TryGetProperty("code", out var value) ? value.ToString() : "";
        var parameters = message.TryGetProperty("parameters", out var array) ? string.Join("，", array.EnumerateArray().Select(x => x.TryGetProperty("value", out var v) ? v.ToString() : x.ToString())) : "";
        return code == "0" ? "" : $"{code} {parameters}".Trim();
    }

    public async Task ReadRenderersAsync(OpContext context, CancellationToken token = default, bool test = false, bool input = false)
    {
        context.Images.Dispose(); context.Clouds.Clear(); context.Profiles.Clear(); context.Boxes.Clear(); context.Texts.Clear(); context.NativeOverlays.Clear();
        // IM0..IM31 is the original public range. Query metadata first; download populated slots only.
        for (int im = 0; im <= 31; im++)
        {
            var data = await PostAsync("/api/dev/render/renderer", new { index = im, test, input }, token).ConfigureAwait(false);
            if (data.TryGetProperty("overlay", out _)) NativeResultDecoder.ApplyRenderer(data, context, im);
            // Original getRenderMesh downloads the aggregated pointset cloud for the first Pointset descriptor.
            var pointset = context.NativeOverlays.FirstOrDefault(overlay => overlay.ViewIndex == im && overlay.Type.Equals("pointset", StringComparison.OrdinalIgnoreCase));
            if (pointset != null && pointset.Values.TryGetValue("IM", out var parentIndex))
            {
                int parent = Convert.ToInt32(parentIndex, System.Globalization.CultureInfo.InvariantCulture);
                if (parent < 0 || parent > 31) throw new InvalidDataException($"IM{im} 点集父图像索引无效：{parent}");
                var pointsetUri = new Uri(Endpoint, $"/api/dev/render/image/pointset?parent_index={parent}{(test ? "&test=1" : "")}{(input ? "&input=1" : "")}&timestamp={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
                pointset.Cloud = NativeResultDecoder.DecodePointCloud(await _http.GetByteArrayAsync(pointsetUri, token).ConfigureAwait(false));
            }
            if (!data.TryGetProperty("image_type", out var kind) || kind.GetString() is not ("3d" or "2d" or "1d")) continue;
            var uri = new Uri(Endpoint, $"/api/dev/render/image?index={im}{(test ? "&test=1" : "")}{(input ? "&input=1" : "")}&timestamp={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
            var bytes = await _http.GetByteArrayAsync(uri, token).ConfigureAwait(false);
            if (kind.GetString() == "3d") context.Clouds.Set(im, NativeResultDecoder.DecodePointCloud(bytes));
            else if (kind.GetString() == "1d") context.Profiles[im] = NativeResultDecoder.DecodeProfile(bytes);
            else
            {
                var mat = Cv2.ImDecode(bytes, ImreadModes.Unchanged);
                if (mat.Empty()) { mat.Dispose(); throw new InvalidDataException($"IM{im} 原版图像格式无法解码"); }
                context.Images.Set(im, mat);
            }
        }
    }

    public void Dispose() { _http.Dispose(); _host.Dispose(); }
}
