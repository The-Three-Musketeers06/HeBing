using System.Text.Json;
using AiVisionStudio.Models;

namespace AiVisionStudio.Services;

/// <summary>Owned template editor parameters, layered over the imported tool catalog.</summary>
internal static class ContourTemplateSchema
{
    public static void Apply(IDictionary<string, DriverDef> definitions)
    {
        if (!definitions.TryGetValue("Basic Shape Model", out var model) ||
            !definitions.TryGetValue("Basic Shape Match", out var match)) return;

        var region = match.Params.First(p => p.Name == "Region");
        var trainingRegion = Copy(region);
        trainingRegion.Depends = null;
        trainingRegion.Field = "basic";
        trainingRegion.Default = model.Params.First(p => p.Name == "Region").Default;
        Replace(model, trainingRegion);
        var matchRegion = Copy(trainingRegion, "Region", "Training Region");
        matchRegion.DisplayName = "模板区域";
        Add(match, matchRegion);
        foreach (var name in new[] { "Use Shielding Area", "Shielding Area", "Parameters", "Create Model" })
            Add(match, Copy(model.Params.First(p => p.Name == name)));

        foreach (var definition in new[] { model, match })
        {
            Add(definition, Group("Model Origin", "模板原点", new { Auto = true, X = 0d, Y = 0d, Angle = 0d, Locked = false },
                Scalar("Auto", "自动居中", "bool", true), Scalar("X", "X", "float", 0),
                Scalar("Y", "Y", "float", 0), Scalar("Angle", "方向（°）", "float", 0), Scalar("Locked", "锁定", "bool", false)));
            Add(definition, new ParamSpec
            {
                Name = "Shielding Strokes", DisplayName = "画笔屏蔽笔画", Type = "array", ArrayType = "group",
                Children = trainingRegion.Children.Select(p => Copy(p)).ToList(), Default = JsonSerializer.SerializeToElement(Array.Empty<object>()),
                Raw = JsonSerializer.SerializeToElement(new { type = "array", array_type = "group" })
            });
            var create = definition.Params.First(p => p.Name == "Create Model");
            var defaults = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(create.Default!.Value.GetRawText());
            defaults!["Angle Range"] = JsonSerializer.SerializeToElement(new { Value = new { min = -180d, max = 180d } });
            create.Default = JsonSerializer.SerializeToElement(defaults);
            var angleValue = create.Children.First(p => p.Name == "Angle Range").Children.First(p => p.Name == "Value");
            angleValue.Default = JsonSerializer.SerializeToElement(new { min = -180d, max = 180d });
            angleValue.Children.First(p => p.Name == "min").Default = JsonSerializer.SerializeToElement(-180d);
            angleValue.Children.First(p => p.Name == "max").Default = JsonSerializer.SerializeToElement(180d);
        }

        Add(match, Range("Angle Range", "搜索角度（°）", -180, 180, -360, 360));
        Add(match, Range("Scale Range", "搜索尺度", 1, 1, .1, 5));
        Add(match, Scalar("Angle Step", "粗搜索角度步长（°）", "float", 5, .1, 36));
        Add(match, Scalar("Scale Step", "粗搜索尺度步长", "float", .05, .001, 1));
        Add(match, Scalar("Timeout", "超时（毫秒，0为不限）", "int", 3000, 0, 600000));

        // New tools show their detected contour immediately; saved projects retain explicit display choices.
        var values = match.ParameterValues is { ValueKind: JsonValueKind.Object } currentValues
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(currentValues.GetRawText())! : new Dictionary<string, JsonElement>();
        values["Display Contours"] = JsonSerializer.SerializeToElement(true);
        values["Sub Pixel"] = JsonSerializer.SerializeToElement(true);
        values["Max Overlap"] = JsonSerializer.SerializeToElement(.5);
        match.ParameterValues = JsonSerializer.SerializeToElement(values);
        match.Params.First(p => p.Name == "Display Contours").Default = JsonSerializer.SerializeToElement(true);
        match.Params.First(p => p.Name == "Sub Pixel").Default = JsonSerializer.SerializeToElement(true);
        match.Params.First(p => p.Name == "Max Overlap").Default = JsonSerializer.SerializeToElement(.5);

        var pose = new[] { Group("Point", "位置", new { X = 0d, Y = 0d }, Scalar("X", "X", "float", 0), Scalar("Y", "Y", "float", 0)),
            Scalar("Angle", "角度", "float", 0), Scalar("ScaleX", "X尺度", "float", 1), Scalar("ScaleY", "Y尺度", "float", 1) };
        foreach (var name in new[] { "Base Point", "Run Point" })
            if (match.Outputs.All(p => p.Name != name)) match.Outputs.Add(Group(name, name == "Base Point" ? "模板基准位姿" : "匹配运行位姿", new { }, pose.Select(p => Copy(p)).ToArray()));
        foreach (var name in new[] { "ShapeMatch.X", "ShapeMatch.Y", "ShapeMatch.Angle", "ShapeMatch.Scale", "ShapeMatch.Score", "ShapeMatch.ElapsedMs" })
            if (match.Outputs.All(p => p.Name != name)) match.Outputs.Add(Scalar(name, name, "float", 0));
        if (match.Outputs.All(p => p.Name != "Matched Num")) match.Outputs.Add(Scalar("Matched Num", "匹配个数", "int", 0));
        foreach (var definition in new[] { model, match })
        {
            Add(definition, new ParamSpec { Name = "Use Mask", DisplayName = "使用掩膜图像", Type = "bool", Field = "input", Default = JsonSerializer.SerializeToElement(false) });
            Add(definition, new ParamSpec { Name = "Mask Im In", DisplayName = "掩膜图像（白色有效）", Type = "im", Field = "input", Default = JsonSerializer.SerializeToElement(1), Depends = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["Use Mask"] = true }) });
        }
        var display = new ParamSpec { Name = "Match Display", DisplayName = "匹配标注", Type = "enum", Field = "render", Default = JsonSerializer.SerializeToElement("Contour"), Items = ["Contour", "Rectangle"] };
        Add(match, display);
        match.Params.First(p => p.Name == "Display Contours Attribute").Depends = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["Match Display"] = "Contour" });
        match.Params.First(p => p.Name == "Display Rect Region Attribute").Depends = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["Match Display"] = "Rectangle" });
        var gray = new DriverDef
        {
            Driver = AiVisionStudio.Algorithm.Impl.ContourTemplateEngine.GrayDriver, Group = match.Group, Version = match.Version,
            Window = match.Window, Raw = match.Raw, ParameterValues = match.ParameterValues, ParameterBind = match.ParameterBind,
            Params = match.Params.Select(p => Copy(p)).ToList(), Outputs = match.Outputs.Select(p => Copy(p)).ToList()
        };
        gray.Params.RemoveAll(p => p.Name is "Match Display" or "Display Contours Attribute" or "Scale Range" or "Scale Step" or "Angle Step" or "Greediness" or "Parameters");
        var grayCreate = gray.Params.First(p => p.Name == "Create Model");
        grayCreate.Children.RemoveAll(p => p.Name is "Scale Range" or "Scale Step" or "Optimization" or "Pregeneration");
        if (grayCreate.Default is { } grayDefaults)
        {
            var filtered = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(grayDefaults.GetRawText());
            foreach (var name in new[] { "Scale Range", "Scale Step", "Optimization", "Pregeneration" }) filtered.Remove(name);
            grayCreate.Default = JsonSerializer.SerializeToElement(filtered);
        }
        if (gray.ParameterValues is { } grayValues)
        {
            var filtered = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(grayValues.GetRawText());
            foreach (var name in new[] { "Scale Range", "Scale Step", "Angle Step", "Greediness", "Parameters" }) filtered.Remove(name);
            filtered["Create Model"] = grayCreate.Default.Value;
            gray.ParameterValues = JsonSerializer.SerializeToElement(filtered);
        }
        if (gray.Params.FirstOrDefault(p => p.Name == "Polarity") is { } polarity) polarity.Items.Remove("Ignore Local Polarity");
        match.Params.RemoveAll(p => p.Name is "Angle Step" or "Scale Step");
        gray.Params.First(p => p.Name == "Display Rect Region Attribute").Depends = null;
        foreach (var definition in new[] { match, gray })
        {
            definition.Params.RemoveAll(p => p.Name is "Base Point" or "Running Point" or "Base Point Display" or "Running Point Display" or "Display Contours" or "Display Rect Region");
            foreach (var parameter in definition.Params)
            {
                if (parameter.Name == "Use Region") parameter.DisplayName = "限制搜索区域";
                if (parameter.Name == "Region") parameter.DisplayName = "搜索区域";
                if (parameter.Name == "Display Rect Region Attribute") parameter.DisplayName = "匹配外框样式";
                if (parameter.Name == "Display Contours Attribute") parameter.DisplayName = "匹配轮廓样式";
            }
        }
        definitions[gray.Driver] = gray;
        foreach (var definition in definitions.Values)
            foreach (var parameter in definition.Params) AddGeometryArrays(parameter);
    }

    internal static bool IsHidden(string driver, IReadOnlyList<string> path)
    {
        if (!AiVisionStudio.Algorithm.Impl.ContourTemplateEngine.IsMatcher(driver) || path.Count == 0) return false;
        string name = path[0];
        if (name is "Model File" or "Angle Step" or "Scale Step" or "Base Point" or "Running Point" or "Base Point Display" or "Running Point Display" or "Display Contours" or "Display Rect Region") return true;
        if (driver != AiVisionStudio.Algorithm.Impl.ContourTemplateEngine.GrayDriver) return false;
        if (name is "Scale Range" or "Greediness" or "Parameters" or "Match Display" or "Display Contours Attribute") return true;
        return name == "Create Model" && path.Count > 1 && path[1] is "Scale Range" or "Scale Step" or "Optimization" or "Pregeneration";
    }

    private static void AddGeometryArrays(ParamSpec parameter)
    {
        foreach (var child in parameter.Children.ToArray()) AddGeometryArrays(child);
        if (parameter.Raw is not { } raw || !raw.TryGetProperty("support_array", out var enabled) || enabled.ValueKind != JsonValueKind.True) return;
        var selector = parameter.Children.FirstOrDefault(child => child.Name == "Type");
        if (selector == null) return;
        foreach (var shape in parameter.Support.Where(shape => !shape.EndsWith(" array", StringComparison.Ordinal)))
        {
            var element = parameter.Children.FirstOrDefault(child => child.Name == shape);
            var arrayName = shape + " array";
            if (element == null || parameter.Children.Any(child => child.Name == arrayName)) continue;
            var array = Copy(element);
            array.Name = arrayName; array.DisplayName = element.DisplayName + "（多区域）";
            array.Type = "array"; array.ArrayType = "group"; array.GroupType = "";
            array.Default = JsonSerializer.SerializeToElement(Array.Empty<object>());
            if (array.Depends is { } dependency)
            {
                using var document = JsonDocument.Parse(dependency.GetRawText().Replace("\"" + shape + "\"", "\"" + arrayName + "\""));
                array.Depends = document.RootElement.Clone();
            }
            parameter.Children.Add(array);
            if (!selector.Items.Contains(arrayName, StringComparer.Ordinal)) selector.Items.Add(arrayName);
        }
    }

    private static void Add(DriverDef definition, ParamSpec parameter)
    { if (definition.Params.All(p => p.Name != parameter.Name)) definition.Params.Add(parameter); }
    private static void Replace(DriverDef definition, ParamSpec parameter)
    {
        var index = definition.Params.FindIndex(p => p.Name == parameter.Name);
        if (index < 0) definition.Params.Add(parameter); else definition.Params[index] = parameter;
    }
    private static ParamSpec Scalar(string name, string label, string type, object value, double? min = null, double? max = null)
        => new() { Name = name, DisplayName = label, Type = type, Default = JsonSerializer.SerializeToElement(value), Minimum = min, Maximum = max };
    private static ParamSpec Group(string name, string label, object value, params ParamSpec[] children)
        => new() { Name = name, DisplayName = label, Type = "group", Children = children.ToList(), Default = JsonSerializer.SerializeToElement(value) };
    private static ParamSpec Range(string name, string label, double lower, double upper, double min, double max)
    {
        var range = Group(name, label, new { min = lower, max = upper },
            Scalar("min", "下限", "float", lower, min, max), Scalar("max", "上限", "float", upper, min, max));
        range.Type = "range"; return range;
    }
    private static ParamSpec Copy(ParamSpec original, string oldName = null, string newName = null)
    {
        JsonElement? Rewrite(JsonElement? value)
        {
            if (!value.HasValue || oldName == null) return value;
            using var document = JsonDocument.Parse(value.Value.GetRawText().Replace("\"" + oldName + "\":", "\"" + newName + "\":"));
            return document.RootElement.Clone();
        }
        return new ParamSpec
        {
            Name = original.Name == oldName ? newName : original.Name, DisplayName = original.DisplayName,
            Field = original.Field, Type = original.Type, Default = Rewrite(original.Default), Depends = Rewrite(original.Depends),
            Minimum = original.Minimum, Maximum = original.Maximum, ArrayType = original.ArrayType,
            GroupType = original.GroupType, Support = original.Support.ToList(), Items = original.Items.ToList(), Raw = Rewrite(original.Raw),
            Children = original.Children.Select(child => Copy(child, oldName, newName)).ToList()
        };
    }
}
