using System.Collections;
using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using AiVisionStudio.Models;
using AiVisionStudio.Services;

namespace AiVisionStudio.Views;

public sealed class ExpressionToolEditor : UserControl
{
    public ExpressionToolEditor()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = new GridLength(40, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = new GridLength(5) });
        grid.RowDefinitions.Add(new() { Height = new GridLength(60, GridUnitType.Star) });
        var editor = new LuaCodeEditor { AcceptsReturn = true, AcceptsTab = true, FontFamily = new FontFamily("Consolas"), FontSize = 14, Padding = new Thickness(12), Background = Brush("#1D1F21"), Foreground = Brush("#C5C8C6"), BorderThickness = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        editor.SetBinding(TextBox.TextProperty, new Binding("SelectedExpressionParameter.Value") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        editor.SetBinding(TextBox.IsReadOnlyProperty, new Binding(nameof(MainViewModel.IsRunning)));
        editor.SetBinding(IsEnabledProperty, new Binding(nameof(MainViewModel.CanEdit)));
        grid.Children.Add(editor);
        var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        Grid.SetRow(splitter, 1); grid.Children.Add(splitter);
        var panel = new DockPanel { Background = Brush("#27272A") };
        var header = new TextBlock { Text = "全局变量", Height = 35, Padding = new Thickness(10,7,0,0), Background = Brush("#323233") };
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var list = new ListView { Background = Brushes.Transparent, Foreground = Brush("#D0D0D4"), BorderThickness = new Thickness(0) };
        list.Resources.Add(typeof(GridViewColumnHeader), FindResource("ToolGridHeader"));
        list.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.GlobalVars)));
        list.View = new GridView { Columns = { new GridViewColumn { Header = "名称", Width = 180, DisplayMemberBinding = new Binding("Name") }, new GridViewColumn { Header = "类型", Width = 100, DisplayMemberBinding = new Binding("Type") }, new GridViewColumn { Header = "值", Width = 180, DisplayMemberBinding = new Binding("Value") } } };
        list.MouseDoubleClick += (_, _) =>
        {
            if (list.SelectedItem is not VarRow value || editor.IsReadOnly) return;
            var method = value.Type is "int" or "Int32" ? "GetIntVariable" : value.Type is "bool" or "Boolean" ? "GetBoolVariable" : value.Type is "float" or "double" or "Double" ? "GetFloatVariable" : "GetStringVariable";
            editor.SelectedText = method + "(\"" + value.Name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\")";
            editor.Focus();
        };
        panel.Children.Add(list); Grid.SetRow(panel, 2); grid.Children.Add(panel); Content = grid;
    }
    private static Brush Brush(string hex) => ThemeBrush.From(hex);
}

public sealed class RecipeToolPreview : UserControl
{
    private MainViewModel _vm;
    private readonly DataGrid _table = new() { IsReadOnly = true, AutoGenerateColumns = true, CanUserAddRows = false, Background = ThemeBrush.From("#27272A"), Foreground = ThemeBrush.From("#D0D0D4"), BorderThickness = new Thickness(0) };
    public RecipeToolPreview()
    {
        var panel = new DockPanel();
        var label = new TextBlock { Text = "配方", Height = 35, Padding = new Thickness(10,7,0,0), Background = ThemeBrush.From("#323233") };
        DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label); panel.Children.Add(_table); Content = panel;
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
        Unloaded += (_, _) => { if (_vm != null) _vm.ImageChanged -= Refresh; };
        Loaded += (_, _) => Attach(DataContext as MainViewModel);
    }
    private void Attach(MainViewModel vm)
    {
        if (_vm != null) _vm.ImageChanged -= Refresh;
        _vm = vm;
        if (_vm != null) _vm.ImageChanged += Refresh;
        Refresh(null, EventArgs.Empty);
    }
    private void Refresh(object sender, EventArgs e)
    {
        var value = _vm?.GetSelectedOutputObject("Recipe List");
        if(value==null&&System.IO.File.Exists(_vm?.ActiveRecipePath))
        {
            try{_table.ItemsSource=RecipeDocument.Load(_vm.ActiveRecipePath).Table.DefaultView;return;}
            catch(Exception error)when(error is System.IO.IOException or System.Text.Json.JsonException){_vm.Status="配方预览失败："+error.Message;}
        }
        var table = new DataTable();
        if (value is IEnumerable rows && value is not string)
            foreach (var item in rows)
            {
                if (item is not IDictionary<string, object> row) continue;
                foreach (var key in row.Keys) if (!table.Columns.Contains(key)) table.Columns.Add(key);
                var record = table.NewRow();
                foreach (var pair in row) record[pair.Key] = pair.Value?.ToString() ?? "";
                table.Rows.Add(record);
            }
        _table.ItemsSource = table.DefaultView;
    }
}

