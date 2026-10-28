using AiVisionStudio.Models;
using AiVisionStudio.Algorithm;

namespace AiVisionStudio.Services;

public partial class MainViewModel
{
    private NativeKernelClient _native;
    private NativeRunResult _nativeResult;
    private readonly Dictionary<string, System.Text.Json.JsonElement> _nativeRawOutputs = new();
    private readonly Dictionary<string, (string Type, object Value)> _pendingNativeGlobals = new(StringComparer.Ordinal);
    private bool _useNativeKernel = string.Equals(Environment.GetEnvironmentVariable("AIVISION_ENGINE"), "native", StringComparison.OrdinalIgnoreCase);
    public bool UseNativeKernel
    {
        get => _useNativeKernel;
        set
        {
            if (_useNativeKernel == value || !CanEdit) return;
            _useNativeKernel = value;
            _nativeResult = null;
            _nativeRawOutputs.Clear();
            _pendingNativeGlobals.Clear();
            _native?.Invalidate();
            if(!value){_native?.Dispose();_native=null;}
            BackendPortText="端口:--";
            _engine.Reset();
            ClearVisualSnapshots();
            foreach (var op in Programs.SelectMany(p => p.Operations)) op.State = op.Enabled ? OpState.None : OpState.Disabled;
            Backend = value ? "原版参照内核待连接" : "自有源码内核（校准中）";
            AuthText = value ? "原版参照" : "自有源码";
            OnPropertyChanged();
            RefreshSelectedOutputs(); RefreshGlobalVars();
            ImageChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public IReadOnlyList<NativeOverlay3D> NativeOverlays => _engine.Context.NativeOverlays;
    public IReadOnlyDictionary<int, ProfileData> Profiles => _engine.Context.Profiles;
    public IReadOnlyDictionary<string, System.Text.Json.JsonElement> NativeRawOutputs => _nativeRawOutputs;
    internal string NativeDatabasePath => _native?.PreparedProjectPath == null ? null : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_native.PreparedProjectPath)!, "data.db");

    private NativeKernelClient NativeClient
    {
        get
        {
            if (_native != null) return _native;
            Uri endpoint = null;
            var configured = Environment.GetEnvironmentVariable("AIVISION_NATIVE_ENDPOINT");
            if (!string.IsNullOrWhiteSpace(configured)) endpoint = new Uri(configured, UriKind.Absolute);
            int? kernelPid = int.TryParse(Environment.GetEnvironmentVariable("AIVISION_NATIVE_PID"), out var pid) ? pid : null;
            _native = new NativeKernelClient(new NativeKernelHostOptions { ExplicitEndpoint = endpoint, ExplicitKernelProcessId = kernelPid });
            if (double.TryParse(Environment.GetEnvironmentVariable("AIVISION_NATIVE_TIMEOUT_SECONDS"), out var seconds) && seconds > 0) _native.RunTimeout = TimeSpan.FromSeconds(seconds);
            return _native;
        }
    }

    private async Task ExecuteNativeAsync(bool step, Action<string, bool> log, CancellationToken token)
    {
        var client = NativeClient;
        var programs = Programs.ToArray();
        await client.PrepareAsync(programs, ProjectPath, SelectedProgram.Name, token).ConfigureAwait(false);
        await FlushNativeGlobalsAsync(client, token).ConfigureAwait(false);
        Backend = "原版 4.5.1 内核"; BackendPortText = $"端口:{client.Endpoint.Port}"; AuthText = "原版内核";
        var resume = _nativeResult?.Paused == true;
        _nativeResult = null;
        if (!resume)
        {
            _engine.Context.OpOutputs.Clear();
            _nativeRawOutputs.Clear();
            foreach (var op in programs.SelectMany(p => p.Operations))
            { op.State = op.Enabled ? OpState.None : OpState.Disabled; op.ErrorMessage = null; op.ElapsedMs = 0; }
        }
        var run = await client.RunAsync(step, token).ConfigureAwait(false);
        await client.ReadResultsAsync(run, programs, _engine.Context, log, token).ConfigureAwait(false);
        foreach (var pair in run.RawOutputs) _nativeRawOutputs[pair.Key] = pair.Value;
        _nativeResult = run;
        log($"原版内核运行{(run.Completed ? "结束" : "暂停")}：OK {programs.SelectMany(p => p.Operations).Count(o => o.State == OpState.Ok)} / NG {programs.SelectMany(p => p.Operations).Count(o => o.State == OpState.Fail)}，耗时 {run.Milliseconds:0.000} ms", false);
    }

