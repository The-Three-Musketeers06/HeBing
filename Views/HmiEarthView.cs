using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using AiVisionStudio.Models;

namespace AiVisionStudio.Views;

/// <summary>Native ThreeEarth dataset rendered as a textured 3D globe and geographic flight arcs.</summary>
public sealed class HmiEarthView : Grid, IDisposable
{
    private sealed record Place(string Name, double Latitude, double Longitude, Point3D Position);
    private sealed record Marker(Place Place, Border Label);
    private sealed record Flight(Point3D[] Points, TranslateTransform3D Position, double Phase);
    private readonly HmiElement _element;
    private readonly Viewport3D _viewport = new();
    private readonly PerspectiveCamera _camera = new(new Point3D(0, 0, 3.5), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 45);
    private readonly Model3DGroup _globe = new(), _connections = new();
    private readonly Canvas _labels = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly AxisAngleRotation3D _yaw = new(new Vector3D(0, 1, 0), -110), _pitch = new(new Vector3D(1, 0, 0), 20);
    private readonly Transform3DGroup _rotation = new();
    private readonly List<Marker> _markers = [];
    private readonly List<Flight> _flights = [];
    private readonly DispatcherTimer _timer;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private Point _previousPointer;
    private bool _dragging, _disposed;
    private double _distance = 3.5, _elapsed;
    private string _dataJson;
    private static readonly Color[] RouteColors = [Color.FromRgb(68, 218, 255), Color.FromRgb(255, 200, 85), Color.FromRgb(132, 246, 169), Color.FromRgb(220, 126, 255)];
    public int RouteCount => _flights.Count;
    public int LocationCount => _markers.Count;
    public bool HasTexture { get; }
    public bool AutoRotate { get; set; } = true;
    public Viewport3D Viewport => _viewport;

