using AiVisionStudio.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using YamlDotNet.RepresentationModel;
using static AiVisionStudio.Models.VisualSnapshot;


namespace AiVisionStudio.Services;

public partial class MainViewModel
{
    private readonly object _visualSnapshotGate = new();

    private readonly Dictionary<VisualSourceKey, VisualSnapshot>
        _visualSnapshots = new();

    private bool _visualSnapshotsDisposed;
    private long _nextVisualVersion;

    public event Action<string, string> VisualProgramRenamed;

    private void InitializeVisualSnapshots()
    {
        _engine.ContourTemplateInvalidated += operation =>
        {
            var original = Programs.SelectMany(program => program.Operations).FirstOrDefault(item => item.Uuid == operation.Uuid);
            if (original != null) original.ParametersRaw = operation.ParametersRaw;
            _contourTeachingChanged = true;
        };
        _engine.ImageInputResolver = ResolveImageInput;
        _engine.ImageInputOverlayResolver = ResolveImageInputOverlays;
        _engine.VisualSnapshotReady += SaveVisualSnapshot;
        _engine.VisualSnapshotsPublished += PruneReplacedVisualOutputs;
        _engine.ResultsInvalidated += OnResultsInvalidated;
    }

    private void SaveVisualSnapshot(VisualSnapshot snapshot)
    {
        lock (_visualSnapshotGate)
        {
            if (_visualSnapshotsDisposed)
            {
                snapshot.Dispose();
                return;
            }
            snapshot.Version = ++_nextVisualVersion;
            _visualFallbacks.Remove(snapshot.Key.OperationId);
            if (_visualSnapshots.Remove(snapshot.Key, out var old))
                old.Dispose();

            _visualSnapshots.Add(snapshot.Key, snapshot);
        }
    }

    private void PruneReplacedVisualOutputs(string operationId, IReadOnlySet<VisualSourceKey> current)
    {
        lock (_visualSnapshotGate)
            foreach (var key in _visualSnapshots.Keys.Where(key => key.OperationId == operationId && !current.Contains(key)).ToArray())
                if (_visualSnapshots.Remove(key, out var previous)) previous.Dispose();
    }

    public IReadOnlyList<VisualSourceInfo> GetVisualSources()
    {
        lock (_visualSnapshotGate)
        {
            return _visualSnapshots.Values.Select(snapshot => new VisualSourceInfo(snapshot.Key, snapshot.OperationName, snapshot.AlgorithmSlot)
            {
                Version = snapshot.Version,
                OutputName = snapshot.OutputName,
                OutputParameter = snapshot.OutputParameter,
                CapturedAt = snapshot.CapturedAt,
                ParameterSignature = snapshot.ParameterSignature
            })
                .ToArray();
        }
    }

    public bool UseVisualSnapshot(
        VisualSourceKey key,
        Action<VisualSnapshot> display)
    {
        lock (_visualSnapshotGate)
        {
            if (!_visualSnapshots.TryGetValue(key, out var snapshot))
                return false;

            // 显示控件会复制图像/点云；
            // 在复制完成前，避免旧快照被替换并释放。
            display(snapshot);
            return true;
        }
    }

    private void ClearVisualSnapshots()
    {
        _imageFileLists.Clear();
        lock (_visualSnapshotGate)
        {
            foreach (var snapshot in _visualSnapshots.Values)
                snapshot.Dispose();

            _visualSnapshots.Clear();
            _visualFallbacks.Clear();
        }
    }
    // 当前流程发生编辑时，只让当前流程的结果失效。
    // 同时释放已经删除的流程或算子的快照。
    // 参数变化时保留上次运行结果。
    // 只有流程或算子已经被删除时，才释放对应快照。
    private void PruneRemovedVisualSnapshots()
    {
        var alive = Programs.SelectMany(program => program.Operations.Select(operation => (ProgramName: program.Name, OperationId: operation.Uuid))).ToHashSet();
        _engine.PruneResults(alive.Select(item => item.OperationId).ToHashSet());
        lock (_visualSnapshotGate)
        {
            var removedKeys = _visualSnapshots.Keys.Where(key => !alive.Contains((key.ProgramName, key.OperationId))).ToArray();
            foreach (var key in removedKeys)
            {
                if (_visualSnapshots.Remove(key, out var snapshot))
                    snapshot.Dispose();
            }
        }
    }

