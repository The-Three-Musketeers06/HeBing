using System.IO;
using System.Text.Json;
using AiVisionStudio.Algorithm.Impl;
using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Services;

public partial class MainViewModel
{
    public DeviceRuntime DeviceRuntime=>_engine.Context.DeviceRuntime;
    public IReadOnlyList<DeviceProfile> ReadDevices()
    {
        return NativeDeviceConfiguration.Read(SelectedProgram?.Root??Programs.FirstOrDefault()?.Root);
    }
    public void ApplyDevices(IEnumerable<DeviceProfile> devices)
    {
        RequireManagementEdit();var rows=devices.Select(d=>d.Copy()).ToList();var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var ids=new HashSet<string>();
        foreach(var row in rows){row.Validate();if(!names.Add(row.Name.Trim())||!ids.Add(row.Id))throw new ArgumentException("设备名或标识重复："+row.Name);row.Name=row.Name.Trim();}
        var old=ReadDevices();
        foreach(var removed in old.Where(d=>!rows.Any(r=>r.Id==d.Id)))
            foreach(var operation in Programs.SelectMany(p=>p.Operations).Where(o=>o.Driver is "ES Device Acquire" or "ES Device Exchange" or "SendString" or "ReciveString" or "Read I/O" or "Write I/O" or "Wait I/O" || o.Driver.StartsWith("Modbus")))
            {
                var parameters=Algorithm.ParamBag.FromYaml(operation.ParametersRaw as YamlNode);var selected=parameters.Str("Device",parameters.Str("Target"));
                if(selected==removed.Id||selected==removed.Name)throw new InvalidOperationException("设备仍被算子引用，请先移除调用："+operation.Name);
            }
        if(!ApplyParameters())throw new InvalidOperationException("请先修正无效参数");
        var json=JsonSerializer.Serialize(rows);if(JsonSerializer.Serialize(old)==json){Status="设备配置未变化";return;}
        BeforeEdit();DeviceRuntime.Dispose();Put(ManagementPrograms(),"es_devices",json);AfterEdit();Status="设备配置已应用，原有连接已断开；保存工程后持久化";
    }
    public async Task AcquireDeviceAsync(DeviceProfile device,CancellationToken token=default)
    {
        RequireManagementEdit();IsRunning=true;
        try
        {
            var directory=string.IsNullOrEmpty(ProjectPath)?AppContext.BaseDirectory:Path.GetDirectoryName(Path.GetFullPath(ProjectPath))!;
            using var frame=await DeviceService.AcquireAsync(device,directory,token);
            if(frame.Image!=null){_engine.Context.SetImage(device.ImageSlot,frame.Image);frame.Image=null;}
            else {_engine.Context.Images.Remove(device.ImageSlot);_engine.Context.Profiles.Remove(device.ImageSlot);_engine.Context.Clouds.Set(device.ImageSlot,frame.Cloud);}
            _engine.InvalidateExecution();InvalidateNativeExecution();_engine.Context.OpOutputs.Clear();RefreshGlobalVars();ImageChanged?.Invoke(this,EventArgs.Empty);Status=$"{device.Name} 已采集到 IM{device.ImageSlot}";
        }
        finally{IsRunning=false;}
    }
    public void AddDeviceOperation(DeviceProfile device)
    {
        RequireManagementEdit();if(!ReadDevices().Any(d=>d.Id==device.Id))throw new InvalidOperationException("请先应用设备配置");
        if(!ApplyParameters())throw new InvalidOperationException("请先修正无效参数");
        var capture=device.Protocol is "图像文件" or "点云 PCD" or "USB / DirectShow" or "海康 MVS" or "SDK 适配器";
        var action=device.Protocol is "TCP 服务器" or "Modbus TCP 服务器" or "Modbus RTU 从站"?"Start / Connect":DeviceService.UsesRegisters(device.Protocol)?"Read Holding Registers":"Send / Receive";
        var values=new YamlMappingNode{{"Device",device.Id},{"IM Out",device.ImageSlot.ToString()},{"Action",action},{"Request",""},{"Value","0"}};
        var op=new Models.Operation{Driver=capture?"ES Device Acquire":"ES Device Exchange",Name=device.Name+(capture?"采集":"通信"),ParametersRaw=new YamlMappingNode{{"value",values}}};
        BeforeEdit();SelectedProgram.Operations.Add(op);Operations.Add(op);Renumber();AfterEdit();SelectedOperation=op;Status="已添加设备算子："+op.Name;
    }
}
