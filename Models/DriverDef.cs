using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AiVisionStudio.Models;

/// <summary>One declared parameter, including the original nested metadata.</summary>
public class ParamSpec
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Field { get; set; } = "basic";
    public string Type { get; set; } = "string";
    public JsonElement? Default { get; set; }
    public List<ParamSpec> Children { get; set; } = new();
    public List<string> Items { get; set; } = new();
    public JsonElement? Depends { get; set; }
    public double? Minimum { get; set; }
    public double? Maximum { get; set; }
    public string ArrayType { get; set; } = "";
    public string GroupType { get; set; } = "";
    public List<string> Support { get; set; } = new();
    /// <summary>All declared fields, including file filters and dynamic list templates.</summary>
    public JsonElement? Raw { get; set; }
    public bool IsRender => Field == "render";
    public bool IsInput => Field == "input";
    public bool IsAdvanced => Field == "advance";
    public bool IsBasic => Field is "basic" or "input";
}

public class DriverDef
{
    public string Driver { get; set; } = "";
    public int Version { get; set; } = 1;
    public string Group { get; set; } = "";
    public string Window { get; set; }
    public List<ParamSpec> Params { get; set; } = new();
    public List<ParamSpec> Outputs { get; set; } = new();
    public JsonElement? ParameterValues { get; set; }
    public JsonElement? ParameterBind { get; set; }
    public JsonElement? Raw { get; set; }
    public string ShortGroup => Group.Contains('|') ? Group.Split('|')[1] : Group;
    public bool Is2D => Group.StartsWith("2D|") || Group.StartsWith("2D Advanced|");
    public bool Is3D => Group.StartsWith("3D|");
    public bool IsSensor => Group == "Sensor";
}

/// <summary>Loads the complete declared kernel schema without dropping nested fields.</summary>
public static class DriverCatalog
{
    private static readonly Dictionary<string, DriverDef> _map = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _translations = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Dictionary<string, string>> _driverTranslations = new(StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyCollection<DriverDef> All => _map.Values;
    public static int Count => _map.Count;
    public static DriverDef Get(string driver)
        => driver != null && _map.TryGetValue(driver, out var result) ? result : null;
    public static string Translate(string name, string driver = null)
    {
        if (name == null) return "";
        if (driver != null && _driverTranslations.TryGetValue(driver, out var context)
            && context.TryGetValue(name, out var contextual)) return contextual;
        return _translations.TryGetValue(name, out var translated) ? translated : name;
    }

    public static void Load(string path)
    {
        if (!File.Exists(path)) return;
        _map.Clear();
        _translations.Clear();
        _driverTranslations.Clear();
        var directory = Path.GetDirectoryName(path) ?? "";
        LoadTranslations(Path.Combine(directory, "drivers_cn.json"));
        LoadTranslations(Path.Combine(directory, "params_cn.json"));
        var contextualPath = Path.Combine(directory, "params_driver_cn.json");
        if (File.Exists(contextualPath))
        {
            using var contexts = JsonDocument.Parse(File.ReadAllText(contextualPath));
            foreach (var context in contexts.RootElement.EnumerateObject())
            {
                if (context.Value.ValueKind != JsonValueKind.Object) continue;
                var strings = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var item in context.Value.EnumerateObject())
                    if (item.Value.ValueKind == JsonValueKind.String) strings[item.Name] = item.Value.GetString();
                _driverTranslations[context.Name] = strings;
            }
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var definition = new DriverDef
            {
                Driver = Text(entry, "driver"), Group = Text(entry, "group"),
                Version = entry.TryGetProperty("version", out var version) && version.TryGetInt32(out var number) ? number : 1,
                Window = Text(entry, "window", null), Raw = entry.Clone(),
                ParameterValues = Clone(entry, "parameter_values"),
                ParameterBind = Clone(entry, "parameter_bind")
            };
            if (entry.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
                foreach (var parameter in parameters.EnumerateArray())
                    definition.Params.Add(ReadSpec(parameter, Text(parameter, "name"), "basic", null, definition.Driver));
            if (entry.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
                foreach (var output in outputs.EnumerateArray())
                    definition.Outputs.Add(ReadSpec(output, Text(output, "name"), "output", null, definition.Driver));
            if (!string.IsNullOrEmpty(definition.Driver)) _map[definition.Driver] = definition;
        }
        AiVisionStudio.Services.ContourTemplateSchema.Apply(_map);
        AiVisionStudio.Services.MatrixCodeSchema.Apply(_map);
        AiVisionStudio.Services.ChannelSplitSchema.Apply(_map);
    }

    private static void LoadTranslations(string path)
    {
        if (!File.Exists(path)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
        foreach (var item in doc.RootElement.EnumerateObject())
            if (item.Value.ValueKind == JsonValueKind.String)
                _translations[item.Name] = item.Value.GetString();
    }

    private static ParamSpec ReadSpec(JsonElement value, string name, string field, JsonElement? parentDefault, string driver)
    {
        JsonElement? defaultValue = Clone(value, "default");
        if (defaultValue == null && parentDefault is { ValueKind: JsonValueKind.Object } parent
            && parent.TryGetProperty(name, out var childDefault)) defaultValue = childDefault.Clone();
        var spec = new ParamSpec
        {
            Name = name, DisplayName = Text(value, "display_name", Translate(name, driver)),
            Field = Text(value, "field", field), Type = Text(value, "type", "string"),
            Default = defaultValue, Depends = Clone(value, "depends"), Raw = value.Clone(),
            Minimum = Number(value, "min"), Maximum = Number(value, "max"),
            ArrayType = Text(value, "array_type"), GroupType = Text(value, "grouptype")
        };
        ReadStrings(value, "items", spec.Items);
        ReadStrings(value, "support", spec.Support);
        if (value.TryGetProperty("children", out var children))
        {
            if (children.ValueKind == JsonValueKind.Object)
            {
                foreach (var child in children.EnumerateObject())
                    if (child.Value.ValueKind == JsonValueKind.Object)
                        spec.Children.Add(ReadSpec(child.Value, child.Name, spec.Field, defaultValue, driver));
            }
            else if (children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                    if (child.ValueKind == JsonValueKind.Object)
                        spec.Children.Add(ReadSpec(child, Text(child, "name"), spec.Field, defaultValue, driver));
            }
        }
        return spec;
    }

    private static void ReadStrings(JsonElement value, string key, List<string> result)
    {
        if (!value.TryGetProperty(key, out var items) || items.ValueKind != JsonValueKind.Array) return;
        foreach (var item in items.EnumerateArray())
            result.Add(item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString());
    }

    private static JsonElement? Clone(JsonElement value, string key)
        => value.TryGetProperty(key, out var result) && result.ValueKind != JsonValueKind.Null ? result.Clone() : null;
    private static string Text(JsonElement value, string key, string fallback = "")
        => value.TryGetProperty(key, out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() : fallback;
    private static double? Number(JsonElement value, string key)
        => value.TryGetProperty(key, out var result) && result.ValueKind == JsonValueKind.Number
            && result.TryGetDouble(out var number) ? number : null;
}
