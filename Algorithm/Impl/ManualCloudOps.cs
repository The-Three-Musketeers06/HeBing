namespace AiVisionStudio.Algorithm.Impl;

public sealed class CloudSetOp : IOperator
{
    public string Driver=>"3D PointCloudOperation";
    internal static PointCloud Require(OpContext ctx,int slot)
    {var c=ctx.Clouds.Get(slot);if(c==null||c.Count==0)throw new ArgumentException($"IM{slot} 输入点云为空");return c;}
    internal static void Publish(OpContext ctx,int slot,PointCloud cloud)
    {if(cloud.Count==0)throw new InvalidOperationException("输出点云为空");ctx.Images.Remove(slot);ctx.Profiles.Remove(slot);ctx.Clouds.Set(slot,cloud);ctx.Vars.Set("Point Count",cloud.Count);}
    internal static PointCloud Copy(PointCloud source,IEnumerable<int> indices)
    {var c=new PointCloud();foreach(var i in indices)c.Add(source.X[i],source.Y[i],source.Z[i],source.I[i]);return c;}
    public bool Run(OpContext ctx,ParamBag p)
    {
        var a=Require(ctx,p.Int("IM In"));var b=Require(ctx,p.Int("Second IM In",1));var mode=p.Str("Mode","add");
        var set=Enumerable.Range(0,b.Count).Select(b.At).ToHashSet();var output=new PointCloud();var written=new HashSet<(float,float,float)>();
        if(mode is not("add" or "intersection" or "subtract"))throw new ArgumentException("点云运算模式无效："+mode);
        for(int i=0;i<a.Count;i++)
        {
            if((i&4095)==0)ctx.CancellationToken.ThrowIfCancellationRequested();var point=a.At(i);
            if((mode=="add"||mode=="intersection"&&set.Contains(point)||mode=="subtract"&&!set.Contains(point))&&written.Add(point))output.Add(point.x,point.y,point.z,a.I[i]);
        }
        if(mode=="add")for(int i=0;i<b.Count;i++){var point=b.At(i);if(written.Add(point))output.Add(point.x,point.y,point.z,b.I[i]);}
        Publish(ctx,p.Int("IM Out",2),output);return true;
    }
}

public sealed class CloudCompareOp : IOperator
{
    public string Driver=>"3D CloudCompare";
    public bool Run(OpContext ctx,ParamBag p)
    {
        var cloud=CloudSetOp.Require(ctx,p.Int("IM In"));var target=CloudSetOp.Require(ctx,p.Int("Target",1));var distance=p.Dbl("Distance Threshold",.2);
        if(!double.IsFinite(distance)||distance<0)throw new ArgumentException("点云比较距离阈值无效");
        var tree=new CloudNeighbors3D(target,ctx.CancellationToken);var indexes=new List<int>();
        for(int i=0;i<cloud.Count;i++){if((i&1023)==0)ctx.CancellationToken.ThrowIfCancellationRequested();if(tree.NearestSquared(cloud.X[i],cloud.Y[i],cloud.Z[i])>distance*distance)indexes.Add(i);}
        CloudSetOp.Publish(ctx,p.Int("IM Out",2),CloudSetOp.Copy(cloud,indexes));return true;
    }
}

public sealed class CloudMergeOp : IOperator
{
    public string Driver {get;}
    public CloudMergeOp(bool splice=false)=>Driver=splice?"3D SplicingFilter":"3D PointCloudDeduplication";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var source=CloudSetOp.Require(ctx,p.Int("IM In"));var threshold=p.Dbl("Distance Threshold",.01);
        if(!double.IsFinite(threshold)||threshold<0)throw new ArgumentException("融合距离阈值无效");
        PointCloud work;
        if(Driver=="3D SplicingFilter")
        {
            var target=CloudSetOp.Require(ctx,p.Int("IM Target"));
            if(p.Raw("Transform Matrix") is not IEnumerable<object> values)throw new ArgumentException("变换矩阵必须为 16 元素数组");
            var m=values.Select(Convert.ToDouble).ToArray();
            if(m.Length!=16||m.Any(n=>!double.IsFinite(n))||Math.Abs(m[12])+Math.Abs(m[13])+Math.Abs(m[14])>1e-10||Math.Abs(m[15]-1)>1e-10)throw new ArgumentException("变换矩阵必须为有效的 4×4 仿射矩阵");
            work=target.Clone();
            for(int i=0;i<source.Count;i++)
            {
                if((i&4095)==0)ctx.CancellationToken.ThrowIfCancellationRequested();double x=source.X[i],y=source.Y[i],z=source.Z[i];
                work.Add((float)(m[0]*x+m[1]*y+m[2]*z+m[3]),(float)(m[4]*x+m[5]*y+m[6]*z+m[7]),(float)(m[8]*x+m[9]*y+m[10]*z+m[11]),source.I[i]);
            }
        }
        else work=CloudSetOp.Copy(source,CloudRegionSelector.Select(source,p));
        if(work.Count==0)throw new ArgumentException("融合区域为空");
        var groups=threshold==0?Enumerable.Range(0,work.Count).GroupBy(work.At).Select(g=>g.ToList()).ToList():CloudComponents.Find(work,Enumerable.Range(0,work.Count),threshold,ctx.CancellationToken);
        var result=new PointCloud();foreach(var group in groups)result.Add((float)group.Average(i=>(double)work.X[i]),(float)group.Average(i=>(double)work.Y[i]),(float)group.Average(i=>(double)work.Z[i]),(float)group.Average(i=>(double)work.I[i]));
        CloudSetOp.Publish(ctx,p.Int("IM Out"),result);
        if(p.Bool("Repeating Points")){DetectionOverlay2D.Begin(ctx,Driver);DetectionOverlay2D.Add(ctx,p,Driver,"pointset",new(){["_color"]="#FF6600"},view:p.Int("IM Out"));ctx.NativeOverlays[^1].Cloud=CloudSetOp.Copy(work,groups.Where(g=>g.Count>1).SelectMany(g=>g));}
        return true;
    }
}

