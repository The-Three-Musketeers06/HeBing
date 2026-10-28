using System.Collections;

namespace AiVisionStudio.Views;

/// <summary>Maps editable geometry between the teaching coordinate system and the current image.</summary>
internal static class FixtureRegionTransform
{
    public static Dictionary<string, object> Apply(IReadOnlyDictionary<string, object> wrapper, IReadOnlyDictionary<string, object> information, bool inverse)
    {
        if (information == null) return new Dictionary<string, object>(wrapper);
        var wrappedType = wrapper.GetValueOrDefault("Type")?.ToString();
        if (wrappedType?.EndsWith(" array", StringComparison.Ordinal) == true && wrapper.GetValueOrDefault(wrappedType) is IEnumerable array)
        {
            var elementType = wrappedType[..^6]; var resultArray = new Dictionary<string, object>(wrapper);
            resultArray[wrappedType] = array.Cast<object>().OfType<IReadOnlyDictionary<string, object>>().Select(value =>
                Apply(GraphicRegionValue.Wrap(elementType, value), information, inverse)[elementType]).ToList();
            return resultArray;
        }
        var first = GraphicRegionValue.Group(information, inverse ? "Run Point" : "Base Point") ?? throw new InvalidOperationException("缺少定位基准，请先运行前置模板匹配");
        var second = GraphicRegionValue.Group(information, inverse ? "Base Point" : "Run Point") ?? throw new InvalidOperationException("缺少定位结果，请先运行前置模板匹配");
        var firstPoint = GraphicRegionValue.Group(first, "Point"); var secondPoint = GraphicRegionValue.Group(second, "Point");
        var sx = GraphicRegionValue.Number(first, "ScaleX", 1); var sy = GraphicRegionValue.Number(first, "ScaleY", 1);
        if (sx <= 0 || sy <= 0) throw new InvalidOperationException("定位基准的缩放必须为正数");
        var scale = GraphicRegionValue.Number(second, "ScaleX", 1) / sx;
        var scaleY = GraphicRegionValue.Number(second, "ScaleY", 1) / sy;
        if (scale <= 0 || Math.Abs(scale - scaleY) > 1e-6) throw new InvalidOperationException("图形区域编辑需要等比定位缩放，请检查定位参数");
        var angle = GraphicRegionValue.Number(second, "Angle") - GraphicRegionValue.Number(first, "Angle");
        var radians = angle * Math.PI / 180; var cos = Math.Cos(radians); var sin = Math.Sin(radians);
        var type = wrapper.GetValueOrDefault("Type")?.ToString() ?? "rect2d";
        var source = GraphicRegionValue.Group(wrapper, type) ?? wrapper;
        var result = new Dictionary<string, object>(source);
        foreach (var key in new[] { "Center", "Start", "End", "Point", "Position" })
            if (GraphicRegionValue.Group(source, key) is { } point) result[key] = TransformPoint(point);
        if (source.ContainsKey("X") && source.ContainsKey("Y")) foreach (var pair in TransformPoint(source)) result[pair.Key] = pair.Value;
        if (GraphicRegionValue.Group(source, "Size") is { } size)
            result["Size"] = new Dictionary<string, object> { ["Width"] = GraphicRegionValue.Number(size, "Width") * scale, ["Height"] = GraphicRegionValue.Number(size, "Height") * scale };
        foreach (var key in new[] { "Radius", "Inner Radius", "Outer Radius", "Long Axis", "Short Axis" })
            if (source.ContainsKey(key)) result[key] = GraphicRegionValue.Number(source, key) * scale;
        foreach (var key in new[] { "Rotation", "Start Angle", "Angle" })
            if (source.ContainsKey(key)) result[key] = GraphicRegionValue.Number(source, key) + angle;
        if (source.GetValueOrDefault("Points") is IEnumerable points)
            result["Points"] = points.Cast<object>().OfType<IReadOnlyDictionary<string, object>>().Select(point => (object)TransformPoint(point)).ToList();
        return GraphicRegionValue.Wrap(type, result);
        Dictionary<string, object> TransformPoint(IReadOnlyDictionary<string, object> point)
        {
            var x = (GraphicRegionValue.Number(point, "X") - GraphicRegionValue.Number(firstPoint, "X")) * scale;
            var y = (GraphicRegionValue.Number(point, "Y") - GraphicRegionValue.Number(firstPoint, "Y")) * scale;
            return GraphicRegionValue.XY(GraphicRegionValue.Number(secondPoint, "X") + x * cos - y * sin, GraphicRegionValue.Number(secondPoint, "Y") + x * sin + y * cos);
        }
    }
}
