using System.Diagnostics;
using System.Text;
using AiVisionStudio.Services;

namespace AiVisionStudio.Algorithm.Impl;

public static class NativeDeviceRegistration
{
    public static void Register()
    {OperatorRegistry.Register(new NativeStringOp(false));OperatorRegistry.Register(new NativeStringOp(true));foreach(var mode in new[]{"Read I/O","Write I/O","Wait I/O"})OperatorRegistry.Register(new NativeIoOp(mode));foreach(var mode in new[]{"ModbusReadBit","ModbusWriteBit","ModbusReadRegister","ModbusWriteRegister"})OperatorRegistry.Register(new NativeModbusOp(mode));}
    public static DeviceProfile Device(OpContext ctx,ParamBag p,string key="Device")
    {var name=p.Str(key);if(string.IsNullOrWhiteSpace(name))throw new ArgumentException("设备参数不能为空");var device=ctx.Devices.SingleOrDefault(d=>d.Id==name||d.Name==name)??throw new ArgumentException("未配置设备："+name);if(!device.Enabled)throw new InvalidOperationException("设备已禁用："+name);return device;}
    public static bool Active(ParamBag p)=>p.Bool("Active",true)^p.Bool("Reverse Active");
}

public sealed class NativeStringOp(bool receive):IOperator
{
    public string Driver=>receive?"ReciveString":"SendString";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;if(!receive&&!NativeDeviceRegistration.Active(p))return true;var device=NativeDeviceRegistration.Device(ctx,p);
        if(device.Category!="通信")throw new ArgumentException("字符串算子要求通信组设备");
        if(receive)
        {
            var timeout=p.Int("Timeout",1000);if(timeout<0)throw new ArgumentException("接收超时不能为负");
            var data=ctx.DeviceRuntime.ReceiveNativeAsync(device,timeout,ctx.CancellationToken).GetAwaiter().GetResult();var text=new UTF8Encoding(false,true).GetString(data);
            ctx.Vars.Set("Recived",text);ctx.Vars.Set("String",text);ctx.Vars.Set("Content",text);ctx.Vars.Set("Data",text);ctx.Vars.Set("Length",data.Length);
        }
        else
        {
            var text=p.Str("Content");var symbols=new[]{"NUL","SOH","STX","ETX","EOT","ENQ","ACK","BEL","BS","HT","LF","VT","FF","CR","SO","SI","DLE","DC1","DC2","DC3","DC4","NAK","SYN","ETB","CAN","EM","SUB","ESC","FS","GS","RS","US","SP"};
            for(int i=0;i<symbols.Length;i++)text=text.Replace("<"+symbols[i]+">",((char)i).ToString(),StringComparison.Ordinal);text=text.Replace("<DEL>","\u007f",StringComparison.Ordinal);
            ctx.DeviceRuntime.SendNativeAsync(device,Encoding.UTF8.GetBytes(text),ctx.CancellationToken).GetAwaiter().GetResult();ctx.Vars.Set("Bytes",Encoding.UTF8.GetByteCount(text));
        }
        return true;
    }
}

public sealed class NativeIoOp(string driver):IOperator
{
    public string Driver=>driver;
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;if(driver=="Write I/O"&&!NativeDeviceRegistration.Active(p))return true;var device=NativeDeviceRegistration.Device(ctx,p,"Target");
        if(device.Category!="I/O"||device.Protocol!="虚拟 I/O")throw new NotSupportedException("该物理 I/O 板卡尚未接入驱动，不能返回模拟状态");
        if(driver=="Read I/O")
        {
            var line=p.Str("Line","All");var inputs=ctx.DeviceRuntime.ReadVirtualInputs(device);
            if(line=="All"){ctx.Vars.Set("Value",inputs.Select(v=>(object)v).ToList());ctx.Vars.Set("DI",inputs.Select(v=>(object)v).ToList());for(int i=0;i<16;i++)ctx.Vars.Set("DI "+i,inputs[i]);}
            else {if(!line.StartsWith("DI ")||!int.TryParse(line[3..],out var index)||index<0||index>=16)throw new ArgumentException("DI 线路必须是 0..15");ctx.Vars.Set("Value",inputs[index]);ctx.Vars.Set("DI "+index,inputs[index]);}
        }
        else if(driver=="Write I/O")
        {
            var values=p.Group("Value")??throw new ArgumentException("缺少 DO 写入值");var updates=new Dictionary<int,bool>();foreach(var pair in values)
            {if(!pair.Key.StartsWith("DO ")||!int.TryParse(pair.Key[3..],out var index)||index<0||index>=16)throw new ArgumentException("DO 线路必须是 0..15");var value=Convert.ToString(pair.Value);if(value=="NoChange")continue;if(value is not ("Low" or "High"))throw new ArgumentException("DO 值无效："+value);updates[index]=value=="High";}
            ctx.DeviceRuntime.SetVirtualOutputs(device,updates);ctx.Vars.Set("DO",ctx.DeviceRuntime.ReadVirtualOutputs(device).Select(v=>(object)v).ToList());
        }
        else
        {
            var index=p.Int("Line");if(index<0||index>=16)throw new ArgumentException("DI 线路必须是 0..15");var mode=p.Str("Wait for","Low");mode=mode switch{"Rise"=>"Rising Edge","Fall"=>"Falling Edge",_=>mode};if(mode is not ("Low" or "High" or "Rising Edge" or "Falling Edge"))throw new ArgumentException("无效等待状态："+mode);
            var timeout=p.Int("Timeout",1000);if(timeout<0)throw new ArgumentException("等待超时不能为负");var watch=Stopwatch.StartNew();var previous=ctx.DeviceRuntime.ReadVirtualInputs(device)[index];
            while(true)
            {ctx.CancellationToken.ThrowIfCancellationRequested();var current=ctx.DeviceRuntime.ReadVirtualInputs(device)[index];var matched=mode switch{"Low"=>!current,"High"=>current,"Rising Edge"=>!previous&&current,_=>previous&&!current};if(matched){ctx.Vars.Set("Value",current);break;}if(timeout>0&&watch.ElapsedMilliseconds>=timeout)throw new TimeoutException("等待 I/O 状态超时");previous=current;if(ctx.CancellationToken.WaitHandle.WaitOne(5))ctx.CancellationToken.ThrowIfCancellationRequested();}
        }
        return true;
    }
}