public sealed class Angle3DOp : IOperator
{
    public string Driver=>"3D Angle";
    private static (MeasureVector3 Direction,bool Plane,Dictionary<string,object> Body) Geometry(object raw)
    {
        if(raw is PlaneObj plane)return(new MeasureVector3(plane.NX,plane.NY,plane.NZ).Unit(),true,new(){["Position"]=plane.Position,["Normal"]=plane.Normal});
        if(raw is not IReadOnlyDictionary<string,object> map)throw new ArgumentException("3D 角度几何类型无效");
        string type=map.GetValueOrDefault("Type")?.ToString()??map.GetValueOrDefault("_type")?.ToString()??(map.ContainsKey("Normal")?"plane":map.ContainsKey("Direction")?"line3d":"vector");
        var body=C3.Sub(map,type)??map;
        var vector=type switch{"line3d"=>MeasureVector3.Read(body,"Direction"),"plane"=>MeasureVector3.Read(body,"Normal"),"vector"=>MeasureVector3.Read(new Dictionary<string,object>{{"v",body}},"v"),_=>throw new ArgumentException("3D 角度只支持直线、平面和向量")};
        return(vector.Unit(),type=="plane",body.ToDictionary(k=>k.Key,k=>k.Value));
    }
    public bool Run(OpContext ctx,ParamBag p)
    {
        var first=p.Raw("First Object");var second=p.Raw("Second Object");
        object[] Items(object v)=>v is IEnumerable<object> list&&v is not IReadOnlyDictionary<string,object>?list.ToArray():new[]{v};
        var a=Items(first);var b=Items(second);int count=Math.Max(a.Length,b.Length);
        if(a.Length==0||b.Length==0||a.Length!=b.Length&&a.Length!=1&&b.Length!=1)throw new ArgumentException("角度几何数组长度不匹配");
        var angles=new List<object>();DetectionOverlay2D.Begin(ctx,Driver);
        for(int i=0;i<count;i++)
        {
            var one=Geometry(a[a.Length==1?0:i]);var two=Geometry(b[b.Length==1?0:i]);var direction=p.Str("Measurement Direction","direct angle");
            MeasureVector3 Project(MeasureVector3 v)=>direction switch{"direct angle"=>v,"in XY-plane" or "project onto XY plane"=>new(v.X,v.Y,0),"in XZ-plane" or "project onto XZ plane"=>new(v.X,0,v.Z),"in YZ-plane" or "project onto YZ plane"=>new(0,v.Y,v.Z),_=>throw new ArgumentException("未知角度测量方向："+direction)};
            var u=Project(one.Direction).Unit();var v=Project(two.Direction).Unit();var degrees=Math.Acos(Math.Clamp(u.Dot(v),-1,1))*180/Math.PI;
            if(one.Plane!=two.Plane)degrees=90-degrees;
            var range=p.Str("Range of returned angle","-90° to 90°");
            if(range=="-90° to 90°"&&one.Plane==two.Plane&&direction!="direct angle")
            {
                var determinant=direction.Contains("XY")?u.X*v.Y-u.Y*v.X:direction.Contains("XZ")?u.X*v.Z-u.Z*v.X:u.Y*v.Z-u.Z*v.Y;
                degrees=Math.Atan2(determinant,u.Dot(v))*180/Math.PI;
            }
            degrees=range switch{"0° to 180°"=>Math.Abs(degrees),"0° to 90°"=>Math.Min(Math.Abs(degrees),180-Math.Abs(degrees)),"-90° to 90°"=>degrees>90?degrees-180:degrees< -90?degrees+180:degrees,_=>throw new ArgumentException("未知角度范围："+range)};
            angles.Add(degrees);
            if(p.Bool("Display First Object"))DetectionOverlay2D.Add(ctx,p,Driver,one.Plane?"plane":"line3d",one.Body,view:p.Int("IM In"));
            if(p.Bool("Display Second Object"))DetectionOverlay2D.Add(ctx,p,Driver,two.Plane?"plane":"line3d",two.Body,view:p.Int("IM In"));
        }
        ctx.Vars.Set("angle",first is IEnumerable<object>||second is IEnumerable<object>?angles:angles[0]);return true;
    }
}
