using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AiVisionStudio.Views;

public sealed partial class CloudView3D
{
    private int[] _pixels, _packedColors;
    private float[] _depths;
    private string _packedMode;
    private double _packedMin, _packedMax;
    private int _cloudVersion;
    private RasterKey _rasterKey;
    private int _cachedRenderedPoints, _cachedSampledPoints;
    private bool _animationInteraction, _wheelInteraction;
    private DispatcherTimer _wheelTimer;
    private readonly record struct RasterKey(CameraState Camera, int Version, int Width, int Height,
        double DpiX, double DpiY, string Mode, double Min, double Max, bool Perspective, int Stride);
    private bool _hasRasterKey;
    private int _overlayVersion;
    private WriteableBitmap _pointsetBitmap;
    private int[] _pointsetPixels;
    private float[] _pointsetDepths;
    private RasterKey? _pointsetKey;
    private static readonly int[] HeightPalette = Enumerable.Range(0, 1276).Select(i => Pack(HeightColor(i / 1275d))).ToArray();
    private static int Pack(Color c) => unchecked((int)0xff000000) | c.R << 16 | c.G << 8 | c.B;

    public bool IsInteractionRendering => _rotating || _panning || _animationInteraction || _wheelInteraction;
    public int SampledPointCount { get; private set; }
    public bool LastRasterWasCached { get; private set; }
    public double LastRasterMilliseconds { get; private set; }
    public long RasterGeneration { get; private set; }
    public void SetInteractiveRendering(bool active) { _animationInteraction = active; InvalidateVisual(); }

