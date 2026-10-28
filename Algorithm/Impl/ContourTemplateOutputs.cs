namespace AiVisionStudio.Algorithm.Impl;

internal static class ContourTemplateOutputs
{
    internal static Dictionary<string, object> Pose(double x, double y, double angle, double scale) => new()
    {
        ["Point"] = DetectionOverlay2D.XY(x, y), ["Angle"] = angle, ["ScaleX"] = scale, ["ScaleY"] = scale
    };

    internal static void Clear(OpContext ctx)
    {
        foreach (var name in new[] { "Score", "X", "Y", "OffsetX", "OffsetY", "Angle", "Rotation", "Scale", "ElapsedMs" }) ctx.Vars.Set("ShapeMatch." + name, 0d);
        ctx.Vars.Set("ShapeMatch.TimedOut", false); ctx.Vars.Set("Elapsed Time", 0d);
        ctx.Vars.Set("Matches", new List<object>()); ctx.Vars.Set("Matched Num", 0);
        ctx.Vars.Set("Base Point", null); ctx.Vars.Set("Run Point", null); ctx.Vars.Set("Points Information", null);
    }
}
