using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Algorithm.Impl;
using OpenCvSharp;
using Window = System.Windows.Window;

namespace AiVisionStudio.Views;

/// <summary>Metric X/Z profile and reference-XY cloud drawing, with explicit depth selection.</summary>
internal sealed class SpatialRegionEditor : Window
{
    private readonly ImageView _view = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly Action<Dictionary<string, object>> _commit;
    private readonly string _type;
    private readonly IReadOnlyDictionary<string, object> _original;
    private readonly Projection _projection;
    private readonly PointCloud _cloud;
    private readonly IReadOnlyDictionary<string, object> _reference;
    private readonly List<(double X, double Y, double Z)> _points;
    private readonly ImageView _cloudPreview;
    private Dictionary<string, object> _pending;

    public static SpatialRegionEditor ForProfile(ProfileData profile, string type, IReadOnlyDictionary<string, object> original, Action<Dictionary<string, object>> commit)
        => new(profile.X.Zip(profile.Z, (x, z) => ((double)x, (double)z, 0d)).Where(q => double.IsFinite(q.Item1 + q.Item2)).ToList(), type, original, commit);

    public static SpatialRegionEditor ForCloud(PointCloud cloud, IReadOnlyDictionary<string, object> reference, string type, IReadOnlyDictionary<string, object> original, Action<Dictionary<string, object>> commit)
    {
        var transform = CloudRegionSelector.CreateReferenceTransform(reference);
        var points = Enumerable.Range(0, cloud.Count).Select(i => transform(cloud.X[i], cloud.Y[i], cloud.Z[i])).Where(q => double.IsFinite(q.X + q.Y + q.Z)).ToList();
        return new(points, type, original, commit, cloud, reference);
    }

