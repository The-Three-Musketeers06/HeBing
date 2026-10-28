using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Algorithm.Impl;
using AiVisionStudio.Models;

namespace AiVisionStudio.Services;

/// <summary>算子实现注册表：driver 名 -> 算法实现。</summary>
public static class OperatorRegistry
{
    private static readonly Dictionary<string, IOperator> _ops = new(StringComparer.OrdinalIgnoreCase);

    static OperatorRegistry()
    {
        Register(new LoadImageOp());
        Register(new DeviceAcquireOp());
        Register(new NativeCaptureOp());
        Register(new DeviceExchangeOp());
        Register(new SaveImageOp());
        Register(new SaveRenderImage2DOp());
        Register(new ThresholdOp());
        Register(new MeanFilterOp());
        Register(new GaussianFilterOp());
        Register(new MedianFilterOp());
        Register(new SobelFilterOp());
        Register(new BlobDetectOp());
        Register(new LineFindOp());
        Register(new CircleFindOp());
        Register(new EllipseFind2DOp());
        Register(new EdgePointFind2DOp());

        // 滤波 / 增强 / 颜色（其余 2D 算子）
        Register(new AutoThresholdOp());
        Register(new AdaptiveThresholdOp());
        Register(new SauvolaThresholdOp());
        Register(new BilateralFilterOp());
        Register(new ClaheOp());
        Register(new ContrastOp());
        Register(new GammaOp());
        Register(new HistogramEqualizeOp());
        Register(new HistogramStretchOp());
        Register(new LaplacianFilterOp());
        Register(new LinearTransformOp());
        Register(new MeanStdNormalizeOp());
        Register(new PetwittFilterOp());
        Register(new RobertsFilterOp());
        Register(new SharpenOp());
        Register(new FlipOp());
        Register(new ResizeOp());
        Register(new RotateOp());
        Register(new MorphOp("Basic Morph Erode", OpenCvSharp.MorphTypes.Erode));
        Register(new MorphOp("Basic Morph Dilate", OpenCvSharp.MorphTypes.Dilate));
        Register(new MorphOp("Basic Morph Close", OpenCvSharp.MorphTypes.Close));
        Register(new MorphOp("Basic Morph Open", OpenCvSharp.MorphTypes.Open));
        Register(new PolarUnwrapOp());
        Register(new PolarRevertOp());
        Register(new DepthToGrayOp());
        Register(new Gray16To8Op());
        Register(new ColorConvertOp());
        Register(new ChannelExtractOp());
        Register(new ChannelSplitOp());

        // 测量
        Register(new P2PMeasureOp());
        Register(new P2LMeasureOp());
        Register(new P2CMeasureOp());
        Register(new P2EMeasureOp());
        Register(new C2CMeasureOp());
        Register(new L2LMeasureOp());
        Register(new L2LAngleOp());
        Register(new L2EMeasureOp());
        Register(new L2CMeasureOp());
        Register(new P2PMidOp());
        Register(new PixelStatisticsOp());
        Register(new ClarityOp());
        Register(new EdgeWidthOp());

        // 拟合 / 几何构造
        Register(new LineFitOp());
        Register(new CircleFitOp());
        Register(new EllipseFitOp());
        Register(new CaliperLineOp());
        Register(new GeometryConstructOp("2D Angle Bisector Detection", "bisector"));
        Register(new GeometryConstructOp("2D Middle Line Search", "middle"));
        Register(new GeometryConstructOp("2D Parallel Line Calculation", "parallel"));
        Register(new GeometryConstructOp("2D Vertical Line Search", "perpendicular"));

        // ROI / 匹配
        Register(new CreateRoiOp());
        Register(new CreateRoiOp("CreateROI"));
        Register(new Region3DOp());
        Register(new CloudTransformOp());
        Register(new CentroidOp());
        Register(new Distance3DOp());
        Register(new Display3DOp());
        Register(new Circle3DOp());
        Register(new CylinderOp());
        Register(new ProfileCaptureOp());
        Register(new GeometryCreate3DOp());

        // 控制流 / 判定 / 数据
        Register(new GroupOp("Group"));
        Register(new GroupOp("End Group"));
        foreach (var driver in new[] { "If", "Else", "End If", "Mark", "Jump", "Call", "Parallel Call" }) Register(new GroupOp(driver));
        Register(new LuaOp());
        Register(new JudgeOp());
        Register(new DelayOp());
        Register(new ValueCheckOp());
        Register(new ReadRecipeFileOp());
        Register(new SwitchRecipeOp());
        Register(new PinMeasurementOp());
        Register(new PinMeasurementOp(true));
        Register(new CloudSetOp());
        Register(new CloudCompareOp());
        Register(new CloudMergeOp());
        Register(new CloudMergeOp(true));
        Register(new Angle3DOp());
        Register(new ValueSettingOp());
        Register(new Blob3DOp());
        Register(new WriteCsvOp());
        Register(new CreateRoiArrayOp());
        Register(new Gap3DOp());
        Register(new DisplayData3DOp());
        Register(new CreateTableOp());
        Register(new WriteTableOp());
        Register(new ShapeMatchOp());
        Register(new ShapeMatchOp(ContourTemplateEngine.GrayDriver));
        Register(new MatrixCodeReadOp());

        // 入门示例补齐
        Register(new CropImageOp());
        Register(new DisplayDataOp());
        Register(new ShapeModelOp());
        Register(new SealantOp());
        Register(new LineIntersectionOp());
        Register(new CreateGeometry2DOp());

        // 3D 点云
        Register(new LoadPointCloudOp());
        Register(new SavePointCloudOp());
        Register(new SequentialProcessingOp());
        Register(new PointCloudToDepthImageOp());
        Register(new DepthImageToPointCloudOp());
        Register(new TrajectorySealant3DOp());
        Register(new CutFilterOp());
        Register(new PlaneOp());
        Register(new HeightOp("3D Height"));
        Register(new RectProbe3DOp());
        Register(new Defect3DOp());
        Register(new Flatness3DOp());
        Register(new GeometryIntersect3DOp());
        Register(new PositionAdjustment3DOp());
        Register(new SolderJoint3DOp());
        Register(new CreateRoiOp());
        Register(new CreateRoiOp("CreateROI"));
        Register(new Region3DOp());
        Register(new CloudTransformOp());
        Register(new CentroidOp());
        Register(new Distance3DOp());
        Register(new Display3DOp());
        Register(new Circle3DOp());
        Register(new CylinderOp());
        Register(new ProfileCaptureOp());
        Register(new GeometryCreate3DOp());

        // 控制流 / 判定 / 数据
        Register(new GroupOp("Group"));
        Register(new GroupOp("End Group"));
        Register(new LuaOp());
        Register(new Blob3DOp());
        Register(new WriteCsvOp());
        Register(new CloudFilterOp("3D PercentileFilter"));
        Register(new CloudFilterOp("3D MedianFilter"));
        Register(new CloudFilterOp("3D GaussianFilter"));
        Register(new CloudFilterOp("3D DownSampleFilter"));
        Register(new CloudFilterOp("3D OutlierRemovalFilter"));
        Register(new CloudFilterOp("3D HoleFillingFilter"));
        GeometryCompletionRegistration.Register();
        ProfileCompletionRegistration.Register();
        MetrologyCompletionRegistration.Register();
        NativeDeviceRegistration.Register();
        CloudGapCompletionRegistration.Register();
    }

