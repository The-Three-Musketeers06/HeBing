using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YamlDotNet.RepresentationModel;
using AiVisionStudio.Models;

namespace AiVisionStudio.Views;

/// <summary>Image windows positioned by the .aivs layout percentages and zIndex.</summary>
public sealed class ImageWorkspace : Panel
{
    private sealed record Pane(int Index, double Left, double Top, double Width, double Height, int ZIndex, Border Frame);
    private readonly List<Pane> _panes = new();
    private readonly Dictionary<int, ImageView> _viewers = new();
    private string _layoutSignature;
    private string _context = "";
    private int? _maximized;
    private readonly HashSet<int> _hidden = new();

    public IReadOnlyDictionary<int, ImageView> Viewers => _viewers;
    // 只有一个未关闭的窗口时，返回它的窗口编号。
    // 最大化不会改变实际打开的窗口数量。
    public bool TryGetSingleOpenPane(out int paneId)
    {
        paneId = -1;
        foreach (var pane in _panes)
        {
            if (_hidden.Contains(pane.Index))
                continue;
            // 已经找到一个，又找到第二个，说明是多画布
            if (paneId >= 0)
            {
                paneId = -1;
                return false;
            }

            paneId = pane.Index;
        }

        return paneId >= 0;
    }
    private readonly Dictionary<int, VisualSourceKey> _paneSources = new();

    private readonly Dictionary<int, Button> _paneTitles = new();


    public event Action DisplayRefreshRequested;

    public event Action<int, FrameworkElement> SourceMenuRequested;
    public event Action<ImageView, bool> ViewSaveRequested;
    public event Action<string, string> ImageSelectionRequested;
    public event Action<ImageView, FrameworkElement> ActionsRequested;
    public ImageWorkspace() { ClipToBounds = true; }
    public event Action LayoutChanged;
    public YamlSequenceNode ExportVisualLayout()
    {
        var layout = new YamlSequenceNode();

        foreach (var pane in _panes)
        {
            var viewer = _viewers[pane.Index];

            var entry = new YamlMappingNode
        {
            {
                "paneId",
                pane.Index.ToString(CultureInfo.InvariantCulture)
            },
            {
                "IM",
                viewer.ViewIndex.ToString(CultureInfo.InvariantCulture)
            },
            {
                "left",
                pane.Left.ToString(CultureInfo.InvariantCulture)
            },
            {
                "top",
                pane.Top.ToString(CultureInfo.InvariantCulture)
            },
            {
                "width",
                pane.Width.ToString(CultureInfo.InvariantCulture)
            },
            {
                "height",
                pane.Height.ToString(CultureInfo.InvariantCulture)
            },
            {
                "zIndex",
                pane.ZIndex.ToString(CultureInfo.InvariantCulture)
            },
            {
                "hidden",
                _hidden.Contains(pane.Index) ? "true" : "false"
            }
        };

            if (_paneSources.TryGetValue(pane.Index, out var source))
            {
                entry.Add("sourceProgram", source.ProgramName);
                entry.Add("sourceOperation", source.OperationId);
                entry.Add("sourceOutput", source.OutputKey);
                entry.Add("sourceKind", source.Kind.ToString());
            }

            layout.Add(entry);
        }

        return layout;
    }

    public void AcceptVisualLayout(YamlNode layout)
    {
        // 已经应用过的布局，不必在下一次刷新时重建窗口
        _layoutSignature = layout?.ToString() ?? "";
    }

    private static string LayoutText(
        YamlMappingNode entry,
        string key)
    {
        return entry.Children.TryGetValue(
            new YamlScalarNode(key),
            out var value)
            ? value.ToString()
            : "";
    }
    public bool TryGetPaneSource(int paneId,out VisualSourceKey source)
    {
        return _paneSources.TryGetValue(paneId, out source);
    }

    public void ForgetPaneSource(int paneId, long? clearedAtVersion = null)
    {
        _paneSources.Remove(paneId);
        if (_viewers.TryGetValue(paneId, out var viewer)) viewer.ClearDisplay(clearedAtVersion);
        SetPaneCaption(paneId, "等待运行");
        SetPaneResultTip(paneId, "");
    }

