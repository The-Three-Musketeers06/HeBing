using System.Text.Json;
using AiVisionStudio.Models;

namespace AiVisionStudio.Services;

internal static class MatrixCodeSchema
{
    public static void Apply(IDictionary<string, DriverDef> definitions)
    {
        if (!definitions.TryGetValue("Basic 2D Code Reader", out var reader)) return;
        if (reader.Params.All(p => p.Name != "Code Number"))
            reader.Params.Insert(reader.Params.FindIndex(p => p.Name == "Decode Type") + 1,
                Scalar("Code Number", "最大读取数量（0为不限）", "int", 0, 0, 10000));
        var polarity = reader.Params.First(p => p.Name == "Code Polar");
        if (!polarity.Items.Contains("Both")) polarity.Items.Add("Both");
        var shape = reader.Params.First(p => p.Name == "Code Shape");
        if (!shape.Items.Contains("Auto")) shape.Items.Insert(0, "Auto");

        var values = reader.ParameterValues is { ValueKind: JsonValueKind.Object } current
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(current.GetRawText())! : new();
        values["Code Number"] = JsonSerializer.SerializeToElement(0);
        foreach (var toggle in new[] { "Text Display", "Result Area" })
        {
            values[toggle] = JsonSerializer.SerializeToElement(true);
            reader.Params.First(p => p.Name == toggle).Default = JsonSerializer.SerializeToElement(true);
        }
        reader.ParameterValues = JsonSerializer.SerializeToElement(values);

        foreach (var output in new[]
        {
            Scalar("Code Count", "识别数量", "int", 0), Scalar("Content", "内容（多码按行排列）", "string", ""),
            Scalar("Timed Out", "识别超时", "bool", false), Scalar("Limit Reached", "已达到设置数量", "bool", false),
            Scalar("Elapsed Time", "耗时（毫秒）", "float", 0),
            new ParamSpec { Name = "Codes", DisplayName = "二维码明细", Type = "array", ArrayType = "group", Children = new()
            {
                Scalar("Content", "内容", "string", ""), Scalar("Code Type", "码制", "string", ""),
                Scalar("Raw Bytes Base64", "原始内容字节（Base64）", "string", ""),
                Scalar("Symbology Identifier", "码制标识", "string", ""), Scalar("Angle", "角度", "float", 0),
                Scalar("Mirrored", "镜像", "bool", false), Scalar("Inverted", "反色", "bool", false),
                Scalar("Module Rows", "模块行数", "int", 0), Scalar("Module Columns", "模块列数", "int", 0),
                new ParamSpec { Name = "Corners", Type = "array", ArrayType = "group", Children = new()
                { Scalar("X", "X", "float", 0), Scalar("Y", "Y", "float", 0) } }
            } }
        })
            if (reader.Outputs.All(p => p.Name != output.Name)) { output.Field = "output"; reader.Outputs.Add(output); }
    }

    private static ParamSpec Scalar(string name, string label, string type, object value, double? min = null, double? max = null)
        => new() { Name = name, DisplayName = label, Type = type, Default = JsonSerializer.SerializeToElement(value), Minimum = min, Maximum = max };
}
