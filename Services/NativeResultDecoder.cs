using System.Globalization;
using System.IO;
using System.Text.Json;
using AiVisionStudio.Algorithm;

namespace AiVisionStudio.Services;

/// <summary>
/// Decodes the original application's documented-by-frontend JSON wire format.
/// Typed groups/arrays retain every field, including metadata. No measurements are recalculated.
/// Evidence: shots/detection-original/output-*.json, renderer-IM*.json, point-cloud-worker.js.
/// </summary>
public static class NativeResultDecoder
{
    public static VarPool DecodeOutputs(JsonElement response)
    {
        var data = Unwrap(response);
        var outputs = data.ValueKind == JsonValueKind.Array ? data
            : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("outputs", out var array) ? array
            : throw new InvalidDataException("原生输出响应缺少 outputs 数组");
        if (outputs.ValueKind != JsonValueKind.Array) throw new InvalidDataException("原生 outputs 必须为数组");
        var result = new VarPool(caseSensitive: true);
        foreach (var field in outputs.EnumerateArray())
        {
            var name = String(field, "name");
            if (string.IsNullOrEmpty(name)) throw new InvalidDataException("原生输出字段缺少 name");
            if (result.All.ContainsKey(name)) throw new InvalidDataException("原生输出字段重复：" + name);
            result.Set(name, DecodeField(field));
        }
        return result;
    }

    /// <summary>Returns decoded immutable-by-convention records for any original overlay type.</summary>
    public static IReadOnlyList<NativeOverlay3D> DecodeOverlays(JsonElement response, int im)
    {
        var data = Unwrap(response);
        var overlays = data.ValueKind == JsonValueKind.Array ? data
            : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("overlay", out var array) ? array
            : throw new InvalidDataException("原生渲染响应缺少 overlay 数组");
        if (overlays.ValueKind != JsonValueKind.Array) throw new InvalidDataException("原生 overlay 必须为数组");
        var decoded = new List<NativeOverlay3D>();
        foreach (var entry in overlays.EnumerateArray())
        {
            var value = DecodeField(entry);
            if (value is Dictionary<string, object> group) Add(group);
            else if (value is System.Collections.IEnumerable groupArray)
                foreach (var item in groupArray)
                    if (item is Dictionary<string, object> fields) Add(fields);
                    else throw new InvalidDataException("原生叠加图元数组元素必须为 group");
            else throw new InvalidDataException("原生叠加图元必须为 group 或 group array");
            void Add(Dictionary<string, object> fields)
            {
                var type = Text(fields, "_type", String(entry, "group_type"));
                // The renderer response is scoped to the requested IM; nested point metadata often uses -1.
                decoded.Add(new NativeOverlay3D { Type = type, ViewIndex = im, Values = fields });
            }
        }
        return decoded;
    }

