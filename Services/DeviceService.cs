using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiVisionStudio.Algorithm;
using OpenCvSharp;

namespace AiVisionStudio.Services;

public sealed class DeviceProfile : INotifyPropertyChanged
{
    public static readonly string[] Categories=["相机","I/O","Modbus","PLC","通信"];
    public static readonly string[] Protocols=["海康 MVS","USB / DirectShow","图像文件","点云 PCD","TCP 客户端","TCP 服务器","UDP","Modbus TCP","Modbus TCP 服务器","Modbus RTU 主站","Modbus RTU 从站","虚拟 I/O","串口","SDK 适配器",..PlcSdkBridge.ProtocolTypes.Keys];
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
    private string _name="设备1",_category="相机",_protocol="图像文件";
    public string Name {get=>_name;set{_name=value;Changed();}}
    public string Category {get=>_category;set{_category=value;Changed();}}
    public string Protocol {get=>_protocol;set{_protocol=value;Changed();}}
    public bool Enabled {get;set;}=true;
    public string Host {get;set;}="127.0.0.1";
    public int Port {get;set;}=5000;
    public int CameraIndex {get;set;}
    public string SerialNumber {get;set;}="";
    public string TriggerMode {get;set;}="连续";
    public string TriggerSource {get;set;}="Line0";
    public double ExposureUs {get;set;}
    public string Address {get;set;}="D0";
    public string File {get;set;}="";
    public int ImageSlot {get;set;}
    public int TimeoutMs {get;set;}=3000;
    public int UnitId {get;set;}=1;
    public int Register {get;set;}
    public int RegisterCount {get;set;}=1;
    public string ComPort {get;set;}="COM1";
    public int BaudRate {get;set;}=115200;
    public int DataBits {get;set;}=8;
    public string Parity {get;set;}="n";
    public int StopBits {get;set;}=1;
    public string Terminator {get;set;}="\\n";
    public string AssemblyPath {get;set;}="";
    public string TypeName {get;set;}="";
    public string OptionsJson {get;set;}="{}";
    public string NativeDriver {get;set;}="";
    public string NativeParametersJson {get;set;}="{}";
    public event PropertyChangedEventHandler PropertyChanged;
    private void Changed([CallerMemberName]string property=null)=>PropertyChanged?.Invoke(this,new(property));
    public DeviceProfile Copy()=>JsonSerializer.Deserialize<DeviceProfile>(JsonSerializer.Serialize(this))!;
    public void Validate()
    {
        if(string.IsNullOrWhiteSpace(Id))throw new ArgumentException("设备标识不能为空");
        if(string.IsNullOrWhiteSpace(Name)||Name.Length>80)throw new ArgumentException("设备名须为 1–80 个字符");
        if(!Categories.Contains(Category)||!Protocols.Contains(Protocol))throw new ArgumentException("设备分类或协议无效");
        if(ImageSlot<0||ImageSlot>31)throw new ArgumentException("图像槽位须为 IM0..IM31");
        if(TimeoutMs<100||TimeoutMs>60000)throw new ArgumentException("超时须为 100..60000 毫秒");
        if(Protocol is "TCP 客户端" or "TCP 服务器" or "UDP" or "Modbus TCP" or "Modbus TCP 服务器" && (string.IsNullOrWhiteSpace(Host)||Port<1||Port>65535))throw new ArgumentException("请输入有效地址和端口");
        if(Protocol is "图像文件" or "点云 PCD" && string.IsNullOrWhiteSpace(File))throw new ArgumentException("请选择采集文件");
        if(Protocol=="USB / DirectShow"&&CameraIndex<0)throw new ArgumentException("相机索引不能为负");
        if((Protocol.StartsWith("Modbus")||Protocol=="虚拟 I/O"||PlcSdkBridge.ProtocolTypes.ContainsKey(Protocol))&&(UnitId<0||UnitId>255||Register<0||Register>65535||RegisterCount<1||RegisterCount>125||Register+RegisterCount>65536))throw new ArgumentException("单元号、寄存器地址或数量无效");
        if(Protocol is "串口" or "Modbus RTU 主站" or "Modbus RTU 从站" &&(!System.Text.RegularExpressions.Regex.IsMatch(ComPort??"",@"^COM[1-9][0-9]*$",System.Text.RegularExpressions.RegexOptions.IgnoreCase)||BaudRate<=0||DataBits<5||DataBits>8||!new[]{"n","e","o","m","s"}.Contains(Parity)||StopBits is not (1 or 2)))throw new ArgumentException("串口参数无效");
        if(!double.IsFinite(ExposureUs)||ExposureUs<0)throw new ArgumentException("曝光时间须为非负有限值，0 表示保持相机当前值");
        if(Protocol=="SDK 适配器" && (string.IsNullOrEmpty(NativeDriver)||!string.IsNullOrEmpty(AssemblyPath)))
        {if(string.IsNullOrWhiteSpace(AssemblyPath)||string.IsNullOrWhiteSpace(TypeName))throw new ArgumentException("请选择 SDK 适配器程序集并填写驱动类名");using var doc=JsonDocument.Parse(OptionsJson);if(doc.RootElement.ValueKind!=JsonValueKind.Object)throw new ArgumentException("SDK 参数须为 JSON 对象");}
    }
}

