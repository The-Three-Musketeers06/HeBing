using System.Windows;
using System.Windows.Input;
using AiVisionStudio.Services;

namespace AiVisionStudio.Views;

public partial class LogWindow : Window
{
    private readonly MainViewModel _vm;

    public LogWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClear(object sender, RoutedEventArgs e) => _vm.Logs.Clear();
}
