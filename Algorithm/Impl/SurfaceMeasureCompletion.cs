using OpenCvSharp;
namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Explicit height-field mesh integration, plus physical axis/PCA bounding boxes.</summary>
public sealed class SurfaceMeasureOp(bool volume):IOperator
{
    public string Driver=>volume?"3D Volume":"3D SurfaceArea";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var cloud=CloudSetOp.Require(ctx,p.Int("IM In"));var groups=CloudRegionSelector.SelectGroups(cloud,p);if(groups.Count==0||groups.Any(g=>g.Count<3))throw new ArgumentException("面积/体积的每个区域至少需要三个点");
        var values=new List<object>();var sets=new List<object>();var boxes=new List<Dictionary<string,object>>();var centers=new List<MeasureVector3>();string method=volume?p.Str("Algorithm","Bounding Box"):"Surface";
        foreach(var indices in groups)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();var points=MetrologyMath.Local(cloud,indices,p);var center=MetrologyMath.Mean(points);centers.Add(MetrologyMath.World(p.Group("Reference"),center));double value;
            if(method=="Bounding Box")
            {
                var g=p.Group("Bounding Box");var type=g?.GetValueOrDefault("Box Type")?.ToString()??"Axis-aligned Bounding Box";double reference=C3.N(C3.Sub(g,"Reference Height"),"Value",0);if(!double.IsFinite(reference))throw new ArgumentException("参考高度必须有限");
                if(type=="Axis-aligned Bounding Box")
                {
                    double x0=points.Min(q=>q.X),x1=points.Max(q=>q.X),y0=points.Min(q=>q.Y),y1=points.Max(q=>q.Y),z0=Math.Min(reference,points.Min(q=>q.Z)),z1=Math.Max(reference,points.Max(q=>q.Z));
                    value=(x1-x0)*(y1-y0)*(z1-z0);boxes.Add(new(){["Start"]=MetrologyMath.World(p.Group("Reference"),new(x0,y0,z0)).Native(),["End"]=MetrologyMath.World(p.Group("Reference"),new(x1,y1,z1)).Native()});
                }
                else if(type=="Oriented Bounding Box")
                {
                    // Include the reference-height footprint, then compute a PCA enclosing box.
                    var support=points.Concat(points.Select(q=>new MeasureVector3(q.X,q.Y,reference))).ToArray();var basis=MetrologyMath.Principal(support);var extents=basis.Axes.Select(axis=>{var d=support.Select(q=>(q-basis.Center).Dot(axis)).ToArray();return d.Max()-d.Min();}).ToArray();value=extents[0]*extents[1]*extents[2];
                    MetrologyMath.Warning(ctx,"自有旋转包围盒采用 PCA 主轴包围盒，并非最小体积包围盒；须按实际工件校准");
                }
                else throw new ArgumentException("无效包围盒类型："+type);
            }
            else if(method=="Surface")
            {
                value=TriangulatedArea(ctx,p,points);
                MetrologyMath.Warning(ctx,"自有表面积采用 XY 投影 Delaunay 三角化和搜索距离筛边；适用于单层表面，并非原版通用曲面贪心重建");
            }
            else if(method=="Contour Integration")
            {
                double dx=0,dy=0,reference=0;string mode="Mean";var g=p.Group("Contour Integration");
                if(volume)
                {
                    var res=C3.Sub(g,"Resampling");dx=C3.N(res,"X Resolution",0);dy=C3.N(res,"Y Resolution",0);mode=res?.GetValueOrDefault("Z Mode")?.ToString()??"Mean";reference=C3.N(C3.Sub(g,"Reference Height"),"Value",0);
                    var direction=g?.GetValueOrDefault("Cut Direction")?.ToString()??"X";if(direction is not("X" or "Y"))throw new ArgumentException("轮廓积分仅支持 X/Y 切片方向");
                    var outline=C3.Sub(g,"Outline Setting");if(outline?.GetValueOrDefault("Enable") is true){int n=(int)C3.N(outline,"Number of Contours",1000);if(n<2||n>100000)throw new ArgumentException("切片数必须为 2..100000");if(direction=="X")dx=(points.Max(q=>q.X)-points.Min(q=>q.X))/(n-1);else dy=(points.Max(q=>q.Y)-points.Min(q=>q.Y))/(n-1);}
                }
                if(!double.IsFinite(reference))throw new ArgumentException("参考高度必须有限");
                // Reject multiple surfaces along the raster normal instead of silently fusing them.
                var duplicate=points.GroupBy(q=>(q.X,q.Y)).Any(group=>group.Max(q=>q.Z)-group.Min(q=>q.Z)>1e-5);
                if(duplicate)throw new NotSupportedException("自有网格积分要求沿参考 Z 方向为单层表面；重叠或封闭表面请分区测量");
                var localCloud=new PointCloud();foreach(var q in points)localCloud.Add((float)q.X,(float)q.Y,(float)q.Z);
                var grid=CloudRasterizer.Build(ctx,localCloud,Enumerable.Range(0,localCloud.Count).ToArray(),dx,dy,mode);
                var raster=grid.Raster;var c=grid.Cloud;if(raster.Width<2||raster.Height<2)throw new ArgumentException("输入点云不能形成二维积分网格");
                double radius=p.GroupDbl("Reconstruct Parameters","Search Radius",2.5),multiplier=p.GroupDbl("Reconstruct Parameters","Nearest Neighbor Distance Multiplier",2.5);int neighbors=(int)p.GroupDbl("Reconstruct Parameters","Max Search Neighbours",50);
                if(!volume&&(!double.IsFinite(radius)||radius<=0||!double.IsFinite(multiplier)||multiplier<=0||neighbors<3||neighbors>100))throw new ArgumentException("表面重建搜索半径/邻点数/距离系数无效");
                double limit=volume?double.PositiveInfinity:Math.Min(radius,multiplier*Math.Min(raster.ResolutionX,raster.ResolutionY));int triangles=0;value=0;
                void Triangle(int a,int b,int d)
                {
                    if(a<0||b<0||d<0)return;var x=MetrologyMath.At(c,a);var y=MetrologyMath.At(c,b);var z=MetrologyMath.At(c,d);
                    if((x-y).Length>limit||(x-z).Length>limit||(y-z).Length>limit)return;
                    triangles++;if(!volume)value+=(y-x).Cross(z-x).Length/2;
                    else value+=PositivePrism(x,y,z,reference)+PositivePrism(x with{Z=-x.Z},y with{Z=-y.Z},z with{Z=-z.Z},-reference);
                }
                for(int y=0;y<raster.Height-1;y++)
                {
                    ctx.CancellationToken.ThrowIfCancellationRequested();for(int x=0;x<raster.Width-1;x++){int k=y*raster.Width+x;int a=raster.PointIndices[k],b=raster.PointIndices[k+1],c1=raster.PointIndices[k+raster.Width],d=raster.PointIndices[k+raster.Width+1];if(a>=0&&d>=0){Triangle(a,b,d);Triangle(a,d,c1);}else{Triangle(a,b,c1);Triangle(b,d,c1);}}
                }
                if(triangles==0)throw new ArgumentException("没有满足参数的有效表面三角形，请检查点距、区域和搜索范围");
                MetrologyMath.Warning(ctx,volume?"自有轮廓积分采用单层高度场网格和分片线性积分；缺失网格不填充，结果需校准":"自有表面积采用单层高度场网格三角化，搜索距离参与边界过滤；并非原版通用曲面贪心重建");
            }
            else throw new NotSupportedException("未支持的体积测量模式："+method);
            if(!double.IsFinite(value)||value<0)throw new ArgumentException("面积/体积无效");values.Add(value);sets.Add(new PointSet3D(cloud,indices));
        }
        bool array=p.Bool("Use Region")&&(p.Raw("Region") is IEnumerable<object>||Convert.ToString((p.Raw("Region") as IReadOnlyDictionary<string,object>)?.GetValueOrDefault("Type"))?.EndsWith(" array")==true);
        ctx.Vars.Set(volume?"volume_measurement":"surface_area",array?values:values[0]);ctx.Vars.Set("region",array?sets:sets[0]);DetectionOverlay2D.Begin(ctx,Driver);
        if(p.Bool(volume?"Volume Text":"Display Surface Area Text"))for(int i=0;i<values.Count;i++)MetrologyMath.Text(ctx,p,Driver,centers[i],$"{Convert.ToDouble(values[i]):0.000}");
        if(p.Bool("Box"))foreach(var box in boxes)DetectionOverlay2D.Add(ctx,p,Driver,"box",box,view:p.Int("IM In"));
        if(p.Bool("Pointset")||p.Bool("Pointset Cut"))cloud.DisplayIndices=groups.SelectMany(g=>g).Distinct().ToArray();return true;
    }
    private static double TriangulatedArea(OpContext ctx,ParamBag p,List<MeasureVector3> points)
    {
        double radius=p.GroupDbl("Reconstruct Parameters","Search Radius",2.5),multiplier=p.GroupDbl("Reconstruct Parameters","Nearest Neighbor Distance Multiplier",2.5);int neighbors=(int)p.GroupDbl("Reconstruct Parameters","Max Search Neighbours",50);
        if(!double.IsFinite(radius)||radius<=0||!double.IsFinite(multiplier)||multiplier<=0||neighbors<3||neighbors>100)throw new ArgumentException("表面重建搜索半径/邻点数/距离系数无效");
        double minX=points.Min(q=>q.X),minY=points.Min(q=>q.Y),range=Math.Max(points.Max(q=>q.X)-minX,points.Max(q=>q.Y)-minY);
        if(range<=1e-12)throw new ArgumentException("表面在 XY 投影退化");const int extent=100000;double scale=(extent-4)/range;var unique=new Dictionary<(float,float),MeasureVector3>();
        foreach(var point in points){var key=((float)((point.X-minX)*scale+2),(float)((point.Y-minY)*scale+2));if(unique.TryGetValue(key,out var old)&&Math.Abs(old.Z-point.Z)>1e-5)throw new NotSupportedException("表面积 XY 投影含重叠层，请旋转参考系或分区测量");unique.TryAdd(key,point);}
        if(unique.Count<3)throw new ArgumentException("表面有效投影点不足三个");using var subdiv=new Subdiv2D(new Rect(0,0,extent,extent));int n=0;
        foreach(var key in unique.Keys){if((n++&1023)==0)ctx.CancellationToken.ThrowIfCancellationRequested();subdiv.Insert(new Point2f(key.Item1,key.Item2));}
        var triangles=new List<(MeasureVector3 A,MeasureVector3 B,MeasureVector3 C)>();var nearest=new Dictionary<MeasureVector3,double>();var adjacency=new Dictionary<MeasureVector3,HashSet<MeasureVector3>>();
        void Edge(MeasureVector3 a,MeasureVector3 b){double d=(a-b).Length;if(!nearest.TryGetValue(a,out var prev)||d<prev)nearest[a]=d;if(!adjacency.TryGetValue(a,out var list))adjacency[a]=list=new();list.Add(b);}
        foreach(var t in subdiv.GetTriangleList())
        {
            if(!unique.TryGetValue((t.Item0,t.Item1),out var a)||!unique.TryGetValue((t.Item2,t.Item3),out var b)||!unique.TryGetValue((t.Item4,t.Item5),out var c))continue;
            triangles.Add((a,b,c));Edge(a,b);Edge(a,c);Edge(b,a);Edge(b,c);Edge(c,a);Edge(c,b);
        }
        var allowed=adjacency.ToDictionary(k=>k.Key,k=>k.Value.OrderBy(v=>(v-k.Key).Length).Take(neighbors).ToHashSet());
        bool Valid(MeasureVector3 a,MeasureVector3 b)=>(a-b).Length<=radius&&(a-b).Length<=multiplier*Math.Max(nearest[a],nearest[b])&&allowed[a].Contains(b)&&allowed[b].Contains(a);
        double area=0;int accepted=0;foreach(var t in triangles){ctx.CancellationToken.ThrowIfCancellationRequested();if(!Valid(t.A,t.B)||!Valid(t.B,t.C)||!Valid(t.A,t.C))continue;area+=(t.B-t.A).Cross(t.C-t.A).Length/2;accepted++;}
        if(accepted==0||!double.IsFinite(area)||area<=0)throw new ArgumentException("没有满足重建参数的有效三角形");return area;
    }
    // Integral of max(z-reference, 0) over a projected triangle; clipping handles crossings exactly.
    private static double PositivePrism(MeasureVector3 a,MeasureVector3 b,MeasureVector3 c,double reference)
    {
        var input=new[]{a,b,c};var polygon=new List<MeasureVector3>();
        for(int i=0;i<3;i++){var p=input[i];var q=input[(i+1)%3];double h=p.Z-reference,k=q.Z-reference;if(h>=0)polygon.Add(p);if(h>=0!=k>=0)polygon.Add(p+(q-p)*(h/(h-k)));}
        if(polygon.Count<3)return 0;double result=0;
        for(int i=1;i<polygon.Count-1;i++){var p=polygon[0];var q=polygon[i];var r=polygon[i+1];double area=Math.Abs((q.X-p.X)*(r.Y-p.Y)-(q.Y-p.Y)*(r.X-p.X))/2;result+=area*(p.Z+q.Z+r.Z-3*reference)/3;}return result;
    }
}
