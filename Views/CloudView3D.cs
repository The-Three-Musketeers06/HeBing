using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AiVisionStudio.Algorithm;

namespace AiVisionStudio.Views;

/// <summary>Orthographic point-cloud viewport with world-space result overlays.</summary>
public sealed partial class CloudView3D : FrameworkElement
{
    public sealed record CameraState(double Yaw, double Pitch, double Zoom, double PanX, double PanY, double FitYaw = 0, double FitPitch = 0)
    {
        public CameraState(double yaw, double pitch, double zoom, double panX, double panY) : this(yaw, pitch, zoom, panX, panY, 0, 0) { }
    }
    private PointCloud _cloud;
    private readonly List<Text3D> _texts = new();
    private readonly List<Box3> _boxes = new();
    private double _yaw, _pitch, _zoom = 1;
    private double _fitYaw, _fitPitch;
    private double _panX, _panY, _yaw0, _pitch0, _panX0, _panY0;
    private double _cx, _cy, _cz, _scale, _ox, _oy, _cosY, _sinY, _cosP, _sinP;
    private double _x1, _y1, _z1, _x2, _y2, _z2, _colorMin, _colorMax;
    private double _gridX1, _gridY1, _gridZ1, _gridX2, _gridY2, _gridZ2;
    private bool _rotating, _panning;
    private Point _dragStart;
    private WriteableBitmap _bitmap;
    private double _pixelsPerDip = 1;
    private readonly Dictionary<(string Text, double Size, Color Color, FontWeight Weight, double Dpi), FormattedText> _textCache = new();
    private readonly List<Rect> _axisLabelBounds = new();
    private static readonly Typeface LabelTypeface = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    public double RenderedMaxLabelSize { get; private set; }
    public int SuppressedPointGlyphCount { get; private set; }
    private static readonly int[][] Faces = { new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 }, new[] { 1, 2, 6, 5 }, new[] { 2, 3, 7, 6 }, new[] { 3, 0, 4, 7 } };
    private static readonly (int, int)[] Edges = { (0, 1), (1, 2), (2, 3), (3, 0), (4, 5), (5, 6), (6, 7), (7, 4), (0, 4), (1, 5), (2, 6), (3, 7) };

    public CloudView3D()
    {
        ClipToBounds = true; Focusable = true; Cursor = Cursors.Hand;
        LostMouseCapture += (_, _) => { _rotating = _panning = false; InvalidateVisual(); };
    }

    public bool HasCloud => _cloud != null && _cloud.Count > 0 && (_cloud.DisplayIndices == null || _cloud.DisplayIndices.Length > 0);
    public int RenderedPointCount { get; private set; }
    public int RenderedBoxCount { get; private set; }
    public int RenderedLabelCount { get; private set; }
    public CameraState CaptureCamera() => new(_yaw, _pitch, _zoom, _panX, _panY, _fitYaw, _fitPitch);
    public void RestoreCamera(CameraState state)
    {
        if (state == null || !double.IsFinite(state.Yaw) || !double.IsFinite(state.Pitch)
            || !double.IsFinite(state.Zoom) || !double.IsFinite(state.PanX) || !double.IsFinite(state.PanY)) return;
        _yaw = NormalizeAngle(state.Yaw); _pitch = NormalizeAngle(state.Pitch);
        _fitYaw = double.IsFinite(state.FitYaw) ? state.FitYaw : 0; _fitPitch = double.IsFinite(state.FitPitch) ? state.FitPitch : 0;
        _zoom = Math.Clamp(state.Zoom, 0.05, 50); _panX = state.PanX; _panY = state.PanY;
        InvalidateVisual();
    }

    public void Show(PointCloud cloud, IEnumerable<Text3D> texts = null, IEnumerable<Box3> boxes = null)
    {
        _cloud = cloud;
        InvalidateCloudRaster();
        _orientedCorners = null;
        ClearMeasurement();
        ReadRanges();
        SetOverlays(texts, boxes);
    }

    public void SetOverlays(IEnumerable<Text3D> texts, IEnumerable<Box3> boxes = null)
    {
        _texts.Clear(); _boxes.Clear();
        if (texts != null) _texts.AddRange(texts);
        if (boxes != null) _boxes.AddRange(boxes);
        InvalidateVisual();
    }

    private void ReadRanges()
    {
        _x1 = _y1 = _z1 = _colorMin = double.PositiveInfinity;
        _x2 = _y2 = _z2 = _colorMax = double.NegativeInfinity;
        if (_cloud == null) return;
        var scalars = _cloud.DisplayScalars?.Length == _cloud.Count ? _cloud.DisplayScalars : null;
        var visible = _cloud.DisplayIndices;
        for (var position = 0; position < (visible?.Length ?? _cloud.Count); position++)
        {
            var i = visible == null ? position : visible[position];
            if (i < 0 || i >= _cloud.Count) continue;
            var x = _cloud.X[i]; var y = _cloud.Y[i]; var z = _cloud.Z[i];
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) continue;
            _x1 = Math.Min(_x1, x); _x2 = Math.Max(_x2, x);
            _y1 = Math.Min(_y1, y); _y2 = Math.Max(_y2, y);
            _z1 = Math.Min(_z1, z); _z2 = Math.Max(_z2, z);
            var scalar = scalars == null ? z : scalars[i];
            if (!float.IsFinite(scalar)) continue;
            _colorMin = Math.Min(_colorMin, scalar); _colorMax = Math.Max(_colorMax, scalar);
        }
        if (!double.IsFinite(_x1)) { _x1 = _y1 = _z1 = 0; _x2 = _y2 = _z2 = 1; }
        if (_cloud.DisplayMin is double min && double.IsFinite(min)) _colorMin = min;
        if (_cloud.DisplayMax is double max && double.IsFinite(max)) _colorMax = max;
        if (!double.IsFinite(_colorMin) || !double.IsFinite(_colorMax)) { _colorMin = 0; _colorMax = 1; }
        if (_colorMax <= _colorMin) { _colorMin -= 0.5; _colorMax += 0.5; }
        _cx = (_x1 + _x2) / 2; _cy = (_y1 + _y2) / 2; _cz = (_z1 + _z2) / 2;
        // Original _Jt.changeAxisPosition expands the start first, then uses it to expand the end.
        _gridX1 = AxisStart(_x1, _x2); _gridX2 = AxisEnd(_gridX1, _x2);
        _gridY1 = AxisStart(_y1, _y2); _gridY2 = AxisEnd(_gridY1, _y2);
        _gridZ1 = AxisStart(_z1, _z2); _gridZ2 = AxisEnd(_gridZ1, _z2);
    }

    public void Reset()
    {
        _cloud = null; _texts.Clear(); _boxes.Clear(); _bitmap = null;
        ResetRasterBuffers();
        _nativeOverlays.Clear(); UnsupportedNativeTypes = Array.Empty<string>(); RenderedNativeOverlayCount = 0;
        RenderedPointCount = RenderedBoxCount = RenderedLabelCount = 0;
        RenderedMaxLabelSize = 0; SuppressedPointGlyphCount = 0;
        _rotating = _panning = false; ReleaseMouseCapture(); InvalidateVisual();
    }

    public void ResetView()
    {
        _yaw = _pitch = _fitYaw = _fitPitch = 0; _zoom = 1; _panX = _panY = 0;
        _rotating = _panning = false; ReleaseMouseCapture(); InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!HasCloud) return;
        if (TryCompassView(e.GetPosition(this))) { e.Handled = true; return; }
        if (DistanceMode) { PickDistanceAt(e.GetPosition(this)); e.Handled=true; return; }
        if (e.ClickCount == 2) { ResetView(); e.Handled = true; return; }
        _panning = PanWithLeftButton; _rotating = !PanWithLeftButton;
        _panX0=_panX;_panY0=_panY;
        _dragStart = e.GetPosition(this); _yaw0 = _yaw; _pitch0 = _pitch;
        CaptureMouse(); e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (!HasCloud) return;
        _panning = true; _dragStart = e.GetPosition(this); _panX0 = _panX; _panY0 = _panY;
        CaptureMouse(); e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e); _rotating = _panning = false; ReleaseMouseCapture(); InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!HasCloud) return;
        var point = e.GetPosition(this);
        if (_rotating)
        {
            _yaw = NormalizeAngle(_yaw0 - (point.X - _dragStart.X) * 0.007);
            _pitch = NormalizeAngle(_pitch0 + (point.Y - _dragStart.Y) * 0.007);
            InvalidateVisual();
        }
        else if (_panning)
        {
            _panX = _panX0 + (point.X - _dragStart.X) / Math.Max(1, ActualWidth);
            _panY = _panY0 + (point.Y - _dragStart.Y) / Math.Max(1, ActualHeight);
            InvalidateVisual();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!HasCloud) return;
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.12 : 1 / 1.12), 0.05, 50);
        MarkWheelInteraction();
        InvalidateVisual(); e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth; var h = ActualHeight;
        if (w < 2 || h < 2) return;
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var surface=!HasCloud&&TryFindResource("RowBg") is Brush emptyBrush?emptyBrush:new SolidColorBrush(Color.FromRgb(35,35,36));
        dc.DrawRectangle(surface, null, new Rect(0, 0, w, h));
        RenderedPointCount = RenderedBoxCount = RenderedLabelCount = 0;
        RenderedMaxLabelSize = 0; SuppressedPointGlyphCount = 0;
        if (!HasCloud) { if(AxesVisible)DrawCompass(dc,w,h);return; }
        PrepareProjection(w, h);
        DrawCloud(dc, w, h);
        if(GridVisible)DrawGrid(dc);
        if(ResultsVisible){DrawBoxes(dc);DrawNativeOverlays(dc);DrawTexts(dc);}
        if(AxesVisible){DrawAxes(dc);DrawCompass(dc,w,h);}
        DrawViewBounds(dc);DrawMeasurement(dc);
    }

    private (double x, double y, double depth) Rotate(double x, double y, double z)
    {
        var xr = x * _cosY - y * _sinY;
        var yr = x * _sinY + y * _cosY;
        return (xr, yr * _cosP + z * _sinP, -yr * _sinP + z * _cosP);
    }

    private void PrepareProjection(double w, double h)
    {
        _cosY = SnapTrig(Math.Cos(_yaw)); _sinY = SnapTrig(Math.Sin(_yaw)); _cosP = SnapTrig(Math.Cos(_pitch)); _sinP = SnapTrig(Math.Sin(_pitch));
        // Match JJt.autoCenter: the orthographic frustum contains the XY bounds with factor 1.2.
        // Rotation changes the projected geometry, not the camera's frustum or scale.
        var cy = Math.Cos(_fitYaw); var sy = Math.Sin(_fitYaw); var cp = Math.Cos(_fitPitch); var sp = Math.Sin(_fitPitch);
        var dx = _x2 - _x1; var dy = _y2 - _y1; var dz = _z2 - _z1;
        var span = Math.Max(1e-6, Math.Max(dx, Math.Max(dy, dz)));
        var fitWidth = Math.Abs(cy) * dx + Math.Abs(sy) * dy;
        var fitHeight = Math.Abs(sy * cp) * dx + Math.Abs(cy * cp) * dy + Math.Abs(sp) * dz;
        _scale = Math.Min(w / Math.Max(span * .02, fitWidth), h / Math.Max(span * .02, fitHeight)) / 1.3 * _zoom;
        _ox = w * (0.5 + _panX);
        _oy = h * (0.5 + _panY);
    }

    private (double x, double y, double depth) Project(double x, double y, double z)
    {
        var rotated = Rotate(x - _cx, y - _cy, z - _cz);
        var distance = Math.Max(1e-6, Math.Max(_x2-_x1,Math.Max(_y2-_y1,_z2-_z1))) * 3;
        var factor = Perspective ? distance / Math.Max(distance*.05,distance-rotated.depth) : 1;
        return (_ox + rotated.x * _scale * factor, _oy - rotated.y * _scale * factor, rotated.depth);
    }

    private static Point Screen((double x, double y, double depth) p) => new(p.x, p.y);

    private void DrawGrid(DrawingContext dc)
    {
        var major = new Pen(Brushes.White, 1);
        var minor = new Pen(new SolidColorBrush(Color.FromRgb(108, 109, 109)), 1);
        // Draw the far faces of the bounding grid. All six faces participate when
        // the camera crosses the object; the near faces do not obscure detection.
        var corners = Corners(_gridX1, _gridY1, _gridZ1, _gridX2, _gridY2, _gridZ2);
        var normals = new[] { (0d, 0d, -1d), (0d, 0d, 1d), (0d, -1d, 0d), (1d, 0d, 0d), (0d, 1d, 0d), (-1d, 0d, 0d) };
        for (int f = 0; f < Faces.Length; f++)
        {
            var normal = normals[f]; if (Rotate(normal.Item1, normal.Item2, normal.Item3).depth >= -1e-7) continue;
            var face = Faces[f]; var a = corners[face[0]]; var b = corners[face[1]]; var c = corners[face[2]]; var d = corners[face[3]];
            for (int division = 0; division <= 25; division++)
            {
                double t = division / 25d; var pen = division % 5 == 0 ? major : minor;
                Point Between((double x,double y,double z) p, (double x,double y,double z) q)
                    => Screen(Project(p.x + (q.x - p.x) * t, p.y + (q.y - p.y) * t, p.z + (q.z - p.z) * t));
                dc.DrawLine(pen, Between(a, b), Between(d, c));
                dc.DrawLine(pen, Between(a, d), Between(b, c));
            }
        }
    }

    private void DrawAxes(DrawingContext dc)
    {
        _axisLabelBounds.Clear();
        var origin = Project(_gridX1, _gridY1, _gridZ1);
        dc.DrawLine(new Pen(Brushes.Red, 1), Screen(origin), Screen(Project(_gridX2, _gridY1, _gridZ1)));
        dc.DrawLine(new Pen(Brushes.Green, 1), Screen(origin), Screen(Project(_gridX1, _gridY2, _gridZ1)));
        dc.DrawLine(new Pen(Brushes.Blue, 1), Screen(origin), Screen(Project(_gridX1, _gridY1, _gridZ2)));
        var margin = Math.Min(_gridX2 - _gridX1, _gridY2 - _gridY1) / 25 * 2;
        foreach (var x in AxisTicks(_gridX1, _gridX2)) CenterLabel(dc, AxisNumber(x), Project(x, _gridY2 + margin, _gridZ1), Colors.White);
        foreach (var y in AxisTicks(_gridY1, _gridY2)) CenterLabel(dc, AxisNumber(y), Project(_gridX2 + margin, y, _gridZ1), Colors.White);
        foreach (var z in AxisTicks(_gridZ1, _gridZ2)) CenterLabel(dc, AxisNumber(z), Project(_gridX1 - margin, _gridY1 - margin, z), Colors.White);
        CenterLabel(dc, "X(mm)", Project(_gridX2 + margin * 2, _gridY1 - margin, _gridZ1), Colors.Red);
        CenterLabel(dc, "Y(mm)", Project(_gridX1 - margin, _gridY2 + margin * 2, _gridZ1), Colors.Green);
        if (_gridZ2 != _gridZ1) CenterLabel(dc, "Z(mm)", Project(_gridX1, _gridY1, _gridZ2 + margin), Colors.Blue);
    }

    private void DrawBoxes(DrawingContext dc)
    {
        foreach (var box in _boxes)
        {
            var corners = Corners(box.X1, box.Y1, box.Z1, box.X2, box.Y2, box.Z2);
            if (corners.Any(p => !double.IsFinite(p.x) || !double.IsFinite(p.y) || !double.IsFinite(p.z))) continue;
            var points = corners.Select(p => Project(p.x, p.y, p.z)).ToArray();
            var color = ParseColor(box.Color);
            if (box.FillOpacity > 0)
            {
                var fill = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(box.FillOpacity, 0, 1) * 255), color.R, color.G, color.B));
                foreach (var face in Faces.Where(f => FrontFacing(f, points)).OrderBy(f => f.Average(i => points[i].depth)))
                    dc.DrawGeometry(fill, null, Polygon(face.Select(i => Screen(points[i]))));
            }
            var line = new Pen(new SolidColorBrush(color), 1.25);
            foreach (var (a, b) in Edges) dc.DrawLine(line, Screen(points[a]), Screen(points[b]));
            RenderedBoxCount++;
            if (!string.IsNullOrEmpty(box.Label))
            {
                var at = Project((box.X1 + box.X2) / 2, (box.Y1 + box.Y2) / 2, Math.Max(box.Z1, box.Z2));
                var label = Text(box.Label, 14, color, FontWeights.Bold);
                dc.DrawText(label, new Point(at.x - label.Width / 2, at.y - label.Height - 3));
                RenderedLabelCount++;
            }
        }
    }

    private void DrawTexts(DrawingContext dc)
    {
        var occupied = new List<Rect>();
        foreach (var item in _texts)
        {
            if (string.IsNullOrEmpty(item.Text) || !double.IsFinite(item.X) || !double.IsFinite(item.Y) || !double.IsFinite(item.Z)) continue;
            if (item.Text.Trim() is "●" or "•" or "⬤") { SuppressedPointGlyphCount++; continue; }
            if (IsInteractionRendering && RenderedLabelCount >= 64) break;
            var p = Project(item.X, item.Y, item.Z);
            if (p.x < -100 || p.y < -100 || p.x > ActualWidth || p.y > ActualHeight + 40) continue;
            // Result labels are screen-space annotations, not world-space geometry.
            var fontSize = Math.Clamp(item.Size > 0 ? item.Size * .5 : 13, 10, 20);
            RenderedMaxLabelSize = Math.Max(RenderedMaxLabelSize, fontSize);
            var text = Text(item.Text, fontSize, ParseColor(item.Color), FontWeights.Normal);
            var at = new Point(Math.Clamp(p.x, 2, Math.Max(2, ActualWidth - text.Width - 2)),
                Math.Clamp(p.y - text.Height, 2, Math.Max(2, ActualHeight - text.Height - 2)));
            var rect = new Rect(at, new Size(text.Width + 4, text.Height + 3));
            if (occupied.Any(r => r.IntersectsWith(rect)))
            {
                bool placed = false;
                for (int row = 1; row <= 16 && !placed; row++)
                    foreach (int sign in new[] { -1, 1 })
                    {
                        var shifted = new Rect(at.X, at.Y + sign * row * (text.Height + 3), rect.Width, rect.Height);
                        if (shifted.Top < 2 || shifted.Bottom > ActualHeight - 2 || occupied.Any(r => r.IntersectsWith(shifted))) continue;
                        rect = shifted; placed = true; break;
                    }
                if (!placed) continue;
                var color = ParseColor(item.Color);
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(140, color.R, color.G, color.B)), .7),
                    new Point(p.x, p.y), new Point(rect.X, rect.Y + text.Height));
            }
            occupied.Add(rect);
            if (item.SizeIsPixels) dc.DrawText(Text(item.Text, fontSize, Colors.Black, FontWeights.Normal), new Point(rect.X + 1, rect.Y + 1));
            dc.DrawText(text, rect.TopLeft);
            RenderedLabelCount++;
        }
    }

    private void DrawCompass(DrawingContext dc, double width, double height)
    {
        _cosY = SnapTrig(Math.Cos(_yaw)); _sinY = SnapTrig(Math.Sin(_yaw)); _cosP = SnapTrig(Math.Cos(_pitch)); _sinP = SnapTrig(Math.Sin(_pitch));
        var origin = new Point(width - 55, height - 55);
        foreach (var marker in CompassMarkers(width, height))
        {
            var pen = new Pen(new SolidColorBrush(marker.Color), marker.Positive ? 1.3 : .8);
            dc.DrawLine(pen, origin, marker.Position);
            var label = Text(marker.Label, 10, marker.Color, FontWeights.Normal);
            dc.DrawText(label, new Point(marker.Position.X - label.Width / 2, marker.Position.Y - label.Height / 2));
        }
    }

    private static (double x, double y, double z)[] Corners(double x1, double y1, double z1, double x2, double y2, double z2)
        => new[] { (x1, y1, z1), (x2, y1, z1), (x2, y2, z1), (x1, y2, z1), (x1, y1, z2), (x2, y1, z2), (x2, y2, z2), (x1, y2, z2) };

    private static bool FrontFacing(int[] face, (double x, double y, double depth)[] points)
    {
        var a = points[face[0]]; var b = points[face[1]]; var c = points[face[2]];
        return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) < -1e-8;
    }

    private static Geometry Polygon(IEnumerable<Point> vertices)
    {
        var points = vertices.ToArray();
        var geometry = new StreamGeometry();
        using (var path = geometry.Open()) { path.BeginFigure(points[0], true, true); path.PolyLineTo(points.Skip(1).ToArray(), true, false); }
        geometry.Freeze(); return geometry;
    }

    private static double AxisStart(double min, double max) => Math.Round(min - (max - min) * .05, 3);
    private static double AxisEnd(double start, double max) => Math.Round(max * 1.05 - start * .05, 3);

    private static IEnumerable<double> AxisTicks(double min, double max)
    {
        var step = (max - min) / 25 - .001;
        if (step <= 0) { yield return min; yield return max; yield break; }
        for (var i = 0; i <= 25; i += 5) yield return min + i * step;
    }

    private static string AxisNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private FormattedText Text(string value, double size, Color color, FontWeight weight)
    {
        var key = (value, size, color, weight, _pixelsPerDip);
        if (_textCache.TryGetValue(key, out var text)) return text;
        if (_textCache.Count >= 2048) _textCache.Clear();
        var brush = new SolidColorBrush(color); brush.Freeze();
        text = new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, size, brush, _pixelsPerDip);
        _textCache[key] = text; return text;
    }

    private void Label(DrawingContext dc, string value, (double x, double y, double depth) position, Color color, double size, double dx, double dy, bool right = false)
    {
        var text = Text(value, size, color, FontWeights.Normal);
        dc.DrawText(text, new Point(position.x + dx - (right ? text.Width : 0), position.y + dy));
    }

    private void CenterLabel(DrawingContext dc, string value, (double x, double y, double depth) position, Color color)
    {
        var text = Text(value, 10.5, color, FontWeights.Normal);
        var rect = new Rect(position.x - text.Width / 2, position.y - text.Height / 2, text.Width, text.Height);
        if (rect.Left < 1 || rect.Top < 1 || rect.Right > ActualWidth - 1 || rect.Bottom > ActualHeight - 1) return;
        var padded = rect; padded.Inflate(3, 2);
        if (_axisLabelBounds.Any(r => r.IntersectsWith(padded))) return;
        _axisLabelBounds.Add(padded); dc.DrawText(text, rect.TopLeft);
    }

    private static Color ParseColor(string name)
    {
        try { return NativeOverlayData.ParseColor(string.IsNullOrWhiteSpace(name) ? "#B8EC26" : name); }
        catch (Exception error) when (error is FormatException or NotSupportedException or ArgumentException) { return Color.FromRgb(184, 236, 38); }
    }

    private static Color HeightColor(double value, uint? sourceRgb = null)
    {
        // Original worker kt()/yJt() stores linear RGB, then Three's output is sRGB.
        var index = Math.Clamp((int)Math.Floor(value * 1275), 0, 1275);
        var segment = index / 255; var fraction = (index % 255) / 255.0;
        var (r, g, b) = segment switch
        {
            0 => (1 - fraction, 0.0, 1.0), 1 => (0.0, fraction, 1.0),
            2 => (0.0, 1.0, 1 - fraction), 3 => (fraction, 1.0, 0.0),
            _ => (1.0, 1 - fraction, 0.0)
        };
        if (sourceRgb is uint rgb)
        {
            r = (r + Linear((byte)(rgb >> 16))) / 2;
            g = (g + Linear((byte)(rgb >> 8))) / 2;
            b = (b + Linear((byte)rgb)) / 2;
        }
        return Color.FromRgb(Srgb(r), Srgb(g), Srgb(b));
    }

    private static byte Srgb(double linear) => (byte)Math.Round(255 * (linear <= .0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - .055));
    private static double Linear(byte srgb) { var value = srgb / 255.0; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
}
