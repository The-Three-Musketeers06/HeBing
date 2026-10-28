using System.Collections;
using System.Globalization;
using AiVisionStudio.Services;
using V3 = AiVisionStudio.Algorithm.Impl.MeasureVector3;

namespace AiVisionStudio.Algorithm.Impl;

public static class GeometryCompletionRegistration
{
    public static void Register()
    {
        OperatorRegistry.Register(new MultilaterationOp());
        OperatorRegistry.Register(new BoundingBox3DOp());
    }
}

internal static class GeometryCompletion
{
    public static V3 ReadPoint(object value)
    {
        if(value is IReadOnlyDictionary<string,object> map)
        {
            var type=Convert.ToString(map.GetValueOrDefault("Type")??map.GetValueOrDefault("_type"));
            if(!string.IsNullOrEmpty(type)&&type!="point3d")throw new ArgumentException("需要三维点，实际为 "+type);
            var body=C3.Sub(map,"point3d")??map;
            return V3.Read(new Dictionary<string,object>{{"p",body}},"p");
        }
        throw new ArgumentException("三维点没有有效输入；二维点不能隐式补零 Z");
    }
    public static Dictionary<string,object> Typed(V3 v,string type="point3d")
    {var output=v.Native();output["Type"]=type;output["_type"]=type;return output;}
    public static V3 Point(PointCloud cloud,int i)=>new(cloud.X[i],cloud.Y[i],cloud.Z[i]);
    public static V3 Mean(IEnumerable<V3> input)
    {double x=0,y=0,z=0;int n=0;foreach(var p in input){if(!p.Finite)continue;n++;x+=(p.X-x)/n;y+=(p.Y-y)/n;z+=(p.Z-z)/n;}if(n==0)throw new ArgumentException("区域没有有限点");return new(x,y,z);}
    public static bool IsArray(ParamBag p)
    {var r=p.Bool("Use Region")?p.Raw("Region"):null;return r is IEnumerable<object> && r is not IReadOnlyDictionary<string,object>||r is IReadOnlyDictionary<string,object> map&&Convert.ToString(map.GetValueOrDefault("Type"))?.EndsWith(" array")==true;}
    public static object Pack(List<object> values,bool array)=>array?values:values.Single();
    public static (V3 Center,V3 X,V3 Y,V3 Z,double MiddleVariance) Principal(IReadOnlyList<V3> points,OpContext ctx)
    {
        var center=Mean(points);var a=new double[3,3];var vectors=new double[,]{{1,0,0},{0,1,0},{0,0,1}};
        foreach(var p in points){ctx.CancellationToken.ThrowIfCancellationRequested();var d=p-center;var xyz=new[]{d.X,d.Y,d.Z};for(int i=0;i<3;i++)for(int j=0;j<3;j++)a[i,j]+=xyz[i]*xyz[j];}
        for(int iteration=0;iteration<64;iteration++)
        {
            int p=0,q=1;for(int i=0;i<3;i++)for(int j=i+1;j<3;j++)if(Math.Abs(a[i,j])>Math.Abs(a[p,q])){p=i;q=j;}
            var scale=Math.Max(a[0,0],Math.Max(a[1,1],a[2,2]));if(scale==0||Math.Abs(a[p,q])<=scale*1e-14)break;
            var angle=.5*Math.Atan2(2*a[p,q],a[q,q]-a[p,p]);var c=Math.Cos(angle);var s=Math.Sin(angle);var pp=a[p,p];var qq=a[q,q];var pq=a[p,q];
            a[p,p]=c*c*pp-2*s*c*pq+s*s*qq;a[q,q]=s*s*pp+2*s*c*pq+c*c*qq;a[p,q]=a[q,p]=0;
            for(int k=0;k<3;k++){if(k!=p&&k!=q){var kp=a[k,p];var kq=a[k,q];a[k,p]=a[p,k]=c*kp-s*kq;a[k,q]=a[q,k]=s*kp+c*kq;}var vp=vectors[k,p];var vq=vectors[k,q];vectors[k,p]=c*vp-s*vq;vectors[k,q]=s*vp+c*vq;}
        }
        var order=Enumerable.Range(0,3).OrderBy(i=>a[i,i]).ToArray();
        V3 Vector(int i){var v=new V3(vectors[0,i],vectors[1,i],vectors[2,i]).Unit();var max=Math.Abs(v.X)>=Math.Abs(v.Y)&&Math.Abs(v.X)>=Math.Abs(v.Z)?v.X:Math.Abs(v.Y)>=Math.Abs(v.Z)?v.Y:v.Z;return max<0?v*-1:v;}
        var z=Vector(order[0]);var x=Vector(order[2]);return(center,x,z.Cross(x).Unit(),z,a[order[1],order[1]]/points.Count);
    }
    public static Dictionary<string,object> Rotation(V3 x,V3 y,V3 z)
    {
        var ry=Math.Asin(Math.Clamp(-x.Z,-1,1));double rx,rz;
        if(Math.Abs(Math.Cos(ry))>1e-10){rx=Math.Atan2(y.Z,z.Z);rz=Math.Atan2(x.Y,x.X);}else{rx=0;rz=Math.Atan2(-y.X,y.Y);}
        return new V3(rx*180/Math.PI,ry*180/Math.PI,rz*180/Math.PI).Native();
    }
    public static (Func<V3,V3> Local,Func<V3,V3> World,Func<V3,V3> Direction) Reference(ParamBag p)
    {
        var f=CloudRegionSelector.CreateReferenceTransform(p.Group("Reference"));V3 Local(V3 v){var q=f(v.X,v.Y,v.Z);return new(q.X,q.Y,q.Z);}
        var zero=Local(new());var ex=Local(new(1,0,0))-zero;var ey=Local(new(0,1,0))-zero;var ez=Local(new(0,0,1))-zero;
        V3 Direction(V3 v)=>new(ex.Dot(v),ey.Dot(v),ez.Dot(v));V3 World(V3 v)=>Direction(v-zero);return(Local,World,Direction);
    }
    public static void Pointset(OpContext ctx,ParamBag p,string driver,PointCloud cloud,IEnumerable<int> indices)
    {var display=new PointCloud();foreach(var i in indices)display.Add(cloud.X[i],cloud.Y[i],cloud.Z[i],cloud.I[i]);ctx.NativeOverlays.Add(new(){Type="pointset",ViewIndex=p.Int("IM In"),Cloud=display,Values=new(){{"_color","#00FF00"},{"_owned_operation",string.IsNullOrEmpty(ctx.OperationUuid)?driver:ctx.OperationUuid}}});}

