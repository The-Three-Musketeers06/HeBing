using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Algorithm.Impl;
using AiVisionStudio.Models;
using OpenCvSharp;

namespace AiVisionStudio.Views;

public partial class ParamsWindow
{
    private TabControl _shapeTabs;
    private ItemsControl _shapeTeachingEditors, _shapeRuntimeEditors, _shapeRenderEditors;
    private TextBlock _shapeResultStatus;
    private readonly ObservableCollection<ShapeMatchRow> _shapeMatchRows = new();
    private readonly List<ParameterNode> _shapeObservedNodes = new();
    private string _shapeOperationId;
    private Mat _shapeTrainingImage;
    private TemplateConfigurationWindow _shapeConfigurationWindow;
    private ContourTemplateModel _shapeTemplate;
    private bool _shapeDirty, _shapeStateLoaded;
    private bool IsPortableShapeMatcher => _vm.SelectedOperation?.Driver == ContourTemplateEngine.ShapeDriver;
    private bool _shapeBusy => _shapeConfigurationWindow?.IsTraining == true;
    private string _shapeStoredPayload;
    private bool IsShapeTool => ContourTemplateEngine.IsTemplateTool(_vm.SelectedOperation?.Driver);
    private bool IsShapeMatcher => ContourTemplateEngine.IsMatcher(_vm.SelectedOperation?.Driver);
    private string ShapeTrainingRegionKey => IsShapeMatcher ? "Training Region" : "Region";

    private void ConfigureShapeMatchingEditor()
    {
        var active = IsShapeTool;
        ShapeEditorHost.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (!active) return;
        FormBody.Visibility = Visibility.Collapsed;
        if (_shapeTabs == null) BuildShapeMatchingEditor();
        if (_shapeOperationId != _vm.SelectedOperation.Uuid)
        {
            _shapeOperationId = _vm.SelectedOperation.Uuid;
            _shapeConfigurationWindow?.Close();
            _shapeTrainingImage?.Dispose(); _shapeTrainingImage = null;
            _shapeTemplate = null; _shapeDirty = false; _shapeMatchRows.Clear();
            _shapeStateLoaded = false; _shapeStoredPayload = null;
            _shapeTabs.SelectedIndex = 0;
            Width = Math.Max(Width, 1100); Height = Math.Max(Height, 700);
        }
        foreach (var node in _shapeObservedNodes) node.PropertyChanged -= OnShapeTeachingParameterChanged;
        _shapeObservedNodes.Clear();
        var roots = _vm.ParameterGroups.SelectMany(g => g.Items.Concat(g.AdvancedItems)).ToArray();
        var teaching = new HashSet<string>(new[] { "IM In", "Model File", ShapeTrainingRegionKey, "Use Shielding Area", "Shielding Area", "Shielding Strokes", "Parameters", "Create Model", "Model Origin" }, StringComparer.Ordinal);
        _shapeTeachingEditors.ItemsSource = roots.Where(n => teaching.Contains(n.Name) && n.Name is not ("Use Shielding Area" or "Shielding Area" or "Shielding Strokes" or "Model File")).ToArray();
        var runtime = roots.Where(n => n.Specification?.Field != "render" &&
            (!teaching.Contains(n.Name) || n.Name == "IM In") && n.Name is not ("Shielding Strokes" or "Model File") && !ContourTemplateStorage.IsInternal(n.Name)).ToArray();
        _shapeRuntimeEditors.ItemsSource = runtime;
        _shapeRenderEditors.ItemsSource = roots.Where(n => n.Specification?.Field == "render").ToArray();
        ObserveShapeRuntimeParameters(roots, runtime);
        foreach (var node in roots.Where(n => teaching.Contains(n.Name) && n.Name is not ("IM In" or "Model File" or "Shielding Strokes")).SelectMany(ShapeDescendants))
        {
            node.PropertyChanged += OnShapeTeachingParameterChanged;
            _shapeObservedNodes.Add(node);
        }
        RestoreProjectTemplate();
        RefreshShapeMatchResults(false);
    }

    private static IEnumerable<ParameterNode> ShapeDescendants(ParameterNode node)
    {
        yield return node;
        foreach (var child in node.Children.SelectMany(ShapeDescendants)) yield return child;
    }

