using System.Globalization;
using System.IO;
using System.Text.Json;
using AiVisionStudio.Services;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

public static class MetrologyCompletionRegistration
{
    public static void Register()
    {
        foreach(var driver in new[]{"Coplanarity","RegressPlaneCoplanarity","SeatSurfaceCoplanarity"}) OperatorRegistry.Register(new CoplanarityMeasurementOp(driver));
        OperatorRegistry.Register(new MultiHeightMeasurementOp());
        foreach(var driver in new[]{"3D ParallelismPlaneLine","3D ParallelismPlanePlane","3D PerpendicularityPlaneLine","3D PerpendicularityPlanePlane"}) OperatorRegistry.Register(new GeometricToleranceOp(driver));
        OperatorRegistry.Register(new SurfaceMeasureOp(false));OperatorRegistry.Register(new SurfaceMeasureOp(true));
        OperatorRegistry.Register(new PlaneProfileMeasurementOp());
        OperatorRegistry.Register(new CoordinateConversionCompletionOp(false));OperatorRegistry.Register(new CoordinateConversionCompletionOp(true));
        OperatorRegistry.Register(new PointPositionCompletionOp());OperatorRegistry.Register(new NominalLineProfileOp());OperatorRegistry.Register(new NominalArcProfileOp());
        OperatorRegistry.Register(new CalibrationMatrixCompletionOp());OperatorRegistry.Register(new HandEyeCalibrationCompletionOp(true));OperatorRegistry.Register(new HandEyeCalibrationCompletionOp(false));
    }
}

