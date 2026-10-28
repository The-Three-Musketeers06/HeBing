using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AiVisionStudio.Views;

public partial class ImageView
{
    private RegionEditSurface _regionSurface;
    private Border _regionChrome;
    private TextBlock _regionCaption, _regionHint;
    private Button _regionApply, _regionDraw, _regionHand;
    private RegionDraft _regionDraft, _regionBeforeDrag;
    private string _regionType;
    private Action<Dictionary<string, object>> _regionCommit;
    private Action<IReadOnlyList<Dictionary<string, object>>> _maskCommit;
    private readonly List<List<Point>> _maskStrokes = new();
    private double _maskRadius = 8;
    private bool _regionDrawing, _regionHandMode, _regionPointerDown, _regionPanning;
    private int _regionHandle = -1;
    private Point _regionPointerStart, _regionPanStart;
    private Matrix _regionPanMatrix;
    private MouseButton _regionPanButton;
    private string _regionLabel;
    private WrapPanel _sectorAngleRow;
    private TextBox _sectorStartAngle, _sectorSweepAngle;
    private bool _updatingSectorAngles, _sectorAnglesValid = true;
    private bool _showRegionChrome = true;

    /// <summary>An edit is a draft until Apply/Enter; Cancel leaves the parameter unchanged.</summary>
    public bool IsRegionEditing => _regionCommit != null || _maskCommit != null;
    public event EventHandler RegionEditingChanged;

