using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Algorithm.Impl;
using Microsoft.Win32;
using OpenCvSharp;
using YamlDotNet.RepresentationModel;
using Path = System.Windows.Shapes.Path;

namespace AiVisionStudio.Views;

/// <summary>Edits an isolated draft. The project is updated only by a successful training operation.</summary>
public partial class TemplateConfigurationWindow : System.Windows.Window
{
    private readonly Func<Mat> _currentImage;
    private readonly string _matchingMethod;
    private readonly Action _imageReplaced;
    private readonly Func<ContourTemplateModel, IReadOnlyDictionary<string, object>, bool> _save;
    private readonly string _regionKey;
    private Dictionary<string, object> _draft;
    private readonly Stack<string> _undo = new(), _redo = new();
    private Mat _image;
    private ContourTemplateModel _model;
    private CancellationTokenSource _training;
    private bool _ready, _updating, _busy, _closePending, _dirty, _showFeatures;
    private Button _undoButton, _redoButton;
    public bool IsTraining => _busy;

    public TemplateConfigurationWindow(Dictionary<string, object> values, string regionKey, Mat image,
        ContourTemplateModel model, Func<Mat> currentImage,
        Func<ContourTemplateModel, IReadOnlyDictionary<string, object>, bool> save, string matchingMethod = "Contour", Action imageReplaced = null)
    {
        InitializeComponent();
        _matchingMethod = matchingMethod; _imageReplaced = imageReplaced;
        WindowScreenBehavior.Attach(this);
        _draft = Copy(values); _regionKey = regionKey; _currentImage = currentImage; _save = save;
        _image = image?.Clone();
        _model = model == null ? null : ContourTemplateEngine.DeserializeModel(ContourTemplateEngine.SerializeModel(model));
        _showFeatures = _model != null;
        CanvasView.ConfigureTemplateCanvas();
        BuildToolbar();
        if (_matchingMethod == "Gray")
        {
            Title = "灰度模板配置";
            foreach (var child in ((Grid)ThresholdMode.Parent).Children.OfType<FrameworkElement>().Where(child => Grid.GetRow(child) >= 2)) child.Visibility = Visibility.Collapsed;
        }
        StoreImage.IsChecked = model == null || model.SourceImagePng?.Length > 10;
        ReadControls(); RefreshCanvas(); RefreshResults();
        _ready = true;
        Status.Text = _image == null ? "请选择图像，再绘制模板区域" : "选择区域后点击训练，模板将直接保存到工程";
        Closing += OnEditorClosing;
        Closed += (_, _) => { CanvasView.CancelRegionEdit(); CanvasView.Clear(); _image?.Dispose(); _image = null; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !CanvasView.IsRegionEditing) { Close(); e.Handled = true; }
            if (Keyboard.Modifiers != ModifierKeys.Control || Keyboard.FocusedElement is TextBox) return;
            if (e.Key == Key.Z) { Undo(); e.Handled = true; }
            if (e.Key == Key.Y) { Redo(); e.Handled = true; }
        };
    }

    private void BuildToolbar()
    {
        AddTool(EditTools, "选择 / 调整区域", "M3,2 L3,21 8,16 12,23 15,21 11,14 18,14 Z", EditRegion);
        AddTool(EditTools, "多边形", "M3,19 L8,5 21,2 21,20 Z M2,18 L5,18 5,21 2,21 Z M7,3 L10,3 10,6 7,6 Z", () => BeginRegion("polygon2d"));
        AddTool(EditTools, "圆形", "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 M12,12 L18,6", () => BeginRegion("circle2d"));
        var rectangle = AddTool(EditTools, "矩形（右键可选扇形、圆环）", "M3,4 L21,4 21,20 3,20 Z M1,2 L5,2 5,6 1,6 Z M19,18 L23,18 23,22 19,22 Z", () => BeginRegion("rect2d"));
        rectangle.ContextMenu = new ContextMenu();
        foreach (var (label, type) in new[] { ("扇形", "sector2d"), ("圆环", "ring2d") })
        {
            var item = new MenuItem { Header = label }; item.Click += (_, _) => BeginRegion(type); rectangle.ContextMenu.Items.Add(item);
        }
        var brush = AddTool(EditTools, "画笔屏蔽（右键设置笔刷半径）", "M5,16 L15,3 21,8 11,21 3,22 Z M14,5 L19,10", () => BeginPaint(false));
        brush.ContextMenu = new ContextMenu();
        foreach (var radius in new[] { 3, 5, 10, 20, 40 })
        {
            var item = new MenuItem { Header = $"半径 {radius} px" }; item.Click += (_, _) => _brushRadius = radius; brush.ContextMenu.Items.Add(item);
        }
        AddTool(EditTools, "设置匹配中心", "M2,8 L2,2 8,2 M16,2 L22,2 22,8 M22,16 L22,22 16,22 M8,22 L2,22 2,16 M12,6 L12,18 M6,12 L18,12", BeginOrigin);
        AddTool(EditTools, "完成绘制（Enter）", "M3,3 L17,3 17,7 M3,3 L3,21 21,21 21,13 M8,12 L12,16 22,6", () => CanvasView.CompleteRegionEdit());
        var eraser = AddTool(EditTools, "橡皮擦：恢复屏蔽特征（右键清除全部屏蔽）", "M3,15 L14,3 22,10 11,22 9,22 Z M8,10 L17,17 M10,22 L23,22", () => BeginPaint(true));
        eraser.ContextMenu = new ContextMenu();
        var clear = new MenuItem { Header = "清除全部屏蔽" }; clear.Click += (_, _) => ClearMask(); eraser.ContextMenu.Items.Add(clear);
        AddTool(EditTools, "删除模板区域", "M3,5 L21,5 M8,5 L8,2 16,2 16,5 M5,5 L6,22 18,22 19,5 M10,9 L10,18 M14,9 L14,18", () => Change(() => _draft[_regionKey] = EmptyRegion()));
        _undoButton = AddTool(EditTools, "撤销（Ctrl+Z）", "M9,4 L3,9 9,14 M3,9 L15,9 C23,9 23,21 15,21 L9,21", Undo);
        _redoButton = AddTool(EditTools, "重做（Ctrl+Y）", "M15,4 L21,9 15,14 M21,9 L9,9 C1,9 1,21 9,21 L15,21", Redo);
        AddTool(ZoomTools, "放大", "M17,10 A7,7 0 1 1 3,10 A7,7 0 1 1 17,10 M15,15 L23,23 M6,10 L14,10 M10,6 L10,14", () => CanvasView.ZoomTemplateCanvas(1.2));
        AddTool(ZoomTools, "缩小", "M17,10 A7,7 0 1 1 3,10 A7,7 0 1 1 17,10 M15,15 L23,23 M6,10 L14,10", () => CanvasView.ZoomTemplateCanvas(1 / 1.2));
        var actual = new Button { Content = "1:1", Width = 34, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, ToolTip = "原始比例" };
        actual.Click += (_, _) => CanvasView.ShowTemplateActualSize(); ZoomTools.Children.Add(actual);
        AddTool(ZoomTools, "适应窗口", "M2,3 L22,3 22,21 2,21 Z M6,10 L6,7 10,7 M14,17 L18,17 18,14 M6,7 L11,12 M18,17 L13,12", () => CanvasView.Fit());
    }

    private static Button AddTool(Panel panel, string label, string geometry, Action action)
    {
        var button = new Button { ToolTip = label, Width = 31, Height = 34, Padding = new Thickness(5), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Focusable = false,
            Content = new Path { Data = Geometry.Parse(geometry), Stroke = new SolidColorBrush(Color.FromRgb(132, 139, 148)), StrokeThickness = 1.5, Stretch = Stretch.Uniform, Width = 21, Height = 21 } };
        button.Click += (_, _) => action(); panel.Children.Add(button); return button;
    }

    public void StartMaskEditing() => BeginPaint(false);
    private void OnMaskPaint(object sender, RoutedEventArgs e) => BeginPaint(false);
    private void OnMaskErase(object sender, RoutedEventArgs e) => BeginPaint(true);
    private void OnMaskClear(object sender, RoutedEventArgs e) => ClearMask();
    private double _brushRadius = 10;
    private void OnCurrentImage(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            using var source = _currentImage?.Invoke();
            if (source == null || source.IsDisposed || source.Empty()) throw new InvalidOperationException("当前输入没有图像，请选择其他图像或先加载上游图像。");
            LoadImage(source);
        }
        catch (Exception exception) { ShowError(exception); }
    }
    private void OnOtherImage(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var picker = new OpenFileDialog { Title = "选择模板训练图像", Filter = "图像文件|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            using var image = Cv2.ImRead(picker.FileName, ImreadModes.Unchanged);
            if (image.Empty()) throw new InvalidOperationException("无法读取所选图像。");
            LoadImage(image);
        }
        catch (Exception exception) { ShowError(exception); }
    }
    private void LoadImage(Mat image)
    {
        CanvasView.CancelRegionEdit();
        bool changed = _image != null && ContourTemplateEngine.ImageFingerprint(_image) != ContourTemplateEngine.ImageFingerprint(image);
        if (changed) { _imageReplaced?.Invoke(); _model = null; }
        _image?.Dispose(); _image = image.Clone(); _showFeatures = false;
        // History belongs to one image; geometry from another image cannot be replayed here.
        _undo.Clear(); _redo.Clear();
        _draft[_regionKey] = EmptyRegion();
        _draft["Use Shielding Area"] = false; _draft["Shielding Strokes"] = Array.Empty<object>();
        _draft["Model Origin"] = Map(new { Auto = true, X = 0d, Y = 0d, Angle = 0d, Locked = false });
        _dirty = true; ReadControls(); RefreshCanvas(); CanvasView.Fit();
        Status.Text = "图像已载入，请绘制模板区域；换图后旧模板已清除，请重新训练";
    }

    private bool EnsureImage()
    {
        if (_busy) return false;
        if (_image != null && !_image.IsDisposed && !_image.Empty()) return true;
        Status.Text = "请先选择当前图像或其他图像"; return false;
    }
    private void EditRegion()
    {
        var region = Group(_draft, _regionKey);
        BeginRegion(region?.GetValueOrDefault("Type")?.ToString() ?? "rect2d", true);
    }
    private void BeginRegion(string type, bool edit = false)
    {
        if (!EnsureImage()) return;
        CanvasView.BeginRegionEdit(type, edit ? Group(_draft, _regionKey) : null,
            value => Change(() => _draft[_regionKey] = value), "模板区域：拖动画选，Enter 完成");
    }
    private void BeginPaint(bool erase)
    {
        if (!EnsureImage()) return;
        CanvasView.BeginMaskPaint(_brushRadius, strokes => Change(() =>
        {
            var values = (_draft.GetValueOrDefault("Shielding Strokes") as IEnumerable)?.Cast<object>().ToList() ?? new();
            values.AddRange(strokes.Select(stroke => (object)new Dictionary<string, object>(stroke) { ["Erase"] = erase }));
            _draft["Shielding Strokes"] = values;
        }));
        Status.Text = erase ? "涂抹要恢复的特征，Enter 应用橡皮擦" : "涂抹要忽略的特征，Enter 应用屏蔽";
    }
    private void ClearMask()
    {
        if (_busy) return;
        Change(() => { _draft["Use Shielding Area"] = false; _draft["Shielding Strokes"] = Array.Empty<object>(); });
    }
    private void BeginOrigin()
    {
        if (!EnsureImage()) return;
        if (LockCenter.IsChecked == true) { Status.Text = "匹配中心已锁定，请先取消锁定"; return; }
        var (x, y) = Origin();
        var initial = Map(new { Type = "origin", origin = new { X = x, Y = y, Angle = Number(Group(_draft, "Model Origin"), "Angle") } });
        CanvasView.BeginRegionEdit("origin", initial, value =>
        {
            var origin = Group(value, "origin") ?? value;
            var point = Group(origin, "Point") ?? origin;
            Change(() => _draft["Model Origin"] = Map(new { Auto = false, X = Number(point, "X"), Y = Number(point, "Y"), Angle = Number(origin, "Angle"), Locked = false }));
        }, "匹配中心：拖动十字设置位置，拖动方向柄调整方向");
    }

    private void Change(Action change)
    {
        if (_busy) return;
        CanvasView.CancelRegionEdit();
        _undo.Push(JsonSerializer.Serialize(_draft)); _redo.Clear(); change();
        _dirty = true; ReadControls(); RefreshCanvas();
        Status.Text = "区域或参数已修改，点击训练后更新工程模板";
    }
    private void Undo() => RestoreHistory(_undo, _redo);
    private void Redo() => RestoreHistory(_redo, _undo);
    private void RestoreHistory(Stack<string> source, Stack<string> target)
    {
        if (_busy || source.Count == 0) return;
        CanvasView.CancelRegionEdit(); target.Push(JsonSerializer.Serialize(_draft));
        _draft = Map(JsonSerializer.Deserialize<JsonElement>(source.Pop()));
        _dirty = true; ReadControls(); RefreshCanvas(); Status.Text = "已恢复编辑草稿，训练后生效";
    }

    private void ReadControls()
    {
        _updating = true;
        var levels = Group(Group(_draft, "Create Model"), "Num Levels");
        var threshold = Group(Group(_draft, "Parameters"), "Edge Threshold");
        ScaleMode.SelectedIndex = Boolean(levels, "Auto", true) ? 0 : 1;
        var scale = ScaleMode.SelectedIndex == 0 ? _model?.PyramidLevels ?? 3 : Number(levels, "Value", 3);
        FeatureScale.Text = Math.Clamp(scale, 1, 10).ToString("0.0", CultureInfo.InvariantCulture);
        ThresholdMode.SelectedIndex = Boolean(threshold, "Auto", true) ? 0 : 1;
        var edge = Number(Group(threshold, "Value"), "max", 20);
        ContrastThreshold.Text = (edge > 0 ? edge : 20).ToString("0.##", CultureInfo.InvariantCulture);
        var (x, y) = Origin();
        CenterX.Text = x.ToString("0.##", CultureInfo.InvariantCulture); CenterY.Text = y.ToString("0.##", CultureInfo.InvariantCulture);
        LockCenter.IsChecked = Boolean(Group(_draft, "Model Origin"), "Locked", false);
        ShieldAll.IsChecked = IsAllShielded();
        UpdateEnabledControls(); _updating = false;
    }
    private void UpdateEnabledControls()
    {
        FeatureScale.IsEnabled = ScaleMode.SelectedIndex == 1; ContrastThreshold.IsEnabled = ThresholdMode.SelectedIndex == 1;
        CenterX.IsReadOnly = CenterY.IsReadOnly = LockCenter.IsChecked == true;
        _undoButton.IsEnabled = !_busy && _undo.Count > 0; _redoButton.IsEnabled = !_busy && _redo.Count > 0;
    }
    private bool SyncControls()
    {
        if (!TryNumber(FeatureScale.Text, out var levels) || levels < 1 || levels > 10 || levels != Math.Truncate(levels))
        { Status.Text = "特征尺度请输入 1～10 的整数层数"; return false; }
        if (!TryNumber(ContrastThreshold.Text, out var threshold) || threshold <= 0 || threshold > 255)
        { Status.Text = "对比度阈值请输入大于 0 且不超过 255 的数值"; return false; }
        if (!TryNumber(CenterX.Text, out var x) || !TryNumber(CenterY.Text, out var y))
        { Status.Text = "匹配中心请输入有效坐标"; return false; }
        var before = JsonSerializer.Serialize(_draft);
        var create = EnsureGroup(_draft, "Create Model");
        create["Num Levels"] = Map(new { Auto = ScaleMode.SelectedIndex == 0, Value = (int)levels });
        var parameters = EnsureGroup(_draft, "Parameters");
        var edge = EnsureGroup(parameters, "Edge Threshold"); edge["Auto"] = ThresholdMode.SelectedIndex == 0;
        // Keep a saved two-threshold range until the user actually changes the displayed upper threshold.
        if (Math.Abs(Number(Group(edge, "Value"), "max") - threshold) > .00001)
            edge["Value"] = Map(new { min = threshold * .4, max = threshold });
        var origin = EnsureGroup(_draft, "Model Origin");
        var (previousX, previousY) = Origin();
        if (Math.Abs(x - previousX) > .011 || Math.Abs(y - previousY) > .011)
        { origin["Auto"] = false; origin["X"] = x; origin["Y"] = y; }
        origin["Locked"] = LockCenter.IsChecked == true;
        if (before != JsonSerializer.Serialize(_draft)) { _undo.Push(before); _redo.Clear(); _dirty = true; }
        UpdateEnabledControls(); return true;
    }
    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updating || _busy) return;
        if (SyncControls()) { RefreshCanvas(); Status.Text = "配置已修改，训练后生效"; }
    }
    private void OnNumberChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_ready || _updating || _busy) return;
        if (SyncControls()) RefreshCanvas();
    }
    private void OnSettingChanged(object sender, RoutedEventArgs e)
    { if (_ready && !_updating && !_busy) { _dirty = true; Status.Text = "存图设置将在下次训练时生效"; } }
    private void OnLockCenter(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updating || _busy) return;
        if (CanvasView.IsRegionEditing) CanvasView.CancelRegionEdit();
        if (SyncControls()) RefreshCanvas();
    }
    private void OnShieldAll(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updating || !EnsureImage()) return;
        var enabled = ShieldAll.IsChecked == true;
        Change(() =>
        {
            _draft["Use Shielding Area"] = enabled;
            if (enabled)
            {
                _draft["Shielding Area"] = Map(new { Type = "rect2d", rect2d = new { Center = new { X = (_image.Width - 1) / 2d, Y = (_image.Height - 1) / 2d }, Size = new { Width = (double)_image.Width, Height = (double)_image.Height }, Rotation = 0d } });
                _draft["Shielding Strokes"] = Array.Empty<object>();
            }
        });
        if (enabled) Status.Text = "已全部屏蔽，用橡皮擦恢复需要保留的轮廓后再训练";
    }
    private bool IsAllShielded()
    {
        var area = Group(Group(_draft, "Shielding Area"), "rect2d");
        return _image != null && Boolean(_draft, "Use Shielding Area", false) && Number(Group(area, "Size"), "Width") >= _image.Width && Number(Group(area, "Size"), "Height") >= _image.Height;
    }

    private async void OnTrain(object sender, RoutedEventArgs e)
    {
        if (_busy) { _training?.Cancel(); return; }
        if (!EnsureImage()) return;
        CanvasView.CompleteRegionEdit();
        if (CanvasView.IsRegionEditing) { Status.Text = "请完成有效的区域绘制，或按 Esc 取消"; return; }
        if (!SyncControls()) return;
        try
        {
            var values = Copy(_draft);
            values["Region"] = values[_regionKey];
            values["Matching Method"] = _matchingMethod;
            var parameters = ParamBag.FromYaml(ToYaml(JsonSerializer.SerializeToElement(values)));
            using var image = _image.Clone();
            using var cancellation = new CancellationTokenSource(); _training = cancellation;
            SetBusy(true); Status.Text = "正在训练模板…";
            var model = await Task.Run(() => ContourTemplateEngine.Train(image, parameters, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closePending) return;
            if (StoreImage.IsChecked != true) model.SourceImagePng = Array.Empty<byte>();
            if (!_save(model, Copy(_draft))) throw new InvalidOperationException("工程模板未能更新，请检查当前工具及参数状态。");
            _model = model; _dirty = false; _showFeatures = true;
            ReadControls(); RefreshCanvas(); RefreshResults();
            Status.Text = _matchingMethod == "Gray" ? "NCC灰度模板训练成功，已保存到工程" : $"训练成功，{model.Features.Count} 个特征，模板已保存到工程";
        }
        catch (OperationCanceledException) { Status.Text = "训练已取消，保留已保存的模板"; }
        catch (Exception exception) { ShowError(exception); }
        finally
        {
            _training = null; SetBusy(false);
            if (_closePending) Close();
        }
    }
    private void SetBusy(bool busy)
    {
        _busy = busy;
        SourceActions.IsEnabled = SettingsPanel.IsEnabled = CenterPanel.IsEnabled = EditTools.IsEnabled = CanvasView.IsEnabled = !busy;
        TrainButton.Content = busy ? "停止训练" : "训练"; UpdateEnabledControls();
    }
    private void OnEditorClosing(object sender, CancelEventArgs e)
    {
        if (!_busy) return;
        _closePending = true; _training?.Cancel(); e.Cancel = true; Status.Text = "正在取消训练，原模板会保留";
    }
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not TextBlock && !ReferenceEquals(e.OriginalSource, sender)) return;
        if (e.ClickCount == 2) OnMaximize(sender, e); else DragMove();
    }
    private void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_model != null && !_busy) Status.Text = _matchingMethod == "Gray" ? $"灰度相关模板：{_model.Width} × {_model.Height} px" : $"工程模板：{_model.Features.Count} 个特征，{_model.Width} × {_model.Height} px";
    }
    private void RefreshResults()
    {
        TemplateResults.Items.Clear();
        if (_model == null) return;
        var row = new StackPanel { Margin = new Thickness(2, 8, 2, 8) };
        row.Children.Add(new TextBlock { Text = "模板 1", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 7) });
        row.Children.Add(new TextBlock { Text = _matchingMethod == "Gray" ? "灰度相关模板" : $"{_model.Features.Count} 个特征 · {_model.Contours.Count} 条轮廓", FontSize = 12, Foreground = Brushes.SlateGray });
        row.Children.Add(new TextBlock { Text = $"{_model.Width} × {_model.Height} px", FontSize = 12, Margin = new Thickness(0, 5, 0, 0), Foreground = Brushes.SlateGray });
        row.Children.Add(new TextBlock { Text = $"中心：({_model.OriginX:0.##}, {_model.OriginY:0.##})", FontSize = 12, Margin = new Thickness(0, 5, 0, 0), Foreground = Brushes.SlateGray });
        row.Children.Add(new TextBlock { Text = "已保存到工程", FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(39, 135, 87)) });
        TemplateResults.Items.Add(row); TemplateResults.SelectedIndex = 0;
    }
    private void RefreshCanvas()
    {
        if (_image == null) { CanvasView.Clear(); return; }
        CanvasView.ShowMat(_image, "模板训练图像");
        var overlays = new List<NativeOverlay3D>();
        AddRegion(overlays, _draft.GetValueOrDefault(_regionKey), "#00C5E0");
        if (Boolean(_draft, "Use Shielding Area", false)) AddRegion(overlays, _draft.GetValueOrDefault("Shielding Area"), "#FF6060");
        if (_draft.GetValueOrDefault("Shielding Strokes") is IEnumerable strokes)
            foreach (var stroke in strokes)
                AddRegion(overlays, stroke, stroke is IReadOnlyDictionary<string, object> map && Boolean(map, "Erase", false) ? "#80E5A0" : "#FF6060");
        if (_model != null && _showFeatures)
            foreach (var contour in _model.Contours.Where(c => c.Count > 1))
                overlays.Add(new NativeOverlay3D { Type = "trace2d", Values = new() { ["Points"] = contour.Select(p => (object)new Dictionary<string, object> { ["X"] = p.X + _model.OffsetX, ["Y"] = p.Y + _model.OffsetY }).ToArray(), ["_color"] = _dirty ? "#E6B74F" : "#00FF70", ["_thickness"] = 1 } });
        var (x, y) = Origin();
        double angle = Boolean(Group(_draft, "Model Origin"), "Auto", true) ? _model?.OriginAngle ?? 0 : Number(Group(_draft, "Model Origin"), "Angle");
        overlays.Add(new NativeOverlay3D { Type = "origin2d", Values = new() { ["X"] = x, ["Y"] = y, ["Angle"] = angle, ["_size"] = 12, ["_color"] = "#FFE050" } });
        CanvasView.SetNativeOverlays(overlays);
    }
    private (double x, double y) Origin()
    {
        var origin = Group(_draft, "Model Origin");
        if (!Boolean(origin, "Auto", true)) return (Number(origin, "X"), Number(origin, "Y"));
        if (_model != null && !_dirty && _showFeatures) return (_model.OriginX, _model.OriginY);
        var region = Group(_draft, _regionKey); var type = region?.GetValueOrDefault("Type")?.ToString() ?? "rect2d";
        var center = Group(Group(region, type), "Center");
        if (center != null && (Number(center, "X") != 0 || Number(center, "Y") != 0)) return (Number(center, "X"), Number(center, "Y"));
        return (_image?.Width / 2d ?? 0, _image?.Height / 2d ?? 0);
    }
    private static void AddRegion(List<NativeOverlay3D> overlays, object raw, string color)
    {
        if (raw is IEnumerable sequence && raw is not IReadOnlyDictionary<string, object> && raw is not string)
        { foreach (var item in sequence) AddRegion(overlays, item, color); return; }
        if (raw is not IReadOnlyDictionary<string, object> region) return;
        var type = region.GetValueOrDefault("Type")?.ToString() ?? "rect2d";
        if (type.EndsWith(" array", StringComparison.Ordinal) && region.GetValueOrDefault(type) is IEnumerable items)
        {
            foreach (var item in items) AddRegion(overlays, new Dictionary<string, object> { ["Type"] = type[..^6], [type[..^6]] = item }, color);
            return;
        }
        if (Group(region, type) is { } geometry) overlays.Add(new NativeOverlay3D { Type = type, Values = new Dictionary<string, object>(geometry) { ["_color"] = color, ["_thickness"] = 2 } });
    }
    private void ShowError(Exception exception) { Status.Text = exception.Message; Status.ToolTip = exception.ToString(); }
    private static bool TryNumber(string text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    private static double Number(IReadOnlyDictionary<string, object> values, string key, double fallback = 0)
        => values != null && TryNumber(values.GetValueOrDefault(key)?.ToString(), out var value) ? value : fallback;
    private static bool Boolean(IReadOnlyDictionary<string, object> values, string key, bool fallback)
        => values != null && bool.TryParse(values.GetValueOrDefault(key)?.ToString(), out var value) ? value : fallback;
    private static Dictionary<string, object> Group(IReadOnlyDictionary<string, object> values, string key) => values?.GetValueOrDefault(key) as Dictionary<string, object>;
    private static Dictionary<string, object> EnsureGroup(Dictionary<string, object> values, string key)
    { if (Group(values, key) is { } group) return group; var result = new Dictionary<string, object>(); values[key] = result; return result; }
    private static Dictionary<string, object> EmptyRegion() => Map(new { Type = "rect2d", rect2d = new { Center = new { X = 0d, Y = 0d }, Size = new { Width = 0d, Height = 0d }, Rotation = 0d } });
    private static Dictionary<string, object> Copy(object value) => Map(JsonSerializer.SerializeToElement(value));
    private static Dictionary<string, object> Map(object value) => (Dictionary<string, object>)FromJson(value is JsonElement element ? element : JsonSerializer.SerializeToElement(value));
    private static object FromJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.Number => value.GetDouble(), JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.String => value.GetString(), _ => null
    };
    private static YamlNode ToYaml(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => new YamlMappingNode(value.EnumerateObject().Select(p => new KeyValuePair<YamlNode, YamlNode>(new YamlScalarNode(p.Name), ToYaml(p.Value)))),
        JsonValueKind.Array => new YamlSequenceNode(value.EnumerateArray().Select(ToYaml)),
        JsonValueKind.String => new YamlScalarNode(value.GetString()), JsonValueKind.Null => new YamlScalarNode(""), _ => new YamlScalarNode(value.GetRawText())
    };
}
