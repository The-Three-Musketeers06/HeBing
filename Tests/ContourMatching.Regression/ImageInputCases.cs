using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Models;
using AiVisionStudio.Services;
using AiVisionStudio.Views;
using OpenCvSharp;
using YamlDotNet.RepresentationModel;

public static class ImageInputCases
{
    public static int Run()
    {
        int count = 0;
        void Check(bool valid, string text) { if (!valid) throw new InvalidOperationException(text); count++; }
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (!app.Resources.Contains("Text")) app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Themes/Light.xaml", UriKind.Relative) });
        if (!app.Resources.Contains("ToolTextBox")) app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AiVisionStudio;component/Views/ToolEditorResources.xaml", UriKind.Relative) });
        DriverCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "driver_defs.json"));
        var directory = Path.Combine(AppContext.BaseDirectory, "case-output", "image-inputs"); Directory.CreateDirectory(directory);
        var fileA = Path.Combine(directory, "source-a.png"); var fileB = Path.Combine(directory, "source-b.png");
        using (var image = new Mat(30, 40, MatType.CV_8UC3, new Scalar(11, 21, 231))) Cv2.ImWrite(fileA, image);
        using (var image = new Mat(30, 40, MatType.CV_8UC3, new Scalar(99, 81, 41))) Cv2.ImWrite(fileB, image);
        var vm = new MainViewModel(Path.Combine(directory, "settings"));
        var oldArithmetic = OperatorRegistry.Get("Basic Arithmetic"); OperatorRegistry.Register(new BinaryProbe());
        Operation Add(string driver, string name) { vm.AddTool(new ToolNode { Label = driver, Cn = name }); return vm.SelectedOperation; }
        ParameterNode Input(string name = "IM In") => vm.AllParameterNodes.Single(node => node.Name == name && node.Path.Length == 1);
        void Choose(Operation source, string parameter = "IM Out", string input = "IM In")
        {
            var choice = vm.GetImageInputChoices(vm.SelectedOperation).Single(choice => choice.Reference.OperationId == source.Uuid && choice.Reference.OutputParameter == parameter);
            vm.SetImageInput(Input(input), choice);
        }
        Mat Snapshot(Operation operation)
        {
            Mat result = null;
            var source = vm.GetVisualSources().FirstOrDefault(source => source.Key.OperationId == operation.Uuid && source.Key.Kind == VisualResultKind.Image);
            if (source != null) vm.UseVisualSnapshot(source.Key, snapshot => result = snapshot.Image);
            return result;
        }
        try
        {
            var a = Add("LoadImage", "原图A"); vm.WriteParameterValue("File", "Type"); vm.WriteParameterValue(fileA, "File"); vm.WriteParameterValue(0, "IM Out");
            var b = Add("LoadImage", "原图B"); vm.WriteParameterValue("File", "Type"); vm.WriteParameterValue(fileB, "File"); vm.WriteParameterValue(0, "IM Out");
            var split = Add(ChannelSplitChannels.Driver, "通道拆分");
            Check(Input().IsImageInput && Input().Options.Count == 0, "输入图像必须使用名称选择器，不能继续显示数字选项");
            Check(!vm.AllParameterNodes.Any(node => node.Name == "IM Out R"), "输出编号参数不能被误当成输入来源");
            Check(vm.GetImageInputChoices(split).Count == 2 && vm.GetImageInputChoices(split).All(choice => !choice.Available), "运行前也应能配置前置图像来源，并标明尚未运行");
            Choose(a); Check(Input().ImageInputLabel.Contains("原图A / 图像"), "选择后必须显示控件名及输出名");
            var mean = Add("Basic Mean Filter", "绿色滤波"); vm.WriteParameterValue(4, "IM Out");
            var choices = vm.GetImageInputChoices(mean);
            Check(choices.Count(choice => choice.Reference.OperationId == split.Uuid) == 3 && choices.All(choice => choice.Reference.OperationId != mean.Uuid), "下拉菜单只能包含前置控件，通道拆分应提供三个子输出");
            Choose(split, "IM Out G");
            var menuMethod = typeof(ParameterEditor).GetMethod("BuildImageInputMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var editor = new ParameterEditor { DataContext = Input() };
            var menu = (ContextMenu)menuMethod.Invoke(editor, null);
            var groups = menu.Items.OfType<MenuItem>().Where(item => item.HasItems).ToArray();
            Check(groups.Length == 3 && groups[0].Header.ToString() == "原图A" && groups[2].Header.ToString() == "通道拆分", "输入菜单必须按控件名称分组");
            Check(groups[2].Items.Count == 3 && groups[2].Items.OfType<MenuItem>().Any(item => item.Header.ToString().Contains("绿色通道") && item.IsChecked), "二级菜单应列出通道并标记当前选择");
            Check(((Button)editor.FindName("ImageInputValue")).Visibility == Visibility.Visible && ((ComboBox)editor.FindName("EnumValue")).Visibility == Visibility.Collapsed, "参数界面必须真正切换为来源选择按钮");
            var original = Add("Basic Mean Filter", "原图滤波"); vm.WriteParameterValue(5, "IM Out"); Choose(a);
            var binary = Add("Basic Arithmetic", "双图处理"); vm.WriteParameterValue(6, "IM Out"); Choose(a); Choose(b, input: "IM In 2");
            vm.SetImageInput(Input("Mask Im In"), null);
            vm.RunAll();
            Check(vm.Operations.All(operation => operation.State == OpState.Ok), "命名图像引用应在真实执行链中正常运行，未启用的遮罩输入不应阻止运行");
            Check(vm.Images.Get(1).At<byte>(0, 0) == 231 && vm.Images.Get(2).At<byte>(0, 0) == 21, "通道拆分必须读取指定原图A，不能读取同样写IM0的后一个控件B");
            Check(vm.Images.Get(4).At<byte>(0, 0) == 21, "后续工具必须读取所选绿色通道");
            Check(vm.Images.Get(5).At<Vec3b>(0, 0) == new Vec3b(11, 21, 231), "后续工具必须能单独读取原图A的完整彩色图像");
            Check(vm.Images.Get(6).At<Vec3b>(0, 0) == new Vec3b(88, 60, 190), "两个输入来源都写IM0时，也必须同时读取各自正确图像");
            Check(vm.Images.Get(0).At<Vec3b>(0, 0) == new Vec3b(99, 81, 41) && !vm.Images.All.ContainsKey(32), "临时输入不能覆盖共享图像池，也不能留下临时编号");
            Check(Snapshot(a).At<Vec3b>(0, 0) == new Vec3b(11, 21, 231), "执行后不能修改或释放上游独立图像");
            vm.SelectedOperation = original;
            using (var selected = vm.CopySelectedInputImage()) Check(selected.At<Vec3b>(0, 0) == new Vec3b(11, 21, 231), "输入预览与框选必须读取所选控件图像，而非被覆盖的IM池");
            vm.RenameOperation(a, "采集原图");
            Check(Input().ImageInputLabel.Contains("采集原图"), "来源重命名应同步更新显示，不破坏UUID引用");
            vm.SelectedOperation = a; vm.WriteParameterValue(9, "IM Out"); vm.RunAll();
            Check(split.State == OpState.Ok && vm.Images.Get(4).At<byte>(0, 0) == 21 && binary.State == OpState.Ok, "上游输出编号改变后，命名引用必须仍跟随该控件的同一输出");
            vm.SelectedOperation = mean;
            var saved = ProjectFile.Serialize(vm.Programs);
            Check(saved.Contains("image_inputs") && saved.Contains(a.Uuid), "工程必须持久化来源身份而非只保存数字槽位");
            var project = Path.Combine(directory, "image-inputs.aivs"); File.WriteAllText(project, saved);
            var reopened = new MainViewModel(Path.Combine(directory, "reopened"));
            try
            {
                reopened.OpenProject(project); reopened.SelectedOperation = reopened.Operations.Single(operation => operation.Uuid == mean.Uuid);
                Check(reopened.AllParameterNodes.Single(node => node.Name == "IM In").ImageInputLabel.Contains("绿色通道"), "重新打开工程应恢复具体通道选择");
                reopened.RunAll(); Check(reopened.Images.Get(4).At<byte>(0, 0) == 21 && reopened.Operations.All(operation => operation.State == OpState.Ok), "重新打开后执行仍应读取正确图像");
            }
            finally { reopened.Dispose(); }
            vm.SelectedOperation = mean; var oldResult = Snapshot(mean); vm.SetImageInput(Input(), null);
            Check(oldResult.IsDisposed && !vm.Images.Has(4), "清除输入选择时必须清理该控件旧结果");
            vm.RunAll(); Check(mean.State == OpState.Fail && !vm.Images.Has(4), "清除选择后必须明确失败，不能悄悄使用旧数字来源");
            Choose(split, "IM Out G"); vm.RunAll();
            Check(mean.State == OpState.Ok && vm.Images.Get(4).At<byte>(0, 0) == 21, "重新选择有效通道后应恢复运行");
            vm.SelectedOperation = split; vm.WriteParameterValue("HSV", "Split Mode"); vm.RunAll();
            Check(mean.State == OpState.Fail && !vm.Images.Has(4), "所选通道被替换后不能误用新模式的同编号图像");
            vm.SelectedOperation = mean; Choose(split, "IM Out S"); vm.RunAll(); Check(mean.State == OpState.Ok, "选择新模式的通道后应恢复处理");
            vm.MoveOperation(a, vm.Operations.Count - 1); vm.RunAll();
            Check(split.State == OpState.Fail && split.ErrorMessage.Contains("之前"), "前置控件移到当前控件后面时，必须拦截循环或逆序引用");
            vm.MoveOperation(a, 0); vm.RunAll(); Check(split.State == OpState.Ok, "恢复正确顺序后应恢复运行");
            vm.SelectedOperation = original; vm.RemoveOperation(a);
            Check(Input().ImageInputLabel.Contains("失效") && !vm.Images.Has(5), "删除来源后必须显示失效并清除依赖结果，不能自动改用其他控件");
            Check(File.Exists(fileA) && File.Exists(fileB), "清理图像引用和处理结果不能删除磁盘照片");
            vm.Undo(); vm.RunAll(); Check(vm.Operations.Single(operation => operation.Uuid == original.Uuid).State == OpState.Ok, "撤销删除后应恢复来源配置并重新生成结果");
        }
        finally { vm.Dispose(); if (oldArithmetic != null) OperatorRegistry.Register(oldArithmetic); }
        Console.WriteLine($"Named image input cases passed: {count}");
        return count;
    }

    private sealed class BinaryProbe : IOperator
    {
        public string Driver => "Basic Arithmetic";
        public bool Run(OpContext context, ParamBag parameters)
        {
            var a = context.Images.Get(parameters.Int("IM In")); var b = context.Images.Get(parameters.Int("IM In 2"));
            var result = new Mat(); Cv2.Absdiff(a, b, result); context.SetImage(parameters.Int("IM Out"), result); return true;
        }
    }
}