    public void SetPaneCaption(int paneId, string caption)
    {
        if (_paneTitles.TryGetValue(paneId, out var button))
        {
            button.Content = caption + " ▾";
            button.ToolTip = caption;
        }
    }
    // 查找同一个来源是否已在其他未关闭窗口中显示
    public bool TryFindOpenPane(
        VisualSourceKey source,
        int? exceptPaneId,
        out int paneId)
    {
        foreach (var pair in _paneSources)
        {
            if (exceptPaneId.HasValue
                && pair.Key == exceptPaneId.Value)
            {
                continue;
            }

            if (!_viewers.ContainsKey(pair.Key)
                || _hidden.Contains(pair.Key))
            {
                continue;
            }

            if (pair.Value == source)
            {
                paneId = pair.Key;
                return true;
            }
        }

        paneId = -1;
        return false;
    }

    public bool ClearDuplicateOpenSources(IReadOnlySet<int> preferredPaneIds = null)
    {
        bool changed = false;
        var used = new HashSet<VisualSourceKey>();

        foreach (var pane in _panes
            .OrderBy(item => preferredPaneIds?.Contains(item.Index) == true ? 0 : 1)
            .ThenBy(item => item.Index))
        {
            if (_hidden.Contains(pane.Index)|| !_paneSources.TryGetValue(pane.Index,out var source))
            {
                continue;
            }

            if (used.Add(source))
                continue;

            _paneSources.Remove(pane.Index);
            _viewers[pane.Index].Clear();

            SetPaneCaption(pane.Index, "等待选择");

            changed = true;
        }

        return changed;
    }

    public bool BindPaneSource(int paneId,VisualSourceKey source,string caption,int algorithmSlot)
    {
        if (!_viewers.TryGetValue(paneId, out var viewer))
            return false;

        // 在执行绑定时再次拦截重复来源
        if (TryFindOpenPane(source, paneId, out _))
            return false;

        bool changed =!_paneSources.TryGetValue(paneId, out var previous)|| previous != source;

        if (changed)
            viewer.Clear();

        _paneSources[paneId] = source;
        viewer.ViewIndex = algorithmSlot;

        SetPaneCaption(paneId, caption);

        return true;
    }

    public int OpenSource(VisualSourceKey source,string caption,int algorithmSlot)
    {
        // 已打开就返回原窗口，不增加窗口
        if (TryFindOpenPane(source, null, out int existing))
            return existing;

        // 优先恢复原来关闭的同来源窗口
        int paneId = _paneSources.Where(pair => _hidden.Contains(pair.Key) && pair.Value == source) .Select(pair => pair.Key) .DefaultIfEmpty(-1) .First();

        // 其次使用已有空窗口
        if (paneId < 0)
        {
            paneId = _viewers.Keys
                .Where(id =>
                    !_hidden.Contains(id)
                    && !_paneSources.ContainsKey(id))
                .OrderBy(id => id)
                .DefaultIfEmpty(-1)
                .First();
        }

        // 再使用其他已关闭的窗口
        if (paneId < 0)
        {
            paneId = _hidden
                .Where(id => _viewers.ContainsKey(id))
                .OrderBy(id => id)
                .DefaultIfEmpty(-1)
                .First();
        }

        if (paneId < 0)
        {
            paneId = _viewers.Count == 0
                ? 0
                : _viewers.Keys.Max() + 1;

            AddPane(paneId, 0, 0, 100, 100, _panes.Count);
        }

        _hidden.Remove(paneId);

        if (!BindPaneSource(
            paneId,
            source,
            caption,
            algorithmSlot))
        {
            _hidden.Add(paneId);
            TilePanes();
            return -1;
        }

        TilePanes();

        return paneId;
    }

    public void SetContext(string context)
    {
        context ??= "";

        if (_context == context)
            return;

        foreach (var viewer in _viewers.Values)
            viewer.Clear();

        _panes.Clear();
        _viewers.Clear();
        _paneSources.Clear();
        _paneTitles.Clear();

        _hidden.Clear();
        _maximized = null;

        // 把旧流程的窗口控件从画布中移除
        Children.Clear();

        _context = context;
        _layoutSignature = null;

        InvalidateMeasure();
        InvalidateArrange();
    }

    public bool IsPaneOpen(int paneId)
    {
        return _viewers.ContainsKey(paneId)
            && !_hidden.Contains(paneId);
    }

   

    public void SetPaneResultTip(int paneId, string text)
    {
        if (_paneTitles.TryGetValue(paneId, out var button))
            button.ToolTip = text;
    }

