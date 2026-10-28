using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AiVisionStudio.Views;

/// <summary>Fit borderless windows to the current monitor's work area, including DPI/display changes.</summary>
public static class WindowScreenBehavior
{
    public static void Attach(Window window)
    {
        var minWidth=window.MinWidth;var minHeight=window.MinHeight;
        void Fit()=>FitToMonitor(window,minWidth,minHeight);
        window.Loaded+=(_,_)=>Fit();window.DpiChanged+=(_,_)=>window.Dispatcher.BeginInvoke(Fit);
        window.SourceInitialized+=(_,_)=>{
            var source=(HwndSource)PresentationSource.FromVisual(window);
            IntPtr Hook(IntPtr hwnd,int message,IntPtr w,IntPtr l,ref bool handled)
            {
                if(message==0x24)
                {
                    var info=Monitor(hwnd);var bounds=Marshal.PtrToStructure<MinMax>(l);
                    bounds.MaxPosition=new(info.Work.Left-info.Bounds.Left,info.Work.Top-info.Bounds.Top);
                    bounds.MaxSize=new(info.Work.Right-info.Work.Left,info.Work.Bottom-info.Work.Top);
                    var dpi=VisualTreeHelper.GetDpi(window);
                    bounds.MinTrack=new((int)Math.Min(window.MinWidth*dpi.DpiScaleX,bounds.MaxSize.X),(int)Math.Min(window.MinHeight*dpi.DpiScaleY,bounds.MaxSize.Y));
                    Marshal.StructureToPtr(bounds,l,false);handled=true;
                }
                if(message is 0x7E or 0x1A or 0x232)window.Dispatcher.BeginInvoke(Fit);
                return IntPtr.Zero;
            }
            source.AddHook(Hook);window.Closed+=(_,_)=>source.RemoveHook(Hook);
        };
    }
    public static Size FittedSize(Size requested,Size work)=>new(Math.Min(requested.Width,work.Width),Math.Min(requested.Height,work.Height));
    private static void FitToMonitor(Window window,double minWidth,double minHeight)
    {
        var hwnd=new WindowInteropHelper(window).Handle;if(hwnd==IntPtr.Zero)return;
        var monitor=Monitor(hwnd);var dpi=VisualTreeHelper.GetDpi(window);var workWidth=(monitor.Work.Right-monitor.Work.Left)/dpi.DpiScaleX;var workHeight=(monitor.Work.Bottom-monitor.Work.Top)/dpi.DpiScaleY;
        window.MinWidth=Math.Min(minWidth,workWidth);window.MinHeight=Math.Min(minHeight,workHeight);
        if(window.WindowState!=WindowState.Normal)return;
        window.Width=Math.Min(window.Width,workWidth);window.Height=Math.Min(window.Height,workHeight);
        if(!GetWindowRect(hwnd,out var rect))return;
        var width=Math.Min(rect.Right-rect.Left,monitor.Work.Right-monitor.Work.Left);var height=Math.Min(rect.Bottom-rect.Top,monitor.Work.Bottom-monitor.Work.Top);
        var left=Math.Clamp(rect.Left,monitor.Work.Left,monitor.Work.Right-width);var top=Math.Clamp(rect.Top,monitor.Work.Top,monitor.Work.Bottom-height);
        if(left!=rect.Left||top!=rect.Top||width!=rect.Right-rect.Left||height!=rect.Bottom-rect.Top)SetWindowPos(hwnd,IntPtr.Zero,left,top,width,height,0x14);
    }
    private static MonitorInfo Monitor(IntPtr hwnd){var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};GetMonitorInfo(MonitorFromWindow(hwnd,2),ref info);return info;}
    [StructLayout(LayoutKind.Sequential)]private struct NativePoint {public int X,Y;public NativePoint(int x,int y){X=x;Y=y;}}
    [StructLayout(LayoutKind.Sequential)]private struct NativeRect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct MonitorInfo {public int Size;public NativeRect Bounds,Work;public int Flags;}
    [StructLayout(LayoutKind.Sequential)]private struct MinMax {public NativePoint Reserved,MaxSize,MaxPosition,MinTrack,MaxTrack;}
    [DllImport("user32.dll")]private static extern IntPtr MonitorFromWindow(IntPtr hwnd,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr hwnd,out NativeRect rect);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
}
