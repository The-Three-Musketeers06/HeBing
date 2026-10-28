using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AiVisionStudio.Services;

namespace AiVisionStudio.Views;

public partial class GlobalVariablesWindow:Window
{
    private readonly MainViewModel _vm;
    private bool _dirty,_loading,_refreshPending,_closed;
    private object _root;
    public ObservableCollection<GlobalVariableEdit> Rows { get; }=new();
    public ICollectionView RowsView { get; }
    public GlobalVariablesWindow(MainViewModel vm)
    {
        _vm=vm;RowsView=CollectionViewSource.GetDefaultView(Rows);RowsView.Filter=item=>item is GlobalVariableEdit row&&(row.Name+" "+row.Type+" "+row.Description).Contains(SearchBox?.Text??"",StringComparison.OrdinalIgnoreCase);
        InitializeComponent();DataContext=this;
        WindowScreenBehavior.Attach(this);
        _vm.PropertyChanged+=OnVmChanged;_vm.ProjectManagementChanged+=OnManagement;_vm.ImageChanged+=OnRuntimeChanged;
        Closed+=(_,_)=>{_closed=true;_vm.PropertyChanged-=OnVmChanged;_vm.ProjectManagementChanged-=OnManagement;_vm.ImageChanged-=OnRuntimeChanged;};Reload();
    }
    public void Reload()
    {
        _loading=true;try{Rows.Clear();foreach(var row in _vm.ReadGlobalDefinitions())Rows.Add(row);_root=_vm.SelectedProgram?.Root;_dirty=false;StatusText.Text=$"{Rows.Count} 个变量；应用后请保存工程。";}finally{_loading=false;UpdateEnabled();}
    }
    private bool Ready()
    {
        if(!_vm.CanEdit){StatusText.Text="运行中，管理窗口为只读；停止后可编辑。";return false;}
        if(!ReferenceEquals(_root,_vm.SelectedProgram?.Root)){StatusText.Text="工程已切换，请重新载入变量列表。";return false;}return true;
    }
    private bool CommitGrid()=>VariablesGrid.CommitEdit(DataGridEditingUnit.Cell,true)&&VariablesGrid.CommitEdit(DataGridEditingUnit.Row,true);
    public bool ApplyDraft()
    {
        if(!Ready()||!CommitGrid())return false;
        _loading=true;try{if(!_vm.TryApplyGlobalDefinitions(Rows,out var error)){StatusText.Text=error;return false;}_dirty=false;Reload();StatusText.Text="定义已应用；运行值未自动保存为默认值。";return true;}finally{_loading=false;}
    }
    private void OnApply(object sender,RoutedEventArgs e)=>ApplyDraft();
    private void OnEditArray(object sender,RoutedEventArgs e)
    {
        if(!Ready()||!CommitGrid()||VariablesGrid.SelectedItem is not GlobalVariableEdit row)return;
        if(!row.Type.EndsWith("[]")){StatusText.Text="请先把变量类型设为 int[]、float[]、bool[] 或 string[]。";return;}
        try{var value=string.IsNullOrWhiteSpace(row.Default)||row.Default=="0"?"[]":row.Default;var dialog=new ArrayVariableWindow(row.Type,value){Owner=this};if(dialog.ShowDialog()==true){row.Default=dialog.Result;VariablesGrid.Items.Refresh();_dirty=true;StatusText.Text="数组草稿已更新；应用定义后生效。";}}
        catch(ArgumentException ex){StatusText.Text=ex.Message;}
    }
    private void OnSave(object sender,RoutedEventArgs e){if(ApplyDraft())StatusText.Text=_vm.TrySave()?"变量定义已保存到工程。":_vm.Status;}
    private void OnAdd(object sender,RoutedEventArgs e)
    {
        if(!Ready())return;var n=1;while(Rows.Any(r=>r.Name.Equals("variable"+n,StringComparison.OrdinalIgnoreCase)))n++;
        var row=new GlobalVariableEdit{Name="variable"+n};Rows.Add(row);VariablesGrid.SelectedItem=row;VariablesGrid.ScrollIntoView(row);_dirty=true;StatusText.Text="已新增草稿，应用后生效。";
    }
    private void OnDelete(object sender,RoutedEventArgs e){if(Ready()&&VariablesGrid.SelectedItem is GlobalVariableEdit row){if(row.RuntimeOnly){StatusText.Text="此变量由脚本创建；应用定义可将其持久化，移除需修改创建它的脚本。";return;}Rows.Remove(row);_dirty=true;StatusText.Text="已从草稿移除；应用时会检查引用。";}}
    private void OnReload(object sender,RoutedEventArgs e){if(!_dirty||MessageBox.Show(this,"放弃尚未应用的变量编辑并重新载入？","重新载入",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)Reload();}
    private void OnSearch(object sender,TextChangedEventArgs e)=>RowsView?.Refresh();
    private void OnCellEdit(object sender,DataGridCellEditEndingEventArgs e){if(!_loading)_dirty=true;}
    private void OnSelection(object sender,SelectionChangedEventArgs e){if(VariablesGrid.SelectedItem is GlobalVariableEdit row)RuntimeValue.Text=row.Current;}
    private void OnWrite(object sender,RoutedEventArgs e)
    {
        if(!Ready()||VariablesGrid.SelectedItem is not GlobalVariableEdit row)return;
        if(_dirty){StatusText.Text="请先应用变量定义，再写入运行值。";return;}
        StatusText.Text=_vm.TryWriteGlobalValue(row.Name,RuntimeValue.Text,out var error)?"已写入运行值："+row.Name:error;
    }
    private void OnReset(object sender,RoutedEventArgs e){if(VariablesGrid.SelectedItem is GlobalVariableEdit row){RuntimeValue.Text=row.Default;OnWrite(sender,e);}}
    private void OnUseCurrent(object sender,RoutedEventArgs e){if(Ready()&&VariablesGrid.SelectedItem is GlobalVariableEdit row){row.Default=row.Current;VariablesGrid.Items.Refresh();_dirty=true;StatusText.Text="已将运行值填入默认值草稿；应用并保存后持久化。";}}
    private void OnReferences(object sender,RoutedEventArgs e){if(VariablesGrid.SelectedItem is GlobalVariableEdit row){var hits=_vm.FindGlobalReferences(row.OriginalName.Length>0?row.OriginalName:row.Name);StatusText.Text=hits.Count==0?"未找到直接绑定或常量名称脚本引用。":string.Join("；",hits);}}
    private void OnImport(object sender,RoutedEventArgs e)
    {
        if(!Ready())return;var dialog=new Microsoft.Win32.OpenFileDialog{Filter="变量文件 (*.json)|*.json",Title="导入变量草稿"};if(dialog.ShowDialog(this)!=true)return;
        try
        {
            var node=JsonNode.Parse(File.ReadAllText(dialog.FileName));var list=node as JsonArray??node?["variables"] as JsonArray??throw new InvalidDataException("文件缺少 variables 数组");
            var rows=list.OfType<JsonObject>().Select(n=>new GlobalVariableEdit{Name=n["name"]?.ToString()??"",Type=n["type"]?.ToString()??"string",Default=n["default"]?.ToString()??"",Description=n["description"]?.ToString()??""}).ToArray();
            foreach(var row in rows){GlobalValue.Parse(row.Type,row.Default);row.OriginalName=_vm.ReadGlobalDefinitions().FirstOrDefault(r=>r.Name.Equals(row.Name,StringComparison.OrdinalIgnoreCase))?.Name??"";}
            Rows.Clear();foreach(var row in rows)Rows.Add(row);_dirty=true;StatusText.Text="已导入草稿；应用时检查重名和引用。";
        }catch(Exception ex)when(ex is IOException or JsonException or ArgumentException or InvalidOperationException){StatusText.Text=ex.Message;}
    }
    private void OnExport(object sender,RoutedEventArgs e)
    {
        if(!CommitGrid())return;var dialog=new Microsoft.Win32.SaveFileDialog{Filter="变量文件 (*.json)|*.json",FileName="global-variables.json"};if(dialog.ShowDialog(this)!=true)return;
        try{var rows=Rows.Select(r=>new{name=r.Name,type=r.Type,@default=GlobalValue.Text(GlobalValue.Parse(r.Type,r.Default)),description=r.Description}).ToArray();File.WriteAllText(dialog.FileName,JsonSerializer.Serialize(new{format="es-global-variables",version=1,variables=rows},new JsonSerializerOptions{WriteIndented=true}));StatusText.Text="已导出变量草稿。";}catch(Exception ex)when(ex is IOException or ArgumentException){StatusText.Text=ex.Message;}
    }
    private void OnVmChanged(object sender,PropertyChangedEventArgs e){if(e.PropertyName is nameof(MainViewModel.CanEdit) or nameof(MainViewModel.SelectedProgram) or nameof(MainViewModel.ProjectPath))UpdateEnabled();}
    private void OnManagement(object sender,EventArgs e){if(!_dirty&&!_loading)Reload();}
    private void OnRuntimeChanged(object sender,EventArgs e)
    {
        if(_refreshPending||_closed)return;_refreshPending=true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(()=>
        {
            _refreshPending=false;
            if(!_closed&&!_dirty&&!_loading&&ReferenceEquals(_root,_vm.SelectedProgram?.Root))Reload();
        }));
    }
    private void UpdateEnabled()
    {var editable=_vm.CanEdit&&ReferenceEquals(_root,_vm.SelectedProgram?.Root);VariablesGrid.IsReadOnly=!editable;DefinitionActions.IsEnabled=_vm.CanEdit;RuntimeActions.IsEnabled=ApplyActions.IsEnabled=editable;if(!_vm.CanEdit)StatusText.Text="运行中，管理窗口为只读；停止后可编辑。";}
    private void OnClosing(object sender,CancelEventArgs e)
    {
        CommitGrid();if(!_dirty)return;var answer=MessageBox.Show(this,"将尚未应用的变量定义应用到当前工程？","全局变量",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
        if(answer==MessageBoxResult.Cancel||answer==MessageBoxResult.Yes&&!ApplyDraft())e.Cancel=true;
    }
}
