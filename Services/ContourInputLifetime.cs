using System.Text.Json;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Algorithm.Impl;
using AiVisionStudio.Models;
using OpenCvSharp;
using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Services;

internal static class ContourInputLifetime
{
    internal static string Fingerprint(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.TryGetProperty("InputFingerprint", out var input) && !string.IsNullOrEmpty(input.GetString())) return input.GetString();
            return json.RootElement.TryGetProperty("SourceFingerprint", out var fingerprint) ? fingerprint.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    internal static void Clear(Operation operation)
    {
        var root = ProjectFile.Clone(operation.ParametersRaw as YamlNode) as YamlMappingNode ?? new();
        var key = new YamlScalarNode("value");
        var values = root.Children.TryGetValue(key, out var raw) ? raw as YamlMappingNode : null;
        values ??= new(); root.Children[key] = values;
        values.Children[new YamlScalarNode(ContourTemplateStorage.ModelKey)] = new YamlScalarNode("");
        values.Children[new YamlScalarNode(ContourTemplateStorage.DirtyKey)] = new YamlScalarNode("true");
        values.Children[new YamlScalarNode("Model File")] = new YamlScalarNode("");
        string regionKey = ContourTemplateEngine.IsMatcher(operation.Driver) ? "Training Region" : "Region";
        var region = new YamlMappingNode { { "Type", "rect2d" }, { "rect2d", new YamlMappingNode {
            { "Center", new YamlMappingNode { { "X", "0" }, { "Y", "0" } } },
            { "Size", new YamlMappingNode { { "Width", "0" }, { "Height", "0" } } }, { "Rotation", "0" } } } };
        values.Children[new YamlScalarNode(regionKey)] = region;
        values.Children[new YamlScalarNode("Use Shielding Area")] = new YamlScalarNode("false");
        values.Children[new YamlScalarNode("Shielding Strokes")] = new YamlSequenceNode();
        values.Children[new YamlScalarNode("Model Origin")] = new YamlMappingNode { { "Auto", "true" }, { "X", "0" }, { "Y", "0" }, { "Angle", "0" }, { "Locked", "false" } };
        operation.ParametersRaw = root;
    }
}

public partial class ExecutionEngine
{
    public event Action<Operation> ContourTemplateInvalidated;

    private void PrepareContourInput(Operation operation, ParamBag parameters)
    {
        if (!ContourTemplateEngine.IsTemplateTool(operation.Driver)) return;
        string fingerprint = ContourInputLifetime.Fingerprint(parameters.Str(ContourTemplateStorage.ModelKey));
        if (string.IsNullOrEmpty(fingerprint)) return;
        var image = Context.Images.Get(parameters.Int("IM In"));
        if (image == null || image.IsDisposed || image.Empty() || ContourTemplateEngine.ImageFingerprint(image) == fingerprint) return;
        ContourInputLifetime.Clear(operation);
        InvalidateResults([operation.Uuid]);
        ContourTemplateInvalidated?.Invoke(operation);
        throw new InvalidOperationException("输入图像已更换，旧模板和匹配结果已清除，请点击“创建”重新训练模板。");
    }
}

public partial class MainViewModel
{
    private readonly Dictionary<string, (long Version, string Signature, string Payload, Mat Image)> _contourInputChecks = new();

    public void ClearSelectedContourTemplate()
    {
        if (!CanEdit || !ContourTemplateEngine.IsTemplateTool(SelectedOperation?.Driver)) return;
        BeforeEdit();
        ContourInputLifetime.Clear(SelectedOperation);
        _contourInputChecks.Remove(SelectedOperation.Uuid);
        _engine.InvalidateResults([SelectedOperation.Uuid]);
        LoadParams(); AfterEdit(); RefreshSelectedOutputs();
        Status = "图像已更换，旧模板已清除，请重新训练";
    }

    public void SynchronizeSelectedContourInput()
    {
        if (!CanEdit || !ContourTemplateEngine.IsTemplateTool(SelectedOperation?.Driver)) return;
        var parameters = ParamBag.FromYaml(SelectedOperation.ParametersRaw as YamlNode);
        string payload = parameters.Str(ContourTemplateStorage.ModelKey);
        if (string.IsNullOrWhiteSpace(payload)) return;
        var image = Images.Get(parameters.Int("IM In"));
        var signature = VisualInputSignature(SelectedOperation);
        long version = LatestVisualVersion;
        if (_contourInputChecks.TryGetValue(SelectedOperation.Uuid, out var previous) && previous.Version == version
            && previous.Signature == signature && ReferenceEquals(previous.Payload, payload) && ReferenceEquals(previous.Image, image)) return;
        _contourInputChecks[SelectedOperation.Uuid] = (version, signature, payload, image);
        string fingerprint = ContourInputLifetime.Fingerprint(payload);
        if (string.IsNullOrEmpty(fingerprint)) return;
        using var input = CopySelectedInputImage();
        if (input != null && !input.IsDisposed && !input.Empty() && ContourTemplateEngine.ImageFingerprint(input) != fingerprint)
            ClearSelectedContourTemplate();
    }

    private void ClearContourTemplatesUsing(Operation source)
    {
        var program = Programs.FirstOrDefault(program => program.Operations.Contains(source));
        if (program == null) return;
        var affected = new HashSet<string> { source.Uuid };
        var slots = new HashSet<int> { ParamBag.FromYaml(source.ParametersRaw as YamlNode).Int("IM Out") };
        foreach (var operation in program.Operations.SkipWhile(operation => operation != source).Skip(1))
        {
            var parameters = ParamBag.FromYaml(operation.ParametersRaw as YamlNode);
            bool linked = parameters.ImageInputs.Values.Any(reference => affected.Contains(reference.OperationId))
                || parameters.ImageInputs.Count == 0 && parameters.Has("IM In") && slots.Contains(parameters.Int("IM In"));
            if (!linked) continue;
            affected.Add(operation.Uuid);
            foreach (var route in DriverCatalog.Get(operation.Driver)?.Params.Where(parameter => parameter.Type == "im" && parameter.Name.Contains("Out", StringComparison.OrdinalIgnoreCase)) ?? [])
                slots.Add(parameters.Int(route.Name));
            if (ContourTemplateEngine.IsTemplateTool(operation.Driver))
            { ContourInputLifetime.Clear(operation); _contourInputChecks.Remove(operation.Uuid); }
        }
        _engine.InvalidateResults(affected);
    }
}
