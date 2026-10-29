using System.Windows.Controls;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;

namespace AiVisionStudio.Views;

public partial class TemplateConfigurationWindow
{
    private readonly Dictionary<string, Button> _drawingTools = new();

    private Button AddDrawingTool(string key, Panel panel, string label, string geometry, Action action)
    {
        var button = AddTool(panel, label, geometry, action);
        _drawingTools[key] = button;
        return button;
    }

    private void SelectDrawingTool(string key)
    {
        var selected = key == null ? null : _drawingTools.GetValueOrDefault(key);
        foreach (var button in _drawingTools.Values.Distinct())
        {
            bool active = ReferenceEquals(button, selected);
            button.Tag = active ? "SelectedTool" : null;
            if (button.Content is Path icon)
            {
                icon.Stroke = new SolidColorBrush(active ? Color.FromRgb(36, 59, 83) : Color.FromRgb(132, 139, 148));
                icon.StrokeThickness = active ? 1.9 : 1.5;
            }
        }
    }
}
