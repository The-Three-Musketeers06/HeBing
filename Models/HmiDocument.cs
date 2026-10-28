using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AiVisionStudio.Models;

/// <summary>A .aivh document. Raw keeps fields the compatibility UI does not edit.</summary>
public sealed class HmiDocument
{
    public string SourcePath { get; internal set; } = "";
    public JsonObject Raw { get; }
    public string Version => HmiJson.String(Raw["version"]);
    public List<HmiPage> Pages { get; } = new();
    public IEnumerable<HmiPage> MainPages => Pages.Where(page => !page.IsSubwindow);
    public IEnumerable<HmiPage> Subwindows => Pages.Where(page => page.IsSubwindow);
    public IEnumerable<HmiElement> AllElements => Pages.SelectMany(page => page.AllElements);
    public HmiDocument(JsonObject raw) => Raw = raw ?? throw new ArgumentNullException(nameof(raw));
}

public abstract class HmiJsonObject
{
    public JsonObject Raw { get; }
    protected HmiJsonObject(JsonObject raw) => Raw = raw ?? throw new ArgumentNullException(nameof(raw));
    public JsonNode GetNode(string path) => HmiJson.Get(Raw, path);
    public JsonElement? GetJson(string path) => GetNode(path) is { } node ? JsonSerializer.SerializeToElement(node) : null;
    public string GetString(string path, string fallback = "") => HmiJson.String(GetNode(path), fallback);
    public double GetNumber(string path, double fallback = 0) => HmiJson.Number(GetNode(path), fallback);
    public bool GetBoolean(string path, bool fallback = false) => HmiJson.Boolean(GetNode(path), fallback);
    public void SetNode(string path, JsonNode value) => HmiJson.Set(Raw, path, value);
    protected void Put(string objectName, string property, JsonNode value)
    {
        if (Raw[objectName] is not JsonObject target) Raw[objectName] = target = new JsonObject();
        target[property] = value;
    }
}

public sealed class HmiPage : HmiJsonObject
{
    public int Index { get; internal set; }
    public List<HmiElement> Elements { get; } = new();
    public IEnumerable<HmiElement> AllElements => Elements.SelectMany(element => element.DescendantsAndSelf());
    public JsonObject Config => GetNode("editCanvasConfig") as JsonObject;
    public double Width { get => GetNumber("editCanvasConfig.width", 1920); set => Put("editCanvasConfig", "width", value); }
    public double Height { get => GetNumber("editCanvasConfig.height", 1080); set => Put("editCanvasConfig", "height", value); }
    public string Background { get => GetString("editCanvasConfig.backGround", "#232324"); set => Put("editCanvasConfig", "backGround", value); }
    public string BackgroundImage => GetString("editCanvasConfig.backgroundImage");
    public bool IsSubwindow { get => GetBoolean("editCanvasConfig.isSubwindow"); set => Put("editCanvasConfig", "isSubwindow", value); }
    public string Title
    {
        get => GetString("name", GetString("editCanvasConfig.name", (IsSubwindow ? "子窗口 " : "看板 ") + (Index + 1)));
        set { if (Raw.ContainsKey("name")) Raw["name"] = value; else Put("editCanvasConfig", "name", value); }
    }
    public HmiPage(JsonObject raw, int index = 0) : base(raw) => Index = index;
}

