using AiVisionStudio.Models;

namespace AiVisionStudio.Services;

internal static class ChannelSplitSchema
{
    public static void Apply(IDictionary<string, DriverDef> definitions)
    {
        if (!definitions.TryGetValue(ChannelSplitChannels.Driver, out var definition)) return;
        var mode = definition.Params.FirstOrDefault(parameter => parameter.Name == "Split Mode");
        if (mode != null) { definition.Params.Remove(mode); definition.Params.Insert(Math.Min(1, definition.Params.Count), mode); }
        foreach (var parameter in definition.Params.Where(parameter => parameter.Name.StartsWith("IM Out ", StringComparison.Ordinal)))
        { parameter.Field = "basic"; parameter.DisplayName = OutputLabels.Translate(parameter.Name); }
        foreach (var name in new[] { "Split Mode", "Channel Count", "Input Channel Count", "IM Out R", "IM Out G", "IM Out B", "IM Out H", "IM Out S", "IM Out V", "Hue Range" })
            if (!definition.Outputs.Any(output => output.Name == name)) definition.Outputs.Add(new ParamSpec
            { Name = name, DisplayName = OutputLabels.Translate(name), Field = "output", Type = name is "Split Mode" or "Hue Range" ? "string" : "int" });
    }
}