    private void DisposeVisualSnapshots()
    {
        lock (_visualSnapshotGate)
        {
            _visualSnapshotsDisposed = true;

            foreach (var snapshot in _visualSnapshots.Values)
                snapshot.Dispose();

            _visualSnapshots.Clear();
        }
    }
    private void RenameVisualProgram(string oldName, string newName)
    {
        lock (_visualSnapshotGate)
        {
            var entries = _visualSnapshots.Where(pair => pair.Key.ProgramName == oldName).ToArray();
            foreach (var pair in entries)
            {
                _visualSnapshots.Remove(pair.Key);

                var snapshot = pair.Value;

                snapshot.Key = pair.Key with
                {
                    ProgramName = newName
                };

                _visualSnapshots.Add(snapshot.Key, snapshot);
            }
        }

        // 修改各流程独立保存的窗口绑定
        foreach (var program in Programs)
        {
            RewriteVisualProgramName(ReadVisualLayoutNode(program.Definition, "visualLayout"), oldName, newName);
            // 兼容工程根节点中的旧布局
            RewriteVisualProgramName( ReadVisualLayoutNode(program.Root, "layout"),oldName,newName);
        }

        VisualProgramRenamed?.Invoke(oldName, newName);
    }

    private static void RewriteVisualProgramName(YamlNode node, string oldName, string newName)
    {
        if (node is YamlMappingNode mapping)
        {
            var key = new YamlScalarNode("sourceProgram");

            if (mapping.Children.TryGetValue(key, out var value) && value.ToString() == oldName)
            {
                mapping.Children[key] = new YamlScalarNode(newName);
            }

            foreach (var child in mapping.Children.Values.ToArray()) RewriteVisualProgramName(child, oldName, newName);
        }
        else if (node is YamlSequenceNode sequence)
        {
            foreach (var child in sequence.Children)
                RewriteVisualProgramName(child, oldName, newName);
        }
    }
    // 只更新显示布局，不使正在运行的算法失效。
    // 获取当前流程自己的布局
    public YamlSequenceNode GetCurrentVisualLayout()
    {
        var program = SelectedProgram;

        if (program == null)
            return CreateBlankVisualLayout();

        // 优先读取当前流程独立保存的布局
        if (ReadVisualLayoutNode(program.Definition, "visualLayout") is YamlSequenceNode ownLayout)
        {
            return ownLayout;
        }

        // 兼容之前保存到工程根节点的旧布局
        if (ReadVisualLayoutNode(program.Root, "layout") is YamlSequenceNode legacyLayout)
        {
            var entries = legacyLayout.Children.OfType<YamlMappingNode>().ToArray();

            // 只迁移明确属于当前流程的窗口
            var ownedEntries = entries.Where(entry => ReadVisualLayoutNode(entry, "sourceProgram")?.ToString() == program.Name).Select(entry => ProjectFile.Clone(entry)).ToArray();
            if (ownedEntries.Length > 0)
                return new YamlSequenceNode(ownedEntries);

            bool hasSourceTags = entries.Any(entry => !string.IsNullOrEmpty(ReadVisualLayoutNode(entry, "sourceProgram")?.ToString()));
            if (!hasSourceTags && program.IsDefault)
            {
                return (YamlSequenceNode)ProjectFile.Clone(legacyLayout);
            }
        }

        // 新流程：一个完全空白的窗口
        return CreateBlankVisualLayout();
    }

    // 保存到当前流程，不再写入工程共享的 Root.layout
    public void UpdateVisualLayout(YamlSequenceNode layout)
    {
        var program = SelectedProgram;

        if (program == null)
            return;

        program.Definition ??= new YamlMappingNode();

        program.Definition.Children[new YamlScalarNode("visualLayout")] = ProjectFile.Clone(layout);
        IsDirty = true;
    }

    // 保留旧方法名称，防止其他调用处仍在使用
    public void UpdateImageLayout(YamlSequenceNode layout)
    {
        UpdateVisualLayout(layout);
    }

    private static YamlSequenceNode CreateBlankVisualLayout()
    {
        return new YamlSequenceNode(new YamlMappingNode
            {
            { "paneId", "0" },
            { "IM", "0" },
            { "left", "0" },
            { "top", "0" },
            { "width", "100" },
            { "height", "100" },
            { "zIndex", "0" },
            { "hidden", "false" }
            });
    }

    private static YamlNode ReadVisualLayoutNode(YamlMappingNode mapping, string key)
    {
        if (mapping == null)
            return null;

        return mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    }
}
