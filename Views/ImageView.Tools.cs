using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AiVisionStudio.Algorithm;

namespace AiVisionStudio.Views;

public partial class ImageView
{
    private readonly Dictionary<string,Button> _tools=new();
    private NativeOverlay3D[] _resultOverlays=Array.Empty<NativeOverlay3D>();
    private OpenCvSharp.Mat _rawImage;
    private string _imageColorMode="Original";
    private bool _resultsVisible=true,_infoVisible,_boundsVisible,_axesVisible,_measureMode;
    private readonly List<Point> _measurePoints=new();
    private DispatcherTimer _rotateTimer;
    public event Action<ImageView,bool> SaveRequested;
    public bool ResultsVisible { get=>_resultsVisible; set{_resultsVisible=value;Cloud3D.ResultsVisible=value;Cloud3D.InvalidateVisual();ApplyImageOverlays();TextOverlay.Visibility=value?Visibility.Visible:Visibility.Collapsed;} }
    public string MeasurementText { get; private set; }="";
    public bool MeasurementEnabled { get=>_measureMode;set{_measureMode=value;Cloud3D.DistanceMode=value;_measurePoints.Clear();Cloud3D.ClearMeasurement();Measure2D.SetOverlays(null,0,0);MeasurementText="";Cursor=value?Cursors.Cross:Cursors.Arrow;} }
    private void BuildViewTools()
    {
        Measure2D.ImageTransform=Mat;
        Add("info","图像信息",()=>{_infoVisible=!_infoVisible;UpdateViewTools();});
        Add("render","渲染方式",ShowRenderMenu);
        Add("results","显示 / 隐藏检测结果",()=>ResultsVisible=!ResultsVisible);
        Add("bounds","包围盒",()=>{_boundsVisible=!_boundsVisible;Cloud3D.BoundsVisible=_boundsVisible;Cloud3D.InvalidateVisual();ApplyImageOverlays();});
        Add("fit","图像居中 / 适应窗口",FitView);
        Add("save","保存原始图像 / 点云",()=>SaveRequested?.Invoke(this,true));
        Add("screenshot","保存检测结果截图",()=>SaveRequested?.Invoke(this,false));
        Add("range","点云色带深度范围",ShowColorRange);
        Add("axes","显示 / 隐藏坐标轴",()=>{if(HasCloud)Cloud3D.AxesVisible=!Cloud3D.AxesVisible;else _axesVisible=!_axesVisible;Cloud3D.InvalidateVisual();ApplyImageOverlays();});
        Add("camera","视角 / 投影方式",ShowCameraMenu);
        Add("pan","手形工具：拖动画面平移",()=>{if(HasImage){ActivateImagePan();return;}Cloud3D.PanWithLeftButton=!Cloud3D.PanWithLeftButton;_tools["pan"].ToolTip=Cloud3D.PanWithLeftButton?"当前左键平移；点击切换旋转":"当前左键旋转；点击切换平移";});
        Add("rotate","自动旋转",ToggleAutoRotate);
        Add("obb","主轴方向包围盒",()=>{Cloud3D.OrientedBoundsVisible=!Cloud3D.OrientedBoundsVisible;Cloud3D.InvalidateVisual();});
        Add("measure","两点距离测量",()=>{MeasurementEnabled=!MeasurementEnabled;InfoText.Text=MeasurementEnabled?"依次点击两个点；再次点击标尺退出":"";InfoBar.Visibility=MeasurementEnabled?Visibility.Visible:Visibility.Collapsed;});
        Unloaded+=(_,_)=>{_rotateTimer?.Stop();Cloud3D.SetInteractiveRendering(false);};
        UpdateViewTools();
        void Add(string id,string tip,Action action)
        {
            var button=new Button{Content=ViewGlyph.Create(id),ToolTip=tip,Width=28,Height=27,Padding=new Thickness(3),
                Background=Brushes.Transparent,BorderThickness=new Thickness(0),Focusable=false};
            if(TryFindResource("CaptionButton") is Style style)button.Style=style;
            button.Click+=(_,e)=>{action();e.Handled=true;};_tools[id]=button;ViewTools.Children.Add(button);
        }
    }
    private void UpdateViewTools()
    {
        foreach(var key in new[]{"range","camera","rotate","obb"})_tools[key].IsEnabled=HasCloud;
        _tools["pan"].IsEnabled=HasImage||HasCloud;
        if(HasImage)_tools["pan"].ToolTip="手形工具：点击后左键平移，再次点击返回框选；也可用 Alt+左键或中键平移";
        _tools["save"].IsEnabled=HasImage||HasCloud;
        _tools["screenshot"].IsEnabled=HasImage||HasCloud||HasProfile;
        InfoBar.Visibility=_infoVisible||MeasurementEnabled?Visibility.Visible:Visibility.Collapsed;
    }
    private void Menu(string tool,params (string Text,Action Action,bool Checked)[] items)
    {
        var menu=new ContextMenu{PlacementTarget=_tools[tool]};
        foreach(var item in items){var entry=new MenuItem{Header=item.Text,IsCheckable=true,IsChecked=item.Checked};entry.Click+=(_,_)=>item.Action();menu.Items.Add(entry);}menu.IsOpen=true;
    }
    private void ShowRenderMenu()
    {
        if(HasCloud)
            Menu("render",new[]{("高度色谱","Height"),("灰度","Gray"),("原始 RGB","RGB"),("纯白","White")}
                .Select(item=>(item.Item1,(Action)(()=>{Cloud3D.ColorMode=item.Item2;Cloud3D.InvalidateVisual();}),Cloud3D.ColorMode==item.Item2)).ToArray());
        else Menu("render",new[]{("原始图像","Original"),("灰度","Gray"),("反色","Invert"),("伪彩色","Color")}
            .Select(item=>(item.Item1,(Action)(()=>SetImageColorMode(item.Item2)),_imageColorMode==item.Item2)).ToArray());
    }
    public void SetImageColorMode(string mode)
    {
        if(mode is not("Original" or "Gray" or "Invert" or "Color"))throw new ArgumentException("未知渲染方式");
        _imageColorMode=mode;if(_rawImage==null)return;
        RenderImage();ApplyImageOverlays();
    }
    private void RenderImage()
    {
        using var display=new OpenCvSharp.Mat();
        if(_imageColorMode=="Original")_rawImage.CopyTo(display);
        else
        {
            using var gray=Algorithm.Impl.Img.ToGray8(_rawImage);
            if(_imageColorMode=="Gray")gray.CopyTo(display);
            else if(_imageColorMode=="Invert")OpenCvSharp.Cv2.BitwiseNot(_rawImage,display);
            else OpenCvSharp.Cv2.ApplyColorMap(gray,display,OpenCvSharp.ColormapTypes.Jet);
        }
        _bitmap=ToBitmap(display,out _,out _);SetSource(_bitmap);
    }
    private void ShowCameraMenu()
        =>Menu("camera",("俯视 XY",()=>Cloud3D.SetView("Top"),false),("正视 XZ",()=>Cloud3D.SetView("Front"),false),
            ("后视 XZ",()=>Cloud3D.SetView("Back"),false),
            ("左视 YZ",()=>Cloud3D.SetView("Left"),false),("右视 YZ",()=>Cloud3D.SetView("Right"),false),
            ("底视 XY",()=>Cloud3D.SetView("Bottom"),false),("等轴视图",()=>Cloud3D.SetView("Isometric"),false),
            ("透视投影",()=>{Cloud3D.Perspective=!Cloud3D.Perspective;Cloud3D.InvalidateVisual();},Cloud3D.Perspective),
            ("显示网格",()=>{Cloud3D.GridVisible=!Cloud3D.GridVisible;Cloud3D.InvalidateVisual();},Cloud3D.GridVisible));
    private void ToggleAutoRotate()
    {
        if(_rotateTimer==null){_rotateTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(40)};_rotateTimer.Tick+=(_,_)=>{if(!HasCloud){_rotateTimer.Stop();Cloud3D.SetInteractiveRendering(false);return;}Cloud3D.RotateBy(.012);};}
        if(_rotateTimer.IsEnabled){_rotateTimer.Stop();Cloud3D.SetInteractiveRendering(false);}else{Cloud3D.SetInteractiveRendering(true);_rotateTimer.Start();}
    }
    private void ShowColorRange()
    {
        var menu=new ContextMenu{PlacementTarget=_tools["range"]};
        var panel=new StackPanel{Margin=new Thickness(10),Width=190};
        var range=Cloud3D.ColorRange;
        var min=new TextBox{Text=range.Minimum.ToString("G9",CultureInfo.InvariantCulture)};
        var max=new TextBox{Text=range.Maximum.ToString("G9",CultureInfo.InvariantCulture)};
        var error=new TextBlock{Foreground=Brushes.Orange,TextWrapping=TextWrapping.Wrap};
        panel.Children.Add(new TextBlock{Text="色带下限"});panel.Children.Add(min);panel.Children.Add(new TextBlock{Text="色带上限"});panel.Children.Add(max);
        var apply=new Button{Content="应用",Margin=new Thickness(0,8,0,0)};
        apply.Click+=(_,_)=>{try{Cloud3D.SetColorRange(double.Parse(min.Text,CultureInfo.InvariantCulture),double.Parse(max.Text,CultureInfo.InvariantCulture));menu.IsOpen=false;}catch(Exception ex)when(ex is FormatException or ArgumentException){error.Text=ex.Message;}};
        var reset=new Button{Content="自动范围"};reset.Click+=(_,_)=>{Cloud3D.SetColorRange(null,null);menu.IsOpen=false;};
        panel.Children.Add(apply);panel.Children.Add(reset);panel.Children.Add(error);menu.Items.Add(panel);menu.IsOpen=true;
    }
    private void ApplyImageOverlays()
    {
        var list=new List<NativeOverlay3D>();if(HasImage&&ResultsVisible)list.AddRange(_resultOverlays);
        if(HasImage&&_boundsVisible)list.Add(new(){Type="rect2d",Values=new(){["Center"]=XY(_bitmap.PixelWidth/2d,_bitmap.PixelHeight/2d),["Size"]=new Dictionary<string,object>{{"Width",_bitmap.PixelWidth},{"Height",_bitmap.PixelHeight}},["Rotation"]=0,["_color"]="#5599CC"}});
        if(HasImage&&_axesVisible)
        {
            list.Add(new(){Type="segment2d",Values=new(){["Start"]=XY(0,0),["End"]=XY(_bitmap.PixelWidth,0),["_color"]="#FF5555"}});
            list.Add(new(){Type="segment2d",Values=new(){["Start"]=XY(0,0),["End"]=XY(0,_bitmap.PixelHeight),["_color"]="#55FF55"}});
        }
        Native2D.SetOverlays(HasImage?list:null,_bitmap?.PixelWidth??0,_bitmap?.PixelHeight??0);
    }
    private static Dictionary<string,object> XY(double x,double y)=>new(){{"X",x},{"Y",y}};
    public bool MeasureImageAt(Point image)
    {
        if(!HasImage||!MeasurementEnabled||image.X<0||image.Y<0||image.X>=_bitmap.PixelWidth||image.Y>=_bitmap.PixelHeight)return false;
        if(_measurePoints.Count==2)_measurePoints.Clear();_measurePoints.Add(image);
        var overlays=_measurePoints.Select(p=>new NativeOverlay3D{Type="point2d",Values=new(){{"X",p.X},{"Y",p.Y},{"_color","#FFFF00"},{"_marker","cross"},{"_size",5}}}).ToList();
        MeasurementText="已选第一个点，请选择第二个点";
        if(_measurePoints.Count==2)
        {
            var a=_measurePoints[0];var b=_measurePoints[1];MeasurementText=(b-a).Length.ToString("0.###",CultureInfo.InvariantCulture)+" px";
            overlays.Add(new(){Type="segment2d",Values=new(){{"Start",XY(a.X,a.Y)},{"End",XY(b.X,b.Y)},{"_color","#FFFF00"}}});
            overlays.Add(new(){Type="text2d",Values=new(){{"Position",XY((a.X+b.X)/2,(a.Y+b.Y)/2)},{"Text",MeasurementText},{"_size",14},{"_color","#FFFF00"}}});
        }
        Measure2D.SetOverlays(overlays,_bitmap.PixelWidth,_bitmap.PixelHeight);InfoText.Text=MeasurementText;return true;
    }
    public void SaveRaw(string path)
    {
        if(HasCloud){PcdIO.Save(path,Cloud3D.SnapshotCloud());return;}
        if(_rawImage!=null){if(!OpenCvSharp.Cv2.ImWrite(path,_rawImage))throw new IOException("保存图像失败");return;}
        if(HasImage){EncodePng(_bitmap,path);return;}
        throw new InvalidOperationException("当前视图没有可保存的原始数据");
    }
    public void SaveScreenshot(string path)
    {
        if(!HasImage&&!HasCloud&&!HasProfile)throw new InvalidOperationException("当前视图没有图像");
        var dpi=VisualTreeHelper.GetDpi(Host);var capture=new RenderTargetBitmap(Math.Max(1,(int)Math.Ceiling(Host.ActualWidth*dpi.DpiScaleX)),Math.Max(1,(int)Math.Ceiling(Host.ActualHeight*dpi.DpiScaleY)),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
        capture.Render(Host);EncodePng(capture,path);
    }
    private static void EncodePng(BitmapSource source,string path)
    {var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(source));using var stream=File.Create(path);encoder.Save(stream);}
}
