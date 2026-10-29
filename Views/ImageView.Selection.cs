using System.Windows;

namespace AiVisionStudio.Views;

public sealed class ImagePointClickedEventArgs(Point point) : EventArgs
{
    public Point Point { get; } = point;
}

public partial class ImageView
{
    public event EventHandler<ImagePointClickedEventArgs> ImagePointClicked;

    /// <summary>Map a point in the image viewport to source pixels, respecting pending pan/zoom.</summary>
    public bool TryGetImagePoint(Point viewportPoint, out Point imagePoint)
    {
        ApplyPendingViewTransform();
        var inverse = Mat.Matrix;
        imagePoint = default;
        if (!HasImage || !inverse.HasInverse) return false;
        inverse.Invert(); imagePoint = inverse.Transform(viewportPoint);
        return imagePoint.X >= 0 && imagePoint.Y >= 0 && imagePoint.X < _bitmap.PixelWidth && imagePoint.Y < _bitmap.PixelHeight;
    }
}
