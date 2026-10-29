using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AiVisionStudio.Models;
using AiVisionStudio.Services;

namespace AiVisionStudio.Views;

public partial class ParamsWindow : Window
{
    private readonly MainViewModel _vm;
    private int? _previewIndex;
    private bool? _previewIsCloud;
    private string _previewInputIdentity;
    private bool _showOverlays = true;
    private bool _closeAfterTest;
    private string _layoutOperationId, _layoutKind;
    private bool? _stackedLayout, _inlineSplit;
    private bool _layoutQueued;
    private bool _resetGroupSizes;
    private GridLength _sideLeft, _sideRight;
    private GridLength _stackTop = new(52, GridUnitType.Star), _stackBottom = new(48, GridUnitType.Star);

    public ParamsWindow(MainViewModel vm)
    {
        InitializeComponent();
        WindowScreenBehavior.Attach(this);
        _vm = vm;
        DataContext = vm;
        _vm.ImageChanged += OnImageChanged;
        _vm.PropertyChanged += OnViewModelChanged;
        _vm.ParameterGroups.CollectionChanged += OnParameterGroupsChanged;
        ConfigureLayout();
        SizeChanged+=(_,_)=>AdaptLayout();
        Loaded += (_, _) =>
        {
            AdaptLayout();
            RefreshPreview();
        };
        Closing += OnWindowClosing;
        Closed += (_, _) =>
        {
            EndRegionEditing(true);
            Preview.Clear();
            DisposeShapeEditor();
            _vm.ImageChanged -= OnImageChanged;
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.ParameterGroups.CollectionChanged -= OnParameterGroupsChanged;
        };
        PreviewKeyDown += OnKeyDown;
    }

