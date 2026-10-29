using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Algorithm.Impl;
using AiVisionStudio.Models;
using OpenCvSharp;

namespace AiVisionStudio.Views;

public partial class ParamsWindow
{
    private DataGrid _shapeMatchTable;
    private TextBlock _shapeSelectionInfo;
    private int _shapeSelectedMatchIndex = -1;
    private bool _shapeSyncingSelection, _shapeResultsStale;
    private readonly List<ParameterNode> _shapeRuntimeObservedNodes = new();
    private string _shapeRuntimeSignature, _shapeRuntimeOperation;
    private object _shapeOutputMatches;

    private void ObserveShapeRuntimeParameters(ParameterNode[] roots, ParameterNode[] runtime)
    {
        foreach (var node in _shapeRuntimeObservedNodes) node.PropertyChanged -= OnShapeRuntimeParameterChanged;
        _shapeRuntimeObservedNodes.Clear();
        foreach (var node in runtime.Concat(roots.Where(node => node.Specification?.Field == "render")).SelectMany(ShapeDescendants).Distinct())
        {
            node.PropertyChanged += OnShapeRuntimeParameterChanged;
            _shapeRuntimeObservedNodes.Add(node);
        }
        var names = runtime.Concat(roots.Where(node => node.Specification?.Field == "render")).Select(node => node.Name).Distinct().OrderBy(name => name).ToArray();
        var bindings = _shapeRuntimeObservedNodes.Where(node => node.IsBound).Select(node => new { node.Path, node.BindingLabel }).ToArray();
        var signature = _vm.SelectedImageInputIdentity + "|" + JsonSerializer.Serialize(new { Values = names.ToDictionary(name => name, name => _vm.ReadParameterValue(name)), Bindings = bindings });
        if (_shapeRuntimeOperation != _vm.SelectedOperation?.Uuid)
        {
            _shapeRuntimeOperation = _vm.SelectedOperation?.Uuid;
            _shapeRuntimeSignature = signature;
            _shapeResultsStale = false;
            ClearShapeSelection();
        }
        else if (_shapeRuntimeSignature != signature)
        {
            _shapeRuntimeSignature = signature;
            InvalidateShapeRuntimeResults();
        }
    }

    private void OnShapeRuntimeParameterChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ParameterNode.Value)) return;
        InvalidateShapeRuntimeResults();
    }

    private void InvalidateShapeRuntimeResults()
    {
        _shapeResultsStale = true;
        ClearShapeMatchRows();
        TestStatus.Text = "参数已修改，请点击“测试”执行匹配";
        if (_shapeResultStatus != null) _shapeResultStatus.Text = "参数已修改，请点击“测试”重新匹配";
        Dispatcher.InvokeAsync(RefreshPreview);
    }

    private void MarkShapeResultsCurrent()
    {
        _shapeResultsStale = false;
    }

    private void ClearShapeMatchRows()
    {
        ClearShapeSelection();
        _shapeMatchRows.Clear();
        _shapeOutputMatches = null;
    }

    private void RefreshShapeRowsIfChanged()
    {
        if (!IsShapeMatcher || _shapeTabs == null || _vm.IsRunning || _shapeResultsStale) return;
        var matches = _vm.GetSelectedOutputValues().GetValueOrDefault("Matches");
        if (!ReferenceEquals(matches, _shapeOutputMatches)) RefreshShapeMatchResults(false);
    }

    private void ClearShapeSelection()
    {
        _shapeSelectedMatchIndex = -1;
        _shapeSyncingSelection = true;
        _shapeMatchTable?.UnselectAll();
        _shapeSyncingSelection = false;
        if (_shapeSelectionInfo != null) _shapeSelectionInfo.Text = "点击右侧绿框或列表中的一行查看对应目标；点击图像空白处取消选择。";
    }

    private void OnShapeMatchSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_shapeSyncingSelection) return;
        SelectShapeMatch(_shapeMatchTable.SelectedItem is ShapeMatchRow row ? row.Index - 1 : -1, false);
    }

    private void SelectShapeMatch(int index, bool openResults)
    {
        if (index < 0 || index >= _shapeMatchRows.Count || _shapeResultsStale)
        { ClearShapeSelection(); RefreshPreview(); return; }
        _shapeSelectedMatchIndex = index;
        var row = _shapeMatchRows[index];
        _shapeSyncingSelection = true;
        _shapeMatchTable.SelectedItem = row;
        _shapeSyncingSelection = false;
        if (openResults && _shapeTabs.SelectedIndex != 2)
        {
            // The tab handler clears previous selections; apply this explicit click afterwards.
            _shapeTabs.SelectedIndex = 2;
            _shapeSelectedMatchIndex = index;
            _shapeSyncingSelection = true; _shapeMatchTable.SelectedItem = row; _shapeSyncingSelection = false;
        }
        _shapeMatchTable.ScrollIntoView(row);
        _shapeSelectionInfo.Text = $"选中目标 #{row.Index}\nX：{row.X:0.00}    Y：{row.Y:0.00}\n角度：{row.Angle:0.00}°    尺度：{row.Scale:0.000}    分数：{row.Score:0.000}";
        RefreshPreview();
    }

    private void OnShapeImagePointClicked(object sender, ImagePointClickedEventArgs e)
    {
        if (!IsShapeMatcher || _shapeTabs?.SelectedIndex is not (1 or 2) || !_showOverlays || Preview.IsRegionEditing || Preview.RegionEditor.IsEditing || _regionTarget != null) return;
        SelectShapeMatch(HitTestShapeMatch(e.Point), true);
    }

    private int HitTestShapeMatch(System.Windows.Point point)
    {
        if (_shapeResultsStale || _shapeTemplate == null) return -1;
        double tolerance = 5 / Math.Max(.001, Preview.TemplateZoom);
        return _shapeMatchRows.Select((row, index) =>
        {
            var match = new ContourMatch { X = row.X, Y = row.Y, Rotation = row.Rotation, Scale = row.Scale };
            var center = ContourTemplateEngine.TransformPoint(_shapeTemplate, match, (_shapeTemplate.Width - 1) / 2d, (_shapeTemplate.Height - 1) / 2d);
            double radians = row.Rotation * Math.PI / 180, cosine = Math.Cos(radians), sine = Math.Sin(radians);
            double dx = point.X - center.X, dy = point.Y - center.Y;
            double x = dx * cosine + dy * sine, y = -dx * sine + dy * cosine;
            double w = _shapeTemplate.Width * row.Scale / 2, h = _shapeTemplate.Height * row.Scale / 2;
            return (Index: index, Contains: Math.Abs(x) <= w + tolerance && Math.Abs(y) <= h + tolerance, Distance: x * x + y * y);
        }).Where(candidate => candidate.Contains).OrderBy(candidate => candidate.Distance).Select(candidate => candidate.Index).DefaultIfEmpty(-1).First();
    }

    private NativeOverlay3D[] ShapeResultOverlays()
    {
        if (_shapeResultsStale || _shapeDirty || !_vm.GetSelectedOutputValues().ContainsKey("Matches")) return [];
        return _vm.GetSelectedPreviewOverlays(_vm.RegionInputIndex).Where(overlay =>
            Equals(overlay.Values.GetValueOrDefault("_owned_operation"), _vm.SelectedOperation.Uuid)
            || Equals(overlay.Values.GetValueOrDefault("_owned_operation"), _vm.SelectedOperation.Driver)).Select(overlay =>
        {
            if (_shapeSelectedMatchIndex < 0 || !Equals(overlay.Values.GetValueOrDefault("_match_index"), _shapeSelectedMatchIndex)) return overlay;
            var values = new Dictionary<string, object>(overlay.Values);
            values["_thickness"] = Math.Max(3, ShapeNumber(values, "_thickness", 1) + 2);
            values["_size"] = Math.Max(8, ShapeNumber(values, "_size", 5));
            return new NativeOverlay3D { Type = overlay.Type, ViewIndex = overlay.ViewIndex, Values = values };
        }).ToArray();
    }

    private void DisposeShapeInteractions()
    {
        Preview.ImagePointClicked -= OnShapeImagePointClicked;
        foreach (var node in _shapeRuntimeObservedNodes) node.PropertyChanged -= OnShapeRuntimeParameterChanged;
        _shapeRuntimeObservedNodes.Clear();
    }
}
