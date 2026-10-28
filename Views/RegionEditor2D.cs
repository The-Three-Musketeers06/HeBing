using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AiVisionStudio.Models;

namespace AiVisionStudio.Views;

public sealed class RegionEditor2D : FrameworkElement
{
    private MatrixTransform _imageTransform;
    private EditableRegion2D _region, _before;
    private bool _creating, _dragging;
    private string _handle;
    private Point _origin;
    private Point _pendingPoint;
    private bool _dragUpdatePending;
    private readonly List<Point> _polygon = new();
    private Point _hover;
    private DrawingGroup _drawing;
    private double _drawingScale = double.NaN;
    public double ImageWidth { get; set; }
    public double ImageHeight { get; set; }
    public bool IsEditing { get; private set; }
    public bool IsInteracting => _dragging || _polygon.Count > 0 || IsEditing && _creating;
    public Func<EditableRegion2D, bool> Commit { get; set; }
    public MatrixTransform ImageTransform
    {
        get => _imageTransform;
        set { if (_imageTransform != null) _imageTransform.Changed -= TransformChanged; _imageTransform = value; if (value != null) value.Changed += TransformChanged; InvalidateRegion(); }
    }
    private void TransformChanged(object sender, EventArgs e)
    {
        if (Math.Abs(Scale - _drawingScale) > .0000001 || double.IsNaN(_drawingScale)) InvalidateRegion();
    }
    private void InvalidateRegion() { _drawing = null; InvalidateVisual(); }
    private double Scale => Math.Max(.0001, Math.Abs(_imageTransform?.Matrix.M11 ?? 1));
    public RegionEditor2D() { IsHitTestVisible = false; Focusable = true; ClipToBounds = true; }
    public void ShowRegion(EditableRegion2D region, bool edit, bool redraw = false)
    {
        CancelDragUpdate();
        _region = region?.Clone(); IsEditing = edit; IsHitTestVisible = edit;
        _creating = redraw || region?.IsValid != true; _polygon.Clear(); _dragging = false;
        Cursor = edit ? Cursors.Cross : null; InvalidateRegion();
    }
    public void Synchronize(EditableRegion2D region)
    {
        if (IsInteracting) return;
        _region = region.Clone(); _creating = !region.IsValid; InvalidateRegion();
    }
    public void Redraw()
    {
        if (_region == null) return;
        CancelDragUpdate();
        if (_dragging && _before != null) _region = _before;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _creating = true; _polygon.Clear(); InvalidateRegion(); Focus();
    }
    public void StopEditing(bool clear = false)
    {
        CancelDragUpdate();
        if (_dragging && _before != null) _region = _before;
        _dragging = false; _creating = false; _polygon.Clear(); if (IsMouseCaptured) ReleaseMouseCapture();
        IsEditing = false; IsHitTestVisible = false; Cursor = null;
        if (clear) _region = null;
        InvalidateRegion();
    }
    private bool ImagePoint(Point view, out Point point)
    {
        var matrix = _imageTransform?.Matrix ?? Matrix.Identity;
        if (!matrix.HasInverse || ImageWidth <= 0 || ImageHeight <= 0) { point = default; return false; }
        matrix.Invert(); point = matrix.Transform(view);
        point = new Point(Math.Clamp(point.X, 0, ImageWidth - 1), Math.Clamp(point.Y, 0, ImageHeight - 1)); return true;
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (IsEditing) dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (_drawing == null)
        {
            _drawingScale = Scale;
            _drawing = new DrawingGroup { Transform = _imageTransform ?? Transform.Identity };
            using var content = _drawing.Open();
            DrawRegion(content);
        }
        dc.DrawDrawing(_drawing);
    }
    private void DrawRegion(DrawingContext dc)
    {
        if (_region == null) return;
        var pen = new Pen(Brushes.LimeGreen, 1.5 / Scale);
        var fill = new SolidColorBrush(Color.FromArgb(24, 32, 220, 90));
        if (_polygon.Count > 0)
        {
            var points = _polygon.Append(_hover).ToArray();
            for (int i = 1; i < points.Length; i++) dc.DrawLine(pen, points[i - 1], points[i]);
        }
        else if ((!_creating || _dragging) && _region.IsValid)
        {
            dc.DrawGeometry(fill, pen, _region.Geometry());
            foreach (var caliper in _region.Calipers()) dc.DrawGeometry(null, pen, caliper);
            if (IsEditing && !_creating)
                foreach (var handle in _region.Handles(Scale)) dc.DrawRectangle(Brushes.White, pen, new Rect(handle.Position.X - 4 / Scale, handle.Position.Y - 4 / Scale, 8 / Scale, 8 / Scale));
        }
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEditing || _region == null || !ImagePoint(e.GetPosition(this), out var point)) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        if (_creating && _region.Kind == "polygon2d")
        {
            e.Handled = true; Focus();
            if (e.ClickCount > 1) { FinishPolygon(); return; }
            _polygon.Add(point); _hover = point; InvalidateRegion(); return;
        }
        _handle = null;
        if (!_creating)
        {
            var hit = _region.Handles(Scale).OrderBy(handle => (handle.Position - point).Length).FirstOrDefault();
            if ((hit.Position - point).Length * Scale <= 9) _handle = hit.Key;
            _handle ??= _region.EdgeHandle(point, 6 / Scale);
            if (_handle == null && !_region.Geometry().FillContains(point) && !_region.Geometry().StrokeContains(new Pen(Brushes.Black, 12 / Scale), point)) return;
        }
        _before = _region.Clone(); _origin = point; _dragging = true;
        if (_creating) _region.DrawFrom(point, point);
        InvalidateRegion();
        Focus(); CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsEditing || !ImagePoint(e.GetPosition(this), out var point)) return;
        if (_polygon.Count > 0) { _hover = point; InvalidateRegion(); e.Handled = true; return; }
        if (!_dragging) return;
        _pendingPoint = point;
        if (!_dragUpdatePending)
        {
            _dragUpdatePending = true;
            CompositionTarget.Rendering += OnDragRendering;
        }
        e.Handled = true;
    }
    private void OnDragRendering(object sender, EventArgs e)
    {
        CancelDragUpdate();
        if (_dragging) UpdateDrag(_pendingPoint);
    }
    private void UpdateDrag(Point point)
    {
        _region = _before.Clone();
        if (_creating) _region.DrawFrom(_origin, point);
        else if (_handle == null || _handle == "move") _region.Translate(point - _origin);
        else _region.MoveHandle(_handle, point, Scale);
        InvalidateRegion();
    }
    private void CancelDragUpdate()
    {
        if (_dragUpdatePending) CompositionTarget.Rendering -= OnDragRendering;
        _dragUpdatePending = false;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        CancelDragUpdate();
        if (ImagePoint(e.GetPosition(this), out var point))
        {
            if (!_creating && (point - _origin).Length * Scale < .25)
            {
                _region = _before;
                _dragging = false; ReleaseMouseCapture(); InvalidateRegion(); e.Handled = true; return;
            }
            UpdateDrag(point);
        }
        FinishGesture(); e.Handled = true;
    }
    private bool FinishGesture()
    {
        bool accepted = _region.IsValid && Commit?.Invoke(_region.Clone()) == true;
        if (!accepted && _before != null) _region = _before;
        if (accepted) _creating = false;
        _dragging = false; _polygon.Clear(); if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateRegion(); return accepted;
    }
    public bool FinishPolygon()
    {
        if (_polygon.Count < 3 || _region == null) return false;
        _before = _region.Clone(); _region.SetPoints(_polygon); return FinishGesture();
    }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (_polygon.Count == 0) return;
        FinishPolygon(); e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        CancelDragUpdate();
        if (_dragging) { _region = _before; _dragging = false; InvalidateRegion(); }
        base.OnLostMouseCapture(e);
    }
}