    private SpatialRegionEditor(List<(double X, double Y, double Z)> points, string type, IReadOnlyDictionary<string, object> original,
        Action<Dictionary<string, object>> commit, PointCloud cloud = null, IReadOnlyDictionary<string, object> reference = null)
    {
        if (points.Count == 0) throw new ArgumentException("输入数据中没有可供画选的有效坐标");
        _type = type; _original = original; _commit = commit; _points = points; _cloud = cloud; _reference = reference;
        Title = cloud == null ? "轮廓区域手动画选（X / Z）" : "点云区域手动画选（参考坐标系 X / Y）";
        Width = cloud == null ? 1050 : 1350; Height = 780; MinWidth = 700; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("Panel"); Foreground = (Brush)Application.Current.FindResource("Text");
        _projection = new(points);
        var root = new DockPanel { Background = Background }; Content = root;
        var instructions = new TextBlock { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap, Text = cloud == null
            ? "在轮廓图上拖动画选。横轴 X、纵轴 Z，坐标单位与输入轮廓一致。Enter 结束画选；Esc 取消当前绘制。"
            : "在参考坐标系的 XY 俯视图上画选。长方体和圆柱的深度自动取区域内点云的 Z 范围；有向包围盒支持绕 Z 旋转。右侧显示生成区域。" };
        DockPanel.SetDock(instructions, Dock.Top); root.Children.Add(instructions);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        AddButton("重新画选", Begin);
        AddButton("结束画选", () => _view.CompleteRegionEdit());
        AddButton("应用区域", () =>
        {
            if (_view.IsRegionEditing) _view.CompleteRegionEdit();
            if (_pending == null) { _status.Text = "请先画出有效区域"; return; }
            _commit(_pending); DialogResult = true;
        });
        AddButton("取消", () => { DialogResult = false; });
        DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status);
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); root.Children.Add(body); body.Children.Add(_view);
        if (cloud != null)
        {
            body.ColumnDefinitions.Add(new() { Width = new GridLength(.65, GridUnitType.Star) });
            _cloudPreview = new ImageView(); Grid.SetColumn(_cloudPreview, 1); body.Children.Add(_cloudPreview); _cloudPreview.ShowCloud(cloud);
        }
        Loaded += (_, _) =>
        {
            using var image = RenderPoints(); _view.ShowMat(image, cloud == null ? "轮廓 X/Z（实际坐标）" : "点云 XY（参考坐标）"); Begin();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (_view.HandleRegionEditKey(e.Key)) { e.Handled = true; return; }
            if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };
        Closed += (_, _) => { _view.CancelRegionEdit(); _view.Clear(); _cloudPreview?.Clear(); };
        void AddButton(string text, Action action)
        {
            var button = new Button { Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(5, 0, 0, 0) };
            button.Click += (_, _) => action(); footer.Children.Add(button);
        }
    }

    private void Begin()
    {
        _pending = null;
        var editType = GraphicRegionValue.EditType(_type);
        if (editType == null) { _status.Text = "此区域类型暂不适用于俯视画选"; return; }
        var initial = Transform(GraphicRegionValue.ToEditor(_type, _original), true);
        _view.BeginRegionEdit(editType, initial, drawn =>
        {
            try
            {
                var native = Transform(drawn, false);
                var output = GraphicRegionValue.FromEditor(_type, _original, native);
                if (_cloud != null) SetCloudDepth(output);
                _pending = output;
                _status.Text = "区域已完成，确认后点击“应用区域”。";
                if (_cloud != null) UpdateCloudPreview(output);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            { _status.Text = exception.Message; _pending = null; }
        }, Title);
    }

    private void SetCloudDepth(Dictionary<string, object> output)
    {
        var body = (Dictionary<string, object>)output[_type];
        var low = _points.Min(p => p.Z); var high = _points.Max(p => p.Z); if (high - low < .001) high = low + .001;
        AssignDepth(low, high);
        var selected = CloudRegionSelector.SelectGroups(_cloud, output, null, _reference).SelectMany(group => group).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("当前画选区域内没有点云，请重新画选");
        var transform = CloudRegionSelector.CreateReferenceTransform(_reference);
        var depth = selected.Select(i => transform(_cloud.X[i], _cloud.Y[i], _cloud.Z[i]).Z).ToArray();
        low = depth.Min(); high = depth.Max(); if (high - low < .001) { low -= .0005; high += .0005; }
        AssignDepth(low, high);
        void AssignDepth(double minimum, double maximum)
        {
            if (_type == "box")
            {
                var start = new Dictionary<string, object>(GraphicRegionValue.Group(body, "Start")); var end = new Dictionary<string, object>(GraphicRegionValue.Group(body, "End"));
                start["Z"] = minimum; end["Z"] = maximum; body["Start"] = start; body["End"] = end;
            }
            else if (_type == "orientedbox")
            {
                var center = new Dictionary<string, object>(GraphicRegionValue.Group(body, "Center")); var size = new Dictionary<string, object>(GraphicRegionValue.Group(body, "Size"));
                center["Z"] = (minimum + maximum) / 2; size["Z"] = maximum - minimum; body["Center"] = center; body["Size"] = size;
            }
            else if (_type == "cylinderbox")
            {
                var bottom = new Dictionary<string, object>(GraphicRegionValue.Group(body, "Bottom")); bottom["Z"] = minimum; body["Bottom"] = bottom; body["Height"] = maximum - minimum;
            }
        }
    }

    private void UpdateCloudPreview(Dictionary<string, object> region)
    {
        var selected = CloudRegionSelector.SelectGroups(_cloud, region, null, _reference).SelectMany(group => group).ToArray();
        var display = _cloud.Clone(); display.DisplayIndices = selected; _cloudPreview.ShowCloud(display);
        _status.Text = $"已选中 {selected.Length:N0} 个点。右侧显示区域内点云；点击“应用区域”保存。";
    }

    private Mat RenderPoints()
    {
        var image = new Mat(700, 1050, MatType.CV_8UC3, new Scalar(28, 28, 28));
        var stride = Math.Max(1, _points.Count / 180000);
        OpenCvSharp.Point? previous = null;
        var minZ = _points.Count > 0 ? _points.Min(p => p.Z) : 0; var maxZ = _points.Count > 0 ? _points.Max(p => p.Z) : 1;
        for (var index = 0; index < _points.Count; index += stride)
        {
            var point = _points[index]; var pixel = _projection.Forward(point.X, point.Y);
            var at = new OpenCvSharp.Point((int)Math.Round(pixel.X), (int)Math.Round(pixel.Y));
            if (_cloud == null && previous.HasValue) Cv2.Line(image, previous.Value, at, Scalar.White, 1, LineTypes.AntiAlias);
            else
            {
                var amount = Math.Clamp((point.Z - minZ) / Math.Max(.000001, maxZ - minZ), 0, 1);
                Cv2.Circle(image, at, 1, new Scalar(255 * (1 - amount), 180, 255 * amount), -1);
            }
            previous = at;
        }
        Cv2.PutText(image, "X ->   " + (_cloud == null ? "Z up" : "Y up (reference XY)"), new OpenCvSharp.Point(25, 25), HersheyFonts.HersheySimplex, .55, Scalar.White);
        Cv2.PutText(image, $"X [{_projection.XMin:G5}, {_projection.XMax:G5}]  {(_cloud == null ? "Z" : "Y")} [{_projection.YMin:G5}, {_projection.YMax:G5}]", new OpenCvSharp.Point(25, 680), HersheyFonts.HersheySimplex, .5, Scalar.White);
        return image;
    }

    private Dictionary<string, object> Transform(IReadOnlyDictionary<string, object> wrapper, bool forward)
    {
        var type = wrapper.GetValueOrDefault("Type")?.ToString();
        var raw = GraphicRegionValue.Group(wrapper, type) ?? wrapper;
        var body = new Dictionary<string, object>(raw);
        foreach (var key in new[] { "Center", "Start", "End", "Point", "Position" })
            if (GraphicRegionValue.Group(raw, key) is { } point) body[key] = ConvertPoint(point);
        if (raw.ContainsKey("X") && raw.ContainsKey("Y"))
            foreach (var pair in ConvertPoint(raw)) body[pair.Key] = pair.Value;
        if (GraphicRegionValue.Group(raw, "Size") is { } size)
            body["Size"] = new Dictionary<string, object> { ["Width"] = ConvertLength(GraphicRegionValue.Number(size, "Width")), ["Height"] = ConvertLength(GraphicRegionValue.Number(size, "Height")) };
        foreach (var key in new[] { "Radius", "Inner Radius", "Outer Radius", "Long Axis", "Short Axis" })
            if (raw.ContainsKey(key)) body[key] = ConvertLength(GraphicRegionValue.Number(raw, key));
        foreach (var key in new[] { "Rotation", "Start Angle", "Angle Range" })
            if (raw.ContainsKey(key)) body[key] = -GraphicRegionValue.Number(raw, key);
        if (raw.GetValueOrDefault("Points") is IEnumerable vertices)
            body["Points"] = vertices.Cast<object>().OfType<IReadOnlyDictionary<string, object>>().Select(p => (object)ConvertPoint(p)).ToList();
        return GraphicRegionValue.Wrap(type, body);
        double ConvertLength(double value) => forward ? value * _projection.Scale : value / _projection.Scale;
        Dictionary<string, object> ConvertPoint(IReadOnlyDictionary<string, object> point)
        {
            var x = GraphicRegionValue.Number(point, "X"); var y = GraphicRegionValue.Number(point, "Y");
            var converted = forward ? _projection.Forward(x, y) : _projection.Inverse(x, y);
            return GraphicRegionValue.XY(converted.X, converted.Y);
        }
    }

    private sealed class Projection
    {
        public double XMin { get; } public double XMax { get; } public double YMin { get; } public double YMax { get; } public double Scale { get; }
        private readonly double _left, _top;
        public Projection(List<(double X, double Y, double Z)> points)
        {
            XMin = points.Count > 0 ? points.Min(p => p.X) : 0; XMax = points.Count > 0 ? points.Max(p => p.X) : 1;
            YMin = points.Count > 0 ? points.Min(p => p.Y) : 0; YMax = points.Count > 0 ? points.Max(p => p.Y) : 1;
            Scale = Math.Min(930 / Math.Max(.001, XMax - XMin), 570 / Math.Max(.001, YMax - YMin));
            _left = (1050 - (XMax - XMin) * Scale) / 2; _top = (700 - (YMax - YMin) * Scale) / 2;
        }
        public (double X, double Y) Forward(double x, double y) => (_left + (x - XMin) * Scale, _top + (YMax - y) * Scale);
        public (double X, double Y) Inverse(double x, double y) => (XMin + (x - _left) / Scale, YMax - (y - _top) / Scale);
    }
}