    public static bool Centroid(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var cloud=ctx.Clouds.Get(p.Int("IM In"))??throw new ArgumentException("质心输入点云为空");var groups=CloudRegionSelector.SelectGroups(cloud,p);
        if(groups.Count==0)throw new ArgumentException("质心区域数组为空");var points=groups.Select(g=>Mean(g.Select(i=>Point(cloud,i)))).ToArray();var array=IsArray(p);
        var output=Pack(points.Select(v=>(object)Typed(v)).ToList(),array);ctx.Vars.Set("Pointe3D",output);ctx.Vars.Set("Point3D",output);ctx.Vars.Set("Point",output);
        // Legacy v6 projects bind Centroid as a scalar even when a single-element
        // CreateROI array supplies the region. Preserve the documented Pointe3D array.
        ctx.Vars.Set("Centroid",points.Length==1?Typed(points[0]):output);
        foreach(var axis in new[]{"X","Y","Z"})ctx.Vars.Set("Centroid."+axis,Pack(points.Select(v=>(object)(axis=="X"?v.X:axis=="Y"?v.Y:v.Z)).ToList(),array));
        ctx.Vars.Set("Centroid.Count",Pack(groups.Select(g=>(object)g.Count).ToList(),array));DetectionOverlay2D.Begin(ctx,"3D PointCloudCentroid");
        if(p.Bool("Centroid"))foreach(var point in points)DetectionOverlay2D.Add(ctx,p,"3D PointCloudCentroid","point3d",Typed(point));
        if(p.Bool("Pointset Cut"))Pointset(ctx,p,"3D PointCloudCentroid",cloud,groups.SelectMany(g=>g).Distinct());return true;
    }
    public static bool Create(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var mode=p.Str("Mode","Line from two points");var points=new List<(string,V3)>();Dictionary<string,object> output;string type;
        V3 Read(string key){var v=ReadPoint(p.Raw(key));points.Add((key,v));return v;}
        if(mode=="Line from two points")
        {
            var a=Read("Point1("+mode+")");var b=Read("Point2("+mode+")");var direction=(b-a).Unit();type="line3d";
            output=new(){{"Type",type},{"_type",type},{"Position",a.Native()},{"Direction",direction.Native()}};
            ctx.Vars.Set("line",output);ctx.Vars.Set("Segment",new Dictionary<string,object>{{"Type","segment3d"},{"Start",a.Native()},{"End",b.Native()}});
        }
        else
        {
            V3 a,u,v;
            if(mode=="Plane from three points"){a=Read("Point1("+mode+")");u=Read("Point2("+mode+")")-a;v=Read("Point3("+mode+")")-a;}
            else if(mode=="Plane from point and line")
            {
                a=Read("Point("+mode+")");var line=p.Raw("Line("+mode+")") as IReadOnlyDictionary<string,object>??throw new ArgumentException("缺少三维直线");
                var t=Convert.ToString(line.GetValueOrDefault("Type")??line.GetValueOrDefault("_type"));if(!string.IsNullOrEmpty(t)&&t!="line3d")throw new ArgumentException("输入不是三维直线");
                line=C3.Sub(line,"line3d")??line;u=V3.Read(line,"Direction").Unit();v=V3.Read(line,"Position")-a;
            }
            else throw new ArgumentException("不支持的几何构造模式："+mode);
            var cross=u.Unit().Cross(v.Unit());if(cross.Length<1e-10)throw new ArgumentException("输入几何共线，不能确定唯一平面");var normal=cross.Unit();type="plane";
            output=new(){{"Type",type},{"_type",type},{"Position",a.Native()},{"Normal",normal.Native()}};ctx.Vars.Set("plane",output);
        }
        DetectionOverlay2D.Begin(ctx,"3D GeometryCreate");foreach(var (key,point) in points)if(p.Bool("Display "+key))DetectionOverlay2D.Add(ctx,p,"3D GeometryCreate","point3d",Typed(point));
        if(p.Bool("Display "+(type=="plane"?"Plane":"Line")+"("+mode+")"))DetectionOverlay2D.Add(ctx,p,"3D GeometryCreate",type,output);
        if(mode=="Plane from point and line"&&p.Bool("Display Line("+mode+")")){var line=p.Group("Line("+mode+")");DetectionOverlay2D.Add(ctx,p,"3D GeometryCreate","line3d",new(C3.Sub(line,"line3d")??line));}
        return true;
    }
}

