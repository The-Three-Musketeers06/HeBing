using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AiVisionStudio.Models;
using AiVisionStudio.Services;

namespace AiVisionStudio.Views;

public sealed partial class HmiWindow
{
    public HmiEditorSession Editor { get; private set; }
    public HmiInspector Inspector { get; private set; }
    public double EditorZoom { get; private set; } = .6;
    public bool ConfirmUnsavedChanges { get; set; } = true;
    private DockPanel _editorRoot;
    private Border _editorCanvas;
    private ScrollViewer _editorScroll;
    private TextBlock _editorStatus;
    private TextBlock _documentTitle;
    private StackPanel _pageStrip;
    private TreeView _layers;
    private ListBox _history;
    private WrapPanel _componentCards;
    private StackPanel _componentCategories;
    private TextBox _componentSearch;
    private Slider _zoomSlider;
    private TextBlock _zoomText;
    private Button _undoButton, _redoButton;
    private HmiComponentCatalog _catalog;
    private string _package, _category;
    private bool _updatingEditor, _fitZoom = true;
    private HmiRuler _horizontalRuler, _verticalRuler;
    private Point? _panOrigin;
    private double _panX, _panY;

    private void InitializeEditor()
    {
        Editor.Changed += (_, _) => { if (!_closed && IsDesignMode && !_rebuildingSurface) { Rebuild(); UpdateEditorState(); } };
        Editor.SelectionChanged += (_, _) => { if (!_closed && IsDesignMode && !_rebuildingSurface) { Surface?.RefreshSelection(); UpdateInspector(); UpdateLayerSelection(); } };
        PreviewKeyDown += EditorKeyDown;
        Closing += (_, e) =>
        {
            if (!ConfirmUnsavedChanges) return;
            var valid = Inspector?.CommitPendingEdits() != false;
            if (valid && !Editor.IsDirty) return;
            var result = MessageBox.Show(this, "HMI 布局已修改，是否保存？", "HMI 编辑器", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel) e.Cancel = true;
            else if (result == MessageBoxResult.Yes) { SaveDocument(); e.Cancel = !valid || Editor.IsDirty; }
        };
    }

