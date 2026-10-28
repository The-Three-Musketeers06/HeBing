using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

public sealed class CloudBoundaryOp:IOperator
{
    public string Driver=>"3D Boundary";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;int im=p.Int("IM In");var source=ctx.Clouds.Get(im)??throw new ArgumentException("边缘提取输入为空");var selected=CloudRegionSelector.Select(source,p);if(selected.Count<3)throw new ArgumentException("边缘提取至少需要三个点");
        var local=CloudRegionSelector.CreateReferenceTransform(p.Group("Reference"));var points=selected.Select(i=>{var q=local(source.X[i],source.Y[i],source.Z[i]);return new Sample(i,new(q.X,q.Y,q.Z));}).Where(q=>q.Point.Finite).ToArray();if(points.Length<3)throw new ArgumentException("有限坐标不足");
        HashSet<int> boundary=[];var mode=p.Str("Extract Mode","ConcaveHull");switch(mode)
        {
            case "Latitude and longitude scanning method":
                int resolution=p.Int("Resolution",5);if(resolution is <1 or >3000)throw new ArgumentException("扫描分辨率必须在1..3000");
                for(int axis=0;axis<2;axis++)
                {
                    var min=points.Min(q=>axis==0?q.Point.Y:q.Point.X);var max=points.Max(q=>axis==0?q.Point.Y:q.Point.X);double step=(max-min)/resolution;
                    foreach(var group in points.GroupBy(q=>step<1e-12?0:Math.Min(resolution-1,(int)((axis==0?q.Point.Y-min:q.Point.X-min)/step))))
                    {boundary.Add(group.MinBy(q=>axis==0?q.Point.X:q.Point.Y).Index);boundary.Add(group.MaxBy(q=>axis==0?q.Point.X:q.Point.Y).Index);}
                }
                break;
            case "Organized":
                var raster=source.Raster??OwnedProfileCaptureOp.InferGrid(source);int size=p.Int("Size",3);if(size is not(3 or 5 or 7))throw new ArgumentException("邻域大小必须为3、5或7");var membership=points.Select(q=>q.Index).ToHashSet();
                for(int y=0;y<raster.Height;y++)for(int x=0;x<raster.Width;x++)
                {
                    int index=raster.PointIndices[y*raster.Width+x];if(!membership.Contains(index))continue;bool edge=false;
                    for(int dy=-size/2;dy<=size/2&&!edge;dy++)for(int dx=-size/2;dx<=size/2;dx++){int xx=x+dx,yy=y+dy;if(xx<0||xx>=raster.Width||yy<0||yy>=raster.Height||!membership.Contains(raster.PointIndices[yy*raster.Width+xx])){edge=true;break;}}
                    if(edge)boundary.Add(index);
                }
                break;
            case "ConcaveHull":boundary=Concave(ctx,points,p.Dbl("Alpha",.5));break;
            case "Based on normal":boundary=Normals(ctx,points,p.Dbl("Search Radius",.05),p.Int("Ksearch",10),p.Dbl("Angle Threshold",120));break;
            default:throw new ArgumentException("无效边缘提取模式："+mode);
        }
        if(boundary.Count==0)throw new ArgumentException("未提取到边缘，请调整参数");var ordered=boundary.Order().ToList();var pointset=new PointSet3D(source,ordered);ctx.Vars.Set("boundary",pointset);ctx.Vars.Set("Point Count",ordered.Count);DetectionOverlay2D.Begin(ctx,Driver);
        if(p.Bool("Boundary")){var cloud=new PointCloud();foreach(var i in ordered)cloud.Add(source.X[i],source.Y[i],source.Z[i]);DetectionOverlay2D.Add(ctx,p,Driver,"pointset",new(){["_size"]=Math.Clamp(p.Int("Size",3),1,7)},view:im);ctx.NativeOverlays[^1].Cloud=cloud;}
        if(mode=="ConcaveHull")ProfileMath.Warn(ctx,"自有凹包使用XY投影Delaunay alpha-shape；Alpha按平均点距映射半径，与原版密度参数标度仍待校准");return true;
    }
    private sealed record Sample(int Index,MeasureVector3 Point);
    private static HashSet<int> Concave(OpContext ctx,Sample[] samples,double density)
    {
        if(!double.IsFinite(density)||density<=0||density>1)throw new ArgumentException("凹包密度必须为(0,1]");
        var distinct=samples.GroupBy(q=>(q.Point.X,q.Point.Y)).Select(g=>g.First()).ToArray();if(distinct.Length<3)throw new ArgumentException("XY投影退化");
        if(distinct.Length>500000)throw new ArgumentException("凹包超过50万投影点，请先下采样");
        double minX=distinct.Min(q=>q.Point.X),minY=distinct.Min(q=>q.Point.Y),spanX=distinct.Max(q=>q.Point.X)-minX,spanY=distinct.Max(q=>q.Point.Y)-minY;
        if(spanX<1e-10||spanY<1e-10)throw new ArgumentException("XY投影退化，无法计算凹包");double scale=4094/Math.Max(spanX,spanY);
        var map=new Dictionary<Point2f,Sample>();foreach(var sample in distinct){var q=new Point2f((float)((sample.Point.X-minX)*scale+1),(float)((sample.Point.Y-minY)*scale+1));map.TryAdd(q,sample);}
        using var subdiv=new Subdiv2D(new Rect(0,0,4096,4096));foreach(var point in map.Keys){ctx.CancellationToken.ThrowIfCancellationRequested();subdiv.Insert(point);}
        // Alpha-shape triangles in XY. Density changes the radius relative to average point spacing.
        double spacing=Math.Sqrt(spanX*spanY/map.Count),limit=spacing*(1.2/density+.3);var edges=new Dictionary<(int,int),int>();
        foreach(var triangle in subdiv.GetTriangleList())
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();var pa=new Point2f(triangle.Item0,triangle.Item1);var pb=new Point2f(triangle.Item2,triangle.Item3);var pc=new Point2f(triangle.Item4,triangle.Item5);
            if(!map.TryGetValue(pa,out var a)||!map.TryGetValue(pb,out var b)||!map.TryGetValue(pc,out var c))continue;
            var ab=new Point2d(a.Point.X-b.Point.X,a.Point.Y-b.Point.Y);var ac=new Point2d(a.Point.X-c.Point.X,a.Point.Y-c.Point.Y);var bc=new Point2d(b.Point.X-c.Point.X,b.Point.Y-c.Point.Y);double area2=Math.Abs(ProfileMath.Cross(ab,ac));if(area2<1e-15)continue;double radius=Math.Sqrt(ProfileMath.Dot(ab,ab)*ProfileMath.Dot(ac,ac)*ProfileMath.Dot(bc,bc))/(2*area2);if(radius>limit)continue;
            foreach(var (i,j) in new[]{(a.Index,b.Index),(b.Index,c.Index),(c.Index,a.Index)}){var key=(Math.Min(i,j),Math.Max(i,j));edges[key]=edges.GetValueOrDefault(key)+1;}
        }
        return edges.Where(kv=>kv.Value==1).SelectMany(kv=>new[]{kv.Key.Item1,kv.Key.Item2}).ToHashSet();
    }
    private static HashSet<int> Normals(OpContext ctx,Sample[] points,double radius,int count,double threshold)
    {
        if(!double.IsFinite(radius+threshold)||radius<=0||count is <3 or >500||threshold<0||threshold>180)throw new ArgumentException("法向边缘邻域或角度阈值无效");
        (long X,long Y,long Z) Cell(MeasureVector3 q)=>((long)Math.Floor(q.X/radius),(long)Math.Floor(q.Y/radius),(long)Math.Floor(q.Z/radius));
        if(points.Any(q=>Math.Max(Math.Abs(q.Point.X),Math.Max(Math.Abs(q.Point.Y),Math.Abs(q.Point.Z)))/radius>long.MaxValue-10))throw new ArgumentException("法向边缘坐标跨度过大");
        var cells=points.GroupBy(q=>Cell(q.Point)).ToDictionary(g=>g.Key,g=>g.ToArray());var result=new HashSet<int>();
        foreach(var point in points)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();var cell=Cell(point.Point);var neighbors=new List<Sample>();for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)for(int z=-1;z<=1;z++)if(cells.TryGetValue((cell.X+x,cell.Y+y,cell.Z+z),out var bucket))neighbors.AddRange(bucket.Where(q=>q.Index!=point.Index&&(q.Point-point.Point).Length<=radius));
            var close=neighbors.OrderBy(q=>(q.Point-point.Point).Length).Take(count).ToList();if(close.Count<3)continue;
            var pca=CloudPrincipalAxes.Fit(close.Select(q=>q.Point).Append(point.Point).ToList());if(pca.Values[1]<1e-16)continue;var (u,v)=CloudPrincipalAxes.Basis(pca.Axes[2]);var angles=close.Select(q=>Math.Atan2((q.Point-point.Point).Dot(v),(q.Point-point.Point).Dot(u))).Order().ToArray();double maxGap=angles[0]+2*Math.PI-angles[^1];for(int i=1;i<angles.Length;i++)maxGap=Math.Max(maxGap,angles[i]-angles[i-1]);if(maxGap*180/Math.PI>=threshold-1e-8)result.Add(point.Index);
        }
        return result;
    }
}
