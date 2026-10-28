using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace AiVisionStudio.Views;

/// <summary>Retains TextBox editing, undo and binding semantics; paints Lua tokens using its actual character geometry.</summary>
public sealed class LuaCodeEditor : TextBox
{
    private LuaSyntaxViewport _syntax;
    public LuaCodeEditor()
    {
        var template = new ControlTemplate(typeof(LuaCodeEditor));
        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.SetBinding(Panel.BackgroundProperty, new Binding(nameof(Background)) { RelativeSource = RelativeSource.TemplatedParent });
        var syntax = new FrameworkElementFactory(typeof(LuaSyntaxViewport), "Syntax");
        syntax.SetValue(IsHitTestVisibleProperty, false);
        grid.AppendChild(syntax);
        var scroller = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        scroller.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        grid.AppendChild(scroller);
        template.VisualTree = grid; Template = template;
        TextChanged += (_, _) => _syntax?.InvalidateVisual();
        AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => _syntax?.InvalidateVisual()));
        SizeChanged += (_, _) => _syntax?.InvalidateVisual();
        LayoutUpdated += (_, _) => { if (_syntax?.NeedsLayoutRedraw == true) { _syntax.NeedsLayoutRedraw = false; _syntax.InvalidateVisual(); } };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || IsReadOnly || !IsEnabled) return;
            var line = GetLineIndexFromCharacterIndex(CaretIndex);
            var start = GetCharacterIndexFromLineIndex(line);
            if (start < 0) return;
            var text = Text[start..Math.Min(CaretIndex, Text.Length)];
            var indent = new string(text.TakeWhile(c => c is ' ' or '\t').ToArray());
            if (Regex.IsMatch(text.TrimEnd(), @"\b(then|do|function)\b[^\r\n]*$")) indent += "    ";
            var position = SelectionStart;
            SelectedText = Environment.NewLine + indent;
            CaretIndex = position + Environment.NewLine.Length + indent.Length;
            e.Handled = true;
        };
    }
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _syntax = GetTemplateChild("Syntax") as LuaSyntaxViewport;
        if (_syntax != null) _syntax.Owner = this;
        SetCurrentValue(ForegroundProperty, Brushes.Transparent);
        SetCurrentValue(CaretBrushProperty, Brushes.White);
    }
}

public sealed class LuaSyntaxViewport : FrameworkElement
{
    internal LuaCodeEditor Owner { get; set; }
    internal bool NeedsLayoutRedraw { get; set; } = true;
    private static readonly Regex Tokens = new(@"(?<comment>--[^\r\n]*)|(?<str>""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*')|(?<keyword>\b(?:and|break|do|else|elseif|end|for|function|goto|if|in|local|not|or|repeat|return|then|until|while)\b)|(?<constant>\b(?:true|false|nil)\b)|(?<number>\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b)", RegexOptions.Compiled);
    protected override void OnRender(DrawingContext drawing)
    {
        var editor = Owner;
        if (editor == null || string.IsNullOrEmpty(editor.Text)) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        drawing.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        var start = 0;
        foreach (var line in editor.Text.Split('\n'))
        {
            var content = line.TrimEnd('\r');
            if (content.Length > 0)
            {
                var rectangle = editor.GetRectFromCharacterIndex(start);
                if (!rectangle.IsEmpty && rectangle.Bottom >= 0 && rectangle.Top <= ActualHeight)
                {
                    var formatted = new FormattedText(content, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        new Typeface(editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch), editor.FontSize, Color("#C5C8C6"), dpi);
                    foreach (Match token in Tokens.Matches(content))
                    {
                        var color = token.Groups["comment"].Success ? "#969896" : token.Groups["str"].Success ? "#B5BD68" : token.Groups["keyword"].Success ? "#B294BB" : "#DE935F";
                        formatted.SetForegroundBrush(Color(color), token.Index, token.Length);
                    }
                    drawing.DrawText(formatted, new Point(rectangle.Left, rectangle.Top));
                }
            }
            start += line.Length + 1;
        }
        drawing.Pop();
    }
    private static Brush Color(string hex) => (Brush)new BrushConverter().ConvertFromString(hex);
}