    private void MarkWheelInteraction()
    {
        _wheelInteraction = true;
        if (_wheelTimer == null)
        {
            _wheelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
            _wheelTimer.Tick += (_, _) => { _wheelTimer.Stop(); _wheelInteraction = false; InvalidateVisual(); };
            Unloaded += (_, _) => { _wheelTimer.Stop(); _wheelInteraction = _animationInteraction = false; };
        }
        _wheelTimer.Stop(); _wheelTimer.Start();
    }
    private static double NormalizeAngle(double angle) => Math.IEEERemainder(angle, 2 * Math.PI);
    private static double SnapTrig(double value) => Math.Abs(value) < 1e-12 ? 0 : Math.Abs(1 - Math.Abs(value)) < 1e-12 ? Math.Sign(value) : value;
    private void InvalidateCloudRaster()
    { _cloudVersion++; _packedColors = null; _hasRasterKey = false; _pointsetKey = null; }
    private void ResetRasterBuffers()
    {
        InvalidateCloudRaster(); _pixels = null; _depths = null; _wheelTimer?.Stop();
        _wheelInteraction = _animationInteraction = false; SampledPointCount = 0;
        _pointsetBitmap = null; _pointsetPixels = null; _pointsetDepths = null; _pointsetKey = null; _textCache.Clear();
    }
    private void PrepareColors()
    {
        var range = ColorRange; var c = _cloud;
        if (_packedColors?.Length == c.Count && _packedMode == ColorMode && _packedMin == range.Minimum && _packedMax == range.Maximum) return;
        _packedColors = new int[c.Count]; _packedMode = ColorMode; _packedMin = range.Minimum; _packedMax = range.Maximum;
        var scalars = c.DisplayScalars?.Length == c.Count ? c.DisplayScalars : null;
        var rgb = c.ColorsRgb?.Length == c.Count ? c.ColorsRgb : null;
        var z = CollectionsMarshal.AsSpan(c.Z); double inverse = 1 / Math.Max(1e-12, range.Maximum - range.Minimum);
        for (int i = 0; i < c.Count; i++)
        {
            if (ColorMode == "White") { _packedColors[i] = unchecked((int)0xffffffff); continue; }
            if (ColorMode == "RGB" && rgb != null) { _packedColors[i] = unchecked((int)0xff000000) | (int)(rgb[i] & 0xffffff); continue; }
            double scalar = scalars == null ? z[i] : scalars[i];
            double fraction = double.IsFinite(scalar) ? (scalar - range.Minimum) * inverse : .5;
            if (ColorMode == "Gray") { int gray = (int)Math.Clamp(fraction * 255, 0, 255); _packedColors[i] = unchecked((int)0xff000000) | gray << 16 | gray << 8 | gray; }
            else _packedColors[i] = rgb == null ? HeightPalette[Math.Clamp((int)Math.Floor(fraction * 1275), 0, 1275)] : Pack(HeightColor(fraction, rgb[i]));
        }
    }
    private void DrawCloud(DrawingContext dc, double w, double h)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = Math.Max(1, (int)Math.Ceiling(w * dpi.DpiScaleX)), ph = Math.Max(1, (int)Math.Ceiling(h * dpi.DpiScaleY));
        var c = _cloud; var visible = c.DisplayIndices; int count = visible?.Length ?? c.Count;
        // Keep the camera responsive while dragging; a release always schedules full detail.
        int stride = IsInteractionRendering ? Math.Max(1, (int)Math.Ceiling(count / 120000d)) : 1;
        var range = ColorRange;
        var key = new RasterKey(CaptureCamera(), _cloudVersion, pw, ph, dpi.DpiScaleX, dpi.DpiScaleY,
            ColorMode, range.Minimum, range.Maximum, Perspective, stride);
        LastRasterWasCached = _hasRasterKey && _rasterKey == key;
        if (!LastRasterWasCached)
        {
            var watch = Stopwatch.StartNew(); PrepareColors();
            if (_bitmap == null || _bitmap.PixelWidth != pw || _bitmap.PixelHeight != ph)
            {
                _bitmap = new WriteableBitmap(pw, ph, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Bgra32, null);
                _pixels = new int[checked(pw * ph)]; _depths = new float[_pixels.Length];
            }
            Array.Clear(_pixels); Array.Fill(_depths, float.NegativeInfinity);
            RasterPoints(c, visible, stride, _packedColors, null, _pixels, _depths, pw, ph, dpi.DpiScaleX, dpi.DpiScaleY,
                out _cachedRenderedPoints, out _cachedSampledPoints);
            _bitmap.WritePixels(new Int32Rect(0, 0, pw, ph), _pixels, pw * 4, 0);
            _rasterKey = key; _hasRasterKey = true; LastRasterMilliseconds = watch.Elapsed.TotalMilliseconds; RasterGeneration++;
        }
        RenderedPointCount = _cachedRenderedPoints; SampledPointCount = _cachedSampledPoints;
        dc.DrawImage(_bitmap, new Rect(0, 0, w, h));
    }
    private void RasterPoints(Algorithm.PointCloud cloud, int[] visible, int stride, int[] colors, int? tint,
        int[] pixels, float[] depths, int width, int height, double dpiX, double dpiY, out int rendered, out int sampled)
    {
        var xs = CollectionsMarshal.AsSpan(cloud.X); var ys = CollectionsMarshal.AsSpan(cloud.Y); var zs = CollectionsMarshal.AsSpan(cloud.Z);
        int count = visible?.Length ?? cloud.Count; rendered = sampled = 0;
        double m00 = _cosY, m01 = -_sinY, m10 = _sinY * _cosP, m11 = _cosY * _cosP, m12 = _sinP;
        double m20 = -_sinY * _sinP, m21 = -_cosY * _sinP, m22 = _cosP;
        double distance = Math.Max(1e-6, Math.Max(_x2 - _x1, Math.Max(_y2 - _y1, _z2 - _z1))) * 3;
        double scaleX = _scale * dpiX, scaleY = _scale * dpiY, ox = _ox * dpiX, oy = _oy * dpiY;
        bool rgb = cloud.ColorsRgb?.Length == cloud.Count;
        for (int position = 0; position < count; position += stride)
        {
            int i = visible == null ? position : visible[position]; if ((uint)i >= (uint)cloud.Count) continue;
            double x = xs[i] - _cx, y = ys[i] - _cy, z = zs[i] - _cz;
            if (!double.IsFinite(x + y + z)) continue;
            sampled++;
            double depth = m20 * x + m21 * y + m22 * z;
            double factor = Perspective ? distance / Math.Max(distance * .05, distance - depth) : 1;
            double px = ox + (m00 * x + m01 * y) * scaleX * factor;
            double py = oy - (m10 * x + m11 * y + m12 * z) * scaleY * factor;
            if (px < 0 || py < 0 || px >= width || py >= height) continue;
            int pixel = (int)py * width + (int)px;
            if (depth >= depths[pixel])
            {
                depths[pixel] = (float)depth;
                pixels[pixel] = tint ?? (colors != null ? colors[i] : unchecked((int)0xff000000) | (rgb ? (int)(cloud.ColorsRgb[i] & 0xffffff) : 0xffffff));
            }
            rendered++;
        }
    }
    private void DrawNativePointSets(DrawingContext dc)
    {
        var sets = _nativeOverlays.Where(o => o.Type.Equals("pointset", StringComparison.OrdinalIgnoreCase) && o.Cloud?.Count > 0).ToArray();
        if (sets.Length == 0) return;
        int total = sets.Sum(o => o.Cloud.Count), stride = IsInteractionRendering ? Math.Max(1, (int)Math.Ceiling(total / 120000d)) : 1;
        var key = _rasterKey with { Version = _overlayVersion, Stride = stride };
        if (_pointsetKey != key)
        {
            int width = key.Width, height = key.Height;
            if (_pointsetBitmap == null || _pointsetBitmap.PixelWidth != width || _pointsetBitmap.PixelHeight != height)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                _pointsetBitmap = new WriteableBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Bgra32, null);
                _pointsetPixels = new int[width * height]; _pointsetDepths = new float[_pointsetPixels.Length];
            }
            Array.Clear(_pointsetPixels); Array.Fill(_pointsetDepths, float.NegativeInfinity);
            foreach (var set in sets)
            {
                bool colored = set.Values.TryGetValue("_color", out var value) && !string.IsNullOrWhiteSpace(value?.ToString());
                int? tint = colored ? Pack(NativeOverlayData.Color(set.Values, "white")) : null;
                RasterPoints(set.Cloud, null, stride, null, tint, _pointsetPixels, _pointsetDepths, width, height, key.DpiX, key.DpiY, out _, out _);
            }
            _pointsetBitmap.WritePixels(new Int32Rect(0, 0, width, height), _pointsetPixels, width * 4, 0);
            _pointsetKey = key;
        }
        dc.DrawImage(_pointsetBitmap, new Rect(0, 0, ActualWidth, ActualHeight));
    }
}
