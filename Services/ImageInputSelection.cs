using AiVisionStudio.Algorithm;
using AiVisionStudio.Models;
using OpenCvSharp;
using YamlDotNet.RepresentationModel;
using static AiVisionStudio.Models.VisualSnapshot;

namespace AiVisionStudio.Services;

public partial class MainViewModel
{
    private static string ImageInputPath(ParameterNode node) => string.Join(".", node.Path);
    private static ImageInputReference ReadImageInput(Operation operation, string path)
    {
        var inputs = GetChild(operation?.ParametersRaw as YamlMappingNode, "image_inputs") as YamlMappingNode;
        if (GetChild(inputs, path) is not YamlMappingNode reference) return null;
        return new(GetChild(reference, "operation")?.ToString(), GetChild(reference, "output")?.ToString() ?? "", GetChild(reference, "parameter")?.ToString() ?? "");
    }

    private VisualSnapshot FindImageInput(ImageInputReference reference)
    {
        if (reference == null || string.IsNullOrEmpty(reference.OperationId)) return null;
        var images = _visualSnapshots.Values.Where(snapshot => snapshot.Key.OperationId == reference.OperationId && snapshot.Key.Kind == VisualResultKind.Image);
        if (!string.IsNullOrEmpty(reference.OutputParameter)) return images.FirstOrDefault(snapshot => snapshot.OutputParameter == reference.OutputParameter);
        return images.FirstOrDefault(snapshot => snapshot.Key == new VisualSourceKey(snapshot.Key.ProgramName, reference.OperationId, reference.OutputKey, VisualResultKind.Image));
    }

    private (Mat Image, int Slot)? ResolveImageInput(ImageInputReference reference)
    {
        lock (_visualSnapshotGate)
        {
            var snapshot = FindImageInput(reference);
            return snapshot?.Image is { IsDisposed: false } image && !image.Empty() ? (image.Clone(), snapshot.AlgorithmSlot) : null;
        }
    }

    public IReadOnlyList<ImageInputSource> GetImageInputChoices(Operation target)
    {
        var program = Programs.FirstOrDefault(program => program.Operations.Contains(target));
        if (target == null || program == null) return [];
        var snapshots = GetVisualSources();
        var result = new List<ImageInputSource>();
        foreach (var operation in program.Operations.TakeWhile(operation => operation != target))
        {
            var definition = DriverCatalog.Get(operation.Driver);
            var saved = operation.ParametersRaw as YamlMappingNode;
            var stored = GetChild(saved, "value") as YamlMappingNode;
            var values = stored == null ? new YamlMappingNode() : new YamlMappingNode(stored.Children);
            var root = new YamlMappingNode();
            if (GetChild(saved, "bind") is { } bind) root.Add("bind", bind);
            foreach (var parameter in definition?.Params ?? [])
                if (GetChild(values, parameter.Name) == null) values.Add(parameter.Name, DefaultNode(parameter));
            root.Children[new YamlScalarNode("value")] = values;
            var parameters = ParamBag.FromYaml(root);
            var actual = snapshots.Where(snapshot => snapshot.Key.ProgramName == program.Name && snapshot.Key.OperationId == operation.Uuid && snapshot.Key.Kind == VisualResultKind.Image).ToArray();
            if (definition?.Is3D == true && actual.Length == 0) continue;
            var routes = definition?.Params.Where(parameter => parameter.Type == "im" && parameter.Name.Contains("Out", StringComparison.OrdinalIgnoreCase)
                && MatchDependencies(values, parameter.Depends, new ParameterNode { Path = [parameter.Name] })).ToArray() ?? [];
            if (operation.Driver == ChannelSplitChannels.Driver)
            {
                foreach (var channel in ChannelSplitChannels.Names(parameters.Str("Split Mode", "RGB")))
                {
                    string route = "IM Out " + channel;
                    var existing = actual.FirstOrDefault(snapshot => snapshot.OutputParameter == route);
                    result.Add(new(new(operation.Uuid, ChannelSplitChannels.Key(channel), route), operation.Name,
                        VisualResultNames.FromParameter(route), existing?.AlgorithmSlot ?? parameters.Int(route), existing != null));
                }
                continue;
            }
            foreach (var source in actual)
            {
                if (source.OutputParameter.Length > 0 && routes.Length > 0 && !routes.Any(route => route.Name == source.OutputParameter) && !(operation.Driver == "Basic Create ROI" && source.OutputParameter == "IM In")) continue;
                result.Add(new(new(operation.Uuid, source.Key.OutputKey, source.OutputParameter), operation.Name,
                    source.OutputName.Length > 0 ? source.OutputName : "图像", source.AlgorithmSlot, true));
            }
            if (definition?.Is3D == true) continue;
            foreach (var route in routes)
            {
                if (result.Any(source => source.Reference.OperationId == operation.Uuid && source.Reference.OutputParameter == route.Name)) continue;
                int slot = parameters.Int(route.Name);
                string key = routes.Length > 1 ? $"output:Image:{route.Name}" : $"IM{slot}";
                string label = VisualResultNames.FromParameter(route.Name, operation.Driver);
                result.Add(new(new(operation.Uuid, key, route.Name), operation.Name, label, slot, false));
            }
            if (actual.Length == 0 && routes.Length == 0 && definition?.Is2D == true && definition.Params.Any(parameter => parameter.Type == "im" && parameter.Name == "IM In"))
                result.Add(new(new(operation.Uuid, "IM" + parameters.Int("IM In")), operation.Name, "图像", parameters.Int("IM In"), false));
        }
        return result;
    }

