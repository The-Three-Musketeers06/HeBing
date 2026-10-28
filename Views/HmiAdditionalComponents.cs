using System.Collections;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using AiVisionStudio.Models;

namespace AiVisionStudio.Views;

/// <summary>Native components recovered from component/*.bin config and indexPage.</summary>
internal static class HmiAdditionalComponents
{
    public static FrameworkElement TryCreate(HmiSurface host, HmiElement element, HmiDocument document, IHmiRuntimeContext runtime,
        List<Action> refresh, List<Action> dispose, List<Func<bool>> commit)
    {
        return element.Type switch
        {
            "TextStateAssociation" => StateText(host, element, runtime, refresh),
            "InputsInput" => Input(host, element, runtime, refresh),
            "InputsDate" => DateInput(host, element),
            "Number" => Number(host, element, runtime, refresh, dispose),
            "FlipperNumber" or "CountDown" => Flipper(host, element, runtime, refresh, dispose),
            "Clock" => AnimatedVector(new ClockVisual(element), refresh, dispose, 100),
            "PipelineH" or "PipelineV" => AnimatedVector(new PipelineVisual(element), refresh, dispose, 80),
            "Title02" => new TitleVisual(element),
            "TablesDatabase" => DatabaseTable(host, element, runtime, refresh),
            "StudioIO" => IoIndicators(host, element, runtime, refresh),
            "ProgramButton" or "StudioToolButton" => ActionButton(host, element, runtime, refresh),
            _ => null
        };
    }

