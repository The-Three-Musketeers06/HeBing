using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AiVisionStudio.Models;

namespace AiVisionStudio.Views;

public partial class ScriptToolEditor : UserControl
{
    private readonly Dictionary<string, string> _examples = new()
    {
        ["读取与写入全局变量"] = "local value = GetFloatVariable(\"value\")\nSetFloatVariable(\"result\", value + 1)\nprintln(value)",
        ["输入变量判定"] = "if input0 ~= nil then\n    println(input0)\nelse\n    println(\"输入变量未绑定\")\nend",
        ["循环计算"] = "local sum = 0\nfor i = 1, 10 do\n    sum = sum + i\nend\nSetFloatVariable(\"sum\", sum)\nprintln(sum)"
    };
    public ScriptToolEditor()
    {
        InitializeComponent();
        FunctionList.ItemsSource = new[] { "println(value)", "print(value)", "GetFloatVariable(name)", "SetFloatVariable(name, value)", "GetIntVariable(name)", "SetIntVariable(name, value)", "GetBoolVariable(name)", "SetBoolVariable(name, value)", "GetStringVariable(name)", "SetStringVariable(name, value)", "GetRecipeSet(index)", "math.abs(value)", "math.sqrt(value)", "string.format(format, value)" };
        ExampleList.ItemsSource = _examples.Keys;
    }
    private void OnCodeChanged(object sender, TextChangedEventArgs e)
    {
        if (LineNumbers == null) return;
        var count = Math.Max(1, CodeEditor.Text.Count(c => c == '\n') + 1);
        LineNumbers.Text = string.Join("\n", Enumerable.Range(1, count));
    }
    private void OnCodeScroll(object sender, ScrollChangedEventArgs e) => LineScroller?.ScrollToVerticalOffset(e.VerticalOffset);
    private void OnInputBinding(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ParameterNode node && Window.GetWindow(this) is ParamsWindow window) window.OpenParameterBinding(node);
    }
    private void OnInsertFunction(object sender, MouseButtonEventArgs e) { if (FunctionList.SelectedItem is string value) Insert(value); }
    private void OnInsertExample(object sender, MouseButtonEventArgs e) { if (ExampleList.SelectedItem is string name && _examples.TryGetValue(name, out var value)) Insert(value); }
    private void Insert(string text)
    {
        if (CodeEditor.IsReadOnly) return;
        CodeEditor.SelectedText = text;
        CodeEditor.CaretIndex = CodeEditor.SelectionStart + text.Length;
        CodeEditor.Focus();
    }
}