    private void BuildShapeMatchingEditor()
    {
        _shapeTabs = new TabControl { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0) };
        _shapeTabs.SetResourceReference(ForegroundProperty, "Text");
        _shapeTabs.ItemContainerStyle = (Style)FindResource("ShapeTabItem");
        var teach = new StackPanel { Margin = new Thickness(9, 8, 7, 8) };
        var capture = new WrapPanel { Margin = new Thickness(0, 7, 0, 4) };
        capture.Children.Add(ShapeAction("创建", (_, _) => OpenShapeTemplateConfiguration()));
        teach.Children.Add(capture);
        _shapeTeachingEditors = ShapeParameterList(); teach.Children.Add(_shapeTeachingEditors);
        _shapeTabs.Items.Add(new TabItem { Header = "模板示教", Content = ShapeScroll(teach) });

        var run = new StackPanel { Margin = new Thickness(9, 8, 7, 8) };
        run.Children.Add(ShapeHint("设置运行参数后，点击底部“测试”执行匹配。"));
        _shapeRuntimeEditors = ShapeParameterList(); run.Children.Add(_shapeRuntimeEditors);
        _shapeTabs.Items.Add(new TabItem { Header = "运行参数", Content = ShapeScroll(run) });

        var result = new StackPanel { Margin = new Thickness(9, 8, 7, 8) };
        _shapeResultStatus = ShapeHint("尚未运行匹配。点击底部“测试”查看定位结果。");
        result.Children.Add(_shapeResultStatus);
        var table = _shapeMatchTable = new DataGrid
        {
            ItemsSource = _shapeMatchRows, AutoGenerateColumns = false, IsReadOnly = true,
            CanUserAddRows = false, CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
            MinHeight = 100, MaxHeight = 300, Margin = new Thickness(0, 10, 0, 8),
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, RowHeaderWidth = 0
        };
        table.SelectionMode = DataGridSelectionMode.Single;
        table.SelectionUnit = DataGridSelectionUnit.FullRow;
        table.IsSynchronizedWithCurrentItem = false;
        table.SelectionChanged += OnShapeMatchSelectionChanged;
        table.SetResourceReference(BackgroundProperty, "Panel"); table.SetResourceReference(ForegroundProperty, "Text");
        table.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "Border");
        table.SetResourceReference(DataGrid.RowBackgroundProperty, "Panel");
        var headers = new Style(typeof(DataGridColumnHeader));
        headers.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("PanelHead")));
        headers.Setters.Add(new Setter(Control.ForegroundProperty, FindResource("Text")));
        headers.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 6, 7, 6)));
        table.ColumnHeaderStyle = headers;
        var cells = new Style(typeof(TextBlock));
        cells.Setters.Add(new Setter(MarginProperty, new Thickness(6, 4, 6, 4)));
        cells.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        foreach (var (title, path, format) in new[] { ("序号", "Index", "0"), ("X", "X", "0.00"), ("Y", "Y", "0.00"), ("角度°", "Angle", "0.00"), ("尺度", "Scale", "0.000"), ("分数", "Score", "0.000") })
            table.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(path) { StringFormat = format }, ElementStyle = cells, Width = path == "Index" ? new DataGridLength(44) : new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = path == "Index" ? 40 : 60 });
        result.Children.Add(table);
        _shapeSelectionInfo = ShapeHint("点击右侧绿框或列表中的一行查看对应目标；点击图像空白处取消选择。");
        result.Children.Add(_shapeSelectionInfo);
        result.Children.Add(ShapeHint("坐标基于模板原点。输出参数可绑定到后续工具的运行位姿，使检测区域随目标移动与旋转。"));
        _shapeRenderEditors = ShapeParameterList();
        result.Children.Add(new Expander { Header = "结果显示设置", Content = _shapeRenderEditors, IsExpanded = false, Margin = new Thickness(0, 8, 0, 0) });
        _shapeTabs.Items.Add(new TabItem { Header = "匹配结果", Content = ShapeScroll(result) });
        _shapeTabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, _shapeTabs)) return;
            Preview.CancelRegionEdit(); ClearShapeSelection(); RefreshPreview();
            if (_shapeTabs.SelectedIndex == 0 && _shapeTemplate != null && !_shapeDirty) TestStatus.Text = "模板已训练，当前仅显示示教区域";
            else if (_shapeResultsStale) TestStatus.Text = "参数已修改，请点击“测试”执行匹配";
        };
        ShapeEditorHost.Content = _shapeTabs;
        Preview.ImagePointClicked += OnShapeImagePointClicked;
    }

    private Button ShapeAction(string label, RoutedEventHandler handler)
    {
        var button = new Button { Content = label, Style = (Style)FindResource("ToolActionButton"), Width = double.NaN, MinWidth = 72, Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 5, 5) };
        button.Click += handler; return button;
    }
    private TextBlock ShapeHint(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 19, Margin = new Thickness(0, 2, 0, 3) };
        block.SetResourceReference(ForegroundProperty, "TextDim"); return block;
    }
    private static ScrollViewer ShapeScroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private static ItemsControl ShapeParameterList()
    {
        var template = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(ParameterEditor)) };
        return new ItemsControl { ItemTemplate = template, Margin = new Thickness(0, 6, 0, 0) };
    }

    private bool CanEditShape()
    {
        if (!_vm.CanEdit || _shapeBusy) { TestStatus.Text = _shapeBusy ? "训练中，请等待或停止训练" : "运行期间不能编辑模板"; return false; }
        return true;
    }
    private TemplateConfigurationWindow CreateShapeTemplateConfiguration()
    {
        RestoreProjectTemplate();
        var operation = _vm.SelectedOperation.Uuid;
        var values = new[] { ShapeTrainingRegionKey, "Use Shielding Area", "Shielding Area", "Shielding Strokes", "Parameters", "Create Model", "Model Origin" }
            .ToDictionary(key => key, key => _vm.ReadParameterValue(key));
        Mat CurrentImage()
        {
            return _vm.CopySelectedInputImage();
        }
        using var current = CurrentImage();
        return new TemplateConfigurationWindow(values, ShapeTrainingRegionKey, current, _shapeTemplate, CurrentImage,
            (model, draft) =>
            {
                if (_vm.SelectedOperation?.Uuid != operation || !_vm.CanEdit) return false;
                if (!_vm.StoreContourTemplate(model, draft)) return false;
                _shapeMatchRows.Clear(); RestoreProjectTemplate(); RefreshPreview();
                TestStatus.Text = $"训练成功：{model.Features.Count} 个特征，模板已保存到工程";
                return true;
            }, IsPortableShapeMatcher ? "Shape" : "Contour", () => { _vm.ClearSelectedContourTemplate(); RestoreProjectTemplate(); RefreshPreview(); });
    }
    private void OpenShapeTemplateConfiguration()
    {
        if (!CanEditShape()) return;
        Preview.CancelRegionEdit();
        TemplateConfigurationWindow editor = null;
        try
        {
            editor = _shapeConfigurationWindow = CreateShapeTemplateConfiguration();
            _shapeConfigurationWindow.Owner = this;
            _shapeConfigurationWindow.ShowDialog();
        }
        catch (Exception exception) { ShowShapeError(exception); }
        finally { _shapeConfigurationWindow = null; RestoreProjectTemplate(); RefreshPreview(); }
        if (editor?.HasSavedTraining == true) TestStatus.Text = "模板已训练，请设置运行参数后点击“测试”";
    }

    private void RestoreProjectTemplate()
    {
        if (_shapeBusy) return;
        var payload = _vm.ReadParameterValue(ContourTemplateStorage.ModelKey)?.ToString();
        var dirty = _vm.ReadParameterValue(ContourTemplateStorage.DirtyKey) is true;
        if (_shapeStateLoaded && payload == _shapeStoredPayload)
        {
            if (payload != null) _shapeDirty = dirty;
            return;
        }
        _shapeStateLoaded = true; _shapeStoredPayload = payload;
        if (payload != null && string.IsNullOrWhiteSpace(payload))
        { _shapeTemplate = null; _shapeTrainingImage?.Dispose(); _shapeTrainingImage = null; _shapeDirty = true; return; }
        try
        {
            ContourTemplateModel model;
            if (payload != null) model = ContourTemplateEngine.DeserializeModel(payload);
            else
            {
                var file = _vm.ReadParameterValue("Model File")?.ToString();
                if (string.IsNullOrWhiteSpace(file))
                { _shapeTemplate = null; _shapeTrainingImage?.Dispose(); _shapeTrainingImage = null; _shapeDirty = false; return; }
                model = ContourTemplateEngine.Load(_vm.ResolveProjectFile(file));
            }
            using var restored = ContourTemplateEngine.DecodeSourceImage(model);
            _shapeTrainingImage?.Dispose(); _shapeTrainingImage = restored.Clone();
            _shapeTemplate = model; _shapeDirty = dirty;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException)
        { _shapeTemplate = null; _shapeTrainingImage?.Dispose(); _shapeTrainingImage = null; _shapeDirty = true; TestStatus.Text = exception.Message; }
    }
    private void OnShapeTeachingParameterChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ParameterNode.Value))
        {
            MarkShapeDirty();
            Dispatcher.InvokeAsync(RefreshPreview);
        }
    }
    private void MarkShapeDirty()
    {
        _shapeDirty = true; _shapeResultsStale = true; ClearShapeMatchRows();
        if (_shapeResultStatus != null) _shapeResultStatus.Text = "示教参数已变化，请重新训练模板";
    }
    private bool TryShowShapeTrainingPreview()
    {
        if (!IsShapeTool || _shapeTabs == null || _regionTarget != null || _parameterRegionPreviewPath != null) return false;
        RestoreProjectTemplate();
        using var current = _vm.CopySelectedInputImage();
        if (current == null || current.IsDisposed || current.Empty()) return false;
        var image = _shapeTabs.SelectedIndex == 0 && !_shapeDirty && _shapeTrainingImage != null ? _shapeTrainingImage : current;
        Preview.ShowMat(image, _vm.GetImageName(_vm.RegionInputIndex));
        PreviewMode.Text = IsPortableShapeMatcher ? "灰度模板示教" : "轮廓模板示教"; PreviewSlot.Text = "输入图像";
        EmptyPreview.Visibility = Visibility.Collapsed; PreviewTextOverlay.Visibility = Visibility.Collapsed;
        if (IsShapeMatcher && _shapeTabs.SelectedIndex > 0)
        {
            PreviewMode.Text = _shapeTabs.SelectedIndex == 1 ? "运行匹配" : "匹配结果";
            Preview.SetNativeOverlays(_showOverlays ? ShapeResultOverlays() : null);
            return true;
        }
        var overlays = new List<NativeOverlay3D>();
        AddShapeRegionOverlay(overlays, _vm.ReadParameterValue(ShapeTrainingRegionKey), _shapeDirty ? "#00C5E0" : "#00FF00");
        var rawOrigin = _vm.ReadParameterValue("Model Origin") as IReadOnlyDictionary<string, object>;
        var autoOrigin = rawOrigin == null || !rawOrigin.TryGetValue("Auto", out var automatic) || Convert.ToBoolean(automatic, CultureInfo.InvariantCulture);
        var ox = autoOrigin ? _shapeTemplate?.OriginX ?? current.Width / 2d : ShapeNumber(rawOrigin, "X");
        var oy = autoOrigin ? _shapeTemplate?.OriginY ?? current.Height / 2d : ShapeNumber(rawOrigin, "Y");
        var angle = autoOrigin ? _shapeTemplate?.OriginAngle ?? 0 : ShapeNumber(rawOrigin, "Angle");
        overlays.Add(new NativeOverlay3D { Type = "point2d", Values = new Dictionary<string, object> { ["X"] = ox, ["Y"] = oy, ["Angle"] = angle, ["_marker"] = "cross", ["_size"] = 5, ["_color"] = "#00FF00" } });
        Preview.SetNativeOverlays(_showOverlays ? overlays : null); return true;
    }
    private static void AddShapeRegionOverlay(List<NativeOverlay3D> overlays, object raw, string color)
    {
        if (raw is IEnumerable values && raw is not IReadOnlyDictionary<string, object> && raw is not string)
        { foreach (var value in values) AddShapeRegionOverlay(overlays, value, color); return; }
        if (raw is not IReadOnlyDictionary<string, object> wrapper) return;
        var type = wrapper.GetValueOrDefault("Type")?.ToString() ?? "rect2d";
        if (type.EndsWith(" array", StringComparison.Ordinal) && wrapper.GetValueOrDefault(type) is IEnumerable array)
        {
            var elementType = type[..^6];
            foreach (var element in array) AddShapeRegionOverlay(overlays, new Dictionary<string, object> { ["Type"] = elementType, [elementType] = element }, color);
            return;
        }
        if (wrapper.GetValueOrDefault(type) is not IReadOnlyDictionary<string, object> shape) return;
        if (type == "rect2d" && shape.GetValueOrDefault("Size") is IReadOnlyDictionary<string, object> size
            && (ShapeNumber(size, "Width") <= 0 || ShapeNumber(size, "Height") <= 0)) return;
        var geometry = new Dictionary<string, object>(shape) { ["_color"] = color, ["_thickness"] = 2 };
        overlays.Add(new NativeOverlay3D { Type = type, Values = geometry });
    }
    private static Dictionary<string, object> ShapeXY(double x, double y) => new() { ["X"] = x, ["Y"] = y };
    private bool ApplyShapeRuntimeRegionPreview()
    {
        if (!IsShapeMatcher || _shapeTabs?.SelectedIndex != 1 || !Preview.HasImage || _vm.ReadParameterValue("Use Region") is not true) return false;
        var overlays = new List<NativeOverlay3D>(_showOverlays ? _vm.GetSelectedPreviewOverlays(_previewIndex ?? _vm.GetSelectedPreviewIndex()) : []);
        if (_showOverlays)
        {
            try
            {
                var region = _vm.ReadParameterValue("Region") as IReadOnlyDictionary<string, object>;
                var parameters = _vm.CreateSelectedParameterBag();
                if (region != null && parameters.Bool("Position Fix")) region = FixtureRegionTransform.Apply(region, parameters.Group("Points Information"), false);
                AddShapeRegionOverlay(overlays, region, "#00C5E0");
            }
            catch (InvalidOperationException) { }
        }
        Preview.SetNativeOverlays(overlays);
        return true;
    }
    private static double ShapeNumber(IReadOnlyDictionary<string, object> values, string name, double fallback = 0)
        => values != null && values.TryGetValue(name, out var raw) && double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : fallback;

    private bool PrepareShapeRuntimeTest()
    {
        if (!IsShapeTool) return true;
        if (_shapeBusy) { TestStatus.Text = "模板训练中，请等待或停止训练后再测试"; return false; }
        Preview.CompleteRegionEdit();
        _parameterRegionPreviewPath = null;
        EndRegionEditing(true);
        if (Preview.IsRegionEditing) { TestStatus.Text = "请先完成有效的区域绘制，或按 Esc 取消"; return false; }
        if (!_vm.IsRunning) { _shapeMatchRows.Clear(); if (_shapeResultStatus != null) _shapeResultStatus.Text = "正在运行…"; }
        if (_shapeTabs != null) _shapeTabs.SelectedIndex = 1;
        return true;
    }
    private void RefreshShapeMatchResults(bool selectResults = true, bool freshResults = false)
    {
        if (!IsShapeTool || _shapeResultStatus == null) return;
        if (freshResults) MarkShapeResultsCurrent();
        ClearShapeMatchRows();
        if (_shapeResultsStale) { _shapeResultStatus.Text = "参数已修改，请点击“测试”重新匹配"; return; }
        var output = _vm.GetSelectedOutputValues();
        _shapeOutputMatches = output.GetValueOrDefault("Matches");
        if (output.GetValueOrDefault("Matches") is IEnumerable matches)
        {
            foreach (var raw in matches)
            {
                if (raw is not IReadOnlyDictionary<string, object> match) continue;
                var center = match.GetValueOrDefault("Center") as IReadOnlyDictionary<string, object> ?? match;
                _shapeMatchRows.Add(new ShapeMatchRow { Index = _shapeMatchRows.Count + 1, X = ShapeNumber(center, "X"), Y = ShapeNumber(center, "Y"),
                    Angle = ShapeNumber(match, "Angle"), Rotation = ShapeNumber(match, "Rotation", ShapeNumber(match, "Angle") - (_shapeTemplate?.OriginAngle ?? 0)), Scale = ShapeNumber(match, "Scale", 1), Score = ShapeNumber(match, "Score") });
            }
        }
        var elapsed = ShapeNumber(output, "ShapeMatch.ElapsedMs", _vm.SelectedOperation?.ElapsedMs ?? 0);
        _shapeResultStatus.Text = IsShapeMatcher ? $"{_vm.SelectedResult} · {_shapeMatchRows.Count} 个匹配 · {elapsed:0.0} ms" : "模板创建工具已运行。使用形状模板匹配工具进行定位测试。";
        if (selectResults) _shapeTabs.SelectedIndex = 2;
    }
    private void ShowShapeError(Exception exception)
    {
        TestStatus.Text = exception.Message;
        TestStatus.ToolTip = exception.ToString();
    }
    private bool HandleShapeClosing(CancelEventArgs e)
    {
        if (!_shapeBusy) return false;
        _shapeConfigurationWindow.Close(); e.Cancel = true;
        TestStatus.Text = "正在取消模板训练"; return true;
    }
    private void DisposeShapeEditor()
    {
        DisposeShapeInteractions();
        _shapeConfigurationWindow?.Close();
        _shapeTrainingImage?.Dispose(); _shapeTrainingImage = null;
        foreach (var node in _shapeObservedNodes) node.PropertyChanged -= OnShapeTeachingParameterChanged;
        _shapeObservedNodes.Clear();
    }
    private sealed class ShapeMatchRow
    {
        public int Index { get; init; }
        public double X { get; init; }
        public double Y { get; init; }
        public double Angle { get; init; }
        public double Rotation { get; init; }
        public double Scale { get; init; }
        public double Score { get; init; }
    }
}
