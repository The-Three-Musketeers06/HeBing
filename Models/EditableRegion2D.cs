using System.Globalization;
using System.Windows;
using System.Windows.Media;
using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Models;

public sealed record RegionEditTarget(string OperationId, string[] RootPath, string TypeKey, int? Index, ParamSpec Specification)
{
    public string Kind => TypeKey.Replace(" array", "");
}

public sealed class EditableRegion2D
{
    public static readonly string[] SupportedKinds = ["rect2d", "window2d", "circle2d", "circlewindow2d", "ring2d", "sector2d", "polygon2d", "line_caliper", "circle_caliper", "arc_caliper", "point2d", "segment2d", "ellipse2d"];
    public string Kind { get; }
    public YamlMappingNode Data { get; }
    public EditableRegion2D(string kind, YamlMappingNode data) { Kind = kind; Data = (YamlMappingNode)Copy(data); }
    public EditableRegion2D Clone() => new(Kind, Data);
    public static YamlNode Copy(YamlNode node) => node switch
    {
        YamlMappingNode map => new YamlMappingNode(map.Children.Select(pair => new KeyValuePair<YamlNode, YamlNode>(Copy(pair.Key), Copy(pair.Value)))),
        YamlSequenceNode list => new YamlSequenceNode(list.Children.Select(Copy)),
        YamlScalarNode scalar => new YamlScalarNode(scalar.Value),
        _ => new YamlMappingNode()
    };
    private YamlNode At(params string[] path)
    {
        YamlNode node = Data;
        foreach (var key in path) node = node is YamlMappingNode map && map.Children.TryGetValue(new YamlScalarNode(key), out var child) ? child : null;
        return node;
    }
    public double Number(string name, double fallback = 0) => At(name) is YamlScalarNode scalar
        && double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : fallback;
    private bool Flag(string name) => At(name) is YamlScalarNode scalar && bool.TryParse(scalar.Value, out var value) && value;
    private Point PointValue(string name = null)
    {
        var map = name == null ? Data : At(name) as YamlMappingNode;
        double Read(string key) => map != null && map.Children.TryGetValue(new YamlScalarNode(key), out var node)
            && double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
        return new Point(Read("X"), Read("Y"));
    }
    private void Set(string name, double value) => Data.Children[new YamlScalarNode(name)] = new YamlScalarNode(value.ToString("G17", CultureInfo.InvariantCulture));
    private void SetFlag(string name, bool value) => Data.Children[new YamlScalarNode(name)] = new YamlScalarNode(value ? "true" : "false");
    private void SetPoint(string name, Point point)
    {
        var map = name == null ? Data : At(name) as YamlMappingNode ?? new YamlMappingNode();
        map.Children[new YamlScalarNode("X")] = new YamlScalarNode(point.X.ToString("G17", CultureInfo.InvariantCulture));
        map.Children[new YamlScalarNode("Y")] = new YamlScalarNode(point.Y.ToString("G17", CultureInfo.InvariantCulture));
        if (name != null) Data.Children[new YamlScalarNode(name)] = map;
    }
    public Point[] Points => At("Points") is YamlSequenceNode list ? list.Children.OfType<YamlMappingNode>()
        .Select(map => new EditableRegion2D("point2d", map).PointValue()).ToArray() : [];
    public void SetPoints(IEnumerable<Point> points) => Data.Children[new YamlScalarNode("Points")] = new YamlSequenceNode(points.Select(point =>
    {
        var item = new EditableRegion2D("point2d", new YamlMappingNode()); item.SetPoint(null, point); return item.Data;
    }));
    public Point Center => Kind == "point2d" ? PointValue() : Kind is "line_caliper" or "segment2d" or "window2d" or "arc_caliper"
        ? Mid(PointValue("Start"), PointValue("End")) : Kind == "polygon2d" ? Points.Length == 0 ? new Point() : new Point(Points.Average(p => p.X), Points.Average(p => p.Y)) : PointValue("Center");
    public static Point Mid(Point first, Point second) => new((first.X + second.X) / 2, (first.Y + second.Y) / 2);
    private static Point Polar(Point center, double radius, double angle) => center + new Vector(Math.Cos(angle * Math.PI / 180), Math.Sin(angle * Math.PI / 180)) * radius;
    private static double Angle(Point center, Point point) => Math.Atan2(point.Y - center.Y, point.X - center.X) * 180 / Math.PI;
    private static double Normalize(double angle) => (angle % 360 + 360) % 360;
    private double Width => At("Size") is YamlMappingNode size ? new EditableRegion2D("", size).Number("Width") : Number("Long Axis");
    private double Height => At("Size") is YamlMappingNode size ? new EditableRegion2D("", size).Number("Height") : Number("Short Axis");
    public static readonly string[] CornerKeys = ["cornerLT", "cornerRT", "cornerRB", "cornerLB"];
    public Point[] RectangleCorners
    {
        get
        {
            var rotate = new RotateTransform(Number("Rotation"), Center.X, Center.Y);
            return new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) }
                .Select(corner => rotate.Transform(Center + new Vector(corner.Item1 * Width / 2, corner.Item2 * Height / 2))).ToArray();
        }
    }
    public string EdgeHandle(Point point, double tolerance)
    {
        if (Kind != "rect2d") return null;
        var corners = RectangleCorners;
        var edges = new[] { "top", "right", "bottom", "left" };
        for (int i = 0; i < corners.Length; i++)
        {
            var edge = corners[(i + 1) % 4] - corners[i];
            if (edge.LengthSquared < .0001) continue;
            var closest = corners[i] + edge * Math.Clamp(Vector.Multiply(point - corners[i], edge) / edge.LengthSquared, 0, 1);
            if ((point - closest).Length <= tolerance) return edges[i];
        }
        return null;
    }
    private void ResizeRectangle(string key, Point position)
    {
        var center = Center;
        double angle = Number("Rotation"), left = -Width / 2, right = Width / 2, top = -Height / 2, bottom = Height / 2;
        var local = new RotateTransform(-angle, center.X, center.Y).Transform(position) - center;
        if (key is "left" or "cornerLT" or "cornerLB") left = Math.Min(local.X, right - 1);
        if (key is "right" or "cornerRT" or "cornerRB") right = Math.Max(local.X, left + 1);
        if (key is "top" or "cornerLT" or "cornerRT") top = Math.Min(local.Y, bottom - 1);
        if (key is "bottom" or "cornerLB" or "cornerRB") bottom = Math.Max(local.Y, top + 1);
        SetPoint("Center", new RotateTransform(angle, center.X, center.Y).Transform(center + new Vector((left + right) / 2, (top + bottom) / 2)));
        SetSize(right - left, bottom - top);
    }
    private void SetSize(double width, double height)
    {
        if (Kind == "ellipse2d") { Set("Long Axis", width); Set("Short Axis", height); return; }
        Data.Children[new YamlScalarNode("Size")] = new YamlMappingNode { { "Width", width.ToString("G17", CultureInfo.InvariantCulture) }, { "Height", height.ToString("G17", CultureInfo.InvariantCulture) } };
    }
    private double PolygonArea()
    {
        var points = Points;
        return points.Length < 3 ? 0 : Math.Abs(points.Select((point, i) => point.X * points[(i + 1) % points.Length].Y - point.Y * points[(i + 1) % points.Length].X).Sum());
    }
    public bool IsValid => Kind switch
    {
        "polygon2d" => PolygonArea() > .01,
        "rect2d" or "ellipse2d" => Width > .01 && Height > .01,
        "window2d" or "line_caliper" or "segment2d" => (PointValue("End") - PointValue("Start")).Length > .01,
        "arc_caliper" => (PointValue("End") - PointValue("Start")).Length > .01 && Number("Radius") >= (PointValue("End") - PointValue("Start")).Length / 2,
        "circle2d" or "circlewindow2d" or "circle_caliper" => Number("Radius") > .01,
        "ring2d" or "sector2d" => Number("Outer Radius") > Number("Inner Radius") && Number("Inner Radius") >= 0 && (Kind != "sector2d" || Number("Angle Range") > 0),
        "point2d" => true,
        _ => false
    };
    public void DrawFrom(Point start, Point end)
    {
        switch (Kind)
        {
            case "rect2d": case "ellipse2d": SetPoint("Center", Mid(start, end)); SetSize(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y)); Set("Rotation", 0); break;
            case "window2d": case "line_caliper": case "segment2d": case "arc_caliper":
                SetPoint("Start", start); SetPoint("End", end);
                if (Kind == "arc_caliper") Set("Radius", Math.Max(1, (end - start).Length * .65));
                break;
            case "point2d": SetPoint(null, end); break;
            default:
                SetPoint("Center", start);
                double radius = (end - start).Length;
                if (Kind is "ring2d" or "sector2d") { Set("Outer Radius", radius); Set("Inner Radius", radius * .6); if (Kind == "sector2d") { Set("Start Angle", 0); Set("Angle Range", 90); } }
                else Set("Radius", radius);
                break;
        }
    }
    public void Translate(Vector shift)
    {
        if (Kind == "polygon2d") SetPoints(Points.Select(point => point + shift));
        else if (Kind is "window2d" or "line_caliper" or "segment2d" or "arc_caliper") { SetPoint("Start", PointValue("Start") + shift); SetPoint("End", PointValue("End") + shift); }
        else SetPoint(Kind == "point2d" ? null : "Center", Center + shift);
    }
    private (Point Center, double Start, double Sweep) Arc()
    {
        var start = PointValue("Start"); var end = PointValue("End"); var direction = end - start;
        double chord = Math.Max(.0001, direction.Length), radius = Math.Max(Number("Radius"), chord / 2);
        var normal = new Vector(-direction.Y / chord, direction.X / chord);
        bool counterclockwise = Flag("AntiClockWise"), large = Flag("Large Arc");
        var center = Mid(start, end) + normal * (Math.Sqrt(Math.Max(0, radius * radius - chord * chord / 4)) * (counterclockwise == large ? 1 : -1));
        double a = Angle(center, start), sweep = counterclockwise ? -Normalize(a - Angle(center, end)) : Normalize(Angle(center, end) - a);
        return (center, a, sweep);
    }
    private Point[] ArcPoints(Point center, double radius, double start, double sweep) => Enumerable.Range(0, 73).Select(i => Polar(center, Math.Max(0, radius), start + sweep * i / 72)).ToArray();
    private static Geometry Path(IEnumerable<Point> points, bool closed)
    {
        var items = points.ToArray(); var geometry = new StreamGeometry();
        if (items.Length == 0) return geometry;
        using var context = geometry.Open(); context.BeginFigure(items[0], closed, closed); context.PolyLineTo(items.Skip(1).ToArray(), true, false); return geometry;
    }
    public Geometry Geometry()
    {
        switch (Kind)
        {
            case "point2d": return new EllipseGeometry(Center, 1, 1);
            case "polygon2d": return Path(Points, true);
            case "window2d": return new RectangleGeometry(new Rect(PointValue("Start"), PointValue("End")));
            case "rect2d": case "ellipse2d":
                Geometry shape = Kind == "ellipse2d" ? new EllipseGeometry(Center, Width / 2, Height / 2) : new RectangleGeometry(new Rect(Center.X - Width / 2, Center.Y - Height / 2, Math.Max(0, Width), Math.Max(0, Height)));
                shape.Transform = new RotateTransform(Number("Rotation"), Center.X, Center.Y); return shape;
            case "segment2d": return Path([PointValue("Start"), PointValue("End")], false);
            case "line_caliper":
                double length = (PointValue("End") - PointValue("Start")).Length, height = Math.Max(3, Number("Caliper Height", 20));
                var band = new RectangleGeometry(new Rect(Center.X - length / 2, Center.Y - height / 2, length, height));
                band.Transform = new RotateTransform(Angle(PointValue("Start"), PointValue("End")), Center.X, Center.Y); return band;
            case "circle2d": case "circlewindow2d": return new EllipseGeometry(Center, Math.Max(0, Number("Radius")), Math.Max(0, Number("Radius")));
            default:
                var center = Center; double radius = Number("Outer Radius"), inner = Number("Inner Radius"), start = Number("Start Angle"), sweep = Kind == "sector2d" ? Number("Angle Range", 90) : 360;
                if (Kind is "circle_caliper" or "arc_caliper")
                {
                    radius = Number("Radius") + Math.Max(3, Number("Caliper Height", 20)) / 2;
                    inner = Math.Max(0, Number("Radius") - Math.Max(3, Number("Caliper Height", 20)) / 2);
                    if (Kind == "arc_caliper") (center, start, sweep) = Arc();
                }
                return Path(ArcPoints(center, radius, start, sweep).Concat(ArcPoints(center, inner, start, sweep).Reverse()), true);
        }
    }
    public IEnumerable<Geometry> Calipers()
    {
        if (Kind is not ("line_caliper" or "circle_caliper" or "arc_caliper")) yield break;
        int count = (int)Math.Clamp(Number("Caliper Count", 20), 3, 200);
        double width = Math.Max(3, Number("Caliper Width", 5)), height = Math.Max(3, Number("Caliper Height", 20));
        for (int i = 0; i < count; i++)
        {
            Point center; double rotation;
            if (Kind == "line_caliper") { center = PointValue("Start") + (PointValue("End") - PointValue("Start")) * ((i + .5) / count); rotation = Angle(PointValue("Start"), PointValue("End")); }
            else { var arc = Kind == "arc_caliper" ? Arc() : (Center, 0d, 360d); double angle = arc.Item2 + arc.Item3 * (i + .5) / count; center = Polar(arc.Item1, Number("Radius"), angle); rotation = angle - 90; }
            var rect = new RectangleGeometry(new Rect(center.X - width / 2, center.Y - height / 2, width, height)); rect.Transform = new RotateTransform(rotation, center.X, center.Y); yield return rect;
        }
    }
    public IReadOnlyList<(string Key, Point Position)> Handles(double scale)
    {
        double offset = 24 / Math.Max(.0001, scale); var handles = new List<(string, Point)> { ("move", Center) };
        if (Kind == "polygon2d") handles.AddRange(Points.Select((point, index) => ("vertex" + index, point)));
        else if (Kind is "rect2d" or "ellipse2d")
        {
            var rotate = new RotateTransform(Number("Rotation"), Center.X, Center.Y);
            var corners = RectangleCorners;
            for (int i = 0; i < corners.Length; i++) handles.Add((CornerKeys[i], corners[i]));
            handles.Add(("left", Mid(corners[0], corners[3]))); handles.Add(("right", Mid(corners[1], corners[2])));
            handles.Add(("top", Mid(corners[0], corners[1]))); handles.Add(("bottom", Mid(corners[2], corners[3])));
            handles.Add(("rotate", rotate.Transform(Center + new Vector(0, -Height / 2 - offset))));
        }
        else if (Kind is "window2d" or "segment2d" or "line_caliper" or "arc_caliper")
        {
            handles.Add(("start", PointValue("Start"))); handles.Add(("end", PointValue("End")));
            var direction = PointValue("End") - PointValue("Start"); if (direction.Length > .001) direction.Normalize();
            var normal = new Vector(-direction.Y, direction.X);
            if (Kind == "line_caliper") { handles.Add(("height", Center + normal * (Number("Caliper Height", 20) / 2 + offset))); handles.Add(("width", PointValue("Start") + direction * (Number("Caliper Width", 5) / 2 + offset))); }
            if (Kind == "arc_caliper")
            {
                var arc = Arc();
                handles.Add(("arc", Polar(arc.Center, Number("Radius"), arc.Start + arc.Sweep / 2)));
                handles.Add(("height", Polar(arc.Center, Number("Radius") + Number("Caliper Height", 20) / 2 + offset, arc.Start + arc.Sweep / 2)));
                handles.Add(("width", Polar(PointValue("Start"), Number("Caliper Width", 5) / 2 + offset, arc.Start + 90)));
            }
        }
        else if (Kind != "point2d")
        {
            bool ring = Kind is "ring2d" or "sector2d";
            handles.Add(("radius", Polar(Center, Number(ring ? "Outer Radius" : "Radius"), ring ? Number("Start Angle") : 0)));
            if (ring) handles.Add(("inner", Polar(Center, Number("Inner Radius"), Kind == "sector2d" ? Number("Start Angle") + Number("Angle Range") / 2 : 90)));
            if (Kind == "sector2d") { handles.Add(("angleStart", Polar(Center, Number("Outer Radius") + offset, Number("Start Angle")))); handles.Add(("angleEnd", Polar(Center, Number("Outer Radius") + offset, Number("Start Angle") + Number("Angle Range")))); }
            if (Kind == "circle_caliper") { handles.Add(("height", Polar(Center, Number("Radius") + Number("Caliper Height", 20) / 2 + offset, 90))); handles.Add(("width", Center + new Vector(Number("Radius"), Number("Caliper Width", 5) / 2 + offset))); }
        }
        return handles;
    }
    public void MoveHandle(string key, Point position, double scale)
    {
        double offset = 24 / Math.Max(.0001, scale), distance = (position - Center).Length;
        if (key.StartsWith("vertex", StringComparison.Ordinal)) { var points = Points; points[int.Parse(key[6..], CultureInfo.InvariantCulture)] = position; SetPoints(points); }
        else if (key is "start" or "end") { SetPoint(key == "start" ? "Start" : "End", position); if (Kind == "arc_caliper") Set("Radius", Math.Max(Number("Radius"), (PointValue("End") - PointValue("Start")).Length / 2)); }
        else if (key is "left" or "right" or "top" or "bottom" || CornerKeys.Contains(key)) ResizeRectangle(key, position);
        else if (key == "rotate") Set("Rotation", Normalize(Angle(Center, position) + 270) - 180);
        else if (key == "radius") { Set(Kind is "ring2d" or "sector2d" ? "Outer Radius" : "Radius", Math.Max(1, distance)); if (Kind is "ring2d" or "sector2d") Set("Inner Radius", Math.Min(Number("Inner Radius"), Math.Max(0, distance - 1))); }
        else if (key == "inner") Set("Inner Radius", Math.Clamp(distance, 0, Math.Max(0, Number("Outer Radius") - 1)));
        else if (key == "angleStart") { double end = Number("Start Angle") + Number("Angle Range"); Set("Start Angle", Angle(Center, position)); Set("Angle Range", Math.Clamp(Normalize(end - Number("Start Angle")), .01, 360)); }
        else if (key == "angleEnd") Set("Angle Range", Math.Clamp(Normalize(Angle(Center, position) - Number("Start Angle")), .01, 360));
        else if (key == "arc")
        {
            var chord = PointValue("End") - PointValue("Start"); double half = chord.Length / 2;
            if (half < .001) return; var normal = new Vector(-chord.Y, chord.X); normal.Normalize();
            double bulge = Vector.Multiply(position - Center, normal), height = Math.Max(.01, Math.Abs(bulge));
            Set("Radius", (half * half + height * height) / (2 * height)); SetFlag("AntiClockWise", bulge > 0); SetFlag("Large Arc", height > half);
        }
        else if (key == "height")
        {
            double height;
            if (Kind == "line_caliper") { var direction = PointValue("End") - PointValue("Start"); if (direction.Length < .001) return; direction.Normalize(); height = Math.Abs(Vector.Multiply(position - Center, new Vector(-direction.Y, direction.X))) - offset; }
            else { var center = Kind == "arc_caliper" ? Arc().Center : Center; height = Math.Abs((position - center).Length - Number("Radius")) - offset; }
            Set("Caliper Height", Math.Max(3, height * 2));
        }
        else if (key == "width")
        {
            double width;
            if (Kind == "line_caliper") { var direction = PointValue("End") - PointValue("Start"); if (direction.Length < .001) return; direction.Normalize(); width = Math.Abs(Vector.Multiply(position - PointValue("Start"), direction)) - offset; }
            else if (Kind == "arc_caliper")
            {
                var arc = Arc(); var tangent = Polar(new Point(), 1, arc.Start + 90);
                width = Math.Abs(Vector.Multiply(position - PointValue("Start"), new Vector(tangent.X, tangent.Y))) - offset;
            }
            else width = Math.Abs(position.Y - Center.Y) - offset;
            Set("Caliper Width", Math.Max(3, width * 2));
        }
    }
}
