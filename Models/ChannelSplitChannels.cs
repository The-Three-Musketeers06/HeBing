namespace AiVisionStudio.Models;

public static class ChannelSplitChannels
{
    public const string Driver = "Basic Channel Split";
    private static readonly string[] Rgb = ["R", "G", "B"];
    private static readonly string[] Hsv = ["H", "S", "V"];
    public static IReadOnlyList<string> Names(string mode) => mode?.ToUpperInvariant() switch
    {
        "RGB" => Rgb, "HSV" => Hsv, _ => throw new ArgumentException("拆分模式只支持RGB或HSV")
    };
    public static string Key(string name) => "channel:" + name;
    public static string Identity(string outputKey) => outputKey?.StartsWith("channel:", StringComparison.Ordinal) == true ? outputKey : null;
    public static string Caption(string outputKey) => Identity(outputKey) == null ? "" : VisualResultNames.FromParameter("IM Out " + outputKey[8..]);
    public static int Order(string outputKey) => outputKey switch
    {
        "channel:R" or "channel:H" => 0, "channel:G" or "channel:S" => 1, "channel:B" or "channel:V" => 2, _ => 0
    };
}
