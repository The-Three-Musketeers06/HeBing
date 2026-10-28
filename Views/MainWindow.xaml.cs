using AiVisionStudio.Models;
using AiVisionStudio.Services;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AiVisionStudio.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private Point _toolDragStart;
    private Point _runDragStart;
    private Operation _runDragOperation;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuStamp = DateTime.Now;
    private DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _toolSearchTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _searchComposing;
    private LogWindow _logWindow;
    private GlobalVariablesWindow _globalsWindow;
    private RecipeManagerWindow _recipesWindow;
    private HmiWindow _hmiWindow;
    private HmiRuntimeBridge _hmiRuntime;
    private string _hmiProjectPath;
    private bool _closeWhenStopped;
    private bool _startupCompleted;
    private bool _isClosed;

    public MainWindow()
    {
        InitializeComponent();
        _toolSearchTimer.Tick += (_, _) =>
        {
            _toolSearchTimer.Stop();
            if (!_isClosed && !_searchComposing) _vm.RefreshSearch(SearchBox.Text);
        };
        WindowScreenBehavior.Attach(this);
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        DataContext = _vm;
        ImageWorkspace.ActionsRequested += ShowImageActions;
        ImageWorkspace.ViewSaveRequested += SaveView;
        ImageWorkspace.ImageSelectionRequested += async (operationId, file) =>
        {
            try { await _vm.SelectImageAndRunAsync(operationId, file); }
            catch (Exception error) { _vm.Status = "切图失败：" + error.Message; }
        };
        ImageWorkspace.LayoutChanged += PersistVisualLayout;
        ImageWorkspace.SourceMenuRequested += (paneId, button) => ShowVisualSourcesMenu(button, paneId);
        ImageWorkspace.DisplayRefreshRequested += () => RefreshImageViews(force: true);
        _vm.VisualProgramRenamed += (oldName, newName) => ImageWorkspace.RenameVisualContext(_vm.ProjectPath + "|" + oldName, _vm.ProjectPath + "|" + newName, oldName, newName);
        RefreshImageViews();
        foreach (var tool in _vm.Tools) tool.IsExpanded = false;
        Closing += OnWindowClosing;
        Closed += (_, _) => { _isClosed = true; _toolSearchTimer.Stop(); _statusTimer?.Stop(); _logWindow?.Close(); _hmiWindow?.Close(); _hmiRuntime?.Dispose(); foreach (var view in ImageWorkspace.Viewers.Values) view.Clear(); _vm.Dispose(); };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsRunning) or nameof(MainViewModel.IsCycling) && _vm.CanEdit && StudioSettings.Current.FastMode)
                RefreshImageViews();
            if (e.PropertyName is nameof(MainViewModel.IsRunning) or nameof(MainViewModel.IsCycling) && _vm.CanEdit && _closeWhenStopped)
                Dispatcher.BeginInvoke(new Action(() => { if (!_isClosed) Close(); }));
            if (e.PropertyName == nameof(MainViewModel.CanEdit))
            {
                foreach(var viewer in ImageWorkspace.Viewers.Values)
                {
                    viewer.SetThumbnailInteractionEnabled(_vm.CanEdit);
                }
            }
        };
        _vm.ImageChanged += (_, _) =>
        {
            if (_hmiWindow != null && _hmiProjectPath != _vm.ProjectPath)
            {
                _hmiWindow.Close();
                if (_hmiWindow != null)
                {
                    _hmiRuntime?.Disconnect("工程已切换：此 HMI 已断开运行数据，请保存布局后重新打开。");
                    _hmiProjectPath = _vm.ProjectPath;
                    return;
                }
                var hmiPath = HmiDocumentService.FindForProject(_vm.ProjectPath);
                if (hmiPath != null) OpenHmi(hmiPath);
            }
        };

        Loaded += async (_, _) =>
        {
            if (_startupCompleted) return;
            _startupCompleted = true;
            _vm.RefreshSearch(SearchBox.Text);
            foreach (var t in _vm.Tools)
                t.IsExpanded = false;
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1])) _vm.OpenProject(args[1]);
            else if (args.Length == 1)
            {
                var settings = StudioSettings.Current;
                if (settings.LoadProjectOnStartup)
                {
                    try
                    {
                        settings.Validate(); _vm.OpenProject(settings.ProjectPath);
                        if (!string.IsNullOrWhiteSpace(settings.ProgramName)) _vm.SelectedProgram = _vm.Programs.FirstOrDefault(p => p.Name == settings.ProgramName) ?? throw new ArgumentException("默认程序不存在：" + settings.ProgramName);
                        if (settings.OpenHmiOnStartup) OnShowHmi(this, null);
                        if (settings.RunOnStartup)
                        {
                            var initialProject = _vm.SelectedProgram?.Root;
                            await System.Threading.Tasks.Task.Delay(settings.StartupDelayMs);
                            if (!_isClosed && !_closeWhenStopped && _vm.CanEdit && ReferenceEquals(initialProject, _vm.SelectedProgram?.Root)) _ = _vm.RunContinuouslyAsync();
                        }
                    }
                    catch (Exception ex) { _vm.Status = "自动启动未完成：" + ex.Message; }
                }
            }
            StartStatusTimer();
            if (args.Contains("--run"))
                await _vm.RunInteractiveAsync();
            if (_isClosed || _closeWhenStopped) return;
            if (args.Contains("--params"))
                OnShowParams(this, null);
            if (args.Contains("--log"))
                OnShowLog(this, null);
            if (args.Contains("--hmi"))
                OnShowHmi(this, null);
            if (args.Contains("--hmi-editor"))
                OnEditHmi(this, null);
            if (args.Contains("--recipes")) OnManageRecipes(this, null);
            if (args.Contains("--globals")) OnManageGlobals(this, null);
            if (args.Contains("--devices")) OnDevices(this, null);
        };

        _vm.ImageChanged += (_, _) => RefreshImageViews();
        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(MainViewModel.SelectedProgram))
                return;
            SelectProgramInTree();
            // 不等运行或其他图像事件，立即切换画布
            RefreshImageViews(force: true);
        };
    }

    private void RefreshImageViews(bool force = false)
    {
        if (!force && StudioSettings.Current.FastMode && (_vm.IsRunning || _vm.IsCycling))
        {
            return;
        }
        ImageWorkspace.SetContext(_vm.ProjectPath + "|" + _vm.SelectedProgram?.Name);
        ImageWorkspace.SetLayout(_vm.GetCurrentVisualLayout());
        // 原版内核仍按算法 IM 池显示。
        // 每控件中间快照功能只接入自有源码内核。
        if (_vm.UseNativeKernel)
        {
            foreach (var pair in ImageWorkspace.Viewers)
            {
                if (!ImageWorkspace.IsPaneOpen(pair.Key))
                    continue;
                var viewer = pair.Value;
                int index = viewer.ViewIndex;

                ImageWorkspace.SetPaneCaption(pair.Key, $"IM{index}");
                var cloud = _vm.Clouds.Get(index);
                var image = _vm.Images.Get(index);

                if (_vm.Profiles.TryGetValue(index, out var profile))
                {
                    viewer.ShowProfile(profile);
                }
                else if (cloud != null)
                {
                    viewer.ShowCloud(cloud, _vm.TextOverlays.Where(item => item.ViewIndex < 0 || item.ViewIndex == index), _vm.BoxOverlays.Where(item => item.ViewIndex < 0 || item.ViewIndex == index));
                }
                else if (image != null)
                {
                    viewer.ShowMat(image, _vm.GetImageName(index));
                    viewer.OverlayText = index == 0 ? _vm.DrawText : "";
                }
                else
                {
                    viewer.Clear();
                }

                viewer.SetNativeOverlays(_vm.NativeOverlays.Where(item => item.ViewIndex < 0 || item.ViewIndex == index));
            }

            return;
        }

        var sources = GetAvailableVisualSources();

        ImageWorkspace.ClearDuplicateOpenSources();

        foreach (var pair in ImageWorkspace.Viewers)
        {
            if (!ImageWorkspace.IsPaneOpen(pair.Key))
            {
                if (ImageWorkspace.TryGetPaneSource(pair.Key, out var hiddenSource) && !sources.Any(item => item.Key == hiddenSource)) ImageWorkspace.ForgetPaneSource(pair.Key, _vm.LatestVisualVersion);
                continue;
            }

            int paneId = pair.Key;
            var viewer = pair.Value;

            if (!ImageWorkspace.TryGetPaneSource(paneId, out var sourceKey))
            {
                // 空窗口只选择没有被其他窗口占用的来源
                var initial = sources.FirstOrDefault(source => (!viewer.ClearedSnapshotVersion.HasValue || source.Version > viewer.ClearedSnapshotVersion.Value)
                    && !ImageWorkspace.TryFindOpenPane(source.Key, paneId, out _));

                if (initial == null)
                {
                    if (!viewer.ClearedSnapshotVersion.HasValue) viewer.Clear();

                    ImageWorkspace.SetPaneCaption(paneId, sources.Length == 0 ? "等待运行" : "等待选择");

                    continue;
                }

                int initialIndex = Array.FindIndex(sources, source => source.Key == initial.Key);

                if (!ImageWorkspace.BindPaneSource(paneId, initial.Key, VisualCaption(initial, initialIndex), initial.AlgorithmSlot))
                {
                    continue;
                }

                sourceKey = initial.Key;
            }

            int sourceIndex = Array.FindIndex(sources, source => source.Key == sourceKey);

            if (sourceIndex < 0)
            {
                var previous = sources.FirstOrDefault(item => item.Key.OperationId == sourceKey.OperationId && !ImageWorkspace.TryFindOpenPane(item.Key, paneId, out _))
                    ?? _vm.GetFallbackVisualSources(sourceKey).FirstOrDefault(item => !ImageWorkspace.TryFindOpenPane(item.Key, paneId, out _));
                if (previous != null)
                {
                    int previousIndex = Array.FindIndex(sources, item => item.Key == previous.Key);
                    if (previousIndex >= 0 && RenderPaneSnapshot(paneId, previous, previousIndex)) continue;
                }
                ImageWorkspace.ForgetPaneSource(paneId, _vm.LatestVisualVersion);
                continue;
            }

            if (!RenderPaneSnapshot(paneId, sources[sourceIndex], sourceIndex))
            {
                viewer.Clear();

                ImageWorkspace.SetPaneCaption(paneId, "暂无结果");
            }
        }

        PersistVisualLayout();
        RefreshImageBrowsers();
    }

    private void RefreshImageBrowsers()
    {
        foreach (var (paneId, viewer) in ImageWorkspace.Viewers)
        {
            viewer.SetThumbnailInteractionEnabled(_vm.CanEdit);
            ImageWorkspace.TryGetPaneSource(paneId, out var binding);
            var source = viewer.HasImage && ImageWorkspace.IsPaneOpen(paneId)
                ? _vm.GetImageBrowserSource(viewer.ViewIndex, binding?.OperationId) : null;
            viewer.SetThumbnailSource(source?.Key, source?.Files ?? Array.Empty<string>(), source?.CurrentFile, source?.OperationId);
        }
    }


    // ---------------- 窗口按钮 ----------------
    private void OnManageGlobals(object sender, RoutedEventArgs e)
    { if (_globalsWindow != null) { _globalsWindow.Activate(); return; } _globalsWindow = new GlobalVariablesWindow(_vm) { Owner = this }; _globalsWindow.Closed += (_, _) => _globalsWindow = null; _globalsWindow.Show(); }
    private void OnManageRecipes(object sender, RoutedEventArgs e)
    { if (_recipesWindow != null) { _recipesWindow.Activate(); return; } _recipesWindow = new RecipeManagerWindow(_vm) { Owner = this }; _recipesWindow.Closed += (_, _) => _recipesWindow = null; _recipesWindow.Show(); }
    private async void OnCycleRun(object sender, RoutedEventArgs e) => await _vm.RunContinuouslyAsync();
    private void OnShowHmi(object sender, RoutedEventArgs e)
    {
        if (_hmiWindow != null) { _hmiWindow.Activate(); return; }
        var path = HmiDocumentService.FindForProject(_vm.ProjectPath);
        if (path != null) OpenHmi(path);
        else OnOpenHmi(sender, e);
    }
    private void OnOpenHmi(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "ES-Vision HMI (*.aivh)|*.aivh", Title = "打开 HMI 文件" };
        if (!string.IsNullOrEmpty(_vm.ProjectPath)) dialog.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(_vm.ProjectPath));
        if (dialog.ShowDialog(this) == true) OpenHmi(dialog.FileName);
    }
    private void OnEditHmi(object sender, RoutedEventArgs e)
    {
        if (_hmiWindow == null)
        {
            var path = HmiDocumentService.FindForProject(_vm.ProjectPath);
            if (path != null) OpenHmi(path); else { OnNewHmi(sender, e); return; }
        }
        if (_hmiWindow != null) { _hmiWindow.IsDesignMode = true; _hmiWindow.Activate(); }
    }
    private void OnNewHmi(object sender, RoutedEventArgs e)
    {
        try { OpenHmi("", HmiDocumentService.Create()); if (_hmiWindow != null) _hmiWindow.IsDesignMode = true; }
        catch (Exception ex) { _vm.Status = "HMI 创建失败：" + ex.Message; }
    }
    private void OpenHmi(string path, AiVisionStudio.Models.HmiDocument document = null)
    {
        HmiRuntimeBridge runtime = null;
        HmiWindow window = null;
        try
        {
            document ??= HmiDocumentService.Load(path);
            _hmiWindow?.Close();
            if (_hmiWindow != null) return; // The editor can cancel closing when it has unsaved changes.
            runtime = new HmiRuntimeBridge(_vm);
            runtime.OperatorEditorRequested += OpenHmiOperator;
            window = new HmiWindow(runtime, document) { Owner = this, SuggestedSavePath = string.IsNullOrEmpty(_vm.ProjectPath) ? null : Path.ChangeExtension(_vm.ProjectPath, ".aivh") };
            _hmiRuntime = runtime;
            _hmiProjectPath = _vm.ProjectPath;
            _hmiWindow = window;
            window.Closed += (_, _) =>
            {
                runtime.Dispose();
                if (ReferenceEquals(_hmiWindow, window)) { _hmiWindow = null; _hmiRuntime = null; }
            };
            window.Show();
            _vm.Status = "已连接 HMI：" + Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            runtime?.Dispose();
            if (ReferenceEquals(_hmiWindow, window)) { _hmiWindow = null; _hmiRuntime = null; }
            window?.Close();
            _vm.Status = "HMI 打开失败：" + ex.Message;
        }
    }
    private void OpenHmiOperator(AiVisionStudio.Models.HmiElement element)
    {
        if (!_vm.CanEdit || !_vm.ApplyParameters()) return;
        var program = _vm.Programs.First(p => p.Name == element.GetString("option.bindPrograms"));
        var operation = program.Operations.First(op => op.Uuid == element.GetString("option.bindOperatorUUID"));
        var previousProgram = _vm.SelectedProgram; var previousOperation = _vm.SelectedOperation;
        try
        {
            _vm.SelectedProgram = program; _vm.SelectedOperation = operation;
            var editor = new ParamsWindow(_vm) { Owner = (Window)_hmiWindow ?? this }; editor.ShowDialog();
        }
        finally { if (_vm.CanEdit) { _vm.SelectedProgram = previousProgram; _vm.SelectedOperation = previousOperation; } }
    }
    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (ConfirmPendingChanges()) _vm.NewProject();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (ConfirmPendingChanges()) _vm.OpenCommand.Execute(null);
    }

    private void OnRecentProjectClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: string path } && ConfirmPendingChanges())
        {
            try { _vm.OpenRecentCommand.Execute(path); }
            catch (Exception ex) { _vm.Status = "打开失败：" + ex.Message; }
        }
        e.Handled = true;
    }

    private bool ConfirmPendingChanges()
    {
        if (!_vm.CanEdit) { _vm.Status = "请先停止运行，再切换工程"; return false; }
        if (_globalsWindow != null) { _globalsWindow.Close(); if (_globalsWindow != null) return false; }
        if (_recipesWindow != null) { _recipesWindow.Close(); if (_recipesWindow != null) return false; }
        var validHmiInput = _hmiWindow?.Inspector?.CommitPendingEdits() != false;
        if (_hmiWindow != null && (!validHmiInput || _hmiWindow.Editor.IsDirty))
        {
            _hmiWindow.Close();
            if (_hmiWindow != null) return false;
        }
        if (!_vm.IsDirty) return true;
        var result = MessageBox.Show(this, $"工程“{_vm.ProjectName}”有未保存的修改，是否保存？",
            "保存工程", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.No || result == MessageBoxResult.Yes && _vm.TrySave();
    }

    private void OnWindowClosing(object sender, CancelEventArgs e)
    {
        if (!_vm.CanEdit)
        {
            e.Cancel = true;
            _closeWhenStopped = true;
            _vm.StopCommand.Execute(null);
            _vm.Status = "正在停止运行...";
            return;
        }
        _closeWhenStopped = false;
        e.Cancel = !ConfirmPendingChanges();
        if (!e.Cancel && _hmiWindow != null) { _hmiWindow.Close(); e.Cancel = _hmiWindow != null; }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var editingText = Keyboard.FocusedElement is TextBox;
        if (control && e.Key == Key.F1 && _vm.RunCommand.CanExecute(null)) _vm.RunCommand.Execute(null);
        else if (control && e.Key == Key.F2 && _vm.StepCommand.CanExecute(null)) _vm.StepCommand.Execute(null);
        else if (control && e.Key == Key.F3) OnCycleRun(this, e);
        else if (control && e.Key == Key.F4) OnPause(this, e);
        else if (control && e.Key == Key.F6) OnClearResults(this, e);
        else if (control && e.Key == Key.F7) OnManageGlobals(this, e);
        else if (control && e.Key == Key.F8) OnDevices(this, e);
        else if (control && e.Key == Key.F9) OnManageRecipes(this, e);
        else if (!control && e.Key == Key.F1) OnManual(this, e);
        else if (control && e.Key == Key.N && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) OnNewProgram(this, e);
        else if (control && e.Key == Key.N) OnNewClick(this, e);
        else if (control && e.Key == Key.O) OnOpenClick(this, e);
        else if (control && e.Key == Key.S && _vm.CanEdit) _vm.TrySave(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        else if (!editingText && control && e.Key == Key.Z && _vm.UndoCommand.CanExecute(null)) _vm.UndoCommand.Execute(null);
        else if (!editingText && control && e.Key == Key.Y && _vm.RedoCommand.CanExecute(null)) _vm.RedoCommand.Execute(null);
        else if (!editingText && control && e.Key == Key.D) OnCtxDuplicate(this, e);
        else if (!editingText && Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.F2 && RunList.IsKeyboardFocusWithin) OnCtxRename(this, e);
        else if (!editingText && e.Key == Key.Delete) OnCtxDelete(this, e);
        else if (e.Key == Key.F5 && _vm.RunCommand.CanExecute(null)) _vm.RunCommand.Execute(null);
        else if (e.Key == Key.F8) OnShowHmi(this, e);
        else if (e.Key == Key.F10 && _vm.StepCommand.CanExecute(null)) _vm.StepCommand.Execute(null);
        else return;
        e.Handled = true;
    }

    // ---------------- 工具树 ----------------
    /// <summary>
    /// 工具树节点加载后，按实际 TreeViewItem 祖先数量调整字体。
    /// 在 Loaded 中计算，可以避免绑定转换器在模板构建时读取到未完整生成的层级。
    /// 搜索、展开、折叠以及虚拟化重新创建节点时均会自动执行。
    /// </summary>
    private void OnToolNodeTextLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock textBlock)
            return;

        int level = 0;
        bool belongsToToolTree = false;
        DependencyObject current = textBlock;

        while (current != null)
        {
            if (current is TreeViewItem)
                level++;

            if (ReferenceEquals(current, ToolTree))
            {
                belongsToToolTree = true;
                break;
            }

            // WPF 模板已加载，此时可以可靠地获取 TreeViewItem 的视觉祖先。
            // 保留逻辑树回退，兼容自定义的 ControlTemplate。
            DependencyObject parent = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : null;
            current = parent ?? LogicalTreeHelper.GetParent(current);
        }

        if (!belongsToToolTree || level == 0)
            return;

        textBlock.FontSize = level switch
        {
            1 => 18d,             // 一级：主目录
            2 => 15d,             // 二级：子目录
            _ => 13d              // 三级及更深：算法
        };

        textBlock.FontWeight = level switch
        {
            1 => FontWeights.SemiBold,
            2 => FontWeights.Medium,
            _ => FontWeights.Normal
        };
    }

    private void OnToolDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.CanEdit) return;
        if (ToolTree.SelectedItem is ToolNode node && node.IsLeaf)
            _vm.InsertTool(node, _vm.Operations.Count);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _toolSearchTimer.Stop();
        if (!_searchComposing) _toolSearchTimer.Start();
    }

    private void OnSearchAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!SearchBox.IsKeyboardFocusWithin) SearchBox.Focus();
    }

    private void OnSearchCompositionStarted(object sender, TextCompositionEventArgs e)
    {
        _searchComposing = true;
        _toolSearchTimer.Stop();
    }

    private void OnSearchCompositionCompleted(object sender, TextCompositionEventArgs e)
    {
        _searchComposing = false;
        OnSearchTextChanged(sender, null);
    }

    private void OnSearchKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        _searchComposing = false;
        OnSearchTextChanged(sender, null);
    }

    private void OnSearchFocusLost(object sender, KeyboardFocusChangedEventArgs e)
    {
        _searchComposing = false;
        OnSearchTextChanged(sender, null);
    }

    private void OnToolPreviewMouseDown(object sender, MouseButtonEventArgs e)
        => _toolDragStart = e.GetPosition(null);

    private void OnToolMouseMove(object sender, MouseEventArgs e)
    {
        if (!_vm.CanEdit) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - _toolDragStart.X) < 4 && Math.Abs(p.Y - _toolDragStart.Y) < 4) return;
        if (ToolTree.SelectedItem is ToolNode { IsLeaf: true } node)
            DragDrop.DoDragDrop(ToolTree, new DataObject("AivTool", node), DragDropEffects.Copy);
    }

    // ---------------- 运行列表 ----------------
    private Operation RunDragOperationFrom(DependencyObject source)
    {
        for (var current = source; current != null && current != RunList;)
        {
            if (current is System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Primitives.Thumb
                or System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.TextBoxBase) return null;
            if (current is ListBoxItem item)
                return ItemsControl.ItemsControlFromItemContainer(item) == RunList ? item.DataContext as Operation : null;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private static bool RunDragThresholdReached(Point start, Point current)
        => Math.Abs(current.X - start.X) >= Math.Max(8, SystemParameters.MinimumHorizontalDragDistance)
           || Math.Abs(current.Y - start.Y) >= Math.Max(8, SystemParameters.MinimumVerticalDragDistance);

    private void OnRunPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _runDragOperation = null;
        if (!_vm.CanEdit) return;
        var operation = RunDragOperationFrom(e.OriginalSource as DependencyObject);
        if (operation == null) return;
        if (e.ClickCount == 1)
        {
            _runDragStart = e.GetPosition(RunList);
            _runDragOperation = operation;
        }
        if (!_vm.UseNativeKernel && ReferenceEquals(RunList.SelectedItem, operation)) ShowImageFor(operation);
    }

    private void OnRunPreviewMouseUp(object sender, MouseButtonEventArgs e) => _runDragOperation = null;

    private void OnRunMouseMove(object sender, MouseEventArgs e)
    {
        if (!_vm.CanEdit || e.LeftButton != MouseButtonState.Pressed) { _runDragOperation = null; return; }
        var operation = _runDragOperation;
        if (operation == null || !_vm.Operations.Contains(operation)) return;
        if (!RunDragThresholdReached(_runDragStart, e.GetPosition(RunList))) return;
        _runDragOperation = null;
        DragDrop.DoDragDrop(RunList, new DataObject("AivOp", operation), DragDropEffects.Move);
        e.Handled = true;
    }

    private void OnRunDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_vm.CanEdit ? DragDropEffects.None
            : e.Data.GetDataPresent("AivTool") ? DragDropEffects.Copy
            : e.Data.GetDataPresent("AivOp") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnRunDrop(object sender, DragEventArgs e)
    {
        if (!_vm.CanEdit) return;
        var idx = DropIndex(e);
        if (e.Data.GetData("AivTool") is ToolNode node)
        {
            _vm.InsertTool(node, idx);
            e.Handled = true;
        }
        else if (e.Data.GetData("AivOp") is Operation op)
        {
            e.Effects = _vm.MoveOperationToInsertion(op, idx) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }
    }

    private int DropIndex(DragEventArgs e)
    {
        var pt = e.GetPosition(RunList);
        for (int i = 0; i < RunList.Items.Count; i++)
        {
            if (RunList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item)
            {
                var b = item.TransformToAncestor(RunList).TransformBounds(new Rect(item.RenderSize));
                if (pt.Y < b.Top + b.Height / 2) return i;
            }
        }
        return RunList.Items.Count;
    }

    private void OnRunPreviewMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(RunList, (DependencyObject)e.OriginalSource) is ListBoxItem item)
            item.IsSelected = true;
    }

    private void OnRunDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RunList.SelectedItem is Operation) OnShowParams(sender, e);
    }

    // ---------------- 右键菜单 ----------------
    private void OnCtxRename(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanEdit || RunList.SelectedItem is not Operation operation) return;
        var name = ProgramNameDialog.Ask(this, "重命名控件", operation.Name, "控件名称");
        if (name == null) return;
        try
        {
            _vm.RenameOperation(operation, name);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            _vm.Status = error.Message;
            MessageBox.Show(this, error.Message, "重命名控件", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnCtxDelete(object sender, RoutedEventArgs e)
    {
        if (_vm.CanEdit) _vm.RemoveOperation(RunList.SelectedItem as Operation);
    }

    private void OnCtxDuplicate(object sender, RoutedEventArgs e)
    {
        if (_vm.CanEdit) _vm.DuplicateOperation(RunList.SelectedItem as Operation);
    }

    private void OnCtxToggleEnabled(object sender, RoutedEventArgs e)
    {
        if (_vm.CanEdit) _vm.ToggleEnabled(RunList.SelectedItem as Operation);
    }

    private void OnCtxToggleBreakpoint(object sender, RoutedEventArgs e)
    {
        if (_vm.CanEdit) _vm.ToggleBreakpoint(RunList.SelectedItem as Operation);
    }

    // ---------------- 图像显示 ----------------
    private void OnRunSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_vm.CanEdit) return;
        if (RunList.SelectedItem is Operation op) ShowImageFor(op);
        RefreshImageBrowsers();
    }

    private void ShowImageFor(Operation op)
    {
        if (!_vm.UseNativeKernel)
        {
            FollowOperationOnSinglePane(op);
            return;
        }
        var file = GetParam(op, "File");
        if (string.IsNullOrWhiteSpace(file) || file == "\"\"") return;
        var extension = Path.GetExtension(file).ToLowerInvariant();
        if (extension is not (".bmp" or ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".gif")) return;
        var full = file;
        if (!Path.IsPathRooted(full))
        {
            var dir = string.IsNullOrEmpty(_vm.ProjectPath) ? null : Path.GetDirectoryName(_vm.ProjectPath);
            if (dir == null) return;
            full = Path.GetFullPath(Path.Combine(dir, full));
        }
        var outputSlot = int.TryParse(GetParam(op, "IM Out"), out var slot) ? slot : 0;
        if (!File.Exists(full)) _vm.Status = "图像不存在：" + full;
        else if (ImageWorkspace.Viewers.TryGetValue(outputSlot, out var viewer)) viewer.Load(full);
    }

    private static string GetParam(Operation op, string key)
    {
        if (op?.ParametersRaw is not YamlDotNet.RepresentationModel.YamlMappingNode root) return null;
        var value = Child(root, "value") as YamlDotNet.RepresentationModel.YamlMappingNode;
        var node = Child(value, key) as YamlDotNet.RepresentationModel.YamlScalarNode;
        return node?.Value;
    }

    private static YamlDotNet.RepresentationModel.YamlNode Child(
        YamlDotNet.RepresentationModel.YamlMappingNode node, string key)
    {
        if (node == null) return null;
        foreach (var kv in node.Children)
            if (kv.Key.ToString() == key) return kv.Value;
        return null;
    }

    private void OnFitAll(object sender, RoutedEventArgs e) => ImageWorkspace.FitAll();

    private void SaveImage(ImageView viewer)
        => SaveView(viewer, false);

    private void SaveView(ImageView viewer, bool raw)
    {
        if (!viewer.HasImage && !viewer.HasCloud && !viewer.HasProfile) { _vm.Status = "当前视图没有可保存的图像"; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = raw ? "保存原始数据" : "保存检测结果截图",
            Filter = raw && viewer.HasCloud ? "PCD 点云 (*.pcd)|*.pcd" : "PNG 图像 (*.png)|*.png",
            DefaultExt = raw && viewer.HasCloud ? ".pcd" : ".png",
            AddExtension = true,
            FileName = $"IM{viewer.ViewIndex}" + (raw && viewer.HasCloud ? ".pcd" : ".png")
        };
        if (dialog.ShowDialog(this) != true) return;
        try { if (raw) viewer.SaveRaw(dialog.FileName); else viewer.SaveScreenshot(dialog.FileName); _vm.Status = "已保存：" + dialog.FileName; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _vm.Status = "图片保存失败：" + ex.Message;
            MessageBox.Show(this, _vm.Status, "保存图片", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowImageActions(ImageView viewer, FrameworkElement button)
    {
        var menu = new ContextMenu { PlacementTarget = button };
        var fit = new MenuItem { Header = "适应窗口" };
        fit.Click += (_, _) => viewer.FitView();
        var save = new MenuItem { Header = viewer.HasCloud ? "保存点云视图..." : "保存图片...", IsEnabled = viewer.HasImage || viewer.HasCloud };
        save.Click += (_, _) => SaveImage(viewer);
        var clear = new MenuItem { Header = "清除图像及结果", IsEnabled = _vm.CanEdit };
        clear.Click += (_, _) => ClearDisplayedVisual(viewer);
        menu.Items.Add(fit); menu.Items.Add(save); menu.Items.Add(clear);
        var restore = new MenuItem { Header = "还原所有图像窗口" }; restore.Click += (_, _) => ImageWorkspace.RestorePanes(); menu.Items.Add(restore);
        menu.IsOpen = true;
    }

    private void ClearDisplayedVisual(ImageView viewer)
    {
        if (!_vm.CanEdit) return;
        int paneId = ImageWorkspace.Viewers.First(pair => ReferenceEquals(pair.Value, viewer)).Key;
        bool bound = ImageWorkspace.TryGetPaneSource(paneId, out var source);
        int slot = viewer.ViewIndex;
        ImageWorkspace.ForgetPaneSource(paneId, _vm.LatestVisualVersion);
        if (bound) _vm.ClearVisualResult(source); else _vm.ClearVisualSlot(slot);
        PersistVisualLayout();
    }

    // ---------------- 弹窗 ----------------
    private void OnShowParams(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanEdit) { _vm.Status = "请先停止运行，再编辑参数"; return; }
        if (_vm.SelectedOperation == null) { _vm.Status = "请先选择一个算子"; return; }
        var win = new ParamsWindow(_vm) { Owner = this };
        win.ShowDialog();
    }

    private void OnShowLog(object sender, RoutedEventArgs e)
    {
        if (_logWindow != null) { _logWindow.Activate(); return; }
        _logWindow = new LogWindow(_vm) { Owner = this };
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show();
    }

    // ---------------- 其它 ----------------
    private void OnProgramSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_vm.CanEdit && e.NewValue is ProgramModel p) _vm.SelectedProgram = p;
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnChineseLanguage(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item) item.IsChecked = true;
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
        => MessageBox.Show(this, "ES-Vision-Studio V1\n\nWPF 兼容复刻版本\n当前已实现的功能以界面和验证报告为准。",
            "关于", MessageBoxButton.OK, MessageBoxImage.Information);

    private void OnToggleProjectPanel(object sender, RoutedEventArgs e) => ApplyPanelVisibility();
    private void OnToggleToolPanel(object sender, RoutedEventArgs e) => ApplyPanelVisibility();
    private void OnToggleRunPanel(object sender, RoutedEventArgs e) => ApplyPanelVisibility();
    private void OnToggleVariablesPanel(object sender, RoutedEventArgs e) => ApplyPanelVisibility();
    private void OnHideProjectPanel(object sender, RoutedEventArgs e) { ProjectPanelMenu.IsChecked = false; ApplyPanelVisibility(); }
    private void OnHideToolPanel(object sender, RoutedEventArgs e) { ToolPanelMenu.IsChecked = false; ApplyPanelVisibility(); }
    private void OnHideRunPanel(object sender, RoutedEventArgs e) { RunPanelMenu.IsChecked = false; ApplyPanelVisibility(); }
    private void OnHideVariablesPanel(object sender, RoutedEventArgs e) { VariablesPanelMenu.IsChecked = false; ApplyPanelVisibility(); }

    private void ApplyPanelVisibility()
    {
        ProjectPanel.Visibility = ProjectPanelMenu.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        ToolPanel.Visibility = ToolPanelMenu.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        RunPanel.Visibility = RunPanelMenu.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        VariablesPanel.Visibility = VariablesPanelMenu.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        ProjectRow.Height = ProjectPanelMenu.IsChecked ? new GridLength(30, GridUnitType.Star) : new GridLength(0);
        ToolRow.Height = ToolPanelMenu.IsChecked ? new GridLength(70, GridUnitType.Star) : new GridLength(0);
        LeftSplitterRow.Height = new GridLength(ProjectPanelMenu.IsChecked && ToolPanelMenu.IsChecked ? 5 : 0);
        LeftColumn.Width = ProjectPanelMenu.IsChecked || ToolPanelMenu.IsChecked ? new GridLength(16, GridUnitType.Star) : new GridLength(0);
        RunColumn.Width = RunPanelMenu.IsChecked ? new GridLength(23, GridUnitType.Star) : new GridLength(0);
        RunSplitterColumn.Width = new GridLength(RunPanelMenu.IsChecked ? 5 : 0);
        VariablesRow.Height = new GridLength(VariablesPanelMenu.IsChecked ? 220 : 0);
        VariablesSplitterRow.Height = new GridLength(VariablesPanelMenu.IsChecked ? 5 : 0);
        UpdateResponsiveLayout();
    }

    private void OnResetLayout(object sender, RoutedEventArgs e)
    {
        ProjectPanelMenu.IsChecked = ToolPanelMenu.IsChecked = RunPanelMenu.IsChecked = VariablesPanelMenu.IsChecked = true;
        ApplyPanelVisibility();
        ImageWorkspace.SetLayout(_vm.GetCurrentVisualLayout(), force: true);
        RefreshImageViews();
        ImageWorkspace.FitAll();
        _vm.Status = "已恢复默认布局";
    }
    private void OnRestoreImagePanes(object sender, RoutedEventArgs e) => ImageWorkspace.RestorePanes();
    private void OnGlobalVariables(object sender, RoutedEventArgs e)
    { VariablesTitle.Text = "全局变量"; VariablesTree.SetBinding(ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding(nameof(MainViewModel.GlobalVars))); }
    private void OnOperationOutputs(object sender, RoutedEventArgs e)
    { VariablesTitle.Text = "算子输出"; VariablesTree.SetBinding(ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding(nameof(MainViewModel.SelectedOutputs))); }
    private void OnVariableExpanded(object sender, RoutedEventArgs e)
    { if (e.OriginalSource is TreeViewItem { DataContext: VarRow row }) row.Expand(); }
    private void OnTileImages(object sender, RoutedEventArgs e) => ImageWorkspace.TilePanes();
    private void OnOpenImagePane(object sender, RoutedEventArgs e)
    {
        // 原版内核保留现有的 IM 窗口打开方式
        if (_vm.UseNativeKernel)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = sender as FrameworkElement
            };
            for (int i = 0; i < 16; i++)
            {
                int index = i;
                var item = new MenuItem
                {
                    Header = "IM" + index
                };
                item.Click += (_, _) =>
                {
                    ImageWorkspace.OpenPane(index);
                    RefreshImageViews(force: true);
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
            return;
        }

        ShowVisualSourcesMenu(sender as FrameworkElement, paneId: null);
    }

    // ---------------- 状态栏 ----------------
    private void StartStatusTimer()
    {
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        _lastCpuTime = proc.TotalProcessorTime;
        _lastCpuStamp = DateTime.Now;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) =>
        {
            try
            {
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                var now = DateTime.Now;
                var cpuNow = p.TotalProcessorTime;
                var elapsed = (now - _lastCpuStamp).TotalMilliseconds;
                if (elapsed > 50)
                {
                    var used = (cpuNow - _lastCpuTime).TotalMilliseconds;
                    var pct = used / (elapsed * Environment.ProcessorCount) * 100;
                    _vm.CpuText = $"CPU:{Math.Clamp(pct, 0, 100):0.0}%";
                }
                _lastCpuTime = cpuNow;
                _lastCpuStamp = now;

                var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ms))
                {
                    var total = ms.ullTotalPhys / 1073741824.0;
                    var avail = ms.ullAvailPhys / 1073741824.0;
                    _vm.MemText = $"系统内存:{total - avail:0.0}/{total:0.0} GB";
                }
            }
            catch { /* 忽略 */ }
        };
        _statusTimer.Start();
    }

    [StructLayout(LayoutKind.Sequential)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
}