    private void OnParameterGroupsChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (_layoutQueued) return;
        _layoutQueued = true;
        Dispatcher.InvokeAsync(() => { _layoutQueued = false; ConfigureLayout(); });
    }

    private static ParameterGroupsPanel FindParameterGroupsPanel(DependencyObject element)
    {
        while (element != null)
        {
            if (element is ParameterGroupsPanel panel) return panel;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void OnParameterGroupDividerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Thumb { DataContext: ParameterGroup group } divider)
        {
            var panel = FindParameterGroupsPanel(divider);
            if (_resetGroupSizes && panel != null) { panel.ResetSizes(); _resetGroupSizes = false; }
            divider.Visibility = panel?.CanResizeAfter(group) == true ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnParameterGroupResize(object sender, DragDeltaEventArgs e)
    {
        if (sender is Thumb { DataContext: ParameterGroup group } divider)
        {
            FindParameterGroupsPanel(divider)?.ResizeAfter(group, e.VerticalChange);
            e.Handled = true;
        }
    }
    private void OnShowAllResults(object sender,RoutedEventArgs e)
    {
        if(!_vm.CanEdit||sender is not Button{DataContext:ParameterGroup group} button)return;
        var enabled=bool.TryParse(button.Tag?.ToString(),out var value)&&value;
        foreach(var node in group.Items.Concat(group.AdvancedItems).Where(n=>n.IsBoolean&&n.CanEditValue))node.BoolValue=enabled;
        _showOverlays=true;TestStatus.Text=enabled?"结果显示选项已开启，点击测试刷新。":"结果显示选项已关闭，点击测试刷新。";
    }

    private void ConfigureLayout()
    {
        var kind = _vm.ToolWindowKind;
        bool resetSizes = _layoutOperationId != _vm.SelectedOperation?.Uuid || _layoutKind != kind;
        _layoutOperationId = _vm.SelectedOperation?.Uuid;
        _layoutKind = kind;
        var script = kind == "script";
        var list = kind.StartsWith("list_", StringComparison.Ordinal) && kind != "list_recipe";
        var single = kind is "basic" or "none" or "list_vertical";
        var groups = new ObservableCollection<ParameterGroup>();
        foreach (var original in _vm.ParameterGroups)
        {
            var group = new ParameterGroup { Title = original.Title, Field = original.Field };
            foreach (var node in original.Items.Where(node => !list || node.Kind != "list")) group.Items.Add(node);
            foreach (var node in original.AdvancedItems) group.AdvancedItems.Add(node);
            if (group.Items.Count > 0 || group.AdvancedItems.Count > 0) groups.Add(group);
        }
        ParameterGroupItems.ItemsSource = groups;
        CompactGroups.ItemsSource=groups;
        FormBody.Visibility = script ? Visibility.Collapsed : Visibility.Visible;
        ScriptHost.Visibility = script ? Visibility.Visible : Visibility.Collapsed;
        if (script && ScriptHost.Content == null) ScriptHost.Content = new ScriptToolEditor { DataContext = _vm };
        RightPane.Visibility = single || script ? Visibility.Collapsed : Visibility.Visible;
        MainDivider.Visibility = single || script ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(LeftPane, single || script ? 3 : 1);
        RightWidth.MinWidth = single || script ? 0 : 440;
        if (resetSizes)
        {
            _resetGroupSizes = true;
            _sideRight = single || script ? new GridLength(0) : new GridLength(kind == "list_script" ? 42 : 60, GridUnitType.Star);
            _sideLeft = new GridLength(kind == "list_script" ? 58 : 40, GridUnitType.Star);
            LeftWidth.Width = _sideLeft;
            RightWidth.Width = _sideRight;
            PreviewHeight.Height = new GridLength(70, GridUnitType.Star);
            OutputHeight.Height = new GridLength(30, GridUnitType.Star);
            _stackTop = new GridLength(52, GridUnitType.Star);
            _stackBottom = new GridLength(48, GridUnitType.Star);
            _stackedLayout = null;
        }
        DividerWidth.Width = new GridLength(single || script ? 0 : 5);
        PreviewPane.Visibility = kind is "image" or "list_image" ? Visibility.Visible : Visibility.Collapsed;
        SpecializedPreview.Visibility = kind is "list_horizontal" or "list_script" or "list_recipe" ? Visibility.Visible : Visibility.Collapsed;
        if (kind == "list_horizontal") SpecializedPreview.Content = new ToolListEditor { DataContext = _vm };
        else if (kind == "list_script") SpecializedPreview.Content = new ExpressionToolEditor { DataContext = _vm };
        else if (kind == "list_recipe") SpecializedPreview.Content = new RecipeToolPreview { DataContext = _vm };
        else SpecializedPreview.Content = null;
        var inlineList = kind is "list_vertical" or "list_image" or "list_script";
        InlineListHost.Visibility = inlineList ? Visibility.Visible : Visibility.Collapsed;
        bool inlineSplit = inlineList && groups.Count > 0;
        if (resetSizes || _inlineSplit != inlineSplit)
        {
            InlineListHeight.Height = inlineList ? new GridLength(groups.Count == 0 ? 1 : 2, GridUnitType.Star) : new GridLength(0);
            FormHeight.Height = inlineList && groups.Count == 0 ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        }
        _inlineSplit = inlineSplit;
        FormHeight.MinHeight = inlineSplit ? 48 : 0;
        InlineListHeight.MinHeight = inlineSplit ? 60 : 0;
        InlineDividerHeight.Height = new GridLength(inlineSplit ? 5 : 0);
        InlineDivider.Visibility = inlineSplit ? Visibility.Visible : Visibility.Collapsed;
        InlineListHost.Content = inlineList ? new ToolListEditor { DataContext = _vm } : null;
        ConfigureShapeMatchingEditor();
        AdaptLayout();
    }

    public bool IsStackedLayout {get;private set;}
    private void AdaptLayout()
    {
        var split=RightPane.Visibility==Visibility.Visible;var narrow=split&&ActualWidth>0&&ActualWidth<900;
        if (_stackedLayout != narrow)
        {
            if (_stackedLayout == true)
            {
                _stackTop = UpperHeight.Height;
                _stackBottom = LowerHeight.Height;
            }
            else if (_stackedLayout == false)
            {
                _sideLeft = LeftWidth.Width;
                _sideRight = RightWidth.Width;
            }
            UpperHeight.Height = narrow ? _stackTop : new GridLength(1, GridUnitType.Star);
            LowerHeight.Height = narrow ? _stackBottom : new GridLength(0);
            if (!narrow) { LeftWidth.Width = _sideLeft; RightWidth.Width = _sideRight; }
            _stackedLayout = narrow;
        }
        IsStackedLayout=narrow;
        UpperHeight.MinHeight = narrow ? (_inlineSplit == true ? 160 : 110) : 0;
        LowerHeight.MinHeight = narrow ? 170 : 0;
        HorizontalDividerHeight.Height=new GridLength(narrow?5:0);
        Grid.SetRow(RightPane,narrow?2:0);Grid.SetColumn(RightPane,narrow?0:2);Grid.SetColumnSpan(RightPane,narrow?3:1);
        Grid.SetColumnSpan(LeftPane,narrow||!split?3:1);
        RightWidth.MinWidth=split&&!narrow?360:0;LeftWidth.MinWidth=narrow?0:300;
        DividerWidth.Width=new GridLength(split&&!narrow?5:0);
        Grid.SetRow(MainDivider,narrow?1:0);Grid.SetColumn(MainDivider,narrow?0:1);Grid.SetColumnSpan(MainDivider,narrow?3:1);
        MainDivider.Width=narrow?double.NaN:5;MainDivider.Height=narrow?5:double.NaN;MainDivider.HorizontalAlignment=HorizontalAlignment.Stretch;MainDivider.VerticalAlignment=VerticalAlignment.Stretch;
        MainDivider.ResizeDirection=narrow?GridResizeDirection.Rows:GridResizeDirection.Columns;
        MainDivider.Cursor = narrow ? Cursors.SizeNS : Cursors.SizeWE;
        PreviewHeight.MinHeight=narrow?70:100;OutputHeight.MinHeight=90;
        ParameterGroupItems.Visibility=narrow?Visibility.Collapsed:Visibility.Visible;CompactGroups.Visibility=narrow?Visibility.Visible:Visibility.Collapsed;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindButton(source) != null) return;
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private static Button FindButton(DependencyObject value)
    {
        while (value != null)
        {
            if (value is Button button) return button;
            value = value is Visual ? VisualTreeHelper.GetParent(value) : LogicalTreeHelper.GetParent(value);
        }
        return null;
    }

    private void OnMaximize(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Keyboard.ClearFocus();
        if (!_vm.CanEdit || !_vm.ApplyParameters())
        {
            TestStatus.Text = "请修正无效参数";
            TestStatus.ToolTip = _vm.Status;
            return;
        }
        // Native tool Save accepts parameters into the current project; project Save writes the file.
        TestStatus.Text = "参数已保存";
        TestStatus.ToolTip = "已保存到当前工程。使用主窗口的保存按钮写入工程文件。";
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (_regionTarget != null)
        {
            if (Preview.RegionEditor.IsInteracting && !Preview.RegionEditor.FinishPolygon())
            { TestStatus.Text = "请先完成有效的绘制区域"; return; }
            EndRegionEditing(true);
            _previewIndex = null;
            _previewIsCloud = null;
        }
        if (!PrepareShapeRuntimeTest()) return;
        if (_vm.IsRunning)
        {
            if (_vm.StopCommand.CanExecute(null)) _vm.StopCommand.Execute(null);
            TestStatus.Text = "正在停止";
            return;
        }
        Keyboard.ClearFocus();
        if (!_vm.ApplyParameters())
        {
            TestStatus.Text = "请修正无效参数";
            TestStatus.ToolTip = _vm.Status;
            return;
        }
        TestButton.Content = "停止";
        TestStatus.Text = "测试中";
        try
        {
            await _vm.TestSelectedAsync(templateCurrentInputOnly: IsShapeMatcher);
            RefreshShapeMatchResults(freshResults: true);
            RefreshPreview();
            TestStatus.Text = _vm.SelectedResult;
            TestStatus.ToolTip = _vm.Status;
        }
        catch (Exception ex)
        {
            TestStatus.Text = "测试失败";
            TestStatus.ToolTip = ex.Message;
        }
        finally
        {
            TestButton.Content = "测试";
            if (_closeAfterTest) Close();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Preview.HandleRegionEditKey(e.Key)) { e.Handled = true; return; }
        if (e.Key == Key.Escape && Preview.RegionEditor.IsEditing) { EndRegionEditing(); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && Preview.RegionEditor.IsEditing)
        { Preview.RegionEditor.FinishPolygon(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.F1) { try{ManualCatalog.Open(ManualCatalog.UrlFor(_vm.SelectedOperation?.Driver));}catch(Exception ex){TestStatus.Text=ex.Message;}e.Handled=true; }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Enter)
        {
            OnTest(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.S)
        {
            OnSave(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnWindowClosing(object sender, CancelEventArgs e)
    {
        if (HandleShapeClosing(e)) return;
        if (_vm.IsRunning)
        {
            _closeAfterTest = true;
            if (_vm.StopCommand.CanExecute(null)) _vm.StopCommand.Execute(null);
            TestStatus.Text = "正在停止";
            e.Cancel = true;
            return;
        }
        if (!_vm.HasParameterErrors) return;
        var answer = MessageBox.Show(this, "参数包含无效输入。是否放弃无效输入并关闭？\n已生效的有效修改会保留。",
            "参数未通过验证", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) { e.Cancel = true; _closeAfterTest = false; return; }
        _vm.DiscardInvalidParameterEdits();
    }

    private void OnViewModelChanged(object sender, PropertyChangedEventArgs e)
    {
        if (_committingRegion) return;
        if (e.PropertyName == nameof(MainViewModel.CanEdit))
        {
            if (Dispatcher.CheckAccess()) RefreshImagePaging();
            else Dispatcher.InvokeAsync(RefreshImagePaging);
        }
        if (e.PropertyName is nameof(MainViewModel.SelectedOperation) or nameof(MainViewModel.Is3DTool))
        {
            void Update()
            {
                if (_regionTarget == null || e.PropertyName == nameof(MainViewModel.SelectedOperation))
                {
                    _previewIndex = null;
                    _previewIsCloud = null;
                }
                RefreshPreview();
            }
            if (Dispatcher.CheckAccess()) Update();
            else Dispatcher.InvokeAsync(Update);
        }
    }

    private void OnOutputExpanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: VarRow row }) row.Expand();
    }

    private void OnImageChanged(object sender, EventArgs e)
    {
        if (_committingRegion) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(() => { RefreshShapeRowsIfChanged(); RefreshPreview(); }); return; }
        RefreshShapeRowsIfChanged();
        RefreshPreview();
    }

    public void RefreshPreview()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(RefreshPreview); return; }
        if (_vm == null || Preview == null) return;
        var identity = _vm.SelectedImageInputIdentity;
        if (_previewInputIdentity != identity)
        {
            _previewInputIdentity = identity;
            _previewIndex = null; _previewIsCloud = null;
            _parameterRegionPreviewPath = null;
            EndRegionEditing(true); Preview.CancelRegionEdit(); Preview.RequestFitView();
        }
        RefreshImagePaging();
        if (IsShapeTool && _vm.CanEdit) _vm.SynchronizeSelectedContourInput();
        if (TryShowShapeTrainingPreview()) return;
        var index = _previewIndex ?? _vm.GetSelectedPreviewIndex();
        var cloud = _vm.Clouds.Get(index);
        bool selectedInput = !_previewIndex.HasValue || _previewIsCloud == false && index == _vm.RegionInputIndex && _vm.HasSelectedImageInput;
        using var selectedImage = selectedInput ? _vm.CopySelectedPreviewImage(index, _previewIndex.HasValue) : null;
        var mat = selectedInput ? selectedImage : _vm.Images.Get(index);
        var preferCloud = _previewIsCloud ?? _vm.Is3DTool;
        var hasCloud = cloud != null && cloud.Count > 0;
        var hasImage = mat != null && !mat.IsDisposed && !mat.Empty();
        var hasProfile = _vm.Profiles.TryGetValue(index, out var profile) && profile.Count > 0;
        var cloudMode = hasCloud && (preferCloud || !hasImage);
        PreviewMode.Text = hasProfile ? "1D模式" : cloudMode || (!hasImage && _vm.Is3DTool) ? "3D模式" : "2D模式";
        PreviewSlot.Text = index >= 0 ? $"IM{index}" : "";
        if (hasProfile) Preview.ShowProfile(profile);
        else if (cloudMode)
            Preview.ShowCloud(cloud, _showOverlays ? _vm.TextOverlays.Where(x => x.ViewIndex < 0 || x.ViewIndex == index) : null,
                _showOverlays ? _vm.BoxOverlays.Where(x => x.ViewIndex < 0 || x.ViewIndex == index) : null);
        else if (hasImage)
            Preview.ShowMat(mat, _vm.GetImageName(index));
        else Preview.Clear();
        Preview.SetNativeOverlays(_showOverlays && _regionTarget == null ? _vm.GetSelectedPreviewOverlays(index, selectedInput && _previewIndex.HasValue) : null);
        SynchronizeEditedRegion();
        if (_regionTarget == null) ApplyParameterRegionPreview();
        EmptyPreview.Visibility = hasImage || hasCloud || hasProfile ? Visibility.Collapsed : Visibility.Visible;
        PreviewTextOverlay.Visibility = _showOverlays && _regionTarget == null && !cloudMode && hasImage ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFit(object sender, RoutedEventArgs e) => Preview.FitView();

    private void OnToggleOverlays(object sender, RoutedEventArgs e)
    {
        _showOverlays = !_showOverlays;
        RefreshPreview();
    }

    private void OnPreviewMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var automatic = new MenuItem { Header = "当前工具图像", IsCheckable = true, IsChecked = !_previewIndex.HasValue };
        automatic.Click += (_, _) => { _previewIndex = null; _previewIsCloud = null; RefreshPreview(); };
        menu.Items.Add(automatic);
        foreach (var item in _vm.Images.All.OrderBy(p => p.Key))
        {
            if (item.Value == null || item.Value.IsDisposed || item.Value.Empty()) continue;
            var index = item.Key;
            var choice = new MenuItem { Header = $"IM{index}  图像  {item.Value.Width} × {item.Value.Height}", IsCheckable = true, IsChecked = _previewIndex == index && _previewIsCloud == false };
            choice.Click += (_, _) => { _previewIndex = index; _previewIsCloud = false; RefreshPreview(); };
            menu.Items.Add(choice);
        }
        foreach (var item in _vm.Clouds.All.OrderBy(p => p.Key))
        {
            if (item.Value == null || item.Value.Count == 0) continue;
            var index = item.Key;
            var choice = new MenuItem { Header = $"IM{index}  点云  {item.Value.Count:N0} 点", IsCheckable = true, IsChecked = _previewIndex == index && _previewIsCloud == true };
            choice.Click += (_, _) => { _previewIndex = index; _previewIsCloud = true; RefreshPreview(); };
            menu.Items.Add(choice);
        }
        OpenMenu(menu, sender);
    }

    private void OnDisplayMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var overlays = new MenuItem { Header = "显示结果标注", IsCheckable = true, IsChecked = _showOverlays };
        overlays.Click += (_, _) => { _showOverlays = overlays.IsChecked; RefreshPreview(); };
        menu.Items.Add(overlays);
        var fit = new MenuItem { Header = "适应窗口" };
        fit.Click += OnFit;
        menu.Items.Add(fit);
        var save = new MenuItem { Header = "保存预览图像", IsEnabled = Preview.HasImage || Preview.HasCloud };
        save.Click += OnSavePreview;
        menu.Items.Add(save);
        OpenMenu(menu, sender);
    }

    private static void OpenMenu(ContextMenu menu, object sender)
    {
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private void OnSavePreview(object sender, RoutedEventArgs e)
    {
        if (!Preview.HasImage && !Preview.HasCloud)
        {
            TestStatus.Text = "当前无图像";
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 图像|*.png", FileName = "preview.png", DefaultExt = ".png" };
        if (dialog.ShowDialog(this) != true) return;
        try { Preview.SavePng(dialog.FileName); TestStatus.Text = "图像已保存"; }
        catch (Exception ex) { TestStatus.Text = "保存失败"; TestStatus.ToolTip = ex.Message; }
    }

    public void OpenParameterBinding(ParameterNode node)
    {
        if (!_vm.CanEdit) return;
        var choices = _vm.GetBindingChoices(node).ToArray();
        var dialog = new Window
        {
            Owner = this, Title = "参数绑定 · " + node.DisplayName, Width = 620, Height = 460,
            MinWidth = 420, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("Panel"), Foreground = (Brush)FindResource("Text"),
            FontFamily = FontFamily, FontSize = 13, ShowInTaskbar = false
        };
        var layout = new DockPanel { Margin = new Thickness(16) };
        dialog.Content = layout;
        var heading = new TextBlock { Text = node.DisplayName + "  ·  " + node.BindingLabel, Margin = new Thickness(0,0,0,12), TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);
        var search = new TextBox { Style = (Style)FindResource("ToolTextBox"), Margin = new Thickness(0,0,0,10), ToolTip = "查找算子或变量" };
        DockPanel.SetDock(search, Dock.Top);
        layout.Children.Add(search);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,12,0,0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        var cancel = new Button { Content = "取消", Style = (Style)FindResource("ToolActionButton"), Margin = new Thickness(0,0,8,0), IsCancel = true };
        var accept = new Button { Content = "绑定", Style = (Style)FindResource("ToolActionButton"), IsDefault = true };
        footer.Children.Add(cancel);
        footer.Children.Add(accept);
        layout.Children.Add(footer);
        var list = new ListBox { Style = (Style)FindResource("ToolOutputList"), ItemsSource = choices, DisplayMemberPath = nameof(ParameterBindingChoice.Label) };
        layout.Children.Add(list);
        search.TextChanged += (_, _) =>
        {
            var text = search.Text.Trim();
            list.ItemsSource = text.Length == 0 ? choices : choices.Where(c => c.Label.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        };
        accept.Click += (_, _) =>
        {
            if (list.SelectedItem is not ParameterBindingChoice choice) return;
            _vm.SetParameterBinding(node, choice);
            dialog.DialogResult = true;
        };
        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedItem is not ParameterBindingChoice choice) return;
            _vm.SetParameterBinding(node, choice);
            dialog.DialogResult = true;
        };
        dialog.ShowDialog();
    }
}

