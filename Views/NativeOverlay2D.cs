using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AiVisionStudio.Algorithm;
using static AiVisionStudio.Views.NativeOverlayData;

namespace AiVisionStudio.Views;

/// <summary>Native typed 2D overlays in image coordinates, sharing the image's pan/zoom transform.</summary>
public sealed class NativeOverlay2D : FrameworkElement
{
    private readonly List<NativeOverlay3D> _overlays = new();
    private MatrixTransform _imageTransform;
    private double _imageWidth, _imageHeight;
    private DrawingGroup _drawing;
    private double _drawingScale = double.NaN;
    private bool _drawingDirty = true;
    private readonly Dictionary<NativeOverlay3D, Geometry> _paths = new();
    private double Scale => Math.Max(.0001, Math.Sqrt(Math.Pow(_imageTransform?.Matrix.M11 ?? 1, 2) + Math.Pow(_imageTransform?.Matrix.M12 ?? 0, 2)));
    public int RenderedOverlayCount { get; private set; }
    public IReadOnlyList<string> UnsupportedTypes { get; private set; } = Array.Empty<string>();
    public MatrixTransform ImageTransform
    {
        get => _imageTransform;
        set
        {
            if (_imageTransform != null) _imageTransform.Changed -= TransformChanged;
            _imageTransform = value;
            if (_imageTransform != null) _imageTransform.Changed += TransformChanged;
            if (_drawing != null) _drawing.Transform = value ?? Transform.Identity;
            _drawingDirty = true;
            InvalidateVisual();
        }
    }
    public NativeOverlay2D() { IsHitTestVisible = false; ClipToBounds = true; }
    private void TransformChanged(object sender, EventArgs args)
    {
        if (Math.Abs(Scale - _drawingScale) < .0000001) return;
        _drawingDirty = true; InvalidateVisual();
    }
    public void SetOverlays(IEnumerable<NativeOverlay3D> overlays, double imageWidth, double imageHeight)
    {
        _overlays.Clear(); if (overlays != null) _overlays.AddRange(overlays);
        _imageWidth = imageWidth; _imageHeight = imageHeight;
        _paths.Clear(); _drawingDirty = true;
        RenderedOverlayCount = 0; UnsupportedTypes = Array.Empty<string>(); InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_drawingDirty || _drawing == null)
        {
            _drawingScale = Scale;
            _drawing = new DrawingGroup { Transform = _imageTransform ?? Transform.Identity };
            using (var content = _drawing.Open()) DrawOverlays(content, _drawingScale);
            _drawingDirty = false;
        }
        dc.DrawDrawing(_drawing);
        if (UnsupportedTypes.Count > 0)
        {
            var message = new FormattedText("尚未显示图元: " + string.Join(", ", UnsupportedTypes), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, Brushes.Orange, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(message, new Point(10, 8));
        }
    }
    private void DrawOverlays(DrawingContext dc, double scale)
    {
        RenderedOverlayCount = 0;
        if (_overlays.Count == 0) return;
        var unsupported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var overlay in _overlays)
        {
            try
            {
                var data = overlay.Values; var type = overlay.Type.ToLowerInvariant();
                var color = NativeOverlayData.Color(data); var brush = new SolidColorBrush(color);
                var thickness = NativeOverlayData.Number(data, "_thickness", 1); if (thickness < 0) thickness = 1;
                var pen = new Pen(brush, thickness / scale);
                switch (type)
                {
                    case "origin2d":
                    {
                        var p = Point(data);
                        var radius = Math.Max(1, NativeOverlayData.Number(data, "_size", 12)) / scale;
                        var angle = NativeOverlayData.Number(data, "Angle") * Math.PI / 180;
                        var dx = radius * Math.Cos(angle); var dy = radius * Math.Sin(angle);
                        dc.DrawLine(pen, new Point(p.X - dx, p.Y - dy), new Point(p.X + dx, p.Y + dy));
                        dc.DrawLine(pen, new Point(p.X + dy, p.Y - dx), new Point(p.X - dy, p.Y + dx));
                        break;
                    }
                    case "point2d":
                    {
                        var p = Point(data); var radius = NativeOverlayData.Number(data, "_size", 10); if (radius <= 0) radius = 10;
                        if (data.TryGetValue("_marker", out var marker) && Equals(marker, "cross"))
                        {
                            radius /= scale;
                            dc.DrawLine(pen, new Point(p.X-radius,p.Y), new Point(p.X+radius,p.Y));
                            dc.DrawLine(pen, new Point(p.X,p.Y-radius), new Point(p.X,p.Y+radius));
                            break;
                        }
                        var inner = radius / 20; radius /= Math.Max(1, scale); inner /= Math.Max(1, scale);
                        var points = Enumerable.Range(0, 8).Select(i => new Point(p.X + (i % 2 == 0 ? radius : inner) * Math.Cos(i * Math.PI / 4 + Math.PI / 4), p.Y + (i % 2 == 0 ? radius : inner) * Math.Sin(i * Math.PI / 4 + Math.PI / 4))).ToArray();
                        var pointColor = new SolidColorBrush(NativeOverlayData.Color(data, "#1196DB"));
                        dc.DrawGeometry(pointColor, new Pen(pointColor, thickness / scale), Path(points, true)); break;
                    }
                    case "segment2d": dc.DrawLine(pen, Point(data, "Start"), Point(data, "End")); break;
                    case "line2d":
                    {
                        var p = Point(data, "Position"); var direction = Point(data, "Direction");
                        var length = Math.Sqrt(_imageWidth * _imageWidth + _imageHeight * _imageHeight);
                        dc.PushClip(new RectangleGeometry(new Rect(0, 0, Math.Max(1, _imageWidth), Math.Max(1, _imageHeight))));
                        dc.DrawLine(pen, new Point(p.X - direction.X * length, p.Y - direction.Y * length), new Point(p.X + direction.X * length, p.Y + direction.Y * length)); dc.Pop(); break;
                    }
                    case "window2d":
                    {
                        var a = Point(data, "Start"); var b = Point(data, "End");
                        dc.PushOpacity(.3); dc.DrawRectangle(brush, pen, new Rect(a, b)); dc.Pop(); break;
                    }
                    case "rect2d":
                    {
                        var center = Point(data, "Center"); var size = Group(data, "Size"); var width = NativeOverlayData.Number(size, "Width"); var height = NativeOverlayData.Number(size, "Height");
                        dc.PushTransform(new RotateTransform(NativeOverlayData.Number(data, "Rotation", 0), center.X, center.Y));
                        dc.DrawRectangle(null, pen, new Rect(center.X - width / 2, center.Y - height / 2, width, height)); dc.Pop(); break;
                    }
                    case "circle2d": case "circlewindow2d":
                    {
                        var center = Point(data, "Center"); var radius = NativeOverlayData.Number(data, "Radius");
                        var filled = type == "circlewindow2d"; if (filled) dc.PushOpacity(.3);
                        dc.DrawEllipse(filled ? brush : null, pen, center, radius, radius); if (filled) dc.Pop(); break;
                    }
                    case "ellipse2d":
                    {
                        var center = Point(data, "Center"); dc.PushTransform(new RotateTransform(NativeOverlayData.Number(data, "Rotation", 0), center.X, center.Y));
                        dc.DrawEllipse(null, pen, center, NativeOverlayData.Number(data, "Long Axis") / 2, NativeOverlayData.Number(data, "Short Axis") / 2); dc.Pop(); break;
                    }
                    case "ring2d":
                        foreach (var name in new[] { "Inner Radius", "Outer Radius" }) dc.DrawEllipse(null, pen, Point(data, "Center"), NativeOverlayData.Number(data, name), NativeOverlayData.Number(data, name)); break;
                    case "sector2d": DrawSector(dc, data, pen); break;
                    case "polygon2d": case "trace2d":
                    {
                        if (!_paths.TryGetValue(overlay, out var geometry))
                        {
                            var points = Points(data).Select(p => new Point(p.X, p.Y)).ToArray();
                            if (points.Length < 2) throw new FormatException("Polygon needs two points");
                            geometry = Path(points, type == "polygon2d" || Flag(data, "Closed"), NativeOverlayData.Number(data, "Smoothing Coefficient", 0));
                            _paths.Add(overlay, geometry);
                        }
                        Brush fill = null;
                        if (Flag(data, "_fill")) { var fillColor = NativeOverlayData.Color(data, "orange", "_fill_color"); fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(77, fillColor.R, fillColor.G, fillColor.B)); }
                        dc.DrawGeometry(fill, pen, geometry); break;
                    }
                    case "text2d":
                    {
                        var value = NativeOverlayData.Text(data).Replace("\t", "    ");
                        var size = NativeOverlayData.Number(data, "_size", 24); if (size <= 0) size = 24;
                        var bold = data.TryGetValue("_thickness", out var nativeThickness) && Convert.ToString(nativeThickness, CultureInfo.InvariantCulture) == "1";
                        var typeface = new Typeface(new FontFamily("Microsoft YaHei"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                        var text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                        dc.DrawText(text, Point(data, "Position")); break;
                    }
                    default: unsupported.Add(overlay.Type); continue;
                }
                RenderedOverlayCount++;
            }
            catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException or ArgumentException)
            { unsupported.Add(overlay.Type + " (数据不完整)"); }
        }
        UnsupportedTypes = unsupported.ToArray();
    }
    private static Point Point(IReadOnlyDictionary<string, object> data, string key = null)
    { var p = Vector(data, key); return new Point(p.X, p.Y); }
    private static Geometry Path(Point[] points, bool closed, double tension = 0)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(points[0], closed, closed);
            if (tension <= 0 || points.Length < 3) path.PolyLineTo(points.Skip(1).ToArray(), true, false);
            else
            {
                // Konva tension controls use distance-weighted incoming/outgoing tangents.
                (Point In, Point Out) Control(int i)
                {
                    var previous = points[i == 0 ? closed ? points.Length - 1 : 0 : i - 1];
                    var current = points[i]; var next = points[i + 1 == points.Length ? closed ? 0 : i : i + 1];
                    var before = (current - previous).Length; var after = (next - current).Length;
                    var sum = before + after; var direction = next - previous;
                    return sum < 1e-12 ? (current, current) : (current - direction * (tension * before / sum), current + direction * (tension * after / sum));
                }
                for (var i = 0; i < points.Length - (closed ? 0 : 1); i++)
                { var next = (i + 1) % points.Length; path.BezierTo(Control(i).Out, Control(next).In, points[next], true, true); }
            }
        }
        geometry.Freeze(); return geometry;
    }
    private static void DrawSector(DrawingContext dc, IReadOnlyDictionary<string, object> data, Pen pen)
    {
        var center = Point(data, "Center"); var inner = NativeOverlayData.Number(data, "Inner Radius"); var outer = NativeOverlayData.Number(data, "Outer Radius");
        var start = NativeOverlayData.Number(data, "Start Angle") * Math.PI / 180; var range = NativeOverlayData.Number(data, "Angle Range") * Math.PI / 180;
        var count = Math.Max(2, (int)Math.Ceiling(Math.Abs(range) * 32));
        Point At(double radius, int i) => new(center.X + radius * Math.Cos(start + range * i / count), center.Y + radius * Math.Sin(start + range * i / count));
        var points = Enumerable.Range(0, count + 1).Select(i => At(outer, i)).Concat(Enumerable.Range(0, count + 1).Reverse().Select(i => At(inner, i))).ToArray();
        dc.DrawGeometry(null, pen, Path(points, true));
    }
}