    public void SetLayout(YamlNode layout, bool force = false)
    {
        var signature = layout?.ToString() ?? "";
        if (!force && signature == _layoutSignature) return;
        _layoutSignature = signature;
        _maximized = null; _hidden.Clear();
        foreach (var viewer in _viewers.Values)
            viewer.Clear();
        _panes.Clear();
        _viewers.Clear();
        _paneSources.Clear();
        _paneTitles.Clear();
        Children.Clear();
        if (layout is YamlSequenceNode sequence)
        {
            foreach (var entry in sequence.Children.OfType<YamlMappingNode>())
            {
                var index = (int)Number(entry, "paneId", Number(entry, "IM", -1));
                if (index < 0 || _viewers.ContainsKey(index)) continue;
                var left = Math.Clamp(Number(entry, "left", 0), 0, 100);
                var top = Math.Clamp(Number(entry, "top", 0), 0, 100);
                var width = Math.Clamp(Number(entry, "width", 100), 0, 100 - left);
                var height = Math.Clamp(Number(entry, "height", 100), 0, 100 - top);
                if (width <= 0 || height <= 0) continue;
                AddPane(index, left, top, width, height, (int)Number(entry, "zIndex", 0)); _viewers[index].ViewIndex = (int)Number(entry, "IM", index);
                string operationId = LayoutText(entry, "sourceOperation");

                if (!string.IsNullOrEmpty(operationId) && Enum.TryParse<VisualResultKind>(LayoutText(entry, "sourceKind"), out var kind))
                {
                    _paneSources[index] = new VisualSourceKey(LayoutText(entry, "sourceProgram"), operationId, LayoutText(entry, "sourceOutput"), kind);
                }

                if (LayoutText(entry, "hidden") == "true") _hidden.Add(index);
            }
        }
        if (_panes.Count == 0)
            AddPane(0, 0, 0, 100, 100, 0);
        // 加载流程或工程时，也按照未关闭的窗口重新均分
        ClearDuplicateOpenSources();
        TilePanes();
    }

    private static double Number(YamlMappingNode mapping, string key, double fallback)
        => mapping.Children.TryGetValue(new YamlScalarNode(key), out var value)
            && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            && double.IsFinite(result) ? result : fallback;