internal static class MetrologyMath
{
    internal static MeasureVector3 At(PointCloud c,int i)=>new(c.X[i],c.Y[i],c.Z[i]);
    internal static MeasureVector3 Mean(IReadOnlyList<MeasureVector3> a)
    {if(a.Count==0||a.Any(p=>!p.Finite))throw new ArgumentException("测量点为空或含非有限坐标");return new(a.Average(p=>p.X),a.Average(p=>p.Y),a.Average(p=>p.Z));}
    internal static (MeasureVector3 Center,MeasureVector3[] Axes,double[] Values) Principal(IReadOnlyList<MeasureVector3> points)
    {
        var center=Mean(points);using var covariance=new Mat(3,3,MatType.CV_64F,Scalar.All(0));
        foreach(var p in points){var d=p-center;double[] a={d.X,d.Y,d.Z};for(int i=0;i<3;i++)for(int j=0;j<3;j++)covariance.Set(i,j,covariance.At<double>(i,j)+a[i]*a[j]);}
        using var eigenvalues=new Mat();using var eigenvectors=new Mat();Cv2.Eigen(covariance,eigenvalues,eigenvectors);
        var axes=Enumerable.Range(0,3).Select(i=>new MeasureVector3(eigenvectors.At<double>(i,0),eigenvectors.At<double>(i,1),eigenvectors.At<double>(i,2)).Unit()).ToArray();
        return(center,axes,Enumerable.Range(0,3).Select(i=>eigenvalues.At<double>(i)).ToArray());
    }
    internal static (MeasureVector3 P,MeasureVector3 N) FitPlane(IReadOnlyList<MeasureVector3> a)
    {
        if(a.Count<3)throw new ArgumentException("平面拟合至少需要三个点");var e=Principal(a);
        if(e.Values[1]<=Math.Max(1e-16,e.Values[0]*1e-12))throw new ArgumentException("测量点共线，不能确定平面");
        return(e.Center,e.Axes[2].Z<0?e.Axes[2]*-1:e.Axes[2]);
    }
    internal static Dictionary<string,object> Plane(MeasureVector3 p,MeasureVector3 n)=>new(){["Position"]=p.Native(),["Normal"]=n.Native()};
    internal static (MeasureVector3 P,MeasureVector3 N) Geometry(object raw,string type)
    {
        if(raw is PlaneObj p&&type=="plane")return(new(p.CX,p.CY,p.CZ),new MeasureVector3(p.NX,p.NY,p.NZ).Unit());
        if(raw is not IReadOnlyDictionary<string,object> m)throw new ArgumentException("几何参数未绑定："+type);
        if(m.TryGetValue("Type",out var t)&&Convert.ToString(t)!=type)throw new ArgumentException("几何类型不符："+t);
        var b=C3.Sub(m,type)??m;return(MeasureVector3.Read(b,"Position"),MeasureVector3.Read(b,type=="plane"?"Normal":"Direction").Unit());
    }
    internal static List<MeasureVector3> ReadPoints(OpContext ctx,string path)
    {
        if(string.IsNullOrWhiteSpace(path))throw new ArgumentException("测量点文件不能为空");var file=ctx.ResolvePath(path);var text=File.ReadAllText(file).TrimStart('\uFEFF',' ','\r','\n','\t');
        var result=new List<MeasureVector3>();
        if(text.StartsWith('[')||text.StartsWith('{'))
        {
            using var doc=JsonDocument.Parse(text);var root=doc.RootElement;if(root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("Points",out var list))root=list;
            if(root.ValueKind!=JsonValueKind.Array)throw new ArgumentException("测量点 JSON 必须为 XYZ 对象或三元素数组的数组");
            foreach(var p in root.EnumerateArray())result.Add(p.ValueKind==JsonValueKind.Array&&p.GetArrayLength()==3?new(p[0].GetDouble(),p[1].GetDouble(),p[2].GetDouble()):new(p.GetProperty("X").GetDouble(),p.GetProperty("Y").GetDouble(),p.GetProperty("Z").GetDouble()));
        }
        else
        {
            int number=0;foreach(var line in text.Split('\n'))
            {
                number++;if(string.IsNullOrWhiteSpace(line))continue;var fields=line.Trim().Split(new[]{',',';','\t',' '},StringSplitOptions.RemoveEmptyEntries).Select(s=>s.Trim('"')).ToArray();
                if(number==1&&fields.Length==3&&fields[0].Equals("X",StringComparison.OrdinalIgnoreCase)&&fields[1].Equals("Y",StringComparison.OrdinalIgnoreCase)&&fields[2].Equals("Z",StringComparison.OrdinalIgnoreCase))continue;
                if(fields.Length!=3)throw new ArgumentException($"测量点 CSV 第 {number} 行不是 XYZ 三列");
                result.Add(new(double.Parse(fields[0],CultureInfo.InvariantCulture),double.Parse(fields[1],CultureInfo.InvariantCulture),double.Parse(fields[2],CultureInfo.InvariantCulture)));
            }
        }
        Mean(result);return result;
    }
    internal static void Warning(OpContext c,string text){c.Vars.Set("Warning",true);c.Vars.Set("Message",text);}
    internal static void Threshold(OpContext c,ParamBag p,double value)
    {
        var g=p.Group("Threshold Settings");bool enabled=g?.GetValueOrDefault("Enable") is true;
        var min=C3.N(g,"Minimum Value",0);var max=C3.N(g,"Maximum Value",double.MaxValue);
        if(enabled&&(!double.IsFinite(min)||!double.IsFinite(max)||min>max))throw new ArgumentException("公差范围无效");
        // The execution engine owns Result; lowercase result is the dimensional verdict.
        c.Vars.Set("result",!enabled||value>=min&&value<=max?"OK":"NG");
    }
    internal static void Text(OpContext c,ParamBag p,string owner,MeasureVector3 at,string text)=>DetectionOverlay2D.Add(c,p,owner,"text3d",new(){["Position"]=at.Native(),["Text"]=text,["Size"]=12,["_color"]="#00D0E0"},view:p.Int("IM In"));
    internal static void Point(OpContext c,ParamBag p,string owner,MeasureVector3 at)=>DetectionOverlay2D.Add(c,p,owner,"point3d",at.Native(),view:p.Int("IM In"));
    internal static void PlaneOverlay(OpContext c,ParamBag p,string owner,MeasureVector3 at,MeasureVector3 n)=>DetectionOverlay2D.Add(c,p,owner,"plane",Plane(at,n),view:p.Int("IM In"));
    internal static List<MeasureVector3> Local(PointCloud cloud,IEnumerable<int> indices,ParamBag p)
    {var transform=CloudRegionSelector.CreateReferenceTransform(p.Group("Reference"));return indices.Select(i=>{var a=transform(cloud.X[i],cloud.Y[i],cloud.Z[i]);return new MeasureVector3(a.X,a.Y,a.Z);}).ToList();}
    internal static MeasureVector3 World(IReadOnlyDictionary<string,object> r,MeasureVector3 p)
    {
        double rx=C3.N(r,"X Angle",0)*Math.PI/180,ry=C3.N(r,"Y Angle",0)*Math.PI/180,rz=C3.N(r,"Z Angle",0)*Math.PI/180;
        var y=Math.Cos(rx)*p.Y-Math.Sin(rx)*p.Z;var z=Math.Sin(rx)*p.Y+Math.Cos(rx)*p.Z;var x=Math.Cos(ry)*p.X+Math.Sin(ry)*z;z=-Math.Sin(ry)*p.X+Math.Cos(ry)*z;
        return new(Math.Cos(rz)*x-Math.Sin(rz)*y+C3.N(r,"X",0),Math.Sin(rz)*x+Math.Cos(rz)*y+C3.N(r,"Y",0),z+C3.N(r,"Z",0));
    }
}

