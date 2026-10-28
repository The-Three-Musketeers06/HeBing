using AiVisionStudio.Models;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using static AiVisionStudio.Models.VisualSnapshot;

namespace AiVisionStudio.Views;

public partial class MainWindow
{

    private VisualSourceInfo[] GetAvailableVisualSources()
    {
        var program = _vm.SelectedProgram;
        if (program == null)
            return Array.Empty<VisualSourceInfo>();
        var operations = program.Operations.ToArray();
        return _vm.GetVisualSources().Where(source => source.Key.ProgramName == program.Name && operations.Any(operation =>
        operation.Uuid == source.Key.OperationId)).OrderBy(source => Array.FindIndex(operations, operation => operation.Uuid == source.Key.OperationId)).ThenBy(source => ChannelSplitChannels.Order(source.Key.OutputKey)).Select(source => source with
        {
            OperationName = operations.First(operation =>
                operation.Uuid == source.Key.OperationId).Name
        })
            .ToArray();
    }
    private void FollowOperationOnSinglePane(Operation operation)
    {
        var program = _vm.SelectedProgram;

        if (_vm.UseNativeKernel || program == null || operation == null || !program.Operations.Any(item => item.Uuid == operation.Uuid) || !ImageWorkspace.TryGetSingleOpenPane(out int paneId))
        {
            return;
        }
        var sources = GetAvailableVisualSources();
        int index = ImageWorkspace.TryGetPaneSource(paneId, out var selectedOutput) && selectedOutput.OperationId == operation.Uuid
            ? Array.FindIndex(sources, source => source.Key == selectedOutput) : -1;
        if (index < 0) index = Array.FindIndex(sources, source => source.Key.OperationId == operation.Uuid);
        if (index < 0 || !RenderPaneSnapshot(paneId, sources[index], index, restoreCleared: true))
        {
            _vm.Status = $"{operation.Name}：暂无图像结果";
            return;
        }

        PersistVisualLayout();

        _vm.Status = $"显示：{operation.Name}";
    }
    private string VisualCaption(
    VisualSourceInfo source,
    int displayIndex)
    {
        var operation = _vm.SelectedProgram?.Operations
            .FirstOrDefault(item =>
                item.Uuid == source.Key.OperationId);

        string suffix = "";

        if (operation?.State == OpState.Fail)
        {
            suffix = " · NG";
        }
        else if (operation != null
            && source.ParameterSignature
                != (operation.ParametersRaw?.ToString() ?? ""))
        {
            suffix = " · 待运行";
        }

        string output = OutputCaption(source);
        bool multiple = _vm.GetVisualSources().Count(item => item.Key.ProgramName == source.Key.ProgramName && item.Key.OperationId == source.Key.OperationId) > 1;
        return OperationCaption(source) + (multiple ? " · " + output : "") + suffix;
    }

    private string OperationCaption(VisualSourceInfo source)
    {
        int index = _vm.SelectedProgram?.Operations.ToList().FindIndex(operation => operation.Uuid == source.Key.OperationId) ?? -1;
        return $"IM{Math.Max(0, index)} · {source.OperationName}";
    }

    private static string OutputCaption(VisualSourceInfo source)
    {
        if (!string.IsNullOrWhiteSpace(source.OutputName)) return source.OutputName;
        string channel = ChannelSplitChannels.Caption(source.Key.OutputKey);
        return channel.Length > 0 ? channel : VisualResultNames.Default(source.Key.Kind);
    }

    private string VisualResultTip(VisualSourceInfo source)
    {
        var operation = _vm.SelectedProgram?.Operations.FirstOrDefault(item => item.Uuid == source.Key.OperationId);
        string state = "已保存的运行图像";
        if (operation?.State == OpState.Fail)
        {
            state = "算子当前为NG，画布显示已保存图像";
        }
        else if (operation != null && source.ParameterSignature != (operation.ParametersRaw?.ToString() ?? ""))
        {
            state = "参数已修改，画布显示上次运行图像";
        }

        return $"图像时间：{source.CapturedAt:yyyy-MM-dd HH:mm:ss.fff}" + $"\n{state}" + $"\n算法来源：IM{source.AlgorithmSlot}";
    }

    private void ShowVisualSourcesMenu(FrameworkElement target, int? paneId)
    {
        var menu = BuildVisualSourcesMenu(target, paneId);
        menu.IsOpen = true;
    }

    private ContextMenu BuildVisualSourcesMenu(FrameworkElement target, int? paneId)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        if (_vm.UseNativeKernel)
        {
            menu.Items.Add(new MenuItem { Header = "原版内核尚未接入逐控件快照", IsEnabled = false });
            return menu;
        }
        var sources = GetAvailableVisualSources();
        if (sources.Length == 0)
            menu.Items.Add(new MenuItem { Header = "暂无可显示的运行结果", IsEnabled = false });