    public static void Register(IOperator op) => _ops[op.Driver] = op;
    public static IOperator Get(string driver)
        => driver != null && _ops.TryGetValue(driver, out var o) ? o : null;
    public static int Count => _ops.Count;
    public static IEnumerable<string> Drivers => _ops.Keys;
}

/// <summary>Resumable execution with structured branches, labels, call frames and cancellation.</summary>
public partial class ExecutionEngine
{
    public OpContext Context { get; } = new();
    private bool _preserveOutputsOnStart;
    private int _parallelDepth;
    private readonly Dictionary<string, List<Operation>> _programs = new(StringComparer.Ordinal);
    private readonly Stack<Frame> _frames = new();
    private List<Operation> _entry;
    private CancellationTokenSource _cancellation = new();
    private Operation _breakpoint;
    private volatile bool _pauseRequested;
    public void RequestPause()=>_pauseRequested=true;
    private int _executed;
    public int MaxOperations { get; set; } = 100000;
    public int LastOkCount { get; private set; }
    public int LastFailCount { get; private set; }
    public event Action<Operation> OperationCompleted;
    public bool IsPaused { get; private set; }
    public bool IsCompleted => _entry != null && _frames.Count == 0;
    public Operation NextOperation => _frames.Count > 0 && _frames.Peek().Position < _frames.Peek().Operations.Count
        ? _frames.Peek().Operations[_frames.Peek().Position] : null;

