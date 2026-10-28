using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace AiVisionStudio.Services;

/// <summary>Project-owned listeners and virtual registers. Never starts a listener on configuration load.</summary>
public sealed partial class DeviceRuntime : IDisposable
{
    private readonly Dictionary<string,Session> _sessions=new();
    private readonly ConcurrentDictionary<string,ushort[]> _registers=new();
    private readonly object _gate=new();
    public ushort[] Read(DeviceProfile p){var data=_registers.GetOrAdd(p.Id,_=>new ushort[65536]);lock(data)return data.Skip(p.Register).Take(p.RegisterCount).ToArray();}
    public void Write(DeviceProfile p,ushort value){var data=_registers.GetOrAdd(p.Id,_=>new ushort[65536]);lock(data)data[p.Register]=p.Protocol=="虚拟 I/O"?(ushort)(value==0?0:1):value;}
    public string Start(DeviceProfile p)
    {
        p.Validate();if(!p.Enabled)throw new InvalidOperationException("设备已禁用");
        if(p.Protocol=="虚拟 I/O")return "虚拟 I/O 已就绪（软件点位）";
        lock(_gate)
        {
            if(_sessions.TryGetValue(p.Id,out var old))return old.Status;
            var session=new Session(p.Copy(),_registers.GetOrAdd(p.Id,_=>new ushort[65536]));
            try{session.Start();_sessions[p.Id]=session;return session.Status;}catch{session.Dispose();throw;}
        }
    }
    public void Stop(string id){StopNative(id);lock(_gate){if(_sessions.Remove(id,out var session))session.Dispose();}}
    public async Task<byte[]> ExchangeAsync(DeviceProfile p,byte[] request,CancellationToken token)
    {
        Session session;lock(_gate){if(!_sessions.TryGetValue(p.Id,out session))throw new InvalidOperationException("请先启动 TCP 服务器");}
        return await session.ExchangeAsync(request,token);
    }
    public void Dispose(){DisposeNative();lock(_gate){foreach(var session in _sessions.Values)session.Dispose();_sessions.Clear();_registers.Clear();}}
    public static ushort Crc(ReadOnlySpan<byte> bytes)
    {ushort crc=0xffff;foreach(var b in bytes){crc^=b;for(int bit=0;bit<8;bit++)crc=(ushort)((crc&1)!=0?(crc>>1)^0xa001:crc>>1);}return crc;}
    public static byte[] ProcessModbus(byte[] request,ushort[] registers)
    {
        if(request.Length<5)return new[]{(byte)((request.Length>0?request[0]:0)|0x80),(byte)3};
        var function=request[0];var address=BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(1));var count=BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3));
        byte[] Error(byte code)=>new[]{(byte)(function|0x80),code};
        lock(registers)
        {
            if(function is 3 or 4)
            {
                if(count<1||count>125||address+count>registers.Length)return Error(2);
                var reply=new byte[2+count*2];reply[0]=function;reply[1]=(byte)(count*2);for(int i=0;i<count;i++)BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2+i*2),registers[address+i]);return reply;
            }
            if(function==6){registers[address]=count;return request[..5];}
            if(function==16)
            {
                if(count<1||count>123||address+count>registers.Length||request.Length!=6+count*2||request[5]!=count*2)return Error(3);
                for(int i=0;i<count;i++)registers[address+i]=BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(6+i*2));return request[..5];
            }
            if(function is 1 or 2)
            {
                if(count<1||count>2000||address+count>registers.Length)return Error(2);var reply=new byte[2+(count+7)/8];reply[0]=function;reply[1]=(byte)(reply.Length-2);
                for(int i=0;i<count;i++)if(registers[address+i]!=0)reply[2+i/8]|=(byte)(1<<(i%8));return reply;
            }
            if(function==5){if(count is not (0 or 0xff00))return Error(3);registers[address]=(ushort)(count==0?0:1);return request[..5];}
            return Error(1);
        }
    }
    private sealed class Session(DeviceProfile profile,ushort[] registers):IDisposable
    {
        private readonly CancellationTokenSource _cancel=new();
        private readonly ConcurrentDictionary<int,TcpClient> _clients=new();
        private readonly Channel<byte[]> _incoming=Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256){FullMode=BoundedChannelFullMode.DropOldest});
        private TcpListener _listener;private SerialDevice _serial;private Task _worker;private int _nextClient;
        public string Status {get;private set;}="";
        public void Start()
        {
            if(profile.Protocol=="Modbus RTU 从站")
            {_serial=new SerialDevice(profile);Status="RTU 从站已启动："+profile.ComPort;_worker=Task.Run(SerialLoop);return;}
            if(profile.Protocol is not ("TCP 服务器" or "Modbus TCP 服务器"))throw new ArgumentException("不是服务器设备");
            if(!IPAddress.TryParse(profile.Host,out var address))throw new ArgumentException("服务器监听地址须为本机 IP，0.0.0.0 代表全部网卡");
            _listener=new TcpListener(address,profile.Port);_listener.Start();Status=$"监听中：{profile.Host}:{profile.Port}";_worker=AcceptLoop();
        }
        private async Task AcceptLoop()
        {
            try{while(!_cancel.IsCancellationRequested){var client=await _listener.AcceptTcpClientAsync(_cancel.Token);var id=Interlocked.Increment(ref _nextClient);_clients[id]=client;_ = Receive(client,id);}}
            catch(Exception ex)when(_cancel.IsCancellationRequested||ex is SocketException or ObjectDisposedException){if(!_cancel.IsCancellationRequested)Status=ex.Message;}
        }
        private async Task Receive(TcpClient client,int id)
        {
            try
            {
                var stream=client.GetStream();var token=_cancel.Token;
                while(!token.IsCancellationRequested)
                {
                    if(profile.Protocol=="TCP 服务器")
                    {var buffer=new byte[65536];var n=await stream.ReadAsync(buffer,token);if(n==0)break;_incoming.Writer.TryWrite(buffer[..n]);}
                    else
                    {
                        var header=new byte[7];await stream.ReadExactlyAsync(header,token);var length=BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                        if(header[2]!=0||header[3]!=0||length<2||length>254)throw new IOException("Modbus 请求头无效");
                        var pdu=new byte[length-1];await stream.ReadExactlyAsync(pdu,token);if(header[6]!=profile.UnitId)continue;
                        var response=ProcessModbus(pdu,registers);BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4),(ushort)(response.Length+1));
                        await stream.WriteAsync(header,token);await stream.WriteAsync(response,token);
                    }
                }
            }
            catch(Exception ex)when(ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException){}
            finally{_clients.TryRemove(id,out _);client.Dispose();}
        }
        private void SerialLoop()
        {
            try
            {
                while(!_cancel.IsCancellationRequested)
                {
                    var request=_serial.Receive(_cancel.Token);if(request.Length<4||Crc(request.AsSpan(0,request.Length-2))!=BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(request.Length-2)))continue;
                    if(request[0]!=profile.UnitId&&request[0]!=0)continue;
                    var response=ProcessModbus(request[1..^2],registers);if(request[0]==0)continue;
                    var packet=new byte[response.Length+3];packet[0]=request[0];response.CopyTo(packet,1);BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(packet.Length-2),Crc(packet.AsSpan(0,packet.Length-2)));_serial.Send(packet,_cancel.Token);
                }
            }
            catch(Exception ex){if(!_cancel.IsCancellationRequested)Status="RTU 已停止："+ex.Message;}
        }
        public async Task<byte[]> ExchangeAsync(byte[] request,CancellationToken token)
        {
            if(profile.Protocol!="TCP 服务器")throw new InvalidOperationException("该设备不是原始 TCP 服务器");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token,_cancel.Token);timeout.CancelAfter(profile.TimeoutMs);
            if(request.Length>0)
            {
                if(_clients.IsEmpty)throw new IOException("没有已连接的 TCP 客户端");
                foreach(var client in _clients.Values)await client.GetStream().WriteAsync(request,timeout.Token);
            }
            return await _incoming.Reader.ReadAsync(timeout.Token);
        }
        public async Task SendOnlyAsync(byte[] bytes,CancellationToken token)
        {
            if(profile.Protocol!="TCP 服务器")throw new InvalidOperationException("不是原始 TCP 服务器");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token,_cancel.Token);timeout.CancelAfter(profile.TimeoutMs);
            if(_clients.IsEmpty)throw new IOException("没有已连接的 TCP 客户端");
            foreach(var client in _clients.Values)await client.GetStream().WriteAsync(bytes,timeout.Token);
        }
        public async Task<byte[]> ReceiveOnlyAsync(CancellationToken token)
        {
            if(profile.Protocol!="TCP 服务器")throw new InvalidOperationException("不是原始 TCP 服务器");
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,_cancel.Token);return await _incoming.Reader.ReadAsync(linked.Token);
        }
        public void Dispose(){_cancel.Cancel();_listener?.Stop();foreach(var client in _clients.Values)client.Dispose();_serial?.Dispose();_incoming.Writer.TryComplete();}
    }
}
