using AiVisionStudio.Algorithm;
using AiVisionStudio.Models;

namespace AiVisionStudio.Services;

public partial class ExecutionEngine
{
    private sealed record RuntimeVisual(string OperationId, object Value);
    private readonly Dictionary<(VisualResultKind Kind, int Slot), RuntimeVisual> _runtimeVisuals = new();
    private readonly Dictionary<string, HashSet<string>> _resultDependencies = new();
    private readonly Dictionary<string, HashSet<object>> _resultGraphics = new();
    private readonly Dictionary<string, string[]> _resultTexts = new();
    public event Action<IReadOnlySet<string>> ResultsInvalidated;

    private object RuntimeValue((VisualResultKind Kind, int Slot) key) => key.Kind switch
    {
        VisualResultKind.Image => Context.Images.All.GetValueOrDefault(key.Slot),
        VisualResultKind.Cloud => Context.Clouds.All.FirstOrDefault(pair => pair.Key == key.Slot).Value,
        _ => Context.Profiles.GetValueOrDefault(key.Slot)
    };

    private void RemoveRuntimeVisual((VisualResultKind Kind, int Slot) key, RuntimeVisual owned)
    {
        if (!ReferenceEquals(RuntimeValue(key), owned.Value)) return;
        if (key.Kind == VisualResultKind.Image) Context.Images.Remove(key.Slot);
        else if (key.Kind == VisualResultKind.Cloud) Context.Clouds.Remove(key.Slot);
        else Context.Profiles.Remove(key.Slot);
    }

    private void RemoveResultGraphics(string operationId)
    {
        if (_resultGraphics.Remove(operationId, out var graphics))
        {
            Context.NativeOverlays.RemoveAll(item => graphics.Contains(item));
            Context.Texts.RemoveAll(item => graphics.Contains(item));
            Context.Boxes.RemoveAll(item => graphics.Contains(item));
        }
        if (_resultTexts.Remove(operationId, out var texts))
            foreach (var text in texts) Context.DrawTexts.Remove(text);
    }