public sealed class MultiHeightMeasurementOp:IOperator
{
    public string Driver=>"MultiHeight";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var cloud=CloudSetOp.Require(ctx,p.Int("IM In"));int count=p.Int("Region Number",2);if(count<1||count>32)throw new ArgumentException("区域数量必须为 1..32");
        var heights=new List<object>();var points=new List<object>();var sets=new List<object>();var output=new Dictionary<string,object>();
        double Quantile(double[] z,double percent){if(!double.IsFinite(percent)||percent<0||percent>100)throw new ArgumentException("百分位必须为 0..100");var k=(z.Length-1)*percent/100;int lo=(int)k;return z[lo]+(z[(int)Math.Ceiling(k)]-z[lo])*(k-lo);}
        for(int i=1;i<=count;i++)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();var indices=CloudRegionSelector.SelectGroups(cloud,p.Raw("Region_"+i),ctx).SelectMany(a=>a).Distinct().ToList();if(indices.Count==0)throw new ArgumentException($"Region_{i} 为空");
            var z=indices.Select(k=>(double)cloud.Z[k]).Order().ToArray();if(z.Any(v=>!double.IsFinite(v)))throw new ArgumentException("高度包含非有限值");
            var trim=p.Str("Outlier removal method","Percentile trimming");
            if(trim=="Percentile trimming")
            {
                var g=p.Group("Percentile trimming");double low=C3.N(g,"Percent to trim from bottom",10),high=C3.N(g,"Percent to trim from top",10);
                if(low<0||high<0||low+high>=100)throw new ArgumentException("剔除百分比无效");var a=Quantile(z,low);var b=Quantile(z,100-high);z=z.Where(v=>v>=a&&v<=b).ToArray();if(z.Length==0)throw new ArgumentException("剔除后点集为空");
            }
            else if(trim!="None")throw new ArgumentException("未知离群剔除方式");
            var value=p.Str("Height mode","Mean") switch{"Mean"=>z.Average(),"Minimum"=>z[0],"Maximum"=>z[^1],"Median"=>Quantile(z,50),"Percentile"=>Quantile(z,p.Dbl("Percentile",10)),_=>throw new ArgumentException("未知高度模式")};
            int k=indices.MinBy(k=>Math.Abs(cloud.Z[k]-value));var at=MetrologyMath.At(cloud,k);heights.Add(value);points.Add(at.Native());sets.Add(new PointSet3D(cloud,indices));
            output["height_"+i+"_value"]=value;output["height_"+i+"_position_point"]=at.Native();
        }
        DetectionOverlay2D.Begin(ctx,Driver);for(int i=0;i<count;i++){var at=MeasureVector3.Read(new Dictionary<string,object>{{"p",points[i]}},"p");if(p.Bool("Point 3D"))MetrologyMath.Point(ctx,p,Driver,at);if(p.Bool("Height Text"))MetrologyMath.Text(ctx,p,Driver,at,$"{i+1}: {Convert.ToDouble(heights[i]):0.000}");}
        foreach(var item in output)ctx.Vars.Set(item.Key,item.Value);ctx.Vars.Set("height",heights);ctx.Vars.Set("height_value",heights);ctx.Vars.Set("height_points",points);ctx.Vars.Set("region",sets);
        MetrologyMath.Warning(ctx,"自有多区域高度统计；百分位插值与原版数值尚未校准");return true;
    }
}

