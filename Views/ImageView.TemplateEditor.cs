using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AiVisionStudio.Views;

public partial class ImageView
{
    /// <summary>Use the image editor canvas without the main preview's side tools and image browser.</summary>
    public void ConfigureTemplateCanvas()
    {
        _showRegionChrome = false;
        _regionChrome.Visibility = Visibility.Collapsed;
        var layout = (Grid)Content;
        layout.ColumnDefinitions[0].Width = new GridLength(0);
        layout.RowDefinitions[1].Height = new GridLength(0);
        layout.RowDefinitions[2].Height = new GridLength(0);
        Host.SetResourceReference(Panel.BackgroundProperty, "RowBg");
    }

    public void ZoomTemplateCanvas(double factor)
    {
        if (_bitmap == null || !double.IsFinite(factor) || factor <= 0) return;
        var scale = Math.Abs(Mat.Matrix.M11);
        if (scale < .000001) return;
        var matrix = Mat.Matrix;
        var ratio = Math.Clamp(scale * factor, .001, 1000) / scale;
        matrix.ScaleAt(ratio, ratio, Host.ActualWidth / 2, Host.ActualHeight / 2);
        Mat.Matrix = matrix; _userMoved = true;
    }

    public void ShowTemplateActualSize()
    {
        if (_bitmap == null) return;
        Mat.Matrix = new Matrix(1, 0, 0, 1, (Host.ActualWidth - _bitmap.PixelWidth) / 2, (Host.ActualHeight - _bitmap.PixelHeight) / 2);
        _userMoved = true;
    }
}
