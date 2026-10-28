using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.IO;
using AiVisionStudio.Services;

namespace AiVisionStudio.Algorithm.Impl;

public sealed class NativeModbusOp(string driver):IOperator
{
    public string Driver=>driver;
    private static readonly string[] Formats=["Signed","Unsigned","Long AB CD","Long CD AB","Long BA DC","Long DC BA","Float AB CD","Float CD AB","Float BA DC","Float DC BA","Double AB CD EF GH","Double GH EF CD AB","Double BA DC FE HG","Double HG FE DC BA"];
    private static int Width(string format)=>format.StartsWith("Double")?8:format is "Signed" or "Unsigned"?2:4;
    private static int[] Order(string format)=>format is "Signed" or "Unsigned"?[0,1]:format[(format.IndexOf(' ')+1)..].Where(c=>c!=' ').Select(c=>c-'A').ToArray();
    private static int Integer(ParamBag p,string key,int fallback,int minimum,int maximum)
    {
        if(!p.Has(key))return fallback;
        var raw=p.Raw(key);decimal value;
        ArgumentException Invalid()=>new($"Modbus {key} 必须为 {minimum}..{maximum} 范围内的整数");
        if(raw is string text){if(!long.TryParse(text,NumberStyles.Integer,CultureInfo.InvariantCulture,out var parsed))throw Invalid();value=parsed;}
        else
        {
            if(raw is not (sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal))throw Invalid();
            if(raw is double d&&(!double.IsFinite(d)||d!=Math.Truncate(d)))throw Invalid();
            if(raw is float f&&(!float.IsFinite(f)||f!=MathF.Truncate(f)))throw Invalid();
            try{value=Convert.ToDecimal(raw,CultureInfo.InvariantCulture);}catch(OverflowException){throw Invalid();}
        }
        if(value<minimum||value>maximum||value!=decimal.Truncate(value))throw Invalid();
        return (int)value;
    }
    public static object Decode(byte[] bytes,string format)
    {
        if(!Formats.Contains(format)||bytes.Length!=Width(format))throw new ArgumentException("Modbus 格式或字节长度无效");var data=new byte[bytes.Length];var order=Order(format);for(int i=0;i<data.Length;i++)data[order[i]]=bytes[i];
        object value=format=="Signed"?(object)(int)BinaryPrimitives.ReadInt16BigEndian(data):format=="Unsigned"?(object)(int)BinaryPrimitives.ReadUInt16BigEndian(data):format.StartsWith("Long")?(object)BinaryPrimitives.ReadInt32BigEndian(data):format.StartsWith("Float")?(object)(double)BinaryPrimitives.ReadSingleBigEndian(data):BinaryPrimitives.ReadDoubleBigEndian(data);
        if(value is double d&&!double.IsFinite(d))throw new InvalidDataException("Modbus 返回非有限浮点数");return value;
    }
    public static byte[] Encode(object value,string format)
    {
        if(!Formats.Contains(format)||value is bool or string||value==null)throw new ArgumentException("Modbus 写入需要有效数字和格式");var number=Convert.ToDouble(value,CultureInfo.InvariantCulture);if(!double.IsFinite(number))throw new ArgumentException("Modbus 数值必须有限");var data=new byte[Width(format)];
        if(format is "Signed" or "Unsigned"||format.StartsWith("Long"))
        {
            var low=format=="Signed"?short.MinValue:format=="Unsigned"?0:int.MinValue;var high=format=="Signed"?short.MaxValue:format=="Unsigned"?ushort.MaxValue:int.MaxValue;
            if(number<low||number>high||number!=Math.Truncate(number))throw new ArgumentException("Modbus 整数超出格式范围");
            if(format=="Signed")BinaryPrimitives.WriteInt16BigEndian(data,(short)number);else if(format=="Unsigned")BinaryPrimitives.WriteUInt16BigEndian(data,(ushort)number);else BinaryPrimitives.WriteInt32BigEndian(data,(int)number);
        }
        else if(format.StartsWith("Float")){if(!float.IsFinite((float)number))throw new ArgumentException("Modbus 单精度浮点溢出");BinaryPrimitives.WriteSingleBigEndian(data,(float)number);}else BinaryPrimitives.WriteDoubleBigEndian(data,number);
        return Order(format).Select(i=>data[i]).ToArray();
    }
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var write=driver.Contains("Write");if(write&&!NativeDeviceRegistration.Active(p))return true;var bits=driver.EndsWith("Bit");var profile=NativeDeviceRegistration.Device(ctx,p).Copy();if(profile.Category!="Modbus")throw new ArgumentException("请选择 Modbus 组设备");
        profile.UnitId=Integer(p,"Slave Address",1,1,255);var address=Integer(p,"Address",0,0,65535);
        var type=p.Str("Type",bits?"Coil":"Holding Register");var allowed=bits?new[]{"Coil","Discrete Input"}:new[]{"Holding Register","Input Register"};if(!allowed.Contains(type))throw new ArgumentException("Modbus 区域无效");
        if(write&&type==allowed[1])throw new NotSupportedException("Modbus 主站不能写入只读离散输入/输入寄存器；请写入线圈/保持寄存器");
        var format=p.Str("Format","Signed");if(!bits&&!Formats.Contains(format))throw new ArgumentException("Modbus 格式无效");var width=bits?1:Width(format);var output=new List<object>();
        byte[] Request(byte function,int at,int count,byte[] bytes=null)
        {var packet=new byte[bytes==null?5:6+bytes.Length];packet[0]=function;BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1),(ushort)at);BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3),(ushort)count);if(bytes!=null){packet[5]=(byte)bytes.Length;bytes.CopyTo(packet,6);}return packet;}
        byte[] Exchange(byte[] request)=>DeviceService.ExchangeModbusPduAsync(profile,request,ctx.CancellationToken).GetAwaiter().GetResult();
        if(!write)
        {
            var number=Integer(p,"Number",1,1,65536);var units=bits?1:width/2;if((long)address+(long)number*units>65536)throw new ArgumentException("Modbus 读取数量越界");
            for(int offset=0;offset<number;)
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();var count=Math.Min(number-offset,bits?2000:125/units);var registerCount=count*units;var function=(byte)(bits?(type=="Coil"?1:2):(type=="Holding Register"?3:4));var response=Exchange(Request(function,address+offset*units,registerCount));var expectedBytes=bits?(count+7)/8:count*width;
                if(response.Length!=2+expectedBytes||response[1]!=expectedBytes)throw new IOException("Modbus 读取字节数不匹配");for(int i=0;i<count;i++)output.Add(bits?(response[2+i/8]&(1<<(i%8)))!=0:Decode(response.AsSpan(2+i*width,width).ToArray(),format));offset+=count;
            }
            ctx.Vars.Set("Values",output);ctx.Vars.Set("Value",output);ctx.Vars.Set("Data",output);
        }
        else
        {
            var values=new List<object>();foreach(var name in p.StringList("_input_list"))
            {var group=p.Group(name)??throw new ArgumentException("Modbus 写入行不存在："+name);var key=bits?"Value":format.StartsWith("Float")||format.StartsWith("Double")?"Float Value":"Int Value";if(!group.TryGetValue(key,out var value)||value==null)throw new ArgumentException("Modbus 写入值不存在："+name+"."+key);if(value is IList list)values.AddRange(list.Cast<object>());else values.Add(value);}
            if(p.Bool("Enable Max Number")){var max=Integer(p,"Max Number",1,1,int.MaxValue);values=values.Take(max).ToList();}
            var units=bits?1:width/2;if(values.Count==0||(long)address+(long)values.Count*units>65536)throw new ArgumentException("Modbus 写入为空或超出地址范围");
            // Validate and encode every item before the first externally visible write.
            var encoded=values.Select(value=>bits?value is bool b?new[]{(byte)(b?1:0)}:throw new ArgumentException("Modbus 位只能写入 bool"):Encode(value,format)).ToArray();
            for(int offset=0;offset<values.Count;)
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();var count=Math.Min(values.Count-offset,bits?1968:123/units);byte[] bytes;
                if(bits){bytes=new byte[(count+7)/8];for(int i=0;i<count;i++)if(encoded[offset+i][0]!=0)bytes[i/8]|=(byte)(1<<(i%8));}else bytes=encoded.Skip(offset).Take(count).SelectMany(x=>x).ToArray();
                var request=Request((byte)(bits?15:16),address+offset*units,count*units,bytes);var response=Exchange(request);if(response.Length!=5||!response.AsSpan().SequenceEqual(request.AsSpan(0,5)))throw new IOException("Modbus 写入回执地址/数量不匹配；前面的分块可能已经写入");offset+=count;
            }
            ctx.Vars.Set("Count",values.Count);
        }
        return true;
    }
}
