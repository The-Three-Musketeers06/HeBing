using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AiVisionStudio.Models;
using AiVisionStudio.Services;
using AiVisionStudio.Views;
using Point = System.Windows.Point;

public static class RunListDragCases
{
    public static int Run()
    {
        int count = 0;
        void Check(bool valid, string text) { if (!valid) throw new InvalidOperationException(text); count++; }
        DriverCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "driver_defs.json"));
        var vm = new MainViewModel(Path.Combine(AppContext.BaseDirectory, "case-output", "run-drag-settings"));
        try
        {
            for (int i = 0; i < 4; i++) vm.AddTool(new ToolNode { Label = "Basic Mean Filter", Cn = "算子" + i });
            var original = vm.Operations.ToArray();
            var undo = typeof(MainViewModel).GetField("_undo", BindingFlags.Instance | BindingFlags.NonPublic)!;
            int UndoCount() { var stack = undo.GetValue(vm); return (int)stack.GetType().GetProperty("Count")!.GetValue(stack); }
            for (int from = 0; from < 4; from++)
                for (int insertion = 0; insertion <= 4; insertion++)
                {
                    for (int i = 0; i < 4; i++) vm.MoveOperation(original[i], i);
                    int undoBefore = UndoCount(), logsBefore = vm.Logs.Count;
                    var expected = original.ToList(); var moving = original[from];
                    int destination = insertion > from ? insertion - 1 : insertion;
                    expected.RemoveAt(from); expected.Insert(destination, moving);
                    bool changed = vm.MoveOperationToInsertion(moving, insertion);
                    Check(vm.Operations.SequenceEqual(expected) && vm.SelectedProgram.Operations.SequenceEqual(expected), "上下拖动的插入位置必须正确，且显示顺序与实际执行顺序一致");
                    Check(vm.Operations.Select(op => op.Index).SequenceEqual(Enumerable.Range(0, 4)), "移动后编号必须连续");
                    if (destination == from)
                        Check(!changed && UndoCount() == undoBefore && vm.Logs.Count == logsBefore, "原行上下半部放下都不能换位，也不能产生修改或移动记录");
                    else
                        Check(changed && vm.Logs.Count == logsBefore + 1 && vm.Logs.Any(log => log.Message.Contains("拖动调整算子顺序")), "真实移动应有可追溯的日志");
                }
            for (int i = 0; i < 4; i++) vm.MoveOperation(original[i], i);
            Check(!vm.MoveOperationToInsertion(original[1], 2) && vm.Operations[1] == original[1] && vm.Operations[2] == original[2], "复现位置1在原行下半部放下的场景，必须不再与位置2交换");
            Check(vm.MoveOperationToInsertion(original[1], 3) && vm.Operations[2] == original[1], "主动拖到下一行之后仍应能够正确排序");
            var order = vm.Operations.Select(op => op.Uuid).ToArray();
            var serialized = ProjectFile.Serialize(vm.Programs);
            Check(ProjectFile.Parse(serialized).Single(program => program.Name == vm.SelectedProgram.Name).Operations.Select(op => op.Uuid).SequenceEqual(order), "工程保存与重新解析不能自动调整算子顺序");
            vm.Undo();
            Check(vm.Operations.Select(op => op.Uuid).SequenceEqual(original.Select(op => op.Uuid)), "真实拖动应可使用撤销恢复");
            vm.Redo(); Check(vm.Operations.Select(op => op.Uuid).SequenceEqual(order), "重做应恢复主动调整后的顺序");
            var current = vm.SelectedProgram; var other = vm.CreateProgram("流程二"); vm.SelectedProgram = other; vm.SelectedProgram = current;
            Check(vm.Operations.Select(op => op.Uuid).SequenceEqual(order), "切换流程后仍应恢复原来的算子顺序");
            Check(!vm.MoveOperationToInsertion(new Operation(), 0) && !vm.MoveOperationToInsertion(null, 0), "非当前列表中的控件不能参与排序");
        }
        finally { vm.Dispose(); }

        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (!app.Resources.Contains("Text")) app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Themes/Light.xaml", UriKind.Relative) });
        if (!app.Resources.Contains("ToolTextBox")) app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Views/ToolEditorResources.xaml", UriKind.Relative) });
        var window = new MainWindow();
        window.Closing -= (CancelEventHandler)typeof(MainWindow).GetMethod("OnWindowClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate(typeof(CancelEventHandler), window);
        var model = (MainViewModel)typeof(MainWindow).GetField("_vm", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
        try
        {
            for (int i = 0; i < 3; i++) model.AddTool(new ToolNode { Label = "Basic Mean Filter", Cn = "行" + i });
            var root = (FrameworkElement)window.Content; root.Measure(new Size(1280, 800)); root.Arrange(new Rect(0, 0, 1280, 800)); root.UpdateLayout();
            var list = (ListBox)window.FindName("RunList"); list.ApplyTemplate(); list.UpdateLayout();
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var origin = typeof(MainWindow).GetMethod("RunDragOperationFrom", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Operation Origin(DependencyObject source) => (Operation)origin.Invoke(window, [source]);
            Check(ReferenceEquals(Origin(item), model.Operations[0]), "拖动源必须来自实际按下的行，不能使用此前选中的其他算子");
            Check(Origin(list) == null && Origin(new ScrollBar()) == null && Origin(new Thumb()) == null, "空白和滚动条不能启动算子拖动");
            var grid = Descendants<Grid>(item).First(); var button = new Button(); var text = new TextBox();
            grid.Children.Add(button); grid.Children.Add(text);
            Check(Origin(button) == null && Origin(text) == null, "行内按钮和输入框的操作不能启动排序");
            grid.Children.Remove(button); grid.Children.Remove(text);
            var threshold = typeof(MainWindow).GetMethod("RunDragThresholdReached", BindingFlags.Static | BindingFlags.NonPublic)!;
            bool Moved(Point point) => (bool)threshold.Invoke(null, [new Point(0, 0), point]);
            Check(!Moved(new Point(4, 4)) && !Moved(new Point(6, 2)), "普通点击中的小幅滑动不能触发拖动");
            Check(Moved(new Point(Math.Max(8, SystemParameters.MinimumHorizontalDragDistance) + 1, 0))
                && Moved(new Point(0, Math.Max(8, SystemParameters.MinimumVerticalDragDistance) + 1)), "超过阈值的主动拖动仍应可用");
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = item };
            typeof(MainWindow).GetMethod("OnRunPreviewMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [list, down]);
            var armed = typeof(MainWindow).GetField("_runDragOperation", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Check(ReferenceEquals(armed.GetValue(window), model.Operations[0]), "鼠标按下应记录原始行身份");
            typeof(MainWindow).GetMethod("OnRunPreviewMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [list, down]);
            Check(armed.GetValue(window) == null, "松开鼠标后必须清除拖动状态，防止下一次操作沿用旧起点");
        }
        finally { window.Close(); }
        Console.WriteLine($"Run list drag cases passed: {count}");
        return count;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
