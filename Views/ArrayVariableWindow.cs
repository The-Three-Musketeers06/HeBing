using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using AiVisionStudio.Services;

namespace AiVisionStudio.Views;

/// <summary>Edits a detached array draft; cancel never changes the live variable.</summary>
public sealed class ArrayVariableWindow : Window
{
    public sealed class Cell { public string Value { get; set; } = ""; }
    private readonly string _type;
    private readonly ObservableCollection<Cell> _rows = new();
    private readonly DataGrid _grid;
    private readonly TextBlock _error = new() { TextWrapping=TextWrapping.Wrap, Margin=new(0,8,0,0) };
    public string Result { get; private set; }
    public ArrayVariableWindow(string type,string value)
    {
        _type=type;Title="数组编辑 · "+type;Width=520;Height=480;MinWidth=360;MinHeight=300;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;SetResourceReference(BackgroundProperty,"Bg");SetResourceReference(ForegroundProperty,"Text");WindowScreenBehavior.Attach(this);
        foreach(var item in (List<object>)GlobalValue.Parse(type,value))_rows.Add(new(){Value=GlobalValue.Text(item)});
        var root=new DockPanel{Margin=new(16)};root.SetResourceReference(Panel.BackgroundProperty,"Bg");Content=root;
        var toolbar=new WrapPanel{Margin=new(0,0,0,10)};DockPanel.SetDock(toolbar,Dock.Top);root.Children.Add(toolbar);
        void Button(string name,Action click){var b=new Button{Content=name,Margin=new(0,0,6,0),Padding=new(8,5,8,5)};b.Click+=(_,_)=>click();toolbar.Children.Add(b);}
        var bottom=new DockPanel{Margin=new(0,10,0,0)};DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(buttons,Dock.Bottom);bottom.Children.Add(buttons);bottom.Children.Add(_error);
        _grid=new DataGrid{ItemsSource=_rows,AutoGenerateColumns=false,CanUserAddRows=false,SelectionMode=DataGridSelectionMode.Single};
        _grid.Columns.Add(new DataGridTextColumn{Header="值（按当前行顺序，从 0 开始）",Binding=new System.Windows.Data.Binding(nameof(Cell.Value)),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});root.Children.Add(_grid);
        bool Commit()=>_grid.CommitEdit(DataGridEditingUnit.Cell,true)&&_grid.CommitEdit(DataGridEditingUnit.Row,true);
        Button("添加",()=>{if(Commit()){var row=new Cell{Value=type=="string[]"?"":type=="bool[]"?"false":"0"};_rows.Add(row);_grid.SelectedItem=row;_grid.ScrollIntoView(row);}});
        Button("删除",()=>{if(Commit()&&_grid.SelectedItem is Cell cell)_rows.Remove(cell);});
        void Move(int delta){if(!Commit())return;var i=_grid.SelectedIndex;if(i<0||i+delta<0||i+delta>=_rows.Count)return;_rows.Move(i,i+delta);_grid.SelectedIndex=i+delta;}
        Button("上移",()=>Move(-1));Button("下移",()=>Move(1));
        var save=new Button{Content="确认",Padding=new(20,5,20,5),Margin=new(5),IsDefault=true};buttons.Children.Add(save);
        save.Click+=(_,_)=>{if(!Commit())return;try{Result=GlobalValue.Text(_rows.Select(r=>GlobalValue.Parse(_type[..^2],r.Value)).ToList());DialogResult=true;}catch(ArgumentException ex){_error.Text=ex.Message;}};
        buttons.Children.Add(new Button{Content="取消",Padding=new(20,5,20,5),Margin=new(5),IsCancel=true});
    }
}
