using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AiVisionStudio.Algorithm;
using static AiVisionStudio.Views.NativeOverlayData;

namespace AiVisionStudio.Views;

/// <summary>Native X/Z profile with metric coordinates and source-ordered measurements.</summary>
public sealed class ProfileView : FrameworkElement
{
    private ProfileData _profile;
    private NativeOverlay3D[] _overlays = [];
    private Matrix _view = Matrix.Identity;
    private Point? _drag;
    private bool _moved;
    public bool HasProfile => _profile?.Count > 0;
    public int RenderedOverlayCount { get; private set; }
    public IReadOnlyList<string> UnsupportedTypes { get; private set; } = Array.Empty<string>();
    public ProfileView()
    {
        ClipToBounds = true;
        SizeChanged += (_, _) => { if (!_moved) Fit(); };
        MouseWheel += (_, e) =>
        {
            var point = e.GetPosition(this); var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
            if (Math.Abs(_view.M11 * factor) is < .00001 or > 10000000) return;
            _view.ScaleAt(factor, factor, point.X, point.Y); _moved = true; InvalidateVisual(); e.Handled = true;
        };
        MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { Fit(); return; } _drag = e.GetPosition(this); CaptureMouse(); e.Handled = true; };
        MouseLeftButtonUp += (_, e) => { _drag = null; ReleaseMouseCapture(); e.Handled = true; };
        LostMouseCapture += (_, _) => _drag = null;
        MouseMove += (_, e) =>
        {
            if (!_drag.HasValue || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(this); var delta = point - _drag.Value; _drag = point;
            _view.Translate(delta.X, delta.Y); _moved = true; InvalidateVisual(); e.Handled = true;
        };
    }
    public void Show(ProfileData profile,bool preserveView = false)
    {
        bool keepView = preserveView && HasProfile;

        _profile = profile;

        if (keepView)
        {
            InvalidateVisual();
            return;
        }

        _moved = false;
        Fit();
    }
    public void SetOverlays(IEnumerable<NativeOverlay3D> overlays)
    { _overlays = overlays?.ToArray() ?? []; InvalidateVisual(); }
    public void Fit()
    {
        if (!HasProfile || ActualWidth <= 1 || ActualHeight <= 1) { InvalidateVisual(); return; }
        var xmin = (double)_profile.X.Min(); var xmax = (double)_profile.X.Max();
        var zmin = (double)_profile.Z.Min(); var zmax = (double)_profile.Z.Max();
        var scale = Math.Min(Math.Max(1, ActualWidth - 110) / Math.Max(.000001, xmax - xmin), Math.Max(1, ActualHeight - 90) / Math.Max(.000001, zmax - zmin));
        _view = new Matrix(scale, 0, 0, -scale, ActualWidth / 2 - (xmin + xmax) * scale / 2, ActualHeight / 2 + (zmin + zmax) * scale / 2);
        _moved = false; InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(35, 35, 35)), null, new Rect(RenderSize));
        RenderedOverlayCount = 0;
        if (!HasProfile) return;
        var inverse = _view; if (!inverse.HasInverse) return; inverse.Invert();
        var a = inverse.Transform(new Point(0, ActualHeight)); var b = inverse.Transform(new Point(ActualWidth, 0));
        var step = TickStep((b.X - a.X) / 10);
        var grid = new Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(75, 75, 75)), 1);
        for (double x = Math.Ceiling(a.X / step) * step; x <= b.X; x += step)
        {
            var px = Map(x, 0).X; dc.DrawLine(grid, new Point(px, 0), new Point(px, ActualHeight - 25));
            Label(dc, x.ToString("0.###", CultureInfo.InvariantCulture), new Point(px + 3, ActualHeight - 24), Brushes.White, 11);
        }
        step = TickStep((b.Y - a.Y) / 10);
        for (double z = Math.Ceiling(a.Y / step) * step; z <= b.Y; z += step)
        {
            var py = Map(0, z).Y; dc.DrawLine(grid, new Point(0, py), new Point(ActualWidth, py));
            Label(dc, z.ToString("0.###", CultureInfo.InvariantCulture), new Point(4, py + 2), Brushes.White, 11);
        }
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        { path.BeginFigure(Map(_profile.X[0], _profile.Z[0]), false, false); for (int i = 1; i < _profile.Count; i++) path.LineTo(Map(_profile.X[i], _profile.Z[i]), true, false); }
        geometry.Freeze(); dc.DrawGeometry(null, new Pen(Brushes.White, 1), geometry);
        var unsupported = new HashSet<string>();
        foreach (var overlay in _overlays)
        {
            try
            {
                var values = overlay.Values; var color = new SolidColorBrush(NativeOverlayData.Color(values));
                var thickness = Number(values, "_thickness", 1); if (thickness <= 0) thickness = 1;
                var pen = new Pen(color, thickness);
                switch (overlay.Type.ToLowerInvariant())
                {
                    case "point1d": dc.DrawEllipse(color, null, Position(values), 5, 5); break;
                    case "segment1d": dc.DrawLine(pen, Position(values, "Start"), Position(values, "End")); break;
                    case "line1d":
                    {
                        var p = Group(values, "Position"); var direction = Group(values, "Direction");
                        var dx = Number(direction, "X"); var dz = Number(direction, "Z"); var norm = Math.Sqrt(dx * dx + dz * dz);
                        if (norm < 1e-12) throw new FormatException("Zero direction");
                        var length = Math.Max(b.X - a.X, b.Y - a.Y) * 2 / norm;
                        dc.DrawLine(pen, Map(Number(p, "X") - dx * length, Number(p, "Z") - dz * length), Map(Number(p, "X") + dx * length, Number(p, "Z") + dz * length)); break;
                    }
                    case "circle1d":
                    case "circlewindow1d":
                    {
                        var radius = Number(values, "Radius") * Math.Abs(_view.M11); dc.DrawEllipse(null, pen, Position(values, "Center"), radius, radius); break;
                    }
                    case "window1d":
                        dc.DrawRectangle(null,pen,new Rect(Position(values,"Start"),Position(values,"End")));break;
                    case "ring1d":
                    {
                        var center=Position(values,"Center");var scale=Math.Abs(_view.M11);
                        var inner=Number(values,"Inner Radius",Number(values,"Radius1",0))*scale;
                        var outer=Number(values,"Outer Radius",Number(values,"Radius2",0))*scale;
                        if(inner<0||outer<=inner)throw new FormatException("Invalid ring radii");
                        dc.DrawEllipse(null,pen,center,inner,inner);dc.DrawEllipse(null,pen,center,outer,outer);break;
                    }
                    case "polygon1d":
                    {
                        var points = ((IEnumerable)values["Points"]).Cast<IReadOnlyDictionary<string, object>>().Select(p => Position(p)).ToArray();
                        if (points.Length < 2) break;
                        bool closed=values.GetValueOrDefault("_closed") is true,filled=values.GetValueOrDefault("_fill") is true;
                        var shape = new StreamGeometry(); using (var path = shape.Open()) { path.BeginFigure(points[0], filled, closed); path.PolyLineTo(points.Skip(1).ToArray(), true, false); }
                        Brush fill=filled?new SolidColorBrush(color.Color){Opacity=.2}:null;dc.DrawGeometry(fill, pen, shape); break;
                    }
                    case "text1d":
                        var size = Number(values, "_size", Number(values,"Size",12)); Label(dc, NativeOverlayData.Text(values), Position(values, "Position"), color, double.IsFinite(size)?Math.Clamp(size,1,72):12); break;
                    default: unsupported.Add(overlay.Type); continue;
                }
                RenderedOverlayCount++;
            }
            catch (Exception e) when (e is FormatException or InvalidCastException or ArgumentException or KeyNotFoundException)
            { unsupported.Add(overlay.Type + " (数据不完整)"); }
        }
        UnsupportedTypes = unsupported.ToArray();
        Label(dc, "X(mm)", new Point(Math.Max(0, ActualWidth - 48), ActualHeight - 24), Brushes.Red, 12);
        Label(dc, "Z(mm)", new Point(4, 3), Brushes.DeepSkyBlue, 12);
        if (unsupported.Count > 0) Label(dc, "尚未显示图元: " + string.Join(", ", unsupported), new Point(65, 3), Brushes.Orange, 12);
    }
    private Point Map(double x, double z) => _view.Transform(new Point(x, z));
    private Point Position(IReadOnlyDictionary<string, object> values, string key = null)
    { if (key != null) values = Group(values, key); return Map(Number(values, "X"), Number(values, "Z")); }
    private static double TickStep(double raw)
    { var magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, .000000001)))); var factor = raw / magnitude; return (factor <= 1 ? 1 : factor <= 2 ? 2 : factor <= 5 ? 5 : 10) * magnitude; }
    private void Label(DrawingContext dc, string text, Point position, Brush brush, double size)
        => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), position);
}