    /// <summary>Replace one IM's overlays after fully validating them, preserving other IM slots.</summary>
    public static void ApplyRenderer(JsonElement response, OpContext ctx, int im)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var records = DecodeOverlays(response, im);
        var boxes = new List<Box3>(); var texts = new List<Text3D>();
        foreach (var record in records)
        {
            var value = record.Values;
            switch (record.Type.ToLowerInvariant())
            {
                case "box":
                {
                    var start = Point(value, "Start"); var end = Point(value, "End");
                    boxes.Add(new Box3 { X1 = start.X, Y1 = start.Y, Z1 = start.Z, X2 = end.X, Y2 = end.Y, Z2 = end.Z,
                        ViewIndex = im, Color = Text(value, "_color", "#00FF00"), FillOpacity = .3 });
                    break;
                }
                case "text3d":
                {
                    var position = Point(value, "Position");
                    texts.Add(new Text3D { X = position.X, Y = position.Y, Z = position.Z, ViewIndex = im,
                        Text = TextItems(value), Size = Number(value, "_size", 24), SizeIsPixels = true,
                        Color = Text(value, "_color", "green") });
                    break;
                }
                // Preserve other types in NativeOverlays. A new visual renderer can consume them
                // without a protocol change; do not substitute a misleading bounding box.
            }
        }
        ctx.Boxes.RemoveAll(box => box.ViewIndex == im);
        ctx.Texts.RemoveAll(text => text.ViewIndex == im);
        ctx.NativeOverlays.RemoveAll(overlay => overlay.ViewIndex == im);
        ctx.Boxes.AddRange(boxes); ctx.Texts.AddRange(texts); ctx.NativeOverlays.AddRange(records);
    }

    /// <summary>The original point-cloud worker consumes standard PCD, little-endian XYZ fields.</summary>
    public static PointCloud DecodePointCloud(ReadOnlySpan<byte> bytes) => PcdIO.Load(bytes.ToArray());
    public static PointCloud DecodePointCloud(byte[] bytes) => PcdIO.Load(bytes);
    public static ProfileData DecodeProfile(byte[] bytes)
    {
        var cloud = DecodePointCloud(bytes);
        var indices = Enumerable.Range(0, cloud.Count).Where(i => float.IsFinite(cloud.X[i]) && float.IsFinite(cloud.Y[i]) && float.IsFinite(cloud.Z[i])).ToArray();
        return new ProfileData(indices.Select(i => cloud.X[i]).ToArray(), indices.Select(i => cloud.Z[i]).ToArray());
    }

    public static object DecodeField(JsonElement field)
    {
        if (field.ValueKind != JsonValueKind.Object) return DecodePlain(field);
        if (!field.TryGetProperty("type", out var typeNode) || !field.TryGetProperty("value", out var value))
            return DecodePlain(field);
        var type = typeNode.GetString() ?? "";
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return type.ToLowerInvariant() switch
        {
            "group" => DecodeGroup(value, String(field, "group_type")),
            "array" => DecodeArray(value, String(field, "array_type"), String(field, "group_type")),
            "bool" or "boolean" => Boolean(value),
            "int" or "integer" => Integer(value),
            "float" or "double" => Float(value),
            "string" or "enum" => ScalarText(value),
            _ => DecodePlain(value)
        };
    }

    private static Dictionary<string, object> DecodeGroup(JsonElement value, string groupType = "")
    {
        var group = new Dictionary<string, object>(StringComparer.Ordinal);
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var name = String(item, "name");
                if (string.IsNullOrEmpty(name)) throw new InvalidDataException("原生 group 子字段缺少 name");
                if (!group.TryAdd(name, DecodeField(item))) throw new InvalidDataException("原生 group 字段重复：" + name);
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) group[property.Name] = DecodePlain(property.Value);
        else throw new InvalidDataException("原生 group 值必须为数组或对象");
        if (!string.IsNullOrEmpty(groupType) && !group.ContainsKey("_type")) group["_type"] = groupType;
        return group;
    }
    private static object DecodeArray(JsonElement value, string elementType, string groupType)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("原生 array 值必须为数组");
        var values = value.EnumerateArray().ToArray();
        // The native serializer emits null for unavailable numeric measurements (e.g. Sealant).
        // Preserve those positions instead of inventing zero or rejecting an otherwise valid result.
        if (values.Any(item => item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
            return values.Select(item => item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null
                : elementType.ToLowerInvariant() switch {
                    "float" or "double" => Float(item), "int" or "integer" => Integer(item),
                    "bool" or "boolean" => Boolean(item), "string" or "enum" => ScalarText(item),
                    "group" => DecodeGroup(item, groupType), _ => DecodePlain(item)
                }).ToArray();
        switch (elementType.ToLowerInvariant())
        {
            case "float": case "double": return values.Select(Float).ToArray();
            case "int": case "integer":
                var integers = values.Select(Integer).ToArray();
                return integers.All(number => number is int) ? integers.Cast<int>().ToArray() : integers.Select(Convert.ToInt64).ToArray();
            case "bool": case "boolean": return values.Select(Boolean).ToArray();
            case "string": case "enum": return values.Select(ScalarText).ToArray();
            case "group": return values.Select(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out _)
                ? DecodeField(item) : DecodeGroup(item, groupType)).ToArray();
            default: return values.Select(DecodePlain).ToArray();
        }
    }
    private static object DecodePlain(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => PlainNumber(value),
            JsonValueKind.Array => value.EnumerateArray().Select(DecodePlain).ToArray(),
            JsonValueKind.Object when value.TryGetProperty("type", out _) && value.TryGetProperty("value", out _) => DecodeField(value),
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(property => property.Name, property => DecodePlain(property.Value), StringComparer.Ordinal),
            _ => throw new InvalidDataException("未知的原生 JSON 值类型")
        };
    }
    private static object PlainNumber(JsonElement value)
    {
        if (value.TryGetInt32(out var integer)) return integer;
        if (value.TryGetInt64(out var wide)) return wide;
        return value.GetDouble();
    }
    public static JsonElement Unwrap(JsonElement response)
    {
        if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("code", out var code))
        {
            if (!code.TryGetInt32(out var number) || number != 0)
                throw new InvalidDataException("原生接口失败：" + ScalarText(code) + " " + String(response, "message"));
            if (!response.TryGetProperty("data", out var data)) throw new InvalidDataException("原生接口响应缺少 data");
            return data;
        }
        return response;
    }
    public static string FormatMessage(object value)
    {
        if (value == null) return "";
        if (value is string text) return text;
        if (value is IReadOnlyDictionary<string, object> group)
        {
            foreach (var key in new[] { "_message", "Message", "message", "_text", "text", "_value" })
                if (group.TryGetValue(key, out var message) && !string.IsNullOrEmpty(Convert.ToString(message, CultureInfo.InvariantCulture)))
                    return Convert.ToString(message, CultureInfo.InvariantCulture);
            if (group.TryGetValue("_items", out var items))
            {
                var formatted = FormatMessage(items); if (!string.IsNullOrEmpty(formatted)) return formatted;
            }
            if (group.TryGetValue("_code", out var code))
            {
                var number = Convert.ToString(code, CultureInfo.InvariantCulture);
                return number == "0" ? "" : "原生消息代码 " + number;
            }
            return JsonSerializer.Serialize(group);
        }
        if (value is System.Collections.IEnumerable array)
            return string.Join("; ", array.Cast<object>().Select(FormatMessage).Where(message => message.Length > 0));
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }
    private static string TextItems(IReadOnlyDictionary<string, object> fields)
    {
        if (fields.TryGetValue("_items", out var raw) && raw is System.Collections.IEnumerable items)
            return string.Join("", items.Cast<object>().Select(item => item is IReadOnlyDictionary<string, object> map
                ? TextItem(map) : Convert.ToString(item, CultureInfo.InvariantCulture)));
        return Text(fields, "Text", Text(fields, "_value", ""));
    }
    private static string TextItem(IReadOnlyDictionary<string, object> item)
    {
        var text = Text(item, "_value", "");
        if (item.TryGetValue("_precision", out var rawPrecision)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
        {
            int.TryParse(Convert.ToString(rawPrecision, CultureInfo.InvariantCulture), out var precision);
            // Original RV helper falls back to the global default (3) for zero or negative precision.
            precision = precision > 0 ? Math.Min(precision, 100) : 3;
            return number.ToString("F" + precision, CultureInfo.InvariantCulture);
        }
        return text;
    }
    private static (double X, double Y, double Z) Point(IReadOnlyDictionary<string, object> fields, string key)
    {
        if (!fields.TryGetValue(key, out var raw) || raw is not IReadOnlyDictionary<string, object> point)
            throw new InvalidDataException("原生图元缺少坐标：" + key);
        return (Number(point, "X"), Number(point, "Y"), Number(point, "Z"));
    }
    private static double Number(IReadOnlyDictionary<string, object> fields, string key, double? fallback = null)
    {
        if (!fields.TryGetValue(key, out var raw) || raw == null)
            return fallback ?? throw new InvalidDataException("原生坐标缺少分量：" + key);
        if (double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) return number;
        throw new InvalidDataException("原生图元包含非有限数值：" + key);
    }
    private static string Text(IReadOnlyDictionary<string, object> fields, string key, string fallback)
        => fields.TryGetValue(key, out var raw) && !string.IsNullOrEmpty(Convert.ToString(raw, CultureInfo.InvariantCulture))
            ? Convert.ToString(raw, CultureInfo.InvariantCulture) : fallback;
    private static string String(JsonElement field, string name)
        => field.ValueKind == JsonValueKind.Object && field.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string ScalarText(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    private static double Float(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
        throw new InvalidDataException("原生 float 不是有效数值");
    }
    private static object Integer(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            || value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return number is >= int.MinValue and <= int.MaxValue ? (object)(int)number : number;
        throw new InvalidDataException("原生 int 不是有效整数");
    }
    private static bool Boolean(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var flag)) return flag;
        throw new InvalidDataException("原生 bool 不是有效布尔值");
    }
}
