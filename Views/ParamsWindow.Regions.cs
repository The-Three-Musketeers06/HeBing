using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AiVisionStudio.Models;
using AiVisionStudio.Services;

namespace AiVisionStudio.Views;

public partial class ParamsWindow
{
    private RegionEditTarget _regionTarget;
    private bool _regionCommitted;
    private bool _committingRegion;

    private static string RegionTypeLabel(string type) => type.Replace(" array", "") switch
    {
        "rect2d" => "矩形", "window2d" => "矩形窗口", "circle2d" or "circlewindow2d" => "圆形",
        "ring2d" => "圆环", "sector2d" => "扇形", "polygon2d" => "多边形", "line_caliper" => "直线卡尺",
        "circle_caliper" => "圆卡尺", "arc_caliper" => "圆弧卡尺", "point2d" => "点", "segment2d" => "线段", "ellipse2d" => "椭圆", _ => type
    };

    public void OpenRegionEditor(ParameterNode node, FrameworkElement button)
    {
        if (!_vm.CanEdit) return;
        var root = MainViewModel.RegionRoot(node);
        if (root == null || _vm.Is3DTool || PreviewPane.Visibility != Visibility.Visible)
        {
            TestStatus.Text = "请在二维图像工具中绘制区域"; return;
        }
        Keyboard.ClearFocus();
        if (!_vm.ApplyParameters()) { TestStatus.Text = "请先修正无效参数"; return; }
        try
        {
            if (node.Parent?.IsArray == true && ReferenceEquals(node.Parent.Parent, root)
                && int.TryParse(node.Path[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                BeginRegionEditor(_vm.RegionTarget(root, node.Parent.Name, index)); return;
            }
            var types = ReferenceEquals(node, root) ? root.Specification.Support.Where(type => EditableRegion2D.SupportedKinds.Contains(type.Replace(" array", ""))).ToArray() : [node.Name];
            if (types.Length == 1 && !types[0].EndsWith(" array", StringComparison.Ordinal))
            {
                BeginRegionEditor(_vm.RegionTarget(root, types[0], null)); return;
            }
            var menu = new ContextMenu { PlacementTarget = button };
            foreach (var type in types)
            {
                var item = new MenuItem { Header = RegionTypeLabel(type) };
                if (type.EndsWith(" array", StringComparison.Ordinal))
                {
                    int count = _vm.RegionItemCount(_vm.RegionTarget(root, type, null));
                    for (int i = 0; i < count; i++)
                    {
                        int selected = i; var child = new MenuItem { Header = $"区域 {i + 1}" };
                        child.Click += (_, _) => BeginRegionEditor(_vm.RegionTarget(root, type, selected)); item.Items.Add(child);
                    }
                    var add = new MenuItem { Header = "新增区域" };
                    add.Click += (_, _) => BeginRegionEditor(_vm.RegionTarget(root, type, count)); item.Items.Add(add);
                }
                else item.Click += (_, _) => BeginRegionEditor(_vm.RegionTarget(root, type, null));
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            TestStatus.Text = error.Message;
        }
    }

    private void BeginRegionEditor(RegionEditTarget target)
    {
        try
        {
            _parameterRegionPreviewPath = null;
            var region = _vm.ReadEditableRegion(target);
            int slot = _vm.RegionInputIndex;
            using var image = _vm.CopySelectedInputImage();
            if (image == null || image.IsDisposed || image.Empty()) { TestStatus.Text = "请先加载或运行前置步骤，提供输入图像"; return; }
            _previewIndex = slot; _previewIsCloud = false;
            EndRegionEditing(true);
            RefreshPreview();
            _regionTarget = target; _regionCommitted = false;
            _vm.ExpandRegionParameters(target);
            Preview.RegionEditor.Commit = CommitEditedRegion;
            Preview.EditRegion(region, target.Index is { } index && index >= _vm.RegionItemCount(target));
            Preview.SetNativeOverlays(null);
            PreviewTextOverlay.Visibility = Visibility.Collapsed;
            RegionEditBar.Visibility = Visibility.Visible;
            RegionEditHint.Text = RegionTypeLabel(target.TypeKey) + (target.Kind == "polygon2d" ? "：单击加点，双击或 Enter 完成" : "：拖动绘制，拖动方块调整") + "；Alt+左键或中键平移，滚轮缩放";
            TestStatus.Text = "绘制后自动回填参数，点击测试执行";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            TestStatus.Text = error.Message;
        }
    }

    private bool CommitEditedRegion(EditableRegion2D region)
    {
        try
        {
            if (_regionTarget == null) return false;
            _committingRegion = true;
            try { _vm.CommitRegion(_regionTarget, region); }
            finally { _committingRegion = false; }
            _regionCommitted = true;
            TestStatus.Text = "区域已更新，点击测试执行";
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            TestStatus.Text = error.Message; return false;
        }
    }

    private void SynchronizeEditedRegion()
    {
        if (_regionTarget == null || Preview.RegionEditor.IsInteracting) return;
        if (!Preview.HasImage || _regionTarget.OperationId != _vm.SelectedOperation?.Uuid
            || _regionCommitted && _vm.CurrentRegionType(_regionTarget) != _regionTarget.TypeKey)
        {
            EndRegionEditing(true); return;
        }
        try { Preview.RegionEditor.Synchronize(_vm.ReadEditableRegion(_regionTarget)); }
        catch (InvalidOperationException) { EndRegionEditing(true); }
    }

    private void EndRegionEditing(bool clear = false)
    {
        Preview.EndRegionEdit(clear);
        RegionEditBar.Visibility = Visibility.Collapsed;
        if (clear) { _regionTarget = null; Preview.RegionEditor.Commit = null; }
    }

    private void OnRedrawRegion(object sender, RoutedEventArgs e) => Preview.RegionEditor.Redraw();
    private void OnFinishRegionEdit(object sender, RoutedEventArgs e)
    {
        if (Preview.RegionEditor.IsInteracting && !Preview.RegionEditor.FinishPolygon())
        {
            TestStatus.Text = "请完成有效区域，多边形至少需要三个点"; return;
        }
        EndRegionEditing();
    }
}
