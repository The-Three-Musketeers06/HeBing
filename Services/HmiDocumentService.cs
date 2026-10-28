using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiVisionStudio.Models;

namespace AiVisionStudio.Services;

/// <summary>Reads/writes original JSON .aivh files and resolves declared value bindings.</summary>
public static class HmiDocumentService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static HmiDocument Load(string path)
    {
        var document = Parse(File.ReadAllText(path));
        document.SourcePath = Path.GetFullPath(path);
        return document;
    }
    public static HmiDocument Parse(string text)
    {
        var raw = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("HMI 文件根节点必须是 JSON 对象。");
        if (raw["HMI_data"] is not JsonObject data || data["component_data"] is not JsonArray pages)
            throw new InvalidDataException("HMI 文件缺少 HMI_data.component_data 页面列表。");
        var document = new HmiDocument(raw);
        for (var index = 0; index < pages.Count; index++)
        {
            if (pages[index] is not JsonObject pageData) throw new InvalidDataException($"HMI 页面 {index} 必须是 JSON 对象。");
            var page = new HmiPage(pageData, index);
            if (pageData["componentList"] is not JsonArray elements) throw new InvalidDataException($"HMI 页面 {index} 缺少 componentList。");
            ReadElements(elements, page.Elements);
            document.Pages.Add(page);
        }
        return document;
    }
    private static void ReadElements(JsonArray input, List<HmiElement> destination)
    {
        foreach (var item in input)
        {
            if (item is not JsonObject raw) throw new InvalidDataException("HMI 控件必须是 JSON 对象，未丢弃未知条目。");
            var element = new HmiElement(raw);
            foreach (var key in new[] { "groupList", "componentList", "children" })
                if (raw[key] is JsonArray children)
                {
                    element.ChildCollectionKey = key;
                    ReadElements(children, element.Children);
                    break;
                }
            destination.Add(element);
        }
    }
    public static string Serialize(HmiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = (JsonObject)document.Raw.DeepClone();
        if (root["HMI_data"] is not JsonObject data) root["HMI_data"] = data = new JsonObject();
        var pages = new JsonArray();
        for (var index = 0; index < document.Pages.Count; index++)
        {
            var page = document.Pages[index];
            var raw = (JsonObject)page.Raw.DeepClone();
            raw["componentList"] = new JsonArray(page.Elements.Select(WriteElement).ToArray());
            pages.Add(raw);
        }
        data["component_data"] = pages;
        return root.ToJsonString(JsonOptions) + Environment.NewLine;
    }
    private static JsonNode WriteElement(HmiElement element)
    {
        var raw = (JsonObject)element.Raw.DeepClone();
        if (element.ChildCollectionKey != null || element.Children.Count > 0)
            raw[element.ChildCollectionKey ?? "groupList"] = new JsonArray(element.Children.Select(WriteElement).ToArray());
        return raw;
    }
    public static void Save(string path, HmiDocument document)
    {
        var fullPath = Path.GetFullPath(path);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, Serialize(document), new UTF8Encoding(false));
            File.Move(temporary, fullPath, true);
            document.SourcePath = fullPath;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string FindForProject(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        var fullPath = Path.GetFullPath(projectPath);
        var matching = Path.ChangeExtension(fullPath, ".aivh");
        if (File.Exists(matching)) return matching;
        var directory = Path.GetDirectoryName(fullPath);
        if (directory == null || !Directory.Exists(directory)) return null;
        var candidates = Directory.EnumerateFiles(directory, "*.aivh").Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
    public static HmiDocument Create(double width = 1920, double height = 1080)
    {
        var page = new JsonObject { ["editCanvasConfig"] = new JsonObject
            { ["width"] = width, ["height"] = height, ["backGround"] = "#232324", ["backgroundImage"] = "", ["isSubwindow"] = false, ["selectColor"] = true },
            ["componentList"] = new JsonArray() };
        return Parse(new JsonObject { ["version"] = "4.5.1", ["imageList"] = new JsonArray(), ["HMI_data"] = new JsonObject
            { ["component_data"] = new JsonArray(page), ["request_global_config"] = new JsonObject { ["requestUrl"] = "", ["requestInterval"] = 30, ["requestIntervalUnit"] = "second" } } }.ToJsonString());
    }

    /// <summary>Delegates supply existing engine values. This never evaluates HMI script/filter text.</summary>
    public static object ResolveBinding(HmiBinding binding, Func<string, object> globalValue, Func<string, string, object> operatorValue)
    {
        if (binding == null) return null;
        if (binding.Kind == HmiBindingKind.GlobalVariable) return globalValue?.Invoke(binding.VariableName);
        if (binding.Kind != HmiBindingKind.OperatorOutput) return null;
        // Check the complete key first so literal output names containing dots
        // or brackets are not accidentally interpreted as object traversal.
        var direct = operatorValue?.Invoke(binding.OperationId, binding.ValuePath);
        if (direct != null) return direct;
        var value = operatorValue?.Invoke(binding.OperationId, binding.VariableName);
        foreach (var segment in binding.PathSegments.Skip(1))
        {
            value = ResolveMember(value, segment);
            if (value == null) break;
        }
        return value;
    }
    private static object ResolveMember(object value, string segment)
    {
        if (value is JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty(segment, out var property)) return property;
            if (json.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index) && index >= 0 && index < json.GetArrayLength()) return json[index];
            return null;
        }
        if (value is JsonObject map) return map[segment];
        if (value is JsonArray array) return int.TryParse(segment, out var index) && index >= 0 && index < array.Count ? array[index] : null;
        if (value is IDictionary dictionary) return dictionary.Contains(segment) ? dictionary[segment] : null;
        if (value is IList list) return int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < list.Count ? list[index] : null;
        if (value is IReadOnlyDictionary<string, object> readOnly) return readOnly.TryGetValue(segment, out var item) ? item : null;
        return null;
    }
}
