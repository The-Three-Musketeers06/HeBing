using System.Collections;
using System.Diagnostics;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Models;

namespace AiVisionStudio.Services;

public partial class ExecutionEngine
{
    private sealed record ParallelBranch(ExecutionEngine Engine,List<Operation> Program,HashSet<int> Writes,bool Overlays,List<(string Text,bool Error)> Logs,Dictionary<string,Operation> Originals)
    {
        public Stopwatch Watch { get; } = new();
    }

    private bool RunParallel(ParamBag p,Action<string,bool> log)
    {
        var count=p.Int("Target Count",2);
        if(count<1||count>8)throw new ArgumentException("并行调用目标数量必须在 1..8 范围内");
        if(_parallelDepth+_frames.Count>=128)throw new InvalidOperationException("子程序调用超过 128 层");
        var initialGlobals=CopyPool(Context.Globals,true);
        var branches=new List<ParallelBranch>();var owners=new Dictionary<int,int>();
        var invalidations = new System.Collections.Concurrent.ConcurrentQueue<IReadOnlySet<string>>();
        var invalidationGate = new object();
        try
        {
            for(int i=1;i<=count;i++)
            {
                var target=p.Group("Target"+i)??throw new ArgumentException("并行目标配置不存在：Target"+i);
                var name=Convert.ToString(target.GetValueOrDefault("Program"));
                if(name==null||!_programs.ContainsKey(name))throw new InvalidOperationException("子程序不存在："+name);
                HashSet<int> Permission(string key)
                    => (target.GetValueOrDefault(key) as IReadOnlyDictionary<string,object> ?? new Dictionary<string,object>())
                        .Where(kv=>kv.Value is true).Select(kv=>kv.Key.StartsWith("IM")&&int.TryParse(kv.Key[2..],out var slot)&&slot>=0&&slot<32?slot:throw new ArgumentException("并行 IM 权限编号无效")).ToHashSet();
                var reads=Permission("IM Read Permission");var writes=Permission("IM Write Permission");
                foreach(var slot in writes)if(!owners.TryAdd(slot,i))throw new InvalidOperationException($"并行目标 Target{owners[slot]} 和 Target{i} 同时请求 IM{slot} 写权限");
                var engine=new ExecutionEngine{MaxOperations=Math.Max(1,MaxOperations-_executed),_preserveOutputsOnStart=true,_parallelDepth=_parallelDepth+_frames.Count};
                engine.ImageInputResolver = ImageInputResolver;
                engine.ImageInputOverlayResolver = ImageInputOverlayResolver;
                engine.ContourTemplateInvalidated += operation => ContourTemplateInvalidated?.Invoke(operation);
                engine._allowedImageReads = reads.Concat(writes).ToHashSet();
                engine.ResultsInvalidated += ids =>
                {
                    lock (invalidationGate)
                    {
                        var affected = AffectedResults(ids);
                        invalidations.Enqueue(affected);
                        ResultsInvalidated?.Invoke(affected);
                    }
                };
                // 子引擎完成每个控件时，直接转发独立快照。
                engine.VisualSnapshotReady += snapshot =>
                {
                    var handler = VisualSnapshotReady;

                    if (handler == null)
                        snapshot.Dispose();
                    else
                        handler(snapshot);
                };
                engine.VisualSnapshotsPublished += (id, keys) => VisualSnapshotsPublished?.Invoke(id, keys);
                var originals=new Dictionary<string,Operation>();
                foreach(var entry in _programs)
                {
                    engine._programs[entry.Key]=entry.Value.Select(op=>{
                        originals[op.Uuid]=op;
                        return new Operation{Uuid=op.Uuid,Name=op.Name,Driver=op.Driver,Version=op.Version,Enabled=op.Enabled,ParametersRaw=op.ParametersRaw,Index=op.Index};
                    }).ToList();
                }
                var child=engine.Context;
                foreach (var selection in Context.SelectedInputFiles) child.SelectedInputFiles[selection.Key] = selection.Value;
                child.ProjectDir=Context.ProjectDir;child.WriteRoot=Context.WriteRoot;child.RecipePath=Context.RecipePath;
                foreach(var recipe in Context.Recipes)child.Recipes[recipe.Key]=recipe.Value;
                child.Devices=Context.Devices;
                child.DeviceRuntime=Context.DeviceRuntime;
                child.Globals=CopyPool(initialGlobals,true);
                foreach(var kv in Context.OpOutputs)child.OpOutputs[kv.Key]=CopyPool(kv.Value);
                foreach(var kv in Context.InputFiles)child.InputFiles[kv.Key]=kv.Value;
                foreach(var slot in reads)
                {
                    if(Context.Images.Get(slot) is {} image)
                    {
                        child.Images.Set(slot,image.Clone());
                        if(Context.Images.GetDepthGeometry(slot) is {} geometry)child.Images.SetDepthGeometry(slot,geometry);
                    }
                    if(Context.Clouds.Get(slot) is {} cloud)
                    {
                        child.Clouds.Set(slot,cloud.Clone());
                        if(Context.Clouds.GetPointSets(slot) is {} sets)child.Clouds.SetPointSets(slot,sets);
                    }
                    if(Context.Profiles.TryGetValue(slot,out var profile))child.Profiles[slot]=profile.Clone();
                }
                foreach (var pair in _runtimeVisuals.Where(pair => reads.Contains(pair.Key.Slot)))
                    if (engine.RuntimeValue(pair.Key) is { } value) engine._runtimeVisuals[pair.Key] = new RuntimeVisual(pair.Value.OperationId, value);
                // Every branch owns its image memory. A write-only slot starts empty.
                child.Images.CheckWrite=slot=>CheckWrite(slot,writes);
                child.Clouds.CheckWrite=slot=>CheckWrite(slot,writes);
                branches.Add(new(engine,engine._programs[name],writes,target.GetValueOrDefault("Show Overlays") is not false,new(),originals));
            }
            var tasks=branches.Select(branch=>Task.Run(()=>{
                branch.Watch.Start();
                try{return branch.Engine.Run(branch.Program,(text,error)=>branch.Logs.Add((text,error)),Context.CancellationToken);}
                finally{branch.Watch.Stop();}
            },Context.CancellationToken)).ToArray();
            Task.WhenAll(tasks).GetAwaiter().GetResult();
            Context.CancellationToken.ThrowIfCancellationRequested();
            while (invalidations.TryDequeue(out var invalid)) InvalidateResults(invalid, false);
            // Reject conflicting global writes before committing any branch to the parent.
            var recipeChanges=branches.Select(b=>b.Engine.Context.RecipePath).Where(path=>path!=Context.RecipePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if(recipeChanges.Length>1)throw new InvalidOperationException("并行子程序切换配方冲突");
            var changes=new Dictionary<string,object>(StringComparer.Ordinal);
            foreach(var branch in branches)foreach(var kv in branch.Engine.Context.Globals.All)
            {
                if(initialGlobals.TryGet(kv.Key,out var old)&&Same(old,kv.Value))continue;
                if(changes.TryGetValue(kv.Key,out var other)&&!Same(other,kv.Value))throw new InvalidOperationException("并行子程序写入冲突的全局变量："+kv.Key);
                changes[kv.Key]=kv.Value;
            }
            foreach(var kv in changes)Context.Globals.Set(kv.Key,CopyValue(kv.Value));
            if(recipeChanges.Length==1)Context.RecipePath=recipeChanges[0];
            var allSuccess=true;
            for(int i=0;i<branches.Count;i++)
            {
                var branch=branches[i];var child=branch.Engine.Context;
                foreach(var entry in branch.Logs)log($"Target{i+1}：{entry.Text}",entry.Error);
                var executed=branch.Engine._programs.Values.SelectMany(v=>v).Where(op=>op.State is OpState.Ok or OpState.Fail).ToArray();
                foreach(var op in executed)
                {
                    Context.OpOutputs[op.Uuid]=child.OpOutputs[op.Uuid];
                    var original=branch.Originals[op.Uuid];original.State=op.State;original.ElapsedMs=op.ElapsedMs;original.ErrorMessage=op.ErrorMessage;
                    OperationCompleted?.Invoke(original);
                }
                foreach(var slot in branch.Writes)
                {
                    if(child.Images.Get(slot) is {} image)
                    {
                        Context.SetImage(slot,image.Clone());
                        if(child.Images.GetDepthGeometry(slot) is {} geometry)Context.Images.SetDepthGeometry(slot,geometry);
                    }
                    if(child.Clouds.Get(slot) is {} cloud)
                    {
                        Context.Images.Remove(slot);Context.Profiles.Remove(slot);Context.Clouds.Set(slot,cloud);
                        if(child.Clouds.GetPointSets(slot) is {} sets)Context.Clouds.SetPointSets(slot,sets);
                    }
                    if(child.Profiles.TryGetValue(slot,out var profile))Context.Profiles[slot]=profile;
                }
                if(branch.Overlays)
                {
                    foreach (var operation in executed) RemoveResultGraphics(operation.Uuid);
                    var overlayOwners=executed.Select(o=>o.Uuid).ToHashSet();
                    Context.Texts.RemoveAll(t=>overlayOwners.Contains(t.Owner));
                    Context.NativeOverlays.RemoveAll(o=>o.Values.TryGetValue("_owned_operation",out var owner)&&overlayOwners.Contains(Convert.ToString(owner)));
                    Context.Texts.AddRange(child.Texts);Context.Boxes.AddRange(child.Boxes);Context.DrawTexts.AddRange(child.DrawTexts);Context.NativeOverlays.AddRange(child.NativeOverlays);
                }
                ImportResultLifetime(branch.Engine, branch.Writes, executed);
                LastOkCount+=branch.Engine.LastOkCount;LastFailCount+=branch.Engine.LastFailCount;_executed+=branch.Engine._executed;
                var ok=branch.Engine.LastFailCount==0&&branch.Engine.IsCompleted;
                allSuccess&=ok;
                var targetOutput=ProgramOutputs(branch.Program,child,ok);
                targetOutput.Set("Time",branch.Watch.Elapsed.TotalMilliseconds);targetOutput.Set("_driver","Parallel Call");
                targetOutput.Set("Message",ok?"":"子程序执行失败");
                Context.Vars.Set("Target"+(i+1),targetOutput.All);
            }
            if(_executed>MaxOperations)throw new InvalidOperationException("并行调用超过操作次数限制");
            _resultDependencies[Context.OperationUuid] = branches.SelectMany(branch => branch.Engine._programs.Values.SelectMany(ops => ops))
                .Where(op => op.State is OpState.Ok or OpState.Fail).Select(op => op.Uuid).ToHashSet();
            Context.Vars.Set("Result",allSuccess&&branches.All(b=>!ProgramOutputs(b.Program,b.Engine.Context,true).All.TryGetValue("Result",out var result)||result is true));
            Context.Vars.Set("Warning",branches.Any(b=>ProgramOutputs(b.Program,b.Engine.Context,true).Get("Warning") is true));
            return allSuccess;
        }
        finally{foreach(var branch in branches)branch.Engine.Context.Images.Dispose();}
    }
    private static void CheckWrite(int slot,HashSet<int> writes)
    {if(!writes.Contains(slot))throw new InvalidOperationException($"并行子程序没有 IM{slot} 写权限");}

    private static VarPool ProgramOutputs(List<Operation> operations,OpContext context,bool successful)
    {
        var result=new VarPool(true);bool warning=false;
        for(int i=0;i<operations.Count;i++)if(context.OpOutputs.TryGetValue(operations[i].Uuid,out var output)&&operations[i].State is OpState.Ok or OpState.Fail)
        {
            var values=CopyPool(output,true);values.Set("_driver",operations[i].Driver);
            result.Set(i+"-"+operations[i].Name,values.All);
            if(output.Get("Result") is false)successful=false;
            warning|=output.Get("Warning") is true;
        }
        result.Set("Result",successful);result.Set("Warning",warning);return result;
    }
    private static VarPool CopyPool(VarPool source,bool sensitive=true)
    {var pool=new VarPool(sensitive);foreach(var kv in source.All)pool.Set(kv.Key,CopyValue(kv.Value));return pool;}
    private static object CopyValue(object value)=>value switch
    {
        VarPool pool=>CopyPool(pool,true),
        IReadOnlyDictionary<string,object> map=>map.ToDictionary(k=>k.Key,k=>CopyValue(k.Value),StringComparer.Ordinal),
        IList list=>list.Cast<object>().Select(CopyValue).ToList(),
        _=>value
    };
    private static bool Same(object a,object b)
    {
        if(ReferenceEquals(a,b))return true;
        if(a is IList x&&b is IList y)return x.Count==y.Count&&x.Cast<object>().Zip(y.Cast<object>()).All(p=>Same(p.First,p.Second));
        if(a is IReadOnlyDictionary<string,object> m&&b is IReadOnlyDictionary<string,object> n)return m.Count==n.Count&&m.All(k=>n.TryGetValue(k.Key,out var v)&&Same(k.Value,v));
        return Equals(a,b);
    }
}
