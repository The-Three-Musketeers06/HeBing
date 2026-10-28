using AiVisionStudio.Services;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Original multi-channel capture contract, backed by the configured ES device drivers.</summary>
public sealed class NativeCaptureOp:IOperator
{
    public string Driver=>"Capture";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var channels=new List<(int Channel,int Slot,int Timeout,DeviceProfile Device)>();var slots=new HashSet<int>();
        for(int channel=0;channel<8;channel++)
        {
            var group=p.Group("Channel "+channel);if(group==null||group.GetValueOrDefault("Enable") is not true)continue;
            var name=Convert.ToString(group.GetValueOrDefault("Sensor"));var device=ctx.Devices.SingleOrDefault(d=>d.Id==name||d.Name==name)??throw new ArgumentException("通道 "+channel+" 未配置传感器："+name);
            if(!device.Enabled)throw new InvalidOperationException("采集设备已禁用："+device.Name);
            int slot=Convert.ToInt32(group.GetValueOrDefault("IM Out")??channel),timeout=Convert.ToInt32(group.GetValueOrDefault("Timeout")??1000);
            if(slot<0||slot>31||!slots.Add(slot))throw new ArgumentException("采集通道输出槽位无效或重复");if(timeout<0)throw new ArgumentException("采集超时不能为负");
            channels.Add((channel,slot,timeout,device.Copy()));
        }
        if(channels.Count==0)throw new ArgumentException("至少启用一个采集通道");
        foreach(var slot in slots){ctx.Images.CheckWrite?.Invoke(slot);ctx.Clouds.CheckWrite?.Invoke(slot);}
        using var batch=CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
        var acquired=new System.Collections.Concurrent.ConcurrentBag<(int Channel,int Slot,DeviceFrame Frame)>();
        try
        {
            var tasks=channels.Select(async channel=>{
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(batch.Token);if(channel.Timeout>0)timeout.CancelAfter(channel.Timeout);
                try
                {
                    var frame=await DeviceService.AcquireAsync(channel.Device,ctx.ProjectDir,timeout.Token).ConfigureAwait(false);
                    acquired.Add((channel.Channel,channel.Slot,frame));timeout.Token.ThrowIfCancellationRequested();
                    if(frame.Image==null&&frame.Cloud==null||frame.Image?.Empty()==true||frame.Cloud?.Count==0)throw new InvalidOperationException("采集设备未返回有效帧："+channel.Device.Name);
                }
                catch{batch.Cancel();throw;}
            }).ToArray();
            Task.WhenAll(tasks).GetAwaiter().GetResult();ctx.CancellationToken.ThrowIfCancellationRequested();
            foreach(var entry in acquired.OrderBy(f=>f.Channel))
            {
                if(entry.Frame.Image is {} image){ctx.SetImage(entry.Slot,image);entry.Frame.Image=null;}
                else CloudSetOp.Publish(ctx,entry.Slot,entry.Frame.Cloud);
                ctx.Vars.Set("Channel "+entry.Channel,new Dictionary<string,object>{{"IM Out",entry.Slot},{"Device",channels.First(c=>c.Channel==entry.Channel).Device.Name}});
            }
            return true;
        }
        finally{foreach(var entry in acquired)entry.Frame.Dispose();}
    }
}