    public void ConfigurePrograms(IEnumerable<ProgramModel> programs)
    {
        var list = programs.ToList();
        _programs.Clear();
        foreach (var program in list) _programs[program.Name] = program.Operations.ToList();
        RecipeRuntime.Configure(Context, list);
        Context.Devices=NativeDeviceConfiguration.Read(list.FirstOrDefault()?.Root);
    }

    public void Stop()
    {
        _pauseRequested = false;
        _cancellation.Cancel();
        if (IsPaused)
        {
            foreach (var frame in _frames)
                if (frame.Caller != null) { frame.Caller.State = OpState.None; frame.Caller.ErrorMessage = "运行已停止"; }
            _frames.Clear();
            IsPaused = false;
            _pauseRequested = false;
            _breakpoint = null;
        }
    }

    /// <summary>Discard the paused instruction/call stack after the project is edited.</summary>
    public void InvalidateExecution()
    {
        _pauseRequested = false;
        _frames.Clear();
        _entry = null;
        _breakpoint = null;
        IsPaused = false;
    }

    public int Run(IEnumerable<Operation> operations, Action<string, bool> log, CancellationToken cancellationToken = default)
        => Execute(operations, log, false, 0, cancellationToken);

    public int Step(IEnumerable<Operation> operations, Action<string, bool> log, int startIndex = 0, CancellationToken cancellationToken = default)
        => Execute(operations, log, true, startIndex, cancellationToken);

    public int TestThrough(IEnumerable<Operation> operations, Operation target, Action<string, bool> log, CancellationToken cancellationToken = default)
    {
        InvalidateExecution();
        return Execute(operations, log, false, 0, cancellationToken, target);
    }

