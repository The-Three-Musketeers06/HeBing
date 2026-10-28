using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AiVisionStudio.Services;

namespace AiVisionStudio.Views;

public partial class MainWindow
{
    private void OnManual(object sender,RoutedEventArgs e){try{ManualCatalog.Open(ManualCatalog.UrlFor(_vm.SelectedOperation?.Driver));}catch(Exception ex){_vm.Status=ex.Message;}}
    private void OnManualAudit(object sender,RoutedEventArgs e)=>new ManualAuditWindow{Owner=this}.Show();
    private void OnPause(object sender,RoutedEventArgs e)=>_vm.RequestPause();
    private void OnClearResults(object sender,RoutedEventArgs e){if(_vm.CanEdit)_vm.ClearHmiResults();}
    private void OnSystemSettings(object sender,RoutedEventArgs e)
    {if(new StudioSettingsWindow{Owner=this}.ShowDialog()==true){_vm.RefreshGlobalVars();RefreshImageViews();_vm.Status="系统参数已保存；自动启动选项在下次启动生效";}}
    private DeviceConfigWindow _devicesWindow;
    private bool? _compactLayout;
    private void OnDevices(object sender,RoutedEventArgs e)
    {
        if(!_vm.CanEdit)return;
        if(_devicesWindow!=null){_devicesWindow.Activate();return;}
        _devicesWindow=new(_vm){Owner=this};_devicesWindow.Closed+=(_,_)=>_devicesWindow=null;_devicesWindow.Show();
    }
    private void ManageProgram(Action action)
    {try{if(!_vm.CanEdit)return;action();SelectProgramInTree();}catch(Exception ex){_vm.Status=ex.Message;MessageBox.Show(this,ex.Message,"程序管理",MessageBoxButton.OK,MessageBoxImage.Information);}}
    private void OnNewProgram(object sender,RoutedEventArgs e)=>ManageProgram(()=>{var name=ProgramNameDialog.Ask(this,"新建程序","程序"+(_vm.Programs.Count+1));if(name!=null)_vm.CreateProgram(name);});
    private void OnRenameProgram(object sender,RoutedEventArgs e)=>ManageProgram(()=>{if(_vm.SelectedProgram==null)return;var name=ProgramNameDialog.Ask(this,"重命名程序",_vm.SelectedProgram.Name);if(name!=null)_vm.RenameProgram(_vm.SelectedProgram,name);});
    private void OnDefaultProgram(object sender,RoutedEventArgs e)=>ManageProgram(()=>_vm.SetDefaultProgram(_vm.SelectedProgram));
    private void OnDeleteProgram(object sender,RoutedEventArgs e)=>ManageProgram(()=>{
        if(_vm.SelectedProgram==null)return;
        if(MessageBox.Show(this,$"删除程序“{_vm.SelectedProgram.Name}”及其 {_vm.SelectedProgram.Operations.Count} 个算子？可通过撤销恢复。","删除程序",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)_vm.DeleteProgram(_vm.SelectedProgram);
    });
    private void OnProgramRightClick(object sender,MouseButtonEventArgs e){if(sender is TreeViewItem item){item.IsSelected=true;e.Handled=true;}}
    private void SelectProgramInTree()=>Dispatcher.BeginInvoke(new Action(()=>{
        foreach(var program in _vm.Programs)if(ProgramTree.ItemContainerGenerator.ContainerFromItem(program) is TreeViewItem item)item.IsSelected=ReferenceEquals(program,_vm.SelectedProgram);
    }));
    private void UpdateResponsiveLayout()
    {
        if(WorkspaceGrid==null)return;var compact=ActualWidth<1050;
        ToolbarStatus.Visibility=compact?Visibility.Collapsed:Visibility.Visible;
        CaptionTitle.Visibility=compact?Visibility.Collapsed:Visibility.Visible;
        LeftColumn.MinWidth=ProjectPanelMenu.IsChecked||ToolPanelMenu.IsChecked?compact?165:185:0;
        RunColumn.MinWidth=RunPanelMenu.IsChecked?compact?190:210:0;
        ImageColumn.MinWidth=compact?240:320;
        ProjectRow.MinHeight=ProjectPanelMenu.IsChecked?70:0;
        ToolRow.MinHeight=ToolPanelMenu.IsChecked?100:0;
        VariablesRow.MinHeight=VariablesPanelMenu.IsChecked?80:0;
        if(_compactLayout!=compact)
        {
            _compactLayout=compact;
            if(ProjectPanelMenu.IsChecked||ToolPanelMenu.IsChecked)LeftColumn.Width=new(compact?23:16,GridUnitType.Star);
            if(RunPanelMenu.IsChecked)RunColumn.Width=new(compact?28:23,GridUnitType.Star);
        }
        if(VariablesPanelMenu.IsChecked)VariablesRow.Height=new(Math.Clamp(ActualHeight*.23,100,220));
    }
}

internal static class ProgramNameDialog
{
    public static string Ask(Window owner,string title,string initial,string label="程序名称")
    {
        var window=new Window{Owner=owner,Title=title,Width=380,Height=180,MinWidth=280,MinHeight=150,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        window.SetResourceReference(Window.BackgroundProperty,"Bg");window.SetResourceReference(Window.ForegroundProperty,"Text");WindowScreenBehavior.Attach(window);
        var panel=new StackPanel{Margin=new(18)};window.Content=panel;panel.Children.Add(new TextBlock{Text=label,Margin=new(0,0,0,7)});
        var input=new TextBox{Text=initial,MinHeight=30};panel.Children.Add(input);var button=new Button{Content="确定",IsDefault=true,HorizontalAlignment=HorizontalAlignment.Right,Padding=new(20,4,20,4),Margin=new(0,12,0,0)};panel.Children.Add(button);
        button.Click+=(_,_)=>{if(!string.IsNullOrWhiteSpace(input.Text))window.DialogResult=true;};window.Loaded+=(_,_)=>{input.Focus();input.SelectAll();};return window.ShowDialog()==true?input.Text.Trim():null;
    }
}