    public HmiEarthView(HmiElement element)
    {
        _element = element ?? throw new ArgumentNullException(nameof(element));
        ClipToBounds = true; Focusable = true; Background = Brushes.Transparent;
        ToolTip = "拖动旋转地球 · 滚轮缩放 · 双击复位";
        var glow = new RadialGradientBrush();
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, 29, 164, 247), .37));
        glow.GradientStops.Add(new GradientStop(Color.FromArgb(55, 24, 157, 232), .49));
        glow.GradientStops.Add(new GradientStop(Colors.Transparent, .68));
        Children.Add(new Border { Background = glow, IsHitTestVisible = false });
        _viewport.Camera = _camera; Children.Add(_viewport); Children.Add(_labels);
        _rotation.Children.Add(new RotateTransform3D(_yaw)); _rotation.Children.Add(new RotateTransform3D(_pitch)); _globe.Transform = _rotation;
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Color.FromRgb(205, 215, 230)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(100, 130, 160), new Vector3D(-1, -1, -3)));
        Material material;
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "HmiEarthTexture.png");
        if (File.Exists(path))
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze();
            var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill }; brush.Freeze();
            var materials = new MaterialGroup(); materials.Children.Add(new DiffuseMaterial(brush));
            materials.Children.Add(new EmissiveMaterial(new ImageBrush(bitmap) { Opacity = .12 })); materials.Freeze(); material = materials; HasTexture = true;
        }
        else material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(18, 62, 98)));
        _globe.Children.Add(new GeometryModel3D(Sphere(1, 96, 48), material));
        _globe.Children.Add(_connections); scene.Children.Add(_globe); _viewport.Children.Add(new ModelVisual3D { Content = scene });
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += Tick;
        Loaded += (_, _) => { if (!_disposed) { _clock.Restart(); _elapsed = 0; _timer.Start(); Refresh(); UpdateProjection(); } };
        Unloaded += (_, _) => { _timer.Stop(); _clock.Stop(); };
        SizeChanged += (_, _) => { UpdateCamera(); UpdateProjection(); };
        MouseLeftButtonDown += (_, e) =>
        {
            Focus();
            if (e.ClickCount == 2) { ResetView(); e.Handled = true; return; }
            _dragging = true; _previousPointer = e.GetPosition(this); CaptureMouse(); Cursor = Cursors.SizeAll; e.Handled = true;
        };
        MouseMove += (_, e) => { if (!_dragging) return; var current = e.GetPosition(this); var delta = current - _previousPointer; _previousPointer = current; Rotate(delta.X * .45, delta.Y * .45); e.Handled = true; };
        MouseLeftButtonUp += (_, e) => { if (!_dragging) return; _dragging = false; Cursor = null; ReleaseMouseCapture(); e.Handled = true; };
        LostMouseCapture += (_, _) => { _dragging = false; Cursor = null; };
        MouseWheel += (_, e) => { _distance = Math.Clamp(_distance * (e.Delta > 0 ? .9 : 1.1), 2.1, 8); UpdateCamera(); UpdateProjection(); e.Handled = true; };
        KeyDown += (_, e) =>
        {
            switch (e.Key) { case Key.Left: Rotate(-5, 0); break; case Key.Right: Rotate(5, 0); break; case Key.Up: Rotate(0, -5); break; case Key.Down: Rotate(0, 5); break; case Key.Home: ResetView(); break; default: return; }
            e.Handled = true;
        };
        Refresh();
    }

    public void Refresh() => SetData(_element.GetNode("option.dataset"));
    public void SetData(JsonNode dataset)
    {
        if (_disposed) return;
        var json = dataset?.ToJsonString() ?? "null"; if (json == _dataJson) return; _dataJson = json;
        _connections.Children.Clear(); _labels.Children.Clear(); _markers.Clear(); _flights.Clear();
        if (dataset is not JsonArray routes) return;
        var unique = new HashSet<string>(StringComparer.Ordinal); var index = 0;
        foreach (var route in routes.OfType<JsonObject>())
        {
            var start = Parse(route["startArray"]); if (start == null || route["endArray"] is not JsonArray destinations) continue;
            var color = RouteColors[index++ % RouteColors.Length]; var material = new EmissiveMaterial(new SolidColorBrush(color)); material.Freeze();
            AddPlace(start, color);
            foreach (var destination in destinations)
            {
                var end = Parse(destination); if (end == null) continue; AddPlace(end, color);
                if ((end.Position - start.Position).LengthSquared < 1e-12) continue;
                var points = Arc(start.Position, end.Position);
                var tube = new GeometryModel3D(Tube(points, .0035, 5), material); _connections.Children.Add(tube);
                var position = new TranslateTransform3D();
                var particle = new GeometryModel3D(Sphere(.011, 8, 6), new EmissiveMaterial(Brushes.White)) { Transform = position }; _connections.Children.Add(particle);
                _flights.Add(new Flight(points, position, (_flights.Count * .173) % 1));
            }
        }
        AnimateFlights(0); UpdateProjection();
        void AddPlace(Place place, Color color)
        {
            var key = $"{place.Latitude:R},{place.Longitude:R}"; if (!unique.Add(key)) return;
            var marker = new GeometryModel3D(Sphere(.015, 10, 7), new EmissiveMaterial(new SolidColorBrush(color))) { Transform = new TranslateTransform3D((Vector3D)place.Position) }; _connections.Children.Add(marker);
            if (string.IsNullOrEmpty(place.Name)) return;
            var label = new Border { Background = new SolidColorBrush(Color.FromArgb(150, 9, 25, 42)), Padding = new Thickness(4, 1, 4, 1), CornerRadius = new CornerRadius(2), Child = new TextBlock { Text = place.Name, FontSize = 10, Foreground = new SolidColorBrush(color) } };
            _labels.Children.Add(label); _markers.Add(new Marker(place, label));
        }
    }
    public void Rotate(double horizontal, double vertical) { _yaw.Angle = (_yaw.Angle + horizontal) % 360; _pitch.Angle = Math.Clamp(_pitch.Angle + vertical, -85, 85); UpdateProjection(); }
    public void ResetView() { _yaw.Angle = -110; _pitch.Angle = 20; _distance = 3.5; UpdateCamera(); UpdateProjection(); }
    private void UpdateCamera() => _camera.Position = new Point3D(0, 0, _distance * Math.Max(1, ActualWidth / Math.Max(1, ActualHeight)));
    private void Tick(object sender, EventArgs e)
    {
        if (_disposed || !IsVisible) return;
        var now = _clock.Elapsed.TotalSeconds; var delta = Math.Min(.1, Math.Max(0, now - _elapsed)); _elapsed = now;
        if (AutoRotate && !_dragging) _yaw.Angle = (_yaw.Angle + delta * 3) % 360;
        AnimateFlights(now); UpdateProjection();
    }
    private void AnimateFlights(double time)
    {
        foreach (var flight in _flights)
        {
            var offset = ((time * .2 + flight.Phase) % 1) * (flight.Points.Length - 1); var at = (int)offset; var next = Math.Min(at + 1, flight.Points.Length - 1);
            var point = flight.Points[at] + (flight.Points[next] - flight.Points[at]) * (offset - at);
            flight.Position.OffsetX = point.X; flight.Position.OffsetY = point.Y; flight.Position.OffsetZ = point.Z;
        }
    }
    private void UpdateProjection()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var scale = ActualWidth / (2 * Math.Tan(_camera.FieldOfView * Math.PI / 360));
        foreach (var marker in _markers)
        {
            var point = _rotation.Transform(marker.Place.Position);
            if (point.Z * _camera.Position.Z < 1.01) { marker.Label.Visibility = Visibility.Hidden; continue; }
            marker.Label.Visibility = Visibility.Visible; var factor = scale / (_camera.Position.Z - point.Z);
            Canvas.SetLeft(marker.Label, ActualWidth / 2 + point.X * factor + 5); Canvas.SetTop(marker.Label, ActualHeight / 2 - point.Y * factor - 9);
        }
    }
    private static Place Parse(JsonNode value)
    {
        if (value is not JsonObject item || !double.TryParse(HmiVisuals.Text(item["N"]), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var latitude) || !double.TryParse(HmiVisuals.Text(item["E"]), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var longitude) || !double.IsFinite(latitude) || !double.IsFinite(longitude) || latitude < -90 || latitude > 90) return null;
        longitude = ((longitude + 180) % 360 + 360) % 360 - 180;
        var lat = latitude * Math.PI / 180; var lon = longitude * Math.PI / 180;
        return new Place(HmiVisuals.Text(item["name"]), latitude, longitude, new Point3D(Math.Cos(lat) * Math.Sin(lon) * 1.006, Math.Sin(lat) * 1.006, Math.Cos(lat) * Math.Cos(lon) * 1.006));
    }
    private static Point3D[] Arc(Point3D start, Point3D end)
    {
        var a = (Vector3D)start; var b = (Vector3D)end; a.Normalize(); b.Normalize();
        var angle = Math.Acos(Math.Clamp(Vector3D.DotProduct(a, b), -1, 1)); var sin = Math.Sin(angle);
        var normal = Vector3D.CrossProduct(a, b); if (normal.Length < .00001) normal = Vector3D.CrossProduct(a, Math.Abs(a.Y) < .9 ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0)); normal.Normalize();
        var tangent = Vector3D.CrossProduct(normal, a); var result = new Point3D[65];
        for (var i = 0; i < result.Length; i++)
        {
            var t = i / (double)(result.Length - 1);
            var v = Math.Abs(sin) > .00001 ? a * (Math.Sin((1 - t) * angle) / sin) + b * (Math.Sin(t * angle) / sin) : angle < .001 ? a : a * Math.Cos(Math.PI * t) + tangent * Math.Sin(Math.PI * t);
            v.Normalize(); v *= 1.008 + Math.Sin(Math.PI * t) * Math.Min(.55, .12 + angle * .16); result[i] = (Point3D)v;
        }
        return result;
    }
    private static MeshGeometry3D Sphere(double radius, int slices, int stacks)
    {
        var mesh = new MeshGeometry3D();
        for (var y = 0; y <= stacks; y++) for (var x = 0; x <= slices; x++)
        {
            var latitude = Math.PI * y / stacks; var longitude = 2 * Math.PI * x / slices - Math.PI;
            var v = new Vector3D(Math.Sin(latitude) * Math.Sin(longitude), Math.Cos(latitude), Math.Sin(latitude) * Math.Cos(longitude));
            mesh.Positions.Add((Point3D)(v * radius)); mesh.Normals.Add(v); mesh.TextureCoordinates.Add(new Point(x / (double)slices, y / (double)stacks));
        }
        for (var y = 0; y < stacks; y++) for (var x = 0; x < slices; x++) { var a = y * (slices + 1) + x; var b = a + slices + 1; Triangle(mesh, a, b, a + 1); Triangle(mesh, a + 1, b, b + 1); }
        mesh.Freeze(); return mesh;
    }
    private static MeshGeometry3D Tube(Point3D[] points, double radius, int sides)
    {
        var mesh = new MeshGeometry3D();
        for (var i = 0; i < points.Length; i++)
        {
            var direction = points[Math.Min(i + 1, points.Length - 1)] - points[Math.Max(0, i - 1)]; direction.Normalize();
            var side = Vector3D.CrossProduct(direction, (Vector3D)points[i]); if (side.Length < .00001) side = Vector3D.CrossProduct(direction, Math.Abs(direction.Y) < .9 ? new Vector3D(0, 1, 0) : new Vector3D(1, 0, 0)); side.Normalize(); var up = Vector3D.CrossProduct(side, direction); up.Normalize();
            for (var j = 0; j < sides; j++) { var angle = 2 * Math.PI * j / sides; var normal = side * Math.Cos(angle) + up * Math.Sin(angle); mesh.Positions.Add(points[i] + normal * radius); mesh.Normals.Add(normal); }
            if (i == 0) continue;
            for (var j = 0; j < sides; j++) { var a = (i - 1) * sides + j; var b = (i - 1) * sides + (j + 1) % sides; var c = i * sides + j; var d = i * sides + (j + 1) % sides; Triangle(mesh, a, c, b); Triangle(mesh, b, c, d); }
        }
        mesh.Freeze(); return mesh;
    }
    private static void Triangle(MeshGeometry3D mesh, int a, int b, int c) { mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(c); }
    public void Dispose() { if (_disposed) return; _disposed = true; _timer.Stop(); _timer.Tick -= Tick; _clock.Stop(); if (IsMouseCaptured) ReleaseMouseCapture(); _viewport.Children.Clear(); _markers.Clear(); _flights.Clear(); }
}
