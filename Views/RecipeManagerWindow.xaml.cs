using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AiVisionStudio.Services;

namespace AiVisionStudio.Views;

public partial class RecipeManagerWindow:Window
{
    private readonly MainViewModel _vm;
    private object _projectRoot;
    private bool _dirty,_changing;
    private string _loadedPath="";
    public ObservableCollection<RecipeEntry> Entries { get; }=new();
    public RecipeDocument Document { get; private set; }
    public RecipeManagerWindow(MainViewModel vm)
    {
        _vm=vm;InitializeComponent();DataContext=this;_projectRoot=vm.SelectedProgram?.Root;
        _vm.PropertyChanged+=OnVmChanged;_vm.ProjectManagementChanged+=OnManagement;
        Closed+=(_,_)=>{_vm.PropertyChanged-=OnVmChanged;_vm.ProjectManagementChanged-=OnManagement;};ReloadEntries();
    }
    private bool Ready()
    {
        if(!_vm.CanEdit){StatusText.Text="运行中，配方为只读；停止后可修改。";return false;}
        if(!ReferenceEquals(_projectRoot,_vm.SelectedProgram?.Root)){StatusText.Text="工程已切换，请重新载入配方。";return false;}return true;
    }
    private void Guard(Action action)
    {try{action();}catch(Exception ex)when(ex is IOException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException){StatusText.Text=ex.Message;}}
    private bool Commit()=>RecipeGrid.CommitEdit(DataGridEditingUnit.Cell,true)&&RecipeGrid.CommitEdit(DataGridEditingUnit.Row,true);
    private bool SaveDocument()
    {
        if(!Ready()||Document==null||!Commit())return false;
        try{Document.Save();_dirty=false;StatusText.Text="配方已保存，下一次运行与 HMI 使用新值。";_changing=true;try{_vm.RefreshRecipeData();}finally{_changing=false;}return true;}
        catch(Exception ex)when(ex is IOException or ArgumentException or InvalidOperationException){StatusText.Text=ex.Message;return false;}
    }
    private bool ConfirmSwitch()
    {
        Commit();if(!_dirty)return true;var answer=MessageBox.Show(this,"保存尚未写入的配方修改？","配方管理",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
        return answer==MessageBoxResult.No||answer==MessageBoxResult.Yes&&SaveDocument();
    }
    public void ReloadEntries(string preferPath=null)
    {
        _changing=true;
        try
        {
            Entries.Clear();foreach(var entry in _vm.ReadRecipes())Entries.Add(entry);
            _projectRoot=_vm.SelectedProgram?.Root;
            RecipeList.SelectedItem=Entries.FirstOrDefault(e=>e.Path==(preferPath??_loadedPath))??Entries.FirstOrDefault(e=>e.Selected)??Entries.FirstOrDefault();
            ActiveText.Text="当前配方："+(Entries.FirstOrDefault(e=>e.Selected)?.Name??"未选择");
        }
        finally{_changing=false;}
        LoadSelected();UpdateEnabled();
    }
    private void LoadSelected()
    {
        Guard(()=>
        {
            Document=null;RecipeGrid.ItemsSource=null;_dirty=false;
            if(RecipeList.SelectedItem is not RecipeEntry entry){_loadedPath="";FilePathText.Text="尚未关联配方，可新建或导入 JSON 配方。";RecipeName.Text="";RefreshFields();return;}
            var doc=RecipeDocument.Load(_vm.ResolveRecipePath(entry.Path));Document=doc;_loadedPath=entry.Path;
            RecipeName.Text=entry.Name;FilePathText.Text=doc.Path;RecipeGrid.ItemsSource=doc.Table.DefaultView;
            doc.Table.ColumnChanged+=(_,_)=>MarkDirty();doc.Table.RowChanged+=(_,_)=>MarkDirty();doc.Table.RowDeleted+=(_,_)=>MarkDirty();
            _dirty=false;RefreshFields();StatusText.Text="配方文件编辑后需保存；切换当前配方后需保存工程关联。";
        });
    }
    private void MarkDirty(){if(!_changing){_dirty=true;CountText.Text=$"{Document?.Table.DefaultView.Count??0} 行 · 未保存";}}
    private void RefreshFields(){RemoveField.ItemsSource=Document?.Fields.Select(f=>f.Name).ToArray();CountText.Text=$"{Document?.Table.DefaultView.Count??0} 行 / {Document?.Fields.Count??0} 字段";}
    private void OnSelection(object sender,SelectionChangedEventArgs e)
    {
        if(_changing)return;
        if(!ConfirmSwitch()){_changing=true;RecipeList.SelectedItem=Entries.FirstOrDefault(r=>r.Path==_loadedPath);_changing=false;return;}LoadSelected();
    }
    private void SetEntries(IEnumerable<RecipeEntry> entries,string selected)
    {_changing=true;try{_vm.ApplyRecipeEntries(entries);}finally{_changing=false;}ReloadEntries(selected);}
    private string NewPath(string name)
    {
        if(string.IsNullOrEmpty(_vm.ProjectPath))throw new InvalidOperationException("请先保存工程，再创建或导入配方。");
        var folder=Path.Combine(_vm.ProjectDirectory,"Recipes");Directory.CreateDirectory(folder);
        var stem=Path.GetFileNameWithoutExtension(name);if(string.IsNullOrWhiteSpace(stem))stem="Recipe";
        var path=Path.Combine(folder,stem+".json");var number=1;while(File.Exists(path))path=Path.Combine(folder,stem+"_"+(number++)+".json");return path;
    }
    private void RegisterFile(string path)
    {
        var entries=_vm.ReadRecipes().ToList();var name=Path.GetFileNameWithoutExtension(path);var candidate=name;var i=1;while(entries.Any(e=>e.Name.Equals(candidate,StringComparison.OrdinalIgnoreCase)))candidate=name+"_"+i++;
        var stored=_vm.StoreRecipePath(path);entries.Add(new(){Name=candidate,Path=stored,Selected=entries.All(e=>!e.Selected)});SetEntries(entries,stored);
    }
    private void OnNew(object sender,RoutedEventArgs e)
    {if(!Ready()||!ConfirmSwitch())return;Guard(()=>{var path=NewPath("Recipe.json");RecipeDocument.Create().Save(path);RegisterFile(path);});}
    private void OnImport(object sender,RoutedEventArgs e)
    {
        if(!Ready()||!ConfirmSwitch())return;var dialog=new Microsoft.Win32.OpenFileDialog{Filter="配方文件 (*.json)|*.json",Title="导入配方副本"};if(dialog.ShowDialog(this)!=true)return;
        Guard(()=>{var document=RecipeDocument.Load(dialog.FileName);var path=NewPath(Path.GetFileName(dialog.FileName));document.Export(path);RegisterFile(path);StatusText.Text="已导入工程内副本；源文件未修改。";});
    }
    private void OnDuplicate(object sender,RoutedEventArgs e)
    {if(!Ready()||Document==null||!Commit())return;Guard(()=>{var path=NewPath(Path.GetFileNameWithoutExtension(Document.Path)+"_copy.json");Document.Export(path);_dirty=false;RegisterFile(path);});}
    private void OnRemove(object sender,RoutedEventArgs e)
    {if(!Ready()||RecipeList.SelectedItem is not RecipeEntry entry||!ConfirmSwitch())return;Guard(()=>{SetEntries(_vm.ReadRecipes().Where(r=>r.Path!=entry.Path),null);StatusText.Text="已移除工程关联，配方文件仍保留在磁盘。";});}
    private void OnRename(object sender,RoutedEventArgs e)
    {if(!Ready()||RecipeList.SelectedItem is not RecipeEntry entry||!ConfirmSwitch())return;Guard(()=>{var entries=_vm.ReadRecipes().ToList();entries.Single(r=>r.Path==entry.Path).Name=RecipeName.Text;SetEntries(entries,entry.Path);});}
    private void OnActivate(object sender,RoutedEventArgs e)
    {if(!Ready()||RecipeList.SelectedItem is not RecipeEntry entry||!ConfirmSwitch())return;Guard(()=>{var entries=_vm.ReadRecipes().ToList();foreach(var item in entries)item.Selected=item.Path==entry.Path;SetEntries(entries,entry.Path);StatusText.Text="当前配方已切换；保存工程关联后重开仍生效。";});}
    private void OnReload(object sender,RoutedEventArgs e){if(ConfirmSwitch()){_projectRoot=_vm.SelectedProgram?.Root;ReloadEntries();}}
    private void OnSaveRecipe(object sender,RoutedEventArgs e)=>SaveDocument();
    private void OnSaveProject(object sender,RoutedEventArgs e){if(Ready())StatusText.Text=_vm.TrySave()?"配方关联已保存到工程。":_vm.Status;}
    private void OnExport(object sender,RoutedEventArgs e)
    {if(Document==null||!Commit())return;var dialog=new Microsoft.Win32.SaveFileDialog{Filter="配方文件 (*.json)|*.json",FileName="recipe-export.json"};if(dialog.ShowDialog(this)==true)Guard(()=>{Document.Export(dialog.FileName);StatusText.Text="已导出当前配方副本。";});}
    private void OnAddRow(object sender,RoutedEventArgs e){if(Ready()&&Document!=null&&Commit()){Document.AddRow();RefreshFields();}}
    private void OnCopyRow(object sender,RoutedEventArgs e){if(Ready()&&Document!=null&&Commit()&&RecipeGrid.SelectedItem is DataRowView row){Document.AddRow(row.Row);RefreshFields();}}
    private void OnDeleteRow(object sender,RoutedEventArgs e){if(Ready()&&Commit()&&RecipeGrid.SelectedItem is DataRowView row){row.Delete();RefreshFields();}}
    private void OnAddField(object sender,RoutedEventArgs e)
    {if(Ready()&&Document!=null&&Commit())Guard(()=>{Document.AddField(FieldName.Text,((ComboBoxItem)FieldType.SelectedItem).Content.ToString(),FieldDefault.Text);RecipeGrid.ItemsSource=null;RecipeGrid.ItemsSource=Document.Table.DefaultView;_dirty=true;RefreshFields();});}
    private void OnRemoveField(object sender,RoutedEventArgs e)
    {if(Ready()&&Document!=null&&Commit()&&RemoveField.SelectedItem is string name)Guard(()=>{Document.RemoveField(name);RecipeGrid.ItemsSource=null;RecipeGrid.ItemsSource=Document.Table.DefaultView;_dirty=true;RefreshFields();});}
    private void OnColumn(object sender,DataGridAutoGeneratingColumnEventArgs e)
    {if(Document?.Fields.FirstOrDefault(f=>f.Name==e.PropertyName)?.Type=="json"){e.Column.IsReadOnly=true;e.Column.Header=e.PropertyName+"（结构只读）";}e.Column.MinWidth=95;}
    private void OnVmChanged(object sender,PropertyChangedEventArgs e){if(e.PropertyName is nameof(MainViewModel.CanEdit) or nameof(MainViewModel.SelectedProgram) or nameof(MainViewModel.ProjectPath))UpdateEnabled();}
    private void OnManagement(object sender,EventArgs e){if(!_changing&&!_dirty)ReloadEntries();}
    private void UpdateEnabled()
    {
        var editable=_vm.CanEdit&&ReferenceEquals(_projectRoot,_vm.SelectedProgram?.Root);
        ListActions.IsEnabled=EntryActions.IsEnabled=RowActions.IsEnabled=FieldActions.IsEnabled=SaveActions.IsEnabled=editable;RecipeGrid.IsReadOnly=!editable;
        if(!_vm.CanEdit)StatusText.Text="运行中，配方为只读；停止后可修改。";
    }
    private void OnClosing(object sender,CancelEventArgs e){if(!ConfirmSwitch())e.Cancel=true;}
}
