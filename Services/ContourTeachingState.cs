using AiVisionStudio.Algorithm.Impl;
using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Services;

public partial class MainViewModel
{
    private YamlMappingNode SelectedParameterValues()
    {
        var values = new YamlMappingNode(Params.Select(item => new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode(item.Name), ProjectFile.Clone(ReadItem(item)))));
        var stored = GetChild(SelectedOperation?.ParametersRaw as YamlMappingNode, "value") as YamlMappingNode;
        foreach (var key in new[] { ContourTemplateStorage.ModelKey, ContourTemplateStorage.DirtyKey })
            if (GetChild(stored, key) is { } value) values.Children[new YamlScalarNode(key)] = ProjectFile.Clone(value);
        return values;
    }

    private void InvalidateContourTeaching(YamlMappingNode values, string parameter)
    {
        if (!ContourTemplateEngine.IsTemplateTool(SelectedOperation?.Driver)) return;
        var region = ContourTemplateEngine.IsMatcher(SelectedOperation.Driver) ? "Training Region" : "Region";
        if (parameter == region || parameter is "Use Shielding Area" or "Shielding Area" or "Shielding Strokes" or "Parameters" or "Create Model" or "Model Origin")
            values.Children[new YamlScalarNode(ContourTemplateStorage.DirtyKey)] = new YamlScalarNode("true");
    }

    public bool ResetContourTeaching()
    {
        if (!ContourTemplateEngine.IsTemplateTool(SelectedOperation?.Driver)) return false;
        var emptyRegion = new Dictionary<string, object>
        {
            ["Type"] = "rect2d", ["rect2d"] = new { Center = new { X = 0d, Y = 0d }, Size = new { Width = 0d, Height = 0d }, Rotation = 0d }
        };
        var regionKey = ContourTemplateEngine.IsMatcher(SelectedOperation.Driver) ? "Training Region" : "Region";
        var changes = new Dictionary<string, object>
        {
            [regionKey] = emptyRegion, ["Use Shielding Area"] = false,
            ["Shielding Area"] = new Dictionary<string, object> { ["Type"] = "polygon2d", ["polygon2d"] = new { Points = Array.Empty<object>() } },
            ["Shielding Strokes"] = Array.Empty<object>(), ["Model Origin"] = new { Auto = true, X = 0d, Y = 0d, Angle = 0d, Locked = false },
            [ContourTemplateStorage.ModelKey] = "", [ContourTemplateStorage.DirtyKey] = true, ["Model File"] = ""
        };
        if (ContourTemplateEngine.IsMatcher(SelectedOperation.Driver)) { changes["Use Region"] = false; changes["Region"] = emptyRegion; }
        return WriteContourState(changes, clearBindings: true);
    }

    public bool StoreContourTemplate(ContourTemplateModel model, IReadOnlyDictionary<string, object> teaching = null)
    {
        using var input = CopySelectedInputImage();
        if (input != null && !input.IsDisposed && !input.Empty()) model.InputFingerprint = ContourTemplateEngine.ImageFingerprint(input);
        var changes = new Dictionary<string, object>
        {
            [ContourTemplateStorage.ModelKey] = ContourTemplateEngine.SerializeModel(model),
            [ContourTemplateStorage.DirtyKey] = false, ["Model File"] = ""
        };
        if (teaching != null)
        {
            var region = ContourTemplateEngine.IsMatcher(SelectedOperation?.Driver) ? "Training Region" : "Region";
            foreach (var name in new[] { region, "Use Shielding Area", "Shielding Area", "Shielding Strokes", "Parameters", "Create Model", "Model Origin" })
                if (teaching.TryGetValue(name, out var value)) changes[name] = value;
            if (ContourTemplateEngine.IsMatcher(SelectedOperation?.Driver))
            {
                changes["Match Display"] = "Rectangle";
                changes["Display Center Point"] = true;
                foreach (var name in new[] { "Display Rect Region Attribute", "Display Center Point Attribute" })
                {
                    var style = ReadParameterValue(name) is IReadOnlyDictionary<string, object> currentStyle
                        ? new Dictionary<string, object>(currentStyle) : new Dictionary<string, object>();
                    style["OK Color"] = "#00FF00";
                    changes[name] = style;
                }
            }
        }
        return WriteContourState(changes, clearBindings: teaching != null);
    }

    private bool WriteContourState(Dictionary<string, object> changes, bool clearBindings = false)
    {
        if (!CanEdit || SelectedOperation == null || !ApplyParameters()) return false;
        var root = ProjectFile.Clone(SelectedOperation.ParametersRaw as YamlNode) as YamlMappingNode ?? new();
        var values = SelectedParameterValues();
        foreach (var pair in changes) values.Children[new YamlScalarNode(pair.Key)] = JsonToYaml(System.Text.Json.JsonSerializer.SerializeToElement(pair.Value));
        root.Children[new YamlScalarNode("value")] = values;
        if (clearBindings && GetChild(root, "bind") is YamlMappingNode bindings)
            foreach (var key in changes.Keys) bindings.Children.Remove(new YamlScalarNode(key));
        BeforeEdit(); SelectedOperation.ParametersRaw = root; AfterEdit(); LoadParams();
        _engine.Context.OpOutputs.Remove(SelectedOperation.Uuid);
        _engine.Context.NativeOverlays.RemoveAll(overlay => Equals(overlay.Values.GetValueOrDefault("_owned_operation"), SelectedOperation.Uuid));
        SelectedOperation.State = Models.OpState.None; SelectedOperation.ErrorMessage = null; SelectedOperation.ElapsedMs = 0;
        RefreshSelectedOutputs();
        Status = "模板示教状态已更新";
        return true;
    }
}
