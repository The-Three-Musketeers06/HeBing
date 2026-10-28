namespace AiVisionStudio.Models;

public static class VisualResultNames
{
    public static bool IsOutputRoute(string driver, string name) => DriverCatalog.Get(driver)?.Params
        .Any(parameter => parameter.Name == name && parameter.Type == "im" && name.Contains("Out", StringComparison.OrdinalIgnoreCase)) == true;

    public static string Default(VisualResultKind kind) => kind switch
    { VisualResultKind.Cloud => "点云", VisualResultKind.Profile => "轮廓", _ => "图像" };

    public static string FromParameter(string name, string driver = null) => name switch
    {
        "IM Out" when driver == "Basic Create ROI" => "掩膜",
        "IM Out" => "图像",
        "Binarized Image Output" => "二值图像", "Blob Image Output" => "斑点图像", "Mask Out" => "掩膜",
        "IM Out R" => "红色通道（R）", "IM Out G" => "绿色通道（G）", "IM Out B" => "蓝色通道（B）",
        "IM Out H" => "色相通道（H）", "IM Out S" => "饱和度通道（S）", "IM Out V" => "明度通道（V）",
        _ => OutputLabels.Translate(name, driver)
    };
}
