namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Owned position adjustment. Original 4.5.1 reference captures are in shots/owned-kernel.</summary>
public sealed class PositionAdjustment3DOp : IOperator
{
    public string Driver => "3D PositionAdjustment";
    internal static IReadOnlyDictionary<string, object> Map(IReadOnlyDictionary<string, object> parent, string key)
        => C3.Sub(parent, key) ?? throw new ArgumentException("缺少位置调整参数：" + key);
    private static object Object(IReadOnlyDictionary<string,object> parent,string key)
        => parent!=null&&parent.TryGetValue(key,out var value)&&value!=null?value:throw new ArgumentException("缺少位置调整对象："+key);

    internal static MeasureVector3 Position(object raw)
    {
        if (raw is PlaneObj plane)
            return Finite(new(plane.CX, plane.CY, plane.CZ));
        if (raw is Box3 box) return Finite(new((box.X1+box.X2)/2,(box.Y1+box.Y2)/2,(box.Z1+box.Z2)/2));
        if (raw is not IReadOnlyDictionary<string, object> map) throw new ArgumentException("新原点必须是三维点、线、平面或盒子");
        var type=map.TryGetValue("Type",out var t)?t?.ToString()?.ToLowerInvariant():null;
        var body=type!=null && C3.Sub(map,type) is {} nested?nested:map;
        if(type=="box" || body.ContainsKey("Start") && body.ContainsKey("End"))
            return Finite((MeasureVector3.Read(body,"Start")+MeasureVector3.Read(body,"End"))*.5);
        if(type is "line3d" or "plane" || body.ContainsKey("Position")) return MeasureVector3.Read(body,"Position");
        if(type is null or "point3d") return MeasureVector3.Read(new Dictionary<string,object>{{"point",body}},"point");
        throw new ArgumentException("无效的新原点类型："+type);
    }
    private static MeasureVector3 Finite(MeasureVector3 v) => v.Finite?v:throw new ArgumentException("位置坐标必须为有限数值");
    private static MeasureVector3 Direction(object raw)
    {
        if(raw is PlaneObj plane) return Finite(new(plane.NX,plane.NY,plane.NZ));
        if(raw is not IReadOnlyDictionary<string,object> map)throw new ArgumentException("新方向必须是三维线或平面");
        var type=map.TryGetValue("Type",out var t)?t?.ToString():map.ContainsKey("Normal")?"plane":"line3d";
        if(type is not ("line3d" or "plane"))throw new ArgumentException("无效的新方向类型："+type);
        var body=C3.Sub(map,type)??map;
        return MeasureVector3.Read(body,type=="plane"?"Normal":"Direction");
    }
    private static bool Flag(IReadOnlyDictionary<string,object> group,char axis)
        => !group.TryGetValue("Adjust "+axis+"-Position",out var value) || value is true;

    public bool Run(OpContext ctx,ParamBag p)
    {
        var cloud=ctx.Clouds.Get(p.Int("IM In"));
        if(cloud==null||cloud.Count==0){ctx.Log("3D 位置调整：输入为空",true);return false;}
        var mode=p.Str("Mode","position only");
        MeasureVector3 origin; IReadOnlyDictionary<string,object> flags; double angle=0;
        if(mode=="position only")
        {
            var group=p.Group("Positioning parameters")??throw new ArgumentException("缺少位置参数");
            origin=Position(Object(group,"New Origin Point(or Object to center on)"));
            flags=C3.Sub(group,"Select components")??new Dictionary<string,object>();
        }
        else if(mode=="orientation")
        {
            var group=p.Group("Orientation adjusting parameters")??throw new ArgumentException("缺少方位参数");
            var geometry=Object(group,"New orientation"); var direction=Direction(geometry);
            if(Math.Abs(direction.X)+Math.Abs(direction.Y)<=1e-30)throw new ArgumentException("新方向在 XY 平面的投影必须非零");
            var target=Map(group,"Target direction for the rotation");
            var axis=target.TryGetValue("Rotation vector onto axis",out var a)?a?.ToString():"+X";
            var targetAngle=axis switch {"+X"=>0d,"+Y"=>Math.PI/2,"-X"=>Math.PI,"-Y"=>-Math.PI/2,_=>throw new ArgumentException("无效的目标轴："+axis)};
            angle=targetAngle-Math.Atan2(direction.Y,direction.X);
            var range=target.TryGetValue("Angle range",out var r)?r?.ToString():"360 degree";
            if(range=="180 degree") { while(angle>Math.PI/2)angle-=Math.PI;while(angle< -Math.PI/2)angle+=Math.PI; }
            else if(range!="360 degree")throw new ArgumentException("无效的角度范围："+range);
            var center=group.TryGetValue("Rotate around",out var c)?c?.ToString():"object center";
            origin=center switch {"object center"=>Position(geometry),"origin"=>new(0,0,0),"specific position"=>Position(Object(group,"Specific position")),_=>throw new ArgumentException("无效的旋转点类型："+center)};
            flags=C3.Sub(group,"Also adjust position")??new Dictionary<string,object>();
        }
        else throw new ArgumentException("无效的位置调整模式："+mode);
        var translation=new MeasureVector3(Flag(flags,'X')?origin.X:0,Flag(flags,'Y')?origin.Y:0,Flag(flags,'Z')?origin.Z:0);
        var type=p.Str("Transformation type","permanent");
        if(type=="temporary")
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            ctx.Vars.Set("Reference3D",new Dictionary<string,object>(StringComparer.Ordinal){
                ["X"]=(double)(float)translation.X,["Y"]=(double)(float)translation.Y,["Z"]=(double)(float)translation.Z,
                ["X Angle"]=0d,["Y Angle"]=0d,["Z Angle"]=(double)(float)(-angle*180/Math.PI),["_im"]=-1});
            ctx.Log("3D 位置调整（自有源码）：已输出临时参考坐标",false);
            return true;
        }
        if(type!="permanent")throw new ArgumentException("无效的变换类型："+type);
        var cosine=Math.Cos(angle);var sine=Math.Sin(angle);
        var result=cloud.Clone();
        for(int i=0;i<cloud.Count;i++)
        {
            if((i&4095)==0)ctx.CancellationToken.ThrowIfCancellationRequested();
            var x=cloud.X[i]-translation.X;var y=cloud.Y[i]-translation.Y;var z=cloud.Z[i]-translation.Z;
            var nx=(float)(cosine*x-sine*y);var ny=(float)(sine*x+cosine*y);var nz=(float)z;
            if(!float.IsFinite(nx)||!float.IsFinite(ny)||!float.IsFinite(nz))throw new ArithmeticException("位置调整输出坐标无效");
            result.X[i]=nx;result.Y[i]=ny;result.Z[i]=nz;
        }
        ctx.Clouds.Set(p.Int("IM Out"),result);
        ctx.Log($"3D 位置调整（自有源码）：{cloud.Count} 点 -> IM{p.Int("IM Out")}",false);
        return true;
    }
}
