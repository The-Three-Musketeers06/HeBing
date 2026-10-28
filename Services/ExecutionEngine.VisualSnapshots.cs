using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Models;

namespace AiVisionStudio.Services;

public partial class ExecutionEngine
{
    public event Action<VisualSnapshot> VisualSnapshotReady;
    public event Action<string, IReadOnlySet<VisualSourceKey>> VisualSnapshotsPublished;

    // 只在单个控件执行期间记录它实际写入的结果。
    private sealed class VisualCapture : IDisposable
    {
        private readonly OpContext _context;
        private readonly ExecutionEngine _engine;
        private readonly int _threadId = Environment.CurrentManagedThreadId;
        private bool _trackingReads = true;
        public HashSet<(VisualResultKind Kind, int Slot)> Written { get; } = new();
        public HashSet<string> Dependencies { get; } = new(StringComparer.Ordinal);
        public HashSet<object> GraphicsBefore => _overlaysBefore;
        public int TextCountBefore => _drawTextsBefore.Length;

        private readonly Dictionary<int, ProfileData> _profilesBefore;

        private readonly HashSet<object> _overlaysBefore;

        private readonly string[] _drawTextsBefore;

        public HashSet<(VisualResultKind Kind, int Slot)> Changed
        { get; } = new();

        public VisualCapture(ExecutionEngine engine, ParamBag parameters)
        {
            _engine = engine;
            var context = _context = engine.Context;

            _profilesBefore = context.Profiles
                .ToDictionary(pair => pair.Key, pair => pair.Value);

            _overlaysBefore = new HashSet<object>(
                ReferenceEqualityComparer.Instance);

            foreach (var item in context.Texts)
                _overlaysBefore.Add(item);

            foreach (var item in context.Boxes)
                _overlaysBefore.Add(item);

            foreach (var item in context.NativeOverlays)
                _overlaysBefore.Add(item);

            _drawTextsBefore = context.DrawTexts.ToArray();
            foreach (var binding in parameters.Bindings.Values) OnOutputRead(binding.Uuid);
            if (parameters.Has("IM In")) OnRead((VisualResultKind.Profile, parameters.Int("IM In")));

            context.Images.VisualWritten += OnImageWritten;
            context.Clouds.VisualWritten += OnCloudWritten;
            context.Images.VisualRead += OnImageRead;
            context.Clouds.VisualRead += OnCloudRead;
            context.OutputRead += OnOutputRead;
        }

        private void OnOutputRead(string operationId)
        {
            if (_trackingReads && Environment.CurrentManagedThreadId == _threadId && !string.IsNullOrEmpty(operationId) && operationId != _context.OperationUuid) Dependencies.Add(operationId);
        }
        private void OnRead((VisualResultKind Kind, int Slot) key)
        {
            if (!_trackingReads || Environment.CurrentManagedThreadId != _threadId || Written.Contains(key)) return;
            if (key.Kind == VisualResultKind.Image && _context.Images.InputOwner(key.Slot) is { } inputOwner)
            { OnOutputRead(inputOwner); return; }
            if (_engine._runtimeVisuals.TryGetValue(key, out var owned) && ReferenceEquals(_engine.RuntimeValue(key), owned.Value)) OnOutputRead(owned.OperationId);
        }
        private void OnImageRead(int slot) => OnRead((VisualResultKind.Image, slot));
        private void OnCloudRead(int slot) => OnRead((VisualResultKind.Cloud, slot));

        private void OnImageWritten(int slot)
        {
            Changed.Add((VisualResultKind.Image, slot));
            Written.Add((VisualResultKind.Image, slot));
        }

        private void OnCloudWritten(int slot)
        {
            Changed.Add((VisualResultKind.Cloud, slot));
            Written.Add((VisualResultKind.Cloud, slot));
        }

        public void Complete(ParamBag parameters)
        {
            _trackingReads = false;
            // 轮廓当前通过字典写入，因此比较前后的对象。
            foreach (var pair in _context.Profiles)
            {
                if (!_profilesBefore.TryGetValue(pair.Key, out var old)
                    || !ReferenceEquals(old, pair.Value))
                {
                    Changed.Add((VisualResultKind.Profile, pair.Key));
                    Written.Add((VisualResultKind.Profile, pair.Key));
                }
            }

            int fallbackSlot = parameters.Int("IM In", 0);

            foreach (var item in _context.Texts)
            {
                if (!_overlaysBefore.Contains(item))
                    MarkOverlaySlot(item.ViewIndex, fallbackSlot);
            }

            foreach (var item in _context.Boxes)
            {
                if (!_overlaysBefore.Contains(item))
                    MarkOverlaySlot(item.ViewIndex, fallbackSlot);
            }

            foreach (var item in _context.NativeOverlays)
            {
                if (!_overlaysBefore.Contains(item))
                    MarkOverlaySlot(item.ViewIndex, fallbackSlot);
            }

            if (!_drawTextsBefore.SequenceEqual(_context.DrawTexts))
                MarkOverlaySlot(0, fallbackSlot);
        }

