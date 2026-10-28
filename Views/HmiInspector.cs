using System.Globalization;
using System.Data;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using AiVisionStudio.Models;
using AiVisionStudio.Services;

namespace AiVisionStudio.Views;

/// <summary>Typed editor for the original HMI properties. Every mutation is delegated to editor history.</summary>
public sealed class HmiInspector : UserControl
{
    private readonly StackPanel _body = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _message = new() { Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 4, 10, 4) };
    private readonly Dictionary<string, FrameworkElement> _editors = new();
    private HmiDocument _document;
    private HmiPage _page;
    private HmiElement[] _selection = [];
    private IHmiRuntimeContext _runtime;
    private bool _building;
    private bool _committing;
    private bool _lastCommitFailed;
    private string _draftOwner = "";
    private readonly Dictionary<string, string> _invalidDrafts = new();
    public Action<string, Action> ApplyEdit { get; set; }
    public Action<HmiAlignment> AlignSelection { get; set; }
    public Action<string, double> SetDimension { get; set; }
    public event Action Changed;
    public IReadOnlyDictionary<string, FrameworkElement> Editors => _editors;
    public int SelectionCount => _selection.Length;
    public HmiInspector()
    {
        Background = HmiVisuals.Brush("#242424"); Foreground = HmiVisuals.Brush("#d8d8d8"); FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        var dock = new DockPanel(); DockPanel.SetDock(_message, Dock.Bottom); dock.Children.Add(_message);
        _scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(12, 8, 12, 14) };
        dock.Children.Add(_scroll); Content = dock;
    }
    public void Inspect(HmiDocument document, HmiPage page, IReadOnlyList<HmiElement> selection, IHmiRuntimeContext runtime = null)
    {
        var owner = (page?.Index.ToString() ?? "") + ":" + string.Join(",", selection?.Select(e => e.Id) ?? []);
        if (owner != _draftOwner) { _invalidDrafts.Clear(); _lastCommitFailed = false; _draftOwner = owner; }
        _document = document; _page = page; _selection = selection?.ToArray() ?? []; _runtime = runtime; Refresh();
    }
    public bool CommitPendingEdits()
    {
        foreach (var table in _editors.Values.OfType<DataGrid>().ToArray())
            if (!table.CommitEdit(DataGridEditingUnit.Cell, true) || !table.CommitEdit(DataGridEditingUnit.Row, true)) return false;
        if (Keyboard.FocusedElement is UIElement focused && IsAncestorOf(focused))
        {
            focused.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, focused, null) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            if (_invalidDrafts.Count == 0 && !_lastCommitFailed) Keyboard.ClearFocus();
        }
        return _invalidDrafts.Count == 0 && !_lastCommitFailed;
    }
    public void Refresh()
    {
        if (_building) return; _building = true;
        var offset = _scroll.VerticalOffset;
        try
        {
            _body.Children.Clear(); _editors.Clear(); if (_invalidDrafts.Count == 0 && !_lastCommitFailed) _message.Text = "";
            _body.Children.Add(new Border { Background = HmiVisuals.Brush("#414141"), CornerRadius = new CornerRadius(3), Padding = new Thickness(9), Margin = new Thickness(0, 0, 0, 14), Child = new TextBlock { Text = _selection.Length == 0 ? "画布配置  ⚙" : _selection.Length == 1 ? "组件配置  ⚒" : $"组件配置 · 已选择 {_selection.Length} 项", TextAlignment = TextAlignment.Center, FontWeight = FontWeights.SemiBold } });
            if (_page == null) { _body.Children.Add(new TextBlock { Text = "请选择看板", Margin = new Thickness(8) }); return; }
            if (_selection.Length == 0) { CanvasFields(); return; }
            BasicFields();
            if (_selection.Length > 1)
            {
                Note(_body, "共同属性会应用到所选的未锁定组件。组件类型不同的专有属性请单独选择后编辑。"); return;
            }
            ComponentFields(_selection[0]);
        }
        finally { _building = false; _scroll.ScrollToVerticalOffset(offset); }
    }
    private void Commit(string description, Action edit, bool rebuild = false)
    {
        if (_building || _committing) return;
        _committing = true;
        try
        {
            if (ApplyEdit != null) ApplyEdit(description, edit); else edit();
            _lastCommitFailed = false; if (_invalidDrafts.Count == 0) _message.Text = ""; Changed?.Invoke(); if (rebuild) Refresh();
        }
        catch (Exception ex) { _lastCommitFailed = true; _message.Text = ex.Message; }
        finally { _committing = false; }
    }
    private bool AncestorLocked(HmiElement e)
    {
        var parent = _document?.AllElements.FirstOrDefault(x => x.Children.Any(c => c.Id == e.Id));
        return parent != null && (parent.Locked || AncestorLocked(parent));
    }
    private bool Editable(HmiElement e, string path) => !AncestorLocked(e) && (!e.Locked || path == "status.lock");
    private void Change(string path, object value, string label, bool rebuild = false)
    {
        if (_building || _committing) return;
        var selected = _selection.Where(e => Editable(e, path)).ToArray();
        if (selected.Length == 0) { _message.Text = "请先解锁组件或其所在分组"; return; }
        if (path is "attr.w" or "attr.h" && value is double dimension && SetDimension != null) { _committing = true; try { SetDimension(path, dimension); Changed?.Invoke(); } catch (Exception ex) { _message.Text = ex.Message; } finally { _committing = false; } return; }
        Commit("修改" + label, () => { foreach (var e in selected) SetValue(e, path, value); }, rebuild);
    }
    private static void SetValue(HmiJsonObject target, string path, object value)
    {
        // Numeric strings such as native icon.rotate and table.pageSize must retain their original type.
        var original = target.GetNode(path);
        if (value is double or int or long && original is JsonValue scalar && scalar.TryGetValue<string>(out _)) value = Convert.ToString(value, CultureInfo.InvariantCulture);
        target.SetNode(path, JsonSerializer.SerializeToNode(value));
    }
    private JsonNode Current(string path) => _selection.FirstOrDefault()?.GetNode(path);
    private string Common(string path, string fallback = "")
    {
        var values = _selection.Select(e => HmiVisuals.Text(e.GetNode(path)) is { Length: > 0 } text ? text : fallback).Distinct().ToArray();
        return values.Length == 1 ? values[0] : "";
    }
    private static void Note(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, Foreground = HmiVisuals.Brush("#999999"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10), FontSize = 11 });
    private StackPanel Section(string title, bool expanded = false)
    {
        var children = new StackPanel { Margin = new Thickness(2, 9, 2, 6) };
        var expander = new Expander { Header = title, Content = children, IsExpanded = expanded, Foreground = Foreground, Margin = new Thickness(0, 2, 0, 0), Padding = new Thickness(0, 10, 0, 10), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = HmiVisuals.Brush("#373737") };
        _body.Children.Add(expander); return children;
    }
    private static Grid Row(Panel parent, string label, FrameworkElement control, double labelWidth = 85)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) }; grid.ColumnDefinitions.Add(new() { Width = new GridLength(labelWidth) }); grid.ColumnDefinitions.Add(new());
        grid.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }); Grid.SetColumn(control, 1); grid.Children.Add(control); parent.Children.Add(grid); return grid;
    }
    private static TextBox Input(string text, bool multiline = false) => new() { Text = text ?? "", Background = HmiVisuals.Brush("#383838"), Foreground = HmiVisuals.Brush("#dedede"), BorderThickness = new Thickness(0), Padding = new Thickness(7, 5, 7, 5), VerticalContentAlignment = VerticalAlignment.Center, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 65 : 29, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden };
    private static Button Button(string label, Action callback) { var b = new Button { Content = label, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(2), Background = HmiVisuals.Brush("#363636"), Foreground = HmiVisuals.Brush("#cccccc"), BorderThickness = new Thickness(0), MinHeight = 28 }; b.Click += (_, _) => callback(); return b; }
    private TextBox String(Panel panel, string label, string path, bool multiline = false, string fallback = "", Action<string> setter = null, string initial = null)
    {
        var field = Input(initial ?? Common(path, fallback), multiline); _editors[path] = field; field.Tag = path;
        void Apply() { var before = initial ?? Common(path, fallback); if (field.Text == before) return; if (setter != null) setter(field.Text); else Change(path, field.Text, label); }
        field.LostKeyboardFocus += (_, _) => Apply(); field.KeyDown += (_, e) => { if (e.Key == Key.Enter && (!multiline || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) { Apply(); e.Handled = true; } };
        if (setter == null) field.IsEnabled = _selection.Any(e => Editable(e, path)); Row(panel, label, field); return field;
    }
    private FrameworkElement Number(Panel panel, string label, string path, double fallback = 0, double min = double.NegativeInfinity, double max = double.PositiveInfinity, double step = 1, Action<double> setter = null, double? initial = null)
    {
        var text = initial?.ToString("0.###", CultureInfo.InvariantCulture) ?? Common(path, fallback.ToString(CultureInfo.InvariantCulture));
        var field = Input(_invalidDrafts.GetValueOrDefault(path) ?? text); _editors[path] = field; field.Tag = path;
        var dock = new DockPanel(); var buttons = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(buttons, Dock.Right); dock.Children.Add(buttons); dock.Children.Add(field);
        void Apply(string value)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number < min || number > max) { _invalidDrafts[path] = value; field.BorderBrush = Brushes.OrangeRed; field.BorderThickness = new Thickness(1); _message.Text = $"{label}：请输入 {min} 至 {max} 范围内的数字"; return; }
            _invalidDrafts.Remove(path); field.BorderThickness = new Thickness(0); if (_invalidDrafts.Count == 0 && !_lastCommitFailed) _message.Text = ""; if (text == value) return; if (setter != null) setter(number); else Change(path, number, label); text = number.ToString("0.###", CultureInfo.InvariantCulture); field.Text = text;
        }
        buttons.Children.Add(Button("−", () => Apply(Math.Clamp(HmiVisuals.Number(field.Text, fallback) - step, min, max).ToString(CultureInfo.InvariantCulture)))); buttons.Children.Add(Button("+", () => Apply(Math.Clamp(HmiVisuals.Number(field.Text, fallback) + step, min, max).ToString(CultureInfo.InvariantCulture))));
        field.LostKeyboardFocus += (_, _) => Apply(field.Text); field.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(field.Text); e.Handled = true; } };
        if (setter == null) dock.IsEnabled = _selection.Any(e => Editable(e, path)); Row(panel, label, dock); return dock;
    }
    private void Boolean(Panel panel, string label, string path, bool fallback = false, Action<bool> setter = null, bool? initial = null)
    {
        var values = _selection.Select(e => e.GetBoolean(path, fallback)).Distinct().ToArray(); var field = new CheckBox { IsThreeState = values.Length > 1, IsChecked = initial ?? (values.Length == 1 ? values[0] : null), Content = "", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 5, 2, 5) }; _editors[path] = field; field.Tag = path;
        field.Click += (_, _) => { if (setter != null) setter(field.IsChecked == true); else Change(path, field.IsChecked == true, label); };
        if (setter == null) field.IsEnabled = _selection.Any(e => Editable(e, path)); Row(panel, label, field);
    }
    private sealed record Choice(string Label, string Value) { public override string ToString() => Label; }
    private ComboBox Select(Panel panel, string label, string path, IEnumerable<(string Label, string Value)> options, string fallback = "", Action<string> setter = null, string initial = null, bool editable = false)
    {
        var value = initial ?? Common(path, fallback); var choices = options.Select(x => new Choice(x.Label, x.Value)).ToList(); if (!string.IsNullOrEmpty(value) && choices.All(x => x.Value != value)) choices.Add(new(value, value));
        var field = new ComboBox { ItemsSource = choices, SelectedItem = choices.FirstOrDefault(x => x.Value == value), IsEditable = editable, MinHeight = 29, Padding = new Thickness(5, 2, 5, 2), MaxDropDownHeight = 300 }; if (editable) field.Text = value; _editors[path] = field; field.Tag = path;
        void Apply(string selected) { if (selected == value) return; if (setter != null) setter(selected); else Change(path, selected, label); value = selected; }
        field.SelectionChanged += (_, _) => { if (field.SelectedItem is Choice c) Apply(c.Value); };
        if (editable) field.LostKeyboardFocus += (_, _) => Apply(field.SelectedItem is Choice c && field.Text == c.Label ? c.Value : field.Text);
        if (setter == null) field.IsEnabled = _selection.Any(e => Editable(e, path)); Row(panel, label, field); return field;
    }
    private void Color(Panel panel, string label, string path, string fallback = "#ffffff", Action<string> setter = null, string initial = null)
    {
        var holder = new StackPanel(); var text = String(holder, "", path, false, fallback, setter, initial); var row = (Grid)holder.Children[0]; holder.Children.Clear(); row.Children.Remove(text);
        var dock = new DockPanel(); var swatch = Button("", () => OpenColorPicker(initial ?? Common(path, fallback), value => { text.Text = value; if (setter != null) setter(value); else Change(path, value, label); })); swatch.Width = 34; swatch.Background = HmiVisuals.Brush(initial ?? Common(path, fallback), fallback); DockPanel.SetDock(swatch, Dock.Left); dock.Children.Add(swatch); dock.Children.Add(text); text.TextChanged += (_, _) => swatch.Background = HmiVisuals.Brush(text.Text, fallback); Row(panel, label, dock);
    }
    private void OpenColorPicker(string value, Action<string> apply)
    {
        var color = HmiVisuals.Color(value); var panel = new StackPanel { Margin = new Thickness(14) }; var dialog = new Window { Title = "选择颜色", Width = 340, Height = 330, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize };
        var preview = new Border { Height = 40, Background = new SolidColorBrush(color), Margin = new Thickness(0, 0, 0, 10) }; panel.Children.Add(preview); var sliders = new List<Slider>();
        Color CurrentColor() => System.Windows.Media.Color.FromArgb((byte)sliders[3].Value, (byte)sliders[0].Value, (byte)sliders[1].Value, (byte)sliders[2].Value);
        foreach (var (name, channel) in new[] { ("R", color.R), ("G", color.G), ("B", color.B), ("A", color.A) }) { var slider = new Slider { Minimum = 0, Maximum = 255, Value = channel, TickFrequency = 1, IsSnapToTickEnabled = true }; sliders.Add(slider); Row(panel, name, slider, 30); slider.ValueChanged += (_, _) => { if (sliders.Count == 4) preview.Background = new SolidColorBrush(CurrentColor()); }; }
        var palette = new UniformGrid { Rows = 1, Margin = new Thickness(0, 10, 0, 8) }; foreach (var hex in HmiVisuals.Palette.Take(7).Append("#ffffff")) { var button = Button("", () => { var c = HmiVisuals.Color(hex); sliders[0].Value = c.R; sliders[1].Value = c.G; sliders[2].Value = c.B; sliders[3].Value = c.A; }); button.Background = HmiVisuals.Brush(hex); palette.Children.Add(button); } panel.Children.Add(palette);
        panel.Children.Add(Button("确定", () => { var c = CurrentColor(); apply($"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}"); dialog.Close(); })); dialog.Content = panel; dialog.ShowDialog();
    }
    private void CanvasFields()
    {
        String(_body, "名称", "canvas.name", initial: _page.Title, setter: value => Commit("修改看板名称", () => _page.Title = value));
        Select(_body, "尺寸预设", "canvas.resolution", [("1920 × 1080", "1920x1080"), ("1280 × 720", "1280x720"), ("1366 × 768", "1366x768"), ("2560 × 1440", "2560x1440"), ("3840 × 2160", "3840x2160")], initial: $"{_page.Width:0}x{_page.Height:0}", setter: value => { var parts = value.Split('x'); if (parts.Length == 2 && double.TryParse(parts[0], out var w) && double.TryParse(parts[1], out var h)) Commit("修改画布分辨率", () => { _page.Width = w; _page.Height = h; }, true); });
        Number(_body, "宽度 W", "canvas.width", min: 100, max: 32768, initial: _page.Width, setter: value => Commit("修改画布宽度", () => _page.Width = value));
        Number(_body, "高度 H", "canvas.height", min: 100, max: 32768, initial: _page.Height, setter: value => Commit("修改画布高度", () => _page.Height = value));
        Color(_body, "背景颜色", "canvas.background", initial: _page.Background, setter: value => Commit("修改画布背景", () => _page.Background = value));
        Select(_body, "应用类型", "canvas.selectColor", [("颜色", "color"), ("背景图", "image")], initial: _page.GetBoolean("editCanvasConfig.selectColor", true) ? "color" : "image", setter: value => Commit("切换画布背景", () => _page.SetNode("editCanvasConfig.selectColor", JsonValue.Create(value == "color"))));
        Row(_body, "页面类型", new TextBlock { Text = _page.IsSubwindow ? "子窗口" : "看板", VerticalAlignment = VerticalAlignment.Center });
        var buttons = new UniformGrid { Rows = 1 }; buttons.Children.Add(Button("导入背景图片", () => ImportImage(null, "editCanvasConfig.backgroundImage"))); buttons.Children.Add(Button("清除背景图片", () => Commit("清除画布图片", () => { _page.SetNode("editCanvasConfig.backgroundImage", JsonValue.Create("")); _page.SetNode("editCanvasConfig.selectColor", JsonValue.Create(true)); }))); _body.Children.Add(buttons);
        if (_page.IsSubwindow) { var section = Section("子窗口设置", true); Number(section, "窗口宽度", "canvas.windowWidth", 1024, 100, 10000, initial: _page.GetNumber("editCanvasConfig.attr.w", 1024), setter: value => Commit("修改子窗口尺寸", () => _page.SetNode("editCanvasConfig.attr.w", JsonValue.Create(value)))); Number(section, "窗口高度", "canvas.windowHeight", 544, 100, 10000, initial: _page.GetNumber("editCanvasConfig.attr.h", 544), setter: value => Commit("修改子窗口尺寸", () => _page.SetNode("editCanvasConfig.attr.h", JsonValue.Create(value)))); }
    }
    private void BasicFields()
    {
        String(_body, "名称", "chartConfig.title"); if (_selection.Length == 1) { var id = Input(_selection[0].Id); id.IsReadOnly = true; id.Foreground = HmiVisuals.Brush("#909090"); Row(_body, "ID", id); }
        Number(_body, "宽度 W", "attr.w", 200, 1, 32768); Number(_body, "高度 H", "attr.h", 100, 1, 32768);
        var align = new UniformGrid { Rows = 1, Margin = new Thickness(0, 12, 0, 8) }; foreach (var (label, value) in new[] { ("⇤", "left"), ("↔", "center"), ("⇥", "right"), ("↥", "top"), ("↕", "middle"), ("↧", "bottom") }) { var button = Button(label, () => Align(value)); button.ToolTip = value switch { "left" => "左对齐", "center" => "水平居中", "right" => "右对齐", "top" => "顶对齐", "middle" => "垂直居中", _ => "底对齐" }; align.Children.Add(button); } _body.Children.Add(align);
        Number(_body, "位置 X", "attr.x"); Number(_body, "位置 Y", "attr.y");
        var style = Section("基本样式"); if (_selection.All(e => e.ComponentType == "Icon" || e.Type is "StudioGroup" or "SubwindowGroup")) Number(style, "旋转角度", "option.rotate", 0, -3600, 3600); Boolean(style, "隐藏", "status.hide"); Boolean(style, "锁定", "status.lock"); Number(style, "层级", "attr.zIndex", 0, -10000, 10000);
        if (_selection.Any(e => e.Locked || AncestorLocked(e))) Note(_body, "锁定组件的属性已禁用。解除组件或分组锁定后可继续编辑。");
    }
    private string RotationPath() => _selection.All(e => e.ComponentType == "Icon" || e.Type is "StudioGroup" or "SubwindowGroup") ? "option.rotate" : "styles.rotateZ";
    private void Align(string direction)
    {
        if (AlignSelection != null) { AlignSelection(direction switch { "left" => HmiAlignment.Left, "center" => HmiAlignment.HorizontalCenter, "right" => HmiAlignment.Right, "top" => HmiAlignment.Top, "middle" => HmiAlignment.VerticalCenter, _ => HmiAlignment.Bottom }); return; }
        var items = _selection.Where(e => Editable(e, "attr.x")).ToArray(); if (items.Length == 0) return;
        var x = items.Length == 1 ? 0 : items.Min(e => e.X); var y = items.Length == 1 ? 0 : items.Min(e => e.Y); var width = items.Length == 1 ? _page.Width : items.Max(e => e.X + e.Width) - x; var height = items.Length == 1 ? _page.Height : items.Max(e => e.Y + e.Height) - y;
        Commit("对齐组件", () => { foreach (var e in items) switch (direction) { case "left": e.X = x; break; case "center": e.X = x + (width - e.Width) / 2; break; case "right": e.X = x + width - e.Width; break; case "top": e.Y = y; break; case "middle": e.Y = y + (height - e.Height) / 2; break; case "bottom": e.Y = y + height - e.Height; break; } }, true);
    }
    private void ComponentFields(HmiElement e)
    {
        if (e.IsGroup) { Note(_body, $"分组包含 {e.Children.Count} 个组件。双击画布中的子组件可独立编辑。"); return; }
        var kind = e.ComponentType;
        AdditionalFields(e);
        if (kind is "ProgramButton" or "StudioToolButton")
        {
            var bindings = Section(kind == "ProgramButton" ? "执行子程序" : "Studio 工具设置", true);
            Select(bindings, "程序", "option.bindPrograms", (_runtime?.ProgramNames() ?? []).Select(name => (name, name)), setter: value => Change("option.bindPrograms", value, "绑定程序", true), editable: true);
            if (kind == "StudioToolButton") Select(bindings, "工具", "option.bindOperatorUUID", (_runtime?.ProgramOperators(e.GetString("option.bindPrograms")) ?? []).Select(op => (op.Name, op.Id)), editable: true);
            if (e.GetNode("option.data") != null) String(bindings, "按钮文字", "option.data"); if (e.GetNode("option.label") != null) String(bindings, "按钮文字", "option.label");
        }
        if (kind is "Process" or "WaterPolo" or "Dial")
        {
            var value = Section("数值配置", true); Number(value, "默认值", "option.dataset", 0);
            if (kind == "Process") { Select(value, "类型", "option.type", [("圆环", "circle"), ("条形", "line")], "circle"); String(value, "单位", "option.unit", fallback: "%"); Number(value, "数值字号", "option.indicatorTextSize", 34, 1, 200); Color(value, "数值颜色", "option.indicatorTextColor"); Color(value, "轨道颜色", "option.railColor", "#3e3e3f"); Number(value, "起始角度", "option.offsetDegree", 0, -360, 360); }
            if (kind == "Dial") { Number(value, "最小值", "option.series[0].min", 0); Number(value, "最大值", "option.series[0].max", 100); Number(value, "刻度数", "option.series[0].splitNumber", 10, 1, 100); Number(value, "指针宽度", "option.series[0].pointer.width", 4, 1, 100); }
        }
        if (kind is "Number" or "FlipperNumber" or "InputsInput" or "TextStateAssociation")
        {
            var value = Section("数据配置", true); if (e.GetNode("option.dataset") is JsonValue scalar && scalar.TryGetValue<double>(out var numeric)) Number(value, "默认值", "option.dataset", numeric); else if (e.GetNode("option.dataset") is JsonValue) String(value, "默认文字", "option.dataset");
        }
        if (kind == "InputsTab") { TabFields(e); return; }
        if (kind == "SquareButton") ButtonStates(e);
        else if (kind is "StudioGroup" or "SubwindowGroup") IconGroup(e);
        else if (kind is "Image" or "ImageCarousel" or "TwoD" or "ThreeD" or "OneD") ImageFields(e);
        else if (kind == "MESButton") MesFields(e);
        else if (kind.StartsWith("Text") || kind.StartsWith("Title") || kind is "InputsParams" or "Iframe" or "SubwindowButton" or "Icon")
        {
            var information = Section(kind == "InputsParams" ? "参数输入" : "信息", true);
            if (kind == "InputsParams") { String(information, "别名", "option.aliasName"); String(information, "默认值", "option.inputValue"); }
            else if (kind == "SubwindowButton") { String(information, "按钮文字", "option.data"); PageChoice(information, "目标子窗口", "option.bindSubwindowsIndex", true); }
            else if (kind == "Iframe") String(information, "网页地址", "option.dataset");
            else if (kind != "Icon" && e.GetNode("option.dataset") is JsonValue) String(information, "文字", "option.dataset", true);
            if (e.GetNode("option.link") != null) { Select(information, "链接协议", "option.linkHead", [("HTTP", "http://"), ("HTTPS", "https://")], "https://"); String(information, "链接地址", "option.link"); }
        }
        if (e.GetNode("option.series") is JsonArray) ChartFields(e);
        if (kind.StartsWith("Tables") || kind is "TableScrollBoard" or "TableList") TableFields(e);
        if (kind is "NGStatistics" or "NGStatisticsSearch")
        {
            var statistics = Section("良率统计"); Number(statistics, "良率阈值 %", "option.threshold", 95, 0, 100); Color(statistics, "阈值前颜色", "option.beforeThresholdColor", "#d03050"); Color(statistics, "阈值后颜色", "option.afterThresholdColor", "#18a058"); Select(statistics, "排列方式", "option.arrangeType", [("横向", "row"), ("纵向", "column")], "row"); Boolean(statistics, "显示图表", "option.show", true); Number(statistics, "环大小", "option.roundSize", 20, 0, 100); if (kind == "NGStatisticsSearch") Boolean(statistics, "固定时间", "option.fixedTime");
        }
        if (kind == "SwitchButton")
        {
            var toggle = Section("开关设置", true); Boolean(toggle, "默认状态", "option.dataset"); Boolean(toggle, "圆角", "option.round", true); Number(toggle, "缩放", "option.scale", 1, .1, 10, .1); Boolean(toggle, "自定义颜色", "option.colorConfig.open"); Color(toggle, "开启颜色", "option.colorConfig.openColor", "#2080f0"); Color(toggle, "关闭颜色", "option.colorConfig.closeColor", "#d03050"); Color(toggle, "按钮颜色", "option.colorConfig.buttonColor"); Boolean(toggle, "自定义文字", "option.textConfig.open"); String(toggle, "开启文字", "option.textConfig.openText", fallback: "开启"); String(toggle, "关闭文字", "option.textConfig.closeText", fallback: "关闭");
        }
        StyleFields(e);
        BindingFields(e);
        if (e.GetNode("option.dataset") is JsonObject data && (data["source"] is JsonArray || data["data"] is JsonArray)) DatasetEditor(e, data["source"] is JsonArray ? "option.dataset.source" : "option.dataset.data");
    }
    private void AdditionalFields(HmiElement e)
    {
        var kind = e.ComponentType;
        if (kind == "Number")
        {
            var panel = Section("数字设置", true); Number(panel, "起始值", "option.from"); Number(panel, "小数位数", "option.precision", 0, 0, 12); Boolean(panel, "千位分隔符", "option.showSeparator", true); Number(panel, "数字字号", "option.numberSize", 34, 1, 500); Color(panel, "数字颜色", "option.numberColor"); String(panel, "前缀", "option.prefixText"); Color(panel, "前缀颜色", "option.prefixColor"); String(panel, "后缀", "option.suffixText"); Color(panel, "后缀颜色", "option.suffixColor");
        }
        if (kind is "FlipperNumber" or "CountDown")
        {
            var panel = Section(kind == "CountDown" ? "倒计时设置" : "翻牌数字设置", true);
            if (kind == "CountDown") { Number(panel, "倒计时秒数", "option.dataset", 600, 0, 1e12); Boolean(panel, "使用结束日期", "option.useEndDate"); Number(panel, "结束时间戳 ms", "option.endDate", DateTimeOffset.Now.ToUnixTimeMilliseconds(), 0, 1e14); Boolean(panel, "显示天数", "option.showDay"); Select(panel, "格式", "option.style", [("时分秒", "时分秒"), ("分秒", "分秒"), ("秒", "秒")], "时分秒"); }
            else Number(panel, "数字位数", "option.flipperLength", 6, 1, 32);
            Color(panel, "牌面背景", "option.flipperBgColor"); Color(panel, "数字颜色", "option.flipperTextColor");
            foreach (var (key, title, value) in new[] { ("flipperWidth", "牌面宽度", 30d), ("flipperHeight", "牌面高度", 50d), ("flipperRadius", "圆角", 5d), ("flipperGap", "间隔", 10d), ("flipperSpeed", "翻转时间 ms", 450d) }) Number(panel, title, "option." + key, value, 0, 10000);
            Select(panel, "翻转方向", "option.flipperType", [("向下", "down"), ("向上", "up")], "down");
        }
        if (kind == "InputsDate")
        {
            var panel = Section("日期设置", true); Select(panel, "显示方式", "option.isPanel", [("下拉", "0"), ("面板", "1")], "0"); Select(panel, "默认值类型", "option.defaultType", [("固定", "static"), ("动态", "dynamic")], "static"); Number(panel, "默认时间戳 ms", "option.dataset", 0, 0, 1e14);
            for (int i = 0; i < 2; i++) { Number(panel, "偏移量 " + (i + 1), $"option.differValue[{i}]", 0, -100000, 100000); Select(panel, "偏移单位 " + (i + 1), $"option.differUnit[{i}]", [("天", "d"), ("小时", "h"), ("分钟", "m"), ("秒", "s"), ("月", "M"), ("年", "y")], "d"); }
        }
        if (kind == "Clock") { var panel = Section("时钟设置", true); Number(panel, "边框宽度", "option.border", 6, 0, 100); Color(panel, "表盘背景", "option.bgColor"); }
        if (kind is "PipelineH" or "PipelineV") { var panel = Section("管道设置", true); Color(panel, "外管颜色", "option.o_color"); Color(panel, "流体颜色", "option.i_color"); Select(panel, "颜色类型", "option.color_type", [("渐变", "1"), ("单色", "2")], "1"); Select(panel, "流动", "option.line_class", [("正向", "svg_ani_flow"), ("反向", "svg_ani_flow_reverse"), ("停止", "")], "svg_ani_flow"); }
        if (kind is "TextStateAssociation" or "TablesDatabase")
        {
            var panel = Section("状态关联", true);
            if (kind == "TextStateAssociation") { String(panel, "默认文字", "option.defaultText"); Color(panel, "默认背景", "option.defaultBackgroundColor"); }
            else String(panel, "关联字段", "option.associationKey");
            if (e.GetNode("option.associationList") is JsonArray states) for (int i = 0; i < states.Count; i++)
            {
                var index = i; var path = $"option.associationList[{i}]"; String(panel, "匹配值 " + (i + 1), path + ".value"); if (kind == "TextStateAssociation") String(panel, "显示文字", path + ".text"); Color(panel, "背景颜色", path + ".backgroundColor"); panel.Children.Add(Button("删除状态", () => Commit("删除状态关联", () => EnsureArray(e, "option.associationList").RemoveAt(index), true)));
            }
            panel.Children.Add(Button("添加状态", () => Commit("添加状态关联", () => EnsureArray(e, "option.associationList").Add(kind == "TextStateAssociation" ? new JsonObject { ["value"] = "", ["text"] = "", ["backgroundColor"] = "#18a058" } : new JsonObject { ["minValue"] = 0, ["maxValue"] = 0, ["color"] = "#ff0000" }), true)));
        }
        if (kind == "StudioIO")
        {
            var panel = Section("IO 设置", true); Number(panel, "灯大小", "option.round.size", 30, 1, 500); Color(panel, "默认灯颜色", "option.round.defaultColor"); Color(panel, "选中灯颜色", "option.round.selectColor"); Color(panel, "文字颜色", "option.text.color"); Number(panel, "字号", "option.text.fontSize", 20, 1, 200);
            if (e.GetNode("option.IO") is JsonArray groups) for (int i = 0; i < groups.Count; i++) { var path = $"option.IO[{i}]"; String(panel, "分组名称 " + (i + 1), path + ".label.dataset"); Color(panel, "标题颜色", path + ".label.color"); Number(panel, "标题字号", path + ".label.fontSize", 20, 1, 200); Color(panel, "标题背景", path + ".label.backGroundColor"); Color(panel, "内容背景", path + ".main.backGroundColor"); }
        }
    }
    private void StyleFields(HmiElement e)
    {
        var style = Section("样式");
        foreach (var (path, label) in new[] { ("fontColor", "文字颜色"), ("color", "颜色"), ("textColor", "文字颜色"), ("backgroundColor", "背景颜色"), ("borderColor", "边框颜色"), ("okColor", "OK 颜色"), ("ngColor", "NG 颜色"), ("colorShadow", "阴影颜色"), ("timeColor", "时间颜色"), ("gradient.from", "渐变起点"), ("gradient.to", "渐变终点") }) if (e.GetNode("option." + path) is JsonValue) Color(style, label, "option." + path);
        foreach (var (path, label, value) in new[] { ("fontSize", "字号", 16d), ("textSize", "字号", 24d), ("size", "大小", 48d), ("timeSize", "时间字号", 24d), ("paddingX", "横向内边距", 0d), ("paddingY", "纵向内边距", 0d), ("letterSpacing", "字间距", 0d), ("borderWidth", "边框宽度", 0d), ("borderRadius", "圆角", 0d), ("lineWidth", "线宽", 1d), ("lineHeight", "行高", 1d), ("dur", "动画时长", 3d), ("animationSpeed", "滚动速度", 50d), ("blurShadow", "阴影模糊", 8d), ("gradient.deg", "渐变角度", 45d) }) if (e.GetNode("option." + path) != null) Number(style, label, "option." + path, value, 0, 10000);
        if (e.GetNode("option.fontWeight") != null) Select(style, "字重", "option.fontWeight", [("常规", "normal"), ("加粗", "bold")], "normal");
        if (e.GetNode("option.textAlign") != null) Select(style, "水平对齐", "option.textAlign", [("左", "left"), ("中", "center"), ("右", "right")], "center");
        if (e.GetNode("option.writingMode") != null) Select(style, "文字方向", "option.writingMode", [("横向", "horizontal-tb"), ("纵向", "vertical-rl")], "horizontal-tb");
        foreach (var (path, label) in new[] { ("showShadow", "文字阴影"), ("bordered", "显示边框"), ("circle", "圆形"), ("dashed", "虚线"), ("ghost", "透明背景"), ("text", "文字按钮"), ("operationPermissions", "操作权限") }) if (e.GetNode("option." + path) is JsonValue) Boolean(style, label, "option." + path);
        if (e.GetNode("option.rotate") is JsonValue rotate && rotate.TryGetValue<bool>(out _)) Boolean(style, "旋转边框", "option.rotate");
        if (e.GetNode("option.colors") is JsonArray colors) for (int i = 0; i < colors.Count; i++) Color(style, "配色 " + (i + 1), $"option.colors[{i}]");
        foreach (var group in new[] { "headerText", "bottomText", "textStyle", "font", "icon" }) if (e.GetNode("option." + group) is JsonObject nested)
        {
            if (nested["fontColor"] != null) Color(style, group == "headerText" ? "标题颜色" : group == "bottomText" ? "底部颜色" : "文字颜色", $"option.{group}.fontColor");
            if (nested["fontSize"] != null) Number(style, group == "headerText" ? "标题字号" : group == "bottomText" ? "底部字号" : "文字大小", $"option.{group}.fontSize", 16, 1, 500);
        }
        if (e.Type.StartsWith("Border") && e.GetNode("option.borderTitle") != null) { String(style, "边框标题", "option.borderTitle"); Number(style, "标题宽度", "option.borderTitleWidth", 250, 1, 10000); Number(style, "标题高度", "option.borderTitleHeight", 32, 1, 10000); Number(style, "标题字号", "option.borderTitleSize", 18, 1, 500); Color(style, "标题颜色", "option.borderTitleColor"); }
    }
    private void BindingFields(HmiElement e)
    {
        if (e.GetNode("option.parameter") is JsonArray parameters)
        {
            var data = Section("数据绑定", true);
            for (int i = 0; i < parameters.Count; i++) Binding(data, "参数 " + (i + 1), $"option.parameter[{i}]");
            if (parameters.Count == 0) data.Children.Add(Button("添加参数绑定", () => Commit("添加参数绑定", () => e.SetNode("option.parameter", new JsonArray("")), true)));
            Note(data, "可选择全局变量或算子输出，也可直接输入 UUID&输出名[索引]。清空绑定后使用组件默认数据。");
        }
        if (e.GetNode("option.sourceTable") is JsonArray sources)
        {
            var data = Section("数据源", true); var names = TableNames();
            for (int i = 0; i < sources.Count; i++) Select(data, "源表 " + (i + 1), $"option.sourceTable[{i}]", names.Select(x => (x, x)), editable: true);
            if (sources.Count == 0) data.Children.Add(Button("添加数据源", () => Commit("添加数据源", () => e.SetNode("option.sourceTable", new JsonArray("")), true)));
            if (e.GetNode("option.sourceData") is JsonObject)
            {
                if (e.Type.StartsWith("StatisticsBar")) { Select(data, "时间 (h)", "option.sourceData.unit", [("0.5", "0.5"), ("1", "1"), ("2", "2")], "1", value => Commit("修改统计时间", () => { e.SetNode("option.sourceData.unit", JsonValue.Create(value)); e.SetNode("option.sourceData.totalNum", JsonValue.Create(12)); }, true)); Number(data, "数量", "option.sourceData.totalNum", 12, 4, e.GetString("option.sourceData.unit") == "0.5" ? 48 : e.GetString("option.sourceData.unit") == "2" ? 12 : 24); }
                else if (e.GetNode("option.sourceData.totalNum") != null) Number(data, "数量", "option.sourceData.totalNum", 12, 1, 10000);
                if (e.GetNode("option.sourceData.sourceLine") is JsonArray lines) for (int i = 0; i < lines.Count; i++) String(data, "数据列 " + (i + 1), $"option.sourceData.sourceLine[{i}]");
            }
        }
    }
    private IEnumerable<string> TableNames()
    {
        try { return _runtime?.QueryTable("").Select(x => HmiVisuals.Text(x.GetValueOrDefault("name"))).Where(x => x.Length > 0).ToArray() ?? []; } catch { return []; }
    }
    private void Binding(Panel panel, string label, string path)
    {
        var references = (_runtime?.BindingReferences() ?? []).Concat(_document?.AllElements.SelectMany(x => x.Bindings).Where(x => x.Kind is HmiBindingKind.GlobalVariable or HmiBindingKind.OperatorOutput).Select(x => x.Reference) ?? []).Distinct().OrderBy(x => x).ToArray();
        var choices = new[] { ("未绑定", "") }.Concat(references.Select(x => (x, x))); var picker = Select(panel, label, path, choices, editable: true);
        picker.ToolTip = "全局变量或 UUID&输出名[索引]";
    }
    private void PageChoice(Panel panel, string label, string path, bool subwindow)
    {
        var choices = (subwindow ? _document.Subwindows : _document.MainPages).Select((p, i) => (p.Title, i.ToString(CultureInfo.InvariantCulture)));
        Select(panel, label, path, choices, "0", value => Change(path, int.TryParse(value, out var index) ? index : 0, label));
    }
    private void ImageFields(HmiElement e)
    {
        var section = Section(e.Type == "ImageCarousel" ? "轮播配置" : "图像设置", true);
        if (e.Type is "OneD" or "TwoD" or "ThreeD") Number(section, "图像池 IM", "option.sceneIM", 0, 0, 31);
        if (e.Type == "ThreeD") { Boolean(section, "显示工具栏", "option.showButton", true); Boolean(section, "自动范围", "option.renderConfig.autoMatic", true); Number(section, "最小值", "option.renderConfig.min", -100); Number(section, "最大值", "option.renderConfig.max", 100); String(section, "绑定算子", "option.bindOperator"); }
        if (e.Type == "TwoD") { Select(section, "渲染模式", "option.renderConfig.renderMode", [("原色", "primary colors"), ("灰度", "gray"), ("伪彩色", "pseudo colors")], "primary colors"); Number(section, "无效值", "option.renderConfig.invalidValue", -10000); }
        if (e.ComponentType == "Image") { section.Children.Add(Button("导入本地图片", () => ImportImage(e, "option.dataset"))); String(section, "图片地址", "option.dataset", initial: e.GetString("option.dataset").StartsWith("data:") ? "（已嵌入图片）" : e.GetString("option.dataset"), setter: value => { if (value != "（已嵌入图片）") Change("option.dataset", value, "图片地址"); }); }
        if (e.GetNode("option.fit") != null) Select(section, "适应方式", "option.fit", [("包含", "contain"), ("覆盖", "cover"), ("填充", "fill"), ("原始大小", "none")], "contain");
        if (e.Type != "ImageCarousel") return;
        Boolean(section, "自动轮播", "option.autoplay", true); Number(section, "间隔 (ms)", "option.interval", 5000, 100, 3600000, 100); Select(section, "方向", "option.direction", [("横向", "horizontal"), ("纵向", "vertical")], "horizontal"); Boolean(section, "显示指示点", "option.showDots", true); Boolean(section, "显示箭头", "option.showArrow");
        if (e.GetNode("option.dataset") is JsonArray items) for (int i = 0; i < items.Count; i++)
        {
            var index = i; var item = new StackPanel { Margin = new Thickness(0, 10, 0, 6) }; section.Children.Add(item); Note(item, "图片 " + (i + 1)); String(item, "地址", $"option.dataset[{i}].url"); Boolean(item, "绑定图像池", $"option.dataset[{i}].bind"); Number(item, "图像池 IM", $"option.dataset[{i}].bindIndex", 0, 0, 31); item.Children.Add(Button("删除图片", () => Commit("删除轮播图片", () => ((JsonArray)e.GetNode("option.dataset")).RemoveAt(index), true)));
        }
        section.Children.Add(Button("添加图片", () => Commit("添加轮播图片", () => EnsureArray(e, "option.dataset").Add(new JsonObject { ["url"] = "", ["name"] = "", ["isLocal"] = false, ["bind"] = false, ["bindIndex"] = 0 }), true)));
    }
    private void ImportImage(HmiElement element, string path)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "图像|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif|所有文件|*.*" }; if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { var bytes = File.ReadAllBytes(dialog.FileName); var data = "data:image/" + Path.GetExtension(dialog.FileName).TrimStart('.') + ";base64," + Convert.ToBase64String(bytes); _ = HmiVisuals.Bitmap(data, _document.SourcePath); Commit("导入图片", () => { if (element == null) { _page.SetNode(path, JsonValue.Create(data)); _page.SetNode("editCanvasConfig.selectColor", JsonValue.Create(false)); } else element.SetNode(path, JsonValue.Create(data)); }, true); } catch (Exception ex) { _message.Text = "图片导入失败：" + ex.Message; }
    }
    private static JsonArray EnsureArray(HmiElement e, string path)
    { if (e.GetNode(path) is not JsonArray) e.SetNode(path, new JsonArray()); return (JsonArray)e.GetNode(path); }
    private void TabFields(HmiElement e)
    {
        var config = Section("标签页配置", true);
        Select(config, "标签类型", "option.tabType", [("线条", "bar"), ("分段", "segment")], "bar");
        var items = e.GetNode("option.dataset") as JsonArray ?? [];
        Select(config, "默认选项", "option.tabLabel", items.OfType<JsonObject>().Select(x => (HmiVisuals.Text(x["label"]), HmiVisuals.Text(x["label"]))));
        Select(config, "标签位置", "option.placement.position", [("上", "top"), ("下", "bottom"), ("左", "left"), ("右", "right")], "top");
        Select(config, "分段方向", "option.segmentDirection.direction", [("横向", "row"), ("纵向", "column")], "row");
        var tabs = Section("选项卡设置", true);
        var targets = _page.AllElements.Where(x => x.Id != e.Id).Select(x => (x.Title, x.Id)).ToArray();
        for (int i = 0; i < items.Count; i++)
        {
            var index = i; Note(tabs, "选项卡 " + (i + 1)); String(tabs, "选项名称", $"option.dataset[{i}].label"); Select(tabs, "关联组件", $"option.dataset[{i}].id", targets);
            var remove = Button("删除选项卡", () => Commit("删除选项卡", () => EnsureArray(e, "option.dataset").RemoveAt(index), true)); remove.IsEnabled = items.Count > 2; tabs.Children.Add(remove);
        }
        tabs.Children.Add(Button("添加选项卡", () => Commit("添加选项卡", () => { var array = EnsureArray(e, "option.dataset"); array.Add(new JsonObject { ["label"] = "选项卡 " + (array.Count + 1), ["value"] = (array.Count + 1).ToString(), ["id"] = "" }); }, true)));
        var style = Section("样式"); Color(style, "文字颜色", "option.textStyle.color"); Number(style, "字号", "option.textStyle.fontSize", 16, 1, 500); Select(style, "字重", "option.textStyle.fontWeight", [("常规", "normal"), ("加粗", "bold")], "normal"); Select(style, "水平对齐", "option.textStyle.textAlign", [("左", "left"), ("中", "center"), ("右", "right")], "center"); Number(style, "字间距", "option.textStyle.letterSpacing", 0, 0, 100);
    }
    private void IconGroup(HmiElement e)
    {
        var section = Section("控制组设置", true); Color(section, "颜色", "option.color", "#03a9f4"); Number(section, "图标大小", "option.size", 48, 8, 500);
        var choices = new[] { ("运行状态", "lens"), ("暂停", "pauseCircleSolid"), ("运行", "nextSolid"), ("循环运行", "syncRounded"), ("全屏", "baselineCropFree"), ("关闭", "outlineClear"), ("保存", "save"), ("清除", "TrashAltRegular") };
        if (e.GetNode("option.IconGroup") is JsonArray items) for (int i = 0; i < items.Count; i++)
        {
            var index = i; Select(section, "图标 " + (i + 1), $"option.IconGroup[{i}].name", choices); String(section, "名称", $"option.IconGroup[{i}].label"); section.Children.Add(Button("删除图标", () => Commit("删除控制图标", () => EnsureArray(e, "option.IconGroup").RemoveAt(index), true)));
        }
        section.Children.Add(Button("添加图标", () => Commit("添加控制图标", () => EnsureArray(e, "option.IconGroup").Add(new JsonObject { ["name"] = "nextSolid", ["label"] = "运行" }), true)));
    }
    private static readonly (string Label, string Value)[] ActionOptions = [("切换看板", "bulletinBoard"), ("打开子窗口", "openSubwindow"), ("关闭子窗口", "closeSubwindow"), ("运行", "singleStepOperation"), ("循环运行", "cycleOperation"), ("暂停", "suspend"), ("清除程序状态", "clearProcedureState"), ("保存参数", "saveParams"), ("全屏", "fullScreen"), ("锁定", "lockScreen"), ("MES 请求", "mesRequest")];
    private static readonly (string Label, string Value)[] TriggerOptions = [("单击", "click"), ("双击", "dblclick"), ("按下", "mousedown"), ("抬起", "mouseup"), ("移入", "mouseenter"), ("移出", "mouseleave")];
    private void ButtonStates(HmiElement e)
    {
        var section = Section("按钮配置", true); var states = e.GetNode("option.iconGroup") as JsonArray ?? [];
        Select(section, "默认状态", "option.defaultIconIndex", states.Select((_, i) => ("状态 " + (i + 1), i.ToString())), "0", value => Change("option.defaultIconIndex", int.TryParse(value, out var i) ? i : 0, "默认状态"));
        Color(section, "外框颜色", "option.o_color", "#0a7ae2"); Color(section, "内部颜色", "option.i_color", "#119bfa");
        for (int i = 0; i < states.Count; i++)
        {
            var index = i; var path = $"option.iconGroup[{i}]"; var state = Section("按钮状态 " + (i + 1), i == 0);
            String(state, "文字", path + ".data"); Number(state, "字号", path + ".fontSize", 16, 1, 500); Select(state, "类型", path + ".type", [("默认", "default"), ("信息", "info"), ("成功", "success"), ("警告", "warning"), ("错误", "error")], "info");
            foreach (var (key, label) in new[] { ("bordered", "边框"), ("circle", "圆形"), ("dashed", "虚线"), ("ghost", "透明背景"), ("text", "文字按钮") }) Boolean(state, label, path + "." + key);
            Select(state, "状态切换", path + ".switchEventType", TriggerOptions, "click");
            if (e.GetNode(path + ".events") is JsonArray events) for (int j = 0; j < events.Count; j++) EventFields(e, state, path + ".events", j);
            state.Children.Add(Button("添加事件", () => Commit("添加按钮事件", () => EnsureArray(e, path + ".events").Add(new JsonObject { ["type"] = "click", ["event"] = "singleStepOperation", ["target"] = "" }), true)));
            var remove = Button("删除按钮状态", () => Commit("删除按钮状态", () => { EnsureArray(e, "option.iconGroup").RemoveAt(index); if (e.GetNumber("option.defaultIconIndex") >= states.Count - 1) e.SetNode("option.defaultIconIndex", JsonValue.Create(0)); }, true)); remove.IsEnabled = states.Count > 1; state.Children.Add(remove);
        }
        section.Children.Add(Button("添加按钮状态", () => Commit("添加按钮状态", () => EnsureArray(e, "option.iconGroup").Add(new JsonObject { ["data"] = "按钮", ["fontSize"] = 16, ["type"] = "info", ["bordered"] = true, ["ghost"] = false, ["events"] = new JsonArray(), ["switchEventType"] = "click" }), true)));
    }
    private void EventFields(HmiElement e, Panel panel, string collectionPath, int index)
    {
        var path = collectionPath + $"[{index}]"; Note(panel, "事件 " + (index + 1)); Select(panel, "触发方式", path + ".type", TriggerOptions, "click");
        Select(panel, "动作", path + ".event", ActionOptions, "singleStepOperation", value => Change(path + ".event", value, "事件动作", true));
        var action = e.GetString(path + ".event");
        if (action == "bulletinBoard") PageChoice(panel, "目标看板", path + ".target", false);
        else if (action is "openSubwindow" or "subwindow") PageChoice(panel, "目标子窗口", path + ".target", true);
        else if (!string.IsNullOrEmpty(e.GetString(path + ".target"))) String(panel, "目标", path + ".target");
        panel.Children.Add(Button("删除事件", () => Commit("删除按钮事件", () => EnsureArray(e, collectionPath).RemoveAt(index), true)));
    }
    private void MesFields(HmiElement e)
    {
        var section = Section("MES 请求", true); String(section, "按钮文字", "option.label"); Select(section, "请求方法", "option.requestType", [("GET", "GET"), ("POST", "POST"), ("PUT", "PUT"), ("DELETE", "DELETE")], "POST"); String(section, "请求地址", "option.requestURL"); Select(section, "触发方式", "option.requestMethod", [("点击", "click"), ("定时", "interval")], "click"); Number(section, "间隔", "option.requestIntervbal", 1000, 1, 3600000); Select(section, "时间单位", "option.requestUnit", [("秒", "second"), ("分", "minute"), ("小时", "hour")], "second");
        if (e.GetNode("option.requestParams") is JsonArray fields) for (int i = 0; i < fields.Count; i++)
        {
            var index = i; Note(section, "请求参数 " + (i + 1)); String(section, "键", $"option.requestParams[{i}].key"); String(section, "值", $"option.requestParams[{i}].value"); Binding(section, "绑定值", $"option.requestParams[{i}].bindValue"); section.Children.Add(Button("删除参数", () => Commit("删除请求参数", () => EnsureArray(e, "option.requestParams").RemoveAt(index), true)));
        }
        section.Children.Add(Button("添加请求参数", () => Commit("添加请求参数", () => EnsureArray(e, "option.requestParams").Add(new JsonObject { ["key"] = "", ["value"] = "", ["bindParams"] = "", ["bindValue"] = "" }), true)));
    }
    private void ChartFields(HmiElement e)
    {
        var global = Section("图表全局设置"); Boolean(global, "显示图例", "option.legend.show", true); Color(global, "图例颜色", "option.legend.textStyle.color", "#b9b8ce"); Number(global, "图例字号", "option.legend.textStyle.fontSize", 12, 1, 200); Select(global, "图例方向", "option.legend.orient", [("横向", "horizontal"), ("纵向", "vertical")], "horizontal"); Boolean(global, "提示框", "option.tooltip.show", true);
        foreach (var axis in new[] { "xAxis", "yAxis" }) if (e.GetNode("option." + axis) is JsonObject)
        {
            var panel = Section(axis == "xAxis" ? "X 轴设置" : "Y 轴设置"); var prefix = "option." + axis; Boolean(panel, "显示", prefix + ".show", true); String(panel, "轴名称", prefix + ".name"); Boolean(panel, "反向", prefix + ".inverse"); Boolean(panel, "显示刻度文字", prefix + ".axisLabel.show", true); Number(panel, "字号", prefix + ".axisLabel.fontSize", 12, 1, 200); Color(panel, "文字颜色", prefix + ".axisLabel.color", "#b9b8ce"); Number(panel, "文字角度", prefix + ".axisLabel.rotate", 0, -360, 360); Boolean(panel, "显示网格线", prefix + ".splitLine.show", axis == "yAxis"); Color(panel, "网格线颜色", prefix + ".splitLine.lineStyle.color", "#484753");
        }
        if (e.GetNode("option.grid") is JsonObject) { var grid = Section("图表边距"); foreach (var (side, label) in new[] { ("left", "左"), ("top", "上"), ("right", "右"), ("bottom", "下") }) Number(grid, label, "option.grid." + side, 60, 0, 1000); }
        var series = (JsonArray)e.GetNode("option.series"); for (int i = 0; i < series.Count; i++)
        {
            var panel = Section("系列 " + (i + 1)); var path = $"option.series[{i}]"; var type = e.GetString(path + ".type");
            if (type == "bar") { Number(panel, "柱宽", path + ".barWidth", 8, 1, 100); Number(panel, "圆角", path + ".itemStyle.borderRadius", 2, 0, 100); }
            else if (type == "line") { Boolean(panel, "平滑曲线", path + ".smooth"); Boolean(panel, "阶梯线", path + ".step"); Number(panel, "线宽", path + ".lineStyle.width", 2, 0, 100); Number(panel, "标记大小", path + ".symbolSize", 0, 0, 100); Select(panel, "线型", path + ".lineStyle.type", [("实线", "solid"), ("虚线", "dashed"), ("点线", "dotted")], "solid"); }
            Color(panel, "系列颜色", path + ".itemStyle.color", HmiVisuals.Palette[i % HmiVisuals.Palette.Length]); Boolean(panel, "显示数值标签", path + ".label.show"); Number(panel, "标签字号", path + ".label.fontSize", 12, 1, 200); Color(panel, "标签颜色", path + ".label.color"); Select(panel, "标签位置", path + ".label.position", [("上", "top"), ("下", "bottom"), ("左", "left"), ("右", "right"), ("内侧", "inside"), ("中心", "center")], "top");
            if (type == "pie" && e.GetNode(path + ".radius") is JsonArray radius) for (int j = 0; j < radius.Count; j++) String(panel, j == 0 ? "内半径" : "外半径", path + $".radius[{j}]");
        }
        if (e.GetNode("option.customThemeColor.themeColor.color") is JsonArray palette) { var colors = Section("主题颜色"); for (int i = 0; i < palette.Count; i++) Color(colors, "颜色 " + (i + 1), $"option.customThemeColor.themeColor.color[{i}]"); }
    }
    private void TableFields(HmiElement e)
    {
        var panel = Section("表格设置", true);
        foreach (var (path, label, value) in new[] { ("cellHeight", "行高", 30d), ("cellWidth", "列宽", 130d), ("pageSize", "每页条数", 20d), ("rowNum", "显示行数", 5d), ("waitTime", "轮播间隔", 2d), ("headerHeight", "表头高度", 35d) }) if (e.GetNode("option." + path) != null) Number(panel, label, "option." + path, value, 1, 10000);
        foreach (var (path, label) in new[] { ("isAutoCell", "自动单元格"), ("firstCellFix", "固定首列"), ("showIndexColumn", "显示序号"), ("singleColumn", "竖线"), ("singleLine", "横线"), ("showIndication", "数值指示"), ("index", "序号"), ("sort", "排序") }) if (e.GetNode("option." + path) != null) Boolean(panel, label, "option." + path);
        foreach (var (path, label) in new[] { ("thBackgroundColor", "表头背景"), ("trBackgroundColor", "行背景"), ("headerBGC", "表头背景"), ("oddRowBGC", "奇数行背景"), ("evenRowBGC", "偶数行背景"), ("indicationColor", "指示颜色"), ("newlyAddedColor", "新增行颜色") }) if (e.GetNode("option." + path) != null) Color(panel, label, "option." + path);
        if (e.GetNode("option.dataset.columns") is JsonArray columns)
        {
            var fields = Section("表格列"); for (int i = 0; i < columns.Count; i++) { Note(fields, "列 " + (i + 1)); String(fields, "标题", $"option.dataset.columns[{i}].title"); String(fields, "字段", $"option.dataset.columns[{i}].key"); Number(fields, "宽度", $"option.dataset.columns[{i}].width", 130, 10, 10000); }
        }
        if (e.GetNode("option.header") is JsonArray headers) for (int i = 0; i < headers.Count; i++) String(panel, "表头 " + (i + 1), $"option.header[{i}]");
        if (e.Type == "TablesSearch") { var search = Section("查询设置"); Select(search, "源表", "option.condition.tableSource", TableNames().Select(x => (x, x)), editable: true); Select(search, "结果筛选", "option.condition.OkNGStatus", [("全部", "All"), ("OK", "OK"), ("NG", "NG")], "All"); }
        if (e.GetNode("option.selectDataParameter") is JsonArray selects) for (int i = 0; i < selects.Count; i++) String(panel, "数据字段 " + (i + 1), $"option.selectDataParameter[{i}]");
    }
    private void DatasetEditor(HmiElement e, string path)
    {
        if (e.GetNode(path) is not JsonArray source) return; var panel = Section("默认数据"); Note(panel, "用于设计预览和未绑定数据时的显示。运行时绑定数据不会写入此表。");
        var keys = source.OfType<JsonObject>().SelectMany(x => x.Select(p => p.Key)).Distinct().ToArray(); if (keys.Length == 0) return;
        var copy = new DataTable(); foreach (var key in keys) copy.Columns.Add(key, typeof(string)); foreach (var row in source.OfType<JsonObject>()) copy.Rows.Add(keys.Select(key => (object)HmiVisuals.Text(row[key])).ToArray());
        var grid = new DataGrid { ItemsSource = copy.DefaultView, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, MaxHeight = 230, MinHeight = 110, HeadersVisibility = DataGridHeadersVisibility.Column, Background = HmiVisuals.Brush("#282828"), Foreground = Foreground, RowBackground = HmiVisuals.Brush("#323232"), BorderThickness = new Thickness(0) };
        for (int i = 0; i < keys.Length; i++) grid.Columns.Add(new DataGridTextColumn { Header = keys[i], Binding = new Binding($"[{i}]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, MinWidth = 75 });
        grid.CellEditEnding += (sender, args) =>
        {
            if (args.EditAction != DataGridEditAction.Commit || args.EditingElement is not TextBox editor || args.Row.Item is not DataRowView row) return;
            var rowIndex = copy.Rows.IndexOf(row.Row); var key = keys[args.Column.DisplayIndex]; var original = source[rowIndex]?[key]; var text = editor.Text; JsonNode value;
            var draftKey = path + "/" + rowIndex + "/" + key;
            if (original is JsonValue scalar && scalar.TryGetValue<double>(out _)) { if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) { args.Cancel = true; _invalidDrafts[draftKey] = text; _message.Text = "该数据单元格必须为数字"; return; } value = JsonValue.Create(number); }
            else if (original is JsonValue flag && flag.TryGetValue<bool>(out _)) { if (!bool.TryParse(text, out var b)) { args.Cancel = true; _invalidDrafts[draftKey] = text; _message.Text = "该数据单元格必须为 true 或 false"; return; } value = JsonValue.Create(b); }
            else value = JsonValue.Create(text);
            _invalidDrafts.Remove(draftKey);
            Commit("修改默认数据", () => { if (e.GetNode(path) is JsonArray rows && rows[rowIndex] is JsonObject target) target[key] = value.DeepClone(); });
        };
        _editors[path] = grid; panel.Children.Add(grid);
        panel.Children.Add(Button("添加数据行", () => Commit("添加默认数据", () => { var row = new JsonObject(); foreach (var key in keys) row[key] = source.FirstOrDefault()?[key] is JsonValue v && v.TryGetValue<double>(out var oldNumber) ? JsonValue.Create(0) : JsonValue.Create(""); EnsureArray(e, path).Add(row); }, true)));
    }
}