public sealed class HmiElement : HmiJsonObject
{
    internal string ChildCollectionKey { get; set; }
    public List<HmiElement> Children { get; } = new();
    public string Id { get => GetString("id"); set => Raw["id"] = value; }
    /// <summary>The original key; icon components use the icon name here.</summary>
    public string Type { get => GetString("key", GetString("chartConfig.key")); set => Raw["key"] = value; }
    public string ComponentType => GetString("chartConfig.key", Type);
    public string Title { get => GetString("chartConfig.title", Type); set => Put("chartConfig", "title", value); }
    public double X { get => GetNumber("attr.x"); set => Put("attr", "x", value); }
    public double Y { get => GetNumber("attr.y"); set => Put("attr", "y", value); }
    public double Width { get => GetNumber("attr.w", 200); set => Put("attr", "w", value); }
    public double Height { get => GetNumber("attr.h", 70); set => Put("attr", "h", value); }
    public int ZIndex { get => (int)GetNumber("attr.zIndex"); set => Put("attr", "zIndex", value); }
    public bool Hidden { get => GetBoolean("status.hide"); set => Put("status", "hide", value); }
    public bool Locked { get => GetBoolean("status.lock"); set => Put("status", "lock", value); }
    public bool IsGroup => GetBoolean("isGroup");
    public JsonObject Option
    {
        get
        {
            if (Raw["option"] is JsonObject option) return option;
            if (Raw["option"] != null) throw new InvalidOperationException("HMI option must be a JSON object.");
            Raw["option"] = option = new JsonObject();
            return option;
        }
    }
    public string Text => GetString("option.dataset", GetString("option.label", GetString("option.data", Title)));
    public string Foreground => GetString("option.fontColor", GetString("option.color", "#FFFFFF"));
    public string Background => GetString("option.backgroundColor", "#00000000");
    public double FontSize => GetNumber("option.fontSize", 16);
    public IReadOnlyList<HmiBinding> Bindings => ReadBindings();
    public IReadOnlyList<HmiAction> Actions => ReadActions();
    public HmiElement(JsonObject raw) : base(raw) { }
    public IEnumerable<HmiElement> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var item in child.DescendantsAndSelf()) yield return item;
    }

    private IReadOnlyList<HmiBinding> ReadBindings()
    {
        var result = new List<HmiBinding>();
        if (GetNode("option.parameter") is JsonArray parameters)
            for (var index = 0; index < parameters.Count; index++)
            {
                var reference = HmiJson.String(parameters[index]);
                if (reference.Length != 0) result.Add(HmiBinding.Parse(reference, $"option.parameter[{index}]", index));
            }
        if (GetNode("option.sceneIM") is { } image)
            result.Add(new HmiBinding { Kind = HmiBindingKind.ImageRegister, Reference = HmiJson.String(image), SourcePath = "option.sceneIM", Index = -1 });
        if (GetNode("option.sourceTable") is JsonArray tables)
            for (var index = 0; index < tables.Count; index++)
                if (HmiJson.String(tables[index]) is { Length: > 0 } table)
                    result.Add(new HmiBinding { Kind = HmiBindingKind.Table, Reference = table, VariableName = table, SourcePath = $"option.sourceTable[{index}]", Index = index });
        return result;
    }

    private IReadOnlyList<HmiAction> ReadActions()
    {
        var actions = new List<HmiAction>();
        if (GetNode("option.iconGroup") is JsonArray icons)
            for (var index = 0; index < icons.Count; index++)
                if (icons[index] is JsonObject icon && icon["events"] is JsonArray events)
                    foreach (var item in events.OfType<JsonObject>())
                        actions.Add(new HmiAction { Raw = item, Trigger = HmiJson.String(item["type"], "click"),
                            Event = HmiJson.String(item["event"]), Target = HmiJson.String(item["target"]), IconIndex = index });
        if (GetNode("option.IconGroup") is JsonArray controls)
            for (var index = 0; index < controls.Count; index++)
                if (controls[index] is JsonObject control)
                    AddIcon(HmiJson.String(control["name"]), index, control);
        if (ComponentType == "Icon") AddIcon(Type, 0, Raw);
        if (Type == "SubwindowButton")
            actions.Add(new HmiAction { Event = "openSubwindow", Target = GetString("option.bindSubwindowsIndex"), Raw = Option, IsImplicit = true });
        if (Type == "MESButton")
            actions.Add(new HmiAction { Event = "mesRequest", Trigger = GetString("option.requestMethod", "click"), Raw = Option, IsImplicit = true });
        return actions;

        void AddIcon(string icon, int index, JsonObject raw)
        {
            var action = icon switch
            {
                "pauseCircleSolid" => "suspend", "nextSolid" => "singleStepOperation", "syncRounded" => "cycleOperation",
                "TrashAltRegular" => "clearProcedureState", "save" => "saveParams",
                "twotoneZoomOutMap" or "baselineCropFree" => "fullScreen", "outlineClear" => "closeSubwindow",
                "IcRoundLockOpen" => "lockScreen", _ => ""
            };
            if (action.Length > 0) actions.Add(new HmiAction { Event = action, IconIndex = index, Raw = raw, IsImplicit = true });
        }
    }
}