        private void MarkOverlaySlot(int slot, int fallbackSlot)
        {
            if (slot < 0)
                slot = fallbackSlot;

            if (_context.Profiles.TryGetValue(slot, out var profile)
                && profile.Count > 0)
            {
                Changed.Add((VisualResultKind.Profile, slot));
            }
            else if (_context.Clouds.Get(slot) is { Count: > 0 })
            {
                Changed.Add((VisualResultKind.Cloud, slot));
            }
            else if (_context.Images.Get(slot) is { } image
                     && !image.IsDisposed
                     && !image.Empty())
            {
                Changed.Add((VisualResultKind.Image, slot));
            }
        }

        public void Dispose()
        {
            _context.Images.VisualWritten -= OnImageWritten;
            _context.Clouds.VisualWritten -= OnCloudWritten;
            _context.Images.VisualRead -= OnImageRead;
            _context.Clouds.VisualRead -= OnCloudRead;
            _context.OutputRead -= OnOutputRead;
        }
    }

    private void PublishVisualSnapshots(
        Operation operation,
        ParamBag parameters,
        VisualCapture capture)
    {
        capture.Complete(parameters);
        RecordRuntimeResults(operation, capture);

        string programName = _programs
            .FirstOrDefault(pair =>
                pair.Value.Any(op => op.Uuid == operation.Uuid))
            .Key ?? "";

        // 有 IM Out 时优先显示主输出。
        // 只有输入图标注结果时，优先使用 IM In。
        bool roiPreview = operation.Driver == "Basic Create ROI";
        if (roiPreview || operation.Driver is "Basic Line Find" or "Basic Circle Find" or "Basic Edge Point Find")
            capture.Changed.Add((VisualResultKind.Image, parameters.Int("IM In", 0)));
        int preferredSlot = roiPreview ? parameters.Int("IM In", 0) : parameters.Has("IM Out")
            ? parameters.Int("IM Out")
            : parameters.Int("IM In", 0);

        var channels = operation.Driver == ChannelSplitChannels.Driver && Context.Vars.Get("Split Mode") is string splitMode
            ? ChannelSplitChannels.Names(splitMode).Select((name, index) => (Name: name, Slot: parameters.Int("IM Out " + name, index + 1))).ToArray()
            : [];
        bool HasVisual((VisualResultKind Kind, int Slot) item) => item.Kind switch
        {
            VisualResultKind.Image => Context.Images.Get(item.Slot) is { IsDisposed: false } image && !image.Empty(),
            VisualResultKind.Cloud => Context.Clouds.Get(item.Slot)?.Count > 0,
            VisualResultKind.Profile => Context.Profiles.TryGetValue(item.Slot, out var profile) && profile.Count > 0,
            _ => false
        };
        var written = capture.Written.Where(HasVisual).ToArray();
        bool multipleOutputs = roiPreview ? capture.Changed.Where(HasVisual).Count() > 1 : written.Length > 1;
        IEnumerable<(VisualResultKind Kind, int Slot)> candidates = multipleOutputs && !roiPreview ? written : capture.Changed;
        var published = new HashSet<VisualSourceKey>();
        foreach (var changed in candidates.OrderBy(item => item.Slot == preferredSlot ? 0 : 1).ThenBy(item => item.Slot).ThenBy(item => item.Kind))
        {
            int slot = changed.Slot;
            string channel = channels.FirstOrDefault(item => item.Slot == slot).Name;
            if (channels.Length > 0 && (changed.Kind != VisualResultKind.Image || channel == null)) continue;

            var image = changed.Kind == VisualResultKind.Image
                ? Context.Images.Get(slot)
                : null;

            var cloud = changed.Kind == VisualResultKind.Cloud
                ? Context.Clouds.Get(slot)
                : null;

            ProfileData profile = null;

            if (changed.Kind == VisualResultKind.Profile)
                Context.Profiles.TryGetValue(slot, out profile);

            bool valid = changed.Kind switch
            {
                VisualResultKind.Image =>
                    image != null
                    && !image.IsDisposed
                    && !image.Empty(),

                VisualResultKind.Cloud => cloud?.Count > 0,

                VisualResultKind.Profile => profile?.Count > 0,

                _ => false
            };

            if (!valid)
                continue;

            string route = DriverCatalog.Get(operation.Driver)?.Params.FirstOrDefault(spec => spec.Type == "im"
                && spec.Name.Contains("Out", StringComparison.OrdinalIgnoreCase) && parameters.Has(spec.Name) && parameters.Int(spec.Name) == slot)?.Name;
            if (roiPreview) route = slot == parameters.Int("IM Out", 1) ? "IM Out" : "IM In";
            string outputName = roiPreview ? route == "IM Out" ? "掩膜" : "图像" : VisualResultNames.Default(changed.Kind);
            if (!roiPreview && channel != null) outputName = ChannelSplitChannels.Caption(ChannelSplitChannels.Key(channel));
            else if (!roiPreview && route != null) outputName = VisualResultNames.FromParameter(route, operation.Driver);
            else if (!roiPreview && multipleOutputs) outputName += (published.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            IEnumerable<NativeOverlay3D> overlays = Context.NativeOverlays.Where(item => item.ViewIndex < 0 || item.ViewIndex == slot);
            if (AiVisionStudio.Algorithm.Impl.ContourTemplateEngine.IsTemplateTool(operation.Driver))
                overlays = overlays.Where(item => MainViewModel.IsTemplateOverlay(item, operation));
            else if (changed.Kind == VisualResultKind.Image && parameters.ImageInputs.TryGetValue("IM In", out var input))
            {
                overlays = overlays.Where(item => !capture.GraphicsBefore.Contains(item));
                if (slot == parameters.Int("IM In") && !capture.Written.Contains(changed))
                    overlays = (ImageInputOverlayResolver?.Invoke(input) ?? []).Concat(overlays);
            }
            var snapshot = new VisualSnapshot
            {
                Key = new VisualSourceKey(
                    programName,
                    operation.Uuid,
                    channel != null ? ChannelSplitChannels.Key(channel) : multipleOutputs ? $"output:{changed.Kind}:{route ?? $"IM{slot}"}" : $"IM{slot}",
                    changed.Kind),

                OperationName = operation.Name,
                OutputName = outputName,
                OutputParameter = channel != null ? "IM Out " + channel : route ?? "",
                AlgorithmSlot = slot,
                ParameterSignature =operation.ParametersRaw?.ToString() ?? "",
                Image = image?.Clone(),
                Cloud = cloud?.Clone(),
                Profile = profile?.Clone(),

                OverlayText = slot == 0
                    ? string.Join(Environment.NewLine, Context.DrawTexts)
                    : "",

                Texts = Context.Texts
                    .Where(item =>
                        item.ViewIndex < 0 || item.ViewIndex == slot)
                    .Select(CopyVisualText)
                    .ToArray(),

                Boxes = Context.Boxes
                    .Where(item =>
                        item.ViewIndex < 0 || item.ViewIndex == slot)
                    .Select(CopyVisualBox)
                    .ToArray(),

                Overlays = overlays
                    .Select(item => new NativeOverlay3D
                    {
                        Type = item.Type,
                        ViewIndex = changed.Kind == VisualResultKind.Image ? slot : item.ViewIndex,
                        Cloud = item.Cloud?.Clone(),

                        Values = item.Values.ToDictionary(
                            pair => pair.Key,
                            pair => CopyVisualValue(pair.Value),
                            StringComparer.OrdinalIgnoreCase)
                    })
                    .ToArray()
            };

            published.Add(snapshot.Key);
            var handler = VisualSnapshotReady;

            if (handler == null)
                snapshot.Dispose();
            else
                handler(snapshot);
            if (!multipleOutputs && channels.Length == 0) break;
        }
        VisualSnapshotsPublished?.Invoke(operation.Uuid, published);
    }

    private static Text3D CopyVisualText(Text3D item)
    {
        return new Text3D
        {
            Owner = item.Owner,
            X = item.X,
            Y = item.Y,
            Z = item.Z,
            Size = item.Size,
            SizeIsPixels = item.SizeIsPixels,
            Text = item.Text,
            Color = item.Color,
            ViewIndex = item.ViewIndex
        };
    }

    private static Box3 CopyVisualBox(Box3 item)
    {
        return new Box3
        {
            X1 = item.X1,
            Y1 = item.Y1,
            Z1 = item.Z1,
            X2 = item.X2,
            Y2 = item.Y2,
            Z2 = item.Z2,
            Color = item.Color,
            ViewIndex = item.ViewIndex,
            Label = item.Label,
            FillOpacity = item.FillOpacity
        };
    }

    private static object CopyVisualValue(object value)
    {
        return value switch
        {
            null => null,

            PointCloud cloud => cloud.Clone(),

            ProfileData profile => profile.Clone(),

            PointSet3D points =>
                new PointSet3D(points.Source.Clone(), points.Indices),

            IReadOnlyDictionary<string, object> map =>
                map.ToDictionary(
                    pair => pair.Key,
                    pair => CopyVisualValue(pair.Value),
                    StringComparer.Ordinal),

            IEnumerable items when value is not string =>
                items.Cast<object>()
                    .Select(CopyVisualValue)
                    .ToList(),

            _ => value
        };
    }
}
