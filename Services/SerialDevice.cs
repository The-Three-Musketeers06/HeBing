using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AiVisionStudio.Services;

/// <summary>Win32 serial settings and bounded synchronous transfers on a worker thread.</summary>
internal sealed class SerialDevice : IDisposable
{
    private readonly SafeFileHandle _handle;
    public SerialDevice(DeviceProfile p)
    {
        _handle=CreateFile("\\\\.\\"+p.ComPort,0xC0000000,0,IntPtr.Zero,3,0,IntPtr.Zero);
        if(_handle.IsInvalid){_handle.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error(),"无法打开 "+p.ComPort);}
        try
        {
            var dcb=new Dcb{Length=(uint)Marshal.SizeOf<Dcb>()};Check(GetCommState(_handle,ref dcb));
            Check(BuildCommDCB($"baud={p.BaudRate} parity={p.Parity} data={p.DataBits} stop={p.StopBits}",ref dcb));Check(SetCommState(_handle,ref dcb));
            var timeouts=new Timeouts{ReadInterval=50,ReadConstant=(uint)p.TimeoutMs,WriteConstant=(uint)p.TimeoutMs};Check(SetCommTimeouts(_handle,ref timeouts));
        }
        catch{_handle.Dispose();throw;}
    }
    public byte[] Exchange(byte[] request,CancellationToken token)
    {
        Send(request,token);return Receive(token);
    }
    public void Send(byte[] request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();Check(WriteFile(_handle,request,request.Length,out var count,IntPtr.Zero));
        if(count!=request.Length)throw new IOException("串口发送超时");
    }
    public byte[] Receive(CancellationToken token)
    {
        var buffer=new byte[65536];Check(ReadFile(_handle,buffer,buffer.Length,out var count,IntPtr.Zero));token.ThrowIfCancellationRequested();return buffer[..count];
    }
    private static void Check(bool result){if(!result)throw new Win32Exception(Marshal.GetLastWin32Error());}
    public void Dispose()=>_handle.Dispose();
    [StructLayout(LayoutKind.Sequential)]private struct Dcb {public uint Length,BaudRate,Flags;public ushort Reserved,XonLim,XoffLim;public byte ByteSize,Parity,StopBits;public byte XonChar,XoffChar,ErrorChar,EofChar,EvtChar;public ushort Reserved1;}
    [StructLayout(LayoutKind.Sequential)]private struct Timeouts {public uint ReadInterval,ReadMultiplier,ReadConstant,WriteMultiplier,WriteConstant;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetCommState(SafeFileHandle handle,ref Dcb state);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool BuildCommDCB(string configuration,ref Dcb state);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetCommState(SafeFileHandle handle,ref Dcb state);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetCommTimeouts(SafeFileHandle handle,ref Timeouts timeouts);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool WriteFile(SafeFileHandle handle,byte[] buffer,int count,out int written,IntPtr overlapped);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool ReadFile(SafeFileHandle handle,byte[] buffer,int count,out int read,IntPtr overlapped);
}