    internal static bool IsImageInputActive(Operation operation, string path)
    {
        var definitions = DriverCatalog.Get(operation.Driver)?.Params;
        var spec = definitions?.FirstOrDefault(spec => spec.Name == path);
        if (spec?.Depends == null) return true;
        var saved = GetChild(operation.ParametersRaw as YamlMappingNode, "value") as YamlMappingNode;
        var values = saved == null ? new YamlMappingNode() : new YamlMappingNode(saved.Children);
        foreach (var parameter in definitions)
            if (GetChild(values, parameter.Name) == null) values.Add(parameter.Name, DefaultNode(parameter));
        return MatchDependencies(values, spec.Depends, new ParameterNode { Path = [path] });
    }

    private string DescribeImageInput(ParameterNode node, IReadOnlyList<ImageInputSource> choices = null)
    {
        var reference = ReadImageInput(SelectedOperation, ImageInputPath(node));
        node.ImageInputReference = reference;
        choices ??= GetImageInputChoices(SelectedOperation);
        if (reference != null)
        {
            if (string.IsNullOrEmpty(reference.OperationId)) return "请选择图像来源";
            var source = choices.FirstOrDefault(source => SameImageInput(source.Reference, reference));
            return source != null ? source.Label + (source.Available ? "" : "（未运行）") : "图像来源已失效，请重新选择";
        }
        if (int.TryParse(node.Value, out int slot))
        {
            var legacy = choices.LastOrDefault(source => source.Slot == slot);
            if (legacy != null) return legacy.Label;
        }
        return node.IsBound ? node.BindingLabel : "请选择图像来源";
    }

    public static bool SameImageInput(ImageInputReference left, ImageInputReference right)
        => left != null && right != null && left.OperationId == right.OperationId
           && (left.OutputParameter.Length > 0 || right.OutputParameter.Length > 0 ? left.OutputParameter == right.OutputParameter : left.OutputKey == right.OutputKey);

    public bool IsSelectedImageInput(ParameterNode node, ImageInputSource source)
        => node != null && SameImageInput(ReadImageInput(SelectedOperation, ImageInputPath(node)), source.Reference);