public sealed class MultilaterationOp:IOperator
{
    public string Driver=>"Multilateration";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var cloud=ctx.Clouds.Get(p.Int("IM In"));if(cloud==null||cloud.Count==0)throw new ArgumentException("多点定位输入点云为空");
        var points=new List<V3>();var regions=new HashSet<int>();var filter=p.Str("Use Dispersion Filter","No Dispersion Filter");
        if(filter is not ("No Dispersion Filter" or "Statistical Outlier Removal" or "RANSAC"))throw new ArgumentException("无效定位滤波方式："+filter);
        for(int k=1;k<=9;k++)
        {
            var setting=p.Group("Point"+k);if(setting?.GetValueOrDefault("Enable") is not true)continue;var mode=Convert.ToString(setting.GetValueOrDefault("Mode"));
            if(mode=="Point")points.Add(GeometryCompletion.ReadPoint(setting.GetValueOrDefault("Point")));
            else if(mode=="3D Rect Probe")
            {
                var indices=CloudRegionSelector.SelectGroups(cloud,setting.GetValueOrDefault(mode),ctx,p.Group("Reference")).SelectMany(g=>g).Distinct().Where(i=>GeometryCompletion.Point(cloud,i).Finite).ToList();
                if(indices.Count==0)throw new ArgumentException("多点定位第 "+k+" 个区域没有有效点");
                if(filter=="Statistical Outlier Removal")
                {
                    var cutoff=C3.N(p.Group(filter),"CutOff",2);if(!double.IsFinite(cutoff)||cutoff<=0)throw new ArgumentException("统计滤波因子必须为正");
                    var center=GeometryCompletion.Mean(indices.Select(i=>GeometryCompletion.Point(cloud,i)));var d=indices.Select(i=>(GeometryCompletion.Point(cloud,i)-center).Length).ToArray();var mean=d.Average();var limit=mean+cutoff*Math.Sqrt(d.Average(x=>(x-mean)*(x-mean)));indices=indices.Where((_,i)=>d[i]<=limit).ToList();
                }
                else if(filter=="RANSAC")
                {
                    var threshold=C3.N(p.Group("RANSAC"),"Distance Threshold",.1);var iterations=C3.N(p.Group("RANSAC"),"Iterations",50);
                    if(!double.IsFinite(threshold)||threshold<=0||iterations<1||iterations>100000||iterations!=Math.Truncate(iterations))throw new ArgumentException("无效 RANSAC 参数");
                    var random=new Random(0);var best=new List<int>();for(int n=0;n<iterations;n++){ctx.CancellationToken.ThrowIfCancellationRequested();var seed=GeometryCompletion.Point(cloud,indices[random.Next(indices.Count)]);var candidate=indices.Where(i=>(GeometryCompletion.Point(cloud,i)-seed).Length<=threshold).ToList();if(candidate.Count>best.Count)best=candidate;}indices=best;
                }
                points.Add(GeometryCompletion.Mean(indices.Select(i=>GeometryCompletion.Point(cloud,i))));regions.UnionWith(indices);
            }
            else throw new ArgumentException("无效定位点模式："+mode);
        }
        if(points.Count<2)throw new ArgumentException("多点定位至少需要两个有效定位点");var centerPoint=GeometryCompletion.Mean(points);var reference=GeometryCompletion.Reference(p);var local=reference.Local(centerPoint);var components=p.Group("Select Components");
        bool Adjust(string axis)=>components==null||components.GetValueOrDefault("Adjust "+axis+"-Position") is not false;
        var shift=reference.Direction(new(Adjust("X")?local.X:0,Adjust("Y")?local.Y:0,Adjust("Z")?local.Z:0));var output=cloud.Clone();
        for(int i=0;i<cloud.Count;i++){if((i&4095)==0)ctx.CancellationToken.ThrowIfCancellationRequested();output.X[i]=(float)(cloud.X[i]-shift.X);output.Y[i]=(float)(cloud.Y[i]-shift.Y);output.Z[i]=(float)(cloud.Z[i]-shift.Z);}
        ctx.Clouds.Set(p.Int("IM Out"),output);ctx.Vars.Set("point",GeometryCompletion.Typed(centerPoint));ctx.Vars.Set("Point.Count",points.Count);DetectionOverlay2D.Begin(ctx,Driver);
        if(p.Bool("Display Point"))DetectionOverlay2D.Add(ctx,p,Driver,"point3d",GeometryCompletion.Typed(centerPoint-shift),view:p.Int("IM Out"));
        if(p.Bool("Pointset Cut"))GeometryCompletion.Pointset(ctx,p,Driver,cloud,regions);
        return true;
    }
}