public sealed class CoplanarityMeasurementOp(string driver):IOperator
{
    public string Driver=>driver;
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var cloud=CloudSetOp.Require(ctx,p.Int("IM In"));var groups=driver=="RegressPlaneCoplanarity"?CloudRegionSelector.SelectGroups(cloud,p.Raw("Region"),ctx,p.Group("Reference")):CloudRegionSelector.SelectGroups(cloud,p);
        if(groups.Count==0||groups.Any(a=>a.Count==0))throw new ArgumentException("共面度区域为空");
        var selected=groups.SelectMany(g=>g).Distinct().ToList();var local=MetrologyMath.Local(cloud,Enumerable.Range(0,cloud.Count),p);var terminals=new List<List<int>>();
        if(driver=="RegressPlaneCoplanarity")terminals=groups;
        else
        {
            var g=p.Group(driver=="Coplanarity"?"Blob Parameters":"Terminal Settings");
            double radius=C3.N(g,driver=="Coplanarity"?"Cluster tolerance":"Minimum Distance Tolerance of Terminals",.1);
            int min=(int)C3.N(g,driver=="Coplanarity"?"Min cluster size":"Minimum Number of Terminal Points",1),max=(int)C3.N(g,driver=="Coplanarity"?"Max cluster size":"Maximum Number of Terminal Points",10000000);
            if(!double.IsFinite(radius)||radius<=0||min<1||max<min)throw new ArgumentException("共面度聚类参数无效");
            terminals=CloudComponents.Find(cloud,selected,radius,ctx.CancellationToken).Where(a=>a.Count>=min&&a.Count<=max).ToList();
            if(driver=="Coplanarity")
            {
                var sort=g?.GetValueOrDefault("Sort mode")?.ToString()??"-X,-Y";var offset=C3.N(g,"Offset",1);if(!double.IsFinite(offset)||offset<0)throw new ArgumentException("共面度排序偏移无效");
                var axes=sort.Split(',');if(axes.Any(a=>a.Length!=2||a[0]!='+'&&a[0]!='-'||!"XYZ".Contains(a[1])))throw new ArgumentException("未知共面度排序方式");
                double Key(List<int> a,string axis){var m=MetrologyMath.Mean(a.Select(i=>local[i]).ToArray());return(axis[1]=='X'?m.X:axis[1]=='Y'?m.Y:m.Z)*(axis[0]=='-'?-1:1);}
                terminals=terminals.OrderBy(g=>Key(g,axes[0])).ToList();if(axes.Length==2){var rows=new List<List<List<int>>>();foreach(var t in terminals){if(rows.Count==0||Math.Abs(Key(rows[^1][0],axes[0])-Key(t,axes[0]))>offset)rows.Add(new());rows[^1].Add(t);}terminals=rows.SelectMany(row=>row.OrderBy(g=>Key(g,axes[1]))).ToList();}
                int limit=(int)C3.N(g,"Max number of objects to output",1);if(limit<1)throw new ArgumentException("输出斑点数必须大于零");terminals=terminals.Take(limit).ToList();
            }
        }
        if(terminals.Count==0)throw new ArgumentException("共面度没有满足条件的端子");
        var tops=terminals.Select(g=>g.MaxBy(i=>local[i].Z)).ToArray();var vertices=tops.Select(i=>local[i]).ToArray();var center=MetrologyMath.Mean(vertices);MeasureVector3 normal=new(0,0,1),planePoint=center;double value;
        int[] triangle=null;
        if(driver=="Coplanarity")
        {
            // The public manual defines population standard deviation of accepted heights.
            var z=terminals.SelectMany(g=>g).Select(i=>local[i].Z).ToArray();var mean=z.Average();value=Math.Sqrt(z.Sum(v=>(v-mean)*(v-mean))/z.Length);
        }
        else if(driver=="RegressPlaneCoplanarity")
        {
            if(vertices.Length<4)throw new ArgumentException("回归共面度至少需要四个非空区域");(planePoint,normal)=MetrologyMath.FitPlane(vertices);var d=vertices.Select(v=>(v-planePoint).Dot(normal)).ToArray();value=d.Max()-d.Min();
        }
        else
        {
            if(vertices.Length<3)throw new ArgumentException("座面法至少需要三个端子");
            // Enumerate support triangles whose projection contains the center of gravity.
            // An upper supporting plane must leave every terminal on or below it.
            var gravity=MetrologyMath.Mean(selected.Select(i=>local[i]).ToArray());double bestHeight=double.PositiveInfinity;value=double.NaN;
            if(vertices.Length>300)throw new NotSupportedException("座面法精确支撑三角形搜索最多支持 300 个端子，请分组测量");
            for(int a=0;a<vertices.Length-2;a++)for(int b=a+1;b<vertices.Length-1;b++)for(int c=b+1;c<vertices.Length;c++)
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();var u=vertices[b]-vertices[a];var v=vertices[c]-vertices[a];var n=u.Cross(v);if(n.Length<1e-10||Math.Abs(n.Z)<1e-10)continue;n=n.Unit();if(n.Z<0)n=n*-1;
                double den=u.X*v.Y-u.Y*v.X;if(Math.Abs(den)<1e-12)continue;var g=gravity-vertices[a];double s=(g.X*v.Y-g.Y*v.X)/den,t=(u.X*g.Y-u.Y*g.X)/den;if(s< -1e-9||t< -1e-9||s+t>1+1e-9)continue;
                var d=vertices.Select(q=>(q-vertices[a]).Dot(n)).ToArray();if(d.Max()>1e-7)continue;
                double h=vertices[a].Z-(n.X*g.X+n.Y*g.Y)/n.Z;if(h>=bestHeight)continue;bestHeight=h;planePoint=vertices[a];normal=n;value=-d.Min();triangle=new[]{a,b,c};
            }
            if(!double.IsFinite(value))throw new ArgumentException("未找到包含重心投影的有效支撑座面");
        }
        DetectionOverlay2D.Begin(ctx,Driver);
        for(int i=0;i<tops.Length;i++){var world=MetrologyMath.At(cloud,tops[i]);if(p.Bool("Top Points"))MetrologyMath.Point(ctx,p,Driver,world);if(p.Bool("Index Text"))MetrologyMath.Text(ctx,p,Driver,world,(i+1).ToString());}
        var worldCenter=MetrologyMath.Mean(selected.Select(i=>MetrologyMath.At(cloud,i)).ToArray());
        // Geometry fitting is carried out in reference coordinates; recompute display plane in world coordinates.
        var r=p.Group("Reference");var worldPoint=MetrologyMath.World(r,planePoint);var worldNormal=(MetrologyMath.World(r,planePoint+normal)-worldPoint).Unit();
        if(p.Bool("Seating Plane")||p.Bool("Least Mean Square Plane"))MetrologyMath.PlaneOverlay(ctx,p,Driver,worldPoint,worldNormal);
        if(p.Bool("Lowest Ball Reference Plane")){var index=vertices.Select((v,i)=>(D:(v-planePoint).Dot(normal),I:i)).MinBy(q=>q.D).I;MetrologyMath.PlaneOverlay(ctx,p,Driver,MetrologyMath.At(cloud,tops[index]),worldNormal);}
        if(p.Bool("Center"))MetrologyMath.Point(ctx,p,Driver,worldCenter);if(p.Bool("Copanlarity Division"))MetrologyMath.Text(ctx,p,Driver,worldCenter,$"{value:0.000}");
        if(p.Bool("Terminal Triangle")&&triangle!=null)for(int j=0;j<3;j++)DetectionOverlay2D.Add(ctx,p,Driver,"segment3d",new(){["Start"]=MetrologyMath.At(cloud,tops[triangle[j]]).Native(),["End"]=MetrologyMath.At(cloud,tops[triangle[(j+1)%3]]).Native()},view:p.Int("IM In"));
        ctx.Vars.Set(driver=="Coplanarity"?"coplanarity_value":"coplanarity_deviation",value);ctx.Vars.Set("terminal_count",terminals.Count);ctx.Vars.Set("top_points",tops.Select(i=>(object)MetrologyMath.At(cloud,i).Native()).ToList());ctx.Vars.Set("plane",MetrologyMath.Plane(worldPoint,worldNormal));
        if(p.Bool("Pointset Cut"))cloud.DisplayIndices=selected.ToArray();MetrologyMath.Warning(ctx,"自有共面度实现：按手册计算，聚类及支撑面数值尚待原版标定");return true;
    }
}