    private void SwitchEditorMode()
    {
        if (FullScreenWindows.TryGetValue(this, out var fullScreenState)) ToggleFullScreen(this);
        if (IsDesignMode)
        {
            foreach (var subwindow in _subwindows.ToArray()) subwindow.Close();
            if (_editorRoot == null) BuildEditorChrome();
            WindowStyle = WindowStyle.None;
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
            var pageIndex = Surface?.Page.Index ?? 0;
            Editor.SelectPage(pageIndex);
            Content = _editorRoot;
            MinWidth = 1000; MinHeight = 620;
            Rebuild(); UpdateEditorState();
            Dispatcher.BeginInvoke(new Action(() => { if (_fitZoom) FitCanvas(); }));
        }
        else
        {
            if (Inspector?.CommitPendingEdits() == false) { _design.IsChecked = true; SetStatus("请先修正组件配置中的无效值。"); return; }
            Content = _root;
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, null);
            WindowStyle = WindowStyle.SingleBorderWindow;
            MinWidth = 640; MinHeight = 420;
            _pages.Items.Clear();
            foreach (var page in Document.MainPages) _pages.Items.Add(new ComboBoxItem { Content = page.Title, Tag = page });
            if (_pages.Items.Count > 0) _pages.SelectedIndex = Math.Max(0, Document.MainPages.ToList().FindIndex(p => p == Editor.CurrentPage));
            else Rebuild();
        }
    }

    private void BuildEditorChrome()
    {
        _catalog = HmiComponentCatalog.Load();
        _editorRoot = new DockPanel { Background = HmiVisuals.Brush("#19191c") };
        InstallEditorStyles(_editorRoot.Resources);
        var header = new Grid { Height = 54, Background = HmiVisuals.Brush("#18181b") };
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 7, 0, 7), HorizontalAlignment = HorizontalAlignment.Left };
        _undoButton = Tool("↶", "撤销 Ctrl+Z", () => Editor.Undo()); commands.Children.Add(_undoButton);
        _redoButton = Tool("↷", "重做 Ctrl+Y", () => Editor.Redo()); commands.Children.Add(_redoButton);
        commands.Children.Add(Tool("⇩", "打开 HMI 布局", OpenEditorDocument));
        commands.Children.Add(Tool("▣", "保存 Ctrl+S", () => SaveDocument()));
        commands.Children.Add(Tool("⇧", "另存为 Ctrl+Shift+S", SaveEditorAs));
        commands.Children.Add(Tool("⧉", "复制 Ctrl+C", CopyEditorSelection));
        commands.Children.Add(Tool("▤", "粘贴 Ctrl+V", PasteEditorSelection));
        commands.Children.Add(Tool("▶", "预览 / 运行 F5", () => IsDesignMode = false));
        commands.Children.Add(Tool("⛶", "全屏 F11", () => ToggleFullScreen(this)));
        _documentTitle = new TextBlock { Text = "ES-Vision-HMI V1", Foreground = Brushes.LightGray, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        header.Children.Add(_documentTitle); header.Children.Add(commands);
        var captions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) };
        captions.Children.Add(Tool("−", "最小化", () => WindowState = WindowState.Minimized));
        captions.Children.Add(Tool("□", "最大化 / 还原", ToggleEditorMaximize));
        captions.Children.Add(Tool("×", "关闭", Close)); header.Children.Add(captions);
        header.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is DependencyObject source && IsInButton(source)) return; if (e.ClickCount == 2) ToggleEditorMaximize(); else if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        DockPanel.SetDock(header, Dock.Top); _editorRoot.Children.Add(header);

        var bottom = new Grid { Height = 34, Background = HmiVisuals.Brush("#28282b") };
        bottom.ColumnDefinitions.Add(new()); bottom.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _editorStatus = new TextBlock { Foreground = Brushes.LightGray, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 6, 8, 4) }; bottom.Children.Add(_editorStatus);
        var zoom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 12, 0) };
        zoom.Children.Add(Button("适应画布", (_, _) => FitCanvas()));
        var snap = new CheckBox { Content = "网格吸附", Foreground = Brushes.LightGray, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        snap.Checked += (_, _) => { _snapEnabled = true; if (Surface != null) Surface.SnapToGrid = true; }; snap.Unchecked += (_, _) => { _snapEnabled = false; if (Surface != null) Surface.SnapToGrid = false; }; zoom.Children.Add(snap);
        _zoomText = new TextBlock { Width = 50, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        zoom.Children.Add(_zoomText);
        _zoomSlider = new Slider { Minimum = 10, Maximum = 200, Width = 140, VerticalAlignment = VerticalAlignment.Center, Value = EditorZoom * 100, SmallChange = 5, LargeChange = 10 };
        _zoomSlider.ValueChanged += (_, _) => { if (!_updatingEditor) SetEditorZoom(_zoomSlider.Value / 100); }; zoom.Children.Add(_zoomSlider);
        Grid.SetColumn(zoom, 1); bottom.Children.Add(zoom); DockPanel.SetDock(bottom, Dock.Bottom); _editorRoot.Children.Add(bottom);

        var layout = new Grid(); layout.ColumnDefinitions.Add(new() { Width = new GridLength(20, GridUnitType.Star), MinWidth = 290 });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(5) }); layout.ColumnDefinitions.Add(new() { Width = new GridLength(60, GridUnitType.Star), MinWidth = 230 });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(5) }); layout.ColumnDefinitions.Add(new() { Width = new GridLength(20, GridUnitType.Star), MinWidth = 290 });
        var leftTabs = new TabControl { BorderThickness = new Thickness(0), Background = HmiVisuals.Brush("#222225") };
        leftTabs.Items.Add(new TabItem { Header = "组件", Content = BuildPalette() });
        _layers = new TreeView { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.LightGray };
        var layerPanel = new DockPanel(); var layerTools = new WrapPanel();
        layerTools.Children.Add(Button("组合", (_, _) => Editor.GroupSelection())); layerTools.Children.Add(Button("解组", (_, _) => Editor.UngroupSelection()));
        layerTools.Children.Add(Button("上移", (_, _) => Editor.MoveLayer(1))); layerTools.Children.Add(Button("下移", (_, _) => Editor.MoveLayer(-1)));
        DockPanel.SetDock(layerTools, Dock.Bottom); layerPanel.Children.Add(layerTools); layerPanel.Children.Add(_layers);
        leftTabs.Items.Add(new TabItem { Header = "图层", Content = layerPanel });
        _history = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.LightGray };
        leftTabs.Items.Add(new TabItem { Header = "历史记录", Content = _history }); layout.Children.Add(leftTabs);
        foreach (var col in new[] { 1, 3 }) { var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = HmiVisuals.Brush("#303035") }; Grid.SetColumn(splitter, col); layout.Children.Add(splitter); }

        var center = new DockPanel { Background = DotGrid() }; Grid.SetColumn(center, 2); layout.Children.Add(center);
        var pageArea = new DockPanel { LastChildFill = true, Background = HmiVisuals.Brush("#222225") };
        var addPage = new Menu { Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
        var add = new MenuItem { Header = "＋" }; add.Items.Add(Menu("新建看板", () => Editor.AddPage())); add.Items.Add(Menu("新建子窗口", () => Editor.AddPage(isSubwindow: true))); addPage.Items.Add(add);
        DockPanel.SetDock(addPage, Dock.Right); pageArea.Children.Add(addPage);
        _pageStrip = new StackPanel { Orientation = Orientation.Horizontal };
        pageArea.Children.Add(new ScrollViewer { Content = _pageStrip, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        DockPanel.SetDock(pageArea, Dock.Top); center.Children.Add(pageArea);
        var stage = new Grid(); stage.RowDefinitions.Add(new() { Height = new GridLength(22) }); stage.RowDefinitions.Add(new()); stage.ColumnDefinitions.Add(new() { Width = new GridLength(22) }); stage.ColumnDefinitions.Add(new());
        _horizontalRuler = new HmiRuler(); Grid.SetColumn(_horizontalRuler, 1); stage.Children.Add(_horizontalRuler);
        _verticalRuler = new HmiRuler { Vertical = true }; Grid.SetRow(_verticalRuler, 1); stage.Children.Add(_verticalRuler);
        _editorCanvas = new Border { Margin = new Thickness(45), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, BorderBrush = HmiVisuals.Brush("#57575d"), BorderThickness = new Thickness(1) };
        _editorScroll = new ScrollViewer { Content = _editorCanvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, CanContentScroll = false };
        _editorScroll.ScrollChanged += (_, _) => UpdateRulers();
        _editorScroll.SizeChanged += (_, _) => { if (_fitZoom) FitCanvas(); };
        _editorScroll.PreviewMouseWheel += (_, e) => { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { SetEditorZoom(EditorZoom * (e.Delta > 0 ? 1.1 : 1 / 1.1)); e.Handled = true; } };
        _editorScroll.PreviewMouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle || Keyboard.IsKeyDown(Key.Space)) { _panOrigin = e.GetPosition(_editorScroll); _panX = _editorScroll.HorizontalOffset; _panY = _editorScroll.VerticalOffset; _editorScroll.CaptureMouse(); e.Handled = true; } };
        _editorScroll.PreviewMouseMove += (_, e) => { if (_panOrigin is Point start) { var p = e.GetPosition(_editorScroll); _editorScroll.ScrollToHorizontalOffset(_panX + start.X - p.X); _editorScroll.ScrollToVerticalOffset(_panY + start.Y - p.Y); } };
        _editorScroll.PreviewMouseUp += (_, e) => { if (_panOrigin != null) { _panOrigin = null; _editorScroll.ReleaseMouseCapture(); e.Handled = true; } };
        _editorScroll.LostMouseCapture += (_, _) => _panOrigin = null;
        _editorScroll.AllowDrop = true;
        _editorScroll.DragOver += (_, e) => { if (e.Data.GetDataPresent("ES-Vision-HMI-Component")) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
        _editorScroll.Drop += (_, e) => { if (e.Data.GetData("ES-Vision-HMI-Component") is string key && Surface != null) { var p = e.GetPosition(Surface); AddComponent(key, p.X, p.Y); e.Handled = true; } };
        Grid.SetRow(_editorScroll, 1); Grid.SetColumn(_editorScroll, 1); stage.Children.Add(_editorScroll); center.Children.Add(stage);
        Inspector = new HmiInspector { ApplyEdit = (label, action) => Editor.Execute(label, action), AlignSelection = mode => Editor.Align(mode, Editor.SelectedElements.Count == 1), SetDimension = SetSelectedDimension };
        Grid.SetColumn(Inspector, 4); layout.Children.Add(Inspector); _editorRoot.Children.Add(layout);
    }

    private FrameworkElement BuildPalette()
    {
        var root = new DockPanel();
        _componentSearch = new TextBox { Margin = new Thickness(7), Padding = new Thickness(7), ToolTip = "搜索组件名称", Background = HmiVisuals.Brush("#38383b"), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
        var searchRow = new Grid(); searchRow.Children.Add(_componentSearch);
        var searchHint = new TextBlock { Text = "搜索组件  ⌕", IsHitTestVisible = false, Foreground = HmiVisuals.Brush("#88888d"), Margin = new Thickness(15, 12, 10, 8) }; searchRow.Children.Add(searchHint);
        _componentSearch.TextChanged += (_, _) => { searchHint.Visibility = _componentSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; RefreshPalette(); }; DockPanel.SetDock(searchRow, Dock.Top); root.Children.Add(searchRow);
        var packages = new StackPanel { Width = 58, Background = HmiVisuals.Brush("#29292c") };
        foreach (var package in _catalog.Components.GroupBy(c => c.Package))
        {
            var key = package.Key; var label = package.First().PackageName;
            var b = Button(label, (_, _) => { _package = key; _category = null; RefreshPalette(); }); b.Padding = new Thickness(3, 14, 3, 14); b.Margin = new Thickness(3); packages.Children.Add(b);
        }
        _package = _catalog.Components.FirstOrDefault()?.Package;
        DockPanel.SetDock(packages, Dock.Left); root.Children.Add(packages);
        _componentCategories = new StackPanel { Width = 61 }; DockPanel.SetDock(_componentCategories, Dock.Left); root.Children.Add(_componentCategories);
        _componentCards = new WrapPanel();
        _componentCards.SizeChanged += (_, _) => { var width = Math.Max(66, (_componentCards.ActualWidth - 13) / 2); foreach (var card in _componentCards.Children.OfType<Button>()) card.Width = width; };
        root.Children.Add(new ScrollViewer { Content = _componentCards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        RefreshPalette(); return root;
    }

    private void RefreshPalette()
    {
        if (_componentCards == null) return;
        var query = _componentSearch.Text.Trim();
        var entries = _catalog.Components.Where(c => query.Length > 0 || c.Package == _package).ToList();
        _componentCategories.Children.Clear();
        void Category(string key, string label) { var b = Button(label, (_, _) => { _category = key; RefreshPalette(); }); b.Padding = new Thickness(2, 8, 2, 8); b.Margin = new Thickness(2); b.FontSize = 11; b.BorderThickness = new Thickness(0); if (_category == key) b.Foreground = HmiVisuals.Brush("#60b4e3"); _componentCategories.Children.Add(b); }
        Category(null, "全部"); foreach (var c in entries.GroupBy(c => c.Category)) Category(c.Key, c.First().CategoryName);
        _componentCards.Children.Clear();
        foreach (var c in entries.Where(c => query.Length > 0 ? (c.Title + c.Key).Contains(query, StringComparison.OrdinalIgnoreCase) : _category == null || c.Category == _category))
        {
            var key = c.Key; var card = new StackPanel();
            if (!string.IsNullOrEmpty(c.ThumbnailPath) && File.Exists(c.ThumbnailPath))
                try { card.Children.Add(new Image { Source = new BitmapImage(new Uri(Path.GetFullPath(c.ThumbnailPath))), Height = 53, Stretch = Stretch.Uniform }); } catch { }
            if (card.Children.Count == 0) card.Children.Add(new TextBlock { Text = "▧", Foreground = HmiVisuals.Brush("#5faed2"), FontSize = 28, Height = 53, TextAlignment = TextAlignment.Center });
            card.Children.Add(new TextBlock { Text = c.Title, FontSize = 10, Margin = new Thickness(1, 4, 1, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            var b = new Button { Content = card, Width = Math.Max(66, (_componentCards.ActualWidth - 13) / 2), Margin = new Thickness(3), Padding = new Thickness(4), Background = HmiVisuals.Brush("#29292d"), Foreground = Brushes.LightGray, BorderThickness = new Thickness(0), ToolTip = c.Title + " · 双击添加或拖入画布" };
            b.MouseDoubleClick += (_, e) => { AddComponent(key); e.Handled = true; };
            Point start = default;
            b.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(b);
            b.PreviewMouseMove += (_, e) => { var p = e.GetPosition(b); if (e.LeftButton == MouseButtonState.Pressed && (p - start).Length > 5) DragDrop.DoDragDrop(b, new DataObject("ES-Vision-HMI-Component", key), DragDropEffects.Copy); };
            _componentCards.Children.Add(b);
        }
    }

    public HmiElement AddComponent(string key, double? x = null, double? y = null)
    {
        var element = (_catalog ??= HmiComponentCatalog.Load()).Create(key);
        if (element == null) return null;
        element.X = Math.Round(x ?? Math.Max(0, (Editor.CurrentPage.Width - element.Width) / 2));
        element.Y = Math.Round(y ?? Math.Max(0, (Editor.CurrentPage.Height - element.Height) / 2));
        return Editor.AddElement(element);
    }

    private void DetachEditorSurface() { if (_editorCanvas != null) _editorCanvas.Child = null; }
    private void AttachEditorSurface()
    {
        if (_editorCanvas == null) return;
        Surface.SnapToGrid = _snapEnabled;
        Surface.LayoutTransform = new ScaleTransform(EditorZoom, EditorZoom); _editorCanvas.Child = Surface;
        Surface.RefreshSelection(); UpdateRulers();
    }
    public void SetEditorZoom(double value)
    {
        _fitZoom = false; EditorZoom = Math.Clamp(value, .1, 2);
        if (IsDesignMode && Surface != null) Surface.LayoutTransform = new ScaleTransform(EditorZoom, EditorZoom);
        if (_zoomSlider != null) { _updatingEditor = true; _zoomSlider.Value = EditorZoom * 100; _updatingEditor = false; _zoomText.Text = Math.Round(EditorZoom * 100) + "%"; }
        UpdateRulers();
    }
    public void FitCanvas()
    {
        if (_editorScroll?.ViewportWidth > 0 && Editor.CurrentPage != null) SetEditorZoom(Math.Min((_editorScroll.ViewportWidth - 92) / Editor.CurrentPage.Width, (_editorScroll.ViewportHeight - 92) / Editor.CurrentPage.Height));
        _fitZoom = true;
    }
    private void UpdateRulers()
    {
        if (_horizontalRuler == null) return;
        _horizontalRuler.Zoom = _verticalRuler.Zoom = EditorZoom;
        _horizontalRuler.Offset = 46 - _editorScroll.HorizontalOffset; _verticalRuler.Offset = 46 - _editorScroll.VerticalOffset;
        _horizontalRuler.InvalidateVisual(); _verticalRuler.InvalidateVisual();
    }
    private void UpdateEditorState()
    {
        Title = "ES-Vision-HMI V1" + (Editor.IsDirty ? " *" : "") + " · " + Path.GetFileNameWithoutExtension(Document.SourcePath);
        if (_editorRoot == null) return;
        _undoButton.IsEnabled = Editor.CanUndo; _redoButton.IsEnabled = Editor.CanRedo;
        _undoButton.ToolTip = "撤销 " + Editor.UndoLabel + " (Ctrl+Z)"; _redoButton.ToolTip = "重做 " + Editor.RedoLabel + " (Ctrl+Y)";
        _documentTitle.Text = "ES-Vision-HMI V1" + (Editor.IsDirty ? " *" : "");
        _editorStatus.Text = (string.IsNullOrEmpty(Document.SourcePath) ? "新建 HMI" : "工程地址：" + Document.SourcePath) + (Editor.IsDirty ? " · 未保存" : "");
        _pageStrip.Children.Clear();
        foreach (var page in Document.Pages)
        {
            var index = page.Index;
            var b = Button((page.IsSubwindow ? "▣ " : "") + page.Title, (_, _) => { Editor.SelectPage(index); Rebuild(); UpdateEditorState(); });
            if (page == Editor.CurrentPage) b.BorderBrush = HmiVisuals.Brush("#54a6cc");
            var menu = new ContextMenu(); menu.Items.Add(Menu("重命名", () => RenameEditorPage(index))); menu.Items.Add(Menu("复制页面", () => { Editor.SelectPage(index); Editor.DuplicatePage(); }));
            menu.Items.Add(Menu("删除页面", () => { Editor.SelectPage(index); Editor.DeletePage(); })); b.ContextMenu = menu; _pageStrip.Children.Add(b);
        }
        _layers.Items.Clear();
        if (Editor.CurrentPage != null) foreach (var element in Editor.CurrentPage.Elements.OrderByDescending(e => e.ZIndex)) _layers.Items.Add(LayerItem(element));
        _history.Items.Clear();
        foreach (var label in Editor.UndoHistory) _history.Items.Add("↶ " + label);
        foreach (var label in Editor.RedoHistory) _history.Items.Add("↷ " + label);
        if (_history.Items.Count == 0) _history.Items.Add("没有待撤销的修改");
        UpdateInspector(); UpdateLayerSelection();
    }
    private TreeViewItem LayerItem(HmiElement element)
    {
        var row = new DockPanel(); var label = new TextBlock { Text = element.Title, Width = 154, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(label);
        var eye = Tool(element.Hidden ? "○" : "◉", "显示 / 隐藏", () => { Editor.Select([element.Id]); Editor.SetHidden(!element.Hidden); }); eye.Width = 29; eye.Padding = new Thickness(2); row.Children.Add(eye);
        var padlock = Tool(element.Locked ? "▣" : "◇", "锁定 / 解锁", () => { Editor.Select([element.Id]); Editor.SetLocked(!element.Locked); }); padlock.Width = 29; padlock.Padding = new Thickness(2); row.Children.Add(padlock);
        var item = new TreeViewItem { Header = row, Tag = element.Id, IsExpanded = true, Foreground = Brushes.LightGray, Padding = new Thickness(0, 2, 0, 2) };
        label.MouseLeftButtonDown += (_, e) => { Editor.Select([element.Id], Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); e.Handled = true; };
        item.ContextMenu = Surface?.CreateEditorContextMenu(element.Id);
        foreach (var child in element.Children.OrderByDescending(e => e.ZIndex)) item.Items.Add(LayerItem(child)); return item;
    }
    private void UpdateLayerSelection()
    {
        if (_layers == null) return;
        void Visit(ItemCollection children) { foreach (var item in children.OfType<TreeViewItem>()) { item.Background = Editor.SelectedIds.Contains((string)item.Tag) ? HmiVisuals.Brush("#34434c") : Brushes.Transparent; Visit(item.Items); } } Visit(_layers.Items);
    }
    private void UpdateInspector() => Inspector?.Inspect(Document, Editor.CurrentPage, Editor.SelectedElements.ToArray(), _runtime);
    private void SetSelectedDimension(string path, double value)
    {
        var selected = Editor.SelectedElements;
        var nested = selected.SelectMany(e => e.Children.SelectMany(c => c.DescendantsAndSelf())).Select(e => e.Id).ToHashSet();
        using var transaction = Editor.BeginTransaction("修改组件尺寸");
        foreach (var e in selected.Where(e => !nested.Contains(e.Id) && Editor.IsEditable(e))) Editor.ResizeElement(e.Id, e.X, e.Y, path == "attr.w" ? value : e.Width, path == "attr.h" ? value : e.Height);
    }
    private void RenameEditorPage(int index)
    {
        var dialog = new Window { Title = "重命名看板", Owner = this, Width = 350, Height = 145, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = HmiVisuals.Brush("#262629") };
        var input = new TextBox { Text = Document.Pages[index].Title, Margin = new Thickness(10), Padding = new Thickness(6) }; var panel = new DockPanel();
        var apply = Button("确定", (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) { Editor.SelectPage(index); Editor.RenamePage(input.Text.Trim()); dialog.Close(); } }); DockPanel.SetDock(apply, Dock.Bottom); panel.Children.Add(apply); panel.Children.Add(input); dialog.Content = panel; dialog.ShowDialog();
    }
    private void SaveEditorAs()
    {
        var suggested = string.IsNullOrEmpty(Document.SourcePath) ? SuggestedSavePath : Document.SourcePath;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "ES-Vision HMI (*.aivh)|*.aivh", DefaultExt = ".aivh", FileName = string.IsNullOrEmpty(suggested) ? "新建看板.aivh" : Path.GetFileName(suggested) };
        if (!string.IsNullOrEmpty(suggested) && Directory.Exists(Path.GetDirectoryName(suggested))) dialog.InitialDirectory = Path.GetDirectoryName(suggested);
        if (dialog.ShowDialog(this) == true) SaveDocument(dialog.FileName);
    }
    private void OpenEditorDocument()
    {
        if (Inspector?.CommitPendingEdits() == false) { SetStatus("请先修正组件配置中的无效值。"); return; }
        if (Editor.IsDirty)
        {
            var result = MessageBox.Show(this, "当前 HMI 尚未保存，是否保存后打开其他布局？", "打开 HMI", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.Cancel) return;
            if (result == MessageBoxResult.Yes) { SaveDocument(); if (Editor.IsDirty) return; }
        }
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "ES-Vision HMI (*.aivh)|*.aivh" };
        if (dialog.ShowDialog(this) != true) return;
        try { Editor.Open(dialog.FileName); FitCanvas(); } catch (Exception ex) { SetStatus("打开失败：" + ex.Message); }
    }
    private void CopyEditorSelection() { var text = Editor.Copy(); if (!string.IsNullOrEmpty(text)) try { Clipboard.SetText(text); } catch { } }
    private void PasteEditorSelection()
    {
        try { if (Clipboard.ContainsText()) { Editor.Paste(Clipboard.GetText()); return; } Editor.Paste(); }
        catch (Exception ex) { SetStatus("无法粘贴：" + ex.Message); }
    }
    private void EditorKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsDesignMode) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control); var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && e.Key == Key.S) { if (shift) SaveEditorAs(); else SaveDocument(); e.Handled = true; return; }
        if (IsInspectorInput(Keyboard.FocusedElement as DependencyObject)) return;
        if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase || Keyboard.FocusedElement is ComboBox) return;
        var handled = true;
        if (ctrl) switch (e.Key)
        {
            case Key.Z: if (shift) Editor.Redo(); else Editor.Undo(); break;
            case Key.Y: Editor.Redo(); break;
            case Key.A: Editor.SelectAll(); break;
            case Key.C: CopyEditorSelection(); break;
            case Key.X: if (Editor.Cut()) try { Clipboard.SetText(Editor.ClipboardText); } catch { } break;
            case Key.V: PasteEditorSelection(); break;
            case Key.D: Editor.DuplicateSelection(); break;
            case Key.G: if (shift) Editor.UngroupSelection(); else Editor.GroupSelection(); break;
            case Key.L: Editor.SetLocked(!Editor.SelectedElements.All(x => x.Locked)); break;
            default: handled = false; break;
        }
        else switch (e.Key)
        {
            case Key.Delete: case Key.Back: Editor.DeleteSelection(); break;
            case Key.Left: Editor.MoveSelection(shift ? -10 : -1, 0); break;
            case Key.Right: Editor.MoveSelection(shift ? 10 : 1, 0); break;
            case Key.Up: Editor.MoveSelection(0, shift ? -10 : -1); break;
            case Key.Down: Editor.MoveSelection(0, shift ? 10 : 1); break;
            case Key.Escape: if (Surface?.CancelPointerEdit() != true) Editor.ClearSelection(); break;
            case Key.F5: IsDesignMode = false; break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }
    private void ToggleEditorMaximize()
    {
        if (FullScreenWindows.TryGetValue(this, out var state))
        {
            ToggleFullScreen(this);
            WindowState = WindowState.Normal;
        }
        else WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
    private bool IsInspectorInput(DependencyObject child)
    {
        while (child != null) { if (ReferenceEquals(child, Inspector)) return true; child = child is Visual || child is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child); }
        return false;
    }
    private static bool IsInButton(DependencyObject child)
    {
        while (child != null) { if (child is System.Windows.Controls.Primitives.ButtonBase) return true; child = child is Visual || child is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child); }
        return false;
    }
    internal static MenuItem Menu(string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); return item; }
    private static Button Tool(string label, string hint, Action action) { var b = Button(label, (_, _) => action()); b.ToolTip = hint; b.Width = 38; b.Padding = new Thickness(3); b.Margin = new Thickness(4, 1, 4, 1); b.Foreground = HmiVisuals.Brush("#5ca7d0"); return b; }
    private static Brush DotGrid()
    {
        var draw = new DrawingGroup(); draw.Children.Add(new GeometryDrawing(HmiVisuals.Brush("#191a1f"), null, new RectangleGeometry(new Rect(0, 0, 16, 16)))); draw.Children.Add(new GeometryDrawing(HmiVisuals.Brush("#555963"), null, new EllipseGeometry(new Point(1, 1), .65, .65)));
        return new DrawingBrush(draw) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 16, 16), Stretch = Stretch.None };
    }
    private bool _snapEnabled;
    private static void InstallEditorStyles(ResourceDictionary resources)
    {
        var border = new FrameworkElementFactory(typeof(Border)); border.Name = "surface";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty)); border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty)); border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3)); border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter)); content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center); content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center); border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(OpacityProperty, .8, "surface")); template.Triggers.Add(hover);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(OpacityProperty, .35, "surface")); template.Triggers.Add(disabled);
        var button = new Style(typeof(Button)); button.Setters.Add(new Setter(Control.TemplateProperty, template)); button.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.LightGray)); resources.Add(typeof(Button), button);
        var tab = new Style(typeof(TabItem)); tab.Setters.Add(new Setter(Control.BackgroundProperty, HmiVisuals.Brush("#27272b"))); tab.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.LightGray)); tab.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 8, 12, 8)));
        var tabBorder = new FrameworkElementFactory(typeof(Border)); tabBorder.Name = "tab"; tabBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty)); tabBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var tabContent = new FrameworkElementFactory(typeof(ContentPresenter)); tabContent.SetValue(ContentPresenter.ContentSourceProperty, "Header"); tabBorder.AppendChild(tabContent);
        var tabTemplate = new ControlTemplate(typeof(TabItem)) { VisualTree = tabBorder }; var selected = new Trigger { Property = TabItem.IsSelectedProperty, Value = true }; selected.Setters.Add(new Setter(Border.BackgroundProperty, HmiVisuals.Brush("#34434b"), "tab")); tabTemplate.Triggers.Add(selected); tab.Setters.Add(new Setter(Control.TemplateProperty, tabTemplate)); resources.Add(typeof(TabItem), tab);
        InstallDropDownStyle(resources);
    }
    private static void InstallDropDownStyle(ResourceDictionary resources)
    {
        var comboStyle = new Style(typeof(ComboBox));
        comboStyle.Setters.Add(new Setter(Control.ForegroundProperty, HmiVisuals.Brush("#dedede")));
        comboStyle.Setters.Add(new Setter(Control.BackgroundProperty, HmiVisuals.Brush("#38383b")));
        comboStyle.Setters.Add(new Setter(Control.BorderBrushProperty, HmiVisuals.Brush("#4b4b50")));
        comboStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 5, 25, 5)));
        var grid = new FrameworkElementFactory(typeof(Grid));
        var toggle = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.ToggleButton)); toggle.Name = "DropDownToggle";
        toggle.SetValue(FocusableProperty, false); toggle.SetValue(Control.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty)); toggle.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        toggle.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding("IsDropDownOpen") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent), Mode = System.Windows.Data.BindingMode.TwoWay });
        var toggleBorder = new FrameworkElementFactory(typeof(Border)); toggleBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty)); toggleBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        var arrow = new FrameworkElementFactory(typeof(TextBlock)); arrow.SetValue(TextBlock.TextProperty, "⌄"); arrow.SetValue(TextBlock.ForegroundProperty, HmiVisuals.Brush("#aaaaaf")); arrow.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Right); arrow.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center); arrow.SetValue(MarginProperty, new Thickness(0, 0, 9, 0)); toggleBorder.AppendChild(arrow);
        toggle.SetValue(Control.TemplateProperty, new ControlTemplate(typeof(System.Windows.Controls.Primitives.ToggleButton)) { VisualTree = toggleBorder }); grid.AppendChild(toggle);
        var selectedContent = new FrameworkElementFactory(typeof(ContentPresenter)); selectedContent.Name = "Selection";
        selectedContent.SetValue(IsHitTestVisibleProperty, false); selectedContent.SetValue(MarginProperty, new TemplateBindingExtension(Control.PaddingProperty)); selectedContent.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        selectedContent.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty)); selectedContent.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty)); grid.AppendChild(selectedContent);
        var edit = new FrameworkElementFactory(typeof(TextBox)); edit.Name = "PART_EditableTextBox"; edit.SetValue(VisibilityProperty, Visibility.Hidden); edit.SetValue(MarginProperty, new Thickness(5, 2, 24, 2)); edit.SetValue(Control.BackgroundProperty, Brushes.Transparent); edit.SetValue(Control.ForegroundProperty, HmiVisuals.Brush("#dedede")); edit.SetValue(Control.BorderThicknessProperty, new Thickness(0)); edit.SetValue(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center); grid.AppendChild(edit);
        var popup = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.Popup)); popup.Name = "PART_Popup"; popup.SetValue(System.Windows.Controls.Primitives.Popup.PlacementProperty, System.Windows.Controls.Primitives.PlacementMode.Bottom); popup.SetValue(System.Windows.Controls.Primitives.Popup.AllowsTransparencyProperty, true); popup.SetValue(System.Windows.Controls.Primitives.Popup.FocusableProperty, false);
        popup.SetValue(System.Windows.Controls.Primitives.Popup.IsOpenProperty, new TemplateBindingExtension(ComboBox.IsDropDownOpenProperty));
        var popupBorder = new FrameworkElementFactory(typeof(Border)); popupBorder.SetValue(Border.BackgroundProperty, HmiVisuals.Brush("#303034")); popupBorder.SetValue(Border.BorderBrushProperty, HmiVisuals.Brush("#53535b")); popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1)); popupBorder.SetBinding(MinWidthProperty, new System.Windows.Data.Binding("ActualWidth") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var scroll = new FrameworkElementFactory(typeof(ScrollViewer)); scroll.SetValue(MaxHeightProperty, 340d); scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto); var items = new FrameworkElementFactory(typeof(ItemsPresenter)); scroll.AppendChild(items); popupBorder.AppendChild(scroll); popup.AppendChild(popupBorder); grid.AppendChild(popup);
        var comboTemplate = new ControlTemplate(typeof(ComboBox)) { VisualTree = grid };
        var editable = new Trigger { Property = ComboBox.IsEditableProperty, Value = true }; editable.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible, "PART_EditableTextBox")); editable.Setters.Add(new Setter(VisibilityProperty, Visibility.Hidden, "Selection")); comboTemplate.Triggers.Add(editable);
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false }; disabled.Setters.Add(new Setter(OpacityProperty, .4)); comboTemplate.Triggers.Add(disabled);
        comboStyle.Setters.Add(new Setter(Control.TemplateProperty, comboTemplate)); resources.Add(typeof(ComboBox), comboStyle);
        var itemStyle = new Style(typeof(ComboBoxItem)); itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, HmiVisuals.Brush("#dedede"))); itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent)); itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
        var itemBorder = new FrameworkElementFactory(typeof(Border)); itemBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty)); itemBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty)); itemBorder.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter))); itemStyle.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ComboBoxItem)) { VisualTree = itemBorder }));
        var highlight = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true }; highlight.Setters.Add(new Setter(Control.BackgroundProperty, HmiVisuals.Brush("#42505a"))); itemStyle.Triggers.Add(highlight); resources.Add(typeof(ComboBoxItem), itemStyle);
    }
}

internal sealed class HmiRuler : FrameworkElement
{
    public bool Vertical { get; set; }
    public double Zoom { get; set; } = 1;
    public double Offset { get; set; }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(HmiVisuals.Brush("#222226"), null, new Rect(RenderSize));
        var length = Vertical ? ActualHeight : ActualWidth; var step = Zoom < .3 ? 100 : Zoom < .8 ? 50 : 10;
        var first = (int)Math.Floor(-Offset / (step * Zoom));
        for (var i = first; i * step * Zoom + Offset < length; i++)
        {
            var p = i * step * Zoom + Offset; var major = i % 2 == 0;
            var pen = new Pen(HmiVisuals.Brush("#69696e"), .5);
            dc.DrawLine(pen, Vertical ? new Point(22, p) : new Point(p, 22), Vertical ? new Point(major ? 13 : 17, p) : new Point(p, major ? 13 : 17));
            if (!major) continue;
            var text = new FormattedText((i * step).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 8, HmiVisuals.Brush("#96969d"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            if (Vertical) { dc.PushTransform(new RotateTransform(-90, 3, p - 3)); dc.DrawText(text, new Point(3, p - 3)); dc.Pop(); }
            else dc.DrawText(text, new Point(p + 2, 1));
        }
    }
}
