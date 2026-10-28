using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using AiVisionStudio.Models;

namespace AiVisionStudio.Views;

public sealed class ColorParameterEditor : UserControl
{
    private readonly Button _swatch;
    private ParameterNode _node;
    public ColorParameterEditor()
    {
        var panel = new DockPanel();
        _swatch = new Button { Width = 29, Height = 25, Margin = new Thickness(0, 0, 5, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), ToolTip = "选择颜色" };
        _swatch.Click += OpenPicker;
        DockPanel.SetDock(_swatch, Dock.Left); panel.Children.Add(_swatch);
        var text = new TextBox { Padding = new Thickness(5,3,5,3), VerticalContentAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Color.FromRgb(35,35,38)), Foreground = Brushes.LightGray };
        text.SetBinding(TextBox.TextProperty, new Binding(nameof(ParameterNode.Value)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        panel.Children.Add(text); Content = panel;
        DataContextChanged += (_, _) =>
        {
            if (_node != null) _node.PropertyChanged -= Changed;
            _node = DataContext as ParameterNode;
            if (_node != null) _node.PropertyChanged += Changed;
            Update();
        };
        Unloaded += (_, _) => { if (_node != null) _node.PropertyChanged -= Changed; };
        Loaded += (_, _) => { if (_node != null) { _node.PropertyChanged -= Changed; _node.PropertyChanged += Changed; } Update(); };
    }
    private void Changed(object sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(ParameterNode.Value)) { if (Dispatcher.CheckAccess()) Update(); else Dispatcher.InvokeAsync(Update); } }
    private void Update() => _swatch.Background = new SolidColorBrush(Parse(_node?.Value));
    internal static Color Parse(string value)
    {
        if (value?.Length == 9 && value[0] == '#' && uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgba))
            return Color.FromArgb((byte)rgba, (byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8));
        try { return (Color)ColorConverter.ConvertFromString(value ?? "#000000"); } catch { return Colors.Transparent; }
    }
    private void OpenPicker(object sender, RoutedEventArgs e)
    {
        if (_node == null) return;
        var color = Parse(_node.Value);
        var window = new Window { Title = _node.DisplayName, Owner = Window.GetWindow(this), Width = 370, Height = 340, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("Panel"), Foreground = (Brush)FindResource("Text"), FontFamily = FontFamily, FontSize = 13, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(18) };
        var preview = new Border { Height = 45, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(color), Margin = new Thickness(0,0,0,12) };
        panel.Children.Add(preview);
        var sliders = new List<Slider>();
        foreach (var channel in new[] { ("R",(double)color.R), ("G",(double)color.G), ("B",(double)color.B), ("A",(double)color.A) })
        {
            var row = new DockPanel { Margin = new Thickness(0,4,0,4) };
            row.Children.Add(new TextBlock { Text = channel.Item1, Width = 24, VerticalAlignment = VerticalAlignment.Center });
            var slider = new Slider { Minimum = 0, Maximum = 255, Value = channel.Item2, TickFrequency = 1, IsSnapToTickEnabled = true };
            sliders.Add(slider); row.Children.Add(slider); panel.Children.Add(row);
            slider.ValueChanged += (_, _) => { if (sliders.Count == 4) preview.Background = new SolidColorBrush(Current()); };
        }
        var palette = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,12,0,8) };
        foreach (var hex in new[] { "#FFFFFF", "#18A058", "#2080F0", "#F0A020", "#D03050" })
        {
            var choice = new Button { Width = 32, Height = 26, Background = new SolidColorBrush(Parse(hex)), Margin = new Thickness(0,0,8,0) };
            choice.Click += (_, _) => { var c = Parse(hex); sliders[0].Value = c.R; sliders[1].Value = c.G; sliders[2].Value = c.B; sliders[3].Value = c.A; };
            palette.Children.Add(choice);
        }
        panel.Children.Add(palette);
        var apply = new Button { Content = "确定", Style = (Style)FindResource("ToolActionButton"), HorizontalAlignment = HorizontalAlignment.Right };
        apply.Click += (_, _) => { var c = Current(); _node.Value = c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}"; window.DialogResult = true; };
        panel.Children.Add(apply); window.Content = panel; window.ShowDialog();
        Color Current() => Color.FromArgb((byte)sliders[3].Value, (byte)sliders[0].Value, (byte)sliders[1].Value, (byte)sliders[2].Value);
    }
}

