using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AiVisionStudio.Views;

public partial class TemplateConfigurationWindow
{
    private sealed record TemplateRegionItem(string Key, int StrokeIndex, Dictionary<string, object> Region);
    private bool _updatingRegionList, _regionListLocked;
    private string _regionEditingKey, _selectedRegionKey;

    private static List<object> MaskStrokes(Dictionary<string, object> draft)
        => (draft?.GetValueOrDefault("Shielding Strokes") as IEnumerable)?.Cast<object>().ToList() ?? new();

    private static string RegionType(Dictionary<string, object> region)
        => region?.GetValueOrDefault("Type")?.ToString() ?? "rect2d";

    private static string RegionTypeName(string type) => type switch
    {
        "rect2d" => "矩形", "circle2d" => "圆形", "polygon2d" => "多边形",
        "ellipse2d" => "椭圆", "sector2d" => "扇形", "ring2d" => "圆环", _ => "区域"
    };

    private static bool HasRegion(Dictionary<string, object> region)
    {
        string type = RegionType(region);
        var shape = Group(region, type);
        return type switch
        {
            "rect2d" => Number(Group(shape, "Size"), "Width") > 0 && Number(Group(shape, "Size"), "Height") > 0,
            "circle2d" => Number(shape, "Radius") > 0,
            "ellipse2d" => Number(shape, "Long Axis") > 0 && Number(shape, "Short Axis") > 0,
            "sector2d" or "ring2d" => Number(shape, "Outer Radius") > 0,
            "polygon2d" => (shape?.GetValueOrDefault("Points") as IEnumerable)?.Cast<object>().Count() >= 3,
            _ => false
        };
    }

    private void TrackRegionEditing(string key)
    {
        _regionEditingKey = _selectedRegionKey = key;
        RefreshRegionList();
    }

    private void RefreshRegionList()
    {
        if (TemplateResults == null || _draft == null || _updatingRegionList) return;
        var entries = new List<TemplateRegionItem>();
        var template = Group(_draft, _regionKey);
        if (HasRegion(template)) entries.Add(new("template", -1, template));
        var strokes = MaskStrokes(_maskWorking ?? _draft);
        for (int index = 0; index < strokes.Count; index++)
        {
            if (strokes[index] is not Dictionary<string, object> region || !HasRegion(region)
                || Boolean(region, "Erase", false) || Boolean(region, "Invert", false) || Boolean(region, "_EditorBrush", false)) continue;
            // Old brush strokes are also polygons. Only explicit polygon regions belong in the list.
            if (RegionType(region) == "polygon2d" && !Boolean(region, "_EditorRegion", false)) continue;
            entries.Add(new("stroke:" + index, index, region));
        }
        if (_regionEditingKey != null && CanvasView.TemplateRegionDraft is { } pending && HasRegion(pending))
        {
            int position = entries.FindIndex(item => item.Key == _regionEditingKey);
            int strokeIndex = _regionEditingKey == "template" ? -1 : int.Parse(_regionEditingKey[7..]);
            var item = new TemplateRegionItem(_regionEditingKey, strokeIndex, pending);
            if (position >= 0) entries[position] = item;
            else if (strokeIndex < 0) entries.Insert(0, item);
            else entries.Add(item);
        }
        _updatingRegionList = true;
        try
        {
            TemplateResults.Items.Clear();
            for (int index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                string type = RegionTypeName(RegionType(entry.Region));
                var row = new StackPanel { Margin = new Thickness(5, 8, 5, 8) };
                row.Children.Add(new TextBlock { Text = $"{index + 1}. " + (entry.StrokeIndex < 0 ? "模板区域 · " + type : type + "掩膜"), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                row.Children.Add(new TextBlock { Text = _regionListLocked ? "已训练 · 已锁定" : "点击调整区域", FontSize = 12, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 5, 0, 0) });
                var listItem = new ListBoxItem { Tag = entry, Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                TemplateResults.Items.Add(listItem);
                if (entry.Key == _selectedRegionKey) TemplateResults.SelectedItem = listItem;
            }
            RegionListHint.Text = _regionListLocked ? "已训练，列表区域已锁定" : "按创建顺序排列，点击区域可修改；Enter 确认";
        }
        finally { _updatingRegionList = false; }
    }

    private void OnRegionItemClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var item = source == null ? null : ItemsControl.ContainerFromElement(TemplateResults, source) as ListBoxItem;
        if (item is { IsSelected: true, Tag: TemplateRegionItem region })
        { EditListedRegion(region); e.Handled = true; }
    }

    private void EditListedRegion(TemplateRegionItem item)
    {
        if (_busy) return;
        _selectedRegionKey = item.Key;
        if (_regionListLocked)
        {
            CanvasView.CancelRegionEdit();
            Status.Text = "区域已训练并锁定；重新选择图像可创建新的区域草稿";
            return;
        }
        if (!FinishMaskGesture()) return;
        if (item.StrokeIndex < 0)
        {
            // Switching to the template keeps pending mask changes in the training draft.
            if (_maskWorking != null) ApplyMask();
            if (_maskWorking != null) return;
            BeginRegion(RegionType(Group(_draft, _regionKey)), true);
            Status.Text = "调整所选模板区域，Enter 确认；训练后锁定";
            return;
        }
        if (!StartMaskSession()) return;
        var strokes = MaskStrokes(_maskWorking);
        if (item.StrokeIndex >= strokes.Count || strokes[item.StrokeIndex] is not Dictionary<string, object> original) return;
        string type = RegionType(original);
        CanvasView.SetTemplateMaskDrawing(true);
        CanvasView.BeginRegionEdit(type, original, changed => EditMask(() =>
        {
            var current = MaskStrokes(_maskWorking);
            if (item.StrokeIndex >= current.Count) return;
            var replacement = new Dictionary<string, object>(original) { ["Type"] = type, [type] = changed[type] };
            current[item.StrokeIndex] = replacement;
            _maskWorking["Shielding Strokes"] = current;
        }), "调整所选掩膜，Enter 确认；应用后用于训练");
        TrackRegionEditing(item.Key);
        SelectDrawingTool("mask:" + type);
        Status.Text = "调整所选掩膜，Enter 确认，再点击“应用”保存到训练草稿";
    }
}
