using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AiVisionStudio.Services;

public sealed partial class DeviceRuntime
{
    private readonly ConcurrentDictionary<string,RawSession> _nativeSessions=new();
    private readonly ConcurrentDictionary<string,VirtualIo> _nativeIo=new();
    private readonly object _nativeGate=new();
    private sealed class VirtualIo {public readonly bool[] Inputs=new bool[16],Outputs=new bool[16];}
    private VirtualIo Io(DeviceProfile p)
    {
        if(!p.Enabled||p.Protocol!="虚拟 I/O")throw new InvalidOperationException("请选择启用的虚拟 I/O 设备；物理板卡需要对应 SDK");
        return _nativeIo.GetOrAdd(p.Id,_=>{var io=new VirtualIo();var raw=NativeDeviceConfiguration.Parameters(p);foreach(var group in new[]{"DI","DO"})if(raw.TryGetProperty(group,out var values))for(int i=0;i<16;i++)if(values.TryGetProperty(group+" "+i,out var bit)&&bit.ValueKind is JsonValueKind.True or JsonValueKind.False)(group=="DI"?io.Inputs:io.Outputs)[i]=bit.GetBoolean();return io;});
    }
    public bool[] ReadVirtualInputs(DeviceProfile p){var io=Io(p);lock(io)return io.Inputs.ToArray();}
    public bool[] ReadVirtualOutputs(DeviceProfile p){var io=Io(p);lock(io)return io.Outputs.ToArray();}
    public void SetVirtualInputs(DeviceProfile p,IReadOnlyDictionary<int,bool> values)
    {if(values.Keys.Any(k=>k<0||k>=16))throw new ArgumentOutOfRangeException(nameof(values));var io=Io(p);lock(io)foreach(var value in values)io.Inputs[value.Key]=value.Value;}
    public void SetVirtualOutputs(DeviceProfile p,IReadOnlyDictionary<int,bool> values)
    {if(values.Keys.Any(k=>k<0||k>=16))throw new ArgumentOutOfRangeException(nameof(values));var io=Io(p);lock(io)foreach(var value in values)io.Outputs[value.Key]=value.Value;}
    private void StopNative(string id)
    {lock(_nativeGate){if(_nativeSessions.TryRemove(id,out var raw))raw.Dispose();_nativeIo.TryRemove(id,out _);}}
    private void DisposeNative()
    {lock(_nativeGate){foreach(var raw in _nativeSessions.Values)raw.Dispose();_nativeSessions.Clear();_nativeIo.Clear();}}
    private RawSession Raw(DeviceProfile p)
    {
        p.Validate();if(!p.Enabled)throw new InvalidOperationException("设备已禁用："+p.Name);
        if(p.Protocol is not ("TCP 客户端" or "UDP" or "串口"))throw new NotSupportedException("设备不支持原始字符串通信："+p.Protocol);
        lock(_nativeGate)return _nativeSessions.GetOrAdd(p.Id,_=>new RawSession(p.Copy()));
    }
    public async Task SendNativeAsync(DeviceProfile p,byte[] bytes,CancellationToken token)
    {
        if(p.Protocol=="TCP 服务器")
        {
            Start(p);Session session;lock(_gate)session=_sessions[p.Id];await session.SendOnlyAsync(bytes,token);return;
        }
        var sessionRaw=Raw(p);await sessionRaw.SendAsync(bytes,token);
    }
    public async Task<byte[]> ReceiveNativeAsync(DeviceProfile p,int timeoutMs,CancellationToken token)
    {
        if(timeoutMs<0)throw new ArgumentException("接收超时不能为负");using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);if(timeoutMs>0)timeout.CancelAfter(timeoutMs);
        try
        {
            if(p.Protocol=="TCP 服务器"){Start(p);Session session;lock(_gate)session=_sessions[p.Id];return await session.ReceiveOnlyAsync(timeout.Token);}
            return await Raw(p).ReceiveAsync(timeout.Token);
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested&&timeout.IsCancellationRequested){throw new TimeoutException("接收字符串超时");}
    }
    private sealed class RawSession(DeviceProfile profile):IDisposable
    {
        private readonly CancellationTokenSource _closed=new();
        private readonly SemaphoreSlim _connect=new(1),_send=new(1),_read=new(1);
        private readonly object _transportGate=new();private bool _disposed;
        private TcpClient _tcp,_pendingTcp;private UdpClient _udp;private SerialDevice _serial;private readonly List<byte> _pending=new();
        private void CheckOpen(){if(_disposed)throw new OperationCanceledException("设备连接已停止",_closed.Token);}
        private void ResetTcp(TcpClient failed)
        {
            // Only retire the failed generation; a concurrent next call may already have connected again.
            lock(_transportGate){if(ReferenceEquals(_tcp,failed))_tcp=null;}
            failed.Dispose();
        }
        private async Task EnsureAsync(CancellationToken token)
        {
            await _connect.WaitAsync(token);try
            {
                lock(_transportGate){CheckOpen();if(_tcp!=null||_udp!=null||_serial!=null)return;}
                if(profile.Protocol=="TCP 客户端")
                {
                    var tcp=new TcpClient();try{using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token,_closed.Token);timeout.CancelAfter(profile.TimeoutMs);await tcp.ConnectAsync(profile.Host,profile.Port,timeout.Token);lock(_transportGate){CheckOpen();token.ThrowIfCancellationRequested();_tcp=tcp;}}catch{tcp.Dispose();throw;}
                }
                else if(profile.Protocol=="UDP")
                {
                    UdpClient udp;if(profile.NativeDriver=="Udp"){if(!IPAddress.TryParse(profile.Host,out var host))throw new ArgumentException("UDP 本地监听地址必须是 IP");udp=new UdpClient(new IPEndPoint(host,profile.Port));}else udp=new UdpClient();
                    try{lock(_transportGate){CheckOpen();token.ThrowIfCancellationRequested();_udp=udp;}}catch{udp.Dispose();throw;}
                }
                else if(profile.Protocol=="串口")
                {
                    var parameters=NativeDeviceConfiguration.Parameters(profile);if(parameters.TryGetProperty("Flow Control",out var flow)&&flow.GetString()!="None")throw new NotSupportedException("串口流控需要扩展驱动，不能忽略该参数");
                    var copy=profile.Copy();copy.TimeoutMs=100;var serial=new SerialDevice(copy);try{lock(_transportGate){CheckOpen();token.ThrowIfCancellationRequested();_serial=serial;}}catch{serial.Dispose();throw;}
                }
            }
            finally{_connect.Release();}
        }
        private async Task<(TcpClient Tcp,UdpClient Udp,SerialDevice Serial)> TransportAsync(CancellationToken token)
        {
            while(true)
            {
                await EnsureAsync(token);
                lock(_transportGate){CheckOpen();if(_tcp!=null||_udp!=null||_serial!=null)return (_tcp,_udp,_serial);}
            }
        }
        public async Task SendAsync(byte[] bytes,CancellationToken token)
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token,_closed.Token);timeout.CancelAfter(profile.TimeoutMs);await _send.WaitAsync(timeout.Token);TcpClient tcp=null;
            try
            {
                var transport=await TransportAsync(timeout.Token);tcp=transport.Tcp;var udp=transport.Udp;var serial=transport.Serial;
                if(tcp!=null)await tcp.GetStream().WriteAsync(bytes,timeout.Token);
                else if(udp!=null)
                {
                    var parameters=NativeDeviceConfiguration.Parameters(profile);if(parameters.TryGetProperty("Enable Send",out var enabled)&&!enabled.GetBoolean())throw new InvalidOperationException("UDP 发送已禁用");
                    var host=parameters.TryGetProperty("Remote Host Address",out var address)?address.GetString():profile.Host;var port=parameters.TryGetProperty("Remote Host Port",out var target)?target.GetInt32():profile.Port;
                    await udp.SendAsync(bytes,new IPEndPoint(IPAddress.Parse(host),port),timeout.Token);
                }
                else await Task.Run(()=>serial.Send(bytes,timeout.Token),timeout.Token);
            }
            catch(Exception ex)when(tcp!=null&&ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException){ResetTcp(tcp);throw;}
            finally{_send.Release();}
        }
        public async Task<byte[]> ReceiveAsync(CancellationToken token)
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,_closed.Token);token=linked.Token;await _read.WaitAsync(token);TcpClient tcp=null;
            try
            {
                var transport=await TransportAsync(token);tcp=transport.Tcp;var udp=transport.Udp;var serial=transport.Serial;
                if(!ReferenceEquals(_pendingTcp,tcp)){_pending.Clear();_pendingTcp=tcp;}
                var terminator=Encoding.UTF8.GetBytes(profile.Terminator.Replace("\\r","\r").Replace("\\n","\n"));
                while(true)
                {
                    token.ThrowIfCancellationRequested();
                    if(_pending.Count>0)
                    {
                        var bytes=_pending.ToArray();var end=terminator.Length==0?-1:bytes.AsSpan().IndexOf(terminator);
                        if(end>=0){var n=end+terminator.Length;var result=bytes[..n];_pending.RemoveRange(0,n);return result;}
                        if(terminator.Length==0)
                        {
                            try{new UTF8Encoding(false,true).GetString(bytes);_pending.Clear();return bytes;}
                            catch(DecoderFallbackException){/* retain a split UTF-8 sequence until more bytes arrive */}
                        }
                    }
                    byte[] incoming;
                    if(tcp!=null){var buffer=new byte[65536];var n=await tcp.GetStream().ReadAsync(buffer,token);if(n==0)throw new IOException("TCP 对方已断开");incoming=buffer[..n];}
                    else if(udp!=null)incoming=(await udp.ReceiveAsync(token)).Buffer;
                    else {incoming=await Task.Run(()=>serial.Receive(token),token);if(incoming.Length==0)continue;}
                    _pending.AddRange(incoming);if(_pending.Count>1048576)throw new IOException("接收缓存超过 1MB，请配置结束符或检查编码");
                }
            }
            catch(Exception ex)when(tcp!=null&&ex is IOException or SocketException or ObjectDisposedException){_pending.Clear();ResetTcp(tcp);throw;}
            finally{_read.Release();}
        }
        public void Dispose()
        {
            TcpClient tcp;UdpClient udp;SerialDevice serial;
            lock(_transportGate){if(_disposed)return;_disposed=true;tcp=_tcp;udp=_udp;serial=_serial;_tcp=null;_udp=null;_serial=null;}
            try{_closed.Cancel();}finally{tcp?.Dispose();udp?.Dispose();serial?.Dispose();}
        }
    }
}