public sealed class DeviceFrame : IDisposable
{
    public Mat Image {get;set;}
    public PointCloud Cloud {get;set;}
    public void Dispose()=>Image?.Dispose();
}

/// <summary>Vendor adapters reference this interface and call their official SDK. No vendor kernel is assumed.</summary>
public interface IVisionDeviceAdapter : IDisposable
{
    Task<string> TestAsync(DeviceProfile profile,CancellationToken cancellationToken);
    Task<DeviceFrame> AcquireAsync(DeviceProfile profile,CancellationToken cancellationToken);
}

public static class DeviceService
{
    public static bool UsesRegisters(string protocol)=>protocol.StartsWith("Modbus")||protocol=="虚拟 I/O"||PlcSdkBridge.ProtocolTypes.ContainsKey(protocol);
    private static int _transaction;
    public static string ResolveFile(string directory,string path)=>Path.GetFullPath(Path.IsPathRooted(path)?path:Path.Combine(directory,path));
    private static IVisionDeviceAdapter Adapter(DeviceProfile p,string directory)
    {
        if(string.IsNullOrWhiteSpace(p.AssemblyPath))throw new NotSupportedException("设备 "+p.NativeDriver+" 尚未配置自有 SDK 适配器，原始参数已保留");
        var path=ResolveFile(directory,p.AssemblyPath);
        if(!System.IO.File.Exists(path))throw new FileNotFoundException("SDK 适配器未安装："+path);
        var type=Assembly.LoadFrom(path).GetType(p.TypeName,true)!;
        return Activator.CreateInstance(type) as IVisionDeviceAdapter??throw new InvalidOperationException("驱动须实现 IVisionDeviceAdapter");
    }
    public static async Task<DeviceFrame> AcquireAsync(DeviceProfile p,string directory,CancellationToken token=default)
    {
        p.Validate();if(!p.Enabled)throw new InvalidOperationException("设备已禁用");
        token.ThrowIfCancellationRequested();
        if(p.Protocol=="海康 MVS")return await HikMvsService.AcquireAsync(p,token);
        if(p.Protocol=="SDK 适配器")
        {
            using var adapter=Adapter(p,directory);var frame=await adapter.AcquireAsync(p,token);
            if(frame==null||frame.Image==null&&frame.Cloud==null||frame.Image?.Empty()==true||frame.Cloud?.Count==0||frame.Image!=null&&frame.Cloud!=null){frame?.Dispose();throw new InvalidDataException("SDK 必须返回一份有效图像或点云");}return frame;
        }
        return await Task.Run(()=>{
            token.ThrowIfCancellationRequested();
            if(p.Protocol=="点云 PCD")return new DeviceFrame{Cloud=PcdIO.Load(ResolveFile(directory,p.File))};
            Mat image;
            if(p.Protocol=="图像文件")image=Cv2.ImDecode(System.IO.File.ReadAllBytes(ResolveFile(directory,p.File)),ImreadModes.Unchanged);
            else if(p.Protocol=="USB / DirectShow")
            {
                using var camera=new VideoCapture(p.CameraIndex,VideoCaptureAPIs.DSHOW);
                if(!camera.IsOpened())throw new IOException("相机打开失败，请检查索引、占用情况和驱动");
                image=new Mat();try{if(!camera.Read(image))throw new IOException("相机未返回帧");}catch{image.Dispose();throw;}
            }
            else throw new InvalidOperationException("该协议不提供图像采集，请使用收发测试或寄存器操作");
            if(image.Empty()){image.Dispose();throw new InvalidDataException("采集图像为空");}
            if(token.IsCancellationRequested){image.Dispose();token.ThrowIfCancellationRequested();}
            return new DeviceFrame{Image=image};
        },token);
    }
    public static async Task<string> TestAsync(DeviceProfile p,string directory,CancellationToken token=default,DeviceRuntime runtime=null)
    {
        p.Validate();if(!p.Enabled)throw new InvalidOperationException("设备已禁用");
        if(p.Protocol is "TCP 服务器" or "Modbus TCP 服务器" or "Modbus RTU 从站" or "虚拟 I/O")return (runtime??throw new InvalidOperationException("没有工程设备会话")).Start(p);
        if(p.Protocol is "图像文件" or "点云 PCD" or "USB / DirectShow" or "海康 MVS")
        {using var frame=await AcquireAsync(p,directory,token);return frame.Cloud is {} cloud?$"点云读取成功：{cloud.Count:N0} 点":$"图像采集成功：{frame.Image.Width} × {frame.Image.Height}";}
        if(p.Protocol=="SDK 适配器"){using var adapter=Adapter(p,directory);return await adapter.TestAsync(p,token);}
        if(p.Protocol is "Modbus TCP" or "Modbus RTU 主站" || PlcSdkBridge.ProtocolTypes.ContainsKey(p.Protocol))return "寄存器读取成功："+string.Join(", ",await ReadRegistersAsync(p,token,runtime,directory));
        if(p.Protocol=="串口"){return await Task.Run(()=>{using var port=new SerialDevice(p);return "串口已打开："+p.ComPort;},token);}
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(p.TimeoutMs);
        if(p.Protocol=="TCP 客户端"){using var tcp=new TcpClient();await tcp.ConnectAsync(p.Host,p.Port,timeout.Token);return "TCP 连接成功（测试后已断开）";}
        throw new InvalidOperationException("UDP 无连接握手，请使用收发测试确认远端响应");
    }
    public static async Task<byte[]> ExchangeAsync(DeviceProfile p,byte[] request,CancellationToken token=default,DeviceRuntime runtime=null)
    {
        p.Validate();if(!p.Enabled)throw new InvalidOperationException("设备已禁用");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(p.TimeoutMs);
        if(p.Protocol=="TCP 服务器")return await (runtime??throw new InvalidOperationException("没有工程设备会话")).ExchangeAsync(p,request,token);
        if(p.Protocol=="串口")return await Task.Run(()=>{using var port=new SerialDevice(p);var result=port.Exchange(request,timeout.Token);return result.Length>0?result:throw new TimeoutException("串口没有返回数据");},timeout.Token);
        if(p.Protocol=="UDP")
        {using var udp=new UdpClient();udp.Connect(p.Host,p.Port);await udp.SendAsync(request,timeout.Token);return (await udp.ReceiveAsync(timeout.Token)).Buffer;}
        if(p.Protocol!="TCP 客户端")throw new InvalidOperationException("收发测试适用于 TCP、UDP 或串口");
        using var tcp=new TcpClient();await tcp.ConnectAsync(p.Host,p.Port,timeout.Token);var stream=tcp.GetStream();
        await stream.WriteAsync(request,timeout.Token);var buffer=new byte[4096];using var result=new MemoryStream();
        var terminator=Encoding.UTF8.GetBytes(p.Terminator.Replace("\\r","\r").Replace("\\n","\n"));
        while(result.Length<1048576)
        {
            var count=await stream.ReadAsync(buffer,timeout.Token);if(count==0)break;result.Write(buffer,0,count);
            if(terminator.Length==0||result.GetBuffer().AsSpan(0,(int)result.Length).IndexOf(terminator)>=0)break;
        }
        if(result.Length>=1048576)throw new IOException("接收数据超过 1MB，请配置结束符");
        if(result.Length==0)throw new IOException("远端已断开且没有返回数据");return result.ToArray();
    }
    public static async Task<ushort[]> ReadRegistersAsync(DeviceProfile p,CancellationToken token=default,DeviceRuntime runtime=null,string directory=null)
    {
        p.Validate();
        if(!p.Enabled)throw new InvalidOperationException("设备已禁用");
        if(PlcSdkBridge.ProtocolTypes.ContainsKey(p.Protocol))return await PlcSdkBridge.ReadAsync(p,directory??AppContext.BaseDirectory,token);
        if(p.Protocol is "虚拟 I/O" or "Modbus TCP 服务器" or "Modbus RTU 从站")return (runtime??throw new InvalidOperationException("没有工程设备会话")).Read(p);
        var body=new byte[5];body[0]=3;BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(1),(ushort)p.Register);BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(3),(ushort)p.RegisterCount);
        var response=p.Protocol=="Modbus RTU 主站"?await RtuAsync(p,body,token):await ModbusAsync(p,body,token);
        if(response.Length!=2+p.RegisterCount*2||response[1]!=p.RegisterCount*2)throw new IOException("Modbus 返回寄存器数量不匹配");
        return Enumerable.Range(0,p.RegisterCount).Select(i=>BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2+i*2))).ToArray();
    }
    public static async Task WriteRegisterAsync(DeviceProfile p,ushort value,CancellationToken token=default,DeviceRuntime runtime=null,string directory=null)
    {
        p.Validate();
        if(!p.Enabled)throw new InvalidOperationException("设备已禁用");
        if(PlcSdkBridge.ProtocolTypes.ContainsKey(p.Protocol)){await PlcSdkBridge.WriteAsync(p,directory??AppContext.BaseDirectory,value,token);return;}
        if(p.Protocol is "虚拟 I/O" or "Modbus TCP 服务器" or "Modbus RTU 从站"){(runtime??throw new InvalidOperationException("没有工程设备会话")).Write(p,value);return;}
        var body=new byte[5];body[0]=6;BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(1),(ushort)p.Register);BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(3),value);
        var response=p.Protocol=="Modbus RTU 主站"?await RtuAsync(p,body,token):await ModbusAsync(p,body,token);
        if(!response.SequenceEqual(body))throw new IOException("Modbus 写入回执不匹配");
    }
    public static async Task<byte[]> ExchangeModbusPduAsync(DeviceProfile p,byte[] body,CancellationToken token)
    {
        p.Validate();if(!p.Enabled)throw new InvalidOperationException("设备已禁用");
        if(p.Protocol is not ("Modbus TCP" or "Modbus RTU 主站"))throw new NotSupportedException("该算子当前支持 Modbus TCP 客户端和 RTU 主站；服务器/从站独立四区内存尚未接入");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(p.TimeoutMs);
        return p.Protocol=="Modbus RTU 主站"?await RtuAsync(p,body,timeout.Token):await ModbusAsync(p,body,timeout.Token);
    }
    private static Task<byte[]> RtuAsync(DeviceProfile p,byte[] body,CancellationToken token)=>Task.Run(()=>{
        if(!p.Enabled)throw new InvalidOperationException("设备已禁用");var packet=new byte[body.Length+3];packet[0]=(byte)p.UnitId;body.CopyTo(packet,1);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(packet.Length-2),DeviceRuntime.Crc(packet.AsSpan(0,packet.Length-2)));
        using var port=new SerialDevice(p);var response=port.Exchange(packet,token);
        if(response.Length<5||response[0]!=p.UnitId||DeviceRuntime.Crc(response.AsSpan(0,response.Length-2))!=BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(response.Length-2)))throw new IOException("Modbus RTU 回执 CRC 或站号错误");
        if(response[1]!=(byte)body[0])throw new IOException("Modbus RTU 功能码或异常码："+response[1]);return response[1..^2];
    },token);
    private static async Task<byte[]> ModbusAsync(DeviceProfile p,byte[] body,CancellationToken token)
    {
        p.Validate();if(!p.Enabled||p.Protocol!="Modbus TCP")throw new InvalidOperationException("请选择启用的 Modbus TCP 设备");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(p.TimeoutMs);
        using var tcp=new TcpClient();await tcp.ConnectAsync(p.Host,p.Port,timeout.Token);var stream=tcp.GetStream();
        var transaction=(ushort)Interlocked.Increment(ref _transaction);var packet=new byte[7+body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(packet,transaction);BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4),(ushort)(body.Length+1));packet[6]=(byte)p.UnitId;body.CopyTo(packet,7);
        await stream.WriteAsync(packet,timeout.Token);var header=new byte[7];await stream.ReadExactlyAsync(header,timeout.Token);
        var length=BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        if(BinaryPrimitives.ReadUInt16BigEndian(header)!=transaction||header[2]!=0||header[3]!=0||header[6]!=p.UnitId||length<2||length>254)throw new IOException("Modbus 报文头不匹配");
        var response=new byte[length-1];await stream.ReadExactlyAsync(response,timeout.Token);
        if(response[0]==(body[0]|0x80))throw new IOException("Modbus 设备异常码："+(response.Length>1?response[1]:-1));
        if(response[0]!=body[0])throw new IOException("Modbus 功能码不匹配");return response;
    }
}
