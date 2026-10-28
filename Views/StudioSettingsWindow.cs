using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AiVisionStudio.Services;

namespace AiVisionStudio.Views;

public sealed class StudioSettingsWindow : Window
{
    public StudioSettingsWindow()
    {
        Title="系统参数 · ES-Vision-Studio";Width=700;Height=660;MinWidth=500;MinHeight=400;FontSize=14;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty,"Bg");SetResourceReference(ForegroundProperty,"Text");WindowScreenBehavior.Attach(this);
        var draft=StudioSettings.Current.Copy();DataContext=draft;
        var root=new DockPanel{Margin=new(20)};root.SetResourceReference(Panel.BackgroundProperty,"Bg");Content=root;
        var bottom=new StackPanel{Margin=new(0,12,0,0)};DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
        var error=new TextBlock{TextWrapping=TextWrapping.Wrap};bottom.Children.Add(error);
        var save=new Button{Content="保存系统参数",Padding=new(20,8,20,8),HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,10,0,0)};bottom.Children.Add(save);
        var form=new StackPanel();root.Children.Add(new ScrollViewer{Content=form,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        form.Children.Add(new TextBlock{Text="启动与运行",FontSize=22,FontWeight=FontWeights.SemiBold,Margin=new(0,0,0,16)});
        void Check(string label,string key){var box=new CheckBox{Content=label,Margin=new(0,8,0,8)};box.SetBinding(CheckBox.IsCheckedProperty,new Binding(key){Mode=BindingMode.TwoWay});form.Children.Add(box);}
        TextBox Field(string label,string key){form.Children.Add(new TextBlock{Text=label,Margin=new(0,8,0,4)});var box=new TextBox{MinHeight=30};box.SetBinding(TextBox.TextProperty,new Binding(key){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged,ValidatesOnExceptions=true});form.Children.Add(box);return box;}
        Check("启动时自动加载工程",nameof(StudioSettings.LoadProjectOnStartup));var path=Field("工程地址",nameof(StudioSettings.ProjectPath));
        var browse=new Button{Content="选择工程…",HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,4,0,4)};form.Children.Add(browse);browse.Click+=(_,_)=>{var dialog=new Microsoft.Win32.OpenFileDialog{Filter="视觉工程 (*.aivs)|*.aivs"};if(dialog.ShowDialog(this)==true)path.Text=dialog.FileName;};
        Field("默认程序名称（留空使用工程默认程序）",nameof(StudioSettings.ProgramName));
        Check("启动时自动打开 HMI",nameof(StudioSettings.OpenHmiOnStartup));Check("加载后自动循环运行",nameof(StudioSettings.RunOnStartup));Field("自动运行延时（毫秒）",nameof(StudioSettings.StartupDelayMs));
        Check("极速模式（暂停主窗口图像刷新）",nameof(StudioSettings.FastMode));Field("结果表格小数位数（3–6，不改变计算精度）",nameof(StudioSettings.DecimalPlaces));
        save.Click+=(_,_)=>{
            try{foreach(var child in form.Children.OfType<TextBox>()){child.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();if(Validation.GetHasError(child))throw new ArgumentException("请修正标红的参数");}draft.Save();DialogResult=true;}
            catch(Exception ex)when(ex is ArgumentException or System.IO.IOException or UnauthorizedAccessException){error.Text=ex.Message;}
        };
    }
}