    private int Execute(IEnumerable<Operation> operations, Action<string, bool> log, bool singleStep, int startIndex, CancellationToken cancellationToken, Operation testTarget = null)
    {
        var list = operations.ToList();
        var swAll = Stopwatch.StartNew();
        log ??= (_, _) => { };
        Context.Log = log;
        try
        {
            bool resume = IsPaused && _entry != null && _entry.SequenceEqual(list);
            if (!resume)
            {
                _pauseRequested = false;
                _frames.Clear();
                _entry = list;
                _cancellation.Dispose();
                _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Context.CancellationToken = _cancellation.Token;
                LastOkCount = LastFailCount = _executed = 0;
                _breakpoint = null;
                if ((!singleStep || startIndex == 0) && !_preserveOutputsOnStart) Context.OpOutputs.Clear();
                _preserveOutputsOnStart = false;
                foreach (var op in list.Concat(_programs.Values.SelectMany(p => p)).Distinct())
                {
                    op.State = op.Enabled ? OpState.None : OpState.Disabled;
                    op.ElapsedMs = 0;
                    op.ErrorMessage = null;
                }
                _frames.Push(new Frame(list) { Position = Math.Clamp(startIndex, 0, list.Count) });
            }
            // Each resume has a new UI request token. Keep the paused engine token, but
            // connect this request for the entire resumed execution (including Lua).
            using var cancellationRegistration = cancellationToken.Register(_cancellation.Cancel);
            cancellationToken.ThrowIfCancellationRequested();
            IsPaused = false;
            int steps = 0;
            while (_frames.Count > 0)
            {
                Context.CancellationToken.ThrowIfCancellationRequested();
                CompleteFrames(log);
                if (testTarget != null && testTarget.State is OpState.Ok or OpState.Fail) break;
                if (_frames.Count == 0) break;
                if (_pauseRequested) { IsPaused=true;_pauseRequested=false;break; }
                var frame = _frames.Peek();
                if (testTarget != null && ReferenceEquals(frame.Operations, list) && frame.Position > list.IndexOf(testTarget)) break;
                var op = frame.Operations[frame.Position];
                if (!op.Enabled)
                {
                    op.State = OpState.Disabled;
                    if (frame.Ends.TryGetValue(frame.Position, out var end))
                    {
                        for (var i = frame.Position; i <= end; i++) frame.Operations[i].State = OpState.Disabled;
                        frame.Position = end + 1;
                    }
                    else frame.Position++;
                    continue;
                }
                if (singleStep && steps > 0) { IsPaused = true; break; }
                if (testTarget == null && !singleStep && op.Breakpoint && !ReferenceEquals(_breakpoint, op))
                {
                    _breakpoint = op;
                    IsPaused = true;
                    log($"断点：{op.Name}（{op.Driver}），已暂停", false);
                    break;
                }
                _breakpoint = null;
                if (++_executed > MaxOperations)
                    throw new InvalidOperationException($"运行超过 {MaxOperations} 个操作，可能存在无限跳转");
                RunOperation(frame, op, log);
                steps++;
            }
        }
        catch (OperationCanceledException)
        {
            if (NextOperation is { State: OpState.Running } cancelled) cancelled.State = OpState.None;
            foreach (var frame in _frames)
                if (frame.Caller != null) { frame.Caller.State = OpState.None; frame.Caller.ErrorMessage = "运行已停止"; }
            _frames.Clear();
            IsPaused = false;
            log("运行已停止", false);
        }
        catch (Exception ex)
        {
            if (NextOperation is { } op) { op.State = OpState.Fail; op.ErrorMessage = ex.Message; }
            LastFailCount++;
            foreach (var frame in _frames)
                if (frame.Caller != null) { frame.Caller.State = OpState.Fail; frame.Caller.ErrorMessage = ex.Message; }
            _frames.Clear();
            IsPaused = false;
            log($"运行失败：{ex.Message}", true);
        }
        finally
        {
            Context.Log = (_, _) => { };
            if (testTarget != null) { _frames.Clear(); IsPaused = false; _breakpoint = null; }
        }
        log($"{(IsPaused ? "运行暂停" : "运行结束")}：OK {LastOkCount}，NG {LastFailCount}，本次耗时 {swAll.Elapsed.TotalMilliseconds:0} ms", LastFailCount > 0);
        return LastOkCount;
    }