    private HashSet<string> AffectedResults(IEnumerable<string> operationIds)
    {
        var invalid = operationIds.ToHashSet(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var pair in _resultDependencies)
                if (!invalid.Contains(pair.Key) && pair.Value.Overlaps(invalid)) changed |= invalid.Add(pair.Key);
        } while (changed);
        return invalid;
    }

    public IReadOnlySet<string> InvalidateResults(IEnumerable<string> operationIds, bool notify = true)
    {
        var invalid = AffectedResults(operationIds);
        foreach (var pair in _runtimeVisuals.Where(pair => invalid.Contains(pair.Value.OperationId)).ToArray())
        {
            RemoveRuntimeVisual(pair.Key, pair.Value);
            _runtimeVisuals.Remove(pair.Key);
        }
        foreach (var id in invalid)
        {
            if (Context.OpOutputs.Remove(id, out var output) && ReferenceEquals(Context.Vars, output)) Context.Vars = new VarPool();
            _resultDependencies.Remove(id);
            RemoveResultGraphics(id);
        }
        foreach (var name in Context.NamedOutputSources.Where(pair => invalid.Contains(pair.Value)).Select(pair => pair.Key).ToArray()) Context.NamedOutputSources.Remove(name);
        foreach (var key in Context.InputFiles.Keys.Where(key => invalid.Any(id => key.StartsWith(id + "|", StringComparison.Ordinal))).ToArray()) Context.InputFiles.Remove(key);
        if (notify) ResultsInvalidated?.Invoke(invalid);
        return invalid;
    }

    private void RecordRuntimeResults(Operation operation, VisualCapture capture)
    {
        var dependents = AffectedResults([operation.Uuid]);
        dependents.Remove(operation.Uuid);
        if (dependents.Count > 0) InvalidateResults(dependents);
        foreach (var pair in _runtimeVisuals.Where(pair => pair.Value.OperationId == operation.Uuid && !capture.Written.Contains(pair.Key)).ToArray())
        {
            RemoveRuntimeVisual(pair.Key, pair.Value);
            _runtimeVisuals.Remove(pair.Key);
        }
        foreach (var key in capture.Written)
            if (RuntimeValue(key) is { } value) _runtimeVisuals[key] = new RuntimeVisual(operation.Uuid, value);
        _resultDependencies[operation.Uuid] = capture.Dependencies;
        _resultGraphics[operation.Uuid] = Context.NativeOverlays.Cast<object>().Concat(Context.Texts).Concat(Context.Boxes)
            .Where(item => !capture.GraphicsBefore.Contains(item)).ToHashSet(ReferenceEqualityComparer.Instance);
        _resultTexts[operation.Uuid] = Context.DrawTexts.Skip(capture.TextCountBefore).ToArray();
    }

    private void PrepareOperationResults(Operation operation, ParamBag parameters)
    {
        if (operation.Driver is "Basic Line Find" or "Basic Circle Find" or "Basic Edge Point Find" or ChannelSplitChannels.Driver or "Basic Color Convert" or "Basic Shape Match" or "Basic NCC Match" or "Basic Shape Model")
        {
            InvalidateResults([operation.Uuid]);
            Context.NamedOutputSources[operation.Name] = operation.Uuid;
            return;
        }
        if (operation.Driver is not ("LoadImage" or "LoadPointCloud")) return;
        var cursors = Context.InputFiles.Where(pair => pair.Key.StartsWith(operation.Uuid + "|", StringComparison.Ordinal)).ToArray();
        InvalidateResults([operation.Uuid]);
        int slot = parameters.Int("IM Out");
        Context.Images.Remove(slot); Context.Clouds.Remove(slot); Context.Profiles.Remove(slot);
        foreach (var key in _runtimeVisuals.Keys.Where(key => key.Slot == slot).ToArray()) _runtimeVisuals.Remove(key);
        foreach (var pair in cursors) Context.InputFiles[pair.Key] = pair.Value;
        Context.NamedOutputSources[operation.Name] = operation.Uuid;
    }

    public void PruneResults(IReadOnlySet<string> alive)
    {
        var removed = _resultDependencies.Keys.Concat(Context.OpOutputs.Keys).Concat(_runtimeVisuals.Values.Select(item => item.OperationId))
            .Where(id => !alive.Contains(id)).Distinct().ToArray();
        if (removed.Length > 0) InvalidateResults(removed);
    }

    public void ClearVisualSlot(int slot)
    {
        var owners = _runtimeVisuals.Where(pair => pair.Key.Slot == slot).Select(pair => pair.Value.OperationId).Distinct().ToArray();
        if (owners.Length > 0) InvalidateResults(owners);
        Context.Images.Remove(slot); Context.Clouds.Remove(slot); Context.Profiles.Remove(slot);
        foreach (var key in _runtimeVisuals.Keys.Where(key => key.Slot == slot).ToArray()) _runtimeVisuals.Remove(key);
        Context.NativeOverlays.RemoveAll(item => item.ViewIndex == slot);
        Context.Texts.RemoveAll(item => item.ViewIndex == slot);
    }

    public void RestoreVisual(VisualSnapshot snapshot)
    {
        int slot = snapshot.AlgorithmSlot;
        if (RuntimeValue((VisualResultKind.Image, slot)) != null || RuntimeValue((VisualResultKind.Cloud, slot)) != null || RuntimeValue((VisualResultKind.Profile, slot)) != null) return;
        if (snapshot.Image != null) Context.SetImage(slot, snapshot.Image.Clone());
        else if (snapshot.Cloud != null) Context.Clouds.Set(slot, snapshot.Cloud.Clone());
        else if (snapshot.Profile != null) Context.Profiles[slot] = snapshot.Profile.Clone();
        var key = (snapshot.Key.Kind, slot);
        if (RuntimeValue(key) is { } value) _runtimeVisuals[key] = new RuntimeVisual(snapshot.Key.OperationId, value);
    }

    private void ClearResultLifetime()
    {
        _runtimeVisuals.Clear(); _resultDependencies.Clear(); _resultGraphics.Clear(); _resultTexts.Clear();
        Context.SelectedInputFiles.Clear();
    }

    internal IReadOnlySet<string> GetImageInputSources(string operationId)
    {
        var sources = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        if (operationId != null) pending.Push(operationId);
        while (pending.TryPop(out var id))
        {
            if (!sources.Add(id)) continue;
            if (_resultDependencies.TryGetValue(id, out var inputs))
                foreach (var input in inputs) pending.Push(input);
        }
        return sources;
    }

    private void ImportResultLifetime(ExecutionEngine child, IReadOnlySet<int> slots, IEnumerable<Operation> executed)
    {
        foreach (var operation in executed)
        {
            if (child._resultDependencies.TryGetValue(operation.Uuid, out var dependencies)) _resultDependencies[operation.Uuid] = new HashSet<string>(dependencies);
            if (child._resultGraphics.TryGetValue(operation.Uuid, out var graphics)) _resultGraphics[operation.Uuid] = new HashSet<object>(graphics, ReferenceEqualityComparer.Instance);
            if (child._resultTexts.TryGetValue(operation.Uuid, out var texts)) _resultTexts[operation.Uuid] = texts;
        }
        foreach (var pair in child._runtimeVisuals.Where(pair => slots.Contains(pair.Key.Slot)))
            if (RuntimeValue(pair.Key) is { } value) _runtimeVisuals[pair.Key] = new RuntimeVisual(pair.Value.OperationId, value);
        foreach (var pair in child.Context.InputFiles) Context.InputFiles[pair.Key] = pair.Value;
    }
}
