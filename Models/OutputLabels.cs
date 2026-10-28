namespace AiVisionStudio.Models;

public static class OutputLabels
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ShapeMatch.Score"] = "匹配得分",
        ["ShapeMatch.X"] = "匹配X坐标",
        ["ShapeMatch.Y"] = "匹配Y坐标",
        ["ShapeMatch.OffsetX"] = "X方向偏移",
        ["ShapeMatch.OffsetY"] = "Y方向偏移",
        ["ShapeMatch.Angle"] = "匹配角度（°）",
        ["ShapeMatch.Rotation"] = "旋转角度（°）",
        ["ShapeMatch.Scale"] = "匹配尺度",
        ["ShapeMatch.ElapsedMs"] = "匹配耗时（毫秒）",
        ["ShapeMatch.TimedOut"] = "匹配超时",
        ["ShapeModel.File"] = "模板文件",
        ["ShapeModel.Width"] = "模板宽度",
        ["ShapeModel.Height"] = "模板高度",
        ["ShapeModel.FeatureCount"] = "模板特征数",
        ["Base Point"] = "模板基准位姿",
        ["Run Point"] = "匹配运行位姿",
        ["Points Information"] = "定位位姿",
        ["ScaleX"] = "X方向尺度",
        ["ScaleY"] = "Y方向尺度",
        ["Rotation"] = "旋转角度",
        ["ElapsedMs"] = "耗时（毫秒）",
        ["TimedOut"] = "超时",
        ["OffsetX"] = "X方向偏移",
        ["OffsetY"] = "Y方向偏移",
        ["Result"] = "结果", ["Warning"] = "警告", ["Message"] = "提示信息", ["Time"] = "耗时（毫秒）",
        ["Filename"] = "文件名", ["Fullpath"] = "完整路径", ["Count"] = "数量", ["Matched Num"] = "匹配数量",
        ["Point Count"] = "点数量", ["EdgePointCount"] = "边缘点数量", ["Points"] = "点集", ["Point"] = "点",
        ["Segment"] = "线段", ["Circle"] = "圆", ["Center"] = "中心点", ["CenterX"] = "中心X", ["CenterY"] = "中心Y",
        ["Radius"] = "半径", ["Diameter"] = "直径", ["Angle"] = "角度", ["Length"] = "长度", ["Distance"] = "距离",
        ["Width"] = "宽度", ["Height"] = "高度", ["Position"] = "位置", ["Direction"] = "方向", ["Normal"] = "法向量",
        ["Area"] = "面积", ["Perimeter"] = "周长", ["Score"] = "得分", ["Scale"] = "尺度", ["Matches"] = "匹配结果",
        ["Minimum"] = "最小值", ["Maximum"] = "最大值", ["Mean"] = "平均值", ["Value"] = "值", ["Index"] = "序号",
        ["ROI"] = "感兴趣区域", ["ShapeCount"] = "图形数量", ["Contours"] = "轮廓", ["Plane"] = "平面",
        ["Content"] = "内容", ["Codes"] = "识别码列表", ["Code Count"] = "识别码数量", ["Code Type"] = "码类型",
        ["Corners"] = "角点", ["Module Rows"] = "模块行数", ["Module Columns"] = "模块列数", ["Mirrored"] = "镜像",
        ["Inverted"] = "反色", ["Limit Reached"] = "已达到数量上限", ["Elapsed Time"] = "耗时（毫秒）",
        ["Split Mode"] = "拆分模式", ["Channel Count"] = "输出通道数", ["Input Channel Count"] = "输入通道数", ["Convert Type"] = "转换类型",
        ["Hue Range"] = "色相数值范围", ["IM Out R"] = "红色通道图像（R）", ["IM Out G"] = "绿色通道图像（G）",
        ["IM Out B"] = "蓝色通道图像（B）", ["IM Out H"] = "色相通道图像（H）", ["IM Out S"] = "饱和度通道图像（S）",
        ["IM Out V"] = "明度通道图像（V）"
    };

    public static string Translate(string name, string driver = null)
    {
        if (string.IsNullOrEmpty(name)) return name ?? "";
        if (Names.TryGetValue(name, out var label)) return label;
        var translated = DriverCatalog.Translate(name, driver);
        if (translated != name) return translated;
        if (name.Contains('.')) return string.Join(".", name.Split('.').Select(part => Translate(part, driver)));
        int bracket = name.IndexOf('[');
        return bracket > 0 ? Translate(name[..bracket], driver) + name[bracket..] : name;
    }
}