    private void RunOperation(Frame frame, Operation op, Action<string, bool> log)
    {
        var sw = Stopwatch.StartNew();
        var errors = new List<string>();
        Context.Log = (message, error) => { if (error) errors.Add(message); log(message, error); };
        Context.Vars = new VarPool();
        Context.OperationUuid=op.Uuid;
        Context.NamedOutputSources.Clear();
        foreach(var source in frame.Operations)Context.NamedOutputSources[source.Name]=source.Uuid;
        Context.OpOutputs.Remove(op.Uuid);
        op.State = OpState.Running;
        op.ErrorMessage = null;
        bool deferred = false;
        try
        {
            var p = ParamBag.FromYaml(op.ParametersRaw as YamlDotNet.RepresentationModel.YamlNode, null, Context);
            int current = frame.Position++;
            bool success = true;
            switch (op.Driver)
            {
                case "If":
                    var condition = ControlValues.Evaluate(p);
                    Context.Vars.Set("Result", condition);
                    if (!condition) frame.Position = frame.Alternatives.TryGetValue(current, out var alternative) ? alternative + 1 : frame.Ends[current];
                    break;
                case "Else": frame.Position = frame.Ends[current]; break;
                case "End If": case "Group": case "End Group": case "Mark": break;
                case "Jump":
                    if (ControlValues.Bool(ControlValues.Read(p, "Active", true)) ^ p.Bool("Reverse Active"))
                        frame.Position = frame.FindLabel(Convert.ToString(ControlValues.Read(p, "Target", "")));
                    break;
                case "Call":
                    var target = Convert.ToString(ControlValues.Read(p, "Target", ""));
                    if (!_programs.TryGetValue(target, out var program)) throw new InvalidOperationException($"子程序不存在：{target}");
                    if (_frames.Count >= 128) throw new InvalidOperationException("子程序调用超过 128 层");
                    _frames.Push(new Frame(program) { Caller = op, FailureCount = LastFailCount, Watch = sw });
                    deferred = true;
                    break;
                case "Parallel Call":
                    success = RunParallel(p, log);
                    break;
                default:
                {
                    var implementation = OperatorRegistry.Get(op.Driver) ?? throw new NotSupportedException($"未实现：{op.Driver}");
                    using var imageInputs = PrepareImageInputs(op, p);
                    PrepareContourInput(op, p);
                    PrepareOperationResults(op, p);
                    RemoveResultGraphics(op.Uuid);
                    using (var capture = new VisualCapture(this, p))
                    {
                        try { success = implementation.Run(Context, p); }
                        catch { capture.Complete(p); RecordRuntimeResults(op, capture); throw; }
                        //必须在下一个控件执行前复制
                        //即使结果为NG，只要实际产生了有效图像，也可以保存
                        PublishVisualSnapshots(op,p, capture);
                    }
                    if (success && Context.Vars.Get("__JumpTarget") is string label) frame.Position = frame.FindLabel(label);
                    break;
                }
            }
            if (success || op.Driver == "Parallel Call") Context.OpOutputs[op.Uuid] = Context.Vars;
            if (!deferred) Finish(op, success, errors.Count > 0 ? string.Join("; ", errors) : "返回 NG");
        }
        catch (OperationCanceledException)
        {
            op.State = OpState.None;
            op.ErrorMessage = "运行已停止";
            throw;
        }
        catch (Exception ex)
        {
            Finish(op, false, ex.GetType().Name + ": " + ex.Message);
            log($"{op.Name}：{ex.Message}", true);
            // A failed condition must never execute the true branch using missing data.
            if (op.Driver == "If" && frame.Ends.TryGetValue(frame.Position - 1, out var end)) frame.Position = end + 1;
        }
        finally
        {
            Context.Log = log;
            if (!deferred)
            {
                op.ElapsedMs = sw.Elapsed.TotalMilliseconds;
                if (op.State is OpState.Ok or OpState.Fail){PublishStatus(op);OperationCompleted?.Invoke(op);}
            }
        }
    }

    private void Finish(Operation op, bool success, string error)
    {
        op.State = success ? OpState.Ok : OpState.Fail;
        op.ErrorMessage = success ? null : error;
        if (success) LastOkCount++; else LastFailCount++;
    }

    private void PublishStatus(Operation op)
    {
        // Failed operators expose status, never stale or partially computed measurement values.
        var output = (op.State == OpState.Ok || op.Driver is "Call" or "Parallel Call") && Context.OpOutputs.TryGetValue(op.Uuid, out var values) ? values : new VarPool();
        if (output.Get("Result") == null) output.Set("Result", op.State == OpState.Ok);
        if (output.Get("Warning") == null) output.Set("Warning", false);
        if (op.State == OpState.Fail || output.Get("Message") == null) output.Set("Message", op.ErrorMessage ?? "");
        output.Set("Time", op.ElapsedMs);
        Context.OpOutputs[op.Uuid] = output;
    }