    private void BuildRegionEditing()
    {
        Focusable = true;
        _regionSurface = new RegionEditSurface(this) { IsHitTestVisible = false };
        Host.Children.Add(_regionSurface);
        var panel = new StackPanel();
        var top = new WrapPanel { Orientation = Orientation.Horizontal };
        _regionCaption = new TextBlock { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
        top.Children.Add(_regionCaption);
        _regionDraw = Add("重新画选", () => { _regionDraft = null; _maskStrokes.Clear(); _regionDrawing = true; _regionHandMode = false; UpdateRegionEditor(); });
        _regionHand = Add("手形平移", () => { _regionHandMode = !_regionHandMode; UpdateRegionEditor(); });
        _regionApply = Add("应用 ↵", CompleteRegionEdit);
        Add("取消 Esc", CancelRegionEdit);
        panel.Children.Add(top);
        _regionHint = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(196, 230, 246)), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 2, 0) };
        panel.Children.Add(_regionHint);
        _sectorAngleRow = new WrapPanel { Margin = new Thickness(2, 5, 2, 0), Visibility = Visibility.Collapsed };
        _sectorAngleRow.Children.Add(new TextBlock { Text = "起始角度（°）", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        _sectorStartAngle = new TextBox { Width = 70, Margin = new Thickness(4, 0, 12, 0), Padding = new Thickness(5, 3, 5, 3) };
        _sectorAngleRow.Children.Add(_sectorStartAngle);
        _sectorAngleRow.Children.Add(new TextBlock { Text = "扇形角度（°）", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        _sectorSweepAngle = new TextBox { Width = 70, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(5, 3, 5, 3) };
        _sectorAngleRow.Children.Add(_sectorSweepAngle);
        _sectorStartAngle.TextChanged += OnSectorAnglesChanged; _sectorSweepAngle.TextChanged += OnSectorAnglesChanged;
        panel.Children.Add(_sectorAngleRow);
        _regionChrome = new Border { Child = panel, Background = new SolidColorBrush(Color.FromArgb(225, 28, 42, 53)), BorderBrush = new SolidColorBrush(Color.FromRgb(53, 171, 204)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(6), Margin = new Thickness(6), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 650, Visibility = Visibility.Collapsed };
        Host.Children.Add(_regionChrome);
        Host.PreviewMouseDown += RegionMouseDown;
        Host.PreviewMouseMove += RegionMouseMove;
        Host.PreviewMouseUp += RegionMouseUp;
        Host.LostMouseCapture += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, Host)) return;
            _regionPointerDown = false;
            EndRegionPan(false);
        };
        PreviewKeyDown += (_, e) => { if (HandleRegionEditKey(e.Key)) e.Handled = true; };
        Mat.Changed += (_, _) => _regionSurface.InvalidateVisual();
        Unloaded += (_, _) => CancelRegionEdit();
        Button Add(string text, Action action)
        {
            var button = new Button { Content = text, Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(2, 0, 2, 0), MinHeight = 27, Focusable = false };
            button.Click += (_, e) => { action(); e.Handled = true; };
            top.Children.Add(button); return button;
        }
    }

    /// <summary>Edits a native geometry dictionary in source-image coordinates.</summary>
    public void BeginRegionEdit(string type, IReadOnlyDictionary<string, object> initial, Action<Dictionary<string, object>> commit, string caption = null)
    {
        if (!HasImage) throw new InvalidOperationException("请先载入要画选的图像");
        if (commit == null) throw new ArgumentNullException(nameof(commit));
        var normalized = type?.ToLowerInvariant() ?? "rect2d";
        if (normalized is "rotatedrect2d" or "rectangle") normalized = "rect2d";
        if (normalized is not ("rect2d" or "ellipse2d" or "circle2d" or "polygon2d" or "trace2d" or "ring2d" or "sector2d" or "segment2d" or "caliper" or "point2d" or "origin" or "fiducialpoint2d"))
            throw new NotSupportedException("当前图像画选不支持该图形：" + type);
        CancelRegionEdit();
        EndRegionEdit(true);
        ApplyPendingViewTransform();
        MeasurementEnabled = false; _dragging = false; ReleaseMouseCapture();
        _regionType = normalized; _regionCommit = commit; _regionLabel = caption ?? RegionTypeLabel(normalized);
        _regionDraft = RegionDraft.Read(normalized, initial);
        _regionDrawing = _regionDraft == null || !_regionDraft.IsValid;
        if (_regionDrawing) _regionDraft = null;
        _regionHandMode = false; Focus();
        UpdateRegionEditor(); RegionEditingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Paints exclusion strokes; every returned item is a polygon2d region.</summary>
    public void BeginMaskPaint(double radius, Action<IReadOnlyList<Dictionary<string, object>>> commit)
    {
        if (!HasImage) throw new InvalidOperationException("请先载入要编辑遮罩的图像");
        if (commit == null) throw new ArgumentNullException(nameof(commit));
        CancelRegionEdit(); MeasurementEnabled = false;
        _maskRadius = double.IsFinite(radius) ? Math.Clamp(radius, 1, 500) : 8;
        _maskCommit = commit; _regionType = "mask"; _regionLabel = "屏蔽画笔"; _regionDrawing = true; _regionHandMode = false;
        Focus(); UpdateRegionEditor(); RegionEditingChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool HandleRegionEditKey(Key key)
    {
        if (!IsRegionEditing) return false;
        if ((key is Key.Delete or Key.Back) && Keyboard.FocusedElement is TextBox text && IsRegionChromeSource(text)) return false;
        if (key == Key.Escape) { CancelRegionEdit(); return true; }
        if (key == Key.Enter) { CompleteRegionEdit(); return true; }
        if (key == Key.Delete)
        {
            _regionDraft = null; _maskStrokes.Clear(); _regionDrawing = true; UpdateRegionEditor(); return true;
        }
        if (key == Key.Back && _regionDrawing && (_regionType is "polygon2d" or "trace2d") && _regionDraft?.Points.Count > 0)
        { _regionDraft.Points.RemoveAt(_regionDraft.Points.Count - 1); UpdateRegionEditor(); return true; }
        return false;
    }

    public void CompleteRegionEdit()
    {
        if (_regionType == "sector2d" && !_sectorAnglesValid)
        { _regionHint.Text = "请输入有效角度：起始角度 -360～360°，扇形角度 -360～360°且不能为 0"; return; }
        if (_maskCommit != null)
        {
            if (_maskStrokes.Count == 0) { _regionHint.Text = "请先在要屏蔽的位置涂抹"; return; }
            var callback = _maskCommit;
            var regions = _maskStrokes.Where(s => s.Count > 0).SelectMany(StrokeRegions).ToArray();
            CancelRegionEdit(); callback(regions); return;
        }
        if (_regionCommit == null) return;
        if (_regionDraft?.IsValid != true) { _regionHint.Text = "请画出有效区域；多边形至少需要三个点"; return; }
        var done = _regionCommit; var region = _regionDraft.ToNative();
        CancelRegionEdit(); done(region);
    }

    public void CancelRegionEdit()
    {
        EndRegionPan();
        var active = IsRegionEditing;
        _regionCommit = null; _maskCommit = null; _regionDraft = null; _regionBeforeDrag = null;
        _maskStrokes.Clear(); _regionPointerDown = false; _regionHandMode = false;
        if (Host.IsMouseCaptured) Host.ReleaseMouseCapture();
        if (_regionChrome != null) _regionChrome.Visibility = Visibility.Collapsed;
        _regionSurface?.InvalidateVisual(); Cursor = Cursors.Arrow;
        UpdateRegionPanCursor();
        if (active) RegionEditingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ActivateImagePan()
    {
        MeasurementEnabled = false;
        if (IsRegionEditing || RegionCanvas.IsEditing) { _regionHandMode = !_regionHandMode; UpdateRegionEditor(); }
        else Cursor = Cursors.Hand;
    }

    private void UpdateRegionEditor()
    {
        _regionChrome.Visibility = IsRegionEditing && _showRegionChrome ? Visibility.Visible : Visibility.Collapsed;
        _regionCaption.Text = _regionLabel ?? "区域编辑";
        _regionApply.IsEnabled = _maskCommit != null ? _maskStrokes.Count > 0 : _regionDraft?.IsValid == true;
        _regionHand.Content = _regionHandMode ? "继续画选" : "手形平移";
        _regionDraw.Content = _maskCommit != null ? "清空笔画" : "重新画选";
        Cursor = _regionHandMode ? Cursors.Hand : Cursors.Cross;
        UpdateRegionPanCursor();
        _regionHint.Text = _regionHandMode ? "左键拖动画面；再次点击手形按钮继续编辑。滚轮缩放，中键始终可平移。"
            : _maskCommit != null ? $"按住左键涂抹要排除的特征，画笔半径 {_maskRadius:0.#} px；Enter 应用，Esc 取消。"
            : _regionDrawing ? _regionType is "polygon2d" or "trace2d" ? "逐点单击画多边形或折线，双击结束；Backspace 撤回一点，Enter 应用。中键平移，滚轮缩放。"
                : "按住左键拖动绘制；释放后拖动方形手柄调整，橙色手柄旋转。Enter 应用，Esc 取消。"
            : "拖动中心移动、方形手柄调整、橙色圆点旋转；重新画选可替换。Enter 应用，Esc 取消。";
        _sectorAngleRow.Visibility = _regionType == "sector2d" ? Visibility.Visible : Visibility.Collapsed;
        _sectorAngleRow.IsEnabled = _regionDraft != null;
        if (_regionType == "sector2d")
        {
            _regionHint.Text += " 扇形可输入角度，也可拖动起止边界控制点。";
            _updatingSectorAngles = true;
            _sectorStartAngle.Text = (_regionDraft?.StartAngle ?? 0).ToString("0.###", CultureInfo.InvariantCulture);
            _sectorSweepAngle.Text = (_regionDraft?.AngleRange ?? 90).ToString("0.###", CultureInfo.InvariantCulture);
            _updatingSectorAngles = false; _sectorAnglesValid = true;
        }
        _regionSurface.InvalidateVisual();
    }

    private void OnSectorAnglesChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingSectorAngles || _regionType != "sector2d" || _regionDraft == null) return;
        _sectorAnglesValid = double.TryParse(_sectorStartAngle.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var start) && double.IsFinite(start) && Math.Abs(start) <= 360;
        _sectorAnglesValid &= double.TryParse(_sectorSweepAngle.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var sweep) && double.IsFinite(sweep) && Math.Abs(sweep) <= 360 && Math.Abs(sweep) > .1;
        _sectorStartAngle.BorderBrush = _sectorSweepAngle.BorderBrush = _sectorAnglesValid ? Brushes.Gray : Brushes.OrangeRed;
        if (_sectorAnglesValid) { _regionDraft.StartAngle = start; _regionDraft.AngleRange = sweep; }
        _regionApply.IsEnabled = _sectorAnglesValid && _regionDraft.IsValid;
        _regionSurface.InvalidateVisual();
    }

    private bool IsRegionChromeSource(object source)
    {
        for (var current = source as DependencyObject; current != null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current == _regionChrome) return true;
        return false;
    }
    private Point RegionImagePoint(Point screen)
    {
        var inverse = Mat.Matrix; if (inverse.HasInverse) inverse.Invert();
        var p = inverse.Transform(screen);
        return new Point(Math.Clamp(p.X, 0, Math.Max(0, (_bitmap?.PixelWidth ?? 1) - 1)), Math.Clamp(p.Y, 0, Math.Max(0, (_bitmap?.PixelHeight ?? 1) - 1)));
    }
    private double RegionScale => Math.Max(.0001, Math.Sqrt(Mat.Matrix.M11 * Mat.Matrix.M11 + Mat.Matrix.M12 * Mat.Matrix.M12));

    private bool WantsRegionPan(MouseButton button, ModifierKeys modifiers) => HasImage
        && (button == MouseButton.Middle || button == MouseButton.Left
            && (IsRegionEditing || RegionCanvas.IsEditing)
            && (_regionHandMode || modifiers.HasFlag(ModifierKeys.Alt)));

    private bool BeginRegionPan(Point position, MouseButton button)
    {
        if (_regionPointerDown || RegionCanvas.IsMouseCaptured) return false;
        ApplyPendingViewTransform();
        _regionPanStart = position; _regionPanMatrix = Mat.Matrix; _regionPanButton = button;
        _regionPanning = Host.CaptureMouse();
        UpdateRegionPanCursor();
        return _regionPanning;
    }

    private void UpdateRegionPan(Point position)
    {
        var delta = position - _regionPanStart;
        var matrix = _regionPanMatrix;
        matrix.Translate(delta.X, delta.Y);
        QueueViewTransform(matrix);
        _userMoved = true;
    }

    private void EndRegionPan(bool releaseCapture = true)
    {
        if (!_regionPanning) return;
        ApplyPendingViewTransform();
        _regionPanning = false;
        UpdateRegionPanCursor();
        if (releaseCapture && Host.IsMouseCaptured) Host.ReleaseMouseCapture();
    }

    private void UpdateRegionPanCursor()
    {
        bool hand = _regionPanning || _regionHandMode && (IsRegionEditing || RegionCanvas.IsEditing);
        Host.ForceCursor = hand;
        Host.Cursor = hand ? Cursors.Hand : null;
    }

    private void RegionMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasImage || IsRegionChromeSource(e.OriginalSource)) return;
        if (_regionPanning) { e.Handled = true; return; }
        if (WantsRegionPan(e.ChangedButton, Keyboard.Modifiers))
        {
            BeginRegionPan(e.GetPosition(Host), e.ChangedButton);
            e.Handled = true; return;
        }
        if (!IsRegionEditing) return;
        if (e.ChangedButton == MouseButton.Right)
        {
            if ((_regionType is "polygon2d" or "trace2d") && _regionDrawing && _regionDraft?.IsValid == true) _regionDrawing = false;
            e.Handled = true; UpdateRegionEditor(); return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        Focus(); e.Handled = true; var p = RegionImagePoint(e.GetPosition(Host));
        if (_maskCommit != null)
        { _maskStrokes.Add(new List<Point> { p }); _regionPointerDown = true; Host.CaptureMouse(); UpdateRegionEditor(); return; }
        if (_regionDrawing && (_regionType is "polygon2d" or "trace2d"))
        {
            _regionDraft ??= new RegionDraft { Type = _regionType };
            if (e.ClickCount == 2)
            { if (_regionDraft.IsValid) _regionDrawing = false; }
            else _regionDraft.Points.Add(p);
            UpdateRegionEditor(); return;
        }
        if (_regionDrawing || _regionDraft == null)
        {
            _regionPointerStart = p; _regionDraft = new RegionDraft { Type = _regionType, Center = p, A = p, B = p };
            if (_regionType is "point2d" or "origin" or "fiducialpoint2d") { _regionDraft.Width = 1; _regionDrawing = false; UpdateRegionEditor(); return; }
            _regionHandle = -2;
        }
        else
        {
            var handles = _regionDraft.Handles(RegionScale);
            _regionHandle = handles.FindIndex(h => (h.Position - p).Length <= 10 / RegionScale);
            if (_regionHandle < 0 && _regionDraft.Contains(p, 8 / RegionScale)) _regionHandle = handles.FindIndex(h => h.Kind == "move");
            if (_regionHandle < 0 && (_regionType is "point2d" or "origin" or "fiducialpoint2d"))
            { _regionDraft.Center = p; UpdateRegionEditor(); return; }
            if (_regionHandle < 0) return;
            _regionPointerStart = p;
        }
        _regionBeforeDrag = _regionDraft.Copy(); _regionPointerDown = true; Host.CaptureMouse(); UpdateRegionEditor();
    }

    private void RegionMouseMove(object sender, MouseEventArgs e)
    {
        if (_regionPanning)
        {
            var state = _regionPanButton == MouseButton.Left ? e.LeftButton : e.MiddleButton;
            if (state == MouseButtonState.Pressed) UpdateRegionPan(e.GetPosition(Host));
            else EndRegionPan();
            e.Handled = true; return;
        }
        if (!IsRegionEditing || !_regionPointerDown) return;
        var p = RegionImagePoint(e.GetPosition(Host)); e.Handled = true;
        if (_maskCommit != null)
        {
            var stroke = _maskStrokes[^1];
            if ((stroke[^1] - p).Length >= Math.Max(.5, _maskRadius / 3)) stroke.Add(p);
            _regionSurface.InvalidateVisual(); return;
        }
        if (_regionHandle == -2) _regionDraft.Draw(_regionPointerStart, p);
        else if (_regionBeforeDrag != null)
        {
            _regionDraft = _regionBeforeDrag.Copy();
            _regionDraft.Drag(_regionHandle, _regionPointerStart, p, RegionScale);
        }
        UpdateRegionEditor();
    }

    private void RegionMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_regionPanning)
        {
            if (e.ChangedButton == _regionPanButton)
            { UpdateRegionPan(e.GetPosition(Host)); EndRegionPan(); }
            e.Handled = true; return;
        }
        if (!_regionPointerDown || !IsRegionEditing) return;
        if (e.ChangedButton == MouseButton.Left)
        {
            // Mouse-up can arrive without an intervening move for short drags.
            if (_maskCommit == null && _regionHandle == -2) _regionDraft.Draw(_regionPointerStart, RegionImagePoint(e.GetPosition(Host)));
            _regionPointerDown = false; Host.ReleaseMouseCapture(); _regionDrawing = _maskCommit == null && _regionDraft?.IsValid != true;
            UpdateRegionEditor(); e.Handled = true;
        }
    }

    private IEnumerable<Dictionary<string, object>> StrokeRegions(List<Point> stroke)
    {
        foreach (var p in stroke)
            yield return RegionDraft.Polygon(Enumerable.Range(0, 16).Select(i => p + new Vector(Math.Cos(i * Math.PI / 8) * _maskRadius, Math.Sin(i * Math.PI / 8) * _maskRadius)));
        for (var i = 1; i < stroke.Count; i++)
        {
            var d = stroke[i] - stroke[i - 1]; if (d.Length < .001) continue;
            var n = new Vector(-d.Y, d.X); n.Normalize(); n *= _maskRadius;
            yield return RegionDraft.Polygon(new[] { stroke[i - 1] + n, stroke[i] + n, stroke[i] - n, stroke[i - 1] - n });
        }
    }

    private void RenderRegionEditor(DrawingContext dc)
    {
        if (!IsRegionEditing || !HasImage) return;
        var scale = RegionScale; var cyan = new SolidColorBrush(Color.FromRgb(20, 218, 246));
        dc.PushTransform(new MatrixTransform(Mat.Matrix));
        var fill = new SolidColorBrush(Color.FromArgb(28, 20, 218, 246));
        var pen = new Pen(cyan, 1.6 / scale);
        if (_maskCommit != null)
        {
            var red = new SolidColorBrush(Color.FromArgb(95, 255, 65, 65));
            foreach (var stroke in _maskStrokes)
            {
                foreach (var p in stroke) dc.DrawEllipse(red, null, p, _maskRadius, _maskRadius);
                for (var i = 1; i < stroke.Count; i++) dc.DrawLine(new Pen(red, _maskRadius * 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, stroke[i - 1], stroke[i]);
            }
        }
        else if (_regionDraft != null)
        {
            var draft = _regionDraft;
            if (draft.Type == "ellipse2d")
            {
                dc.PushTransform(new RotateTransform(draft.Angle, draft.Center.X, draft.Center.Y));
                dc.DrawEllipse(fill, pen, draft.Center, draft.Width / 2, draft.Height / 2); dc.Pop();
            }
            else if (draft.Type is "circle2d" or "ring2d" or "sector2d")
            {
                if (draft.Type == "circle2d") dc.DrawEllipse(fill, pen, draft.Center, draft.Radius, draft.Radius);
                else if (draft.Type == "ring2d")
                { dc.DrawEllipse(null, pen, draft.Center, draft.Radius, draft.Radius); dc.DrawEllipse(null, pen, draft.Center, draft.InnerRadius, draft.InnerRadius); }
                else DrawPath(draft.Outline(), true);
            }
            else if (draft.Type is "point2d" or "origin" or "fiducialpoint2d")
            {
                var length = 32 / scale; var angle = draft.Angle * Math.PI / 180;
                dc.DrawLine(new Pen(Brushes.IndianRed, 2 / scale), draft.Center, draft.Center + new Vector(Math.Cos(angle), Math.Sin(angle)) * length);
                dc.DrawLine(new Pen(Brushes.LimeGreen, 2 / scale), draft.Center, draft.Center + new Vector(-Math.Sin(angle), Math.Cos(angle)) * length);
            }
            else DrawPath(draft.Outline(), draft.Type is "rect2d" || draft.Type == "polygon2d" && !_regionDrawing);
            foreach (var handle in draft.Handles(scale))
            {
                if (handle.Kind == "rotate") dc.DrawEllipse(Brushes.Orange, new Pen(Brushes.White, 1 / scale), handle.Position, 4 / scale, 4 / scale);
                else if (handle.Kind == "move")
                { dc.DrawLine(pen, handle.Position - new Vector(6 / scale, 0), handle.Position + new Vector(6 / scale, 0)); dc.DrawLine(pen, handle.Position - new Vector(0, 6 / scale), handle.Position + new Vector(0, 6 / scale)); }
                else dc.DrawRectangle(Brushes.White, pen, new Rect(handle.Position.X - 3.5 / scale, handle.Position.Y - 3.5 / scale, 7 / scale, 7 / scale));
            }
        }
        dc.Pop();
        void DrawPath(IReadOnlyList<Point> points, bool closed)
        {
            if (points.Count < 2) return;
            var geometry = new StreamGeometry(); using (var context = geometry.Open()) { context.BeginFigure(points[0], closed, closed); context.PolyLineTo(points.Skip(1).ToArray(), true, false); }
            dc.DrawGeometry(closed ? fill : null, pen, geometry);
        }
    }

    private static string RegionTypeLabel(string type) => type switch
    { "rect2d" => "矩形区域", "ellipse2d" => "椭圆区域", "circle2d" => "圆形区域", "polygon2d" => "多边形区域", "trace2d" => "折线轨迹", "ring2d" => "圆环区域", "sector2d" => "扇环区域", "segment2d" or "caliper" => "线段 / 卡尺", "point2d" => "图像选点", _ => "原点与方向" };

    private sealed class RegionEditSurface(ImageView owner) : FrameworkElement
    { protected override void OnRender(DrawingContext drawingContext) => owner.RenderRegionEditor(drawingContext); }

    private sealed class RegionDraft
    {
        public string Type;
        public Point Center, A, B;
        public double Width, Height, Angle, Radius, InnerRadius, StartAngle, AngleRange = 90;
        public List<Point> Points = new();
        public bool IsValid => Type switch
        {
            "rect2d" or "ellipse2d" => Width > .1 && Height > .1,
            "circle2d" => Radius > .1,
            "ring2d" or "sector2d" => Radius > .1 && InnerRadius >= 0 && InnerRadius < Radius && (Type != "sector2d" || Math.Abs(AngleRange) > .1),
            "polygon2d" => Points.Count >= 3 && Math.Abs(Points.Select((p, i) => p.X * Points[(i + 1) % Points.Count].Y - Points[(i + 1) % Points.Count].X * p.Y).Sum()) > .2,
            "trace2d" => Points.Count >= 2 && Points.Zip(Points.Skip(1), (a, b) => (b - a).Length).Sum() > .1,
            "segment2d" or "caliper" => (B - A).Length > .1,
            _ => true
        };
        public RegionDraft Copy() => new() { Type = Type, Center = Center, A = A, B = B, Width = Width, Height = Height, Angle = Angle, Radius = Radius, InnerRadius = InnerRadius, StartAngle = StartAngle, AngleRange = AngleRange, Points = Points.ToList() };
        public static RegionDraft Read(string type, IReadOnlyDictionary<string, object> raw)
        {
            if (raw == null || raw.Count == 0) return null;
            var shape = Group(raw, type) ?? raw;
            if (type == "caliper") shape = Group(raw, "segment2d") ?? shape;
            var draft = new RegionDraft { Type = type, Center = PointAt(shape, "Center"), A = PointAt(shape, "Start"), B = PointAt(shape, "End"), Angle = Number(shape, "Rotation", Number(shape, "Angle")), Radius = Number(shape, "Radius", Number(shape, "Outer Radius")), InnerRadius = Number(shape, "Inner Radius"), StartAngle = Number(shape, "Start Angle"), AngleRange = Number(shape, "Angle Range", 90) };
            var size = Group(shape, "Size"); draft.Width = Number(size, "Width"); draft.Height = Number(size, "Height");
            if (type == "ellipse2d") { draft.Width = Number(shape, "Long Axis"); draft.Height = Number(shape, "Short Axis"); }
            if (type is "point2d" or "origin" or "fiducialpoint2d") draft.Center = shape.ContainsKey("Point") ? PointAt(shape, "Point") : PointAt(shape);
            if (shape.TryGetValue("Points", out var points) && points is IEnumerable list)
                foreach (var item in list) if (item is IReadOnlyDictionary<string, object> point) draft.Points.Add(PointAt(point));
            return draft;
        }
        public void Draw(Point start, Point end)
        {
            A = start; B = end;
            if (Type is "rect2d" or "ellipse2d") { Center = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2); Width = Math.Abs(end.X - start.X); Height = Math.Abs(end.Y - start.Y); Angle = 0; }
            else if (Type is "circle2d" or "ring2d" or "sector2d") { Center = start; Radius = (end - start).Length; InnerRadius = Type == "ring2d" ? Radius * .5 : 0; StartAngle = 0; AngleRange = 90; }
        }
        public List<(Point Position, string Kind, int Index)> Handles(double scale)
        {
            var handles = new List<(Point, string, int)>();
            if (Type is "polygon2d" or "trace2d")
            {
                for (var i = 0; i < Points.Count; i++) handles.Add((Points[i], "vertex", i));
                if (Points.Count > 0) handles.Add((new Point(Points.Average(p => p.X), Points.Average(p => p.Y)), "move", 0));
                return handles;
            }
            if (Type is "rect2d" or "ellipse2d")
            {
                var corners = Outline(); for (var i = 0; i < corners.Count; i++) handles.Add((corners[i], "corner", i));
                var offset = Rotate(new Vector(0, -Height / 2 - 24 / scale), Angle); handles.Add((Center + offset, "rotate", 0));
            }
            else if (Type is "circle2d" or "ring2d" or "sector2d")
            {
                var angle = Type == "sector2d" ? StartAngle + AngleRange / 2 : 0;
                handles.Add((At(Radius, angle), "radius", 0));
                if (Type != "circle2d") handles.Add((At(InnerRadius, angle), "inner", 0));
                if (Type == "sector2d") { handles.Add((At(Radius, StartAngle), "start", 0)); handles.Add((At(Radius, StartAngle + AngleRange), "end", 0)); }
            }
            else if (Type is "segment2d" or "caliper") { handles.Add((A, "a", 0)); handles.Add((B, "b", 0)); handles.Add((new Point((A.X + B.X) / 2, (A.Y + B.Y) / 2), "move", 0)); return handles; }
            else if (Type is "origin" or "fiducialpoint2d") handles.Add((Center + Rotate(new Vector(40 / scale, 0), Angle), "rotate", 0));
            handles.Add((Center, "move", 0)); return handles;
        }
        public void Drag(int handleIndex, Point start, Point current, double scale)
        {
            var handles = Handles(scale); if (handleIndex < 0 || handleIndex >= handles.Count) return;
            var handle = handles[handleIndex]; var delta = current - start;
            switch (handle.Kind)
            {
                case "move": Center += delta; A += delta; B += delta; Points = Points.Select(p => p + delta).ToList(); break;
                case "vertex": Points[handle.Index] = current; break;
                case "a": A = current; break;
                case "b": B = current; break;
                case "rotate": Angle = Math.Atan2(current.Y - Center.Y, current.X - Center.X) * 180 / Math.PI + (Type is "rect2d" or "ellipse2d" ? 90 : 0); break;
                case "corner":
                {
                    var opposite = Outline()[(handle.Index + 2) % 4]; var local = Rotate(current - opposite, -Angle);
                    Center = new Point((current.X + opposite.X) / 2, (current.Y + opposite.Y) / 2); Width = Math.Abs(local.X); Height = Math.Abs(local.Y); break;
                }
                case "radius": Radius = Math.Max(InnerRadius + .1, (current - Center).Length); break;
                case "inner": InnerRadius = Math.Clamp((current - Center).Length, 0, Math.Max(0, Radius - .1)); break;
                case "start":
                {
                    var end = StartAngle + AngleRange; StartAngle = Math.Atan2(current.Y - Center.Y, current.X - Center.X) * 180 / Math.PI;
                    AngleRange = PositiveAngle(end - StartAngle); break;
                }
                case "end": AngleRange = PositiveAngle(Math.Atan2(current.Y - Center.Y, current.X - Center.X) * 180 / Math.PI - StartAngle); break;
            }
        }
        private static double PositiveAngle(double angle) { angle %= 360; return angle <= 0 ? angle + 360 : angle; }
        private Point At(double radius, double angle) => Center + Rotate(new Vector(radius, 0), angle);
        public IReadOnlyList<Point> Outline()
        {
            if (Type is "rect2d" or "ellipse2d") return new[] { new Vector(-Width / 2, -Height / 2), new Vector(Width / 2, -Height / 2), new Vector(Width / 2, Height / 2), new Vector(-Width / 2, Height / 2) }.Select(v => Center + Rotate(v, Angle)).ToArray();
            if (Type is "polygon2d" or "trace2d") return Points;
            if (Type == "sector2d") return Enumerable.Range(0, 61).Select(i => At(Radius, StartAngle + AngleRange * i / 60)).Concat(Enumerable.Range(0, 61).Reverse().Select(i => At(InnerRadius, StartAngle + AngleRange * i / 60))).ToArray();
            return new[] { A, B };
        }
        public bool Contains(Point p, double tolerance)
        {
            if (Type == "rect2d") { var v = Rotate(p - Center, -Angle); return Math.Abs(v.X) <= Width / 2 + tolerance && Math.Abs(v.Y) <= Height / 2 + tolerance; }
            if (Type == "ellipse2d") { var v = Rotate(p - Center, -Angle); return v.X * v.X / Math.Pow(Width / 2 + tolerance, 2) + v.Y * v.Y / Math.Pow(Height / 2 + tolerance, 2) <= 1; }
            if (Type is "circle2d" or "ring2d" or "sector2d") return (p - Center).Length <= Radius + tolerance;
            if (Type == "polygon2d" && Points.Count > 2)
            {
                var inside = false; for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
                    if ((Points[i].Y > p.Y) != (Points[j].Y > p.Y) && p.X < (Points[j].X - Points[i].X) * (p.Y - Points[i].Y) / (Points[j].Y - Points[i].Y) + Points[i].X) inside = !inside;
                return inside;
            }
            if (Type is "segment2d" or "caliper")
            { var v = B - A; var t = v.LengthSquared > 1e-9 ? Math.Clamp(Vector.Multiply(p - A, v) / v.LengthSquared, 0, 1) : 0; return (p - (A + v * t)).Length <= tolerance; }
            return (p - Center).Length <= tolerance;
        }
        public Dictionary<string, object> ToNative()
        {
            var shape = new Dictionary<string, object>();
            if (Type == "rect2d") { shape["Center"] = XY(Center); shape["Size"] = new Dictionary<string, object> { ["Width"] = Width, ["Height"] = Height }; shape["Rotation"] = Angle; }
            else if (Type == "ellipse2d") { shape["Center"] = XY(Center); shape["Long Axis"] = Width; shape["Short Axis"] = Height; shape["Rotation"] = Angle; }
            else if (Type is "circle2d" or "ring2d" or "sector2d")
            {
                shape["Center"] = XY(Center);
                if (Type == "circle2d") shape["Radius"] = Radius;
                else { shape["Inner Radius"] = InnerRadius; shape["Outer Radius"] = Radius; }
                if (Type == "sector2d") { shape["Start Angle"] = StartAngle; shape["Angle Range"] = AngleRange; }
            }
            else if (Type is "polygon2d" or "trace2d") shape["Points"] = Points.Select(p => (object)XY(p)).ToList();
            else if (Type is "segment2d" or "caliper") { shape["Start"] = XY(A); shape["End"] = XY(B); }
            else if (Type == "fiducialpoint2d") { shape["Point"] = XY(Center); shape["Angle"] = Angle; shape["ScaleX"] = 1d; shape["ScaleY"] = 1d; }
            else { shape = XY(Center); if (Type == "origin") { shape["Angle"] = Angle; shape["Auto"] = false; } }
            return new Dictionary<string, object> { ["Type"] = Type, [Type] = shape };
        }
        public static Dictionary<string, object> Polygon(IEnumerable<Point> points) => new() { ["Type"] = "polygon2d", ["polygon2d"] = new Dictionary<string, object> { ["Points"] = points.Select(p => (object)XY(p)).ToList() } };
        private static Dictionary<string, object> XY(Point p) => new() { ["X"] = p.X, ["Y"] = p.Y };
        private static IReadOnlyDictionary<string, object> Group(IReadOnlyDictionary<string, object> source, string key) => source != null && source.TryGetValue(key, out var value) ? value as IReadOnlyDictionary<string, object> : null;
        private static Point PointAt(IReadOnlyDictionary<string, object> source, string key = null) { var group = key == null ? source : Group(source, key); return new Point(Number(group, "X"), Number(group, "Y")); }
        private static double Number(IReadOnlyDictionary<string, object> source, string key, double fallback = 0) => source != null && source.TryGetValue(key, out var value) && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result) ? result : fallback;
        private static Vector Rotate(Vector vector, double degrees) { var radians = degrees * Math.PI / 180; return new Vector(vector.X * Math.Cos(radians) - vector.Y * Math.Sin(radians), vector.X * Math.Sin(radians) + vector.Y * Math.Cos(radians)); }
    }
}
