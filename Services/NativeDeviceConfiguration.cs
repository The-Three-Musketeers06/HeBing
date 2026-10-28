using System.Text.Json;
using AiVisionStudio.Algorithm;
using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Services;

public static class NativeDeviceConfiguration
{
    private static YamlNode Get(YamlNode parent,string key)=>parent is YamlMappingNode map&&map.Children.TryGetValue(new YamlScalarNode(key),out var node)?node:null;
    public static IReadOnlyList<DeviceProfile> Read(YamlMappingNode root)
    {
        var saved=(Get(Get(root,"programs"),"es_devices") as YamlScalarNode)?.Value;
        if(saved!=null)return JsonSerializer.Deserialize<List<DeviceProfile>>(saved)??new();
        var devices=Get(root,"devices");if(Get(devices,"define") is not YamlMappingNode definitions)return Array.Empty<DeviceProfile>();
        var order=(Get(devices,"order") as YamlSequenceNode)?.Children.Select(n=>n.ToString()).ToList()??new();
        order.AddRange(definitions.Children.Keys.Select(n=>n.ToString()).Where(id=>!order.Contains(id)));
        var output=new List<DeviceProfile>();
        foreach(var id in order)
        {
            var entry=Get(definitions,id);if(entry==null)continue;var driver=Get(entry,"driver")?.ToString()??"";var p=ParamBag.FromYaml(Get(entry,"parameters"));
            var d=new DeviceProfile{Id=id,Name=Get(entry,"name")?.ToString()??id,NativeDriver=driver,Enabled=Get(entry,"enabled")?.ToString()!="false",Terminator=""};
            var raw=new Dictionary<string,object>();if(Get(Get(entry,"parameters"),"value") is YamlMappingNode values)foreach(var key in values.Children.Keys)raw[key.ToString()]=p.Raw(key.ToString());
            d.NativeParametersJson=JsonSerializer.Serialize(raw);
            d.Protocol=driver switch{"TcpClient"=>"TCP 客户端","TcpServer"=>"TCP 服务器","Udp"=>"UDP","SerialPort"=>"串口","IOEmulator"=>"虚拟 I/O","ModbusTcpClient"=>"Modbus TCP","ModbusTcpServer"=>"Modbus TCP 服务器","ModbusRtuMaster"=>"Modbus RTU 主站","ModbusRtuSlave"=>"Modbus RTU 从站","HikCamera" or "Hikrobot" or "Hikrobot Camera"=>"海康 MVS",_=>"SDK 适配器"};
            var plc=driver.StartsWith("Inovance")||driver.StartsWith("Melsec")||driver.StartsWith("Omron")||driver.StartsWith("Siemens")||driver=="KeyenceNanoSerialOverTcp";
            d.Category=driver is "IOEmulator" or "AdLink Dask" or "Tydi IO"?"I/O":plc?"PLC":driver.StartsWith("Modbus")?"Modbus":d.Protocol is "TCP 客户端" or "TCP 服务器" or "UDP" or "串口"?"通信":"相机";
            var client=driver is "TcpClient" or "ModbusTcpClient";d.Host=p.Str(plc?"Target Address":client?"Remote Host Address":"Host Address","127.0.0.1");d.Port=p.Int(plc?"Target Port":client?"Remote Host Port":"Host Port",driver.StartsWith("Modbus")?502:5000);
            d.TimeoutMs=Math.Clamp(p.Int("Connect Timeout",3000),100,60000);d.ComPort=p.Str("Port Name","COM1");d.BaudRate=p.Int("Baud Rate",9600);d.DataBits=p.Int("Data Bits",8);d.StopBits=p.Int("Stop Bits",1);d.Parity=p.Str("Parity","None") switch{"Even"=>"e","Odd"=>"o",_=>"n"};d.UnitId=p.Int("Slave Id",1);d.SerialNumber=p.Str("Serial");
            output.Add(d);
        }
        return output;
    }
    public static JsonElement Parameters(DeviceProfile profile)
    {using var doc=JsonDocument.Parse(profile.NativeParametersJson??"{}");return doc.RootElement.Clone();}
}
