using System.IO;
using System.Text.Json.Nodes;
using AiVisionStudio.Models;

namespace AiVisionStudio.Services;

/// <summary>The installed 4.5.1 component catalog and original default configurations.</summary>
public sealed class HmiComponentCatalog
{
    private readonly Dictionary<string, HmiComponentDefinition> _byKey;
    public IReadOnlyList<HmiComponentDefinition> Components { get; }
    public IReadOnlyList<HmiComponentDefinition> Entries => Components;
    public IReadOnlyList<HmiComponentCategory> Categories { get; }

    private HmiComponentCatalog(List<HmiComponentDefinition> components, List<HmiComponentCategory> categories)
    {
        Components = components.AsReadOnly();
        Categories = categories.AsReadOnly();
        _byKey = components.ToDictionary(component => component.Key, StringComparer.Ordinal);
    }

    public static HmiComponentCatalog Load(string path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "Assets", "hmi-components.json");
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("HMI 组件目录不是 JSON 对象。");
        var assetDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (root["components"] is not JsonArray entries) throw new InvalidDataException("HMI 组件目录缺少 components。");
        var components = new List<HmiComponentDefinition>();
        foreach (var entry in entries.OfType<JsonObject>())
        {
            var thumbnail = Read(entry, "thumbnail");
            if (entry["template"] is not JsonObject template) throw new InvalidDataException("HMI 组件缺少原始模板：" + Read(entry, "key"));
            components.Add(new HmiComponentDefinition(Read(entry, "key"), Read(entry, "title"), Read(entry, "package"), Read(entry, "packageName"),
                Read(entry, "category"), Read(entry, "categoryName"), thumbnail.Length == 0 ? "" : Path.GetFullPath(Path.Combine(assetDirectory, thumbnail)),
                Read(entry, "sourceKey"), Read(entry, "sourceSha256"), entry["importImage"]?.GetValue<bool>() == true, template,
                (entry["includes"] as JsonArray)?.Select(value => value?.ToString() ?? "").ToArray() ?? []));
        }
        var categories = new List<HmiComponentCategory>();
        if (root["categories"] is JsonArray categoryData)
            foreach (var category in categoryData.OfType<JsonObject>())
            {
                var package = Read(category, "package");
                var key = Read(category, "key");
                categories.Add(new HmiComponentCategory(package, Read(category, "packageName"), key, Read(category, "name"),
                    components.Where(component => component.Package == package && component.Category == key).ToArray()));
            }
        return new HmiComponentCatalog(components, categories);
    }

    public HmiComponentDefinition Find(string key) => key != null && _byKey.TryGetValue(key, out var component) ? component : null;

    public HmiElement Create(string key, double? x = null, double? y = null)
    {
        var definition = Find(key) ?? throw new KeyNotFoundException("HMI 组件不存在：" + key);
        var raw = definition.Template;
        raw["id"] = Guid.NewGuid().ToString("N")[..15];
        raw["canvasIndex"] = -1;
        raw["status"] = new JsonObject { ["lock"] = false, ["hide"] = false };
        raw["filter"] = "";
        var element = new HmiElement(raw);
        ClearBindings(element.Option);
        ApplyCurrentTimeDefaults(key, element.Option);
        if (x.HasValue) element.X = x.Value;
        if (y.HasValue) element.Y = y.Value;
        return element;
    }

    private static void ClearBindings(JsonObject option)
    {
        foreach (var key in new[] { "parameter", "sourceTable", "sourceLine" })
            if (option[key] is JsonArray values)
                for (var index = 0; index < values.Count; index++) values[index] = "";
        foreach (var key in new[] { "bindOperator", "bindSubwindowsIndex", "bindPrograms", "bindOperatorUUID" })
            if (option.ContainsKey(key)) option[key] = "";
        if (option["requestParams"] is JsonArray parameters)
            foreach (var parameter in parameters.OfType<JsonObject>())
                foreach (var key in new[] { "bindParams", "bindValue" })
                    if (parameter.ContainsKey(key)) parameter[key] = "";
        // Tab ids reference other canvas components, not tab labels or values.
        if (option["tabLabel"] != null && option["dataset"] is JsonArray tabs)
            foreach (var tab in tabs.OfType<JsonObject>()) if (tab.ContainsKey("id")) tab["id"] = "";
    }

    private static void ApplyCurrentTimeDefaults(string key, JsonObject option)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (key == "CountDown") option["endDate"] = now;
        if (key == "InputsDate") option["dataset"] = now;
        if (key == "NGStatisticsSearch") option["range"] = new JsonArray(JsonValue.Create(now - 60_000), JsonValue.Create(now));
        if (key == "TablesSearch" && option["condition"] is JsonObject condition)
        {
            condition["startDate"] = now - 3_600_000;
            condition["endDate"] = now;
        }
    }

    private static string Read(JsonObject value, string name) => value[name]?.ToString() ?? "";
}

public sealed class HmiComponentDefinition
{
    private readonly JsonObject _template;
    public string Key { get; }
    public string Title { get; }
    public string Package { get; }
    public string PackageName { get; }
    public string Category { get; }
    public string CategoryName { get; }
    public string ThumbnailPath { get; }
    public string SourceKey { get; }
    public string SourceSha256 { get; }
    public bool ImportImage { get; }
    public IReadOnlyList<string> Includes { get; }
    /// <summary>Every access returns an independent clone; edits cannot alter later insertions.</summary>
    public JsonObject Template => (JsonObject)_template.DeepClone();

    internal HmiComponentDefinition(string key, string title, string package, string packageName, string category, string categoryName,
        string thumbnailPath, string sourceKey, string sourceSha256, bool importImage, JsonObject template, string[] includes)
    {
        Key = key; Title = title; Package = package; PackageName = packageName; Category = category; CategoryName = categoryName;
        ThumbnailPath = thumbnailPath; SourceKey = sourceKey; SourceSha256 = sourceSha256; ImportImage = importImage;
        _template = (JsonObject)template.DeepClone(); Includes = Array.AsReadOnly(includes);
    }
    public override string ToString() => Title;
}

public sealed record HmiComponentCategory(string Package, string PackageName, string Key, string Name, IReadOnlyList<HmiComponentDefinition> Components);
