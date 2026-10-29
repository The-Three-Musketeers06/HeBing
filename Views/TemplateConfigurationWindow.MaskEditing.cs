using System.Collections;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Algorithm.Impl;
using OpenCvSharp;

namespace AiVisionStudio.Views;

public partial class TemplateConfigurationWindow
{
    private static readonly string[] MaskKeys = ["Use Shielding Area", "Shielding Area", "Shielding Strokes"];
    private Dictionary<string, object> _maskWorking;
    private readonly Stack<string> _maskUndo = new(), _maskRedo = new();
    private Button _maskUndoButton, _maskRedoButton, _maskApplyButton, _maskCancelButton;
    private bool _updatingImageSize;

    private void SetBrushRadius(double radius)
    {
        if (!double.IsFinite(radius)) return;
        _brushRadius = Math.Clamp(Math.Round(radius), 1, 500);
        CanvasView.SetTemplateBrushRadius(_brushRadius);
        BrushSizeSlider.Value = _brushRadius;
    }

    private void OnBrushSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_ready && !_busy) SetBrushRadius(e.NewValue);
    }

    private void BuildMaskToolbar()
    {
        AddDrawingTool("mask:rect2d", MaskTools, "矩形掩膜", "M3,4 L21,4 21,20 3,20 Z", () => BeginMaskRegion("rect2d"));
        AddDrawingTool("mask:circle2d", MaskTools, "圆形掩膜", "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12", () => BeginMaskRegion("circle2d"));
        AddDrawingTool("mask:polygon2d", MaskTools, "多边形掩膜", "M3,19 L8,5 21,2 21,20 Z", () => BeginMaskRegion("polygon2d"));
        MaskButton("全部填充", FillMask);
        MaskButton("清空", ClearMask);
        MaskButton("反选", InvertMask);
        _maskUndoButton = AddTool(MaskTools, "撤销掩膜（Ctrl+Z）", "M9,4 L3,9 9,14 M3,9 L15,9 C23,9 23,21 15,21 L9,21", UndoMask);
        _maskRedoButton = AddTool(MaskTools, "重做掩膜（Ctrl+Y）", "M15,4 L21,9 15,14 M21,9 L9,9 C1,9 1,21 9,21 L15,21", RedoMask);
        _maskApplyButton = MaskButton("应用", ApplyMask);
        _maskCancelButton = MaskButton("取消修改", CancelMask);
    }

    private Button MaskButton(string label, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(8, 4, 8, 4), MinHeight = 31, Focusable = false };
        button.Click += (_, _) => action(); MaskTools.Children.Add(button); return button;
    }

    private ParamBag DraftParameters(Dictionary<string, object> draft)
        => ParamBag.FromYaml(ToYaml(JsonSerializer.SerializeToElement(draft)));

    private Mat TrainingRegion()
        => BlobDetectOp.RegionMask(_image.Size(), _draft.GetValueOrDefault(_regionKey), DraftParameters(_draft));

    private bool RequireAppliedMask()
    {
        if (_maskWorking == null) return true;
        Status.Text = "请先点击掩膜工具栏的“应用”或“取消修改”";
        return false;
    }

    private bool FinishMaskGesture()
    {
        if (!CanvasView.IsRegionEditing) return true;
        if (!CanvasView.HasPendingTemplateGesture) CanvasView.CancelRegionEdit();
        else CanvasView.CompleteRegionEdit();
        if (!CanvasView.IsRegionEditing) return true;
        Status.Text = "请先完成有效图形；多边形至少需要三个点，或点击“取消修改”";
        return false;
    }

    private bool StartMaskSession()
    {
        if (!EnsureImage() || !FinishMaskGesture()) return false;
        try
        {
            using var region = TrainingRegion();
            if (Cv2.CountNonZero(region) < 25) { Status.Text = "请先绘制有效的模板区域，再编辑掩膜"; return false; }
        }
        catch (Exception error) { ShowError(error); return false; }
        _maskWorking ??= Copy(_draft);
        UpdateMaskControls();
        return true;
    }

    private void BeginMaskRegion(string type)
    {
        if (!StartMaskSession()) return;
        int index = MaskStrokes(_maskWorking).Count;
        CanvasView.SetTemplateMaskDrawing(true);
        CanvasView.BeginRegionEdit(type, null, region => AddMaskStrokes([new Dictionary<string, object>(region) { ["_EditorRegion"] = true }], false), "绘制掩膜，Enter 确认；应用后用于训练");
        TrackRegionEditing("stroke:" + index);
        SelectDrawingTool("mask:" + type);
        Status.Text = "绘制红色屏蔽区域，点击“应用”确认，或“取消修改”恢复";
    }

    private static string MaskSnapshot(Dictionary<string, object> draft)
        => JsonSerializer.Serialize(MaskKeys.ToDictionary(key => key, key => draft.GetValueOrDefault(key)));

    private void EditMask(Action change)
    {
        if (_maskWorking == null) return;
        var before = MaskSnapshot(_maskWorking);
        change();
        if (before != MaskSnapshot(_maskWorking)) { _maskUndo.Push(before); _maskRedo.Clear(); }
        RefreshCanvas(); UpdateMaskControls();
        Status.Text = "掩膜修改待应用：红色区域不参与训练；应用后点击训练保存到工程";
    }

    private void AddMaskStrokes(IReadOnlyList<Dictionary<string, object>> strokes, bool erase)
    {
        EditMask(() =>
        {
            var values = (_maskWorking.GetValueOrDefault("Shielding Strokes") as IEnumerable)?.Cast<object>().ToList() ?? new();
            values.AddRange(strokes.Select(stroke => (object)new Dictionary<string, object>(stroke) { ["Erase"] = erase }));
            _maskWorking["Shielding Strokes"] = values;
        });
    }

    private void AddBrushStrokes(IReadOnlyList<Dictionary<string, object>> strokes, bool erase)
        => AddMaskStrokes(strokes.Select(stroke => new Dictionary<string, object>(stroke) { ["_EditorBrush"] = true }).ToArray(), erase);

    private void FillMask()
    {
        if (!StartMaskSession()) return;
        EditMask(() =>
        {
            _maskWorking["Use Shielding Area"] = true;
            _maskWorking["Shielding Area"] = Copy(_draft[_regionKey]);
            _maskWorking["Shielding Strokes"] = Array.Empty<object>();
        });
    }

    private void ClearMask()
    {
        if (!StartMaskSession()) return;
        EditMask(() => { _maskWorking["Use Shielding Area"] = false; _maskWorking["Shielding Strokes"] = Array.Empty<object>(); });
    }

    private void InvertMask()
    {
        if (!StartMaskSession()) return;
        var region = Copy(_draft[_regionKey]); region["Invert"] = true;
        AddMaskStrokes([region], false);
    }

    private void UndoMask() => RestoreMaskHistory(_maskUndo, _maskRedo);
    private void RedoMask() => RestoreMaskHistory(_maskRedo, _maskUndo);
    private void RestoreMaskHistory(Stack<string> source, Stack<string> target)
    {
        if (_busy || _maskWorking == null || source.Count == 0) return;
        CanvasView.CancelRegionEdit();
        target.Push(MaskSnapshot(_maskWorking));
        var state = Map(JsonSerializer.Deserialize<JsonElement>(source.Pop()));
        foreach (var key in MaskKeys) _maskWorking[key] = state.GetValueOrDefault(key);
        RefreshCanvas(); UpdateMaskControls();
        Status.Text = "已恢复掩膜编辑，点击“应用”确认";
    }

    private void ApplyMask()
    {
        if (_busy || _maskWorking == null || !FinishMaskGesture()) return;
        var state = _maskWorking;
        _maskWorking = null; _maskUndo.Clear(); _maskRedo.Clear();
        CanvasView.SetTemplateMaskDrawing(false);
        if (MaskSnapshot(state) != MaskSnapshot(_draft))
            Change(() => { foreach (var key in MaskKeys) _draft[key] = state.GetValueOrDefault(key); });
        else { RefreshCanvas(); UpdateMaskControls(); }
        Status.Text = "掩膜已应用到训练草稿，点击“训练”保存到工程";
    }

    private void CancelMask()
    {
        if (_busy || _maskWorking == null) return;
        CanvasView.CancelRegionEdit(); CanvasView.SetTemplateMaskDrawing(false);
        _maskWorking = null; _maskUndo.Clear(); _maskRedo.Clear();
        RefreshCanvas(); UpdateMaskControls(); Status.Text = "已取消本次掩膜修改，恢复编辑前状态";
    }

    private void UpdateMaskControls()
    {
        if (_maskApplyButton == null) return;
        bool editing = _maskWorking != null;
        MaskTools.IsEnabled = !_busy && _image != null;
        BrushSizePanel.IsEnabled = MaskTools.IsEnabled;
        _maskApplyButton.IsEnabled = _maskCancelButton.IsEnabled = editing;
        _maskUndoButton.IsEnabled = editing && _maskUndo.Count > 0;
        _maskRedoButton.IsEnabled = editing && _maskRedo.Count > 0;
        _undoButton.IsEnabled = !_busy && !editing && _undo.Count > 0;
        _redoButton.IsEnabled = !_busy && !editing && _redo.Count > 0;
        SettingsPanel.IsEnabled = CenterPanel.IsEnabled = !_busy && !editing;
    }

    private void RefreshMaskPreview()
    {
        try
        {
            using var region = TrainingRegion();
            using var mask = TemplateShieldingMask.Create(_image.Size(), DraftParameters(_maskWorking ?? _draft), region);
            CanvasView.ShowTemplateMask(mask, region);
        }
        catch (Exception error) { CanvasView.ShowTemplateMask(null); ShowError(error); }
    }

    private void OnImageSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingImageSize || !CanvasView.HasImage) return;
        if (ImageSize.SelectedIndex == 0) CanvasView.Fit();
        else if (ImageSize.SelectedIndex is >= 1 and <= 5)
            CanvasView.SetTemplateZoom(new[] { .25, .5, 1d, 2, 4 }[ImageSize.SelectedIndex - 1]);
        UpdateImageSize();
    }

    private void UpdateImageSize()
    {
        if (ImageSize == null || !CanvasView.HasImage) return;
        _updatingImageSize = true;
        var scales = new[] { .25, .5, 1d, 2, 4 };
        int index = Array.FindIndex(scales, value => Math.Abs(value - CanvasView.TemplateZoom) < .00001);
        ImageSize.SelectedIndex = CanvasView.TemplateFitsWindow ? 0 : index < 0 ? 6 : index + 1;
        _updatingImageSize = false;
    }
}
