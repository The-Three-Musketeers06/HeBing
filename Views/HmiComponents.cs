using System.Collections;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AiVisionStudio.Models;

namespace AiVisionStudio.Views;

internal static class HmiComponents
{
    private static readonly HashSet<string> ChartTypes = new(StringComparer.Ordinal)
    { "BarCommon", "BarCrossrange", "BarLine", "LineCommon", "LineLinear", "LineGradients", "StatisticsLineCommon", "StatisticsBarCommon", "StatisticsBarCrossrange", "CapsuleChart", "BarStatistical", "PieCommon", "PieCircle", "OneD" };

    public static FrameworkElement Create(HmiSurface host, HmiElement e, HmiDocument document, IHmiRuntimeContext runtime, List<Action> refresh, List<Action> dispose, List<Func<bool>> commit)
    {
        var additional = HmiAdditionalComponents.TryCreate(host, e, document, runtime, refresh, dispose, commit);
        if (additional != null) return additional;
        if (e.Type == "ThreeEarth") { var earth = new HmiEarthView(e); refresh.Add(earth.Refresh); dispose.Add(earth.Dispose); return earth; }
        if (HmiAdvancedChart.Types.Contains(e.Type))
        {
            var advanced = new HmiAdvancedChart(e); var references = HmiVisuals.References(e);
            refresh.Add(() =>
            {
                JsonNode dataset = e.GetNode("option.dataset");
                if (dataset == null && e.Type == "Graph") dataset = e.GetNode("option.series[0]");
                if (!host.IsDesignMode && references.Any(r => !string.IsNullOrEmpty(r)))
                {
                    var values = references.Select(r => string.IsNullOrEmpty(r) ? null : HmiVisuals.Unwrap(runtime?.Read(r))).ToArray();
                    if (values.All(v => v == null)) dataset = null;
                    else if (e.Type == "Radar")
                    {
                        dataset = dataset?.DeepClone() ?? new JsonObject();
                        dataset["seriesData"] = new JsonArray(new JsonObject { ["name"] = e.GetString("option.dataset.seriesData[0].name", "数据"), ["value"] = JsonSerializer.SerializeToNode(values.Select(v => v == null ? 0 : HmiVisuals.Number(v)).ToArray()) });
                    }
                    else if (e.Type == "Funnel")
                    {
                        dataset = dataset?.DeepClone() ?? new JsonObject(); var key = HmiVisuals.Strings(dataset["dimensions"]).ElementAtOrDefault(1) ?? "value";
                        if (dataset["source"] is JsonArray rows) for (int i = 0; i < rows.Count; i++) if (rows[i] is JsonObject row) row[key] = JsonSerializer.SerializeToNode(values.ElementAtOrDefault(i));
                    }
                    else dataset = JsonSerializer.SerializeToNode(values.FirstOrDefault(v => v != null));
                }
                advanced.SetData(dataset);
            });
            return advanced;
        }
        if (e.Type.StartsWith("Border", StringComparison.Ordinal) || e.Type.StartsWith("Decorates", StringComparison.Ordinal) || e.Type == "Title01") return new HmiDecoration(e);
        if (e.Type == "TimeCommon")
        {
            var time = new TextBlock { FontSize = e.GetNumber("option.timeSize", 24), Foreground = HmiVisuals.Brush(e.GetString("option.timeColor", "#e6f7ff")), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
            refresh.Add(() => time.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")); return time;
        }
        if (e.Type == "Weather") return new TextBlock { Text = "天气数据未连接", FontSize = e.GetNumber("option.font.fontSize", 24), Foreground = HmiVisuals.Brush(e.GetString("option.font.fontColor", "#e6f7ff")), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, ToolTip = "原组件依赖外部天气服务；当前没有可验证的数据源。" };
        if (e.Type == "InputsTab") return Tabs(host, e, document, refresh);
        if (ChartTypes.Contains(e.Type)) return Chart(host, e, runtime, refresh);
        if (e.Type is "TablesBasic" or "TablesStatistics" or "TablesSearch" or "TablesFormula" or "TableScrollBoard" or "TableList") return Table(host, e, runtime, refresh);
        if (e.Type.StartsWith("Text", StringComparison.Ordinal)) return Text(host, e, runtime, refresh);
        if (e.Type is "StudioGroup" or "SubwindowGroup")
        {
            var panel = new UniformGrid { Rows = 1 };
            if (e.Option?["IconGroup"] is JsonArray icons)
                foreach (var icon in icons.OfType<JsonObject>()) panel.Children.Add(Icon(host, e, runtime, HmiVisuals.Text(icon["name"]), HmiVisuals.Text(icon["label"]), refresh));
            return panel;
        }
        if (e.Type is "InputsParams")
        {
            var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new());
            var label = new TextBlock { Text = e.GetString("option.aliasName"), Foreground = HmiVisuals.Brush(e.Foreground), FontSize = e.FontSize, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6) };
            var input = new TextBox { Text = e.GetString("option.inputValue", ""), FontSize = e.FontSize, Foreground = HmiVisuals.Brush(e.Foreground), Background = HmiVisuals.Brush("#343438"), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(3), Padding = new Thickness(6, 3, 6, 3), IsReadOnly = host.IsDesignMode };
            Grid.SetColumn(input, 1); grid.Children.Add(label); grid.Children.Add(input);
            var reference = HmiVisuals.References(e).FirstOrDefault(x => !string.IsNullOrEmpty(x));
            bool dirty = false, updating = false;
            input.TextChanged += (_, _) => { if (!updating) dirty = true; };
            bool Apply()
            {
                if (host.IsDesignMode || !dirty) return true;
                string error = "未绑定变量";
                if (!string.IsNullOrEmpty(reference) && runtime != null && runtime.Write(reference, input.Text, out error)) { dirty = false; input.BorderBrush = HmiVisuals.Brush("#03a9f4"); host.Report("参数已应用：" + reference); return true; }
                input.BorderBrush = Brushes.OrangeRed; input.ToolTip = error; host.Report(error); return false;
            }
            commit.Add(Apply); input.KeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Enter) { Apply(); args.Handled = true; } };
            input.LostKeyboardFocus += (_, _) => Apply();
            refresh.Add(() => { if (host.IsDesignMode || dirty || input.IsKeyboardFocusWithin) return; updating = true; var value = runtime?.Read(reference ?? ""); input.Text = HmiVisuals.Text(value); input.ToolTip = value == null ? "绑定暂无数据：" + reference : reference; updating = false; });
            return grid;
        }
        if (e.Type == "SwitchButton")
        {
            var toggle = new ToggleButton { Content = "关闭", FontSize = Math.Max(12, e.FontSize), Foreground = Brushes.White, Background = HmiVisuals.Brush("#66666d"), BorderThickness = new Thickness(0), Margin = new Thickness(2), IsEnabled = !host.IsDesignMode };
            var reference = HmiVisuals.References(e).FirstOrDefault(x => !string.IsNullOrEmpty(x)); bool update = false;
            toggle.Click += (_, _) => { if (update) return; string error = "未绑定变量"; if (runtime == null || string.IsNullOrEmpty(reference) || !runtime.Write(reference, toggle.IsChecked == true, out error)) { host.Report(error); Refresh(); } };
            void Refresh()
            {
                update = true; var value = host.IsDesignMode ? HmiVisuals.Unwrap(e.Option?["dataset"]) : runtime?.Read(reference ?? "");
                var on = HmiVisuals.Boolean(value); toggle.IsChecked = on;
                toggle.Content = e.GetBoolean("option.textConfig.open") ? e.GetString(on ? "option.textConfig.openText" : "option.textConfig.closeText", on ? "开启" : "关闭") : on ? "●  开启" : "关闭  ●";
                toggle.Background = HmiVisuals.Brush(e.GetBoolean("option.colorConfig.open") ? e.GetString(on ? "option.colorConfig.openColor" : "option.colorConfig.closeColor") : on ? "#2080f0" : "#55555b");
                toggle.ToolTip = value == null && !host.IsDesignMode ? "绑定暂无数据：" + reference : reference; update = false;
            }
            refresh.Add(Refresh); return toggle;
        }
        if (e.Type is "TwoD" or "ThreeD" or "OneD")
        {
            var view = new ImageView(); var pool = (int)e.GetNumber("option.sceneIM"); object last = null;
            IReadOnlyList<Algorithm.NativeOverlay3D> lastNative = Array.Empty<Algorithm.NativeOverlay3D>();
            IReadOnlyList<Algorithm.Box3> lastBoxes = Array.Empty<Algorithm.Box3>();
            IReadOnlyList<Algorithm.Text3D> lastTexts = Array.Empty<Algorithm.Text3D>();
            refresh.Add(() =>
            {
                if (host.IsDesignMode) return;
                object current = e.Type == "ThreeD" ? runtime?.Cloud(pool) : e.Type == "OneD" ? runtime?.Profile(pool) : runtime?.Image(pool);
                var native = runtime?.NativeOverlays(pool) ?? Array.Empty<Algorithm.NativeOverlay3D>();
                var boxes = runtime?.BoxOverlays(pool) ?? Array.Empty<Algorithm.Box3>();
                var texts = runtime?.TextOverlays(pool) ?? Array.Empty<Algorithm.Text3D>();
                var imageChanged = !ReferenceEquals(last, current);
                if (!imageChanged && native.SequenceEqual(lastNative) && boxes.SequenceEqual(lastBoxes) && texts.SequenceEqual(lastTexts)) return;
                last = current; lastNative = native; lastBoxes = boxes; lastTexts = texts;
                if (current is Algorithm.ProfileData profile) view.ShowProfile(profile);
                else if (current is Algorithm.PointCloud cloud) view.ShowCloud(cloud, texts, boxes);
                else if (current is OpenCvSharp.Mat mat) { if (imageChanged) view.ShowMat(mat); }
                else view.Clear();
                view.SetNativeOverlays(native);
            });
            view.ToolTip = "IM" + pool;
            if (e.Type == "TwoD") { var menu = new ContextMenu(); var load = new MenuItem { Header = "加载本地图片到 IM" + pool, IsEnabled = !host.IsDesignMode }; load.Click += async (_, _) => await host.Act("importImage", e); menu.Items.Add(load); view.ContextMenu = menu; }
            return view;
        }
        if (e.Type is "Image" or "ImageCarousel" || e.ComponentType == "Image") return Image(host, e, document, runtime, refresh, dispose);
        if (e.Type == "SquareButton") return SquareButton(host, e);
        if (e.Type is "SubwindowButton" or "MESButton")
        {
            var button = StyledButton(e.GetString("option.label", e.GetString("option.data", e.Title)), e);
            button.Click += async (_, _) => await host.Act(e.Type == "MESButton" ? "mes" : "subwindow", e, e.GetString("option.bindSubwindowsIndex")); return button;
        }
        if (e.Type is "NGStatistics" or "NGStatisticsSearch") return Statistics(host, e, runtime, refresh);
        if (e.Type == "SystemInfo") return SystemInformation(e, runtime, refresh);
        if (e.Type == "Iframe")
        {
            var grid = new Grid(); var browser = new WebBrowser { Visibility = Visibility.Collapsed };
            var label = new TextBlock { Text = e.GetString("option.dataset"), Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var open = HmiWindow.Button("加载网页", (_, _) => { try { if (!Uri.TryCreate(e.GetString("option.dataset"), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidDataException("网页地址必须为 HTTP/HTTPS"); browser.Navigate(uri); browser.Visibility = Visibility.Visible; } catch (Exception ex) { host.Report(ex.Message); } });
            open.HorizontalAlignment = HorizontalAlignment.Center; open.VerticalAlignment = VerticalAlignment.Bottom; open.IsEnabled = !host.IsDesignMode;
            grid.Children.Add(label); grid.Children.Add(browser); grid.Children.Add(open); dispose.Add(() => browser.Dispose()); return grid;
        }
        if (e.ComponentType == "Icon" || e.Type is "lens" or "save" or "pauseCircleSolid" or "nextSolid" or "syncRounded" or "TrashAltRegular" or "twotoneZoomOutMap" or "twotoneZoomInMap" or "IcRoundLockOpen") return Icon(host, e, runtime, e.Type, e.Title, refresh);
        host.UnsupportedTypes.Add(e.Type);
        return new Border { BorderBrush = Brushes.OrangeRed, BorderThickness = new Thickness(1), Child = new TextBlock { Text = "未支持的组件：" + e.Type, Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap } };
    }

    private static FrameworkElement Text(HmiSurface host, HmiElement e, IHmiRuntimeContext runtime, List<Action> refresh)
    {
        var text = new TextBlock { FontSize = e.GetNumber("option.fontSize", e.GetNumber("option.size", 20)), Foreground = HmiVisuals.Brush(e.Foreground), FontWeight = e.GetString("option.fontWeight") == "bold" ? FontWeights.Bold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap, TextAlignment = e.GetString("option.textAlign", "left") switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => TextAlignment.Left }, VerticalAlignment = VerticalAlignment.Center };
        if (e.Type == "TextGradient") text.Foreground = new LinearGradientBrush(HmiVisuals.Color(e.GetString("option.gradient.from", "#0000ff")), HmiVisuals.Color(e.GetString("option.gradient.to", "#ff0000")), e.GetNumber("option.gradient.deg", 45));
        if (e.Type == "TextBarrage" && e.GetBoolean("option.showShadow")) text.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = HmiVisuals.Color(e.GetString("option.colorShadow", "#33da94")), BlurRadius = e.GetNumber("option.blurShadow", 8), ShadowDepth = 0 };
        var border = new Border { Child = text, Background = HmiVisuals.Brush(e.Background, "transparent"), BorderBrush = HmiVisuals.Brush(e.GetString("option.borderColor", "transparent")), BorderThickness = new Thickness(e.GetNumber("option.borderWidth")), CornerRadius = new CornerRadius(e.GetNumber("option.borderRadius")), Padding = new Thickness(e.GetNumber("option.paddingX", e.GetNumber("option.paddingLeft")), e.GetNumber("option.paddingY", e.GetNumber("option.paddingTop")), e.GetNumber("option.paddingX", e.GetNumber("option.paddingRight")), e.GetNumber("option.paddingY", e.GetNumber("option.paddingBottom"))), ClipToBounds = true };
        refresh.Add(() =>
        {
            var value = HmiVisuals.Value(e, runtime, host.IsDesignMode);
            if (e.Type == "TextLog" && !host.IsDesignMode && !HmiVisuals.References(e).Any(x => !string.IsNullOrEmpty(x))) value = string.Join("\n", HmiVisuals.Rows(runtime?.Read("$logs")).Select(row => $"{HmiVisuals.Text(row.GetValueOrDefault("Time"))} {HmiVisuals.Text(row.GetValueOrDefault("Level"))} {HmiVisuals.Text(row.GetValueOrDefault("Message"))}"));
            if (e.Type == "TextOKNG")
            {
                var ok = HmiVisuals.Boolean(value); text.Text = value == null ? "—" : ok ? "OK" : "NG";
                text.Foreground = HmiVisuals.Brush(value == null ? "#888888" : e.GetString(ok ? "option.okColor" : "option.ngColor", ok ? "#00ff00" : "#ff0000"));
            }
            else text.Text = value == null ? "—" : HmiVisuals.Text(value);
            text.ToolTip = value == null && HmiVisuals.References(e).Any(x => !string.IsNullOrEmpty(x)) ? "绑定暂无数据：" + string.Join(", ", HmiVisuals.References(e)) : null;
        });
        if (e.Type == "TextLog") return new ScrollViewer { Content = border, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (e.Type == "TextBarrage" && !host.IsDesignMode)
        {
            var transform = new TranslateTransform(); text.RenderTransform = transform;
            border.Loaded += (_, _) => { var width = Math.Max(border.ActualWidth, e.Width); var speed = Math.Max(1, e.GetNumber("option.animationSpeed", 50)); transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(width, -width, TimeSpan.FromSeconds(width * 2 / speed)) { RepeatBehavior = RepeatBehavior.Forever }); };
        }
        return border;
    }
    private static FrameworkElement Icon(HmiSurface host, HmiElement e, IHmiRuntimeContext runtime, string icon, string label, List<Action> refresh)
    {
        string glyph = icon switch { "lens" => "●", "pauseCircleSolid" => "❚❚", "nextSolid" => "▶", "syncRounded" => "⟳", "save" => "▣", "TrashAltRegular" => "⌫", "outlineClear" => "×", "IcRoundLockOpen" => "♙", _ => "⛶" };
        var button = new Button { Content = glyph, ToolTip = label, FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = e.GetNumber("option.size", 48), Foreground = HmiVisuals.Brush(e.GetString("option.color", "#03a9f4")), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Focusable = false };
        button.RenderTransformOrigin = new Point(.5, .5); button.RenderTransform = new RotateTransform(e.GetNumber("option.rotate"));
        if (icon == "lens") { button.IsHitTestVisible = false; refresh.Add(() => button.Foreground = HmiVisuals.Brush(runtime?.IsRunning == true ? "#4ccc64" : e.GetString("option.color", "#03a9f4"))); }
        else button.Click += async (_, _) => { if (icon == "save" && !host.CommitInputs()) return; await host.Act(icon, e); };
        return button;
    }
    private static FrameworkElement Tabs(HmiSurface host, HmiElement e, HmiDocument document, List<Action> refresh)
    {
        var items = (e.GetNode("option.dataset") as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var segment = e.GetString("option.tabType", "bar") == "segment"; var placement = e.GetString("option.placement.position", "top");
        var vertical = segment ? e.GetString("option.segmentDirection.direction", "row") == "column" : placement is "left" or "right";
        var panel = new UniformGrid { Rows = vertical ? Math.Max(1, items.Length) : 1, Columns = vertical ? 1 : Math.Max(1, items.Length), Background = segment ? HmiVisuals.Brush("#18181c") : Brushes.Transparent };
        var selected = e.GetString("option.tabLabel"); var frames = new List<Border>();
        void Apply()
        {
            for (int i = 0; i < items.Length; i++)
            {
                var active = HmiVisuals.Text(items[i]["label"]) == selected;
                frames[i].Background = segment && active ? HmiVisuals.Brush("#3c3c40") : Brushes.Transparent;
                frames[i].BorderBrush = HmiVisuals.Brush(active ? "#63b3ed" : "#ffffff18");
                frames[i].BorderThickness = segment ? new Thickness(0) : placement switch { "bottom" => new Thickness(0, active ? 3 : 1, 0, 0), "left" => new Thickness(0, 0, active ? 3 : 1, 0), "right" => new Thickness(active ? 3 : 1, 0, 0, 0), _ => new Thickness(0, 0, 0, active ? 3 : 1) };
                var target = document.AllElements.FirstOrDefault(x => x.Id == HmiVisuals.Text(items[i]["id"]));
                if (target != null && target.Id != e.Id) Show(target, active);
            }
        }
        void Show(HmiElement target, bool visible) { host.SetElementVisible(target.Id, visible); foreach (var child in target.Children) Show(child, visible); }
        for (int i = 0; i < items.Length; i++)
        {
            var item = items[i]; var title = new HmiSpacedLabel { Text = HmiVisuals.Text(item["label"]), FontSize = e.GetNumber("option.textStyle.fontSize", 16), Foreground = HmiVisuals.Brush(e.GetString("option.textStyle.color", "#ffffff")), Bold = e.GetString("option.textStyle.fontWeight") == "bold", Spacing = e.GetNumber("option.textStyle.letterSpacing"), Alignment = e.GetString("option.textStyle.textAlign", "center") };
            var border = new Border { Child = title, CornerRadius = new CornerRadius(segment ? 3 : 0), Margin = segment ? new Thickness(3) : new Thickness(0), Padding = new Thickness(8, 2, 8, 2), Cursor = System.Windows.Input.Cursors.Hand };
            border.MouseLeftButtonUp += (_, args) => { if (host.IsDesignMode) return; selected = HmiVisuals.Text(item["label"]); Apply(); args.Handled = true; };
            frames.Add(border); panel.Children.Add(border);
        }
        refresh.Add(Apply); return panel;
    }
    private static Button StyledButton(string text, HmiElement e)
    {
        var color = e.GetString("option.type") switch { "error" => "#d03050", "success" => "#18a058", "warning" => "#f0a020", _ => "#2080f0" };
        return new Button { Content = text, FontSize = e.GetNumber("option.fontSize", 16), Foreground = HmiVisuals.Brush(e.GetBoolean("option.ghost") ? color : "#ffffff"), Background = HmiVisuals.Brush(e.GetBoolean("option.ghost") ? "transparent" : color), BorderBrush = HmiVisuals.Brush(color), BorderThickness = new Thickness(e.GetBoolean("option.bordered", true) ? 1 : 0), Padding = new Thickness(5), Margin = new Thickness(2) };
    }
    private static FrameworkElement SquareButton(HmiSurface host, HmiElement e)
    {
        var icons = e.GetNode("option.iconGroup") as JsonArray; var selected = Math.Clamp((int)e.GetNumber("option.defaultIconIndex"), 0, Math.Max(0, (icons?.Count ?? 1) - 1));
        var icon = icons?.ElementAtOrDefault(selected) as JsonObject;
        var button = StyledButton(HmiVisuals.Text(icon?["data"] ?? JsonValue.Create(e.Title)), e);
        button.Background = HmiVisuals.Brush(e.GetString("option.o_color", "#0a7ae2")); button.Foreground = Brushes.White; button.BorderBrush = HmiVisuals.Brush(e.GetString("option.i_color", "#119bfa")); button.BorderThickness = new Thickness(2);
        async Task Fire(string trigger)
        {
            foreach (var action in e.Actions.Where(x => x.IconIndex == selected && (x.Trigger == trigger || trigger == "click" && string.IsNullOrEmpty(x.Trigger)))) await host.Act(action.Event, e, action.Target);
        }
        button.Click += async (_, _) => await Fire("click"); button.MouseDoubleClick += async (_, _) => await Fire("dblclick"); button.MouseEnter += async (_, _) => await Fire("mouseenter"); button.MouseLeave += async (_, _) => await Fire("mouseleave");
        button.PreviewMouseLeftButtonDown += async (_, _) => await Fire("mousedown"); button.PreviewMouseLeftButtonUp += async (_, _) => await Fire("mouseup");
        return button;
    }

    private static FrameworkElement Chart(HmiSurface host, HmiElement e, IHmiRuntimeContext runtime, List<Action> refresh)
    {
        var chart = new HmiChart(e); var refs = HmiVisuals.References(e); var tables = HmiVisuals.Strings(e.GetNode("option.sourceTable")).Where(x => !string.IsNullOrEmpty(x)).ToArray(); var table = tables.FirstOrDefault(); string last = null;
        refresh.Add(() =>
        {
            var rows = HmiVisuals.Rows(e.Option?["dataset"]); var dims = HmiVisuals.Strings(e.GetNode("option.dataset.dimensions"));
            if (!host.IsDesignMode && !string.IsNullOrEmpty(table))
            {
                rows = tables.SelectMany(name => HmiVisuals.Rows(runtime?.QueryTable(name))).ToList();
                if (rows.Count > 0 && e.Type.StartsWith("StatisticsBar", StringComparison.Ordinal))
                {
                    var total = Math.Clamp((int)e.GetNumber("option.sourceData.totalNum", 12), 1, 1000);
                    var times = rows.Select(row => row.FirstOrDefault(x => x.Key.Equals("InsertTime", StringComparison.OrdinalIgnoreCase)).Value).Select(value => DateTime.TryParse(HmiVisuals.Text(value), out var date) ? (DateTime?)date : null).Where(x => x.HasValue).Select(x => x.Value).ToArray();
                    if (times.Length > 0)
                    {
                        // Original StatisticsBarCommon.configPage.vue labels this setting 时间(h)
                        // and offers exactly the strings 0.5, 1 and 2.
                        var unit = e.GetNumber("option.sourceData.unit", 1);
                        var bucketTicks = TimeSpan.FromHours(unit is .5 or 1 or 2 ? unit : 1).Ticks;
                        string Bucket(DateTime time) => new DateTime(time.Ticks / bucketTicks * bucketTicks, time.Kind).ToString("yyyy-MM-dd HH:mm");
                        rows = times.GroupBy(Bucket).OrderBy(x => x.Key).TakeLast(total).Select(x => new Dictionary<string, object> { ["时间"] = x.Key, ["产量"] = x.Count() }).ToList(); dims = ["时间", "产量"];
                    }
                    else { dims = ["数据表", "产量"]; rows = [new() { ["数据表"] = table, ["产量"] = rows.Count }]; }
                }
                else if (rows.Count > 0)
                {
                    var fields = HmiVisuals.Strings(e.GetNode("option.sourceData.sourceLine")).Where(x => !string.IsNullOrEmpty(x) && rows.Any(r => r.ContainsKey(x))).ToArray();
                    var timeField = rows[0].Keys.FirstOrDefault(x => x.Equals("InsertTime", StringComparison.OrdinalIgnoreCase)) ?? rows[0].Keys.First();
                    var numeric = fields.Length > 0 ? fields : rows[0].Keys.Where(x => x != timeField && rows.Any(r => double.IsFinite(HmiVisuals.Number(r.GetValueOrDefault(x), double.NaN)))).Take(12).ToArray();
                    dims = new[] { timeField }.Concat(numeric).ToArray();
                }
            }
            var labels = new List<string>(); var names = new List<string>(); var series = new List<double[]>();
            if (!host.IsDesignMode && e.Type == "OneD")
            {
                var mat = runtime?.Image((int)e.GetNumber("option.sceneIM"));
                if (mat != null && !mat.Empty())
                {
                    using var numeric = new OpenCvSharp.Mat(); mat.ConvertTo(numeric, OpenCvSharp.MatType.CV_64F);
                    var count = Math.Min(2000, numeric.Cols); var data = new double[count];
                    for (int i = 0; i < count; i++) data[i] = numeric.At<double>(numeric.Rows / 2, (int)((long)i * numeric.Cols / count));
                    labels.AddRange(Enumerable.Range(0, count).Select(x => x.ToString())); names.Add("IM" + (int)e.GetNumber("option.sceneIM")); series.Add(data);
                }
            }
            else if (!host.IsDesignMode && refs.Any(x => !string.IsNullOrEmpty(x)))
            {
                var values = refs.Select(x => string.IsNullOrEmpty(x) ? null : HmiVisuals.Unwrap(runtime?.Read(x))).ToArray();
                if (values.Any(x => x != null))
                {
                    if (e.Type == "BarStatistical")
                    {
                        var result = (values.FirstOrDefault() as IEnumerable)?.Cast<object>().ToArray() ?? [];
                        labels.AddRange(Enumerable.Range(1, result.Length).Select(x => x.ToString())); names.AddRange(["OK", "NG"]);
                        series.Add(result.Select(x => HmiVisuals.Boolean(x) ? 1d : 0).ToArray()); series.Add(result.Select(x => HmiVisuals.Boolean(x) ? 0d : 1).ToArray());
                    }
                    else if (e.Type is "PieCommon" or "PieCircle" or "CapsuleChart")
                    {
                        labels.AddRange(rows.Select((row, i) => HmiVisuals.Text(row.GetValueOrDefault(dims.FirstOrDefault() ?? "name"))));
                        if (labels.Count != values.Length) { labels.Clear(); labels.AddRange(refs.Select(x => x.Contains('&') ? x[(x.IndexOf('&') + 1)..] : x)); }
                        names.Add("值"); series.Add(values.Select(x => x == null ? double.NaN : HmiVisuals.Number(x)).ToArray());
                    }
                    else
                    {
                        for (int i = 0; i < values.Length; i++)
                        {
                            if (string.IsNullOrEmpty(refs[i])) continue;
                            names.Add(dims.ElementAtOrDefault(i + 1) ?? refs[i].Split('&').Last());
                            series.Add(values[i] is IEnumerable a && values[i] is not string ? a.Cast<object>().Select(x => HmiVisuals.Number(x, double.NaN)).ToArray() : [HmiVisuals.Number(values[i], double.NaN)]);
                        }
                        labels.AddRange(Enumerable.Range(1, series.Count == 0 ? 0 : series.Max(x => x.Length)).Select(x => x.ToString()));
                    }
                }
            }
            else if (e.Type == "PieCircle") { labels.Add("值"); names.Add("值"); series.Add([HmiVisuals.Number(e.Option?["dataset"])]); }
            else
            {
                if (dims.Length < 2 && rows.Count > 0) dims = rows[0].Keys.ToArray();
                if (dims.Length >= 2)
                {
                    labels.AddRange(rows.Select(x => HmiVisuals.Text(x.GetValueOrDefault(dims[0]))));
                    foreach (var dim in dims.Skip(1).Where(x => rows.Any(r => double.IsFinite(HmiVisuals.Number(r.GetValueOrDefault(x), double.NaN))))) { names.Add(dim); series.Add(rows.Select(x => HmiVisuals.Number(x.GetValueOrDefault(dim), double.NaN)).ToArray()); }
                    if (e.Type == "PieCommon") { names = [dims[1]]; series = [rows.Select(x => HmiVisuals.Number(x.GetValueOrDefault(dims[1]))).ToArray()]; }
                }
            }
            var key = string.Join("|", labels) + string.Join("|", series.Select(x => string.Join(",", x.Select(v => v.ToString("R")))));
            if (key != last) { chart.SetData(labels, names, series); last = key; }
            chart.ToolTip = series.Count == 0 && !host.IsDesignMode ? "绑定暂无数据" : null;
        });
        return chart;
    }

    private static FrameworkElement Table(HmiSurface host, HmiElement e, IHmiRuntimeContext runtime, List<Action> refresh)
    {
        var grid = new DockPanel();
        var table = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, IsReadOnly = e.Type != "TablesFormula" || host.IsDesignMode, Background = HmiVisuals.Brush(e.GetString("option.trBackgroundColor", "#202024")), Foreground = HmiVisuals.Brush(e.GetString("option.fontColor", "#ffffff")), BorderThickness = new Thickness(0), GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = HmiVisuals.Brush("#44444a"), RowBackground = HmiVisuals.Brush(e.GetString("option.oddRowBGC", e.GetString("option.trBackgroundColor", "#202024"))), AlternatingRowBackground = HmiVisuals.Brush(e.GetString("option.evenRowBGC", e.GetString("option.trBackgroundColor", "#29292d"))), FontSize = e.GetNumber("option.fontSize", 16), RowHeight = Math.Max(20, e.GetNumber("option.cellHeight", 30)), HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single };
        var headerStyle = new Style(typeof(DataGridColumnHeader)); headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, HmiVisuals.Brush(e.GetString("option.headerBGC", e.GetString("option.thBackgroundColor", "#3e4040"))))); headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White)); headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6))); table.ColumnHeaderStyle = headerStyle;
        var rowStyle = new Style(typeof(DataGridCell)); rowStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0))); rowStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4))); table.CellStyle = rowStyle;
        var references = HmiVisuals.References(e); var definitions = e.GetNode("option.dataset.columns") as JsonArray;
        DateTime? ReadDate(string path) { var epoch = e.GetNumber(path); try { return epoch > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)epoch).LocalDateTime : null; } catch { return null; } }
        DateTime? from = e.Type == "TablesSearch" ? ReadDate("option.condition.startDate") : null, to = e.Type == "TablesSearch" ? ReadDate("option.condition.endDate") : null; string source = e.GetString("option.condition.tableSource"); var resultFilter = e.GetString("option.condition.OkNGStatus", "All"); var page = 0; var pageSize = Math.Max(1, (int)e.GetNumber("option.pageSize", 20)); bool edited = false; string last = null;
        var searchStatus = new TextBlock { Foreground = Brushes.LightGray, Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
        if (e.Type == "TablesSearch")
        {
            var bar = new WrapPanel(); var sources = new ComboBox { MinWidth = 120, Margin = new Thickness(3) };
            try { foreach (var row in runtime?.QueryTable("") ?? []) sources.Items.Add(HmiVisuals.Text(row.GetValueOrDefault("name"))); } catch { }
            if (!string.IsNullOrEmpty(source)) { if (!sources.Items.Contains(source)) sources.Items.Add(source); sources.SelectedItem = source; }
            else if (sources.Items.Count > 0) sources.SelectedIndex = 0;
            var start = new DatePicker { Width = 120, Margin = new Thickness(3), SelectedDate = from }; var end = new DatePicker { Width = 120, Margin = new Thickness(3), SelectedDate = to };
            var statusFilter = new ComboBox { Width = 75, Margin = new Thickness(3), ItemsSource = new[] { "All", "OK", "NG" }, SelectedItem = resultFilter };
            bar.Children.Add(sources); bar.Children.Add(start); bar.Children.Add(end);
            bar.Children.Add(statusFilter);
            bar.Children.Add(HmiWindow.Button("查询", (_, _) => { source = sources.SelectedItem as string ?? ""; from = start.SelectedDate; to = end.SelectedDate?.AddDays(1).AddTicks(-1); resultFilter = statusFilter.SelectedItem as string ?? "All"; page = 0; last = null; host.Refresh(); }));
            DockPanel.SetDock(bar, Dock.Top); grid.Children.Add(bar);
        }
        if (e.Type is "TablesBasic" or "TablesSearch" or "TablesFormula")
        {
            var footer = new StackPanel { Orientation = Orientation.Horizontal };
            footer.Children.Add(HmiWindow.Button("上一页", (_, _) => { page = Math.Max(0, page - 1); last = null; host.Refresh(); }));
            footer.Children.Add(HmiWindow.Button("下一页", (_, _) => { page++; last = null; host.Refresh(); })); footer.Children.Add(searchStatus);
            if (e.Type == "TablesFormula") footer.Children.Add(HmiWindow.Button("保存配方", async (_, _) =>
            {
                if (!table.CommitEdit(DataGridEditingUnit.Cell, true) || !table.CommitEdit(DataGridEditingUnit.Row, true)) { host.Report("配方含无效值，请修正后保存"); return; }
                if (table.ItemsSource is DataView view)
                {
                    var rows = new JsonArray(); foreach (DataRow row in view.Table.Rows) { var json = new JsonObject(); foreach (DataColumn col in view.Table.Columns) json[col.ColumnName] = JsonSerializer.SerializeToNode(row[col] == DBNull.Value ? null : row[col]); rows.Add(json); }
                    // Recipe tables are intentionally not paged so saving cannot discard rows on other pages.
                    var payload = new HmiElement((JsonObject)e.Raw.DeepClone()); payload.SetNode("option.dataset.data", rows); await host.Act("saveRecipe", payload); edited = false;
                }
            }));
            DockPanel.SetDock(footer, Dock.Bottom); grid.Children.Add(footer);
        }
        grid.Children.Add(table); table.CellEditEnding += (_, _) => edited = true;
        refresh.Add(() =>
        {
            if (edited || table.IsKeyboardFocusWithin) return;
            List<Dictionary<string, object>> rows;
            if (!host.IsDesignMode && e.Type == "TablesFormula") rows = HmiVisuals.Rows(runtime?.Read("$recipe"));
            else if (!host.IsDesignMode && e.Type == "TablesSearch") rows = string.IsNullOrEmpty(source) ? [] : HmiVisuals.Rows(runtime?.QueryTable(source, from, to));
            else if (!host.IsDesignMode && references.Any(x => !string.IsNullOrEmpty(x)))
            {
                if (e.Type == "TablesStatistics") rows = HmiVisuals.Rows(runtime?.Read(references.First(x => !string.IsNullOrEmpty(x))));
                else if (e.Type == "TableList") rows = references.Select((r, i) => new Dictionary<string, object> { ["name"] = HmiVisuals.Text((e.GetNode("option.dataset") as JsonArray)?.ElementAtOrDefault(i)?["name"]), ["value"] = runtime?.Read(r) }).Where(x => x["value"] != null).ToList();
                else
                {
                    var row = new Dictionary<string, object>(); for (int i = 0; i < references.Length; i++) { var key = HmiVisuals.Text((definitions?.ElementAtOrDefault(i) as JsonObject)?["key"]); if (string.IsNullOrEmpty(key)) key = "data" + i; row[key] = string.IsNullOrEmpty(references[i]) ? null : runtime?.Read(references[i]); } rows = row.Values.Any(x => x != null) ? [row] : [];
                }
            }
            else if (!host.IsDesignMode && e.Type == "TableScrollBoard") rows = HmiVisuals.Rows(runtime?.Read("$logs")).Select(x => new Dictionary<string, object> { ["c0"] = x.GetValueOrDefault("Time"), ["c1"] = x.GetValueOrDefault("Message"), ["c2"] = x.GetValueOrDefault("Level") }).ToList();
            else rows = HmiVisuals.Rows(e.Option?["dataset"]);
            if (e.Type == "TablesSearch" && resultFilter is "OK" or "NG") rows = rows.Where(row => { var result = row.FirstOrDefault(x => x.Key.Equals("OKNG", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("result", StringComparison.OrdinalIgnoreCase)).Value; var value = HmiVisuals.Text(result); return resultFilter == "OK" ? value.Equals("OK", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" : value.Equals("NG", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0"; }).ToList();
            if (e.Type == "TableList" && e.GetBoolean("option.sort", true)) rows = rows.OrderByDescending(x => HmiVisuals.Number(x.GetValueOrDefault("value"))).ToList();
            var count = rows.Count; var pages = Math.Max(1, (int)Math.Ceiling(count / (double)pageSize)); page = Math.Clamp(page, 0, pages - 1);
            searchStatus.Text = $"{page + 1}/{pages}  共 {count} 条";
            if (e.Type is "TablesBasic" or "TablesSearch") rows = rows.Skip(page * pageSize).Take(pageSize).ToList();
            var keyText = JsonSerializer.Serialize(rows); if (keyText == last) return; last = keyText;
            table.Columns.Clear(); var data = new DataTable();
            var columns = new List<(string Key, string Title, double Width)>();
            if (definitions != null) foreach (var def in definitions.OfType<JsonObject>()) columns.Add((HmiVisuals.Text(def["key"]), HmiVisuals.Text(def["title"]), HmiVisuals.Number(def["width"], 130)));
            else if (e.Type == "TableScrollBoard") { var headers = HmiVisuals.Strings(e.Option?["header"]); columns.AddRange(headers.Select((x, i) => ("c" + i, x, e.Width / Math.Max(1, headers.Length)))); }
            else if (rows.Count > 0) columns.AddRange(rows[0].Keys.Where(x => x != "key").Select(x => (x, x, 140d)));
            if (e.Type == "TablesStatistics" && !host.IsDesignMode)
            {
                var selected = HmiVisuals.Strings(e.Option?["selectDataParameter"]);
                for (int i = 0; i < Math.Min(selected.Length, columns.Count); i++) if (!string.IsNullOrEmpty(selected[i])) columns[i] = (selected[i], columns[i].Title, columns[i].Width);
            }
            foreach (var column in columns.DistinctBy(x => x.Key))
            {
                if (string.IsNullOrEmpty(column.Key)) continue;
                var sample = rows.Select(x => x.GetValueOrDefault(column.Key)).FirstOrDefault(x => x != null);
                var columnType = e.Type == "TablesFormula" && sample is double or float or decimal ? typeof(double) : e.Type == "TablesFormula" && sample is int or long ? typeof(long) : typeof(object);
                data.Columns.Add(column.Key, columnType); table.Columns.Add(new DataGridTextColumn { Header = column.Title, Binding = new Binding("[" + column.Key + "]") { Mode = table.IsReadOnly ? BindingMode.OneWay : BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, ValidatesOnExceptions = true }, Width = Math.Max(40, column.Width) });
            }
            foreach (var row in rows) { var output = data.NewRow(); foreach (DataColumn col in data.Columns) output[col] = row.TryGetValue(col.ColumnName, out var value) && value != null ? value is string or bool or int or long or double or float ? value : HmiVisuals.Text(value) : DBNull.Value; data.Rows.Add(output); }
            table.ItemsSource = data.DefaultView; table.ToolTip = count == 0 && !host.IsDesignMode ? "暂无运行数据" : null;
        });
        return grid;
    }

    private static FrameworkElement Image(HmiSurface host, HmiElement e, HmiDocument document, IHmiRuntimeContext runtime, List<Action> refresh, List<Action> dispose)
    {
        if (e.Type == "Image" || e.ComponentType == "Image")
        {
            var image = new System.Windows.Controls.Image { Stretch = e.GetString("option.fit") switch { "cover" => Stretch.UniformToFill, "fill" => Stretch.Fill, "none" => Stretch.None, _ => Stretch.Uniform } };
            try { image.Source = HmiVisuals.Bitmap(e.GetString("option.dataset"), document.SourcePath); } catch (Exception ex) { host.Errors.Add("图片：" + ex.Message); }
            var content = new Grid(); content.Children.Add(image); var remoteState = new TextBlock { Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; content.Children.Add(remoteState);
            var border = new Border { Child = content, CornerRadius = new CornerRadius(e.GetNumber("option.borderRadius")), ClipToBounds = true };
            var cancellation = new CancellationTokenSource(); dispose.Add(() => { cancellation.Cancel(); cancellation.Dispose(); }); var requested = false;
            async Task DownloadStaticImage()
            {
                requested = true; remoteState.Text = "图片加载中…";
                try { var bitmap = await HmiVisuals.RemoteBitmapAsync(e.GetString("option.dataset"), cancellation.Token); if (!cancellation.IsCancellationRequested) { image.Source = bitmap; remoteState.Text = ""; } }
                catch (OperationCanceledException) { if (!cancellation.IsCancellationRequested) remoteState.Text = "远程图片加载超时"; }
                catch (Exception ex) { if (!cancellation.IsCancellationRequested) { remoteState.Text = "远程图片加载失败"; remoteState.ToolTip = ex.Message; host.Report(ex.Message); } }
            }
            refresh.Add(() => { var source = e.GetString("option.dataset"); if (!requested && !host.IsDesignMode && host.IsLoaded && Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") _ = DownloadStaticImage(); });
            var menu = new ContextMenu();
            if (!host.IsDesignMode) { var load = new MenuItem { Header = "??????? IM" + (int)e.GetNumber("option.sceneIM") }; load.Click += async (_, _) => await host.Act("importImage", e); menu.Items.Add(load); }
            if (menu.Items.Count > 0) border.ContextMenu = menu; return border;
        }
        var panel = new DockPanel(); var view = new ImageView(); var caption = new TextBlock { Foreground = Brushes.LightGray, TextAlignment = TextAlignment.Center }; var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var images = e.Option?["dataset"] as JsonArray; int index = 0, lastIndex = -1; object lastFrame = null; var nextTime = DateTime.UtcNow.AddMilliseconds(Math.Max(100, e.GetNumber("option.interval", 5000)));
        var cancellationSource = new CancellationTokenSource(); dispose.Add(() => { cancellationSource.Cancel(); cancellationSource.Dispose(); });
        var loadedRemote = new Dictionary<int, System.Windows.Media.Imaging.BitmapSource>(); var loadingRemote = new HashSet<int>(); var failedRemote = new Dictionary<int, string>();
        controls.Children.Add(HmiWindow.Button("‹", (_, _) => { index = (index - 1 + Math.Max(1, images?.Count ?? 1)) % Math.Max(1, images?.Count ?? 1); host.Refresh(); })); controls.Children.Add(caption); controls.Children.Add(HmiWindow.Button("›", (_, _) => { index = (index + 1) % Math.Max(1, images?.Count ?? 1); host.Refresh(); }));
        controls.Children.Add(HmiWindow.Button("重试", (_, _) => { failedRemote.Clear(); lastIndex = -1; host.Refresh(); }));
        DockPanel.SetDock(controls, Dock.Bottom); panel.Children.Add(controls); panel.Children.Add(view);
        void Display(FrameworkElement child) { panel.Children.RemoveAt(panel.Children.Count - 1); panel.Children.Add(child); }
        async Task Download(int position, string source)
        {
            loadingRemote.Add(position);
            try { var bitmap = await HmiVisuals.RemoteBitmapAsync(source, cancellationSource.Token); if (!cancellationSource.IsCancellationRequested) { loadedRemote[position] = bitmap; if (index == position) { lastIndex = -1; host.Refresh(); } } }
            catch (OperationCanceledException) { if (!cancellationSource.IsCancellationRequested) { failedRemote[position] = "远程图片加载超时"; lastIndex = -1; host.Refresh(); } }
            catch (Exception ex) { if (!cancellationSource.IsCancellationRequested) { failedRemote[position] = ex.Message; lastIndex = -1; host.Refresh(); } }
            finally { loadingRemote.Remove(position); }
        }
        refresh.Add(() =>
        {
            if (images == null || images.Count == 0) return;
            if (!host.IsDesignMode && e.GetBoolean("option.autoplay") && DateTime.UtcNow >= nextTime) { index = (index + 1) % images.Count; nextTime = DateTime.UtcNow.AddMilliseconds(Math.Max(100, e.GetNumber("option.interval", 5000))); }
            var item = images[index] as JsonObject; caption.Text = $"  {index + 1}/{images.Count}  ";
            if (item == null) return;
            if (!host.IsDesignMode && HmiVisuals.Boolean(item["bind"])) { var mat = runtime?.Image((int)HmiVisuals.Number(item["bindIndex"])); if (lastIndex != index || !ReferenceEquals(lastFrame, mat)) { view.ShowMat(mat); lastFrame = mat; } }
            else
            {
                var source = HmiVisuals.Text(item["url"]); var isRemote = Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
                if (isRemote && !host.IsDesignMode && host.IsLoaded && !loadedRemote.ContainsKey(index) && !failedRemote.ContainsKey(index) && !loadingRemote.Contains(index)) _ = Download(index, source);
                if (lastIndex != index)
                {
                    var bitmap = loadedRemote.GetValueOrDefault(index) ?? HmiVisuals.Bitmap(source, document.SourcePath);
                    if (bitmap != null) Display(new System.Windows.Controls.Image { Source = bitmap, Stretch = Stretch.Uniform });
                    else Display(new TextBlock { Text = failedRemote.ContainsKey(index) ? "远程图片加载失败 · 点击重试" : isRemote ? host.IsDesignMode ? "远程图片 · 运行时加载" : "远程图片加载中…" : "图片不存在", ToolTip = failedRemote.GetValueOrDefault(index) ?? source, Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                }
            }
            if (!host.IsDesignMode && HmiVisuals.Boolean(item["bind"]) && !panel.Children.Contains(view)) { panel.Children.RemoveAt(panel.Children.Count - 1); panel.Children.Add(view); }
            lastIndex = index;
        });
        return panel;
    }
    private static FrameworkElement Statistics(HmiSurface host, HmiElement e, IHmiRuntimeContext runtime, List<Action> refresh)
    {
        var panel = new DockPanel(); var text = new TextBlock { FontSize = e.GetNumber("option.headerText.fontSize", 20), Foreground = Brushes.White, TextAlignment = TextAlignment.Center, Margin = new Thickness(10) }; DockPanel.SetDock(text, Dock.Bottom); panel.Children.Add(text);
        var chart = new HmiChart(e); panel.Children.Add(chart); var sources = HmiVisuals.Strings(e.Option?["sourceTable"]).Where(x => !string.IsNullOrEmpty(x)).ToArray();
        DateTime? from = null, to = null;
        if (e.Type == "NGStatisticsSearch")
        {
            var bar = new WrapPanel(); var start = new DatePicker { Width = 130, Margin = new Thickness(2) }; var end = new DatePicker { Width = 130, Margin = new Thickness(2) }; bar.Children.Add(start); bar.Children.Add(end); bar.Children.Add(HmiWindow.Button("查询", (_, _) => { from = start.SelectedDate; to = end.SelectedDate?.AddDays(1).AddTicks(-1); host.Refresh(); })); DockPanel.SetDock(bar, Dock.Top); panel.Children.Insert(0, bar);
        }
        refresh.Add(() =>
        {
            double ok = 0, ng = 0, unknown = 0;
            if (host.IsDesignMode) { ok = e.GetNumber("option.dataset.okCount"); ng = e.GetNumber("option.dataset.ngCount"); }
            else if (sources.Length > 0)
                foreach (var row in sources.SelectMany(source => runtime?.QueryTable(source, from, to) ?? []))
                {
                    var result = row.FirstOrDefault(x => x.Key.Equals("result", StringComparison.OrdinalIgnoreCase) || x.Key.Equals("OKNG", StringComparison.OrdinalIgnoreCase));
                    var value = HmiVisuals.Text(result.Value).Trim();
                    if (value.Equals("OK", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1") ok++;
                    else if (value.Equals("NG", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0") ng++;
                    else unknown++;
                }
            var pass = ok + ng > 0 ? ok / (ok + ng) * 100 : 0;
            text.Text = $"总数  {ok + ng + unknown:0}     OK  {ok:0}     NG  {ng:0}     良率  {(ok + ng > 0 ? pass.ToString("0.##") + "%" : "—")}" + (unknown > 0 ? $"     未判定  {unknown:0}" : "");
            text.Foreground = HmiVisuals.Brush(e.GetString(pass >= e.GetNumber("option.threshold", 95) ? "option.afterThresholdColor" : "option.beforeThresholdColor", "#ffffff"));
            chart.SetData(["OK", "NG"], ["数量"], [new[] { ok, ng }]);
        });
        return panel;
    }
    private static FrameworkElement SystemInformation(HmiElement e, IHmiRuntimeContext runtime, List<Action> refresh)
    {
        var panel = new StackPanel { Orientation = e.GetString("option.arrangeType") == "column" ? Orientation.Vertical : Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var fields = new Dictionary<string, TextBlock>();
        foreach (var key in new[] { "CPUConfig", "memoryConfig", "diskConfig", "enginConfig" }) if (e.GetBoolean("option." + key + ".show", true)) { var label = new TextBlock { FontSize = e.FontSize, Foreground = HmiVisuals.Brush(e.Foreground), Margin = new Thickness(12) }; fields[key] = label; panel.Children.Add(label); }
        var proc = Process.GetCurrentProcess(); var previousTime = DateTime.UtcNow; var previousCpu = proc.TotalProcessorTime;
        refresh.Add(() =>
        {
            var now = DateTime.UtcNow; var cpuTime = proc.TotalProcessorTime; var cpu = (cpuTime - previousCpu).TotalMilliseconds / Math.Max(1, (now - previousTime).TotalMilliseconds) / Environment.ProcessorCount * 100; previousTime = now; previousCpu = cpuTime;
            foreach (var (key, label) in fields)
            {
                var prefix = e.GetString("option." + key + ".prefix");
                switch (key)
                {
                    case "CPUConfig": label.Text = $"{prefix}  {cpu:0.0}%"; label.ToolTip = "当前程序 CPU 占用"; break;
                    case "memoryConfig": label.Text = $"{prefix}  {proc.WorkingSet64 / 1048576d:0.0} MB"; label.ToolTip = "当前程序工作集"; break;
                    case "diskConfig": var driveName = e.GetString("option.diskConfig.name"); var drive = DriveInfo.GetDrives().FirstOrDefault(x => x.IsReady && driveName.Contains(x.Name[..1], StringComparison.OrdinalIgnoreCase)) ?? DriveInfo.GetDrives().FirstOrDefault(x => x.IsReady); label.Text = $"{prefix}  {(drive?.AvailableFreeSpace ?? 0) / 1073741824d:0.0} GB"; break;
                    default: label.Text = prefix + "  " + HmiVisuals.Text(runtime?.Read("$runtime")) + " ms"; break;
                }
            }
        });
        return panel;
    }
}
