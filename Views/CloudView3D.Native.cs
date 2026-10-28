using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using AiVisionStudio.Algorithm;
using static AiVisionStudio.Views.NativeOverlayData;

namespace AiVisionStudio.Views;

public sealed partial class CloudView3D
{
    private readonly List<NativeOverlay3D> _nativeOverlays = new();
    public int RenderedNativeOverlayCount { get; private set; }
    public IReadOnlyList<string> UnsupportedNativeTypes { get; private set; } = Array.Empty<string>();
    public void SetNativeOverlays(IEnumerable<NativeOverlay3D> overlays)
    {
        _nativeOverlays.Clear();
        _overlayVersion++;
        if (overlays != null) _nativeOverlays.AddRange(overlays);
        InvalidateVisual();
    }

    private void DrawNativeOverlays(DrawingContext dc)
    {
        RenderedNativeOverlayCount = 0;
        var unsupported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var span = Math.Max(_x2 - _x1, Math.Max(_y2 - _y1, _z2 - _z1));
        DrawNativePointSets(dc);
        foreach (var overlay in _nativeOverlays)
        {
            var data = overlay.Values; var type = overlay.Type.ToLowerInvariant();
            try
            {
                var color = NativeOverlayData.Color(data, "#00FF00");
                var pen = new Pen(new SolidColorBrush(color), 1);
                switch (type)
                {
                    case "box": case "text3d": continue; // Already decoded to the dedicated world-space models.
                    case "pointset":
                        if (overlay.Cloud == null) { unsupported.Add("pointset (数据未加载)"); continue; }
                        break; // Point sets share a single cached bitmap/depth buffer.
                    case "point3d":
                    {
                        pen = new Pen(new SolidColorBrush(NativeOverlayData.Color(data, "red")), 1);
                        var p = NativePoint(Vector(data));
                        dc.DrawLine(pen, new Point(p.X - 3, p.Y), new Point(p.X + 3, p.Y));
                        dc.DrawLine(pen, new Point(p.X, p.Y - 3), new Point(p.X, p.Y + 3));
                        break;
                    }
                    case "line3d":
                    {
                        var p = Vector(data, "Position"); var direction = Unit(Vector(data, "Direction")) * span;
                        NativeLine(dc, p - direction, p + direction, pen); break;
                    }
                    case "segment3d":
                    {
                        var start = Vector(data, "Start"); var end = Vector(data, "End");
                        NativeLine(dc, start, end, pen);
                        var axis = end - start; var length = axis.Length;
                        if (length > 0)
                        {
                            var (u, v) = Basis(axis); var center = end - axis * .1;
                            var ring = Enumerable.Range(0, 16).Select(i => center + length * .05 * (u * Math.Cos(i * Math.PI / 8) + v * Math.Sin(i * Math.PI / 8))).ToArray();
                            for (var i = 0; i < ring.Length; i++) NativeFace(dc, new[] { end, ring[i], ring[(i + 1) % ring.Length] }, color, 1);
                        }
                        break;
                    }
                    case "plane":
                    {
                        var p = Vector(data, "Position"); var (u, v) = Basis(Vector(data, "Normal")); u *= span / 2; v *= span / 2;
                        NativeFace(dc, new[] { p - u - v, p + u - v, p + u + v, p - u + v }, NativeOverlayData.Color(data, "yellow"), .3); break;
                    }
                    case "polygon3d": case "polygon2d": case "trace3d":
                        NativePolyline(dc, Points(data, type == "polygon2d" ? 5 : 0), new Pen(new SolidColorBrush(NativeOverlayData.Color(data, "blue")), 1), type != "trace3d" || Flag(data, "Closed")); break;
                    case "circle3d": case "ring3d":
                    {
                        var center = Vector(data, "Center"); var rotation = Vector(data, "Rotation");
                        var radii = type == "ring3d" ? new[] { Number(data, "Inner Radius"), Number(data, "Outer Radius") } : new[] { Number(data, "Radius") };
                        pen = new Pen(new SolidColorBrush(NativeOverlayData.Color(data, type == "circle3d" ? "red" : "#00FF00")), 1);
                        foreach (var radius in radii) NativePolyline(dc, Circle(center, radius, rotation, type == "circle3d" ? 50 : 64), pen, true);
                        break;
                    }
                    case "sphere": DrawNativeSphere(dc, data, pen); break;
                    case "cylinder": case "cylinderbox": DrawNativeCylinder(dc, data, color, type == "cylinderbox"); break;
                    case "orientedbox": DrawNativeOrientedBox(dc, data, color); break;
                    case "window2d":
                    {
                        var a = Vector(data, "Start", 5); var b = Vector(data, "End", 5);
                        NativeFace(dc, new[] { a, new Vector3D(b.X, a.Y, 5), b, new Vector3D(a.X, b.Y, 5) }, color, .3, pen); break;
                    }
                    case "circlewindow2d": NativeFace(dc, Circle(Vector(data, "Center", 5), Number(data, "Radius"), new Vector3D(), 64), color, .3, pen); break;
                    default: unsupported.Add(overlay.Type); continue;
                }
                RenderedNativeOverlayCount++;
            }
            catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException or ArgumentException)
            { unsupported.Add(overlay.Type + " (数据不完整)"); }
        }
        UnsupportedNativeTypes = unsupported.ToArray();
        if (unsupported.Count > 0) dc.DrawText(Text("尚未显示图元: " + string.Join(", ", unsupported), 12, Colors.Orange, FontWeights.Normal), new Point(10, 8));
    }

    private Point NativePoint(Vector3D point) => Screen(Project(point.X, point.Y, point.Z));
    private void NativeLine(DrawingContext dc, Vector3D start, Vector3D end, Pen pen) => dc.DrawLine(pen, NativePoint(start), NativePoint(end));
    private void NativePolyline(DrawingContext dc, IEnumerable<Vector3D> points, Pen pen, bool closed = false)
    {
        var vertices = points.ToArray();
        var step = IsInteractionRendering ? Math.Max(1, (int)Math.Ceiling(vertices.Length / 256d)) : 1;
        var array = vertices.Where((_, i) => i % step == 0 || i == vertices.Length - 1).Select(NativePoint).ToArray(); if (array.Length < 2) return;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open()) { path.BeginFigure(array[0], false, closed); path.PolyLineTo(array.Skip(1).ToArray(), true, false); }
        geometry.Freeze(); dc.DrawGeometry(null, pen, geometry);
    }
    private void NativeFace(DrawingContext dc, IEnumerable<Vector3D> points, Color color, double opacity, Pen border = null)
    {
        var array = points.Select(NativePoint).ToArray(); if (array.Length < 3) return;
        dc.DrawGeometry(new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)Math.Round(opacity * 255), color.R, color.G, color.B)), border, Polygon(array));
    }
    private static Vector3D[] Circle(Vector3D center, double radius, Vector3D rotation, int count)
        => Enumerable.Range(0, count).Select(i => center + Euler(new Vector3D(radius * Math.Cos(i * Math.PI * 2 / count), radius * Math.Sin(i * Math.PI * 2 / count), 0), rotation)).ToArray();
    private void DrawNativeSphere(DrawingContext dc, IReadOnlyDictionary<string, object> data, Pen pen)
    {
        var center = Vector(data, "Center"); var radius = Number(data, "Radius");
        for (var longitude = 0; longitude < 360; longitude += 30)
        {
            var phi = longitude * Math.PI / 180;
            NativePolyline(dc, Enumerable.Range(0, 37).Select(i =>
            {
                var theta = (-90 + i * 5) * Math.PI / 180;
                return center + new Vector3D(radius * Math.Cos(theta) * Math.Cos(phi), -radius * Math.Cos(theta) * Math.Sin(phi), radius * Math.Sin(theta));
            }), pen);
        }
        for (var latitude = -60; latitude <= 60; latitude += 30)
        {
            var theta = latitude * Math.PI / 180;
            NativePolyline(dc, Circle(center + new Vector3D(0, 0, radius * Math.Sin(theta)), radius * Math.Cos(theta), new Vector3D(), 36), pen, true);
        }
        var corners = Corners(center.X - radius, center.Y - radius, center.Z - radius, center.X + radius, center.Y + radius, center.Z + radius);
        foreach (var (a, b) in Edges) NativeLine(dc, new Vector3D(corners[a].x, corners[a].y, corners[a].z), new Vector3D(corners[b].x, corners[b].y, corners[b].z), pen);
    }
    private void DrawNativeCylinder(DrawingContext dc, IReadOnlyDictionary<string, object> data, Color color, bool vertical)
    {
        var bottom = Vector(data, "Bottom"); var top = vertical ? bottom + new Vector3D(0, 0, Number(data, "Height")) : Vector(data, "Top");
        var radius = Number(data, "Radius"); var (u, v) = Basis(top - bottom);
        var lower = Enumerable.Range(0, 64).Select(i => bottom + radius * (u * Math.Cos(i * Math.PI / 32) + v * Math.Sin(i * Math.PI / 32))).ToArray();
        var upper = lower.Select(p => p + top - bottom).ToArray(); var pen = new Pen(new SolidColorBrush(color), 1);
        var faces = Enumerable.Range(0, 64).Select(i => new[] { lower[i], lower[(i + 1) % 64], upper[(i + 1) % 64], upper[i] }).ToList();
        faces.Add(lower.Reverse().ToArray()); faces.Add(upper);
        foreach (var face in faces.OrderBy(f => f.Average(p => Project(p.X, p.Y, p.Z).depth)))
        {
            var points = face.Select(p => Project(p.X, p.Y, p.Z)).ToArray();
            if (FrontFacing(new[] { 0, 1, 2 }, points)) NativeFace(dc, face, color, .2);
        }
        NativePolyline(dc, lower, pen, true); NativePolyline(dc, upper, pen, true);
        for (var i = 0; i < 64; i += 16) NativeLine(dc, lower[i], upper[i], pen);
    }
    private void DrawNativeOrientedBox(DrawingContext dc, IReadOnlyDictionary<string, object> data, Color color)
    {
        var center = Vector(data, "Center"); var size = Vector(data, "Size"); var rotation = Vector(data, "Rotation");
        var points = Corners(-size.X / 2, -size.Y / 2, -size.Z / 2, size.X / 2, size.Y / 2, size.Z / 2)
            .Select(p => center + Euler(new Vector3D(p.x, p.y, p.z), rotation)).ToArray();
        var projected = points.Select(p => Project(p.X, p.Y, p.Z)).ToArray();
        foreach (var face in Faces.Where(f => FrontFacing(f, projected)).OrderBy(f => f.Average(i => projected[i].depth))) NativeFace(dc, face.Select(i => points[i]), color, .3);
        var pen = new Pen(new SolidColorBrush(color), 1);
        foreach (var (a, b) in Edges) NativeLine(dc, points[a], points[b], pen);
    }
}
