using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace AiVisionStudio.Views;

public partial class ImageView
{
    private Image _templateMaskImage;
    private BitmapSource _templateMaskBaseBitmap;
    private WriteableBitmap _templateEraseBitmap;
    private OpenCvSharp.Rect _templateEraseBounds;
    private Geometry _templateMaskClip;
    private bool _templateMaskDrawing, _templateViewConfigured;
    public event EventHandler TemplateViewChanged;
    public double TemplateZoom => Math.Abs(Mat.Matrix.M11);
    public bool TemplateFitsWindow => !_userMoved;
    public bool HasPendingTemplateGesture => _maskStrokes.Count > 0 || _regionDraft != null;
    public Dictionary<string, object> TemplateRegionDraft => _maskCommit == null && _regionDraft?.IsValid == true ? _regionDraft.ToNative() : null;
    public event EventHandler TemplateRegionDraftChanged;

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
        if (!_templateViewConfigured)
        {
            Mat.Changed += (_, _) => TemplateViewChanged?.Invoke(this, EventArgs.Empty);
            _templateViewConfigured = true;
        }
    }

    public void ZoomTemplateCanvas(double factor)
    {
        if (_bitmap == null || !double.IsFinite(factor) || factor <= 0) return;
        ApplyPendingViewTransform();
        var scale = Math.Abs(Mat.Matrix.M11);
        if (scale < .000001) return;
        var matrix = Mat.Matrix;
        var ratio = Math.Clamp(scale * factor, .001, 1000) / scale;
        matrix.ScaleAt(ratio, ratio, Host.ActualWidth / 2, Host.ActualHeight / 2);
        _userMoved = true; Mat.Matrix = matrix;
    }

    public void ShowTemplateActualSize()
    {
        if (_bitmap == null) return;
        _userMoved = true;
        Mat.Matrix = new Matrix(1, 0, 0, 1, (Host.ActualWidth - _bitmap.PixelWidth) / 2, (Host.ActualHeight - _bitmap.PixelHeight) / 2);
    }

    public void SetTemplateZoom(double scale)
    {
        if (_bitmap == null || !double.IsFinite(scale) || scale <= 0) return;
        ZoomTemplateCanvas(scale / Math.Max(.0001, TemplateZoom));
    }

    public void SetTemplateMaskDrawing(bool enabled)
    { _templateMaskDrawing = enabled; _regionSurface.InvalidateVisual(); }

    public void SetTemplateBrushRadius(double radius)
    {
        if (!double.IsFinite(radius)) return;
        _maskRadius = Math.Clamp(radius, 1, 500);
        UpdateTemplateMaskErasePreview();
        _regionSurface.InvalidateVisual();
    }

    private void UpdateTemplateMaskErasePreview()
    {
        if (_templateMaskImage == null) return;
        void Restore()
        {
            _templateMaskImage.Source = _templateMaskBaseBitmap;
            _templateEraseBitmap = null; _templateEraseBounds = default;
        }
        if (!_maskErasing || _maskCommit == null || _maskStrokes.Count == 0 || _templateMaskBaseBitmap == null)
        { Restore(); return; }

        // Rasterize the same integer polygons used when committing the brush stroke. Updating
        // only its bounding rectangle keeps the preview exact without rebuilding the full image.
        var polygons = _maskStrokes.SelectMany(StrokeRegions).Select(region =>
            ((IEnumerable<object>)((IReadOnlyDictionary<string, object>)region["polygon2d"])["Points"])
                .Cast<IReadOnlyDictionary<string, object>>().Select(point => new OpenCvSharp.Point(
                    (int)Math.Round(Convert.ToDouble(point["X"])), (int)Math.Round(Convert.ToDouble(point["Y"])))).ToArray()).ToArray();
        var points = polygons.SelectMany(polygon => polygon).ToArray();
        if (points.Length == 0) { Restore(); return; }
        int left = Math.Max(0, points.Min(point => point.X)), top = Math.Max(0, points.Min(point => point.Y));
        int right = Math.Min(_templateMaskBaseBitmap.PixelWidth, points.Max(point => point.X) + 1);
        int bottom = Math.Min(_templateMaskBaseBitmap.PixelHeight, points.Max(point => point.Y) + 1);
        if (right <= left || bottom <= top) { Restore(); return; }
        if (_templateEraseBitmap != null)
        {
            left = Math.Min(left, _templateEraseBounds.Left); top = Math.Min(top, _templateEraseBounds.Top);
            right = Math.Max(right, _templateEraseBounds.Right); bottom = Math.Max(bottom, _templateEraseBounds.Bottom);
        }
        var bounds = new OpenCvSharp.Rect(left, top, right - left, bottom - top);
        using var erased = new OpenCvSharp.Mat(bounds.Height, bounds.Width, MatType.CV_8UC1, Scalar.Black);
        foreach (var polygon in polygons)
            Cv2.FillPoly(erased, [polygon.Select(point => new OpenCvSharp.Point(point.X - left, point.Y - top)).ToArray()], Scalar.White);
        erased.GetArray(out byte[] excluded);
        var rectangle = new Int32Rect(left, top, bounds.Width, bounds.Height);
        int stride = checked(bounds.Width * 4);
        var pixels = new byte[checked(stride * bounds.Height)];
        _templateMaskBaseBitmap.CopyPixels(rectangle, pixels, stride, 0);
        for (int index = 0; index < excluded.Length; index++) if (excluded[index] != 0) pixels[index * 4 + 3] = 0;
        _templateEraseBitmap ??= new WriteableBitmap(_templateMaskBaseBitmap);
        _templateEraseBitmap.WritePixels(rectangle, pixels, stride, 0);
        _templateEraseBounds = bounds; _templateMaskImage.Source = _templateEraseBitmap;
    }

    public void ShowTemplateMask(OpenCvSharp.Mat mask, OpenCvSharp.Mat trainingRegion = null)
    {
        if (_templateMaskImage == null)
        {
            if (mask == null) return;
            _templateMaskImage = new Image { Stretch = Stretch.Fill, RenderTransform = Mat, IsHitTestVisible = false, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(_templateMaskImage, BitmapScalingMode.NearestNeighbor);
            ((Canvas)Img.Parent).Children.Add(_templateMaskImage);
        }
        if (mask == null)
        {
            _templateMaskImage.Source = null; _templateMaskBaseBitmap = null;
            _templateEraseBitmap = null; _templateEraseBounds = default; _templateMaskClip = null; return;
        }
        using var pixels = new OpenCvSharp.Mat(mask.Size(), MatType.CV_8UC4, Scalar.All(0));
        pixels.SetTo(new Scalar(60, 60, 255, 96), mask);
        int stride = checked((int)pixels.Step());
        var bitmap = BitmapSource.Create(mask.Width, mask.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Data, checked(stride * mask.Height), stride);
        bitmap.Freeze();
        _templateMaskBaseBitmap = bitmap; _templateEraseBitmap = null; _templateEraseBounds = default;
        _templateMaskImage.Source = bitmap; _templateMaskImage.Width = mask.Width; _templateMaskImage.Height = mask.Height;
        if (trainingRegion != null)
        {
            Cv2.FindContours(trainingRegion, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.Tree, ContourApproximationModes.ApproxSimple);
            var clip = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var geometry = clip.Open())
                foreach (var contour in contours.Where(contour => contour.Length >= 3))
                {
                    geometry.BeginFigure(new System.Windows.Point(contour[0].X, contour[0].Y), true, true);
                    geometry.PolyLineTo(contour.Skip(1).Select(point => new System.Windows.Point(point.X, point.Y)).ToArray(), true, false);
                }
            clip.Freeze(); _templateMaskClip = clip;
        }
        UpdateTemplateMaskErasePreview();
    }
}
