using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Views;
using OpenCvSharp;

namespace AiVisionStudio.Services;

internal static class ResultImageWriter2D
{
    public static void Write(Mat image,IReadOnlyList<NativeOverlay3D> overlays,string path)
    {
        void Render()
        {
            var viewer=new ImageView();
            try
            {
                viewer.ShowMat(image);viewer.SetNativeOverlays(overlays);var bitmap=viewer.RenderResultBitmap();
                BitmapEncoder encoder=Path.GetExtension(path).ToLowerInvariant() switch{".bmp"=>new BmpBitmapEncoder(),".jpg" or ".jpeg"=>new JpegBitmapEncoder(),".tif" or ".tiff"=>new TiffBitmapEncoder(),".png"=>new PngBitmapEncoder(),_=>throw new NotSupportedException("结果图像格式仅支持 BMP / PNG / JPEG / TIFF")};
                encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
            }
            finally{viewer.Clear();}
        }
        var dispatcher=Application.Current?.Dispatcher;
        if(dispatcher!=null&&!dispatcher.CheckAccess()){dispatcher.Invoke(Render);return;}
        if(Thread.CurrentThread.GetApartmentState()==ApartmentState.STA){Render();return;}
        Exception failure=null;var thread=new Thread(()=>{try{Render();}catch(Exception error){failure=error;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(failure!=null)throw new IOException("结果图像导出失败",failure);
    }
}