    private static FrameworkElement StateText(HmiSurface host, HmiElement e, IHmiRuntimeContext runtime, List<Action> refresh)
    {
        var text = new HmiSpacedLabel { Foreground = HmiVisuals.Brush(e.Foreground), FontSize = e.FontSize,
            Bold = e.GetString("option.fontWeight") == "bold", Spacing = e.GetNumber("option.letterSpacing"),
            Alignment = e.GetString("option.textAlign", "center"), WritingMode = e.GetString("option.writingMode", "horizontal-tb") };
        var border = new Border { Child = text, Padding = new Thickness(e.GetNumber("option.paddingX", 10), e.GetNumber("option.paddingY", 10), e.GetNumber("option.paddingX", 10), e.GetNumber("option.paddingY", 10)),
            BorderBrush = HmiVisuals.Brush(e.GetString("option.borderColor", "#ffffff")), BorderThickness = new Thickness(e.GetNumber("option.borderWidth")), CornerRadius = new CornerRadius(e.GetNumber("option.borderRadius", 5)) };
        refresh.Add(() =>
        {
            var value = HmiVisuals.Unwrap(HmiVisuals.Value(e, runtime, host.IsDesignMode));
            var key = value is bool boolean ? boolean ? "true" : "false" : value == null ? "undefined" : Convert.ToString(value, CultureInfo.InvariantCulture);
            var association = (e.Option["associationList"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(item => item["value"]?.ToString() == key);
            text.Text = association?["text"]?.ToString() ?? e.GetString("option.defaultText", "noData");
            text.InvalidateVisual();
            border.Background = HmiVisuals.Brush(association?["backgroundColor"]?.ToString() ?? e.GetString("option.defaultBackgroundColor", "#ff0000"));
        });
        return border;
    }

    private static FrameworkElement Input(HmiSurface host, HmiElement e, IHmiRuntimeContext runtime, List<Action> refresh)
    {
        var localValue = e.GetString("option.dataset");
        var input = new TextBox { Text = e.GetString("option.dataset"), FontSize = e.FontSize, Foreground = HmiVisuals.Brush(e.Foreground),
            FontWeight = e.GetString("option.fontWeight") == "bold" ? FontWeights.Bold : FontWeights.Normal, Background = HmiVisuals.Brush("#343438"),
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 2, 6, 2), IsReadOnly = host.IsDesignMode };
        // The original InputsInput onChange is local; InputsParams is the separate
        // control that writes project globals. Do not silently change that contract.
        input.LostKeyboardFocus += (_, _) => { if (!host.IsDesignMode) localValue = input.Text; };
        input.KeyDown += (_, args) => { if (args.Key == Key.Enter && !host.IsDesignMode) localValue = input.Text; };
        refresh.Add(() =>
        {
            if (input.IsKeyboardFocusWithin) return;
            var value = HmiVisuals.References(e).Any(reference => reference.Length > 0)
                ? HmiVisuals.Value(e, runtime, host.IsDesignMode) : localValue;
            input.Text = HmiVisuals.Text(value);
            input.ToolTip = value == null ? "绑定暂无数据" : null;
        });
        return input;
    }

    private static FrameworkElement DateInput(HmiSurface host, HmiElement e)
    {
        var type = e.GetString("option.componentInteractEventKey", "date");
        var range = type.EndsWith("range", StringComparison.Ordinal);
        var dates = new DateTime?[range ? 2 : 1];
        for (var index = 0; index < dates.Length; index++)
        {
            var node = e.GetNode(range ? $"option.dataset[{index}]" : "option.dataset");
            var millis = HmiVisuals.Number(node, double.NaN);
            try { dates[index] = double.IsFinite(millis) ? DateTimeOffset.FromUnixTimeMilliseconds((long)millis).LocalDateTime : null; } catch (ArgumentOutOfRangeException) { }
            if (e.GetString("option.defaultType") == "dynamic")
            {
                var amount = e.GetNumber($"option.differValue[{index}]");
                var now = DateTime.Now;
                dates[index] = e.GetString($"option.differUnit[{index}]", "d") switch
                {
                    "y" => now.AddYears((int)amount), "Q" => now.AddMonths((int)amount * 3), "M" => now.AddMonths((int)amount),
                    "w" => now.AddDays(amount * 7), "h" => now.AddHours(amount), "m" => now.AddMinutes(amount),
                    "s" => now.AddSeconds(amount), "ms" => now.AddMilliseconds(amount), _ => now.AddDays(amount)
                };
            }
        }
        void Commit()
        {
            if (host.IsDesignMode) return;
            if (range && dates[0] > dates[1]) { host.Report("开始时间不能晚于结束时间"); return; }
            // Runtime date selections are local state, as with native InputsDate.
            // Keep the editor document unchanged when previewing the HMI.
        }
        DateTime Normalize(DateTime date)
        {
            if (type.StartsWith("year", StringComparison.Ordinal)) return new DateTime(date.Year, 1, 1);
            if (type.StartsWith("quarter", StringComparison.Ordinal)) return new DateTime(date.Year, (date.Month - 1) / 3 * 3 + 1, 1);
            if (type.StartsWith("month", StringComparison.Ordinal)) return new DateTime(date.Year, date.Month, 1);
            return type == "datetime" ? date : date.Date;
        }
        var panel = new UniformGrid { Rows = 1, Columns = dates.Length, IsEnabled = !host.IsDesignMode };
        for (var index = 0; index < dates.Length; index++)
        {
            var slot = index;
            var column = new StackPanel();
            if (e.GetString("option.isPanel", "0") == "1")
            {
                var calendar = new System.Windows.Controls.Calendar { SelectedDate = dates[slot], DisplayDate = dates[slot] ?? DateTime.Now,
                    DisplayMode = type.StartsWith("year") ? CalendarMode.Decade : type.StartsWith("month") || type.StartsWith("quarter") ? CalendarMode.Year : CalendarMode.Month };
                calendar.SelectedDatesChanged += (_, _) => { dates[slot] = calendar.SelectedDate.HasValue ? Normalize(calendar.SelectedDate.Value) : null; Commit(); };
                column.Children.Add(calendar);
            }
            else
            {
                var picker = new DatePicker { SelectedDate = dates[slot], SelectedDateFormat = DatePickerFormat.Short, VerticalContentAlignment = VerticalAlignment.Center };
                picker.SelectedDateChanged += (_, _) => { dates[slot] = picker.SelectedDate.HasValue ? Normalize(picker.SelectedDate.Value + (dates[slot]?.TimeOfDay ?? TimeSpan.Zero)) : null; Commit(); };
                picker.DateValidationError += (_, args) => { args.ThrowException = false; host.Report("日期格式无效"); };
                column.Children.Add(picker);
            }
            if (type == "datetime")
            {
                var time = new TextBox { Text = dates[slot]?.ToString("HH:mm:ss") ?? "00:00:00", ToolTip = "时:分:秒" };
                time.LostKeyboardFocus += (_, _) => { if (TimeSpan.TryParse(time.Text, CultureInfo.InvariantCulture, out var value) && value >= TimeSpan.Zero && value < TimeSpan.FromDays(1)) { dates[slot] = (dates[slot] ?? DateTime.Now).Date + value; Commit(); } else host.Report("时间格式无效"); };
                column.Children.Add(time);
            }
            panel.Children.Add(column);
        }
        return panel;
    }

    private static FrameworkElement Number(HmiSurface host, HmiElement e, IHmiRuntimeContext runtime, List<Action> refresh, List<Action> dispose)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        TextBlock Text(string value, string color) => new() { Text = value, Foreground = HmiVisuals.Brush(color), FontSize = e.GetNumber("option.numberSize", 34), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(Text(e.GetString("option.prefixText"), e.GetString("option.prefixColor", "#4a9ef8")));
        var number = Text("", e.GetString("option.numberColor", "#4a9ef8")); panel.Children.Add(number);
        panel.Children.Add(Text(e.GetString("option.suffixText"), e.GetString("option.suffixColor", "#4a9ef8")));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        var watch = new Stopwatch(); var from = e.GetNumber("option.from"); var target = double.NaN; var displayed = from;
        var duration = Math.Max(0, e.GetNumber("option.dur", 3));
        void Draw()
        {
            var progress = duration == 0 ? 1 : Math.Clamp(watch.Elapsed.TotalSeconds / duration, 0, 1);
            displayed = from + (target - from) * progress;
            number.Text = displayed.ToString((e.GetBoolean("option.showSeparator", true) ? "N" : "F") + Math.Clamp((int)e.GetNumber("option.precision"), 0, 12), CultureInfo.InvariantCulture);
            if (progress >= 1) timer.Stop();
        }
        timer.Tick += (_, _) => Draw(); dispose.Add(timer.Stop);
        refresh.Add(() =>
        {
            var value = HmiVisuals.Number(HmiVisuals.Value(e, runtime, host.IsDesignMode), double.NaN);
            if (double.IsNaN(value)) { timer.Stop(); target = double.NaN; number.Text = "—"; return; }
            if (value == target) return;
            from = double.IsFinite(target) ? displayed : e.GetNumber("option.from"); target = value; watch.Restart(); Draw();
            if (duration > 0) timer.Start();
        });
        return panel;
    }

    private static FrameworkElement Flipper(HmiSurface host, HmiElement e, IHmiRuntimeContext runtime, List<Action> refresh, List<Action> dispose)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var countdown = e.Type == "CountDown"; var origin = DateTime.UtcNow; var previous = double.NaN; var lastText = "";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        void Render(string value)
        {
            if (lastText == value) return;
            var old = lastText; lastText = value;
            panel.Children.Clear();
            for (var index = 0; index < value.Length; index++)
            {
                var ch = value[index]; var numeric = char.IsDigit(ch) || ch == '-';
                var text = new TextBlock { Text = ch.ToString(), Foreground = HmiVisuals.Brush(e.GetString("option.flipperTextColor", "#4a9ef8")),
                    FontSize = e.GetNumber("option.flipperWidth", 30) * .9, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var card = new Border { Child = text, Width = numeric ? Math.Max(4, e.GetNumber("option.flipperWidth", 30)) : double.NaN,
                    Height = Math.Max(4, e.GetNumber("option.flipperHeight", 50)), Margin = new Thickness(e.GetNumber("option.flipperGap", 10) / 2, 0, e.GetNumber("option.flipperGap", 10) / 2, 0),
                    Background = numeric ? HmiVisuals.Brush(e.GetString("option.flipperBgColor", "#16293e")) : Brushes.Transparent,
                    BorderBrush = HmiVisuals.Brush(e.GetString("option.flipperTextColor", "#4a9ef8")), BorderThickness = new Thickness(e.GetNumber("option.flipperBorderWidth")), CornerRadius = new CornerRadius(e.GetNumber("option.flipperRadius", 5)) };
                if (numeric && index < old.Length && old[index] != ch)
                {
                    var transform = new ScaleTransform(1, .1); text.RenderTransform = transform; text.RenderTransformOrigin = new Point(.5, e.GetString("option.flipperType") == "up" ? 1 : 0);
                    transform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.1, 1, TimeSpan.FromMilliseconds(Math.Clamp(e.GetNumber("option.flipperSpeed", 450), 1, 10000))));
                }
                panel.Children.Add(card);
            }
        }
        void Update()
        {
            var value = HmiVisuals.Number(HmiVisuals.Value(e, runtime, host.IsDesignMode), double.NaN);
            if (!countdown)
            {
                var length = Math.Clamp((int)e.GetNumber("option.flipperLength", 6), 1, 80);
                var text = double.IsFinite(value) ? Math.Truncate(value).ToString("0", CultureInfo.InvariantCulture).PadLeft(length, '0') : "—";
                Render(text.Length > length ? text[^length..] : text); return;
            }
            if (value != previous && double.IsFinite(value)) { origin = DateTime.UtcNow; previous = value; }
            var seconds = e.GetBoolean("option.useEndDate") ? (e.GetNumber("option.endDate") - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000 : value - (DateTime.UtcNow - origin).TotalSeconds;
            if (!double.IsFinite(seconds)) { Render("—"); return; }
            var duration = TimeSpan.FromSeconds(Math.Clamp(Math.Ceiling(seconds), 0, 86_400_000));
            var day = e.GetBoolean("option.showDay"); var chinese = e.GetString("option.style", "时分秒") == "时分秒";
            Render((day ? duration.Days.ToString("00") + (chinese ? "天" : ":") : "") + (day ? duration.Hours : (int)duration.TotalHours).ToString("00") + (chinese ? "时" : ":") + duration.Minutes.ToString("00") + (chinese ? "分" : ":") + duration.Seconds.ToString("00") + (chinese ? "秒" : ""));
        }
        refresh.Add(Update); timer.Tick += (_, _) => Update(); if (countdown) timer.Start(); dispose.Add(timer.Stop);
        return new Viewbox { Child = panel, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
    }

    private static FrameworkElement IoIndicators(HmiSurface host, HmiElement e, IHmiRuntimeContext runtime, List<Action> refresh)
    {
        var panel = new Grid(); var lamps = new Ellipse[2, 16];
        foreach (var height in new[] { new GridLength(30), new GridLength(1, GridUnitType.Star), new GridLength(30), new GridLength(1, GridUnitType.Star), new GridLength(10) }) panel.RowDefinitions.Add(new RowDefinition { Height = height });
        for (var row = 0; row < 2; row++)
        {
            var box = new Grid();
            var section = new Border { Child = box, Margin = new Thickness(10, 0, 10, 0), Background = HmiVisuals.Brush(e.GetString($"option.IO[{row}].main.backGroundColor", "#006600")), CornerRadius = new CornerRadius(Math.Max(0, e.GetNumber($"option.IO[{row}].main.borderRadius"))) };
            var title = new TextBlock { Text = e.GetString($"option.IO[{row}].label.dataset"), Foreground = HmiVisuals.Brush(e.GetString($"option.IO[{row}].label.color", "#ffffff")), Background = HmiVisuals.Brush(e.GetString($"option.IO[{row}].label.backGroundColor", "#0101f7")), FontSize = e.GetNumber($"option.IO[{row}].label.fontSize", 20), TextAlignment = TextAlignment.Center, FontWeight = e.GetString($"option.IO[{row}].label.fontWeight") == "bold" ? FontWeights.Bold : FontWeights.Normal };
            title.Background = Brushes.Transparent;
            title.VerticalAlignment = VerticalAlignment.Center; title.TextTrimming = TextTrimming.CharacterEllipsis;
            var header = new Border { Child = title, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(50, 0, 0, 0), Background = HmiVisuals.Brush(e.GetString($"option.IO[{row}].label.backGroundColor", "#0101f7")), CornerRadius = new CornerRadius(Math.Max(0, e.GetNumber($"option.IO[{row}].label.borderRadius"))) };
            box.SizeChanged += (_, _) => { header.Width = box.ActualWidth * .3; header.Height = box.ActualHeight * .2; header.RenderTransform = new TranslateTransform(0, -box.ActualHeight * .05); };
            var grid = new UniformGrid { Columns = 16, Rows = 1, VerticalAlignment = VerticalAlignment.Bottom };
            for (var col = 0; col < 16; col++)
            {
                var cell = new StackPanel(); var size = Math.Max(2, e.GetNumber("option.round.size", 30));
                lamps[row, col] = new Ellipse { Width = size, Height = size, Stroke = Brushes.Black, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Center };
                cell.Children.Add(lamps[row, col]); cell.Children.Add(new TextBlock { Text = (15 - col).ToString(), Opacity = col is 0 or 15 ? 1 : 0, TextAlignment = TextAlignment.Center, FontSize = e.GetNumber("option.text.fontSize", 20), Foreground = HmiVisuals.Brush(e.GetString("option.text.color", "#000000")), FontWeight = e.GetString("option.text.fontWeight") == "bold" ? FontWeights.Bold : FontWeights.Normal }); grid.Children.Add(cell);
            }
            box.Children.Add(grid); box.Children.Add(header); Grid.SetRow(section, row * 2 + 1); panel.Children.Add(section);
        }
        refresh.Add(() =>
        {
            var references = HmiVisuals.References(e);
            for (var row = 0; row < 2; row++)
            {
                object source = !host.IsDesignMode && row < references.Length && references[row].Length > 0 ? runtime?.Read(references[row]) : e.GetNode($"option.dataset[{row}]");
                var values = HmiVisuals.Unwrap(source) as IEnumerable; var bits = values?.Cast<object>().ToArray();
                for (var col = 0; col < 16; col++)
                {
                    var known = bits != null && col < bits.Length;
                    lamps[row, col].Fill = !known ? HmiVisuals.Brush("#555555") : HmiVisuals.Brush(e.GetString(HmiVisuals.Boolean(bits[col]) ? "option.round.selectColor" : "option.round.defaultColor", "#ffffff"));
                    lamps[row, col].ToolTip = $"{(row == 0 ? "DI" : "DO")} {15 - col}: " + (!known ? "暂无数据" : HmiVisuals.Boolean(bits[col]) ? "1" : "0");
                }
            }
        });
        return panel;
    }

    private static FrameworkElement ActionButton(HmiSurface host, HmiElement e, IHmiRuntimeContext runtime, List<Action> refresh)
    {
        var color = e.GetString("option.type") switch { "error" => "#d03050", "warning" => "#f0a020", "success" => "#18a058", _ => "#2080f0" };
        var ghost = e.GetBoolean("option.ghost", true);
        var button = new Button { Content = e.GetString("option.data", e.Title), FontSize = e.FontSize, Foreground = HmiVisuals.Brush(ghost ? color : "#ffffff"),
            Background = ghost || e.GetBoolean("option.text") ? Brushes.Transparent : HmiVisuals.Brush(color), BorderBrush = HmiVisuals.Brush(color), BorderThickness = new Thickness(e.GetBoolean("option.bordered", true) ? 1 : 0), Padding = new Thickness(5) };
        button.Click += async (_, _) => await host.Act(e.Type == "ProgramButton" ? "runProgram" : "openOperator", e);
        refresh.Add(() => button.IsEnabled = !host.IsDesignMode && runtime?.IsRunning != true);
        return button;
    }

    private static FrameworkElement DatabaseTable(HmiSurface host, HmiElement e, IHmiRuntimeContext runtime, List<Action> refresh)
    {
        var root = new DockPanel(); var page = 0; var size = Math.Clamp((int)e.GetNumber("option.pageSize", 5), 1, 1000);
        var table = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false,
            Background = HmiVisuals.Brush(e.GetString("option.trBackgroundColor", "#141312")), Foreground = HmiVisuals.Brush(e.Foreground), FontSize = e.FontSize,
            RowHeight = Math.Max(1, e.GetNumber("option.cellHeight", 30)), HeadersVisibility = DataGridHeadersVisibility.Column, BorderThickness = new Thickness(0),
            GridLinesVisibility = e.GetBoolean("option.singleLine") && e.GetBoolean("option.singleColumn") ? DataGridGridLinesVisibility.All : e.GetBoolean("option.singleLine") ? DataGridGridLinesVisibility.Horizontal : e.GetBoolean("option.singleColumn") ? DataGridGridLinesVisibility.Vertical : DataGridGridLinesVisibility.None };
        table.ColumnHeaderStyle = new Style(typeof(DataGridColumnHeader)); table.ColumnHeaderStyle.Setters.Add(new Setter(Control.BackgroundProperty, HmiVisuals.Brush(e.GetString("option.thBackgroundColor", "#3e403e")))); table.ColumnHeaderStyle.Setters.Add(new Setter(Control.ForegroundProperty, HmiVisuals.Brush(e.Foreground)));
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var label = new TextBlock { Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5) };
        var previousButton = HmiWindow.Button("‹", (_, _) => { }); var nextButton = HmiWindow.Button("›", (_, _) => { });
        toolbar.Children.Add(previousButton); toolbar.Children.Add(label); toolbar.Children.Add(nextButton); DockPanel.SetDock(toolbar, Dock.Bottom); root.Children.Add(toolbar); root.Children.Add(table);
        var rows = new List<Dictionary<string, object>>(); var fields = new List<(string Key, string Title, double Width)>(); var signature = "";
        previousButton.Click += (_, _) => { page--; Draw(); }; nextButton.Click += (_, _) => { page++; Draw(); };
        void Draw()
        {
            var pages = Math.Max(1, (rows.Count + size - 1) / size); page = Math.Clamp(page, 0, pages - 1);
            var data = new DataTable(); foreach (var field in fields) if (!data.Columns.Contains(field.Key)) data.Columns.Add(field.Key, typeof(object));
            foreach (var row in rows.Skip(page * size).Take(size)) { var item = data.NewRow(); foreach (DataColumn column in data.Columns) item[column.ColumnName] = row.GetValueOrDefault(column.ColumnName) ?? DBNull.Value; data.Rows.Add(item); }
            table.Columns.Clear(); foreach (var field in fields) table.Columns.Add(new DataGridTextColumn { Header = field.Title, Binding = new Binding("[" + field.Key + "]"), Width = e.GetBoolean("option.isAutoCell") ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(Math.Max(1, field.Width)) });
            table.ItemsSource = data.DefaultView; label.Text = $"{page + 1} / {pages}  共 {rows.Count} 条"; previousButton.IsEnabled = page > 0; nextButton.IsEnabled = page + 1 < pages;
        }
        table.LoadingRow += (_, args) =>
        {
            args.Row.Background = HmiVisuals.Brush(e.GetString("option.trBackgroundColor", "#141312"));
            var key = e.GetString("option.associationKey");
            if (key.Length == 0 || args.Row.Item is not DataRowView row || !row.DataView.Table.Columns.Contains(key)) return;
            var value = HmiVisuals.Number(row[key], double.NaN);
            var rule = (e.Option["associationList"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(item => HmiVisuals.Number(item["minValue"], double.PositiveInfinity) <= value && value <= HmiVisuals.Number(item["maxValue"], double.NegativeInfinity));
            if (rule?["color"] != null) args.Row.Background = HmiVisuals.Brush(rule["color"].ToString());
        };
        refresh.Add(() =>
        {
            var definitions = (e.GetNode("option.dataset.columns") as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
            var tables = HmiVisuals.Strings(e.GetNode("option.sourceTable")).Where(value => value.Length > 0).ToArray();
            rows = host.IsDesignMode ? HmiVisuals.Rows(e.GetNode("option.dataset")) : tables.SelectMany(name => HmiVisuals.Rows(runtime?.QueryTable(name))).Reverse().ToList();
            var selected = HmiVisuals.Strings(e.GetNode("option.sourceLine"));
            if (!host.IsDesignMode && selected.Any(value => value.Length > 0))
                rows = rows.Select(row =>
                {
                    var mapped = new Dictionary<string, object> { ["time"] = row.GetValueOrDefault("InsertTime") ?? row.GetValueOrDefault("time") };
                    for (var index = 0; index < selected.Length; index++) if (selected[index].Length > 0 && selected[index] != "time") mapped[index < definitions.Length ? definitions[index]["key"]?.ToString() ?? selected[index] : selected[index]] = row.GetValueOrDefault(selected[index]);
                    return mapped;
                }).ToList();
            fields = definitions.Select(column => (column["key"]?.ToString() ?? "", column["title"]?.ToString() ?? "", HmiVisuals.Number(column["width"], e.GetNumber("option.cellWidth", 130)))).Where(field => field.Item1.Length > 0).ToList();
            if (!host.IsDesignMode && !selected.Any(value => value.Length > 0) && rows.Count > 0) fields = rows[0].Keys.Select(key => (key, key, e.GetNumber("option.cellWidth", 130))).ToList();
            var next = JsonSerializer.Serialize(rows) + JsonSerializer.Serialize(fields);
            if (next == signature) return; signature = next; Draw();
        });
        return root;
    }

    private static FrameworkElement AnimatedVector(FrameworkElement view, List<Action> refresh, List<Action> dispose, int interval)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(interval) };
        timer.Tick += (_, _) => view.InvalidateVisual(); timer.Start(); refresh.Add(view.InvalidateVisual); dispose.Add(timer.Stop); return view;
    }

