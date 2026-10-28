using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AiVisionStudio.Views;

public sealed class ClampedGridSplitter : GridSplitter
{
    private Grid _grid;
    private DefinitionBase _first, _second;
    private bool _columns;

    static ClampedGridSplitter()
    {
        EventManager.RegisterClassHandler(typeof(ClampedGridSplitter), DragStartedEvent,
            new DragStartedEventHandler((sender, args) => { ((ClampedGridSplitter)sender).BeginResize(); args.Handled = true; }));
        EventManager.RegisterClassHandler(typeof(ClampedGridSplitter), DragDeltaEvent,
            new DragDeltaEventHandler((sender, args) => { ((ClampedGridSplitter)sender).Move(args.HorizontalChange, args.VerticalChange); args.Handled = true; }));
        EventManager.RegisterClassHandler(typeof(ClampedGridSplitter), DragCompletedEvent,
            new DragCompletedEventHandler((sender, args) => { ((ClampedGridSplitter)sender).EndResize(); args.Handled = true; }));
    }

    private void BeginResize()
    {
        EndResize();
        if (Parent is not Grid grid) return;
        _columns = ResizeDirection == GridResizeDirection.Columns
            || ResizeDirection == GridResizeDirection.Auto && ActualWidth <= ActualHeight;
        int index = _columns ? Grid.GetColumn(this) : Grid.GetRow(this);
        var definitions = _columns ? grid.ColumnDefinitions.Cast<DefinitionBase>().ToArray() : grid.RowDefinitions.Cast<DefinitionBase>().ToArray();
        int next = index + (_columns ? Grid.GetColumnSpan(this) : Grid.GetRowSpan(this));
        if (index < 1 || next >= definitions.Length) return;
        _grid = grid;
        _first = definitions[index - 1];
        _second = definitions[next];
        if (Length(_first) == new GridLength(0) || Length(_second) == new GridLength(0)) EndResize();
    }

    private static double Actual(DefinitionBase definition) => definition is ColumnDefinition column ? column.ActualWidth : ((RowDefinition)definition).ActualHeight;
    private static double Minimum(DefinitionBase definition) => definition is ColumnDefinition column ? column.MinWidth : ((RowDefinition)definition).MinHeight;
    private static double Maximum(DefinitionBase definition) => definition is ColumnDefinition column ? column.MaxWidth : ((RowDefinition)definition).MaxHeight;
    private static GridLength Length(DefinitionBase definition) => definition is ColumnDefinition column ? column.Width : ((RowDefinition)definition).Height;
    private static void SetLength(DefinitionBase definition, double value, GridUnitType unit)
    {
        if (definition is ColumnDefinition column) column.Width = new GridLength(value, unit);
        else ((RowDefinition)definition).Height = new GridLength(value, unit);
    }

    private void Move(double horizontal, double vertical)
    {
        if (_grid == null) return;
        double first = Actual(_first), second = Actual(_second);
        double lower = Math.Max(Minimum(_first) - first, second - Maximum(_second));
        double upper = Math.Min(Maximum(_first) - first, second - Minimum(_second));
        if (lower > upper) return;
        double requested = _columns ? horizontal * (_grid.FlowDirection == FlowDirection.RightToLeft ? -1 : 1) : vertical;
        if (!double.IsFinite(requested)) return;
        double change = Math.Clamp(requested, lower, upper);
        if (Math.Abs(change) < .001) return;
        var firstUnit = Length(_first).IsStar ? GridUnitType.Star : GridUnitType.Pixel;
        var secondUnit = Length(_second).IsStar ? GridUnitType.Star : GridUnitType.Pixel;
        var stars = (_columns ? _grid.ColumnDefinitions.Cast<DefinitionBase>() : _grid.RowDefinitions.Cast<DefinitionBase>())
            .Where(definition => Length(definition).IsStar).Select(definition => (Definition: definition, Size: Actual(definition))).ToArray();
        foreach (var star in stars) SetLength(star.Definition, star.Size, GridUnitType.Star);
        SetLength(_first, first + change, firstUnit);
        SetLength(_second, second - change, secondUnit);
    }

    private void EndResize()
    {
        _grid = null;
        _first = _second = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            BeginResize();
            Move(e.Key == Key.Left ? -KeyboardIncrement : e.Key == Key.Right ? KeyboardIncrement : 0,
                e.Key == Key.Up ? -KeyboardIncrement : e.Key == Key.Down ? KeyboardIncrement : 0);
            EndResize();
            e.Handled = true;
        }
        else base.OnKeyDown(e);
    }
}