    private void AddPane(int index, double left, double top, double width, double height, int zIndex)
    {
        var viewer = new ImageView { ViewIndex = index };
        viewer.ImageSelectionRequested += (operationId, file) => ImageSelectionRequested?.Invoke(operationId, file);

        viewer.SaveRequested +=(view, raw) => ViewSaveRequested?.Invoke(view, raw);
        var dock = new DockPanel();
        var title = new Grid();
        var sourceButton = new Button
        {
            Content = "选择图像 ▾",
            FontSize = 17,
            FontWeight = FontWeights.Bold,

            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("Text"),
            BorderThickness = new Thickness(0),

            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,

            Margin = new Thickness(38, 0, 98, 0),
            Padding = new Thickness(2, 0, 2, 0),

            ToolTip = "切换当前窗口显示的控件结果"
        };

        sourceButton.Click += (_, _) =>SourceMenuRequested?.Invoke(index, sourceButton);
        _paneTitles[index] = sourceButton;
        title.Children.Add(sourceButton);
        var settings = HeaderButton("⚙", "图像操作");
        settings.HorizontalAlignment = HorizontalAlignment.Left;
        settings.Foreground = (Brush)FindResource("Accent");
        settings.Click += (_, _) => ActionsRequested?.Invoke(viewer, settings);
        title.Children.Add(settings);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var fit = HeaderButton("fit", "适应窗口");
        fit.Click += (_, _) => viewer.FitView();
        var maximize = HeaderButton("maximize", "最大化 / 还原图像窗口");
        maximize.Click += (_, _) => ToggleMaximize(index);
        var close = HeaderButton("close", "关闭图像窗口（视图菜单可恢复）");
        close.Click += (_, _) => ClosePane(index);
        buttons.Children.Add(fit); buttons.Children.Add(maximize); buttons.Children.Add(close);
        title.Children.Add(buttons);
        var header = new Border { Style = (Style)FindResource("ImagePanelHeader"), Child = title };
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header); dock.Children.Add(viewer);
        var frame = new Border { Style = (Style)FindResource("PanelBorder"), Child = dock, Margin = new Thickness(1.5) };
        SetZIndex(frame, zIndex);
        Children.Add(frame);
        _panes.Add(new Pane(index, left, top, width, height, zIndex, frame));
        _viewers.Add(index, viewer);
    }

    private Button HeaderButton(string text, string tip)
        => new() { Style = (Style)FindResource("CaptionButton"), Content = text == "⚙" ? text : ViewGlyph.Create(text), ToolTip = tip, FontSize = 18, VerticalAlignment = VerticalAlignment.Center };

    public void ToggleMaximize(int index)
    {
        if (!_viewers.ContainsKey(index))
            return;

        _hidden.Remove(index);

        _maximized = _maximized == index
            ? null
            : index;

        UpdatePanes();
        FitAll();
    }
    public void ClosePane(int index)
    {
        if (!_viewers.TryGetValue(index, out var viewer))
            return;

        // 释放窗口自己的显示副本，控件结果快照仍然保留
        viewer.Clear();

        _hidden.Add(index);
        TilePanes();
    }
    public void RestorePanes()
    {
        var openPaneIds = _panes.Where(pane => !_hidden.Contains(pane.Index))
            .Select(pane => pane.Index).ToHashSet();
        _hidden.Clear();

        ClearDuplicateOpenSources(openPaneIds);
        TilePanes();

        DisplayRefreshRequested?.Invoke();
    }
    public void OpenPane(int index)
    {
        if (index < 0 || index > 255) throw new ArgumentOutOfRangeException(nameof(index));
        if (!_viewers.ContainsKey(index)) AddPane(index, 0, 0, 100, 100, _panes.Count);
        _hidden.Remove(index); TilePanes();
    }
    public void EnsureImagePanes(IEnumerable<int> indices)
    {
        var added = false;
        foreach (var index in indices.Where(i => i >= 0 && i <= 255).Distinct().Order())
            if (!_viewers.ContainsKey(index)) { AddPane(index, 0, 0, 100, 100, _panes.Count); added = true; }
        if (added) TilePanes();
    }
    public void TilePanes()
    {
        _maximized = null;
        var visible = _panes.Where(pane => !_hidden.Contains(pane.Index)).ToArray();
        int count = visible.Length;
        if (count == 0)
        {
            UpdatePanes();
            FitAll();
            return;
        }

        // 1个画布占满。
        // 其他奇数数量，按下一个偶数划分网格。
        int slots = count == 1 ? 1 : count + count % 2;
        // 找到能整除网格总数、且尽量接近正方形的行列组合
        int rows = (int)Math.Floor(Math.Sqrt(slots));
        while (rows > 1 && slots % rows != 0)
            rows--;
        int columns = slots / rows;
        double width = 100d / columns;
        double height = 100d / rows;
        // 只排列真实存在的画布，不创建空白窗口
        for (int i = 0; i < count; i++)
        {
            var item = visible[i];
            int at = _panes.FindIndex(pane => pane.Index == item.Index);
            int row = i / columns;
            int column = i % columns;
            _panes[at] = item with
            {
                Left = column * width,
                Top = row * height,
                Width = width,
                Height = height
            };
        }

        UpdatePanes();
        FitAll();
    }
    private void UpdatePanes()
    {
        foreach (var pane in _panes) pane.Frame.Visibility = _hidden.Contains(pane.Index) || _maximized.HasValue && _maximized != pane.Index ? Visibility.Collapsed : Visibility.Visible;
        InvalidateMeasure(); InvalidateArrange(); LayoutChanged?.Invoke();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        var height = double.IsFinite(availableSize.Height) ? availableSize.Height : 0;
        foreach (var pane in _panes) pane.Frame.Measure(_maximized == pane.Index ? new Size(width, height) : new Size(width * pane.Width / 100, height * pane.Height / 100));
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var pane in _panes)
            pane.Frame.Arrange(_maximized == pane.Index ? new Rect(finalSize) : new Rect(finalSize.Width * pane.Left / 100, finalSize.Height * pane.Top / 100,
                finalSize.Width * pane.Width / 100, finalSize.Height * pane.Height / 100));
        return finalSize;
    }

    public void FitAll()
    {
        foreach (var pane in _panes)
        {
            if (_hidden.Contains(pane.Index))
                continue;
            if (_maximized.HasValue&& _maximized.Value != pane.Index)
            {
                continue;
            }

            if (_viewers.TryGetValue(pane.Index,out var viewer))
            {
                viewer.RequestFitView();
            }
        }
    }
    public void RenameVisualContext(string oldContext, string newContext,string oldProgram,string newProgram)
    {
        if (_context != oldContext)
            return;

        foreach (var pair in _paneSources.ToArray())
        {
            if (pair.Value.ProgramName == oldProgram)
            {
                _paneSources[pair.Key] = pair.Value with
                {
                    ProgramName = newProgram
                };
            }
        }

        _context = newContext;
        _layoutSignature = null;
    }
}

