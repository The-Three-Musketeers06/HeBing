using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Models;
using AiVisionStudio.Services;
using AiVisionStudio.Views;
using OpenCvSharp;

public static class VisualOutputCases
{
    public static int Run()
    {
        int count = 0;
        void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); count++; }
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (!app.Resources.Contains("Text")) app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Themes/Light.xaml", UriKind.Relative) });
        if (!app.Resources.Contains("ToolTextBox")) app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Views/ToolEditorResources.xaml", UriKind.Relative) });
        DriverCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "driver_defs.json"));
        OperatorRegistry.Register(new NoVisual());
        var writer = new MultiVisual(); OperatorRegistry.Register(writer);
        var standalone = new MainViewModel(Path.Combine(AppContext.BaseDirectory, "case-output", "visual-output-settings"));
        try
        {
            standalone.AddTool(new ToolNode { Label = writer.Driver, Cn = "多结果测试" });
            standalone.RunAll();
            var outputs = standalone.GetVisualSources();
            Check(outputs.Count == 3 && outputs.Select(info => info.Key).Distinct().Count() == 3, "普通多输出控件也必须保留三个独立结果");
            Check(outputs.All(info => info.OutputName.StartsWith("图像")) && outputs.Select(info => info.OutputName).Distinct().Count() == 3, "通用输出应提供可区分的中文名称");
            var images = new List<Mat>(); foreach (var info in outputs) standalone.UseVisualSnapshot(info.Key, snapshot => images.Add(snapshot.Image));
            standalone.RunAll();
            Check(images.All(image => image.IsDisposed) && standalone.GetVisualSources().Count == 3, "重复运行必须替换结果而不能累积图层");
            images.Clear(); foreach (var info in standalone.GetVisualSources()) standalone.UseVisualSnapshot(info.Key, snapshot => images.Add(snapshot.Image));
            writer.Count = 1; standalone.RunAll();
            Check(standalone.GetVisualSources().Count == 1 && images.All(image => image.IsDisposed), "输出数量减少时必须移除并释放过期快照");
            Check(!standalone.Images.Has(7) && !standalone.Images.Has(8) && standalone.Images.Has(6), "过期输出的运行图像池也必须清除");
            writer.Count = 0; standalone.RunAll();
            Check(standalone.GetVisualSources().Count == 0 && !standalone.Images.Has(6), "没有输出时不能显示上一轮结果");
            writer.Count = 3; writer.Mixed = true; standalone.RunAll();
            outputs = standalone.GetVisualSources();
            Check(outputs.Count == 3 && outputs.Select(info => info.Key.Kind).Distinct().Count() == 3, "二维图像、点云、轮廓混合输出必须分别保留");
            Check(outputs.Any(info => info.OutputName.StartsWith("点云")) && outputs.Any(info => info.OutputName.StartsWith("轮廓")), "混合输出的名称应反映实际结果类型");
            standalone.RemoveOperation(standalone.SelectedOperation);
            Check(standalone.GetVisualSources().Count == 0 && standalone.Images.All.Count == 0 && standalone.Clouds.Get(7) == null && !standalone.Profiles.ContainsKey(8), "删除多输出控件必须清除全部类型的结果");
        }
        finally { standalone.Dispose(); }

        var main = new MainWindow();
        main.Closing -= (CancelEventHandler)typeof(MainWindow).GetMethod("OnWindowClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate(typeof(CancelEventHandler), main);
        var vm = (MainViewModel)typeof(MainWindow).GetField("_vm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
        var workspace = (ImageWorkspace)main.FindName("ImageWorkspace");
        var refresh = typeof(MainWindow).GetMethod("RefreshImageViews", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var build = typeof(MainWindow).GetMethod("BuildVisualSourcesMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ContextMenu Menu(int? paneId) => (ContextMenu)build.Invoke(main, new object[] { main, paneId });
        MenuItem Group(ContextMenu menu, int index) => (MenuItem)menu.Items[index];
        MenuItem Output(ContextMenu menu, string key) => menu.Items.OfType<MenuItem>().SelectMany(group => group.Items.OfType<MenuItem>()).Single(item => item.Tag is VisualSourceKey source && source.OutputKey == key);
        void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        string Title(int paneId) => ((Dictionary<int, Button>)typeof(ImageWorkspace).GetField("_paneTitles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workspace))[paneId].Content.ToString();
        try
        {
            vm.AddTool(new ToolNode { Label = new NoVisual().Driver, Cn = "纯数值控件" });
            vm.AddTool(new ToolNode { Label = ChannelSplitChannels.Driver, Cn = "通道拆分" }); var split = vm.SelectedOperation;
            vm.AddTool(new ToolNode { Label = "Basic Mean Filter", Cn = "滤波" }); var filter = vm.SelectedOperation;
            vm.WriteParameterValue(1, "IM In"); vm.WriteParameterValue(4, "IM Out");
            vm.Images.Set(0, new Mat(30, 40, MatType.CV_8UC3, new Scalar(10, 20, 30))); vm.RunAll(); refresh.Invoke(main, [true]);
            Check(split.State == OpState.Ok && filter.State == OpState.Ok, "分组选择应使用真实运行产生的结果");
            Check(workspace.TryGetSingleOpenPane(out int first), "初始只应有一个画布");
            var menu = Menu(first);
            Check(menu.Items.Count == 2 && Group(menu, 0).Header.ToString() == "IM1 · 通道拆分" && Group(menu, 1).Header.ToString() == "IM2 · 滤波", "IM编号必须对应控件序号，不能由输出数量或有图像控件数量决定");
            Check(Group(menu, 0).Items.Count == 3 && Group(menu, 1).Items.Count == 1, "每个控件只占一个主菜单项，输出放在二级菜单");
            Check(Group(menu, 0).Items.OfType<MenuItem>().Select(item => item.Header.ToString()).All(header => !header.StartsWith("IM")), "二级菜单不能为通道重新分配IM编号");
            Check(!Output(menu, "channel:R").IsEnabled && Output(menu, "channel:R").IsChecked && Output(menu, "channel:G").IsEnabled, "已显示的具体输出应禁用，其他通道仍可选择");
            int paneCount = workspace.Viewers.Count; Click(Output(menu, "channel:B"));
            Check(workspace.Viewers.Count == paneCount && workspace.TryGetPaneSource(first, out var selected) && selected.OutputKey == "channel:B", "切换输出只应替换当前画布");
            Check(Title(first).StartsWith("IM1 · 通道拆分") && Title(first).Contains("蓝色通道"), "画布标题应同时显示控件编号及选中的输出");
            typeof(MainWindow).GetMethod("FollowOperationOnSinglePane", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [split]);
            Check(workspace.TryGetPaneSource(first, out selected) && selected.OutputKey == "channel:B", "再次选择同一控件不应重置已选通道");
            var stale = Menu(first); var staleGreen = Output(stale, "channel:G");
            Click(Output(Menu(null), "channel:G"));
            var second = workspace.Viewers.Keys.Single(id => id != first);
            Check(workspace.Viewers.Count == paneCount + 1 && workspace.TryGetPaneSource(second, out selected) && selected.OutputKey == "channel:G", "同一控件的不同输出应能在两个画布中对比");
            Check(Title(first).StartsWith("IM1 ·") && Title(second).StartsWith("IM1 ·"), "同一控件的多个画布应共用IM编号");
            var duplicate = Menu(first);
            Check(!Output(duplicate, "channel:G").IsEnabled && Output(duplicate, "channel:G").ToolTip.ToString().Contains("已经有相同图层"), "其他画布占用的同一输出必须禁用并说明原因");
            Click(staleGreen);
            Check(workspace.Viewers.Count == paneCount + 1 && workspace.TryGetPaneSource(first, out selected) && selected.OutputKey == "channel:B" && vm.Status.Contains("相同"), "过期菜单点击也不能绕过重复输出检查");
            var snapshotKey = selected;
            vm.RunAll(); refresh.Invoke(main, [true]);
            Check(workspace.TryGetPaneSource(first, out selected) && selected == snapshotKey, "重新运行应保留当前通道选择");
            vm.SelectedOperation = split; vm.WriteParameterValue("HSV", "Split Mode"); vm.RunAll(); refresh.Invoke(main, [true]);
            workspace.TryGetPaneSource(first, out var sourceA); workspace.TryGetPaneSource(second, out var sourceB);
            Check(sourceA != null && sourceB != null && sourceA.OperationId == split.Uuid && sourceB.OperationId == split.Uuid && sourceA != sourceB && new[] { "channel:H", "channel:S", "channel:V" }.Contains(sourceA.OutputKey), "模式变化后应在原画布内选择可用输出，不能退到其他控件或重复显示");
            menu = Menu(null);
            Check(menu.Items.Count == 2 && Group(menu, 0).Items.Count == 3 && Group(menu, 1).Header.ToString().StartsWith("IM2 ·"), "切换模式后仍然只能有一个通道拆分主菜单项");
            vm.RenameOperation(split, "颜色检查"); refresh.Invoke(main, [true]);
            Check(Group(Menu(null), 0).Header.ToString() == "IM1 · 颜色检查" && Title(first).Contains("颜色检查"), "控件重命名必须同步更新分组和画布标题");
            var exported = workspace.ExportVisualLayout(); var restored = new ImageWorkspace(); restored.SetLayout(exported);
            Check(restored.TryGetPaneSource(first, out selected) && workspace.TryGetPaneSource(first, out var original) && selected == original, "布局必须保存控件身份和具体输出，而不只保存IM编号");
            workspace.ClosePane(second); refresh.Invoke(main, [true]);
            Check(workspace.TryGetSingleOpenPane(out _) && Output(Menu(null), sourceB.OutputKey).IsEnabled, "关闭画布后该输出应立即重新可选");
            vm.RemoveOperation(split); refresh.Invoke(main, [true]);
            Check(vm.GetVisualSources().Count == 0 && Menu(null).Items.Count == 1 && !Group(Menu(null), 0).IsEnabled, "删除控件后菜单及其全部输出必须清除");
        }
        finally { main.Close(); }
        Console.WriteLine($"Grouped visual output cases passed: {count}");
        return count;
    }

    private sealed class NoVisual : IOperator
    {
        public string Driver => "__VisualOutput_NoImage";
        public bool Run(OpContext context, ParamBag parameters) { context.Vars.Set("Count", 1); return true; }
    }

    private sealed class MultiVisual : IOperator
    {
        public string Driver => "__VisualOutput_Multi";
        public int Count { get; set; } = 3;
        public bool Mixed { get; set; }
        public bool Run(OpContext context, ParamBag parameters)
        {
            for (int i = 0; i < Count; i++)
            {
                if (Mixed && i == 1) { var cloud = new PointCloud(); cloud.Add(1, 2, 3); context.Clouds.Set(7, cloud); }
                else if (Mixed && i == 2) context.Profiles[8] = new ProfileData([1, 2, 3], [4, 5, 6]);
                else context.SetImage(6 + i, new Mat(10, 12, MatType.CV_8UC1, new Scalar(20 * (i + 1))));
            }
            return true;
        }
    }
}