    private void InvalidateNativeExecution()
    {
        _native?.Invalidate();
        _nativeResult = null;
        _nativeRawOutputs.Clear();
    }

    private async Task TestNativeAsync(Operation target, Action<string, bool> log, CancellationToken token)
    {
        var client = NativeClient;
        var programs = Programs.ToArray();
        if (_engine.Context.OpOutputs.Count == 0 || client.PreparedProjectPath == null)
        {
            await client.PrepareAsync(programs, ProjectPath, SelectedProgram.Name, token).ConfigureAwait(false);
            await FlushNativeGlobalsAsync(client, token).ConfigureAwait(false);
            var prefix = new NativeRunResult();
            int targetIndex = Operations.IndexOf(target), pc = 0, steps = 0;
            while (pc < targetIndex)
            {
                if (++steps > 1000) throw new InvalidOperationException("测试前置流程超过 1000 个单步，请先运行并检查循环条件");
                var step = await client.RunAsync(true, token).ConfigureAwait(false);
                foreach (var pair in step.Operations) prefix.Operations[pair.Key] = pair.Value;
                pc = step.ProgramCounter;
                if (step.Completed) break;
            }
            if (prefix.Operations.Count > 0)
            {
                await client.ReadResultsAsync(prefix, programs, _engine.Context, log, token).ConfigureAwait(false);
                foreach (var pair in prefix.RawOutputs) _nativeRawOutputs[pair.Key] = pair.Value;
            }
        }
        var result = await client.TestAsync(target.Driver, target.Uuid, target.ParametersRaw, token).ConfigureAwait(false);
        await client.ReadTestResultsAsync(result, target, _engine.Context, log, token).ConfigureAwait(false);
        _nativeRawOutputs[target.Uuid] = result;
        _nativeResult = null;
        Backend = "原版 4.5.1 内核"; BackendPortText = $"端口:{client.Endpoint.Port}"; AuthText = "原版内核";
    }

    private async Task SetNativeGlobalAsync(NativeKernelClient client, string name, string type, object value, CancellationToken token)
    {
        var globals = await client.ListGlobalsAsync(token).ConfigureAwait(false);
        int index = 0;
        foreach (var global in globals.EnumerateArray())
        {
            if (global.GetProperty("name").GetString() == name)
            {
                // Original HMI params-02bab784.js sends both commands: runs initialize custom
                // variables from their default, while the live display reads the current value.
                await client.SetGlobalAsync(index, type, value, true, token).ConfigureAwait(false);
                await client.SetGlobalAsync(index, type, value, false, token).ConfigureAwait(false);
                return;
            }
            index++;
        }
        throw new InvalidOperationException("原版内核不存在全局变量：" + name);
    }

    private async Task FlushNativeGlobalsAsync(NativeKernelClient client, CancellationToken token)
    {
        foreach (var pair in _pendingNativeGlobals.ToArray())
        {
            await SetNativeGlobalAsync(client, pair.Key, pair.Value.Type, pair.Value.Value, token).ConfigureAwait(false);
            _pendingNativeGlobals.Remove(pair.Key);
        }
    }

    private bool WriteNativeGlobal(string name, string type, object value, out string error)
    {
        error = "";
        try
        {
            if (_native?.Endpoint != null && _engine.Context.OpOutputs.Count > 0)
                SetNativeGlobalAsync(_native, name, type, value, CancellationToken.None).GetAwaiter().GetResult();
            else _pendingNativeGlobals[name] = (type, value);
            return true;
        }
        catch (Exception exception) { error = exception.Message; return false; }
    }
}
