using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using OpenCvSharp;
#if HIK_MVS
using MvCameraControl;
#endif

namespace AiVisionStudio.Services;

public sealed record HikCameraInfo(string Serial,string Model,string Address);

/// <summary>Calls the installed Hikrobot MVS SDK; image buffers are copied before being returned to the SDK.</summary>
public static class HikMvsService
{
    private static readonly object Gate=new();
    public static string InstalledSdkPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"MVS","Development","DotNet","win64","netstandard2.0","MvCameraControl.Net.dll");
    private static void LoadSdk()
    {
        if(!File.Exists(InstalledSdkPath))throw new FileNotFoundException("未安装海康 MVS .NET SDK，请安装 MVS 或设置厂家 SDK 适配器",InstalledSdkPath);
        Assembly.LoadFrom(InstalledSdkPath);
    }
    public static IReadOnlyList<HikCameraInfo> Enumerate(){LoadSdk();return EnumerateCore();}
    public static Task<DeviceFrame> AcquireAsync(DeviceProfile profile,CancellationToken token)=>Task.Run(()=>{LoadSdk();return AcquireCore(profile,token);},token);
#if HIK_MVS
    private static void Check(int code,string operation){if(code!=MvError.MV_OK)throw new IOException($"海康 {operation} 失败：0x{code:X8}");}
    private static List<IDeviceInfo> Devices()
    {
        Check(DeviceEnumerator.EnumDevices(DeviceTLayerType.MvGigEDevice|DeviceTLayerType.MvUsbDevice,out var devices),"枚举");return devices;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]private static IReadOnlyList<HikCameraInfo> EnumerateCore()
    {
        lock(Gate)
        {
            SDKSystem.Initialize();
            try{return Devices().Select(d=>new HikCameraInfo(d.SerialNumber,d.ModelName,d is IGigEDeviceInfo g?$"{g.CurrentIp>>24}.{(g.CurrentIp>>16)&255}.{(g.CurrentIp>>8)&255}.{g.CurrentIp&255}":"USB")).ToArray();}
            finally{SDKSystem.Finalize();}
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]private static DeviceFrame AcquireCore(DeviceProfile p,CancellationToken token)
    {
        lock(Gate)
        {
            token.ThrowIfCancellationRequested();SDKSystem.Initialize();IDevice device=null;bool grabbing=false;
            try
            {
                var devices=Devices();var info=!string.IsNullOrWhiteSpace(p.SerialNumber)?devices.SingleOrDefault(d=>d.SerialNumber==p.SerialNumber):devices.ElementAtOrDefault(p.CameraIndex);
                if(info==null)throw new IOException($"没有找到海康相机：序列号 {p.SerialNumber}，枚举到 {devices.Count} 台");
                device=DeviceFactory.CreateDevice(info);Check(device.Open(),"打开相机");
                if(device is IGigEDevice gige&&gige.GetOptimalPacketSize(out var packetSize)==MvError.MV_OK&&packetSize>0)Check(device.Parameters.SetIntValue("GevSCPSPacketSize",packetSize),"设置包大小");
                Check(device.Parameters.SetEnumValueByString("TriggerMode",p.TriggerMode=="连续"?"Off":"On"),"设置触发模式");
                if(p.TriggerMode!="连续")Check(device.Parameters.SetEnumValueByString("TriggerSource",p.TriggerMode=="软触发"?"Software":p.TriggerSource),"设置触发源");
                if(p.ExposureUs>0)Check(device.Parameters.SetFloatValue("ExposureTime",(float)p.ExposureUs),"设置曝光");
                Check(device.StreamGrabber.StartGrabbing(),"开始采集");grabbing=true;
                if(p.TriggerMode=="软触发")Check(device.Parameters.SetCommandValue("TriggerSoftware"),"软件触发");
                Check(device.StreamGrabber.GetImageBuffer((uint)p.TimeoutMs,out var frame),"获取图像");
                try
                {
                    token.ThrowIfCancellationRequested();var image=frame.Image;IImage converted=null;
                    try
                    {
                        if(image.PixelType is not (MvGvspPixelType.PixelType_Gvsp_Mono8 or MvGvspPixelType.PixelType_Gvsp_Mono16 or MvGvspPixelType.PixelType_Gvsp_BGR8_Packed))
                        {
                            var destination=image.PixelType.ToString().Contains("Mono")?MvGvspPixelType.PixelType_Gvsp_Mono16:MvGvspPixelType.PixelType_Gvsp_BGR8_Packed;
                            Check(device.PixelTypeConverter.ConvertPixelType(image,out converted,destination),"转换像素");image=converted;
                        }
                        var type=image.PixelType==MvGvspPixelType.PixelType_Gvsp_Mono16?MatType.CV_16UC1:image.PixelType==MvGvspPixelType.PixelType_Gvsp_Mono8?MatType.CV_8UC1:MatType.CV_8UC3;
                        using var view=Mat.FromPixelData((int)image.Height,(int)image.Width,type,image.PixelDataPtr);return new(){Image=view.Clone()};
                    }
                    finally{converted?.Dispose();}
                }
                finally{device.StreamGrabber.FreeImageBuffer(frame);}
            }
            finally{if(grabbing)device.StreamGrabber.StopGrabbing();if(device!=null){device.Close();device.Dispose();}SDKSystem.Finalize();}
        }
    }
#else
    private static IReadOnlyList<HikCameraInfo> EnumerateCore()=>throw new InvalidOperationException("此构建未找到海康 MVS SDK，请安装 SDK 后重新编译或使用 SDK 适配器");
    private static DeviceFrame AcquireCore(DeviceProfile p,CancellationToken token)=>throw new InvalidOperationException("此构建未包含海康 MVS SDK 接口");
#endif
}
