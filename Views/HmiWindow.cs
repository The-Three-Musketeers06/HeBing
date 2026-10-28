using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AiVisionStudio.Models;
using AiVisionStudio.Services;

namespace AiVisionStudio.Views;

/// <summary>A native HMI host preserving the original document's canvas coordinates.</summary>
public sealed partial class HmiWindow : Window
{
    private readonly IHmiRuntimeContext _runtime;
    private readonly DockPanel _root = new();
    private readonly ComboBox _pages = new() { MinWidth = 180, Margin = new Thickness(10, 4, 8, 4) };
    private readonly CheckBox _design = new() { Content = "设计模式", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
    private readonly TextBlock _status = new() { Margin = new Thickness(12, 5, 10, 5), Foreground = Brushes.LightGray, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Viewbox _viewbox = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly DispatcherTimer _timer;
    private readonly List<Window> _subwindows = [];
    private bool _closed;
    private bool _locked;
    private bool _rebuildingSurface;
    public HmiDocument Document { get; }
    public string SuggestedSavePath { get; set; }
    public HmiSurface Surface { get; private set; }
    public bool IsDesignMode { get => _design.IsChecked == true; set => _design.IsChecked = value; }
    public int CurrentPageIndex => Surface?.Page.Index ?? -1;
    public IReadOnlyList<string> RenderErrors => Surface?.Errors ?? (IReadOnlyList<string>)Array.Empty<string>();
    public HmiWindow(IHmiRuntimeContext runtime, HmiDocument document)
    {
        _runtime = runtime; Document = document ?? throw new ArgumentNullException(nameof(document));
        Editor = new HmiEditorSession(Document);
        Title = "HMI · " + Path.GetFileNameWithoutExtension(document.SourcePath);
        Width = 1280; Height = 800; MinWidth = 640; MinHeight = 420;
        Background = HmiVisuals.Brush("#171719"); Foreground = Brushes.White; FontFamily = new FontFamily("Microsoft YaHei UI");
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Background = HmiVisuals.Brush("#303034"), Height = 42 };
        toolbar.Children.Add(_pages); toolbar.Children.Add(_design);
        toolbar.Children.Add(Button("保存布局", (_, _) => SaveDocument()));
        toolbar.Children.Add(Button("刷新", (_, _) => RefreshRuntime()));
        toolbar.Children.Add(Button("全屏", (_, _) => ToggleFullScreen(this)));
        toolbar.Children.Add(Button("解锁", (_, _) => { _locked = false; _design.IsEnabled = true; if (Surface != null) Surface.IsHitTestVisible = true; SetStatus("已解锁"); }));
        DockPanel.SetDock(toolbar, Dock.Top); _root.Children.Add(toolbar);
        DockPanel.SetDock(_status, Dock.Bottom); _root.Children.Add(_status);
        _root.Children.Add(new Border { Child = _viewbox, Padding = new Thickness(0), ClipToBounds = true }); Content = _root;
        foreach (var page in Document.Pages.Where(x => !x.IsSubwindow)) _pages.Items.Add(new ComboBoxItem { Content = string.IsNullOrWhiteSpace(page.Title) ? "看板 " + (page.Index + 1) : page.Title, Tag = page });
        // Window shortcuts must run before canvas shortcuts: Escape exits full screen
        // without also clearing the current selection or cancelling an edit.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F11 || e.Key == Key.Escape && FullScreenWindows.TryGetValue(this, out var state))
            {
                ToggleFullScreen(this);
                e.Handled = true;
            }
        };
        InitializeEditor();
        _pages.SelectionChanged += (_, _) => { if (!IsDesignMode) Rebuild(); }; _design.Checked += (_, _) => SwitchEditorMode(); _design.Unchecked += (_, _) => SwitchEditorMode();
        if (_pages.Items.Count > 0) _pages.SelectedIndex = 0;
        else if (Document.Pages.Count > 0) ShowPage(Document.Pages[0]);
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshRuntime();
        if (_runtime != null) _runtime.Changed += RuntimeChanged;
        Loaded += (_, _) => { RefreshRuntime(); _timer.Start(); };
        Closed += (_, _) => { _closed = true; _timer.Stop(); if (_runtime != null) _runtime.Changed -= RuntimeChanged; foreach (var w in _subwindows.ToArray()) w.Close(); Surface?.Dispose(); };
    }
    internal static Button Button(string label, RoutedEventHandler click)
    {
        var button = new Button { Content = label, Margin = new Thickness(3), Padding = new Thickness(12, 5, 12, 5), Background = HmiVisuals.Brush("#3a3a40"), Foreground = Brushes.White, BorderBrush = HmiVisuals.Brush("#505058") };
        button.Click += click; return button;
    }
    private void RuntimeChanged(object sender, EventArgs args)
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshRuntime));
    }
    private void Rebuild()
    {
        if (IsDesignMode) { if (Editor.CurrentPage != null) ShowPage(Editor.CurrentPage); return; }
        if (_pages.SelectedItem is ComboBoxItem { Tag: HmiPage page }) ShowPage(page);
        else if (Surface != null) ShowPage(Surface.Page);
    }
    public void ShowPage(HmiPage page)
    {
        if (_rebuildingSurface) return;
        _rebuildingSurface = true;
        try
        {
        if (Surface?.CancelPointerEdit() == true) page = Document.Pages.ElementAtOrDefault(page.Index) ?? Document.Pages.First();
        _viewbox.Child = null;
        DetachEditorSurface();
        Surface?.Dispose();
        Surface = new HmiSurface(Document, page, _runtime, IsDesignMode, HandleAction, SetStatus, IsDesignMode ? Editor : null);
        Surface.IsHitTestVisible = !_locked;
        if (IsDesignMode) AttachEditorSurface(); else _viewbox.Child = Surface;
        RefreshRuntime();
        }
        finally { _rebuildingSurface = false; }
    }
    public void NavigatePage(int regularIndex)
    {
        if (regularIndex < 0 || regularIndex >= _pages.Items.Count) { SetStatus("看板不存在：" + regularIndex); return; }
        _pages.SelectedIndex = regularIndex;
    }
    public void RefreshRuntime()
    {
        if (_closed) return;
        Surface?.Refresh();
        foreach (var window in _subwindows) if (window.Content is Viewbox { Child: HmiSurface s }) s.Refresh();
        if (Surface?.Errors.Count > 0) SetStatus(Surface.Errors[^1]);
        else _status.Text = IsDesignMode ? "设计模式 · Ctrl 多选 · 拖动/方向键移动 · Ctrl+S 保存 · Ctrl+Z 撤销" : _runtime?.Status ?? "运行模式";
    }
    private void SetStatus(string message) { _status.Text = message; if (_editorStatus != null) _editorStatus.Text = message; }
    public void SaveDocument(string path = null)
    {
        if (IsDesignMode && Inspector?.CommitPendingEdits() == false) { SetStatus("保存未完成：请先修正组件配置中的无效值。"); return; }
        try { if (string.IsNullOrWhiteSpace(path ?? Document.SourcePath)) { SaveEditorAs(); return; } Editor.Save(path ?? Document.SourcePath); UpdateEditorState(); SetStatus("已保存：" + Document.SourcePath); }
        catch (Exception ex) { SetStatus("保存失败：" + ex.Message); }
    }
    internal async Task HandleAction(string action, HmiElement element, string target = null)
    {
        try
        {
            if (IsDesignMode) return;
            if (action == "bulletinBoard") { NavigatePage(int.TryParse(target, out var i) ? i : 0); return; }
            if (action is "subwindow" or "SubwindowButton" or "openSubwindow") { OpenSubwindow((int)HmiVisuals.Number(target, element?.GetNumber("option.bindSubwindowsIndex", 0) ?? 0)); return; }
            if (action is "baselineCropFree" or "twotoneZoomOutMap" or "twotoneZoomInMap" or "fullscreen" or "fullScreen") { ToggleFullScreen(this); return; }
            if (action is "outlineClear" or "close" or "closeSubwindow") { Close(); return; }
            if (action is "IcRoundLockOpen" or "lockScreen") { _locked = true; _design.IsEnabled = false; if (Surface != null) Surface.IsHitTestVisible = false; SetStatus("界面已锁定，点击工具栏“解锁”恢复操作"); return; }
            if ((action == "save" || action == "saveParams") && Surface?.CommitInputs() == false) return;
            if (_runtime != null) await _runtime.ExecuteAsync(action, element);
            RefreshRuntime();
        }
        catch (Exception ex) { SetStatus("操作失败：" + ex.Message); }
    }
    public void OpenSubwindow(int subwindowIndex)
    {
        var page = Document.Pages.Where(x => x.IsSubwindow).ElementAtOrDefault(subwindowIndex);
        if (page == null) { SetStatus("子窗口不存在：" + subwindowIndex); return; }
        var work = SystemParameters.WorkArea;
        var window = new Window { Title = page.Title ?? "子窗口", Owner = IsLoaded ? this : null, Width = Math.Clamp(page.GetNumber("editCanvasConfig.attr.w", page.Width), 120, work.Width), Height = Math.Clamp(page.GetNumber("editCanvasConfig.attr.h", page.Height), 100, work.Height), Background = HmiVisuals.Brush(page.Background), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var surface = new HmiSurface(Document, page, _runtime, IsDesignMode, async (action, element, target) =>
        {
            if (action is "outlineClear" or "close" or "closeSubwindow") { window.Close(); return; }
            if (action is "baselineCropFree" or "fullscreen" or "fullScreen") { ToggleFullScreen(window); return; }
            await HandleAction(action, element, target);
        }, SetStatus);
        window.Content = new Viewbox { Stretch = Stretch.Uniform, Child = surface };
        window.Closed += (_, _) => { surface.Dispose(); _subwindows.Remove(window); }; _subwindows.Add(window); window.Show();
    }
    private sealed record FullScreenState(WindowStyle Style, WindowState State);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, FullScreenState> FullScreenWindows = new();
    private static void ToggleFullScreen(Window window)
    {
        if (FullScreenWindows.TryGetValue(window, out var previous)) { FullScreenWindows.Remove(window); window.WindowStyle = previous.Style; window.WindowState = previous.State; }
        else { FullScreenWindows.Add(window, new FullScreenState(window.WindowStyle, window.WindowState)); window.WindowStyle = WindowStyle.None; window.WindowState = WindowState.Maximized; }
    }
}

