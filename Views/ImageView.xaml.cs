using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AiVisionStudio.Algorithm;

namespace AiVisionStudio.Views;

public partial class ImageView : UserControl
{
    private bool _dragging;
    private Point _dragOrigin;
    private Matrix _dragMatrix;
    private BitmapSource _bitmap;
    private bool _userMoved;
    private Matrix _pendingViewTransform;
    private bool _viewTransformPending;


    public long DisplayedSnapshotVersion { get; set; }
    public long? ClearedSnapshotVersion { get; private set; }
    private int _fitRequestVersion;
    public ImageView()
    {
        InitializeComponent();
        Native2D.ImageTransform = Mat;
        RegionCanvas.ImageTransform = Mat;
        BuildViewTools();
        BuildRegionEditing();

        MouseWheel += OnWheel;
        PreviewMouseLeftButtonDown += (_, _) => ApplyPendingViewTransform();
        MouseLeftButtonDown += OnDown;
        MouseLeftButtonUp += OnUp;
        MouseMove += OnMove;
        Host.MouseLeave += (_, _) => UpdatePixelStatus(null);
        LostMouseCapture += (_, _) => { ApplyPendingViewTransform(); _dragging = false; };
        Unloaded += (_, _) => { ApplyPendingViewTransform(); _dragging = false; };

        Host.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 1 && e.NewSize.Height > 1)
                RequestFitView();
        };

        // 重新加载到页面时恢复默认适应视图
        Loaded += (_, _) => RequestFitView();
    }


    public bool HasImage => _bitmap != null;
    public RegionEditor2D RegionEditor => RegionCanvas;
    public void EditRegion(AiVisionStudio.Models.EditableRegion2D region, bool redraw = false)
    {
        CancelRegionEdit();
        ApplyPendingViewTransform();
        MeasurementEnabled = false;
        RegionCanvas.ShowRegion(region, true, redraw);
        _tools["measure"].IsEnabled = false;
        RegionCanvas.Focus();
    }
    public void EndRegionEdit(bool clear = false)
    {
        EndRegionPan();
        RegionCanvas.StopEditing(clear);
        _regionHandMode = false;
        UpdateRegionPanCursor();
        _tools["measure"].IsEnabled = true;
    }
    public bool HasCloud => Cloud3D.Visibility == Visibility.Visible && Cloud3D.HasCloud;
    public bool HasProfile => Profile.Visibility == Visibility.Visible && Profile.HasProfile;
    public int ViewIndex { get; set; }
    public string OverlayText { get => TextOverlay.Text; set => TextOverlay.Text = value ?? ""; }

    private void SetSource(BitmapSource bs)
    {
        // The canvas arranges the full source image before applying the view transform;
        // a Grid would clip the unscaled image to its layout slot in a narrow viewport.
        Img.Stretch = Stretch.Fill;
        Img.HorizontalAlignment = HorizontalAlignment.Left;
        Img.VerticalAlignment = VerticalAlignment.Top;
        Img.Source = bs;
        RegionCanvas.ImageWidth = bs.PixelWidth;
        RegionCanvas.ImageHeight = bs.PixelHeight;
        _pixelBitmap = null;
        Img.Width = bs.PixelWidth;
        Img.Height = bs.PixelHeight;
        Img.Visibility = Visibility.Visible;
        Cloud3D.Visibility = Visibility.Collapsed;
        Profile.Visibility = Visibility.Collapsed;
        Native2D.Visibility = Visibility.Visible;
        Native2D.SetOverlays(null, bs.PixelWidth, bs.PixelHeight);
        Img.InvalidateMeasure();
        Img.InvalidateVisual();
    }

    public void Load(string path)
    {
        CancelRegionEdit();
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            _bitmap = bmp;
            _rawImage?.Dispose(); _rawImage = null;
            _resultOverlays = Array.Empty<NativeOverlay3D>(); MeasurementEnabled = false;
            SetSource(bmp);
            Placeholder.Visibility = Visibility.Collapsed;
            InfoBar.Visibility = Visibility.Visible;
            InfoText.Text = $"{Path.GetFileName(path)}   {bmp.PixelWidth} x {bmp.PixelHeight}   {bmp.Format}";
            SetImageStatus(Path.GetFileName(path));
            _userMoved = false;
            Fit();
            UpdateViewTools();
        }
        catch (Exception ex)
        {
            Clear();
            Placeholder.Text = "无法加载图像：" + ex.Message;
            Placeholder.Visibility = Visibility.Visible;
        }
    }

    public void ShowMat(OpenCvSharp.Mat mat, string imageName = null)
    {
        if (mat == null || mat.Empty()) { Clear(); return; }
        try
        {
            if (_bitmap?.PixelWidth != mat.Width || _bitmap?.PixelHeight != mat.Height) CancelRegionEdit();
            var sameSize = _bitmap?.PixelWidth == mat.Width && _bitmap?.PixelHeight == mat.Height;
            _rawImage?.Dispose(); _rawImage = mat.Clone();
            RenderImage();
            var w = mat.Width; var h = mat.Height;
            Placeholder.Visibility = Visibility.Collapsed;
            InfoBar.Visibility = Visibility.Visible;
            InfoText.Text = $"IM  {w} x {h}";
            SetImageStatus(imageName);
            if (!sameSize || !_userMoved && Mat.Matrix.IsIdentity) { _userMoved = false; Fit(); }
            ApplyImageOverlays(); UpdateViewTools();
        }
        catch (Exception ex)
        {
            Clear();
            Placeholder.Text = "显示失败：" + ex.Message;
            Placeholder.Visibility = Visibility.Visible;
        }
    }

    /// <summary>显示 3D 点云（含文字标注/包围盒），替代原版的 3D 视图。</summary>
    public void ShowCloud(PointCloud cloud, IEnumerable<Text3D> texts = null, IEnumerable<Box3> boxes = null)
    {
        CancelPendingViewTransform();
        EndRegionEdit(true);
        CancelRegionEdit();
        if (cloud == null || cloud.Count == 0) { Clear(); return; }
        _bitmap = null;
        SetThumbnailSource(null, Array.Empty<string>(), null);
        ClearImageStatus();
        _rawImage?.Dispose(); _rawImage = null;
        OverlayText = "";
        Img.Source = null;
        Img.Visibility = Visibility.Collapsed;
        Cloud3D.Visibility = Visibility.Visible;
        Profile.Visibility = Visibility.Collapsed;
        Native2D.Visibility = Visibility.Collapsed;
        Cloud3D.Show(cloud.Clone(), texts, boxes);
        Placeholder.Visibility = Visibility.Collapsed;
        InfoBar.Visibility = Visibility.Collapsed;
        InfoText.Text = $"点云 {cloud.Count:N0} 点  (左键旋转 / 右键平移 / 滚轮缩放)";
        UpdateViewTools();
    }

    public void SetCloudOverlays(IEnumerable<Text3D> texts, IEnumerable<Box3> boxes = null)
        => Cloud3D.SetOverlays(texts, boxes);

    public void ShowProfile(
    ProfileData profile,
    bool preserveView = false)
    {
        CancelRegionEdit();
        ClearImageStatus();
        CancelPendingViewTransform();
        EndRegionEdit(true);
        if (profile?.Count is null or 0)
        {
            Clear();
            return;
        }

        bool keepView = preserveView && HasProfile;

        _bitmap = null;
        Img.Source = null;
        Img.Visibility = Visibility.Collapsed;

        _rawImage?.Dispose();
        _rawImage = null;

        Cloud3D.Visibility = Visibility.Collapsed;
        Native2D.Visibility = Visibility.Collapsed;

        Profile.Visibility = Visibility.Visible;
        Profile.Show(profile, keepView);

        Placeholder.Visibility = Visibility.Collapsed;
        InfoBar.Visibility = Visibility.Collapsed;
        OverlayText = "";

        UpdateViewTools();
    }

    public CloudView3D.CameraState CaptureCloudCamera() => Cloud3D.CaptureCamera();
    public void RestoreCloudCamera(CloudView3D.CameraState state) => Cloud3D.RestoreCamera(state);

    public void SetNativeOverlays(IEnumerable<NativeOverlay3D> overlays)
    {
        var list = overlays?.ToArray() ?? Array.Empty<NativeOverlay3D>();
        _resultOverlays = list;
        Cloud3D.SetNativeOverlays(HasCloud ? list : null);
        ApplyImageOverlays();
        Profile.SetOverlays(HasProfile ? list : null);
    }

    public void ClearDisplay(long? clearedAtVersion = null)
    {
        var version = Math.Max(ClearedSnapshotVersion ?? DisplayedSnapshotVersion, clearedAtVersion ?? 0);
        Clear();
        ClearedSnapshotVersion = version;
    }

    public void ResumeSnapshotDisplay() => ClearedSnapshotVersion = null;

    public void Clear()
    {
        SetThumbnailSource(null, Array.Empty<string>(), null);
        CancelPendingViewTransform();
        EndRegionEdit(true);
        DisplayedSnapshotVersion = 0;
        ClearedSnapshotVersion = null;
        _fitRequestVersion++;
        CancelRegionEdit();
        _dragging = false;
        _userMoved = false;
        ReleaseMouseCapture();
        _bitmap = null;
        ClearImageStatus();
        _rawImage?.Dispose(); _rawImage = null; _resultOverlays = Array.Empty<NativeOverlay3D>();
        _rotateTimer?.Stop(); MeasurementEnabled = false;
        OverlayText = "";
        Img.Source = null;
        Img.Visibility = Visibility.Visible;
        Cloud3D.Visibility = Visibility.Visible;
        Cloud3D.Reset();
        Profile.Visibility = Visibility.Collapsed; Profile.Show(null); Profile.SetOverlays(null);
        Cloud3D.SetNativeOverlays(null);
        Native2D.SetOverlays(null, 0, 0);
        Placeholder.Text = "";
        Placeholder.Visibility = Visibility.Visible;
        InfoBar.Visibility = Visibility.Collapsed;
        Mat.Matrix = Matrix.Identity;
        UpdateViewTools();
    }

    public void FitView()
    {
        // 取消尚未执行的旧适配请求
        _fitRequestVersion++;
        _userMoved = false;
        if (HasCloud)
            Cloud3D.ResetView();
        else if (HasProfile)
            Profile.Fit();
        else
            Fit();
    }

    public void RequestFitView()
    {
        int request = ++_fitRequestVersion;

        // 等窗口完成布局后，再使用新的实际尺寸
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (request != _fitRequestVersion || !IsLoaded)
                    return;

                UpdateLayout();

                if (request != _fitRequestVersion|| Host.ActualWidth <= 1|| Host.ActualHeight <= 1)
                {
                    return;
                }

                if (!HasImage && !HasCloud && !HasProfile)
                    return;

                FitView();
            }));
    }

    public BitmapSource RenderResultBitmap()
    {
        BitmapSource source = _bitmap;
        if (source != null && ResultsVisible && _resultOverlays.Length > 0)
        {
            var width = source.PixelWidth; var height = source.PixelHeight;
            var overlay = new NativeOverlay2D { ImageTransform = new MatrixTransform(Matrix.Identity) };
            overlay.SetOverlays(_resultOverlays, width, height); overlay.Measure(new Size(width, height)); overlay.Arrange(new Rect(0, 0, width, height));
            var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) { dc.DrawImage(source, new Rect(0, 0, width, height)); dc.DrawRectangle(new VisualBrush(overlay), null, new Rect(0, 0, width, height)); }
            var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); result.Render(visual); source = result;
        }
        if (source == null && (HasCloud || HasProfile))
        {
            FrameworkElement visual = HasProfile ? Profile : Cloud3D;
            var dpi = VisualTreeHelper.GetDpi(visual);
            var capture = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY)),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            capture.Render(visual);
            source = capture;
        }
        if (source == null) throw new InvalidOperationException("当前视图没有图像");
        return source;
    }
    public void SavePng(string path)
    {
        var source = RenderResultBitmap();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = File.Create(path);
        encoder.Save(output);
    }



    public void Fit()
    {
        CancelPendingViewTransform();
        if (_bitmap == null) return;
        var vw = Host.ActualWidth;
        var vh = Host.ActualHeight;
        if (vw <= 1 || vh <= 1) return;
        var s = Math.Min(vw / _bitmap.PixelWidth, vh / _bitmap.PixelHeight) * 0.96;
        var m = Matrix.Identity;
        m.Scale(s, s);
        m.Translate((vw - _bitmap.PixelWidth * s) / 2, (vh - _bitmap.PixelHeight * s) / 2);
        _userMoved = false;
        Mat.Matrix = m;
        UpdatePixelStatus(Host.IsMouseOver ? Mouse.GetPosition(Host) : null);
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_bitmap == null || !Host.IsMouseOver || e.Delta == 0) return;
        if (_regionPanning) { e.Handled = true; return; }
        var m = _viewTransformPending ? _pendingViewTransform : Mat.Matrix;
        var currentScale = Math.Max(.0001, Math.Abs(m.M11));
        var targetScale = Math.Clamp(currentScale * Math.Pow(1.15, e.Delta / 120d), 0.001, 1000);
        var k = targetScale / currentScale;
        var pos = e.GetPosition(Host);
        m.ScaleAt(k, k, pos.X, pos.Y);
        QueueViewTransform(m);
        _userMoved = true;
        UpdatePixelStatus(pos);
        e.Handled = true;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (IsRegionEditing || RegionCanvas.IsEditing || _bitmap == null || !Host.IsMouseOver) return;
        ApplyPendingViewTransform();
        if (MeasurementEnabled)
        {
            var inverse = Mat.Matrix; if (!inverse.HasInverse) return; inverse.Invert(); MeasureImageAt(inverse.Transform(e.GetPosition(Host))); e.Handled = true; return;
        }
        if (e.ClickCount == 2)
        {
            _dragging = false; ReleaseMouseCapture(); ReopenThumbnailPanel(); Fit(); e.Handled = true; return;
        }

        // 普通模式：单击开始拖动。
        _dragOrigin = e.GetPosition(Host);
        _dragging = true;
        _dragMatrix = Mat.Matrix;

        CaptureMouse();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(Host);
        bool click = _dragging && e.ChangedButton == MouseButton.Left && (position - _dragOrigin).Length <= 4;
        if (_dragging) QueuePan(e.GetPosition(Host));
        ApplyPendingViewTransform();
        _dragging = false;
        ReleaseMouseCapture();
        if (click && HasImage && Mat.Matrix.HasInverse && !IsRegionEditing && !RegionCanvas.IsEditing && !MeasurementEnabled)
        {
            TryGetImagePoint(position, out var point);
            ImagePointClicked?.Invoke(this, new ImagePointClickedEventArgs(point));
        }
    }
    
    private void OnMove(object sender, MouseEventArgs e)
    {
        if (Host.IsMouseOver) UpdatePixelStatus(e.GetPosition(Host));
        if (!_dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { ApplyPendingViewTransform(); _dragging = false; ReleaseMouseCapture(); return; }
        QueuePan(e.GetPosition(Host));
    }

    private void QueuePan(Point p)
    {
        var m = _dragMatrix;
        m.Translate(p.X - _dragOrigin.X, p.Y - _dragOrigin.Y);
        QueueViewTransform(m);
        _userMoved = true;
    }

    private void QueueViewTransform(Matrix matrix)
    {
        _pendingViewTransform = matrix;
        if (_viewTransformPending) return;
        _viewTransformPending = true;
        CompositionTarget.Rendering += OnViewTransformFrame;
    }

    private void OnViewTransformFrame(object sender, EventArgs e) => ApplyPendingViewTransform();

    private void ApplyPendingViewTransform()
    {
        if (!_viewTransformPending) return;
        CancelPendingViewTransform();
        if (HasImage) Mat.Matrix = _pendingViewTransform;
    }

    private void CancelPendingViewTransform()
    {
        if (_viewTransformPending) CompositionTarget.Rendering -= OnViewTransformFrame;
        _viewTransformPending = false;
    }

    private static BitmapSource ToBitmap(OpenCvSharp.Mat mat, out int w, out int h)
    {
        var src = mat;
        using var normalized = new OpenCvSharp.Mat();
        using var bgr = new OpenCvSharp.Mat();
        // WPF Bgr24 requires 8-bit channels. Convert depth before expanding a
        // 16-bit grayscale image, otherwise its samples get interpreted as bytes.
        if (src.Depth() != OpenCvSharp.MatType.CV_8U)
        {
            OpenCvSharp.Cv2.Normalize(src, normalized, 0, 255,
                OpenCvSharp.NormTypes.MinMax, OpenCvSharp.MatType.CV_8U);
            src = normalized;
        }
        if (src.Channels() == 1)
        {
            OpenCvSharp.Cv2.CvtColor(src, bgr, OpenCvSharp.ColorConversionCodes.GRAY2BGR);
        }
        else if (src.Channels() == 4)
        {
            OpenCvSharp.Cv2.CvtColor(src, bgr, OpenCvSharp.ColorConversionCodes.BGRA2BGR);
        }
        else if (src.Channels() != 3) throw new NotSupportedException($"不支持显示 {src.Channels()} 通道图像");
        var use = bgr.Empty() ? src : bgr;
        w = use.Width;
        h = use.Height;
        var bytes = new byte[w * h * 3];
        // An OpenCV ROI can have padding between rows; copy each row's pixels.
        for (var row = 0; row < h; row++)
            System.Runtime.InteropServices.Marshal.Copy(use.Ptr(row), bytes, row * w * 3, w * 3);
        var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, bytes, w * 3);
        bs.Freeze();
        return bs;
    }
}
