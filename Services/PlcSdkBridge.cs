using System.IO;
using System.Reflection;
using System.Text.Json;

namespace AiVisionStudio.Services;

/// <summary>Optional installed HSL driver bridge. No SDK binaries or activation keys are bundled.</summary>
public static class PlcSdkBridge
{
    public static readonly IReadOnlyDictionary<string,string> ProtocolTypes=new Dictionary<string,string>{
        ["汇川 Easy TCP"]="HslCommunication.Profinet.Inovance.InovanceEasyNet",
        ["汇川 TCP"]="HslCommunication.Profinet.Inovance.InovanceTcpNet",
        ["基恩士 KV 上位链路"]="HslCommunication.Profinet.Keyence.KeyenceNanoSerialOverTcp",
        ["三菱 MC A1E"]="HslCommunication.Profinet.Melsec.MelsecA1ENet",
        ["三菱 MC ASCII"]="HslCommunication.Profinet.Melsec.MelsecMcAsciiNet",
        ["欧姆龙 CIP TCP"]="HslCommunication.Profinet.Omron.OmronCipNet",
        ["欧姆龙 FINS TCP"]="HslCommunication.Profinet.Omron.OmronFinsNet",
        ["欧姆龙 FINS UDP"]="HslCommunication.Profinet.Omron.OmronFinsUdp",
        ["西门子 S7"]="HslCommunication.Profinet.Siemens.SiemensS7Net"
    };
    private static object Create(DeviceProfile p,string directory)
    {
        p.Validate();
        var path=DeviceService.ResolveFile(directory,string.IsNullOrWhiteSpace(p.AssemblyPath)?"Drivers/HslCommunication.dll":p.AssemblyPath);
        if(!File.Exists(path))throw new FileNotFoundException("该 PLC 协议需要安装对应通信驱动。请选择有效的 HslCommunication.dll（使用现有授权）或 SDK 适配器。",path);
        var assembly=Assembly.LoadFrom(path);var type=assembly.GetType(string.IsNullOrWhiteSpace(p.TypeName)?ProtocolTypes[p.Protocol]:p.TypeName,true)!;
        var authorization=Environment.GetEnvironmentVariable("ESVISION_HSL_AUTHORIZATION");
        if(!string.IsNullOrEmpty(authorization)&&assembly.GetType("HslCommunication.Authorization")?.GetMethod("SetAuthorizationCode",new[]{typeof(string)}) is {} authorize)
            if(authorize.Invoke(null,new object[]{authorization}) is false)throw new InvalidOperationException("PLC 通信 SDK 授权未通过");
        object device;
        using var options=JsonDocument.Parse(p.OptionsJson);
        if(p.Protocol=="西门子 S7")
        {
            var model=assembly.GetType("HslCommunication.Profinet.Siemens.SiemensPLCS",true)!;
            var selected=options.RootElement.TryGetProperty("PlcModel",out var value)?value.GetString():"S1200";
            device=Activator.CreateInstance(type,Enum.Parse(model,selected!),p.Host)!;
        }
        else device=Activator.CreateInstance(type)!;
        void Set(string key,object value){var property=type.GetProperty(key);if(property?.CanWrite==true)property.SetValue(device,Convert.ChangeType(value,property.PropertyType));}
        Set("IpAddress",p.Host);Set("Port",p.Port);Set("ConnectTimeOut",p.TimeoutMs);Set("ReceiveTimeOut",p.TimeoutMs);Set("Station",p.UnitId);
        foreach(var option in options.RootElement.EnumerateObject())
        {
            if(option.Name=="PlcModel")continue;
            var property=type.GetProperty(option.Name);if(property?.CanWrite!=true)throw new ArgumentException("驱动没有该参数："+option.Name);
            property.SetValue(device,property.PropertyType.IsEnum?Enum.Parse(property.PropertyType,option.Value.ToString()):JsonSerializer.Deserialize(option.Value.GetRawText(),property.PropertyType));
        }
        return device;
    }
    private static object Result(object result)
    {
        var type=result.GetType();if(type.GetProperty("IsSuccess")?.GetValue(result) is not true)throw new IOException("PLC 驱动："+type.GetProperty("Message")?.GetValue(result));
        return type.GetProperty("Content")?.GetValue(result);
    }
    private static object Invoke(object device,string name,Type[] types,params object[] args)
        =>device.GetType().GetMethod(name,types)?.Invoke(device,args)??throw new MissingMethodException("驱动没有接口："+name);
    public static Task<ushort[]> ReadAsync(DeviceProfile p,string directory,CancellationToken token=default)=>Task.Run(()=>{
        token.ThrowIfCancellationRequested();var device=Create(p,directory);
        try{return (ushort[])Result(Invoke(device,"ReadUInt16",new[]{typeof(string),typeof(ushort)},p.Address,(ushort)p.RegisterCount));}
        catch(TargetInvocationException ex){throw new IOException(ex.InnerException?.Message??ex.Message,ex.InnerException);}
        finally{Close(device);}
    },token);
    public static Task WriteAsync(DeviceProfile p,string directory,ushort value,CancellationToken token=default)=>Task.Run(()=>{
        token.ThrowIfCancellationRequested();var device=Create(p,directory);
        try{Result(Invoke(device,"Write",new[]{typeof(string),typeof(ushort)},p.Address,value));}
        catch(TargetInvocationException ex){throw new IOException(ex.InnerException?.Message??ex.Message,ex.InnerException);}
        finally{Close(device);}
    },token);
    private static void Close(object device){try{device.GetType().GetMethod("ConnectClose",Type.EmptyTypes)?.Invoke(device,null);}finally{(device as IDisposable)?.Dispose();}}
}