public sealed class GeometricToleranceOp(string driver):IOperator
{
    public string Driver=>driver;
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;CloudSetOp.Require(ctx,p.Int("IM In"));var actual=MetrologyMath.Geometry(p.Raw("Actral Plane"),"plane");bool line=driver.EndsWith("PlaneLine"),perpendicular=driver.Contains("Perpendicularity");
        var reference=MetrologyMath.Geometry(p.Raw(line?"Reference Line":"Reference Plane"),line?"line3d":"plane");var points=MetrologyMath.ReadPoints(ctx,p.Str("Points File"));
        MeasureVector3 normal;
        if(line)normal=perpendicular?reference.N:reference.N.Cross(MeasureVector3.Read(new Dictionary<string,object>{{"v",p.Group("Vector")}},"v")).Unit();
        else normal=perpendicular?(actual.N-reference.N*actual.N.Dot(reference.N)).Unit():reference.N;
        var projected=points.Select(q=>q-actual.N*(q-actual.P).Dot(actual.N)).ToArray();var distances=projected.Select(q=>(q-reference.P).Dot(normal)).ToArray();var result=distances.Max()-distances.Min();
        MetrologyMath.Threshold(ctx,p,result);DetectionOverlay2D.Begin(ctx,driver);
        if(p.Bool("Display Actral Plane"))MetrologyMath.PlaneOverlay(ctx,p,driver,actual.P,actual.N);
        if(p.Bool(line?"Display Reference Line":"Display Reference Plane"))DetectionOverlay2D.Add(ctx,p,driver,line?"line3d":"plane",new(){["Position"]=reference.P.Native(),[line?"Direction":"Normal"]=reference.N.Native()},view:p.Int("IM In"));
        if(p.Bool("Display Tolerance Band Plane"))MetrologyMath.PlaneOverlay(ctx,p,driver,reference.P,normal);
        for(int i=0;i<points.Count;i++){if(p.Bool("Intersections"))MetrologyMath.Point(ctx,p,driver,projected[i]);if(p.Bool("Line"))DetectionOverlay2D.Add(ctx,p,driver,"segment3d",new(){["Start"]=projected[i].Native(),["End"]=(projected[i]-normal*distances[i]).Native()},view:p.Int("IM In"));}
        if(p.Bool(perpendicular?"Perpendicularity":"Parallelism"))MetrologyMath.Text(ctx,p,driver,actual.P,$"{result:0.000}");
        ctx.Vars.Set("distance",distances.Cast<object>().ToList());ctx.Vars.Set(perpendicular?"perpendicularity":"parallelism",result);if(perpendicular&&!line)ctx.Vars.Set("parallelism",result);return true;
    }
}

