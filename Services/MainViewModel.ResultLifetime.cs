using AiVisionStudio.Models;
using YamlDotNet.RepresentationModel;
using static AiVisionStudio.Models.VisualSnapshot;

namespace AiVisionStudio.Services;

public partial class MainViewModel
{
    private readonly Dictionary<string, string[]> _visualFallbacks = new();
    public long LatestVisualVersion { get { lock (_visualSnapshotGate) return _nextVisualVersion; } }

    private void OnResultsInvalidated(IReadOnlySet<string> invalid)
    {
        lock (_visualSnapshotGate)
        {
            foreach (var program in Programs)
                for (int i = 0; i < program.Operations.Count; i++)
                    if (invalid.Contains(program.Operations[i].Uuid))
                        _visualFallbacks[program.Operations[i].Uuid] = program.Operations.Take(i).Reverse().Where(op => !invalid.Contains(op.Uuid)).Select(op => op.Uuid).ToArray();
            foreach (var pair in _visualSnapshots.Where(pair => invalid.Contains(pair.Key.OperationId)).ToArray())
            { _visualSnapshots.Remove(pair.Key); pair.Value.Dispose(); }
            foreach (var id in invalid)
                if (_visualFallbacks.TryGetValue(id, out var predecessors))
                    foreach (var predecessor in predecessors)
                        foreach (var snapshot in _visualSnapshots.Values.Where(item => item.Key.OperationId == predecessor)) _engine.RestoreVisual(snapshot);
        }
        foreach (var operation in Programs.SelectMany(program => program.Operations).Where(op => invalid.Contains(op.Uuid)))
        {
            _nativeRawOutputs.Remove(operation.Uuid);
            if (operation.State != OpState.Running) operation.State = operation.Enabled ? OpState.None : OpState.Disabled;
            operation.ElapsedMs = 0; operation.ErrorMessage = null;
        }
    }

    public IReadOnlyList<VisualSourceInfo> GetFallbackVisualSources(VisualSourceKey source)
    {
        var available = GetVisualSources().Where(item => item.Key.ProgramName == source.ProgramName).ToArray();
        string[] predecessors;
        lock (_visualSnapshotGate)
            if (!_visualFallbacks.TryGetValue(source.OperationId, out predecessors)) return [];
        return predecessors.Select(id => available.FirstOrDefault(item => item.Key.OperationId == id)).Where(item => item != null).ToArray();
    }

    public void ClearVisualResult(VisualSourceKey source)
    {
        if (!CanEdit || source == null) return;
        _engine.InvalidateResults([source.OperationId]);
        RefreshGlobalVars(); RefreshSelectedOutputs();
        ImageChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearVisualSlot(int slot)
    {
        if (!CanEdit) return;
        _engine.ClearVisualSlot(slot);
        RefreshGlobalVars(); RefreshSelectedOutputs();
        ImageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string PrimaryImageSignature(Operation operation)
    {
        var root = operation?.ParametersRaw as YamlMappingNode;
        return GetChild(GetChild(root, "image_inputs") as YamlMappingNode, "IM In") + "\u001f" + GetChild(GetChild(root, "value") as YamlMappingNode, "IM In") + "\u001f" + GetChild(GetChild(root, "bind") as YamlMappingNode, "IM In");
    }

    private static string VisualInputSignature(Operation operation)
    {
        if (operation?.ParametersRaw is not YamlMappingNode root) return null;
        var values = GetChild(root, "value") as YamlMappingNode;
        var bindings = GetChild(root, "bind") as YamlMappingNode;
        var keys = new List<string> { "IM In", "IM Out", "Mask Im In" };
        if (operation.Driver == ChannelSplitChannels.Driver) keys.AddRange(new[] { "IM Out R", "IM Out G", "IM Out B", "IM Out H", "IM Out S", "IM Out V" });
        if (operation.Driver is "LoadImage" or "LoadPointCloud")
        {
            keys.Add("Type");
            keys.Add(string.Equals(GetChild(values, "Type")?.ToString(), "Directory", StringComparison.OrdinalIgnoreCase) ? "Directory" : "File");
        }
        return PrimaryImageSignature(operation) + "\u001d" + GetChild(root, "image_inputs") + "\u001e" + string.Join("\u001f", keys.Where(key => GetChild(values, key) != null || GetChild(bindings, key) != null)
            .Select(key => key + ":" + GetChild(values, key) + ":" + GetChild(bindings, key)));
    }

    private void InvalidateChangedVisualInput(Operation operation, string previous)
    {
        if (previous != VisualInputSignature(operation))
        {
            if (AiVisionStudio.Algorithm.Impl.ContourTemplateEngine.IsTemplateTool(operation.Driver) && previous?.Split('\u001d')[0] != PrimaryImageSignature(operation)) ContourInputLifetime.Clear(operation);
            ClearContourTemplatesUsing(operation);
            if (SelectedOperation == operation) LoadParams();
            RefreshGlobalVars(); RefreshSelectedOutputs();
        }
    }
}