public sealed class BoundingBox3DOp:IOperator
{
    public string Driver=>"3D BoundingBox";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var cloud=ctx.Clouds.Get(p.Int("IM In"))??throw new ArgumentException("包围盒输入点云为空");var groups=CloudRegionSelector.SelectGroups(cloud,p);if(groups.Count==0)throw new ArgumentException("包围盒区域数组为空");
        var mode=p.Str("Bounding Box Type","AABB");if(mode is not ("AABB" or "OBB"))throw new ArgumentException("无效包围盒类型："+mode);var outputs=new List<object>();
        foreach(var group in groups)
        {
            var points=group.Select(i=>GeometryCompletion.Point(cloud,i)).Where(v=>v.Finite).ToArray();if(points.Length==0)throw new ArgumentException("包围盒区域为空");
            var axes=mode=="OBB"?GeometryCompletion.Principal(points,ctx):(new V3(),new V3(1,0,0),new V3(0,1,0),new V3(0,0,1),0d);
            var origin=axes.Item1;var x=axes.Item2;var y=axes.Item3;var z=axes.Item4;
            var coordinates=points.Select(v=>{var d=v-origin;return new V3(d.Dot(x),d.Dot(y),d.Dot(z));}).ToArray();
            var min=new V3(coordinates.Min(v=>v.X),coordinates.Min(v=>v.Y),coordinates.Min(v=>v.Z));var max=new V3(coordinates.Max(v=>v.X),coordinates.Max(v=>v.Y),coordinates.Max(v=>v.Z));var middle=(min+max)*.5;var size=max-min;
            Dictionary<string,object> output;
            if(mode=="AABB")output=new(){{"Type","box"},{"_type","box"},{"Start",min.Native()},{"End",max.Native()},{"Length",Math.Max(size.X,size.Y)},{"Width",Math.Min(size.X,size.Y)},{"Height",size.Z}};
            else output=new(){{"Type","orientedbox"},{"_type","orientedbox"},{"Center",(origin+x*middle.X+y*middle.Y+z*middle.Z).Native()},{"Size",size.Native()},{"Rotation",GeometryCompletion.Rotation(x,y,z)}};
            outputs.Add(output);
        }
        ctx.Vars.Set("bounding_box",outputs);DetectionOverlay2D.Begin(ctx,Driver);if(p.Bool("Bounding Box"))foreach(Dictionary<string,object> output in outputs)DetectionOverlay2D.Add(ctx,p,Driver,mode=="AABB"?"box":"orientedbox",output);
        if(mode=="OBB"){ctx.Vars.Set("Warning",true);ctx.Vars.Set("Message","旋转包围盒采用主轴包围，保证包含选中点；尚未实现任意形状全局最小体积搜索");}
        return true;
    }
}