public sealed partial class HmiSurface : Canvas, IDisposable
{
    private readonly IHmiRuntimeContext _runtime;
    private readonly HmiDocument _document;
    private readonly Func<string, HmiElement, string, Task> _action;
    private readonly Action<string> _status;
    private readonly List<Action> _refresh = [];
    private readonly List<Action> _dispose = [];
    private readonly List<Func<bool>> _commit = [];
    private Border _selected;
    public HmiPage Page { get; }
    public bool IsDesignMode { get; }
    public List<string> Errors { get; } = [];
    public List<string> UnsupportedTypes { get; } = [];
    public Dictionary<string, FrameworkElement> ComponentViews { get; } = [];
    public HmiSurface(HmiDocument document, HmiPage page, IHmiRuntimeContext runtime, bool design, Func<string, HmiElement, string, Task> action = null, Action<string> status = null, HmiEditorSession editor = null)
    {
        _document = document; Page = page; _runtime = runtime; IsDesignMode = design; _action = action; _status = status;
        _editor = editor;
        Width = Math.Max(1, page.Width); Height = Math.Max(1, page.Height); Background = HmiVisuals.Brush(page.Background, "#232324"); ClipToBounds = true;
        var bg = HmiVisuals.Text(page.Raw?["editCanvasConfig"]?["backgroundImage"]);
        if (!page.GetBoolean("editCanvasConfig.selectColor", false) && !string.IsNullOrEmpty(bg)) try { var bitmap = HmiVisuals.Bitmap(bg, document.SourcePath); if (bitmap != null) Background = new ImageBrush(bitmap) { Stretch = Stretch.Fill }; } catch (Exception ex) { Errors.Add("画布背景：" + ex.Message); }
        foreach (var element in page.Elements) AddElement(this, element);
        if (design && editor != null) InitializeDesigner();
        Refresh();
    }
    private void AddElement(Canvas canvas, HmiElement element)
    {
        try
        {
            FrameworkElement child;
            if (element.IsGroup || element.Type == "group")
            {
                var group = new Canvas(); foreach (var nested in element.Children) AddElement(group, nested); child = group;
            }
            else child = CreateComponent(element);
            var frame = new Border { Child = child, Width = Math.Max(1, element.Width), Height = Math.Max(1, element.Height), ClipToBounds = true, Background = Brushes.Transparent, Tag = element, Visibility = element.Hidden ? Visibility.Collapsed : Visibility.Visible };
            SetLeft(frame, element.X); SetTop(frame, element.Y); SetZIndex(frame, element.ZIndex); canvas.Children.Add(frame); ComponentViews[element.Id] = child;
            if (IsDesignMode) { if (_editor != null) AttachEditorDrag(canvas, frame, element); else AttachDesigner(canvas, frame, element); }
        }
        catch (Exception ex)
        {
            Errors.Add(element.Type + " " + element.Id + ": " + ex.Message);
            var text = new TextBlock { Text = element.Title + "\n" + ex.Message, Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, Width = Math.Max(1, element.Width), Height = Math.Max(1, element.Height) };
            SetLeft(text, element.X); SetTop(text, element.Y); canvas.Children.Add(text);
        }
    }
    private void AttachDesigner(Canvas canvas, Border frame, HmiElement element)
    {
        Point origin = default; double startX = 0, startY = 0; bool moving = false;
        frame.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (element.Locked) return;
            if (_selected != null) { _selected.BorderThickness = new Thickness(0); }
            _selected = frame; frame.BorderBrush = HmiVisuals.Brush("#03a9f4"); frame.BorderThickness = new Thickness(1);
            origin = e.GetPosition(canvas); startX = element.X; startY = element.Y; moving = true; frame.CaptureMouse(); e.Handled = true;
        };
        frame.PreviewMouseMove += (_, e) =>
        {
            if (!moving || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(canvas); MoveElement(element.Id, startX + p.X - origin.X, startY + p.Y - origin.Y);
        };
        frame.PreviewMouseLeftButtonUp += (_, e) => { moving = false; frame.ReleaseMouseCapture(); e.Handled = true; };
        frame.LostMouseCapture += (_, _) => moving = false;
        var resize = new Thumb { Width = 12, Height = 12, Background = HmiVisuals.Brush("#03a9f4"), Cursor = Cursors.SizeNWSE, Opacity = .7 };
        SetLeft(resize, element.X + element.Width - 12); SetTop(resize, element.Y + element.Height - 12); SetZIndex(resize, element.ZIndex + 1); canvas.Children.Add(resize);
        resize.DragDelta += (_, e) => { if (!element.Locked) ResizeElement(element.Id, element.Width + e.HorizontalChange, element.Height + e.VerticalChange); };
        frame.SizeChanged += (_, _) => { SetLeft(resize, element.X + frame.Width - 12); SetTop(resize, element.Y + frame.Height - 12); };
        _refresh.Add(() => { SetLeft(resize, element.X + element.Width - 12); SetTop(resize, element.Y + element.Height - 12); });
        var menu = new ContextMenu(); var properties = new MenuItem { Header = "组件属性" }; properties.Click += (_, _) => EditProperties(element); menu.Items.Add(properties); frame.ContextMenu = menu;
    }
    public void MoveElement(string id, double x, double y)
    {
        if (!IsDesignMode) return;
        var frame = FindFrame(this, id); if (frame?.Tag is not HmiElement e || e.Locked) return;
        e.X = Math.Round(x); e.Y = Math.Round(y); SetLeft(frame, e.X); SetTop(frame, e.Y); Refresh();
    }
    public void ResizeElement(string id, double width, double height)
    {
        if (!IsDesignMode) return;
        var frame = FindFrame(this, id); if (frame?.Tag is not HmiElement e || e.Locked) return;
        e.Width = Math.Max(12, Math.Round(width)); e.Height = Math.Max(12, Math.Round(height)); frame.Width = e.Width; frame.Height = e.Height;
    }
    public void SetElementVisible(string id, bool visible)
    {
        var frame = FindFrame(this, id); if (frame != null) frame.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
    private static Border FindFrame(Canvas canvas, string id)
    {
        foreach (var frame in canvas.Children.OfType<Border>()) { if (frame.Tag is HmiElement e && e.Id == id) return frame; if (frame.Child is Canvas group && FindFrame(group, id) is { } found) return found; } return null;
    }
    private void EditProperties(HmiElement element)
    {
        var dialog = new Window { Title = element.Title + " · 组件属性", Width = 560, Height = 650, Owner = Window.GetWindow(this), Background = HmiVisuals.Brush("#262628"), Foreground = Brushes.White, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(14) }; var fields = new StackPanel();
        var hint = new TextBlock { Text = element.Type, Foreground = Brushes.LightGray, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap }; DockPanel.SetDock(hint, Dock.Top); panel.Children.Add(hint);
        TextBox Field(string label, string value)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) }; row.ColumnDefinitions.Add(new() { Width = new GridLength(100) }); row.ColumnDefinitions.Add(new());
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }); var input = new TextBox { Text = value, Padding = new Thickness(5), Background = HmiVisuals.Brush("#38383d"), Foreground = Brushes.White }; Grid.SetColumn(input, 1); row.Children.Add(input); fields.Children.Add(row); return input;
        }
        var title = Field("组件名称", element.Title); var x = Field("X", element.X.ToString()); var y = Field("Y", element.Y.ToString()); var width = Field("宽度", element.Width.ToString()); var height = Field("高度", element.Height.ToString());
        var text = Field("文字", element.Option?["dataset"] is JsonValue ? element.GetString("option.dataset") : element.GetString("option.label", element.GetString("option.data")));
        var foreground = Field("文字颜色", element.Foreground); var background = Field("背景颜色", element.Background); var font = Field("字号", element.FontSize.ToString());
        var binding = Field("数据绑定", string.Join(";", HmiVisuals.References(element)));
        var editor = new TextBox { Text = element.Option?.ToJsonString(new() { WriteIndented = true }), Height = 200, AcceptsReturn = true, AcceptsTab = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Background = HmiVisuals.Brush("#202022"), Foreground = Brushes.White, FontFamily = new FontFamily("Consolas") };
        var advanced = new Expander { Header = "高级组件配置", Content = editor, Margin = new Thickness(0, 12, 0, 0) }; fields.Children.Add(advanced);
        var save = HmiWindow.Button("应用", (_, _) =>
        {
            try
            {
                if (!double.TryParse(x.Text, out var px) || !double.TryParse(y.Text, out var py) || !double.TryParse(width.Text, out var pw) || !double.TryParse(height.Text, out var ph) || pw < 12 || ph < 12 || !double.TryParse(font.Text, out var fs) || fs <= 0) throw new InvalidDataException("请输入有效坐标、尺寸和字号");
                if (advanced.IsExpanded) element.Raw["option"] = JsonNode.Parse(editor.Text) as JsonObject ?? throw new InvalidDataException("高级配置必须为对象");
                else
                {
                    if (element.Option?["dataset"] is JsonValue || element.Type.StartsWith("Text")) element.SetNode("option.dataset", JsonValue.Create(text.Text));
                    else if (element.Option?.ContainsKey("label") == true) element.SetNode("option.label", JsonValue.Create(text.Text));
                    else if (element.Option?.ContainsKey("data") == true) element.SetNode("option.data", JsonValue.Create(text.Text));
                    element.SetNode("option.fontColor", JsonValue.Create(foreground.Text)); element.SetNode("option.backgroundColor", JsonValue.Create(background.Text)); element.SetNode("option.fontSize", JsonValue.Create(fs));
                    element.SetNode("option.parameter", new JsonArray(binding.Text.Split(';').Select(s => (JsonNode)JsonValue.Create(s.Trim())).ToArray()));
                }
                element.Title = title.Text; MoveElement(element.Id, px, py); ResizeElement(element.Id, pw, ph);
                var frame = FindFrame(this, element.Id); if (frame != null) { frame.Child = CreateComponent(element); ComponentViews[element.Id] = (FrameworkElement)frame.Child; }
                Refresh(); dialog.Close();
            }
            catch (Exception ex) { hint.Text = ex.Message; }
        }); DockPanel.SetDock(save, Dock.Bottom); panel.Children.Add(save); panel.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); dialog.Content = panel; dialog.ShowDialog();
    }
    public void Refresh()
    {
        foreach (var update in _refresh) try { update(); } catch (Exception ex) { var message = "刷新：" + ex.Message; if (!Errors.Contains(message)) Errors.Add(message); }
    }
    public bool CommitInputs() { var ok = true; foreach (var commit in _commit) ok &= commit(); return ok; }
    internal void Report(string message) { _status?.Invoke(message); }
    internal void EditDesign(string label, Action mutation) { if (!IsDesignMode) return; if (_editor != null) _editor.Execute(label, mutation); else { mutation(); Refresh(); } }
    internal async Task Act(string action, HmiElement element, string target = null)
    {
        if (IsDesignMode) return;
        try { if (_action != null) await _action(action, element, target); else if (_runtime != null) await _runtime.ExecuteAsync(action, element); }
        catch (Exception ex) { Report("操作失败：" + ex.Message); }
    }
    public void Dispose() { CancelPointerEdit(); foreach (var dispose in _dispose) dispose(); _dispose.Clear(); _refresh.Clear(); }
    private FrameworkElement CreateComponent(HmiElement element) => HmiComponents.Create(this, element, _document, _runtime, _refresh, _dispose, _commit);
}
