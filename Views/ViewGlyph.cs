using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AiVisionStudio.Views;

internal static class ViewGlyph
{
    internal static FrameworkElement Create(string name, Brush brush = null)
    {
        var data = name switch
        {
            "info" => "M2,2 L18,2 L18,18 L2,18 Z M9,8 L11,8 L11,15 M10,5 L10,6 M6,15 L14,15",
            "render" => "M3,4 L17,4 L17,16 L3,16 Z M4,15 L8,10 L11,13 L15,8 L17,11 M7,7 L7,8",
            "bounds" => "M2,5 L12,2 L18,6 L18,16 L8,19 L2,15 Z M2,5 L8,9 L18,6 M8,9 L8,19 M12,2 L12,12 L2,15 M12,12 L18,16",
            "fit" => "M2,7 L2,2 L7,2 M13,2 L18,2 L18,7 M18,13 L18,18 L13,18 M7,18 L2,18 L2,13 M7,7 L13,7 L13,13 L7,13 Z",
            "save" => "M2,2 L16,2 L18,4 L18,18 L2,18 Z M5,2 L5,8 L14,8 L14,2 M6,13 L14,13 L14,18",
            "screenshot" => "M2,5 L7,5 L9,2 L13,2 L15,5 L18,5 L18,17 L2,17 Z M7,10 A4,4 0 1,0 15,10 A4,4 0 1,0 7,10",
            "range" => "M2,3 L2,17 M6,3 L6,17 M10,3 L10,17 M14,3 L14,17 M18,3 L18,17",
            "axes" => "M4,3 L4,16 L18,16 M2,5 L4,2 L6,5 M15,14 L18,16 L15,18 M4,16 L13,7 M10,7 L13,7 L13,10",
            "camera" => "M2,6 L13,6 L13,15 L2,15 Z M13,9 L18,5 L18,16 L13,12 M6,3 L10,3",
            "pan" => "M2,10 L18,10 M10,2 L10,18 M2,10 L5,7 M2,10 L5,13 M10,2 L7,5 M10,2 L13,5 M18,10 L15,7 M18,10 L15,13 M10,18 L7,15 M10,18 L13,15",
            "rotate" => "M4,7 A7,7 0 1,1 3,13 M2,3 L4,7 L8,5",
            "obb" => "M3,7 L11,2 L18,6 L16,16 L8,19 L2,14 Z M3,7 L10,11 L18,6 M10,11 L8,19 M11,2 L10,11",
            "measure" => "M2,14 L14,2 L18,6 L6,18 Z M6,10 L9,13 M9,7 L12,10 M12,4 L15,7",
            "results" => "M1,10 Q10,-2 19,10 Q10,22 1,10 Z M7,10 A3,3 0 1,0 13,10 A3,3 0 1,0 7,10",
            "close" => "M3,3 L17,17 M17,3 L3,17",
            "maximize" => "M2,7 L2,2 L7,2 M13,2 L18,2 L18,7 M18,13 L18,18 L13,18 M7,18 L2,18 L2,13",
            _ => "M3,3 L17,3 L17,17 L3,17 Z"
        };
        return new Path { Data=Geometry.Parse(data),Stroke=brush??new SolidColorBrush(Color.FromRgb(78,156,196)),
            StrokeThickness=1.4,Width=19,Height=19,Stretch=Stretch.Uniform,SnapsToDevicePixels=true };
    }
}