public enum HmiBindingKind { GlobalVariable, OperatorOutput, ImageRegister, Table }

public sealed class HmiBinding
{
    public string Reference { get; init; } = "";
    public HmiBindingKind Kind { get; init; }
    public string OperationId { get; init; } = "";
    public string VariableName { get; init; } = "";
    public string ValuePath { get; init; } = "";
    public IReadOnlyList<string> PathSegments { get; init; } = Array.Empty<string>();
    public string SourcePath { get; init; } = "";
    public int Index { get; init; }
    public static HmiBinding Parse(string reference, string sourcePath = "", int index = 0)
    {
        reference ??= "";
        var separator = reference.IndexOf('&');
        if (separator < 0)
            return new HmiBinding { Reference = reference, Kind = HmiBindingKind.GlobalVariable, VariableName = reference, ValuePath = reference, SourcePath = sourcePath, Index = index };
        var valuePath = reference[(separator + 1)..];
        var segments = HmiJson.Segments(valuePath);
        return new HmiBinding { Reference = reference, Kind = HmiBindingKind.OperatorOutput, OperationId = reference[..separator],
            VariableName = segments.FirstOrDefault() ?? valuePath, ValuePath = valuePath, PathSegments = segments, SourcePath = sourcePath, Index = index };
    }
}

public sealed class HmiAction
{
    public string Trigger { get; init; } = "click";
    public string Event { get; init; } = "";
    public string Target { get; init; } = "";
    public int IconIndex { get; init; }
    public bool IsImplicit { get; init; }
    public JsonObject Raw { get; init; }
}

internal static class HmiJson
{
    public static string String(JsonNode value, string fallback = "")
        => value is JsonValue scalar ? scalar.TryGetValue<string>(out var text) ? text : scalar.ToJsonString() : fallback;
    public static double Number(JsonNode value, double fallback = 0)
        => double.TryParse(String(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : fallback;
    public static bool Boolean(JsonNode value, bool fallback = false)
        => bool.TryParse(String(value), out var result) ? result : fallback;
    public static string[] Segments(string path)
        => Regex.Matches(path ?? "", @"[^.\[\]]+").Select(match => match.Value).ToArray();
    public static JsonNode Get(JsonNode node, string path)
    {
        if (string.IsNullOrEmpty(path)) return node;
        if (node is JsonObject direct && direct.TryGetPropertyValue(path, out var literal)) return literal;
        foreach (var segment in Segments(path))
            node = node switch
            {
                JsonObject map => map[segment],
                JsonArray sequence when int.TryParse(segment, out var index) && index >= 0 && index < sequence.Count => sequence[index],
                _ => null
            };
        return node;
    }
    public static void Set(JsonObject root, string path, JsonNode value)
    {
        var segments = Segments(path);
        if (segments.Length == 0) throw new ArgumentException("A JSON property path is required.", nameof(path));
        JsonNode current = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var key = segments[index];
            if (current is JsonObject map)
            {
                if (map[key] == null) map[key] = int.TryParse(segments[index + 1], out _) ? new JsonArray() : new JsonObject();
                current = map[key];
            }
            else if (current is JsonArray array && int.TryParse(key, out var position) && position >= 0 && position < array.Count) current = array[position];
            else throw new InvalidOperationException("HMI property path does not exist: " + path);
        }
        if (current is JsonObject target) target[segments[^1]] = value?.DeepClone();
        else if (current is JsonArray items && int.TryParse(segments[^1], out var slot) && slot >= 0 && slot < items.Count) items[slot] = value?.DeepClone();
        else throw new InvalidOperationException("HMI property path does not exist: " + path);
    }
}