    public void SetImageInput(ParameterNode node, ImageInputSource source)
    {
        if (!CanEdit || node?.IsImageInput != true || SelectedOperation?.Uuid != node.OperationId) return;
        if (HasParameterErrors) { Status = "请先修正无效参数"; return; }
        if (source != null && !GetImageInputChoices(SelectedOperation).Any(candidate => SameImageInput(candidate.Reference, source.Reference)))
        { Status = "所选图像来源已失效，请重新选择"; return; }
        var previous = VisualInputSignature(SelectedOperation);
        BeforeEdit();
        var root = ProjectFile.Clone(SelectedOperation.ParametersRaw as YamlNode) as YamlMappingNode ?? new();
        var inputs = GetChild(root, "image_inputs") as YamlMappingNode ?? new();
        inputs.Children[new YamlScalarNode(ImageInputPath(node))] = new YamlMappingNode
        {
            { "operation", source?.Reference.OperationId ?? "" }, { "output", source?.Reference.OutputKey ?? "" },
            { "parameter", source?.Reference.OutputParameter ?? "" }
        };
        root.Children[new YamlScalarNode("image_inputs")] = inputs;
        if (source != null)
        {
            var values = GetChild(root, "value") as YamlMappingNode ?? new();
            SetAtPath(values, node.Path, new YamlScalarNode(source.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            root.Children[new YamlScalarNode("value")] = values;
        }
        var bindings = GetChild(root, "bind") as YamlMappingNode;
        if (AtPath(bindings, BindingPath(node)) is YamlMappingNode binding)
        { binding.Children[new YamlScalarNode("_binded")] = new YamlScalarNode("false"); binding.Children[new YamlScalarNode("_global")] = new YamlScalarNode("false"); }
        SelectedOperation.ParametersRaw = root;
        LoadParams();
        InvalidateChangedVisualInput(SelectedOperation, previous);
        AfterEdit();
    }

    private void RefreshImageInputLabels()
    {
        var nodes = AllParameterNodes.Where(node => node.IsImageInput).ToArray();
        if (nodes.Length == 0) return;
        var choices = GetImageInputChoices(SelectedOperation);
        foreach (var node in nodes) node.ImageInputLabel = DescribeImageInput(node, choices);
    }

    public string SelectedImageInputIdentity => SelectedOperation?.Uuid + "\u001e" + PrimaryImageSignature(SelectedOperation);

    private IReadOnlyList<NativeOverlay3D> ResolveImageInputOverlays(ImageInputReference reference)
    {
        lock (_visualSnapshotGate) return FindImageInput(reference)?.Overlays.ToArray() ?? [];
    }

    public bool HasSelectedImageInput => ReadImageInput(SelectedOperation, "IM In") != null;

    public Mat CopySelectedInputImage()
    {
        var reference = ReadImageInput(SelectedOperation, "IM In");
        if (reference != null) return ResolveImageInput(reference)?.Image;
        var input = ParamBag.FromYaml(SelectedOperation?.ParametersRaw as YamlNode, null, _engine.Context).Int("IM In");
        var image = Images.Get(input);
        return image is { IsDisposed: false } && !image.Empty() ? image.Clone() : null;
    }

    public Mat CopySelectedPreviewImage(int index, bool inputOnly = false)
    {
        if (!inputOnly)
            lock (_visualSnapshotGate)
            {
                var snapshot = _visualSnapshots.Values.FirstOrDefault(snapshot => snapshot.Key.OperationId == SelectedOperation?.Uuid
                    && snapshot.Key.Kind == VisualResultKind.Image && snapshot.AlgorithmSlot == index);
                if (snapshot?.Image is { IsDisposed: false } image && !image.Empty()) return image.Clone();
            }
        if (HasSelectedImageInput) return CopySelectedInputImage();
        var fallback = Images.Get(index);
        return fallback is { IsDisposed: false } && !fallback.Empty() ? fallback.Clone() : null;
    }
}