public sealed class PlaneProfileMeasurementOp:IOperator
{
    public string Driver=>"3D ProfileofSurfacePlane";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var cloud=CloudSetOp.Require(ctx,p.Int("IM In"));var actual=MetrologyMath.Geometry(p.Raw("Actral Plane"),"plane");var theoretical=MetrologyMath.ReadPoints(ctx,p.Str("Theoretical Plane File"));
        if(p.Str("Fitting Method","Least Squares")!="Least Squares")throw new NotSupportedException("面轮廓度理论平面暂支持最小二乘法；RANSAC 尚待实现");
        var fitted=MetrologyMath.FitPlane(theoretical);
        if(p.Bool("Fit Cloud"))
        {
            var selected=CloudRegionSelector.Select(cloud,p);var target=MetrologyMath.FitPlane(selected.Select(i=>MetrologyMath.At(cloud,i)).ToArray());
            // Least squares normal translation only, preserving nominal dimensions and orientation.
            double shift=theoretical.Average(q=>(target.P-q).Dot(target.N));theoretical=theoretical.Select(q=>q+target.N*shift).ToList();fitted=MetrologyMath.FitPlane(theoretical);
            MetrologyMath.Warning(ctx,"自有贴合采用沿目标法线的最小二乘平移，保留理论方向；与原版迭代贴合仍需校准");
        }
        var distances=theoretical.Select(q=>Math.Abs((q-actual.P).Dot(actual.N))).ToArray();double value=2*distances.Max();MetrologyMath.Threshold(ctx,p,value);
        DetectionOverlay2D.Begin(ctx,Driver);if(p.Bool("Actral Fitting Plane"))MetrologyMath.PlaneOverlay(ctx,p,Driver,actual.P,actual.N);if(p.Bool("Theoretical Plane"))MetrologyMath.PlaneOverlay(ctx,p,Driver,fitted.P,fitted.N);
        foreach(var q in theoretical){var foot=q-actual.N*(q-actual.P).Dot(actual.N);if(p.Bool("Intersections")){MetrologyMath.Point(ctx,p,Driver,q);MetrologyMath.Point(ctx,p,Driver,foot);}if(p.Bool("Line"))DetectionOverlay2D.Add(ctx,p,Driver,"segment3d",new(){["Start"]=q.Native(),["End"]=foot.Native()},view:p.Int("IM In"));}
        if(p.Bool("Profile"))MetrologyMath.Text(ctx,p,Driver,actual.P,$"{value:0.000}");ctx.Vars.Set("theoretical_plane",MetrologyMath.Plane(fitted.P,fitted.N));ctx.Vars.Set("profile_of_surface",value);return true;
    }
}
