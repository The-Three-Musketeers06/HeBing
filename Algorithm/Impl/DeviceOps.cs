using System.Text;
using AiVisionStudio.Services;

namespace AiVisionStudio.Algorithm.Impl;

public sealed class DeviceAcquireOp : IOperator
{
    public string Driver=>"ES Device Acquire";
    internal static DeviceProfile Device(OpContext ctx,ParamBag p)
        =>ctx.Devices.SingleOrDefault(d=>d.Id==p.Str("Device")||d.Name==p.Str("Device"))??throw new InvalidOperationException("设备未配置："+p.Str("Device"));
    public bool Run(OpContext ctx,ParamBag p)
    {
        var device=Device(ctx,p);
        using var frame=DeviceService.AcquireAsync(device,ctx.ProjectDir,ctx.CancellationToken).GetAwaiter().GetResult();
        var slot=p.Int("IM Out",device.ImageSlot);
        if(slot<0||slot>31)throw new ArgumentException("输出图像槽位无效");
        if(frame.Image!=null)
        {ctx.Vars.Set("Width",frame.Image.Width);ctx.Vars.Set("Height",frame.Image.Height);ctx.SetImage(slot,frame.Image);frame.Image=null;}
        else {ctx.Images.Remove(slot);ctx.Profiles.Remove(slot);ctx.Clouds.Set(slot,frame.Cloud);ctx.Vars.Set("PointCount",frame.Cloud.Count);}
        ctx.Vars.Set("Device",device.Name);ctx.Log($"设备采集：{device.Name} -> IM{slot}",false);return true;
    }
}
public sealed class DeviceExchangeOp : IOperator
{
    public string Driver=>"ES Device Exchange";
    public bool Run(OpContext ctx,ParamBag p)
    {
        var device=DeviceAcquireOp.Device(ctx,p);var token=ctx.CancellationToken;
        switch(p.Str("Action","Send / Receive"))
        {
            case "Start / Connect":ctx.Vars.Set("Response",DeviceService.TestAsync(device,ctx.ProjectDir,token,ctx.DeviceRuntime).GetAwaiter().GetResult());break;
            case "Stop / Disconnect":ctx.DeviceRuntime.Stop(device.Id);ctx.Vars.Set("Response","设备已断开");break;
            case "Send / Receive":
                var data=DeviceService.ExchangeAsync(device,Encoding.UTF8.GetBytes(p.Str("Request").Replace("\\r","\r").Replace("\\n","\n")),token,ctx.DeviceRuntime).GetAwaiter().GetResult();
                ctx.Vars.Set("Response",Encoding.UTF8.GetString(data));ctx.Vars.Set("Bytes",data.Length);break;
            case "Read Holding Registers":ctx.Vars.Set("Registers",DeviceService.ReadRegistersAsync(device,token,ctx.DeviceRuntime,ctx.ProjectDir).GetAwaiter().GetResult().Select(v=>(object)(double)v).ToList());break;
            case "Write Single Register":
                var value=p.Int("Value");if(value<0||value>65535)throw new ArgumentException("寄存器写入值须为 0..65535");
                DeviceService.WriteRegisterAsync(device,(ushort)value,token,ctx.DeviceRuntime,ctx.ProjectDir).GetAwaiter().GetResult();ctx.Vars.Set("Value",value);break;
            default:throw new ArgumentException("设备通信动作无效");
        }
        ctx.Log("设备通信完成："+device.Name,false);return true;
    }
}
