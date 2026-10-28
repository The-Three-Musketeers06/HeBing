using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using AiVisionStudio.Models;

namespace AiVisionStudio.Views;

/// <summary>One native-schema parameter, recursively including nested structures and arrays.</summary>
public partial class ParameterEditor : UserControl
{
    private ParameterNode _node;
    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(ParameterEditor),
        new PropertyMetadata(false, (sender, _) => ((ParameterEditor)sender).UpdateEditor()));
    public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }

    public ParameterEditor()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        ValueCell.SizeChanged += (_, _) => UpdateNumericSpacing();
        DataContextChanged += OnNodeChanged;
        Unloaded += (_, _) => { if (_node != null) _node.PropertyChanged -= OnNodePropertyChanged; };
        Loaded += (_, _) =>
        {
            if (_node != null)
            {
                _node.PropertyChanged -= OnNodePropertyChanged;
                _node.PropertyChanged += OnNodePropertyChanged;
            }
            UpdateEditor();
        };
    }

    private void OnNodeChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_node != null) _node.PropertyChanged -= OnNodePropertyChanged;
        _node = DataContext as ParameterNode;
        if (_node != null) _node.PropertyChanged += OnNodePropertyChanged;
        UpdateEditor();
    }

    private void OnNodePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ParameterNode.IsBound) or nameof(ParameterNode.IsEnabled)
            or nameof(ParameterNode.IsExpanded) or nameof(ParameterNode.IsVisible) or nameof(ParameterNode.CanBind))) return;
        if (Dispatcher.CheckAccess()) UpdateEditor();
        else Dispatcher.InvokeAsync(UpdateEditor);
    }

    private void UpdateEditor()
    {
        if (_node == null || Leaf == null) return;
        var structured = _node.Kind is "matrix" or "range" or "coordinates2d";
        GroupChildren.ItemsSource = _node.IsGroup && !structured && _node.IsExpanded && _node.IsVisible ? _node.Children : null;
        Leaf.Visibility = _node.IsGroup && _node.Kind is not ("range" or "coordinates2d") ? Visibility.Collapsed : Visibility.Visible;
        Group.Visibility = _node.IsGroup && !structured ? Visibility.Visible : Visibility.Collapsed;
        MatrixPanel.Visibility = _node.Kind == "matrix" ? Visibility.Visible : Visibility.Collapsed;
        LabelColumn.Width = Compact ? new GridLength(0) : new GridLength(_node.Kind == "coordinates2d" ? 28 : 49, GridUnitType.Star);
        LeafLabel.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
        Leaf.ColumnDefinitions[2].Width = new GridLength(_node.CanBind ? 23 : 0);
        TextValue.Visibility = Visibility.Collapsed;
        NumberValue.Visibility = Visibility.Collapsed;
        EnumValue.Visibility = Visibility.Collapsed;
        ImageInputValue.Visibility = Visibility.Collapsed;
        BooleanValue.Visibility = Visibility.Collapsed;
        FileValue.Visibility = Visibility.Collapsed;
        ColorValue.Visibility = Visibility.Collapsed;
        RangeValue.Visibility = Visibility.Collapsed;
        MultilineValue.Visibility = Visibility.Collapsed;
        BoundValue.Visibility = Visibility.Collapsed;
        ArrayActions.Visibility = _node.AddItemCommand != null || _node.RemoveItemCommand != null ? Visibility.Visible : Visibility.Collapsed;
        AddArrayButton.Visibility = _node.AddItemCommand != null ? Visibility.Visible : Visibility.Collapsed;
        RemoveArrayButton.Visibility = _node.RemoveItemCommand != null ? Visibility.Visible : Visibility.Collapsed;
        LeafRemoveButton.Visibility = _node.RemoveItemCommand != null ? Visibility.Visible : Visibility.Collapsed;
        GroupBinding.Visibility = _node.IsBound ? Visibility.Visible : Visibility.Collapsed;
        RegionDrawButton.Visibility = AiVisionStudio.Services.MainViewModel.CanDrawRegion(_node) || SupportsGraphicEdit(_node) ? Visibility.Visible : Visibility.Collapsed;
        BindPositionLeafButton.Visibility = _node.Name == "Position Fix" ? Visibility.Visible : Visibility.Collapsed;
        BindPositionGroupButton.Visibility = _node.Name == "Points Information" ? Visibility.Visible : Visibility.Collapsed;
        UpdateResponsiveLayout();
        if (_node.Kind is "range" or "coordinates2d") { RangeValue.Visibility = Visibility.Visible; return; }
        if (_node.IsGroup) return;
        FrameworkElement editor = _node.IsImageInput ? ImageInputValue : _node.IsBound ? BoundValue :
            _node.Kind == "color" ? ColorValue :
            _node.IsBoolean ? BooleanValue :
            _node.IsEnum && !_node.IsFreeformEnum ? EnumValue :
            _node.IsNumeric ? NumberValue :
            _node.IsFile ? FileValue :
            _node.IsMultiline ? MultilineValue : TextValue;
        editor.Visibility = Visibility.Visible;
    }

    private void UpdateResponsiveLayout()
    {
        if (_node == null || Leaf == null) return;
        bool stacked = !Compact && (_node.IsImageInput || ActualWidth > 0 && ActualWidth < Math.Max(320, FontSize * 25));
        int row = stacked ? 1 : 0;
        Grid.SetColumnSpan(LeafLabel, stacked ? 4 : 1);
        LeafLabel.TextWrapping = stacked ? TextWrapping.Wrap : TextWrapping.NoWrap;
        LeafLabel.Margin = stacked ? new Thickness(0, 4, 0, 0) : new Thickness(0, 10, 7, 10);
        Grid.SetRow(ValueCell, row); Grid.SetColumn(ValueCell, stacked ? 0 : 1); Grid.SetColumnSpan(ValueCell, stacked ? 2 : 1);
        Grid.SetRow(LeafBinding, row); Grid.SetRow(LeafRemoveButton, row);
        Grid.SetRow(MultilineValue, row + 1); Grid.SetRow(LeafError, row + 2);
        Grid.SetRow(BindPositionLeafButton, row + 1);
        GroupChildren.Margin = new Thickness(ActualWidth > 0 && ActualWidth < 360 ? 8 : 17, 0, 0, 0);
        UpdateNumericSpacing();
    }

    private void UpdateNumericSpacing()
    {
        if (_node == null) return;
        bool steps = _node.RegionCornerKey == null && ValueCell.ActualWidth >= Math.Max(135, FontSize * 11);
        NumericSteps.Visibility = steps ? Visibility.Visible : Visibility.Collapsed;
        NumericText.Padding = steps ? new Thickness(8, 3, 45, 3) : new Thickness(4, 3, 4, 3);
    }

    private void OnDrawRegion(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_node != null && Window.GetWindow(this) is ParamsWindow window)
        {
            if (AiVisionStudio.Services.MainViewModel.CanDrawRegion(_node) && !window.UsesFixtureRegionEditing) window.OpenRegionEditor(_node, (FrameworkElement)sender);
            else window.BeginParameterRegionEdit(_node);
        }
    }

    private void OnImageInputClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var menu = BuildImageInputMenu();
        menu.IsOpen = true;
    }

    private ContextMenu BuildImageInputMenu()
    {
        var menu = new ContextMenu { PlacementTarget = ImageInputValue };
        var sources = _node?.GetImageInputChoices?.Invoke() ?? [];
        foreach (var group in sources.GroupBy(source => source.Reference.OperationId))
        {
            var parent = new MenuItem { Header = group.First().OperationName };
            foreach (var source in group)
            {
                var item = new MenuItem
                {
                    Header = source.OutputName + (source.Available ? "" : "（未运行）"), Tag = source,
                    IsCheckable = true, IsChecked = AiVisionStudio.Services.MainViewModel.SameImageInput(_node.ImageInputReference, source.Reference),
                    ToolTip = source.Label + (source.Available ? "" : "：运行前置控件后可处理该图像")
                };
                item.Click += (menuSender, args) => { args.Handled = true; _node?.SelectImageInput?.Invoke(_node, source); };
                parent.Items.Add(item);
            }
            menu.Items.Add(parent);
        }
        if (sources.Count == 0) menu.Items.Add(new MenuItem { Header = "前面没有可用的图像来源，请先添加取图或图像处理控件", IsEnabled = false });
        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "清除选择", IsEnabled = _node?.IsEnabled == true };
        clear.Click += (menuSender, args) => { args.Handled = true; _node?.SelectImageInput?.Invoke(_node, null); };
        menu.Items.Add(clear);
        return menu;
    }

    private void OnBindingClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_node != null && Window.GetWindow(this) is ParamsWindow window) window.OpenParameterBinding(_node);
    }

    private void OnBindRegionPositionClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_node?.IsEnabled == true && Window.GetWindow(this) is ParamsWindow window) window.BindRegionPosition(_node);
    }

    private static bool SupportsGraphicEdit(ParameterNode node)
    {
        if (!node.IsGroup) return false;
        var group = node.Specification?.GroupType ?? "";
        if (group is "region" or "mask" or "caliper" or "trace" or "fiducialpoint2d") return true;
        var types = new[] { "rect2d", "circle2d", "polygon2d", "trace2d", "ring2d", "sector2d", "ellipse2d", "segment2d", "point2d", "window2d", "window1d", "line_caliper", "circle_caliper", "arc_caliper", "box", "cylinderbox", "orientedbox", "caliper" };
        if (types.Contains(group, StringComparer.OrdinalIgnoreCase)) return true;
        var name = node.Name.EndsWith(" array", StringComparison.Ordinal) ? node.Name[..^6] : node.Name;
        if (types.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        if (node.Specification?.Support.Any(s => types.Contains(s, StringComparer.OrdinalIgnoreCase)) == true) return true;
        var fields = node.Children.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return fields.Contains("Start") && fields.Contains("End")
            || fields.Contains("Center") && (fields.Contains("Size") || fields.Contains("Radius") || fields.Contains("Outer Radius"))
            || fields.Contains("Points") || node.Name is "Model Origin";
    }
}

/// <summary>Match the native numeric display without rounding the serialized value or interrupting typing.</summary>
public sealed class ParameterNumberConverter : IMultiValueConverter
{
    private string _displayedValue;
    private string _sourceValue;
    private string _editingValue;
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var text = values.ElementAtOrDefault(0) as string ?? "";
        var kind = values.ElementAtOrDefault(1) as string;
        _sourceValue = text;
        bool focused = values.ElementAtOrDefault(2) is true;
        if (!focused) _editingValue = null;
        if (kind is "int" or "integer") return _displayedValue = text;
        int decimalPoint = text.IndexOf('.');
        if (focused && text == _editingValue && (decimalPoint < 0 || text.Length - decimalPoint <= 4)) return _displayedValue = text;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) return _displayedValue = text;
        return _displayedValue = number.ToString("0.000", CultureInfo.InvariantCulture);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        var text = value?.ToString() ?? "";
        if (text == _displayedValue && _displayedValue != _sourceValue) return [Binding.DoNothing, Binding.DoNothing, Binding.DoNothing];
        _editingValue = text;
        return [text, Binding.DoNothing, Binding.DoNothing];
    }
}