    private void CompleteFrames(Action<string, bool> log)
    {
        while (_frames.Count > 0 && _frames.Peek().Position >= _frames.Peek().Operations.Count)
        {
            var frame = _frames.Pop();
            if (frame.Caller != null)
            {
                Context.OpOutputs[frame.Caller.Uuid] = ProgramOutputs(frame.Operations, Context, LastFailCount == frame.FailureCount);
                _resultDependencies[frame.Caller.Uuid] = frame.Operations.Where(op => op.Uuid != frame.Caller.Uuid && Context.OpOutputs.ContainsKey(op.Uuid)).Select(op => op.Uuid).ToHashSet();
                Finish(frame.Caller, LastFailCount == frame.FailureCount, "子程序执行失败");
                frame.Caller.ElapsedMs = frame.Watch.Elapsed.TotalMilliseconds;
                PublishStatus(frame.Caller);
            }
        }
    }

    public void Reset()
    {
        Stop();
        _frames.Clear();
        _entry = null;
        IsPaused = false;
        Context.Images.Dispose();
        ClearResultLifetime();
        Context.DeviceRuntime.Dispose();
        Context.Clouds.Clear();
        Context.Profiles.Clear();
        Context.OpOutputs.Clear();
        Context.NamedOutputSources.Clear();
        Context.Vars.Clear();
        Context.Globals.Clear();
        Context.DrawTexts.Clear();
        Context.Texts.Clear();
        Context.Boxes.Clear();
        Context.NativeOverlays.Clear();
        Context.InputFiles.Clear();
        Context.OperationUuid="";
    }

    private sealed class Frame
    {
        public List<Operation> Operations { get; }
        public int Position;
        public Operation Caller;
        public int FailureCount;
        public Stopwatch Watch;
        public Dictionary<int, int> Ends { get; } = new();
        public Dictionary<int, int> Alternatives { get; } = new();
        private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);

        public Frame(List<Operation> operations)
        {
            Operations = operations;
            var stack = new Stack<(string Driver, int Index)>();
            for (int i = 0; i < operations.Count; i++)
            {
                var op = operations[i];
                switch (op.Driver)
                {
                    case "If": case "Group": stack.Push((op.Driver, i)); break;
                    case "Else":
                        if (stack.Count == 0 || stack.Peek().Driver != "If" || Alternatives.ContainsKey(stack.Peek().Index))
                            throw new InvalidOperationException($"Else 缺少对应 If：{op.Name}");
                        Alternatives[stack.Peek().Index] = i;
                        break;
                    case "End If": case "End Group":
                        var expected = op.Driver == "End If" ? "If" : "Group";
                        if (stack.Count == 0 || stack.Peek().Driver != expected) throw new InvalidOperationException($"{op.Driver} 嵌套不匹配");
                        var start = stack.Pop().Index;
                        Ends[start] = i;
                        if (Alternatives.TryGetValue(start, out var alternative)) Ends[alternative] = i;
                        break;
                    case "Mark":
                        var label = ParamBag.FromYaml(op.ParametersRaw as YamlDotNet.RepresentationModel.YamlNode).Str("Mark");
                        if (string.IsNullOrEmpty(label) || !_labels.TryAdd(label, i)) throw new InvalidOperationException($"跳转标记为空或重复：{label}");
                        break;
                }
            }
            if (stack.Count > 0) throw new InvalidOperationException($"{stack.Peek().Driver} 缺少结束节点");
        }

        public int FindLabel(string target) => _labels.TryGetValue(target, out var index) ? index
            : throw new InvalidOperationException($"跳转标记不存在：{target}");
    }
}
