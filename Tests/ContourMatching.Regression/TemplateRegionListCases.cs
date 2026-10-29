using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AiVisionStudio.Algorithm.Impl;
using AiVisionStudio.Models;
using AiVisionStudio.Services;
using AiVisionStudio.Views;
using OpenCvSharp;

internal static partial class TemplateMaskEditingCases
{
    public static int RunRegionList()
    {
        int count = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); count++; }
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".build", "region-list"));
        Directory.CreateDirectory(output);
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.Resources.MergedDictionaries.Count == 0)
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Themes/Light.xaml", UriKind.Relative) });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Views/ToolEditorResources.xaml", UriKind.Relative) });
        }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        DriverCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "driver_defs.json"));
        using var image = new Mat(180, 240, MatType.CV_8UC1, new Scalar(20));
        Cv2.Rectangle(image, new OpenCvSharp.Rect(55, 55, 55, 70), Scalar.White, 5);
        Cv2.Circle(image, new OpenCvSharp.Point(155, 70), 15, new Scalar(180), 4);
        Cv2.Line(image, new OpenCvSharp.Point(130, 105), new OpenCvSharp.Point(185, 125), new Scalar(210), 5);
        using var region = new Mat(image.Size(), MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(region, new OpenCvSharp.Rect(30, 20, 181, 141), Scalar.White, -1);
        var vm = new MainViewModel(Path.Combine(output, "settings"));
        try
        {
            vm.AddTool(new ToolNode { Label = ContourTemplateEngine.ShapeDriver, Cn = "基于灰度匹配" });
            vm.Images.Set(0, image.Clone());
            vm.WriteParameterValue(Rectangle(120, 90, 180, 140), "Training Region");
            var parent = new ParamsWindow(vm);
            var editor = (TemplateConfigurationWindow)Call(parent, "CreateShapeTemplateConfiguration");
            try
            {
                Render(editor, output, "initial.png", 1180, 760);
                var list = Field<ListBox>(editor, "TemplateResults");
                var canvas = Field<ImageView>(editor, "CanvasView");
                var tools = Field<Dictionary<string, Button>>(editor, "_drawingTools");
                string Label(int index) => ((StackPanel)((ListBoxItem)list.Items[index]).Content).Children.OfType<TextBlock>().First().Text;
                void Select(int index) { list.SelectedIndex = -1; list.SelectedIndex = index; }
                void Draft(string type, Dictionary<string, object> shape)
                {
                    var field = canvas.GetType().GetField("_regionDraft", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    field.SetValue(canvas, field.FieldType.GetMethod("Read", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, [type, shape]));
                    Call(canvas, "UpdateRegionEditor");
                }
                void Add(string type, Dictionary<string, object> shape)
                {
                    Call(editor, "BeginMaskRegion", type); Draft(type, shape); canvas.CompleteRegionEdit();
                }
                Check(list.Items.Count == 1 && Label(0).Contains("模板区域"), "未训练时也必须列出已有模板区域");
                Call(editor, "BeginRegion", "rect2d", false);
                Check(Equals(tools["template:rect2d"].Tag, "SelectedTool"), "矩形工具进入绘制模式后必须持续加深显示");
                Call(editor, "BeginRegion", "circle2d", false);
                Check(tools["template:rect2d"].Tag == null && Equals(tools["template:circle2d"].Tag, "SelectedTool"), "切换工具后只能加深当前工具");
                canvas.CancelRegionEdit();
                Check(tools.Values.All(button => button.Tag == null), "退出编辑时不能保留错误的工具选中状态");
                Add("rect2d", Rectangle(50, 40, 12, 10));
                Check(list.Items.Count == 2, "未应用的掩膜也必须出现在区域列表");
                Call(editor, "CancelMask");
                Check(list.Items.Count == 1, "取消掩膜必须从列表移除取消的区域");
                var circle = Map(new { Type = "circle2d", circle2d = new { Center = new { X = 55d, Y = 37d }, Radius = 7d } });
                var polygon = Map(new { Type = "polygon2d", polygon2d = new { Points = new[] { new { X = 180d, Y = 45d }, new { X = 194d, Y = 45d }, new { X = 190d, Y = 60d } } } });
                Add("circle2d", circle); Add("rect2d", Rectangle(150, 130, 20, 12)); Add("polygon2d", polygon);
                Check(list.Items.Count == 4 && Label(1).Contains("圆形掩膜") && Label(2).Contains("矩形掩膜") && Label(3).Contains("多边形掩膜"), "列表必须按创建顺序显示几何掩膜");
                Call(editor, "UndoMask"); Check(list.Items.Count == 3, "撤销必须同步移除最后创建的区域");
                Call(editor, "RedoMask"); Check(list.Items.Count == 4 && Label(3).Contains("多边形掩膜"), "重做必须恢复原来的区域顺序");
                Call(editor, "BeginPaint", false);
                Check(Equals(tools["mask:brush"].Tag, "SelectedTool"), "画笔模式必须加深画笔工具");
                PaintStroke(editor, new System.Windows.Point(75, 70), new System.Windows.Point(90, 70));
                Call(editor, "BeginPaint", true);
                Check(Equals(tools["mask:erase"].Tag, "SelectedTool") && tools["mask:brush"].Tag == null, "橡皮擦模式必须替换画笔的选中状态");
                PaintStroke(editor, new System.Windows.Point(75, 70), new System.Windows.Point(90, 70));
                Check(list.Items.Count == 4, "每次画笔和橡皮擦都不能增加区域列表条目");
                Select(1);
                Check(canvas.IsRegionEditing && Equals(tools["mask:circle2d"].Tag, "SelectedTool"), "点击圆形条目必须进入对应圆形掩膜的调整模式");
                Draft("circle2d", Map(new { Type = "circle2d", circle2d = new { Center = new { X = 70d, Y = 38d }, Radius = 8d } }));
                canvas.CompleteRegionEdit();
                using (var mask = Mask(editor, region, true))
                    Check(mask.At<byte>(38, 70) == 255 && mask.At<byte>(37, 55) == 0 && list.Items.Count == 4, "列表修改必须替换原来的圆形掩膜，而不是叠加新区域");
                Select(2); Draft("rect2d", Rectangle(150, 135, 18, 10)); canvas.CompleteRegionEdit();
                Select(3); Draft("polygon2d", Map(new { Type = "polygon2d", polygon2d = new { Points = new[] { new { X = 178d, Y = 42d }, new { X = 192d, Y = 42d }, new { X = 186d, Y = 57d } } } })); canvas.CompleteRegionEdit();
                Check(list.Items.Count == 4 && Label(1).Contains("圆形掩膜") && Label(2).Contains("矩形掩膜") && Label(3).Contains("多边形掩膜"), "修改多种掩膜不能改变列表顺序或复制条目");
                Select(0);
                Check(canvas.IsRegionEditing && Field<Dictionary<string, object>>(editor, "_maskWorking") == null, "从列表选择模板区域必须保存待应用的掩膜草稿并进入模板调整");
                Draft("rect2d", Rectangle(118, 90, 176, 140)); canvas.CompleteRegionEdit();
                var template = (Dictionary<string, object>)Field<Dictionary<string, object>>(editor, "_draft")["Training Region"];
                Check(JsonSerializer.Serialize(template).Contains("118") && list.Items.Count == 4 && vm.ReadParameterValue(ContourTemplateStorage.ModelKey) == null, "列表修改模板区域只能修改训练草稿，不能提前写入工程模板");
                Call(editor, "BeginMaskRegion", "rect2d"); Draft("rect2d", Rectangle(120, 145, 12, 6));
                Check(list.Items.Count == 5, "已画出但尚未按 Enter 的有效区域也必须出现在列表");
                Select(4);
                Check(canvas.IsRegionEditing && list.Items.Count == 5, "点击新画出的区域必须编辑同一条目，不能重复添加");
                canvas.CompleteRegionEdit(); Call(editor, "ApplyMask");
                Render(editor, output, "editable-regions.png", 1180, 760);
                Call(editor, "OnTrain", null, new RoutedEventArgs()); Wait(editor);
                Check(Field<ContourTemplateModel>(editor, "_model") != null && Field<bool>(editor, "_regionListLocked") && list.Items.Count == 5, "训练成功后必须保留全部区域列表并锁定编辑");
                string trained = JsonSerializer.Serialize(Field<Dictionary<string, object>>(editor, "_draft"));
                Select(1); Select(0);
                Check(!canvas.IsRegionEditing && JsonSerializer.Serialize(Field<Dictionary<string, object>>(editor, "_draft")) == trained, "训练后点击任何列表条目都不能修改区域或显示编辑手柄");
                Render(editor, output, "trained-regions.png", 1180, 760);
                string project = Path.Combine(output, "regions.aivs");
                Check(ProjectFile.SaveAll(project, vm.Programs), "区域顺序与掩膜元数据必须能够保存到工程");
                var reopened = new MainViewModel(Path.Combine(output, "reopened"));
                try
                {
                    reopened.OpenProject(project); reopened.SelectedOperation = reopened.Operations.Single(); reopened.Images.Set(0, image.Clone());
                    var restoredParent = new ParamsWindow(reopened);
                    var restoredEditor = (TemplateConfigurationWindow)Call(restoredParent, "CreateShapeTemplateConfiguration");
                    try { Check(Field<ListBox>(restoredEditor, "TemplateResults").Items.Count == 5 && Field<bool>(restoredEditor, "_regionListLocked"), "工程重开必须保留几何区域顺序、排除画笔笔迹并锁定已训练区域"); }
                    finally { restoredEditor.Close(); restoredParent.Close(); }
                }
                finally { reopened.Dispose(); }
                Call(editor, "LoadImage", image);
                Check(!Field<bool>(editor, "_regionListLocked") && list.Items.Count == 0, "重新选择图像必须开始新的可编辑区域草稿");
            }
            finally { editor.Close(); parent.Close(); }
        }
        finally { vm.Dispose(); }
        Console.WriteLine($"Template region list and tool selection cases passed: {count}");
        return count;
    }
}
