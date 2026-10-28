using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using AiVisionStudio.Models;

namespace AiVisionStudio.Views;

/// <summary>Native matrix cells and two-ended ranges retain the original scalar node bindings.</summary>
public sealed class StructuredParameterEditor : UserControl
{
    private ParameterNode _node;
    public StructuredParameterEditor()
    {
        DataContextChanged += (_, _) =>
        {
            if (_node != null) _node.Children.CollectionChanged -= OnChildrenChanged;
            _node = DataContext as ParameterNode;
            if (_node != null) _node.Children.CollectionChanged += OnChildrenChanged;
            Build();
        };
    }
    private void OnChildrenChanged(object sender, NotifyCollectionChangedEventArgs e) => Build();
    private void Build()
    {
        if (_node == null) { Content = null; return; }
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 5) };
        if (_node.Kind == "range")
        {
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var separator = new TextBlock { Text = "–", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(separator, 1); grid.Children.Add(separator);
            for (var i = 0; i < Math.Min(2, _node.Children.Count); i++)
            {
                var editor = new TextBox { DataContext = _node.Children[i], Style = (Style)FindResource("ToolTextBox"), MinWidth = 0, Margin = new Thickness(0, 3, 0, 3) };
                editor.SetBinding(TextBox.TextProperty, new Binding(nameof(ParameterNode.Value)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
                editor.SetBinding(IsEnabledProperty, new Binding(nameof(ParameterNode.CanEditValue)));
                editor.SetBinding(ToolTipProperty, new Binding(nameof(ParameterNode.Error)));
                Grid.SetColumn(editor, i == 0 ? 0 : 2); grid.Children.Add(editor);
            }
        }
        else if (_node.Kind == "coordinates2d")
        {
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (int i = 0; i < Math.Min(2, _node.Children.Count); i++)
            {
                grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                var cell = new DockPanel { Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0) };
                cell.Children.Add(new TextBlock { Text = _node.Children[i].Name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) });
                cell.Children.Add(new ParameterEditor { Compact = true, DataContext = _node.Children[i] });
                Grid.SetColumn(cell, i); grid.Children.Add(cell);
            }
            grid.SizeChanged += (_, e) =>
            {
                bool stacked = e.NewSize.Width < 210;
                grid.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 1, GridUnitType.Star);
                for (int i = 0; i < grid.Children.Count; i++)
                { Grid.SetRow(grid.Children[i], stacked ? i : 0); Grid.SetColumn(grid.Children[i], stacked ? 0 : i); }
            };
        }
        else if (_node.Kind == "matrix")
        {
            var rows = Number("rows", 4); var cols = Number("cols", 4);
            grid.ColumnDefinitions.Add(new() { Width = new GridLength(28) });
            for (var col = 0; col < cols; col++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = 70 });
            grid.RowDefinitions.Add(new() { Height = new GridLength(26) });
            for (var row = 0; row < rows; row++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            for (var col = 0; col < cols; col++) AddText(col.ToString(), 0, col + 1);
            for (var row = 0; row < rows; row++)
            {
                AddText(row.ToString(), row + 1, 0);
                for (var col = 0; col < cols; col++)
                {
                    var index = row * cols + col;
                    if (index >= _node.Children.Count) continue;
                    var editor = new TextBox { DataContext = _node.Children[index], Style = (Style)FindResource("ToolTextBox"), Margin = new Thickness(2), MinWidth = 65 };
                    editor.SetBinding(IsEnabledProperty, new Binding(nameof(ParameterNode.CanEditValue)));
                    editor.SetBinding(ToolTipProperty, new Binding(nameof(ParameterNode.Error)));
                    var binding = new MultiBinding { Converter = new ParameterNumberConverter(), Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
                    binding.Bindings.Add(new Binding(nameof(ParameterNode.Value)) { Mode = BindingMode.TwoWay });
                    binding.Bindings.Add(new Binding(nameof(ParameterNode.Kind)) { Mode = BindingMode.OneWay });
                    binding.Bindings.Add(new Binding(nameof(TextBox.IsKeyboardFocused)) { RelativeSource = RelativeSource.Self, Mode = BindingMode.OneWay });
                    editor.SetBinding(TextBox.TextProperty, binding);
                    Grid.SetRow(editor, row + 1); Grid.SetColumn(editor, col + 1); grid.Children.Add(editor);
                }
            }
        }
        Content = _node.Kind == "range" ? grid : new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = _node.Kind == "coordinates2d" ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        int Number(string key, int fallback) => _node.Specification?.Raw is {} raw && raw.TryGetProperty(key, out var value) && value.TryGetInt32(out var result) ? Math.Clamp(result, 1, 32) : fallback;
        void AddText(string value, int row, int col)
        {
            var text = new TextBlock { Text = value, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, row); Grid.SetColumn(text, col); grid.Children.Add(text);
        }
    }
}

