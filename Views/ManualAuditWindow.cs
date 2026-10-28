using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AiVisionStudio.Services;

namespace AiVisionStudio.Views;

public sealed class ManualAuditWindow:Window
{
    public ManualAuditWindow()
    {
        Title="手册功能核对 · ES-Vision-Studio";Width=1150;Height=720;MinWidth=640;MinHeight=420;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty,"Bg");SetResourceReference(ForegroundProperty,"Text");WindowScreenBehavior.Attach(this);
        var root=new DockPanel{Margin=new(20)};root.SetResourceReference(Panel.BackgroundProperty,"Bg");Content=root;
        var header=new StackPanel{Margin=new(0,0,0,12)};DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        header.Children.Add(new TextBlock{Text="手册功能核对",FontSize=24,FontWeight=FontWeights.SemiBold});
        var rows=ManualCatalog.Read();
        header.Children.Add(new TextBlock{Text=$"共 {rows.Count} 个手册页面 · “已有实现”不代表所有模式和原版数值一致",Margin=new(0,8,0,8),TextWrapping=TextWrapping.Wrap});
        var search=new TextBox{MinHeight=30,ToolTip="搜索功能、分类、状态或缺项"};header.Children.Add(search);
        var filter=new ComboBox{ItemsSource=new[]{"全部"}.Concat(rows.Select(r=>r.Status).Distinct()).ToArray(),SelectedIndex=0,Margin=new(0,8,0,0),HorizontalAlignment=HorizontalAlignment.Left,MinWidth=220};header.Children.Add(filter);
        var detail=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,8)};var bottom=new StackPanel();DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);bottom.Children.Add(detail);
        var open=new Button{Content="打开选中功能的官方手册",HorizontalAlignment=HorizontalAlignment.Right,Padding=new(16,8,16,8)};bottom.Children.Add(open);
        var view=CollectionViewSource.GetDefaultView(rows);view.Filter=item=>item is ManualFeature row&&(filter.SelectedIndex==0||row.Status==filter.SelectedItem?.ToString())&&(row.Title+row.Category+row.Status+row.Driver+row.Gaps).Contains(search.Text,StringComparison.OrdinalIgnoreCase);
        var grid=new DataGrid{ItemsSource=view,IsReadOnly=true,AutoGenerateColumns=false,CanUserAddRows=false,SelectionMode=DataGridSelectionMode.Single};root.Children.Add(grid);
        void Column(string label,string field,double width)=>grid.Columns.Add(new DataGridTextColumn{Header=label,Binding=new Binding(field),Width=width});
        Column("分类",nameof(ManualFeature.Category),130);Column("功能",nameof(ManualFeature.Title),210);Column("核对状态",nameof(ManualFeature.Status),150);
        grid.Columns.Add(new DataGridTextColumn{Header="未完成 / 待验证范围",Binding=new Binding(nameof(ManualFeature.Gaps)),Width=new DataGridLength(1,DataGridLengthUnitType.Star),MinWidth=220});
        grid.SelectionChanged+=(_,_)=>{if(grid.SelectedItem is ManualFeature row)detail.Text="依据："+row.Evidence+"\n"+row.Url;};
        search.TextChanged+=(_,_)=>view.Refresh();filter.SelectionChanged+=(_,_)=>view.Refresh();
        open.Click+=(_,_)=>{if(grid.SelectedItem is ManualFeature row)try{ManualCatalog.Open(row.Url);}catch(Exception ex){detail.Text=ex.Message;}};
    }
}