        foreach (var group in sources.GroupBy(source => source.Key.OperationId))
        {
            var parent = new MenuItem { Header = OperationCaption(group.First()), ToolTip = "选择该控件的输出结果" };
            foreach (var source in group)
            {
                bool occupied = ImageWorkspace.TryFindOpenPane(source.Key, null, out int occupiedPane);
                bool isCurrent = paneId.HasValue && ImageWorkspace.TryGetPaneSource(paneId.Value, out var current) && current == source.Key;
                var item = new MenuItem
                {
                    Header = OutputCaption(source) + (occupied ? "（已显示）" : ""),
                    Tag = source.Key,
                    IsEnabled = !occupied,
                    IsCheckable = paneId.HasValue,
                    IsChecked = isCurrent,
                    ToolTip = occupied ? isCurrent ? "当前窗口已经有相同图层" : $"已经有相同图层，正在窗口 {occupiedPane + 1} 显示" : VisualResultTip(source)
                };
                ToolTipService.SetShowOnDisabled(item, true);
                item.Click += (menuSender, e) =>
                {
                    e.Handled = true;
                    if (ImageWorkspace.TryFindOpenPane(source.Key, paneId, out _))
                    { _vm.Status = "已经有相同的图层"; return; }
                    var available = GetAvailableVisualSources();
                    int index = Array.FindIndex(available, candidate => candidate.Key == source.Key);
                    if (index < 0) { _vm.Status = "该图层暂无可显示结果"; return; }
                    var latest = available[index];
                    if (paneId.HasValue)
                    {
                        if (!RenderPaneSnapshot(paneId.Value, latest, index, restoreCleared: true))
                        { _vm.Status = "无法切换该输出结果"; return; }
                    }
                    else if (ImageWorkspace.OpenSource(latest.Key, VisualCaption(latest, index), latest.AlgorithmSlot) < 0)
                    { _vm.Status = "无法打开该图层"; return; }
                    PersistVisualLayout();
                    RefreshImageViews(force: true);
                };
                parent.Items.Add(item);
            }
            menu.Items.Add(parent);
        }
        return menu;
    }

    private void DisplayVisualSnapshot(ImageView viewer,VisualSnapshot snapshot, bool preserveView = false)
    {
        switch (snapshot.Key.Kind)
        {
            case VisualResultKind.Image: viewer.ShowMat(snapshot.Image, _vm.GetImageName(snapshot.AlgorithmSlot, snapshot.Key.OperationId)); viewer.OverlayText = snapshot.OverlayText;
                break;

            case VisualResultKind.Cloud:
                viewer.ShowCloud(snapshot.Cloud, snapshot.Texts,snapshot.Boxes);
                break;

            case VisualResultKind.Profile:
                viewer.ShowProfile(snapshot.Profile,preserveView);
                break;
        }

        viewer.SetNativeOverlays(snapshot.Overlays);
    }
    private bool RenderPaneSnapshot(int paneId,VisualSourceInfo info,int displayIndex, bool restoreCleared = false)
    {
        if (!ImageWorkspace.Viewers.TryGetValue( paneId,out var viewer))
        {
            return false;
        }

        if (ImageWorkspace.TryFindOpenPane(info.Key,paneId,out _))
        {
            return false;
        }

        bool sameSource =ImageWorkspace.TryGetPaneSource(paneId,out var currentSource) && currentSource == info.Key;

        if (restoreCleared)
            viewer.ResumeSnapshotDisplay();

        bool hasContent =viewer.HasImage|| viewer.HasCloud|| viewer.HasProfile;

        // 数据没有变化，只更新标题和提示
        if (sameSource && (hasContent && viewer.DisplayedSnapshotVersion == info.Version
            || viewer.ClearedSnapshotVersion == info.Version))
        {
            if (!ImageWorkspace.BindPaneSource(paneId,info.Key,VisualCaption(info, displayIndex),info.AlgorithmSlot))
            {
                return false;
            }

            ImageWorkspace.SetPaneResultTip(paneId,VisualResultTip(info));

            return true;
        }

        bool applied = false;

        bool found = _vm.UseVisualSnapshot(info.Key, snapshot =>
        {
            var latest = info with
            {
                Key = snapshot.Key,
                AlgorithmSlot = snapshot.AlgorithmSlot,
                Version = snapshot.Version,
                CapturedAt = snapshot.CapturedAt,
                ParameterSignature = snapshot.ParameterSignature
            };

            bool preserveView =sameSource&& hasContent&& currentSource.Kind == snapshot.Key.Kind;

            if (!ImageWorkspace.BindPaneSource(paneId,latest.Key,VisualCaption(latest, displayIndex),latest.AlgorithmSlot))
            {
                return;
            }

            viewer.ResumeSnapshotDisplay();
            DisplayVisualSnapshot(viewer, snapshot,preserveView);

            viewer.DisplayedSnapshotVersion = snapshot.Version;

            // 新打开、换来源或换结果类型时自动适应
            if (!preserveView)viewer.RequestFitView();

            ImageWorkspace.SetPaneResultTip(paneId,VisualResultTip(latest));

            applied = true;
        });

        return found && applied;
    }
    private void PersistVisualLayout()
    {
        if (_vm.UseNativeKernel)
            return;

        if (_vm.SelectedProgram == null)
            return;

        var layout = ImageWorkspace.ExportVisualLayout();

        // 与当前流程自己的布局比较
        var current = _vm.GetCurrentVisualLayout();

        if (current.ToString() == layout.ToString())
            return;

        _vm.UpdateVisualLayout(layout);

        ImageWorkspace.AcceptVisualLayout(layout);
    }
}