    private sealed class ClockVisual(HmiElement element) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            var scale = Math.Min(ActualWidth, ActualHeight) / 200; if (scale <= 0) return;
            dc.PushTransform(new TranslateTransform((ActualWidth - 200 * scale) / 2, (ActualHeight - 200 * scale) / 2)); dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawEllipse(HmiVisuals.Brush(element.GetString("option.bgColor", "#84a5e9")), new Pen(HmiVisuals.Brush(element.GetString("option.borderColor", "#ffffff")), element.GetNumber("option.border", 6)), new Point(100, 100), 80, 80);
            var brush = HmiVisuals.Brush(element.GetString("option.color", "#ffffff"));
            Point At(double degrees, double radius) { var radians = (degrees - 90) * Math.PI / 180; return new Point(100 + Math.Cos(radians) * radius, 100 + Math.Sin(radians) * radius); }
            for (var index = 0; index < 12; index++) dc.DrawLine(new Pen(brush, 1), At(index * 30, 60), At(index * 30, 70));
            var now = DateTime.Now; var seconds = now.Second + now.Millisecond / 1000d;
            dc.DrawLine(new Pen(brush, 3), new Point(100, 100), At(now.Hour % 12 * 30 + now.Minute / 2d, 45));
            dc.DrawLine(new Pen(brush, 4), new Point(100, 100), At(now.Minute * 6 + seconds / 10d, 60));
            dc.DrawLine(new Pen(brush, 2), new Point(100, 100), At(seconds * 6, 70));
            dc.DrawEllipse(HmiVisuals.Brush("#128a86"), new Pen(HmiVisuals.Brush("#c1efed"), 2), new Point(100, 100), 3, 3); dc.Pop(); dc.Pop();
        }
    }

    private sealed class PipelineVisual(HmiElement element) : FrameworkElement
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            var horizontal = element.Type == "PipelineH"; var thickness = horizontal ? ActualHeight : ActualWidth;
            var start = horizontal ? new Point(0, ActualHeight / 2) : new Point(ActualWidth / 2, 0);
            var end = horizontal ? new Point(ActualWidth, ActualHeight / 2) : new Point(ActualWidth / 2, ActualHeight);
            dc.DrawLine(new Pen(HmiVisuals.Brush(element.GetString("option.o_color", "#0a7ae2")), thickness), start, end);
            var phase = _clock.Elapsed.TotalSeconds % 10 / 10; var effect = element.GetString("option.line_class", "svg_ani_flow");
            var dash = effect.EndsWith("stop") ? 10 : effect.EndsWith("back") ? 13 - 3 * phase : 10 + 3 * phase;
            var pen = new Pen(HmiVisuals.Brush(element.GetString("option.i_color", "#119bfa")), thickness / 2) { DashStyle = new DashStyle([dash / (thickness / 2), 5 / (thickness / 2)], 1000 / (thickness / 2)) };
            dc.DrawLine(pen, start, end);
        }
    }

    private sealed class TitleVisual(HmiElement element) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            var w = ActualWidth; var h = ActualHeight; if (w < 1 || h < 1) return;
            var fill = HmiVisuals.Brush(element.GetString("option.colors[0]", "#1dc1f533")); var pen = new Pen(HmiVisuals.Brush(element.GetString("option.colors[1]", "#1dc1f5ff")), 1);
            void Shape(Point[] points, bool close)
            {
                var geometry = new StreamGeometry(); using (var context = geometry.Open()) { context.BeginFigure(points[0], close, close); context.PolyLineTo(points.Skip(1).ToArray(), true, false); } dc.DrawGeometry(close ? fill : null, pen, geometry);
            }
            Shape([new(15.5, 6.5), new(.5, h / 2), new(15.5, h - 6.5), new(w - 15.5, h - 6.5), new(w - .5, h / 2), new(w - 15.5, 6.5)], true);
            Shape([new(20.5, 14.5), new(8.5, h / 2), new(20.5, h - 14.5)], false); Shape([new(w - 20.5, 14.5), new(w - 8.5, h / 2), new(w - 20.5, h - 14.5)], false);
            foreach (var right in new[] { false, true }) foreach (var bottom in new[] { false, true })
            {
                Point Map(double x, double y) => new(right ? w - x : x, bottom ? h - y : y);
                Shape([Map(15.5, 6.5), Map(20.5, .5), Map(50.5, .5), Map(55.5, 6.5)], true);
            }
            var text = new FormattedText(element.GetString("option.dataset"), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), Math.Max(1, element.GetNumber("option.textSize", 32)), HmiVisuals.Brush(element.GetString("option.textColor", "#ffffff")), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point((w - text.Width) / 2, (h - text.Height) / 2));
        }
    }
}
