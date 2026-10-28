namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Rigid camera-to-base coordinate conversion with a double precision pose.</summary>
public sealed class CoordinateConversionCompletionOp(bool eyeInHand):IOperator
{
    public string Driver=>"3D CoordinateConversion "+(eyeInHand?"EyeInHand":"EyeToHand");
    private static double[] Identity()=>new double[]{1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1};
    private static double[] Multiply(double[] a,double[] b){var r=new double[16];for(int i=0;i<4;i++)for(int j=0;j<4;j++)for(int k=0;k<4;k++)r[i*4+j]+=a[i*4+k]*b[k*4+j];return r;}
    private static double[] Quaternion(double x,double y,double z,double w)
    {
        double length=Math.Sqrt(x*x+y*y+z*z+w*w);if(!double.IsFinite(length)||length<1e-12)throw new ArgumentException("姿态四元数必须有限且非零");x/=length;y/=length;z/=length;w/=length;
        return new[]{1-2*(y*y+z*z),2*(x*y-z*w),2*(x*z+y*w),0,2*(x*y+z*w),1-2*(x*x+z*z),2*(y*z-x*w),0,2*(x*z-y*w),2*(y*z+x*w),1-2*(x*x+y*y),0,0,0,0,1};
    }
    private static IReadOnlyDictionary<string,object> Unwrap(object raw,string type)
    {
        if(raw is not IReadOnlyDictionary<string,object> m)throw new ArgumentException("缺少几何参数："+type);
        if(m.TryGetValue("Type",out var t)&&t?.ToString()!=type)throw new ArgumentException("几何类型不匹配："+t);return C3.Sub(m,type)??m;
    }
    private static void Translation(double[] a,MeasureVector3 p){if(!p.Finite)throw new ArgumentException("位置坐标必须有限");a[3]=p.X;a[7]=p.Y;a[11]=p.Z;}
    private static void Rigid(double[] a)
    {
        if(a.Length!=16||a.Any(v=>!double.IsFinite(v))||Math.Abs(a[12])+Math.Abs(a[13])+Math.Abs(a[14])+Math.Abs(a[15]-1)>1e-8)throw new ArgumentException("标定矩阵必须为有效的 4×4 仿射矩阵");
        var axes=new[]{new MeasureVector3(a[0],a[4],a[8]),new MeasureVector3(a[1],a[5],a[9]),new MeasureVector3(a[2],a[6],a[10])};
        if(axes.Any(v=>Math.Abs(v.Length-1)>1e-5)||Math.Abs(axes[0].Dot(axes[1]))+Math.Abs(axes[0].Dot(axes[2]))+Math.Abs(axes[1].Dot(axes[2]))>1e-5||Math.Abs(axes[0].Cross(axes[1]).Dot(axes[2])-1)>1e-5)throw new ArgumentException("位姿转换要求正交右手旋转矩阵，不能包含缩放、剪切或反射");
    }
    private static Dictionary<string,object> Rotation(double[] m)
    {
        double x,y,z,w;double tr=m[0]+m[5]+m[10];
        if(tr>0){double s=Math.Sqrt(tr+1)*2;w=s/4;x=(m[9]-m[6])/s;y=(m[2]-m[8])/s;z=(m[4]-m[1])/s;}
        else if(m[0]>m[5]&&m[0]>m[10]){double s=Math.Sqrt(1+m[0]-m[5]-m[10])*2;w=(m[9]-m[6])/s;x=s/4;y=(m[1]+m[4])/s;z=(m[2]+m[8])/s;}
        else if(m[5]>m[10]){double s=Math.Sqrt(1+m[5]-m[0]-m[10])*2;w=(m[2]-m[8])/s;x=(m[1]+m[4])/s;y=s/4;z=(m[6]+m[9])/s;}
        else{double s=Math.Sqrt(1+m[10]-m[0]-m[5])*2;w=(m[4]-m[1])/s;x=(m[2]+m[8])/s;y=(m[6]+m[9])/s;z=s/4;}
        double norm=Math.Sqrt(x*x+y*y+z*z+w*w);if(w<0)norm=-norm;return new(){["X"]=x/norm,["Y"]=y/norm,["Z"]=z/norm,["W"]=w/norm};
    }
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;ctx.CancellationToken.ThrowIfCancellationRequested();if(p.Raw("Calibration Matrix") is not IEnumerable<object> data)throw new ArgumentException("标定矩阵必须为 16 元素数组");var calibration=data.Select(Convert.ToDouble).ToArray();Rigid(calibration);
        double[] tool=Identity();if(eyeInHand){var tcp=Unwrap(p.Raw("TCP Pose"),"pose");var q=C3.Sub(tcp,"Quaternion")??throw new ArgumentException("TCP 缺少四元数");tool=Quaternion(C3.N(q,"X",double.NaN),C3.N(q,"Y",double.NaN),C3.N(q,"Z",double.NaN),C3.N(q,"W",double.NaN));Translation(tool,MeasureVector3.Read(tcp,"Position"));}
        double[] local;var type=p.Str("Input Type","point+plane");
        if(type=="point+plane")
        {
            var point=Unwrap(p.Raw("Point"),"point3d");var pos=MeasureVector3.Read(new Dictionary<string,object>{{"p",point}},"p");var normal=MetrologyMath.Geometry(p.Raw("Plane"),"plane").N;
            // Normal alone cannot define roll: choose the shortest rotation from +Z.
            local=normal.Z< -1+1e-10?Quaternion(1,0,0,0):Quaternion(-normal.Y,normal.X,0,1+normal.Z);Translation(local,pos);
            MetrologyMath.Warning(ctx,"点面输入无法确定绕法线的滚转角，自有实现采用从 +Z 到法线的最短旋转约定");
        }
        else if(type is "circle" or "circle3d")
        {
            var circle=Unwrap(p.Raw("Circle"),"circle3d");var rotation=MeasureVector3.Read(circle,"Rotation");double x=rotation.X*Math.PI/360,y=rotation.Y*Math.PI/360,z=rotation.Z*Math.PI/360;
            local=Multiply(Multiply(Quaternion(0,0,Math.Sin(z),Math.Cos(z)),Quaternion(0,Math.Sin(y),0,Math.Cos(y))),Quaternion(Math.Sin(x),0,0,Math.Cos(x)));Translation(local,MeasureVector3.Read(circle,"Center"));
        }
        else throw new ArgumentException("坐标转换输入类型无效："+type);
        var result=Multiply(Multiply(tool,calibration),local);Rigid(result);var position=new MeasureVector3(result[3],result[7],result[11]);var pose=new Dictionary<string,object>{["Position"]=position.Native(),["Quaternion"]=Rotation(result)};
        ctx.Vars.Set("pose",pose);ctx.Vars.Set("matrix",result.Cast<object>().ToList());return true;
    }
}
