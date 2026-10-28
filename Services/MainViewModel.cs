using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Models;
using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Services;

public partial class MainViewModel : INotifyPropertyChanged
{
    public ObservableCollection<ToolNode> Tools { get; private set; } = new();
    public ObservableCollection<ProgramModel> Programs { get; } = new();
    public ObservableCollection<Operation> Operations { get; } = new();

    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand AddOperationCommand { get; }
    public RelayCommand RemoveOperationCommand { get; }
    public RelayCommand RunCommand { get; }
    public RelayCommand StepCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand OpenRecentCommand { get; }
    public ObservableCollection<string> RecentProjects { get; } = new();
    private readonly string _recentFile;
    private sealed record EditHistoryEntry(string Document, Dictionary<string, Guid> ProgramIds);
    private readonly Stack<EditHistoryEntry> _undo = new();
    private readonly Stack<EditHistoryEntry> _redo = new();
    private string _savedDocument;
    private readonly Dictionary<(string Uuid, string Name), (string Value, string Error)> _invalidParameters = new();
    private bool _isDirty;
    public bool IsDirty { get => _isDirty; private set { _isDirty = value; OnPropertyChanged(); } }
    private volatile bool _contourTeachingChanged;
    private bool _isRunning;
    private CancellationTokenSource _interactiveCancellation;
    public bool IsRunning
    {
        get => _isRunning;
        private set { _isRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanEdit)); SynchronizeParameterTree(); System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
    }
    public bool CanEdit => !IsRunning && !IsCycling;

    public ObservableCollection<LogEntry> Logs { get; } = new();
    private readonly ExecutionEngine _engine = new();

    public MainViewModel(string settingsDirectory = null)
    {
        InitializeVisualSnapshots();
        _recentFile = Path.Combine(settingsDirectory ?? Path.Combine(AppContext.BaseDirectory, "UserData"), "recent-projects.json");
        if (DriverCatalog.Count == 0) DriverCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "driver_defs.json"));
        _engine.OperationCompleted += operation =>
        {
            if (operation.State != OpState.Ok || operation.Driver is not ("LoadImage" or "LoadPointCloud")) return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            void Publish() { RefreshGlobalVars(); RefreshSelectedOutputs(); ImageChanged?.Invoke(this, EventArgs.Empty); }
            // A safe boundary: the worker waits while viewers copy the loaded frame.
            if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.Invoke(Publish);
            else Publish();
        };
        LoadTools();
        OpenCommand = new RelayCommand(_ => Open());
        SaveCommand = new RelayCommand(_ => Save(false));
        SaveAsCommand = new RelayCommand(_ => Save(true));
        AddOperationCommand = new RelayCommand(p => InsertTool(p as ToolNode, Operations.Count));
        RemoveOperationCommand = new RelayCommand(p => RemoveOperation(p as Operation));
        RunCommand = new RelayCommand(async _ => await RunInteractiveAsync(false), _ => CanEdit && Operations.Count > 0);
        StepCommand = new RelayCommand(async _ => await RunInteractiveAsync(true), _ => CanEdit && Operations.Count > 0);
        StopCommand = new RelayCommand(async _ =>
        {
            _cycleCancellation?.Cancel();
            _interactiveCancellation?.Cancel();
            _engine.Stop();
            if (UseNativeKernel && _native != null)
                try { await _native.StopAsync(); } catch (Exception ex) { Log("原版停止失败：" + ex.Message, true); }
            if (UseNativeKernel && !IsRunning) InvalidateNativeExecution();
            Status = IsRunning ? "正在停止运行" : "运行已停止";
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }, _ => IsRunning || IsCycling || IsExecutionPaused);
        UndoCommand = new RelayCommand(_ => Undo(), _ => CanEdit && _undo.Count > 0);
        RedoCommand = new RelayCommand(_ => Redo(), _ => CanEdit && _redo.Count > 0);
        OpenRecentCommand = new RelayCommand(p =>
        {
            if (p is not string path) return;
            try { OpenProject(path); }
            catch (Exception ex) { Status = "打开失败：" + ex.Message; Log(Status, true); }
        }, _ => CanEdit);
        try
        {
            if (File.Exists(_recentFile))
                foreach (var path in JsonSerializer.Deserialize<string[]>(File.ReadAllText(_recentFile)) ?? [])
                    if (File.Exists(path) && !RecentProjects.Contains(path)) RecentProjects.Add(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        NewProject();
        Backend = UseNativeKernel ? "原版参照内核待连接" : "自有源码内核（校准中）";
        AuthText = UseNativeKernel ? "原版参照" : "自有源码";
    }

    // ---------------- 运行 ----------------
    private void Log(string msg, bool isError = false)
        => Logs.Add(new LogEntry { Time = DateTime.Now.ToString("HH:mm:ss.fff"), Level = isError ? "error" : "info", Message = msg });

    public async Task RunInteractiveAsync(bool step = false)
    {
        if (IsRunning || !ApplyParameters() || Operations.Count == 0) return;
        using var cancellation = new CancellationTokenSource();
        _interactiveCancellation = cancellation;
        IsRunning = true;
        var operations = Operations.ToArray();
        var index = Math.Max(0, Array.IndexOf(operations, SelectedOperation));
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        void DispatchLog(string message, bool error)
        {
            if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.InvokeAsync(() => Log(message, error));
            else Log(message, error);
        }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            PrepareRun();
            Status = step ? "单步执行中" : "运行中";
            if (UseNativeKernel) await ExecuteNativeAsync(step, DispatchLog, cancellation.Token);
            else await Task.Run(() =>
            {
                if (step) _engine.Step(operations, DispatchLog, index, cancellation.Token);
                else _engine.Run(operations, DispatchLog, cancellation.Token);
            });
            await OnUiAsync(() =>
            {
                FinishRun(timer.Elapsed);
                if (cancellation.IsCancellationRequested) Status = "运行已停止";
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { await OnUiAsync(() => { Status = "运行已停止"; Log(Status); }); }
        catch (Exception ex) { await OnUiAsync(() => { Status = "运行失败：" + ex.Message; Log(Status, true); }); }
        finally { await OnUiAsync(() => { _interactiveCancellation = null; IsRunning = false; }); }
    }

    private static Task OnUiAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) return dispatcher.InvokeAsync(action).Task;
        action();
        return Task.CompletedTask;
    }

    private void PrepareRun()
    {
        _engine.ConfigurePrograms(Programs);
        _engine.Context.ProjectDir = string.IsNullOrEmpty(ProjectPath) ? AppContext.BaseDirectory : Path.GetDirectoryName(ProjectPath)!;
        _engine.Context.Devices = ReadDevices().Select(d => d.Copy()).ToArray();
        if (!_engine.IsPaused)
        {
            _engine.Context.DrawTexts.Clear();
            _engine.Context.Texts.Clear();
            _engine.Context.Boxes.Clear();
            _engine.Context.NativeOverlays.Clear();
        }
    }

    private void FinishRun(TimeSpan elapsed)
    {
        if (_contourTeachingChanged) { _contourTeachingChanged = false; IsDirty = true; }
        RunTimeMilliseconds = UseNativeKernel && _nativeResult?.Milliseconds > 0 ? _nativeResult.Milliseconds : elapsed.TotalMilliseconds;
        RunTimeText = $"运行时间:{RunTimeMilliseconds:0.000}ms";
        RefreshGlobalVars();
        RefreshSelectedOutputs();
        OnPropertyChanged(nameof(DrawText));
        OnPropertyChanged(nameof(DrawTextVisible));
        Status = UseNativeKernel
            ? IsExecutionPaused ? $"原版内核已暂停：第 {_nativeResult.ProgramCounter} 步" : $"原版内核运行结束：OK {Programs.SelectMany(p => p.Operations).Count(o => o.State == OpState.Ok)} / NG {ExecutionFailureCount}"
            : _engine.IsPaused ? $"已暂停：{_engine.NextOperation?.Name}" : $"运行结束：OK {_engine.LastOkCount} / NG {_engine.LastFailCount}";
        if (!UseNativeKernel && _engine.NextOperation != null && Operations.Contains(_engine.NextOperation)) SelectedOperation = _engine.NextOperation;
        if (ExecutionFailureCount > 0 && !Clouds.All.Any(p => p.Value?.Count > 0) && !Images.All.Any(p => p.Value != null && !p.Value.Empty()) && !Profiles.Any(p => p.Value.Count > 0))
        {
            var failed = Programs.SelectMany(p => p.Operations).FirstOrDefault(o => o.State == OpState.Fail);
            if (failed != null) Status = $"运行失败：{failed.Name}：{failed.ErrorMessage}";
        }
        ImageChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RunAll()
    {
        if (!CanEdit) return;
        if (!ApplyParameters()) return;
        if (Operations.Count == 0) { Status = "运行列表为空"; return; }
        PrepareRun();
        Log($"开始全部运行（{Operations.Count} 个算子）");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (UseNativeKernel) ExecuteNativeAsync(false, Log, CancellationToken.None).GetAwaiter().GetResult();
        else _engine.Run(Operations, Log);
        sw.Stop();
        FinishRun(sw.Elapsed);
        if (SelectedOperation == null && Operations.Count > 0) SelectedOperation = Operations[0];
    }

    public void RunStep()
    {
        if (!CanEdit) return;
        if (!ApplyParameters()) return;
        if (Operations.Count == 0) return;
        var idx = SelectedOperation == null ? 0 : Operations.IndexOf(SelectedOperation);
        var op = Operations[Math.Clamp(idx, 0, Operations.Count - 1)];
        PrepareRun();
        Log($"单步执行：{op.Name}（{op.Driver}）");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        if (UseNativeKernel) ExecuteNativeAsync(true, Log, CancellationToken.None).GetAwaiter().GetResult();
        else _engine.Step(Operations, Log, idx);
        FinishRun(timer.Elapsed);
    }

    /// <summary>运行后通知界面刷新显示区。</summary>
    public event EventHandler ImageChanged;

    public ImagePool Images => _engine.Context.Images;
    public CloudPool Clouds => _engine.Context.Clouds;
    public string WriteRoot { get => _engine.Context.WriteRoot; set => _engine.Context.WriteRoot = value; }
    public bool IsExecutionPaused => UseNativeKernel ? _nativeResult?.Paused == true : _engine.IsPaused;
    public void RequestPause()
    {
        if (UseNativeKernel) { Status = "原版参照模式请使用停止或断点"; return; }
        if (IsRunning) { _engine.RequestPause(); Status = "将在当前工具完成后暂停"; }
    }
    public bool IsExecutionCompleted => UseNativeKernel ? _nativeResult?.Completed == true : _engine.IsCompleted;
    public int ExecutionFailureCount => UseNativeKernel ? Programs.SelectMany(p => p.Operations).Count(o => o.State == OpState.Fail) : _engine.LastFailCount;

    // ---------------- 全局变量 / 状态栏 ----------------
    public System.Collections.ObjectModel.ObservableCollection<VarRow> GlobalVars { get; } = new();

    /// <summary>DrawText3D 写入的多行文本（显示在影像区左上）。</summary>
    /// <summary>3D 场景文字（DrawText3D 原始参数）。</summary>
    public IReadOnlyList<Text3D> TextOverlays => _engine.Context.Texts;

    /// <summary>3D 包围盒。</summary>
    public IReadOnlyList<Box3> BoxOverlays => _engine.Context.Boxes;

    public string DrawText
    {
        get
        {
            if (_engine.Context.DrawTexts.Count == 0) return "";
            return string.Join(Environment.NewLine, _engine.Context.DrawTexts);
        }
    }

    public System.Windows.Visibility DrawTextVisible
        => string.IsNullOrEmpty(DrawText) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public void RefreshGlobalVars()
    {
        GlobalVars.Clear();

        // 1) 工程全局变量（programs.variables + Lua 写入）
        foreach (var kv in _engine.Context.Globals.All.OrderBy(k => k.Key))
        {
            var row = VarRow.FromOwned(kv.Key, kv.Value);
            var definition = SelectedProgram?.Variables.FirstOrDefault(v => v.Name == kv.Key);
            // Lua stores scalar numbers as doubles; the project owns the declared variable type.
            if (definition != null && (GlobalValue.Types.Contains(row.Type) || row.Type == "array")) row.Type = definition.Type;
            GlobalVars.Add(row);
        }
    }

    public string ProjectName => string.IsNullOrEmpty(ProjectPath) ? "未打开工程" : Path.GetFileName(ProjectPath);
    public string StatusPath => string.IsNullOrEmpty(ProjectPath) ? "工程地址:(未打开)" : $"工程地址:{ProjectPath}";

    private string _runTime = "运行时间:0 ms";
    public double RunTimeMilliseconds { get; private set; }
    public string RunTimeText { get => _runTime; set { _runTime = value; OnPropertyChanged(); } }

    private string _cpu = "CPU:0%";
    public string CpuText { get => _cpu; set { _cpu = value; OnPropertyChanged(); } }

    private string _mem = "系统内存:0/0 GB";
    public string MemText { get => _mem; set { _mem = value; OnPropertyChanged(); } }

    private string _auth = "本地模式";
    public string AuthText { get => _auth; set { _auth = value; OnPropertyChanged(); } }

    private string _backendPort = "";
    public string BackendPortText { get => _backendPort; set { _backendPort = value; OnPropertyChanged(); } }

    public void Dispose()
    {
        _cycleCancellation?.Cancel();
        _interactiveCancellation?.Cancel();

        DisposeVisualSnapshots();

        _native?.Dispose();
        _engine.Reset();
    }

    // ---------------- 工具列表 ----------------
    private readonly List<ToolNode> _allTools = new();
    private string _toolSearchText = "";

    private void LoadTools()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "tools.json");
        if (!File.Exists(path)) return;
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        foreach (var top in doc.RootElement.EnumerateArray())
        {
            var node = ReadTool(top);
            if (node.Label == "2D") node.IsExpanded = true;
            _allTools.Add(node);
            Tools.Add(node);
        }
    }

    /// <summary>按关键字过滤工具树（组内无匹配则整组隐藏）。</summary>
    public void RefreshSearch(string text)
    {
        var q = (text ?? "").Trim();
        if (string.Equals(_toolSearchText, q, StringComparison.OrdinalIgnoreCase)) return;
        var matches = q.Length == 0 ? _allTools.ToArray()
            : _allTools.Select(tool => Filter(tool, q)).Where(tool => tool != null).ToArray();
        _toolSearchText = q;
        if (Tools.SequenceEqual(matches)) return;
        Tools = new ObservableCollection<ToolNode>(matches);
        OnPropertyChanged(nameof(Tools));
    }

    private static ToolNode Filter(ToolNode n, string q)
    {
        if (n.IsLeaf)
            return n.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || (n.Cn ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ? n : null;

        var copy = new ToolNode { Label = n.Label, Cn = n.Cn, Depth = n.Depth, IsExpanded = true };
        foreach (var c in n.Children)
        {
            var f = Filter(c, q);
            if (f != null) copy.Children.Add(f);
        }
        return copy.Children.Count > 0 ? copy : null;
    }

    private static ToolNode ReadTool(JsonElement e, int depth = 0)
    {
        var node = new ToolNode
        {
            Depth = depth,
            Label = e.TryGetProperty("driver", out var driver) ? driver.GetString()
                : e.TryGetProperty("label", out var l) ? l.GetString() : "",
            Cn = e.TryGetProperty("cn", out var c) ? c.GetString() : ""
        };
        if (e.TryGetProperty("children", out var kids))
            foreach (var k in kids.EnumerateArray())
                node.Children.Add(ReadTool(k, depth + 1));
        return node;
    }

    // ---------------- 工程 ----------------
    private string _projectPath = "";
    public string ProjectPath
    {
        get => _projectPath;
        private set { _projectPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProjectTitle)); OnPropertyChanged(nameof(ProjectName)); OnPropertyChanged(nameof(StatusPath)); }
    }

    public string ProjectTitle => string.IsNullOrEmpty(ProjectPath) ? "new project" : Path.GetFileName(ProjectPath);

    private ProgramModel _selectedProgram;
    public ProgramModel SelectedProgram
    {
        get => _selectedProgram;
        set
        {
            if (!CanEdit)
                return;

            if (ReferenceEquals(_selectedProgram, value))
                return;

            SelectedOperation = null;

            _selectedProgram = value;

            Operations.Clear();

            if (value != null)
            {
                foreach (var operation in value.Operations)Operations.Add(operation);
            }

            Renumber();

            // 当前流程和算子列表都更新完成后，再通知界面
            OnPropertyChanged(nameof(SelectedProgram));
        }
    }

    public void OpenProject(string path)
    {
        if (!CanEdit) throw new InvalidOperationException("请先停止运行再打开工程。");
        var programs = ProjectFile.Load(path);
        _pendingNativeGlobals.Clear();
        if (programs.Count == 0) throw new InvalidDataException("工程不包含 programs.define 程序定义。");
        _invalidParameters.Clear();
        _nodeDrafts.Clear();
        _engine.Reset();
        ClearVisualSnapshots();
        InvalidateNativeExecution();
        SelectedProgram = null;
        Programs.Clear();
        foreach (var p in programs) Programs.Add(p);
        ProjectPath = path;
        SelectedProgram = Programs.FirstOrDefault(p => p.IsDefault) ?? Programs.FirstOrDefault(p => p.Name == "main") ?? Programs.FirstOrDefault();

        // 全局变量：按工程定义初始化默认值
        _engine.Context.Globals.Clear();
        var sel = SelectedProgram;
        if (sel != null)
            foreach (var v in sel.Variables)
                _engine.Context.Globals.Set(v.Name, ParseDefault(v.Type, v.Default));
        RefreshRecipePath();
        RefreshGlobalVars();
        _undo.Clear();
        _redo.Clear();
        _savedDocument = ProjectFile.Serialize(Programs);
        IsDirty = false;
        RememberProject(path);
        RunTimeMilliseconds = 0;
        RunTimeText = "运行时间:0 ms";
        Status = $"已打开：{ProjectName}";
        ImageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static object ParseDefault(string type, string text)
    {
        if (type?.EndsWith("[]") == true) return GlobalValue.Parse(type, string.IsNullOrWhiteSpace(text) ? "[]" : text);
        if (string.IsNullOrEmpty(text)) return null;
        switch ((type ?? "").ToLowerInvariant())
        {
            case "int": return int.TryParse(text, out var i) ? i : 0;
            case "float":
            case "double":
                return double.TryParse(text, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0.0;
            case "bool": return bool.TryParse(text, out var b) && b;
            default: return text;
        }
    }

    private void RefreshRecipePath()
    {
        var recipe = SelectedProgram?.RecipePath;
        var directory = string.IsNullOrEmpty(ProjectPath) ? AppContext.BaseDirectory : Path.GetDirectoryName(Path.GetFullPath(ProjectPath))!;
        _engine.Context.RecipePath = string.IsNullOrEmpty(recipe) ? "" : Path.GetFullPath(Path.IsPathRooted(recipe) ? recipe : Path.Combine(directory, recipe));
    }

    private void Open()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "ES-Vision 工程 (*.aivs)|*.aivs|所有文件 (*.*)|*.*",
            Title = "打开工程"
        };
        if (dlg.ShowDialog() == true)
        {
            try { OpenProject(dlg.FileName); }
            catch (Exception ex) { Status = "打开失败：" + ex.Message; Log(Status, true); }
        }
    }

    private void Save(bool saveAs) => TrySave(saveAs);

    public bool TrySave(bool saveAs = false)
    {
        if (!CanEdit) { Status = "请先停止运行再保存工程"; return false; }
        if (!ApplyParameters()) return false;
        var path = ProjectPath;
        if (string.IsNullOrEmpty(ProjectPath) || saveAs)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "ES-Vision 工程 (*.aivs)|*.aivs",
                FileName = string.IsNullOrEmpty(ProjectPath) ? "new project.aivs" : Path.GetFileName(ProjectPath),
                Title = "保存工程"
            };
            if (dlg.ShowDialog() != true) return false;
            path = dlg.FileName;
        }
        return SaveProject(path);
    }

    public bool SaveProject(string path)
    {
        if (!CanEdit) { Status = "请先停止运行再保存工程"; return false; }
        if (!ApplyParameters()) return false;
        try
        {
            if (!ProjectFile.SaveAll(path, Programs)) throw new InvalidDataException("没有可写的工程结构");
            ProjectPath = Path.GetFullPath(path);
            RefreshRecipePath();
            _savedDocument = ProjectFile.Serialize(Programs);
            IsDirty = false;
            RememberProject(ProjectPath);
            Status = $"已保存：{ProjectName}    {DateTime.Now:HH:mm:ss}";
            return true;
        }
        catch (Exception ex) { Status = "保存失败：" + ex.Message; Log(Status, true); return false; }
    }

    public void NewProject()
    {
        if (!CanEdit) { Status = "请先停止运行再新建工程"; return; }
        _pendingNativeGlobals.Clear();
        _invalidParameters.Clear();
        _nodeDrafts.Clear();
        _engine.Reset();
        ClearVisualSnapshots();
        InvalidateNativeExecution();
        SelectedProgram = null;
        Programs.Clear();
        foreach (var program in ProjectFile.Create()) Programs.Add(program);
        ProjectPath = "";
        SelectedProgram = Programs[0];
        RefreshRecipePath();
        _undo.Clear();
        _redo.Clear();
        _savedDocument = ProjectFile.Serialize(Programs);
        IsDirty = false;
        Logs.Clear();
        RefreshGlobalVars();
        Status = "新建工程";
        RunTimeMilliseconds = 0;
        RunTimeText = "运行时间:0 ms";
        ImageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RememberProject(string path)
    {
        path = Path.GetFullPath(path);
        foreach (var existing in RecentProjects.Where(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase)).ToArray()) RecentProjects.Remove(existing);
        RecentProjects.Insert(0, path);
        while (RecentProjects.Count > 10) RecentProjects.RemoveAt(RecentProjects.Count - 1);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_recentFile)!);
            File.WriteAllText(_recentFile, JsonSerializer.Serialize(RecentProjects));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("最近工程列表未保存：" + ex.Message, true); }
    }

    private void BeforeEdit()
    {
        var snapshot = CaptureEditHistory();
        if (snapshot != null) _undo.Push(snapshot);
        _redo.Clear();
    }

    private EditHistoryEntry CaptureEditHistory()
    {
        var document = ProjectFile.Serialize(Programs);
        return document == null ? null : new EditHistoryEntry(document,
            Programs.ToDictionary(program => program.Name, program => program.HistoryId));
    }

    private void AfterEdit()
    {
        // 保留现有图片，只清理已删除控件的快照
        PruneRemovedVisualSnapshots();
        InvalidateNativeExecution();
        _engine.InvalidateExecution();
        _engine.ConfigurePrograms(Programs);
        IsDirty = _invalidParameters.Count > 0 || _nodeDrafts.Count > 0 || ProjectFile.Serialize(Programs) != _savedDocument;
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        SynchronizeParameterTree();
        ImageChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Undo() => RestoreHistory(_undo, _redo);
    public void Redo() => RestoreHistory(_redo, _undo);

    private void RestoreHistory(Stack<EditHistoryEntry> from, Stack<EditHistoryEntry> to)
    {
        if (!CanEdit) return;
        if (from.Count == 0) return;
        
        _invalidParameters.Clear();
        _nodeDrafts.Clear();
        var selectedProgramId = SelectedProgram?.HistoryId;
        var selectedId = SelectedOperation?.Uuid;
        var previousInputs = Programs.SelectMany(program => program.Operations).ToDictionary(op => op.Uuid, VisualInputSignature);
        var previousVariables = SelectedProgram?.Variables.ToArray() ?? Array.Empty<GlobalVarDef>();
        var current = CaptureEditHistory();
        var entry = from.Peek();
        var programs = ProjectFile.Parse(entry.Document);
        foreach (var program in programs)
        {
            if (entry.ProgramIds.TryGetValue(program.Name, out var historyId))
                program.HistoryId = historyId;

            var previous = Programs.FirstOrDefault(item => item.HistoryId == program.HistoryId);
            if (previous != null && previous.Name != program.Name)
                RenameVisualProgram(previous.Name, program.Name);
        }
        from.Pop();
        if (current != null) to.Push(current);
        SelectedProgram = null;
        Programs.Clear();
        foreach (var program in programs) Programs.Add(program);
        SelectedProgram = Programs.FirstOrDefault(x => x.HistoryId == selectedProgramId) ?? Programs.FirstOrDefault();
        SelectedOperation = Operations.FirstOrDefault(x => x.Uuid == selectedId);
        foreach (var operation in Programs.SelectMany(program => program.Operations))
            if (previousInputs.TryGetValue(operation.Uuid, out var previousInput)) InvalidateChangedVisualInput(operation, previousInput);
        AfterEdit();
        RestoreManagementAfterHistory(previousVariables);
    }

    // ---------------- 运行列表 ----------------
    public void AddTool(ToolNode node) => InsertTool(node, Operations.Count);

    public void InsertTool(ToolNode node, int index)
    {
        if (!CanEdit) return;
        if (node == null || !node.IsLeaf) return;
        BeforeEdit();
        var op = new Operation { Driver = node.Label, Version = DriverCatalog.Get(node.Label)?.Version ?? 1, Name = UniqueOperationName(node.Display), ParametersRaw = DefaultParameters(node.Label) };
        index = Math.Clamp(index, 0, Operations.Count);
        Operations.Insert(index, op);
        if (_selectedProgram != null && index <= _selectedProgram.Operations.Count)
            _selectedProgram.Operations.Insert(index, op);
        Renumber();
        SelectedOperation = op;
        Status = $"已添加算子：{op.Name}";
        AfterEdit();
    }

    private string UniqueOperationName(string name)
    {
        if (!Operations.Any(operation => operation.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return name;
        int suffix = 2;
        while (Operations.Any(operation => operation.Name.Equals($"{name} {suffix}", StringComparison.OrdinalIgnoreCase))) suffix++;
        return $"{name} {suffix}";
    }

    public void RenameOperation(Operation operation, string name)
    {
        if (!CanEdit) throw new InvalidOperationException("请先停止运行再重命名控件");
        if (operation == null || !Operations.Contains(operation)) throw new ArgumentException("请选择运行列表中的控件");
        name = name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl))
            throw new ArgumentException("控件名称须为 1–80 个可见字符");
        if (operation.Name == name) return;
        if (Operations.Any(item => item != operation && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("当前流程已存在同名控件：" + name);
        if (!ApplyParameters()) throw new InvalidOperationException("请先修正无效参数");
        BeforeEdit();
        var oldName = operation.Name;
        operation.Name = name;
        if (_engine.Context.NamedOutputSources.TryGetValue(oldName, out var sourceId) && sourceId == operation.Uuid)
        {
            _engine.Context.NamedOutputSources.Remove(oldName);
            _engine.Context.NamedOutputSources[name] = operation.Uuid;
        }
        AfterEdit();
        Status = "控件已重命名：" + name;
    }

    public bool MoveOperationToInsertion(Operation operation, int insertionIndex)
    {
        if (!CanEdit || operation == null) return false;
        int from = Operations.IndexOf(operation);
        if (from < 0) return false;
        int destination = Math.Clamp(insertionIndex, 0, Operations.Count);
        if (destination > from) destination--;
        if (destination == from) return false;
        MoveOperation(operation, destination);
        Status = $"已调整顺序：{operation.Name}（编号{from} → {destination}）";
        Log("拖动调整算子顺序：" + operation.Name + $"（编号{from} → {destination}）");
        return true;
    }

    public void MoveOperation(Operation op, int index)
    {
        if (!CanEdit) return;
        if (op == null) return;
        var from = Operations.IndexOf(op);
        if (from < 0) return;
        index = Math.Clamp(index, 0, Operations.Count - 1);
        if (index == from) return;
        BeforeEdit();
        Operations.Move(from, index);
        if (_selectedProgram != null) _selectedProgram.Operations.Move(from, index);
        Renumber();
        AfterEdit();
    }

    public void RemoveOperation(Operation op)
    {
        if (!CanEdit) return;
        if (op == null || !Operations.Contains(op)) return;
        int removedIndex = Operations.IndexOf(op);
        bool wasSelected = ReferenceEquals(op, _selectedOp);
        BeforeEdit();
        _engine.InvalidateResults([op.Uuid]);
        Operations.Remove(op);
        _selectedProgram?.Operations.Remove(op);
        foreach (var key in _invalidParameters.Keys.Where(k => k.Uuid == op.Uuid).ToArray()) _invalidParameters.Remove(key);
        foreach (var key in _nodeDrafts.Keys.Where(k => k.Operation == op.Uuid).ToArray()) _nodeDrafts.Remove(key);
        if (wasSelected) SelectedOperation = Operations.Take(removedIndex).LastOrDefault();
        Renumber();
        RefreshGlobalVars(); RefreshSelectedOutputs();
        AfterEdit();
    }

    public void DuplicateOperation(Operation op)
    {
        if (!CanEdit) return;
        if (op == null || !Operations.Contains(op)) return;
        BeforeEdit();
        var idx = Operations.IndexOf(op) + 1;
        var copy = new Operation
        {
            Driver = op.Driver,
            Version = op.Version,
            Name = UniqueOperationName(op.Name + " 副本"),
            Comment = op.Comment,
            Enabled = op.Enabled,
            ParametersRaw = ProjectFile.Clone(op.ParametersRaw as YamlNode),
            OriginalRaw = ProjectFile.Clone(op.OriginalRaw) as YamlMappingNode
        };
        Operations.Insert(idx, copy);
        _selectedProgram?.Operations.Insert(idx, copy);
        Renumber();
        SelectedOperation = copy;
        AfterEdit();
    }

    public void ToggleEnabled(Operation op)
    {
        if (!CanEdit) return;
        if (op == null) return;
        BeforeEdit();
        op.Enabled = !op.Enabled;
        op.State = op.Enabled ? OpState.None : OpState.Disabled;
        AfterEdit();
    }

    public void ToggleBreakpoint(Operation op)
    {
        if (!CanEdit) return;
        if (op == null) return;
        BeforeEdit();
        op.Breakpoint = !op.Breakpoint;
        AfterEdit();
    }

    private void Renumber()
    {
        for (int i = 0; i < Operations.Count; i++) Operations[i].Index = i;
    }

    // ---------------- 选中算子 / 参数面板 ----------------
    private Operation _selectedOp;
    public Operation SelectedOperation
    {
        get => _selectedOp;
        set
        {
            if (ReferenceEquals(_selectedOp, value)) return;
            if (_selectedOp != null) _selectedOp.IsSelected = false;
            _selectedOp = value;
            if (_selectedOp != null) _selectedOp.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            LoadParams();
            RefreshSelectedOutputs();
        }
    }

    public bool HasSelection => _selectedOp != null;
    public ObservableCollection<ParamItem> Params { get; } = new();

    private void LoadParams()
    {
        foreach (var old in Params) old.PropertyChanged -= OnParameterChanged;
        Params.Clear();
        if (_selectedOp == null) { ParameterGroups.Clear(); return; }
        var values = GetChild(_selectedOp.ParametersRaw as YamlMappingNode, "value") as YamlMappingNode;
        var seen = new HashSet<string>();
        var def = DriverCatalog.Get(_selectedOp.Driver);
        if (def != null)
        {
            foreach (var spec in def.Params)
            {
                var node = GetChild(values, spec.Name) ?? DefaultNode(spec);
                AddParameter(spec.Name, spec.Type, node);
                seen.Add(spec.Name);
            }
        }
        if (values != null)
            foreach (var kv in values.Children)
                if (seen.Add(kv.Key.ToString()))
                    AddParameter(kv.Key.ToString(), kv.Value is YamlScalarNode sc ? GuessKind(kv.Key.ToString(), sc.Value ?? "") : "complex", kv.Value);
        foreach (var list in def?.Params.Where(p => p.Type == "list") ?? [])
        {
            var listItem = Params.FirstOrDefault(p => p.Name == list.Name);
            if (listItem == null || ReadItem(listItem) is not YamlSequenceNode members) continue;
            foreach (var member in members.Children.OfType<YamlScalarNode>())
                if (seen.Add(member.Value))
                {
                    var initial = list.Raw is { } raw && raw.TryGetProperty("value", out var template) ? template.Clone() : (JsonElement?)null;
                    AddParameter(member.Value, "group", DefaultNode(new ParamSpec { Type = "group", Children = list.Children, Default = initial }));
                }
        }
        RebuildParameterTree();
    }

    private void AddParameter(string name, string kind, YamlNode node)
    {
        if (node is YamlScalarNode { Value: null or "" })
        {
            var definition = DriverCatalog.Get(SelectedOperation.Driver);
            var values = GetChild(SelectedOperation.ParametersRaw as YamlMappingNode, "value") as YamlMappingNode;
            foreach (var list in definition?.Params.Where(p => p.Type == "list" && p.Children.Count > 0) ?? [])
            {
                var members = GetChild(values, list.Name) as YamlSequenceNode ?? DefaultNode(list) as YamlSequenceNode;
                if (members?.Children.Any(x => x.ToString() == name) == true)
                {
                    node = DefaultNode(new ParamSpec { Type = "group", Children = list.Children });
                    kind = "group";
                    break;
                }
            }
        }
        var item = new ParamItem
        {
            Name = name,
            Kind = kind,
            IsComplex = node is not YamlScalarNode,
            Value = node is YamlScalarNode scalar ? scalar.Value ?? "" : ToYaml(node)
        };
        if (_invalidParameters.TryGetValue((SelectedOperation.Uuid, name), out var draft))
        {
            item.Value = draft.Value;
            item.Error = draft.Error;
        }
        item.PropertyChanged += OnParameterChanged;
        Params.Add(item);
    }

    private static YamlNode JsonToYaml(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.Object => new YamlMappingNode(json.EnumerateObject().Select(p => new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode(p.Name), JsonToYaml(p.Value)))),
        JsonValueKind.Array => new YamlSequenceNode(json.EnumerateArray().Select(JsonToYaml)),
        JsonValueKind.String => new YamlScalarNode(json.GetString()),
        JsonValueKind.Null or JsonValueKind.Undefined => new YamlScalarNode(""),
        _ => new YamlScalarNode(json.GetRawText())
    };

    private static YamlMappingNode DefaultParameters(string driver)
    {
        var definition = DriverCatalog.Get(driver);
        var values = definition?.ParameterValues is { } json ? JsonToYaml(json) as YamlMappingNode : null;
        values ??= new YamlMappingNode();
        foreach (var spec in definition?.Params ?? [])
            if (GetChild(values, spec.Name) == null) values.Add(spec.Name, DefaultNode(spec));
        var bind = definition?.ParameterBind is { } bindings ? JsonToYaml(bindings) : new YamlMappingNode();
        return new YamlMappingNode { { "value", values }, { "bind", bind } };
    }

    private void OnParameterChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ParamItem.Value) || sender is not ParamItem item || SelectedOperation == null) return;
        if (!CanEdit) { item.Error = "运行期间不能修改参数"; return; }
        try
        {
            YamlNode node;
            var value = item.Value ?? "";
            if (item.IsComplex)
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(value));
                if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is YamlScalarNode)
                    throw new FormatException("请输入完整 YAML 对象或数组");
                node = stream.Documents[0].RootNode;
                var old = GetChild(GetChild(SelectedOperation.ParametersRaw as YamlMappingNode, "value") as YamlMappingNode, item.Name);
                if (old != null && node.NodeType != old.NodeType && old is not YamlScalarNode { Value: null or "" }) throw new FormatException("参数结构类型不能更改");
            }
            else
            {
                switch (item.Kind.ToLowerInvariant())
                {
                    case "int":
                    case "integer":
                    case "im":
                        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _)) throw new FormatException("请输入整数");
                        break;
                    case "float":
                    case "double":
                    case "number":
                        if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) throw new FormatException("请输入有效数字");
                        break;
                    case "bool":
                        if (!bool.TryParse(value, out var boolean)) throw new FormatException("请输入 true 或 false");
                        value = boolean ? "true" : "false";
                        break;
                }
                node = new YamlScalarNode(value);
            }
            var root = SelectedOperation.ParametersRaw as YamlMappingNode ?? new YamlMappingNode();
            var values = GetChild(root, "value") as YamlMappingNode ?? new YamlMappingNode();
            var luaBindings = SelectedOperation.Driver == "Lua" && item.Name == "_input_names" ? PrepareLuaInputBindings(root, node) : null;
            if (GetChild(values, item.Name) is { } current && ToYaml(current) == ToYaml(node))
            {
                _invalidParameters.Remove((SelectedOperation.Uuid, item.Name));
                item.Error = "";
                IsDirty = _invalidParameters.Count > 0 || _nodeDrafts.Count > 0 || ProjectFile.Serialize(Programs) != _savedDocument;
                SynchronizeParameterTree();
                return;
            }
            var previousInput = VisualInputSignature(SelectedOperation);
            BeforeEdit();
            values.Children[new YamlScalarNode(item.Name)] = node;
            InvalidateContourTeaching(values, item.Name);
            if (luaBindings != null) root.Children[new YamlScalarNode("bind")] = luaBindings;
            root.Children[new YamlScalarNode("value")] = values;
            SelectedOperation.ParametersRaw = root;
            InvalidateChangedVisualInput(SelectedOperation, previousInput);
            _invalidParameters.Remove((SelectedOperation.Uuid, item.Name));
            item.Error = "";
            AfterEdit();
            Status = $"参数已更新：{item.Name}";
            SynchronizeParameterTree();
        }
        catch (Exception ex) when (ex is FormatException or YamlDotNet.Core.YamlException)
        {
            item.Error = ex.Message;
            _invalidParameters[(SelectedOperation.Uuid, item.Name)] = (item.Value, ex.Message);
            IsDirty = true;
            Status = $"参数 {item.Name}：{ex.Message}";
        }
    }

    public bool ApplyParameters()
    {
        if (_nodeDrafts.Count > 0)
        {
            var invalidNode = _nodeDrafts.First();
            var op = Programs.SelectMany(p => p.Operations).FirstOrDefault(o => o.Uuid == invalidNode.Key.Operation);
            Status = $"参数 {op?.Name}：{invalidNode.Value.Error}";
            return false;
        }
        if (_invalidParameters.Count > 0)
        {
            var error = _invalidParameters.First();
            var operation = Programs.SelectMany(p => p.Operations).FirstOrDefault(o => o.Uuid == error.Key.Uuid);
            Status = $"参数 {operation?.Name}/{error.Key.Name}：{error.Value.Error}";
            return false;
        }
        var invalid = Params.FirstOrDefault(p => p.HasError);
        if (invalid != null) { Status = $"参数 {invalid.Name}：{invalid.Error}"; return false; }
        return true;
    }

    private static string DictToText(Dictionary<string, object> dict, int depth)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var kv in dict)
        {
            if (depth > 0) sb.Append(new string(' ', depth * 2));
            if (kv.Value is Dictionary<string, object> sub)
            {
                sb.AppendLine($"{kv.Key}:");
                sb.Append(DictToText(sub, depth + 1));
            }
            else
            {
                sb.AppendLine($"{kv.Key}: {kv.Value}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static YamlDotNet.RepresentationModel.YamlNode GetChild(
        YamlDotNet.RepresentationModel.YamlMappingNode node, string key)
    {
        if (node == null) return null;
        foreach (var kv in node.Children)
            if (kv.Key.ToString() == key) return kv.Value;
        return null;
    }

    private static string ToYaml(YamlDotNet.RepresentationModel.YamlNode node)
    {
        var sw = new StringWriter();
        new YamlStream(new YamlDocument(node)).Save(sw, false);
        return sw.ToString().TrimEnd();
    }

    private static string GuessKind(string name, string value)
    {
        if (value is "true" or "false") return "bool";
        if (double.TryParse(value, out _)) return "number";
        if (name.Contains("Type") || name.Contains("Mode")) return "select";
        return "text";
    }

    // ---------------- 状态 ----------------
    private string _status = "就绪";
    public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }

    private string _backend = "本地引擎";
    public string Backend
    {
        get => _backend;
        set { _backend = value; OnPropertyChanged(); OnPropertyChanged(nameof(BackendText)); }
    }

    public string BackendText => $"后台状态:({Backend})";

    private string _license = "未授权";
    public string License { get => _license; set { _license = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
