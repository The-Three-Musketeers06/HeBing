using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AiVisionStudio.Models;
using AiVisionStudio.Services;

namespace AiVisionStudio.Views;

public sealed partial class HmiSurface
{
    private readonly HmiEditorSession _editor;
    private readonly List<UIElement> _selectionChrome = [];
    private Rectangle _marquee;
    private Point _marqueeOrigin;
    private string[] _marqueeStart = [];
    private bool _selectingArea;
    private bool _editingPointer;
    private HmiEditTransaction _pointerTransaction;
    public bool SnapToGrid { get; set; }
    public double GridSize { get; set; } = 10;

    private void InitializeDesigner()
    {
        Focusable = true;
        ContextMenu = CreateEditorContextMenu(null);
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.Handled || _editingPointer) return;
            Focus(); _marqueeOrigin = e.GetPosition(this); _marqueeStart = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? _editor.SelectedIds.ToArray() : [];
            if (_marqueeStart.Length == 0) _editor.ClearSelection();
            _selectingArea = true;
            _marquee = new Rectangle { Stroke = HmiVisuals.Brush("#69c0ef"), StrokeThickness = 1, Fill = HmiVisuals.Brush("#5599cc22"), IsHitTestVisible = false };
            SetZIndex(_marquee, int.MaxValue); Children.Add(_marquee); CaptureMouse(); e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            if (!_selectingArea) return;
            var rect = new Rect(_marqueeOrigin, e.GetPosition(this)); SetLeft(_marquee, rect.Left); SetTop(_marquee, rect.Top); _marquee.Width = rect.Width; _marquee.Height = rect.Height;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!_selectingArea) return;
            var rect = new Rect(_marqueeOrigin, e.GetPosition(this));
            var ids = Page.Elements.Where(x => !x.Hidden && rect.IntersectsWith(new Rect(x.X, x.Y, x.Width, x.Height))).Select(x => x.Id);
            _selectingArea = false; Children.Remove(_marquee); _marquee = null; ReleaseMouseCapture();
            _editor.Select(_marqueeStart.Concat(rect.Width > 2 || rect.Height > 2 ? ids : [])); e.Handled = true;
        };
        LostMouseCapture += (_, _) => { if (_selectingArea) { _selectingArea = false; if (_marquee != null) Children.Remove(_marquee); _marquee = null; } };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _pointerTransaction != null) { var transaction = _pointerTransaction; _pointerTransaction = null; _editingPointer = false; ReleaseMouseCapture(); transaction.Cancel(); e.Handled = true; } };
        Loaded += (_, _) => RefreshSelection();
    }

    private void AttachEditorDrag(Canvas parent, Border frame, HmiElement element)
    {
        Point origin = default;
        var starts = new Dictionary<string, Point>();
        bool moving = false;
        frame.ContextMenu = CreateEditorContextMenu(element.Id);
        frame.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.Handled || Keyboard.IsKeyDown(Key.Space)) return;
            if (element.Children.Count > 0 && e.OriginalSource is DependencyObject hit)
            {
                var child = NearestElement(hit);
                if (child != null && child.Id != element.Id && _editor.SelectedIds.Contains(child.Id)) return;
            }
            // A double click enters a group; the first click still selects/moves the group as a unit.
            if ((element.IsGroup || element.Children.Count > 0) && e.ClickCount >= 2 && e.OriginalSource is DependencyObject source)
            {
                var nested = NearestElement(source);
                if (nested != null && nested.Id != element.Id) { _editor.Select(nested.Id); e.Handled = true; return; }
            }
            Focus();
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (_editor.SelectedIds.Contains(element.Id)) _editor.Select(_editor.SelectedIds.Where(id => id != element.Id));
                else _editor.Select(element.Id, true);
                e.Handled = true; return;
            }
            if (!_editor.SelectedIds.Contains(element.Id)) _editor.Select(element.Id);
            if (!_editor.IsEditable(element)) { e.Handled = true; return; }
            origin = e.GetPosition(this); starts.Clear();
            foreach (var selected in SelectedRoots().Where(_editor.IsEditable)) starts[selected.Id] = new Point(selected.X, selected.Y);
            moving = true; _editingPointer = true; frame.CaptureMouse(); e.Handled = true;
        };
        frame.PreviewMouseMove += (_, e) =>
        {
            if (!moving || e.LeftButton != MouseButtonState.Pressed) return;
            var delta = e.GetPosition(this) - origin;
            if (_pointerTransaction == null && delta.Length < 2) return;
            _pointerTransaction ??= _editor.BeginTransaction("拖动组件");
            var dx = Math.Round(delta.X); var dy = Math.Round(delta.Y);
            if (SnapToGrid && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { dx = Math.Round(dx / GridSize) * GridSize; dy = Math.Round(dy / GridSize) * GridSize; }
            foreach (var selected in SelectedRoots().Where(_editor.IsEditable)) if (starts.TryGetValue(selected.Id, out var start)) { selected.X = start.X + dx; selected.Y = start.Y + dy; }
            SynchronizeFrames(); RefreshSelection(); e.Handled = true;
        };
        void EndMove()
        {
            if (!moving) return;
            moving = false; _editingPointer = false; var transaction = _pointerTransaction; _pointerTransaction = null;
            frame.ReleaseMouseCapture(); transaction?.Commit();
        }
        frame.PreviewMouseLeftButtonUp += (_, e) => { if (moving) { EndMove(); e.Handled = true; } };
        frame.LostMouseCapture += (_, _) => EndMove();
    }

    private IEnumerable<HmiElement> SelectedRoots()
    {
        var selection = _editor.SelectedElements;
        var descendants = selection.SelectMany(x => x.Children.SelectMany(y => y.DescendantsAndSelf())).Select(x => x.Id).ToHashSet();
        return selection.Where(x => !descendants.Contains(x.Id));
    }
    private void SynchronizeFrames()
    {
        foreach (var element in Page.AllElements)
        {
            var frame = FindFrame(this, element.Id); if (frame == null) continue;
            SetLeft(frame, element.X); SetTop(frame, element.Y); frame.Width = Math.Max(1, element.Width); frame.Height = Math.Max(1, element.Height);
        }
    }
    public void RefreshSelection()
    {
        if (_editor == null || !IsDesignMode) return;
        // During a thumb drag its mouse capture must stay on the same visual.
        if (_resizing) return;
        foreach (var item in _selectionChrome) Children.Remove(item); _selectionChrome.Clear();
        foreach (var element in SelectedRoots())
        {
            var frame = FindFrame(this, element.Id); if (frame == null) continue;
            var p = frame.TranslatePoint(new Point(), this);
            var outline = new Rectangle { Width = element.Width, Height = element.Height, Stroke = HmiVisuals.Brush(element.Locked ? "#e6b156" : "#70c6ef"), StrokeThickness = 1.2, IsHitTestVisible = false };
            AddChrome(outline, p.X, p.Y);
            if (_editingPointer || !_editor.IsEditable(element)) continue;
            foreach (var (hx, hy) in new[] { (0, 0), (1, 0), (2, 0), (0, 1), (2, 1), (0, 2), (1, 2), (2, 2) })
            {
                var thumb = new Thumb { Width = 8, Height = 8, Background = Brushes.White, BorderBrush = HmiVisuals.Brush("#5fb4df"), BorderThickness = new Thickness(1), Cursor = hx == 1 ? Cursors.SizeNS : hy == 1 ? Cursors.SizeWE : hx == hy ? Cursors.SizeNWSE : Cursors.SizeNESW };
                AddChrome(thumb, p.X + hx * element.Width / 2 - 4, p.Y + hy * element.Height / 2 - 4);
                double sx = 0, sy = 0, sw = 0, sh = 0, accumulatedX = 0, accumulatedY = 0;
                thumb.DragStarted += (_, _) => { _resizing = true; _pointerTransaction = _editor.BeginTransaction("调整组件尺寸"); sx = element.X; sy = element.Y; sw = element.Width; sh = element.Height; accumulatedX = accumulatedY = 0; };
                thumb.DragDelta += (_, e) =>
                {
                    accumulatedX += e.HorizontalChange; accumulatedY += e.VerticalChange;
                    var dx = Math.Round(accumulatedX); var dy = Math.Round(accumulatedY);
                    var minW = Math.Max(1, element.GetNumber("attr.minWidth", 12)); var minH = Math.Max(1, element.GetNumber("attr.minHeight", 12));
                    var w = hx == 1 ? sw : Math.Max(minW, sw + (hx == 0 ? -dx : dx)); var h = hy == 1 ? sh : Math.Max(minH, sh + (hy == 0 ? -dy : dy));
                    _editor.ResizeElement(element.Id, hx == 0 ? sx + sw - w : sx, hy == 0 ? sy + sh - h : sy, w, h);
                    SynchronizeFrames();
                };
                thumb.DragCompleted += (_, e) => { _resizing = false; var transaction = _pointerTransaction; _pointerTransaction = null; if (e.Canceled) transaction?.Cancel(); else transaction?.Commit(); RefreshSelection(); };
            }
        }
    }
    private bool _resizing;
    public bool CancelPointerEdit()
    {
        if (_pointerTransaction == null) return false;
        var transaction = _pointerTransaction; _pointerTransaction = null; _editingPointer = false; _resizing = false;
        Mouse.Capture(null); transaction.Cancel(); return true;
    }
    private static HmiElement NearestElement(DependencyObject visual)
    {
        while (visual != null)
        {
            if (visual is Border { Tag: HmiElement element }) return element;
            visual = visual is Visual || visual is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(visual) : LogicalTreeHelper.GetParent(visual);
        }
        return null;
    }
    private void AddChrome(UIElement item, double x, double y) { SetLeft(item, x); SetTop(item, y); SetZIndex(item, int.MaxValue - 1); Children.Add(item); _selectionChrome.Add(item); }

    internal ContextMenu CreateEditorContextMenu(string id)
    {
        var menu = new ContextMenu(); if (_editor == null) return menu;
        menu.Opened += (_, _) => { if (id != null && !_editor.SelectedIds.Contains(id)) _editor.Select(id); };
        menu.Items.Add(HmiWindow.Menu("复制  Ctrl+C", () => { var text = _editor.Copy(); if (text.Length > 0) try { Clipboard.SetText(text); } catch { } }));
        menu.Items.Add(HmiWindow.Menu("剪切  Ctrl+X", () => { if (_editor.Cut()) try { Clipboard.SetText(_editor.ClipboardText); } catch { } }));
        menu.Items.Add(HmiWindow.Menu("粘贴  Ctrl+V", () => { try { if (Clipboard.ContainsText()) _editor.Paste(Clipboard.GetText()); else _editor.Paste(); } catch (Exception ex) { Report(ex.Message); } }));
        menu.Items.Add(HmiWindow.Menu("复制一份  Ctrl+D", () => _editor.DuplicateSelection()));
        menu.Items.Add(new Separator());
        menu.Items.Add(HmiWindow.Menu("组合  Ctrl+G", () => _editor.GroupSelection())); menu.Items.Add(HmiWindow.Menu("解除组合  Ctrl+Shift+G", () => _editor.UngroupSelection()));
        var align = new MenuItem { Header = "对齐" };
        foreach (var (label, mode) in new[] { ("左对齐", HmiAlignment.Left), ("水平居中", HmiAlignment.HorizontalCenter), ("右对齐", HmiAlignment.Right), ("顶对齐", HmiAlignment.Top), ("垂直居中", HmiAlignment.VerticalCenter), ("底对齐", HmiAlignment.Bottom) }) align.Items.Add(HmiWindow.Menu(label, () => _editor.Align(mode, _editor.SelectedElements.Count == 1)));
        align.Items.Add(HmiWindow.Menu("水平等间距", () => _editor.Distribute(true))); align.Items.Add(HmiWindow.Menu("垂直等间距", () => _editor.Distribute(false))); menu.Items.Add(align);
        menu.Items.Add(HmiWindow.Menu("置于顶层", () => _editor.BringToFront())); menu.Items.Add(HmiWindow.Menu("置于底层", () => _editor.SendToBack()));
        menu.Items.Add(HmiWindow.Menu("锁定 / 解锁  Ctrl+L", () => _editor.SetLocked(!_editor.SelectedElements.All(x => x.Locked))));
        menu.Items.Add(HmiWindow.Menu("显示 / 隐藏", () => _editor.SetHidden(!_editor.SelectedElements.All(x => x.Hidden))));
        menu.Items.Add(new Separator()); menu.Items.Add(HmiWindow.Menu("删除  Delete", () => _editor.DeleteSelection()));
        return menu;
    }
}
