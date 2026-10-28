namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Owned directional cone probing and three-dimensional geometric fits.</summary>
public sealed class RectProbe3DOp : IOperator
{
    public string Driver=>"3D RectProbe";
    private static double ProbeNumber(object raw,string name)
    {
        if(raw is bool||!double.TryParse(Convert.ToString(raw,System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value))
            throw new ArgumentException("探针参数必须为有限数值："+name);
        return value;
    }
    private sealed record Fit(MeasureVector3 Center,MeasureVector3 Direction,MeasureVector3 Normal,bool HasPlane=true);
    private sealed class ConeBucket
    {
        public double MinimumDepth;
        public readonly List<int> Points=new();
    }
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;ctx.CancellationToken.ThrowIfCancellationRequested();
        var im=p.Int("IM In");var cloud=ctx.Clouds.Get(im);
        if(cloud==null||cloud.Count==0){ctx.Log("3D 方形探针：输入为空",true);return false;}
        var selected=CloudRegionSelector.Select(cloud,p);
        if(selected.Count==0){ctx.Log("3D 方形探针：区域内无点",true);return false;}
        var local=CloudRegionSelector.CreateReferenceTransform(p.Group("Reference"));
        var points=selected.ToDictionary(i=>i,i=> {var q=local(cloud.X[i],cloud.Y[i],cloud.Z[i]);return new MeasureVector3(q.X,q.Y,q.Z);});
        var foreground=p.Group("Probe only foreground");
        if(foreground!=null&&foreground.TryGetValue("Enabled",out var enabled)&&enabled is true)
        {
            var threshold=C3.N(foreground,"Threshold",0);
            if(!double.IsFinite(threshold))throw new ArgumentException("前景阈值必须是有限数值");
            selected=selected.Where(i=>points[i].Z>=threshold).ToList();
        }
        var probing=p.Group("Probe Direction");var direction=probing!=null&&probing.TryGetValue("Direction",out var d)?d?.ToString():"no probing";
        if(direction!="no probing")
        {
            int axis=direction is "+X" or "-X"?0:direction is "+Y" or "-Y"?1:direction is "+Z" or "-Z"?2:throw new ArgumentException("无效的探针搜索方向："+direction);
            var cone=probing.TryGetValue("Approximate angle of exclusion cone",out var rawCone)?ProbeNumber(rawCone,"排除锥角"):45;
            var resolution=p.Has("Resolution")?ProbeNumber(p.Raw("Resolution"),"分辨率"):1000;
            if(!double.IsFinite(cone)||cone<=0||cone>=90||!double.IsFinite(resolution)||resolution<=0)throw new ArgumentException("探针排除锥角必须在 0..90 度之间，分辨率必须为正");
            double Coordinate(MeasureVector3 q)=>axis==0?q.X:axis==1?q.Y:q.Z;
            double Projection(MeasureVector3 q)=>direction[0]=='+'?Coordinate(q):-Coordinate(q);
            // The configured cone angle is its full apex angle; radius/depth uses the half-angle.
            var tangent=Math.Tan(cone*Math.PI/360);var step=1/resolution;
            (double U,double V) Lateral(MeasureVector3 q)=>axis==0?(q.Y,q.Z):axis==1?(q.X,q.Z):(q.X,q.Y);
            var uv=selected.ToDictionary(i=>i,i=>Lateral(points[i]));
            if(selected.Count==0)throw new ArgumentException("前景阈值过滤后没有有效点");
            var originU=uv.Values.Min(q=>q.U);var originV=uv.Values.Min(q=>q.V);
            var span=Math.Max(uv.Values.Max(q=>q.U)-originU,uv.Values.Max(q=>q.V)-originV);
            var cell=Math.Max(1e-9,Math.Max(step,span/Math.Sqrt(Math.Clamp(resolution,16,65536))));
            var buckets=new Dictionary<(long U,long V),ConeBucket>();
            var front=new List<int>();
            foreach(var index in selected.OrderBy(i=>Projection(points[i])))
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();
                var point=points[index];var depth=Projection(point);var q=uv[index];bool occluded=false;
                var key=((long)Math.Floor((q.U-originU)/cell),(long)Math.Floor((q.V-originV)/cell));
                bool Blocked(KeyValuePair<(long U,long V),ConeBucket> entry)
                {
                    var bucket=entry.Value;var ahead=depth-bucket.MinimumDepth;if(ahead<=step)return false;
                    var left=originU+entry.Key.U*cell;var bottom=originV+entry.Key.V*cell;
                    var du=Math.Max(0,Math.Max(left-q.U,q.U-left-cell));var dv=Math.Max(0,Math.Max(bottom-q.V,q.V-bottom-cell));
                    if(du*du+dv*dv>ahead*ahead*tangent*tangent)return false;
                    foreach(var candidate in bucket.Points)
                    {
                        var delta=point-points[candidate];var distance=depth-Projection(points[candidate]);
                        if(distance>step&&Math.Max(0,delta.Dot(delta)-distance*distance)<=distance*distance*tangent*tangent)return true;
                    }
                    return false;
                }
                if(front.Count>0)
                {
                    var radius=(depth-Projection(points[front[0]]))*tangent;
                    var reach=(long)Math.Ceiling(radius/cell)+1;
                    if(reach<=8)
                    {
                        for(long u=key.Item1-reach;u<=key.Item1+reach&&!occluded;u++)
                            for(long v=key.Item2-reach;v<=key.Item2+reach&&!occluded;v++)
                                if(buckets.TryGetValue((u,v),out var bucket))occluded=Blocked(new((u,v),bucket));
                    }
                    else foreach(var entry in buckets)
                    {ctx.CancellationToken.ThrowIfCancellationRequested();if(Blocked(entry)){occluded=true;break;}}
                }
                if(!occluded)
                {
                    front.Add(index);if(!buckets.TryGetValue(key,out var bucket))buckets[key]=bucket=new(){MinimumDepth=depth};bucket.Points.Add(index);
                }
            }
            selected=front;
        }
        var mode=p.Str("Output Geometry","Line");
        if(mode=="Segment")mode="Segment 3D";
        if(mode is not ("Point" or "Line" or "Plane" or "Segment 3D"))throw new ArgumentException("无效的探针输出几何："+mode);
        int minimum=mode=="Point"?1:mode=="Plane"?3:2;
        if(selected.Count<minimum)throw new ArgumentException("搜索到的有效边缘点不足以拟合 "+mode);
        MeasureVector3 World(int i)=>new(cloud.X[i],cloud.Y[i],cloud.Z[i]);
        var filter=p.Str("Use dispersion filter","No dispersion filter");
        var fit=Principal(selected.Select(World).ToArray(),ctx);
        if(filter=="Statistical outlier removal")
        {
            var cutoff=C3.N(p.Group(filter),"CutOff",2);
            if(!double.IsFinite(cutoff)||cutoff<=0)throw new ArgumentException("统计滤波因子必须为正");
            var distances=selected.Select(i=>Residual(World(i),fit,mode)).ToArray();var mean=distances.Average();
            var threshold=mean+cutoff*Math.Sqrt(distances.Average(x=>(x-mean)*(x-mean)));
            selected=selected.Where((_,i)=>distances[i]<=threshold).ToList();
        }
        else if(filter=="RANSAC")selected=Ransac(selected,World,p.Group("RANSAC"),mode,ctx);
        else if(filter!="No dispersion filter")throw new ArgumentException("无效的探针滤波方式："+filter);
        if(selected.Count<minimum)throw new ArgumentException("探针滤波后的点数不足");
        fit=Principal(selected.Select(World).ToArray(),ctx);
        if(mode=="Plane"&&!fit.HasPlane)throw new ArgumentException("拟合平面的点共线或全部重合");
        if(mode is "Line" or "Segment 3D" && selected.All(i=>(World(i)-fit.Center).Length<=1e-12))throw new ArgumentException("拟合直线的点全部重合");
        var set=new PointSet3D(cloud,selected);
        var output=new Dictionary<string,object>();
        output["middle_point"]=fit.Center.Native();output["region"]=set;
        var line=new Dictionary<string,object>{{"Position",fit.Center.Native()},{"Direction",fit.Direction.Native()}};
        var plane=new Dictionary<string,object>{{"Position",fit.Center.Native()},{"Normal",fit.Normal.Native()}};
        var positions=selected.Select(i=>(World(i)-fit.Center).Dot(fit.Direction)).ToArray();
        var start=fit.Center+fit.Direction*positions.Min();var end=fit.Center+fit.Direction*positions.Max();
        if(mode=="Line")output["line"]=line;
        if(mode=="Plane")output["plane"]=plane;
        if(mode=="Segment 3D")
        {output["segment"]=new Dictionary<string,object>{{"Start",start.Native()},{"End",end.Native()}};output["Segment Center"]=((start+end)*.5).Native();}
        foreach(var pair in output)ctx.Vars.Set(pair.Key,pair.Value);
        ctx.Vars.Set("Warning",true);ctx.Vars.Set("Message","自有方形探针：排除锥角按圆锥顶角解释（内部半角=输入/2）；该角度解释及离群滤波与原版尚待数值对照");
        if(p.Bool("Point 3D"))ctx.NativeOverlays.Add(new(){Type="point3d",ViewIndex=im,Values=fit.Center.Native()});
        if(p.Bool("Line 3D")&&mode=="Line")ctx.NativeOverlays.Add(new(){Type="line3d",ViewIndex=im,Values=line});
        if(p.Bool("Plane 3D")&&mode=="Plane")ctx.NativeOverlays.Add(new(){Type="plane",ViewIndex=im,Values=plane});
        if(p.Bool("Segment"))ctx.NativeOverlays.Add(new(){Type="segment3d",ViewIndex=im,Values=new(){{"Start",start.Native()},{"End",end.Native()}}});
        if(p.Bool("Segment Center"))ctx.NativeOverlays.Add(new(){Type="point3d",ViewIndex=im,Values=((start+end)*.5).Native()});
        if(p.Bool("Pointset")||p.Bool("Pointset Cut"))
        {
            ctx.Clouds.SetPointSets(im,new[]{set});var display=new PointCloud();foreach(var i in selected)display.Add(cloud.X[i],cloud.Y[i],cloud.Z[i],cloud.I[i]);
            ctx.NativeOverlays.Add(new(){Type="pointset",ViewIndex=im,Cloud=display,Values=new(){{"_color","#00FF00"}}});
        }
        ctx.Log($"3D 方形探针（自有源码）：{selected.Count}/{points.Count} 点，{mode}",false);return true;
    }
    private static double Residual(MeasureVector3 p,Fit fit,string mode)
        =>mode=="Plane"?Math.Abs((p-fit.Center).Dot(fit.Normal)):mode=="Point"?(p-fit.Center).Length:(p-fit.Center).Cross(fit.Direction).Length;
    private static List<int> Ransac(List<int> indices,Func<int,MeasureVector3> point,IReadOnlyDictionary<string,object> settings,string mode,OpContext ctx)
    {
        var threshold=C3.N(settings,"Distance Threshold",.1);var iterations=C3.N(settings,"Iterations",50);
        if(!double.IsFinite(threshold)||threshold<=0||!double.IsFinite(iterations)||iterations<1||iterations>100000||iterations!=Math.Truncate(iterations))throw new ArgumentException("无效的 RANSAC 参数");
        var random=new Random(0);var best=new List<int>();
        for(int k=0;k<iterations;k++)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            var sample=Enumerable.Range(0,mode=="Plane"?3:2).Select(_=>point(indices[random.Next(indices.Count)])).ToArray();
            Fit fit;
            try
            {
                var delta=(sample[1]-sample[0]).Unit();var normal=mode=="Plane"?(sample[1]-sample[0]).Cross(sample[2]-sample[0]).Unit():new MeasureVector3(0,0,1);
                fit=new(sample[0],delta,normal);
            }
            catch(ArgumentException){continue;}
            var inliers=indices.Where(i=>Residual(point(i),fit,mode)<=threshold).ToList();if(inliers.Count>best.Count)best=inliers;
        }
        return best;
    }
    private static Fit Principal(MeasureVector3[] points,OpContext ctx)
    {
        var center=new MeasureVector3(points.Average(p=>p.X),points.Average(p=>p.Y),points.Average(p=>p.Z));
        var a=new double[3,3];var v=new double[3,3]{{1,0,0},{0,1,0},{0,0,1}};
        foreach(var point in points)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            var p=point-center;var xyz=new[]{p.X,p.Y,p.Z};for(int i=0;i<3;i++)for(int j=0;j<3;j++)a[i,j]+=xyz[i]*xyz[j];
        }
        for(int iter=0;iter<64;iter++)
        {
            int p=0,q=1;for(int i=0;i<3;i++)for(int j=i+1;j<3;j++)if(Math.Abs(a[i,j])>Math.Abs(a[p,q])){p=i;q=j;}
            var scale=Math.Max(Math.Abs(a[0,0]),Math.Max(Math.Abs(a[1,1]),Math.Abs(a[2,2])));
            if(scale==0||Math.Abs(a[p,q])<=scale*1e-14)break;
            var angle=.5*Math.Atan2(2*a[p,q],a[q,q]-a[p,p]);var c=Math.Cos(angle);var s=Math.Sin(angle);
            var pp=a[p,p];var qq=a[q,q];var pq=a[p,q];a[p,p]=c*c*pp-2*s*c*pq+s*s*qq;a[q,q]=s*s*pp+2*s*c*pq+c*c*qq;a[p,q]=a[q,p]=0;
            for(int k=0;k<3;k++)
            {
                if(k!=p&&k!=q){var kp=a[k,p];var kq=a[k,q];a[k,p]=a[p,k]=c*kp-s*kq;a[k,q]=a[q,k]=s*kp+c*kq;}
                var vp=v[k,p];var vq=v[k,q];v[k,p]=c*vp-s*vq;v[k,q]=s*vp+c*vq;
            }
        }
        var order=Enumerable.Range(0,3).OrderBy(i=>a[i,i]).ToArray();
        MeasureVector3 Vector(int i)
        {
            var d=new MeasureVector3(v[0,i],v[1,i],v[2,i]).Unit();
            var max=Math.Abs(d.X)>=Math.Abs(d.Y)&&Math.Abs(d.X)>=Math.Abs(d.Z)?d.X:Math.Abs(d.Y)>=Math.Abs(d.Z)?d.Y:d.Z;
            return max<0?d*-1:d;
        }
        return new(center,Vector(order[2]),Vector(order[0]),a[order[1],order[1]]>Math.Max(1e-20,a[order[2],order[2]]*1e-12));
    }
}
