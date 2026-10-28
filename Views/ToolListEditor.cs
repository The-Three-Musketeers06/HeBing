using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Globalization;
using AiVisionStudio.Models;
using AiVisionStudio.Services;

namespace AiVisionStudio.Views;

/// <summary>Original list windows expose each named list member as a table row.</summary>
public sealed class ToolListEditor : UserControl
{
    private MainViewModel _vm;
    private ParameterNode _list;
    private readonly DataGrid _table;
    private readonly Button _add;
    private readonly List<ParameterNode> _observed = new();
    private bool _pending;
    public ToolListEditor()
    {
        var root = new DockPanel { Background = Brush("#27272A") };
        var header = new DockPanel { Height = 36, Background = Brush("#323233"), LastChildFill = true };
        _add = new Button { Content = "+ 添加", Width = 65, Height = 26, Margin = new Thickness(5), Background = Brush("#2D3943"), Foreground = Brush("#4D98BD"), BorderThickness = new Thickness(0) };
        DockPanel.SetDock(_add, Dock.Right); header.Children.Add(_add);
        header.Children.Add(new TextBlock { Text = "表单设计", Margin = new Thickness(12,0,0,0), VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        _table = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserResizeRows = false,
            RowHeaderWidth = 0, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = Brush("#35353A"), Background = Brush("#27272A"), Foreground = Brush("#D0D0D4"), BorderThickness = new Thickness(0),
            RowBackground = Brush("#27272A"), AlternatingRowBackground = Brush("#29292D"), MinRowHeight = 40, EnableRowVirtualization = true, SelectionUnit = DataGridSelectionUnit.FullRow };
        var headerStyle = new Style(typeof(DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brush("#323233")));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brush("#D0D0D4")));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9,7,9,7)));
        _table.ColumnHeaderStyle = headerStyle;
        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        cellStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5,0,5,0)));
        cellStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        _table.CellStyle = cellStyle;
        _table.LoadingRow += (_, e) => e.Row.Header = e.Row.GetIndex();
        _table.SelectionChanged += (_, _) =>
        {
            if (_vm != null && _table.SelectedItem is ParameterNode selected && selected.Children.FirstOrDefault(node => node.Name == "Expression") is {} expression)
                _vm.SelectedExpressionParameter = expression;
        };
        root.Children.Add(_table); Content = root;
        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
        Unloaded += (_, _) => Detach();
        Loaded += (_, _) => Attach(DataContext as MainViewModel);
    }
    private void Detach()
    {
        if (_vm != null) _vm.ParameterGroups.CollectionChanged -= GroupsChanged;
        foreach (var node in _observed) node.PropertyChanged -= NodeChanged;
        _observed.Clear();
    }
    private void Attach(MainViewModel vm)
    {
        Detach(); _vm = vm;
        if (_vm != null) _vm.ParameterGroups.CollectionChanged += GroupsChanged;
        Build();
    }
    private void GroupsChanged(object sender, NotifyCollectionChangedEventArgs e) => Schedule();
    private void NodeChanged(object sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(ParameterNode.IsVisible)) Schedule(); }
    private void Schedule()
    {
        if (_pending) return; _pending = true;
        Dispatcher.InvokeAsync(() => { _pending = false; Build(); });
    }
    private void Build()
    {
        foreach (var node in _observed) node.PropertyChanged -= NodeChanged;
        _observed.Clear();
        _table.Columns.Clear();
        _list = _vm?.AllParameterNodes.FirstOrDefault(node => node.Kind == "list" && node.Parent == null);
        _table.ItemsSource = _list?.Children;
        _add.Command = _list?.AddItemCommand;
        if (_list == null) return;
        _table.Columns.Add(new DataGridTextColumn { Header = "序号", Binding = new Binding { Converter = new RowNumberConverter() }, IsReadOnly = true, Width = 55 });
        var nameCell = new FrameworkElementFactory(typeof(Button));
        nameCell.SetBinding(ContentControl.ContentProperty, new Binding(nameof(ParameterNode.Name)));
        nameCell.SetValue(Control.ForegroundProperty, Brush("#C0C0C5"));
        nameCell.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        nameCell.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        nameCell.AddHandler(Button.ClickEvent, new RoutedEventHandler(Rename));
        _table.Columns.Add(new DataGridTemplateColumn { Header = "名称", CellTemplate = new DataTemplate { VisualTree = nameCell }, Width = 110 });
        var fields = _list.Specification?.Children ?? [];
        foreach (var fieldGroup in fields.GroupBy(field => field.DisplayName))
        {
            var names = fieldGroup.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
            var members = _list.Children.SelectMany(row => row.Children.Where(node => names.Contains(node.Name))).ToArray();
            foreach (var member in members) { _observed.Add(member); member.PropertyChanged += NodeChanged; }
            if (members.Length > 0 && members.All(node => !node.IsVisible)) continue;
            var factory = new FrameworkElementFactory(typeof(ParameterEditor));
            factory.SetValue(ParameterEditor.CompactProperty, true);
            factory.SetBinding(DataContextProperty, new Binding { Converter = new ActiveFieldConverter(names) });
            var width = fieldGroup.Any(field => field.Type == "string" || field.Name == "Expression") ? 220 : fieldGroup.All(field => field.Type == "bool") ? 90 : 200;
            _table.Columns.Add(new DataGridTemplateColumn { Header = fieldGroup.Key, CellTemplate = new DataTemplate { VisualTree = factory },
                Width = new DataGridLength(width), MinWidth = Math.Min(width, 170) });
        }
        var actions = new FrameworkElementFactory(typeof(StackPanel));
        actions.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        foreach (var direction in new[] { -1, 1 })
        {
            var move = new FrameworkElementFactory(typeof(Button));
            move.SetValue(ContentControl.ContentProperty, direction < 0 ? "↑" : "↓");
            move.SetValue(Control.ForegroundProperty, Brush("#9DA1A9"));
            move.SetValue(Control.BackgroundProperty, Brushes.Transparent);
            move.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            move.SetValue(FrameworkElement.WidthProperty, 25.0);
            move.SetValue(TagProperty, direction);
            move.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, _) =>
            {
                if (sender is Button { DataContext: ParameterNode row, Tag: int moveDirection }) _vm.MoveParameterListMember(_list, row.Name, moveDirection);
            }));
            actions.AppendChild(move);
        }
        var remove = new FrameworkElementFactory(typeof(Button));
        remove.SetValue(ContentControl.ContentProperty, "删除");
        remove.SetValue(Control.ForegroundProperty, Brush("#9DA1A9"));
        remove.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        remove.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        remove.SetBinding(Button.CommandProperty, new Binding(nameof(ParameterNode.RemoveItemCommand)));
        actions.AppendChild(remove);
        _table.Columns.Add(new DataGridTemplateColumn { Header = "操作", Width = 105, CellTemplate = new DataTemplate { VisualTree = actions } });
    }

    private void Rename(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ParameterNode row } || _vm?.SelectedOperation?.Driver is not ("Write CSV" or "Create Table")) return;
        if (_vm.SelectedOperation.Driver == "Create Table" && row.Name is "Id" or "InsertTime" or "OKNG") return;
        var window = new Window { Title = "名称", Width = 360, Height = 185, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize, Background = Brush("#27272A"), Foreground = Brush("#D0D0D4"), ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(16) };
        var name = new TextBox { Text = row.Name, MaxLength = 32, Style = (Style)FindResource("ToolTextBox") };
        var error = new TextBlock { Foreground = Brush("#DA6B6B"), Margin = new Thickness(0,8,0,8) };
        var apply = new Button { Content = "确定", Style = (Style)FindResource("ToolActionButton"), HorizontalAlignment = HorizontalAlignment.Right };
        apply.Click += (_, _) => { if (_vm.RenameParameterListMember(_list, row.Name, name.Text, out var message)) window.DialogResult = true; else error.Text = message; };
        panel.Children.Add(name); panel.Children.Add(error); panel.Children.Add(apply); window.Content = panel; window.ShowDialog();
    }

    private sealed class ActiveFieldConverter(HashSet<string> names) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value as ParameterNode)?.Children.FirstOrDefault(node => node.IsVisible && names.Contains(node.Name));
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
    private sealed class RowNumberConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is ParameterNode node ? node.Parent?.Children.IndexOf(node) ?? 0 : 0;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
    private static Brush Brush(string hex) => ThemeBrush.From(hex);
}

