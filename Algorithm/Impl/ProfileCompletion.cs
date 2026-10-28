using System.Collections;
using System.Globalization;
using OpenCvSharp;
using AiVisionStudio.Services;

namespace AiVisionStudio.Algorithm.Impl;

public static class ProfileCompletionRegistration
{
    public static void Register()
    {
        OperatorRegistry.Register(new OwnedProfileCaptureOp());
        OperatorRegistry.Register(new OwnedProfileCaptureOp(true));
        foreach(var kind in new[]{"Line","Circle","Area","Distance","Intersection","InflectionPoints","CrossSectionStep"})
            OperatorRegistry.Register(new ProfileMeasurementOp(kind));
        OperatorRegistry.Register(new ProfileDisplayDataOp());
        OperatorRegistry.Register(new ProfileCreateRoiOp());
        OperatorRegistry.Register(new ProfileSealantOp());
        OperatorRegistry.Register(new CloudBoundaryOp());
        OperatorRegistry.Register(new CloudInflectionOp());
        OperatorRegistry.Register(new CloudUnrollOp());
        OperatorRegistry.Register(new SegmentedSealant3DOp());
    }
}

internal static class ProfileMath
{
    public static double N(IReadOnlyDictionary<string,object> d,string k,double fallback=double.NaN)
    { var v=C3.N(d,k,fallback); if(!double.IsFinite(v))throw new ArgumentException("轮廓参数不是有限数值："+k);return v; }
    public static string S(IReadOnlyDictionary<string,object> d,string k,string fallback="")=>d?.GetValueOrDefault(k)?.ToString()??fallback;
    public static bool B(IReadOnlyDictionary<string,object> d,string k,bool fallback=false)=>d?.GetValueOrDefault(k) is {} v?ControlValues.Bool(v):fallback;
    public static Point2d Point(IReadOnlyDictionary<string,object> d)=>new(N(d,"X"),N(d,"Z"));
    public static Dictionary<string,object> Native(Point2d p)=>new(){["X"]=p.X,["Z"]=p.Y};
    public static Dictionary<string,object> Line(Point2d p,Point2d d)=>new(){["Position"]=Native(p),["Direction"]=Native(d)};
    public static double Cross(Point2d a,Point2d b)=>a.X*b.Y-a.Y*b.X;
    public static double Dot(Point2d a,Point2d b)=>a.X*b.X+a.Y*b.Y;
    public static List<Point2d> Input(OpContext c,int im)
    {
        var profile=c.Profiles.GetValueOrDefault(im)??throw new ArgumentException($"IM{im} 不存在轮廓，请先提取或加载轮廓");
        var points=Enumerable.Range(0,profile.Count).Where(i=>float.IsFinite(profile.X[i])&&float.IsFinite(profile.Z[i])).Select(i=>new Point2d(profile.X[i],profile.Z[i])).ToList();
        if(points.Count<2)throw new ArgumentException("轮廓有效点不足两个");return points;
    }
    public static void Store(OpContext c,int im,List<Point2d> points)
    {
        if(points.Count<2||points.Any(q=>!double.IsFinite(q.X+q.Y)||Math.Abs(q.X)>float.MaxValue||Math.Abs(q.Y)>float.MaxValue))throw new ArgumentException("提取到的轮廓为空或坐标溢出");
        c.Images.Remove(im);c.Clouds.Remove(im);c.Profiles[im]=new(points.Select(q=>(float)q.X).ToArray(),points.Select(q=>(float)q.Y).ToArray());
        c.Vars.Set("Profile.Points",points.Select(q=>(object)Native(q)).ToList());c.Vars.Set("Profile.Count",points.Count);
        c.Vars.Set("Profile.Min",points.Min(q=>q.Y));c.Vars.Set("Profile.Max",points.Max(q=>q.Y));c.Vars.Set("Profile.Range",points.Max(q=>q.Y)-points.Min(q=>q.Y));
    }
    public static IReadOnlyDictionary<string,object> Unwrap(object value,string type)
    {
        if(type=="plane"&&value is PlaneObj plane)return new Dictionary<string,object>{["Position"]=plane.Position,["Normal"]=plane.Normal};
        if(value is not IReadOnlyDictionary<string,object> d)throw new ArgumentException("缺少轮廓几何："+type);
        if(d.TryGetValue("Type",out var t)&&t?.ToString()!=type)throw new ArgumentException("轮廓几何类型不匹配："+t);
        return C3.Sub(d,type)??d;
    }
    public static List<IReadOnlyDictionary<string,object>> Regions(object value)
    {
        if(value is IEnumerable a && value is not string && value is not IReadOnlyDictionary<string,object>)return a.Cast<object>().Select(v=>Unwrap(v,"window1d")).ToList();
        if(value is IReadOnlyDictionary<string,object> map&&S(map,"Type")=="window1d array")return Regions(map.GetValueOrDefault("window1d array"));
        return [Unwrap(value,"window1d")];
    }
    public static List<Point2d> Crop(List<Point2d> points,IReadOnlyDictionary<string,object> region)
    {
        var a=Point(C3.Sub(region,"Start"));var b=Point(C3.Sub(region,"End"));
        if(a.X==b.X||a.Y==b.Y)throw new ArgumentException("轮廓窗口宽度和高度必须非零");
        var selected=points.Where(q=>q.X>=Math.Min(a.X,b.X)&&q.X<=Math.Max(a.X,b.X)&&q.Y>=Math.Min(a.Y,b.Y)&&q.Y<=Math.Max(a.Y,b.Y)).ToList();
        if(selected.Count==0)throw new ArgumentException("轮廓窗口内没有有效点");return selected;
    }
    public static List<List<Point2d>> Groups(List<Point2d> points,object region)=>Regions(region).Select(r=>Crop(points,r)).ToList();
    public static List<List<Point2d>> Groups(List<Point2d> points,ParamBag p)=>p.Bool("Use Region")?Groups(points,p.Raw("Region")):[points];
    public static (Point2d p,Point2d d) FitLine(List<Point2d> points)
    {
        if(points.Count<2||points.All(q=>Geo.Dist(q,points[0])<1e-12))throw new ArgumentException("拟合直线至少需要两个不同的点");
        var f=LineFitOp.FitLine(points);return(new(f.x,f.y),new(f.vx,f.vy));
    }
    public static (Point2d center,double radius) FitCircle(List<Point2d> points)
    {
        if(points.Count<3)throw new ArgumentException("拟合圆至少需要三个点");
        var origin=new Point2d(points.Average(q=>q.X),points.Average(q=>q.Y));
        var f=CircleFitOp.Fit(points.Select(q=>q-origin).ToList());
        if(!double.IsFinite(f.cx+f.cy+f.r)||f.r<=0)throw new ArgumentException("圆拟合退化：输入点共线或重复");
        return(new(f.cx+origin.X,f.cy+origin.Y),f.r);
    }
    public static List<Point2d> Inliers(OpContext c,List<Point2d> points,ParamBag p,bool circle)
    {
        var method=p.Str("Fitting Method","RANSAC");if(method=="Least Squares")return points;
        if(method!="RANSAC")throw new ArgumentException("未知轮廓拟合模式："+method);
        var threshold=p.GroupDbl("RANSAC Parameters","Distance Threshold",.02);var iterations=p.GroupDbl("RANSAC Parameters","Max Iterations",500);
        if(!double.IsFinite(threshold)||threshold<=0||iterations<1||iterations>10000||iterations!=Math.Truncate(iterations))throw new ArgumentException("RANSAC 距离阈值或迭代次数无效");
        int required=circle?3:2;if(points.Count<required)throw new ArgumentException("拟合轮廓点数不足");
        var random=new Random(381);List<Point2d> best=[];double bestError=double.PositiveInfinity;
        for(int k=0;k<(int)iterations;k++)
        {
            if((k&15)==0)c.CancellationToken.ThrowIfCancellationRequested();
            int a=random.Next(points.Count),b=random.Next(points.Count),d=random.Next(points.Count);if(a==b||circle&&(a==d||b==d))continue;
            Func<Point2d,double> residual;
            if(circle){try{var f=FitCircle([points[a],points[b],points[d]]);residual=q=>Math.Abs(Geo.Dist(q,f.center)-f.radius);}catch(ArgumentException){continue;}}
            else{var vector=points[b]-points[a];var length=Geo.Dist(points[a],points[b]);if(length<1e-12)continue;residual=q=>Math.Abs(Cross(q-points[a],vector))/length;}
            var found=new List<Point2d>();double error=0;foreach(var q in points){var r=residual(q);if(r<=threshold){found.Add(q);error+=r*r;}}
            if(found.Count>best.Count||found.Count==best.Count&&error<bestError){best=found;bestError=error;}if(best.Count==points.Count)break;
        }
        if(best.Count<required)throw new ArgumentException("RANSAC 未找到有效模型");return best;
    }
    public static Point2d Inflection(List<Point2d> points,string direction,double threshold)
    {
        var ordered=points.OrderBy(q=>q.X).ToList();if(ordered.Count<3)throw new ArgumentException("拐点至少需要三个有效点");
        if(threshold<0||!double.IsFinite(threshold))throw new ArgumentException("干扰去除阈值无效");
        // Local changes of tangent direction. Collinear samples never become fabricated corners.
        var candidates=new List<Point2d>();
        for(int i=1;i<ordered.Count-1;i++)
        {
            var a=ordered[i]-ordered[i-1];var b=ordered[i+1]-ordered[i];var lengths=Math.Sqrt(Dot(a,a)*Dot(b,b));
            if(lengths<1e-18)continue;
            if(Math.Abs(Cross(a,b))/lengths>1e-5&&Math.Max(Math.Abs(a.Y),Math.Abs(b.Y))>=threshold)candidates.Add(ordered[i]);
        }
        if(candidates.Count==0)throw new ArgumentException("未找到满足阈值的轮廓拐点");
        return direction switch{"+X"=>candidates.MaxBy(q=>q.X),"-X"=>candidates.MinBy(q=>q.X),"+Z"=>candidates.MaxBy(q=>q.Y),"-Z"=>candidates.MinBy(q=>q.Y),_=>throw new ArgumentException("无效拐点方向")};
    }
    public static Point2d Feature(List<Point2d> points,IReadOnlyDictionary<string,object> settings)
    {
        var feature=S(settings,"Feature","Average");var a=points.OrderBy(q=>q.X).ToList();
        switch(feature)
        {
            case "Min X":return a[0];case "Max X":return a[^1];case "Min Z":return a.MinBy(q=>q.Y);case "Max Z":return a.MaxBy(q=>q.Y);
            case "Average":return new(a.Average(q=>q.X),a.Average(q=>q.Y));
            case "Median":return (a[(a.Count-1)/2]+a[a.Count/2])*.5;
            case "Inflection Point":var noise=C3.Sub(settings,"Noise Removal");return Inflection(a,S(settings,"Inflection Point Type","+Z"),B(noise,"Enable")?N(noise,"Threshold",0):0);
            case "Edge":
                var direction=S(settings,"Edge Direction","Rising or Falling");if(direction is not("Rising" or "Falling" or "Rising or Falling"))throw new ArgumentException("无效边缘方向");
                var edges=Enumerable.Range(1,a.Count-1).Where(i=>direction=="Rising"?a[i].Y>a[i-1].Y:direction=="Falling"?a[i].Y<a[i-1].Y:a[i].Y!=a[i-1].Y).ToList();
                if(edges.Count==0)throw new ArgumentException("未找到轮廓边缘");
                var index=S(settings,"Edge Type","Best") switch{"First"=>edges[0],"Last"=>edges[^1],"Best"=>edges.MaxBy(i=>Math.Abs(a[i].Y-a[i-1].Y)),_=>throw new ArgumentException("无效边缘选择")};
                return (a[index]+a[index-1])*.5;
            default:throw new ArgumentException("未知轮廓特征："+feature);
        }
    }
    public static void Overlay(OpContext c,ParamBag p,string driver,string toggle,string type,Dictionary<string,object> values)
    {if(p.Bool(toggle)){values["_size"]=12;DetectionOverlay2D.Add(c,p,driver,type,values,view:p.Int("IM In"));}}
    public static void Text(OpContext c,ParamBag p,string driver,string toggle,Point2d location,string text)
        =>Overlay(c,p,driver,toggle,"text1d",new(){["Position"]=Native(location),["Text"]=text});
    public static void Output(OpContext c,string name,List<object> values,bool array)=>c.Vars.Set(name,array?values:values.Single());
    public static void Warn(OpContext c,string message){c.Vars.Set("Warning",true);c.Vars.Set("Message",message);}
}

/// <summary>Profiles retain metric X/Z samples; output slots are never raster screenshots.</summary>
public sealed class OwnedProfileCaptureOp(bool arbitrary=false):IOperator
{
    public string Driver=>arbitrary?"3D ProfileArbitraryCapture":"3D ProfileCapture";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var cloud=ctx.Clouds.Get(p.Int("IM In"));if(cloud==null||cloud.Count==0)throw new ArgumentException("输入点云为空");
        var selected=new List<int>();List<Point2d> points;
        if(arbitrary)
        {
            var plane=ProfileMath.Unwrap(p.Raw("Cut Plane"),"plane");var normal=MeasureVector3.Read(plane,"Normal").Unit();var center=MeasureVector3.Read(plane,"Position");
            var originMap=ProfileMath.Unwrap(p.Raw("Origin"),"point3d");var origin=new MeasureVector3(ProfileMath.N(originMap,"X"),ProfileMath.N(originMap,"Y"),ProfileMath.N(originMap,"Z"));
            var axis=MeasureVector3.Read(ProfileMath.Unwrap(p.Raw("X Axis of Profile"),"line3d"),"Direction");axis=(axis-normal*axis.Dot(normal)).Unit();
            int angle=p.Int("Angle with X Axis",90);if(angle is not(90 or -90))throw new ArgumentException("轮廓Z轴角度必须为90或-90");
            var zAxis=normal.Cross(axis)*(angle/90.0);var width=p.Dbl("Extract Width",.03);if(!double.IsFinite(width)||width<=0)throw new ArgumentException("提取宽度必须大于0");
            points=[];for(int i=0;i<cloud.Count;i++){if((i&4095)==0)ctx.CancellationToken.ThrowIfCancellationRequested();var q=new MeasureVector3(cloud.X[i],cloud.Y[i],cloud.Z[i]);if(q.Finite&&Math.Abs((q-center).Dot(normal))<=width){selected.Add(i);points.Add(new((q-origin).Dot(axis),(q-origin).Dot(zAxis)));}}
            points=points.OrderBy(q=>q.X).ToList();
        }
        else points=Capture(ctx,cloud,p.Str("Cut Plane","XZ-Plane"),p.Dbl("Profile Cut Position"),p.Str("Extract Pattern","Fusion(average)"),p.Dbl("Extract Width",.03),p.Dbl("Resolution",.1),p.Str("Mode","From Row"),p.Int("Number"),selected);
        ProfileMath.Store(ctx,p.Int("IM Out",1),points);DetectionOverlay2D.Begin(ctx,Driver);
        if(p.Bool("Display Extract Points"))
        {
            DetectionOverlay2D.Add(ctx,p,Driver,"pointset",new(){["Indices"]=selected.ToArray(),["IM"]=p.Int("IM In")},view:p.Int("IM In"));
            var display=new PointCloud();foreach(var index in selected)display.Add(cloud.X[index],cloud.Y[index],cloud.Z[index]);ctx.NativeOverlays[^1].Cloud=display;
        }
        ctx.Log($"轮廓提取：{points.Count} 点 -> IM{p.Int("IM Out",1)}",false);return true;
    }
    internal static List<Point2d> Capture(OpContext ctx,PointCloud c,string plane,double pos,string mode,double width,double resolution,string rowMode,int number,List<int> selected)
    {
        if(plane is not("XZ-Plane" or "YZ-Plane" or "XY-Plane"))throw new ArgumentException("无效切割平面");
        if(!double.IsFinite(pos))throw new ArgumentException("提取位置无效");
        if(mode is "Nearest" or "Row and Column")
        {
            var grid=c.Raster??InferGrid(c);bool row=mode=="Row and Column"?rowMode switch{"From Row"=>true,"From Column"=>false,_=>throw new ArgumentException("无效行列模式")}:plane switch{"XZ-Plane"=>true,"YZ-Plane"=>false,_=>throw new ArgumentException("最近提取仅支持XZ/YZ平面")};
            int max=row?grid.Height:grid.Width;int index=number;
            if(mode=="Nearest")
            {
                var candidates=Enumerable.Range(0,max).Select(k=>(Index:k,Points:Enumerable.Range(0,row?grid.Width:grid.Height).Select(j=>grid.PointIndices[row?k*grid.Width+j:j*grid.Width+k]).Where(i=>i>=0&&i<c.Count).ToArray())).Where(g=>g.Points.Length>0).ToList();
                if(candidates.Count==0)throw new ArgumentException("有序点云没有有效行列");
                index=candidates.MinBy(g=>Math.Abs(g.Points.Average(i=>row?c.Y[i]:c.X[i])-pos)).Index;
            }
            if(index<0||index>=max)throw new ArgumentException("轮廓行列编号超出点云范围");
            for(int j=0;j<(row?grid.Width:grid.Height);j++){int i=grid.PointIndices[row?index*grid.Width+j:j*grid.Width+index];if(i>=0&&i<c.Count&&float.IsFinite(c.X[i]+c.Y[i]+c.Z[i]))selected.Add(i);}
            return selected.Select(i=>new Point2d(row?c.X[i]:c.Y[i],c.Z[i])).OrderBy(q=>q.X).ToList();
        }
        if(mode is not("Range" or "Fusion(average)"))throw new ArgumentException("无效轮廓提取模式："+mode);
        if(!double.IsFinite(width)||width<=0||mode=="Fusion(average)"&&(!double.IsFinite(resolution)||resolution<=0))throw new ArgumentException("提取宽度和分辨率必须为正数");
        var list=new List<Point2d>();for(int i=0;i<c.Count;i++){if((i&4095)==0)ctx.CancellationToken.ThrowIfCancellationRequested();var a=c.At(i);if(!float.IsFinite(a.x+a.y+a.z))continue;double distance=plane=="XZ-Plane"?a.y:plane=="YZ-Plane"?a.x:a.z;if(Math.Abs(distance-pos)>width)continue;selected.Add(i);list.Add(plane=="XZ-Plane"?new(a.x,a.z):plane=="YZ-Plane"?new(a.y,a.z):new(a.x,a.y));}
        list=list.OrderBy(q=>q.X).ToList();if(mode=="Range"||list.Count==0)return list;
        double start=list[0].X;return list.GroupBy(q=>Math.Floor((q.X-start)/resolution+1e-6)).Select(g=>new Point2d(g.Average(q=>q.X),g.Average(q=>q.Y))).ToList();
    }
    internal static CloudRaster InferGrid(PointCloud c)
    {
        // Recover only an actual Cartesian lattice; never resample arbitrary clouds for Nearest mode.
        var xs=c.X.Where(float.IsFinite).Distinct().Order().ToArray();var ys=c.Y.Where(float.IsFinite).Distinct().Order().ToArray();
        if(xs.Length==0||ys.Length==0||(long)xs.Length*ys.Length>Math.Max(c.Count*8L,1024)||xs.Length*(long)ys.Length>16_000_000)throw new ArgumentException("最近/行列提取需要有序点云，请先运行点云有序化");
        var xi=xs.Select((x,i)=>(x,i)).ToDictionary(v=>v.x,v=>v.i);var yi=ys.Select((y,i)=>(y,i)).ToDictionary(v=>v.y,v=>v.i);var indices=Enumerable.Repeat(-1,xs.Length*ys.Length).ToArray();
        for(int i=0;i<c.Count;i++){if(!float.IsFinite(c.X[i]+c.Y[i]+c.Z[i]))continue;int cell=yi[c.Y[i]]*xs.Length+xi[c.X[i]];if(indices[cell]>=0)throw new ArgumentException("点云含重复XY坐标，不能按有序轮廓提取");indices[cell]=i;}
        return new(xs.Length,ys.Length,xs[0],ys[0],xs.Length>1?(xs[^1]-xs[0])/(xs.Length-1):1,ys.Length>1?(ys[^1]-ys[0])/(ys.Length-1):1,indices);
    }
}

public sealed class ProfileMeasurementOp(string kind):IOperator
{
    public string Driver=>"3D Profile"+kind;
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var input=ProfileMath.Input(ctx,p.Int("IM In"));DetectionOverlay2D.Begin(ctx,Driver);
        void Draw(string toggle,string type,Dictionary<string,object> value)=>ProfileMath.Overlay(ctx,p,Driver,toggle,type,value);
        void Text(string toggle,Point2d at,string text)=>ProfileMath.Text(ctx,p,Driver,toggle,at,text);
        var results=new Dictionary<string,List<object>>();void Add(string key,object value){if(!results.TryGetValue(key,out var list))results[key]=list=[];list.Add(value);}
        if(kind is "Distance" or "CrossSectionStep" or "Intersection")
        {
            string first=kind=="Distance"?"Point1":kind=="Intersection"?"Line1":"Point",second=kind=="Distance"?"Point2":kind=="Intersection"?"Line2":p.Str("Mode","Point to Point")=="Point to Point"?"Reference Point":"Reference Line";
            var a=p.Group(first)??throw new ArgumentException("缺少参数："+first);var b=p.Group(second)??throw new ArgumentException("缺少参数："+second);
            var ga=ProfileMath.Groups(input,a.GetValueOrDefault("Region"));var gb=second=="Reference Line"&&ProfileMath.S(b,"Acquisition Method")=="Horizontal Reference Line"?new List<List<Point2d>>{input}:ProfileMath.Groups(input,b.GetValueOrDefault("Region"));
            if(ga.Count!=gb.Count&&ga.Count!=1&&gb.Count!=1)throw new ArgumentException("两个区域数组长度不匹配");int count=Math.Max(ga.Count,gb.Count);
            for(int i=0;i<count;i++)
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();var pa=ga[ga.Count==1?0:i];var pb=gb[gb.Count==1?0:i];
                if(kind=="Intersection")
                {
                    var l1=ProfileMath.FitLine(pa);var l2=ProfileMath.FitLine(pb);var cross=ProfileMath.Cross(l1.d,l2.d);if(Math.Abs(cross)<1e-10)throw new ArgumentException("两条轮廓直线平行或重合，无唯一交点");
                    var pt=l1.p+l1.d*(ProfileMath.Cross(l2.p-l1.p,l2.d)/cross);Add("intersection_point",ProfileMath.Native(pt));
                    Draw("Display Line1","line1d",ProfileMath.Line(l1.p,l1.d));Draw("Display Line2","line1d",ProfileMath.Line(l2.p,l2.d));Draw("Display Intersection Point","point1d",ProfileMath.Native(pt));
                    if(p.Bool("Caculate Angle")){double angle=Math.Acos(Math.Clamp(Math.Abs(ProfileMath.Dot(l1.d,l2.d)),0,1))*180/Math.PI;Add("angle",angle);Text("Display Angle",pt,$"{angle:0.###}°");}
                    continue;
                }
                var q=ProfileMath.Feature(pa,a);Point2d r;
                if(kind=="CrossSectionStep"&&second=="Reference Line")
                {
                    var line=ProfileMath.S(b,"Acquisition Method","Fit Line") switch{"Fit Line"=>ProfileMath.FitLine(pb),"Horizontal Reference Line"=>(new Point2d(0,ProfileMath.N(b,"Value",0)),new Point2d(1,0)),_=>throw new ArgumentException("未知参考线获取模式")};
                    r=line.Item1+line.Item2*ProfileMath.Dot(q-line.Item1,line.Item2);Draw("Display Reference Line","line1d",ProfileMath.Line(line.Item1,line.Item2));Draw("Display Root","point1d",ProfileMath.Native(r));
                }
                else r=ProfileMath.Feature(pb,b);
                Add(first=="Point1"?"point1":"point",ProfileMath.Native(q));Add(second=="Point2"?"point2":"reference_point",ProfileMath.Native(r));
                Draw("Display "+first,"point1d",ProfileMath.Native(q));Draw("Display "+second,"point1d",ProfileMath.Native(r));
                if(kind=="Distance")
                {
                    var values=new[]{Geo.Dist(q,r),Math.Abs(q.X-r.X),Math.Abs(q.Y-r.Y)};var names=new[]{"lines_distance","distance_in_x_direction","distance_in_z_direction"};var toggles=new[]{"Linear Distance","Distance in X Direction","Distance in Z Direction"};
                    for(int k=0;k<3;k++){Add(names[k],values[k]);Draw("Display Segment("+toggles[k]+")","segment1d",new(){["Start"]=ProfileMath.Native(q),["End"]=ProfileMath.Native(k==0?r:k==1?new(r.X,q.Y):new(q.X,r.Y))});Text("Display Text("+toggles[k]+")",(q+r)*.5,values[k].ToString("0.###",CultureInfo.InvariantCulture));}
                }
                else{var distance=second=="Reference Line"?Geo.Dist(q,r):q.Y-r.Y;Add("cross_section_step",distance);Draw("Display Segment","segment1d",new(){["Start"]=ProfileMath.Native(q),["End"]=ProfileMath.Native(r)});Text("Display Cross Section Step Text",q,distance.ToString("0.###",CultureInfo.InvariantCulture));}
            }
            foreach(var kv in results)ProfileMath.Output(ctx,kv.Key,kv.Value,count>1);
            if(new[]{ProfileMath.S(a,"Feature"),ProfileMath.S(b,"Feature")}.Any(f=>f is "Edge" or "Inflection Point"))
                ProfileMath.Warn(ctx,"轮廓特征使用相邻点边缘中点或局部切线拐点；原版亚点定位及干扰去除数值仍待校准");
            return true;
        }
        var groups=ProfileMath.Groups(input,p);foreach(var points in groups)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            switch(kind)
            {
                case "Line":
                    var fitted=ProfileMath.FitLine(ProfileMath.Inliers(ctx,points,p,false));var line=ProfileMath.Line(fitted.p,fitted.d);Add("line",line);Draw("Display Line","line1d",line);
                    if(p.Bool("Transform Profile")){if(groups.Count!=1)throw new ArgumentException("轮廓变换只能使用一个拟合区域");ProfileMath.Store(ctx,p.Int("IM Out"),input.Select(q=>new Point2d(ProfileMath.Dot(q-fitted.p,fitted.d),ProfileMath.Cross(fitted.d,q-fitted.p))).ToList());}break;
                case "Circle":
                    var circle=ProfileMath.FitCircle(ProfileMath.Inliers(ctx,points,p,true));var circleMap=new Dictionary<string,object>{["Center"]=ProfileMath.Native(circle.center),["Radius"]=circle.radius};Add("circle",circleMap);Add("radius",circle.radius);Draw("Display Circle","circle1d",circleMap);Draw("Display Center","point1d",ProfileMath.Native(circle.center));Text("Display Radius Text",circle.center,circle.radius.ToString("0.###",CultureInfo.InvariantCulture));break;
                case "InflectionPoints":
                    var noise=p.Group("Noise Removal");var corner=ProfileMath.Inflection(points,p.Str("Search Type","+X"),ProfileMath.B(noise,"Enable")?ProfileMath.N(noise,"Threshold",0):0);Add("inflection_point",ProfileMath.Native(corner));Draw("Display Inflection Point","point1d",ProfileMath.Native(corner));break;
                case "Area":
                    double area=0;var mode=p.Str("Measurement Mode","Profile to Line");var polygon=points;
                    if(mode=="Closed Profile"){for(int i=0;i<points.Count;i++)area+=ProfileMath.Cross(points[i],points[(i+1)%points.Count]);area=Math.Abs(area)*.5;}
                    else if(mode=="Profile to Line")
                    {
                        double baseline=p.Dbl("Reference Height");if(!double.IsFinite(baseline))throw new ArgumentException("面积基准高度无效");bool absolute=p.Bool("Caculate the area under the reference line as a positive area");var gap=p.Group("Skip gaps in area caculation");double maximum=ProfileMath.B(gap,"Enable")?ProfileMath.N(gap,"Maximum distance between two valid points",.01):double.MaxValue;if(maximum<=0)throw new ArgumentException("缺口宽度阈值必须为正");
                        var sorted=points.OrderBy(q=>q.X).ToList();for(int i=1;i<sorted.Count;i++){var a=sorted[i-1];var b=sorted[i];double dx=b.X-a.X;if(Geo.Dist(a,b)>maximum)continue;double h1=baseline-a.Y,h2=baseline-b.Y;if(h1*h2<0){var t=Math.Abs(h1)/(Math.Abs(h1)+Math.Abs(h2));area+=.5*dx*(t*(absolute?Math.Abs(h1):Math.Max(0,h1))+(1-t)*(absolute?Math.Abs(h2):Math.Max(0,h2)));}else area+=.5*dx*(absolute?Math.Abs(h1)+Math.Abs(h2):Math.Max(0,h1)+Math.Max(0,h2));}
                        polygon=[new(sorted[0].X,baseline),..sorted,new(sorted[^1].X,baseline)];
                    }
                    else throw new ArgumentException("未知轮廓面积模式");
                    Add("area",area);Draw("Display Area Polygons","polygon1d",new(){["Points"]=polygon.Select(q=>(object)ProfileMath.Native(q)).ToList(),["_closed"]=true,["_fill"]=true});Text("Display Area Text",points[0],area.ToString("0.###",CultureInfo.InvariantCulture));break;
                default:throw new NotSupportedException(kind);
            }
        }
        foreach(var kv in results)ProfileMath.Output(ctx,kv.Key,kv.Value,kind is "Line" or "Circle"||groups.Count>1);
        if(kind=="InflectionPoints")ProfileMath.Warn(ctx,"自有轮廓拐点采用局部切线变化和方向极值，干扰阈值依据相邻Z变化；尚未完成原版噪声处理数值校准");
        return true;
    }
}

public sealed class ProfileDisplayDataOp:IOperator
{
    public string Driver=>"Profile Display Data";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;ProfileMath.Input(ctx,p.Int("IM In"));DetectionOverlay2D.Begin(ctx,Driver);
        foreach(var key in p.StringList("_input_list"))
        {
            var row=p.Group(key)??throw new ArgumentException("缺少显示行："+key);if(!ProfileMath.B(row,"Display",true))continue;
            var value=p.Input(key);int decimals=(int)ProfileMath.N(row,"Decimals",3);if(decimals is <0 or >6)throw new ArgumentException("小数位数超出0..6");
            string content=value is double or float or decimal?Convert.ToDouble(value).ToString("F"+decimals,CultureInfo.InvariantCulture):Convert.ToString(value,CultureInfo.InvariantCulture)??"";
            var text=ProfileMath.S(row,"Prefix")+content+ProfileMath.S(row,"Suffix");
            var values=new Dictionary<string,object>{["Position"]=ProfileMath.Native(new(ProfileMath.N(row,"X",0),ProfileMath.N(row,"Z",0))),["Text"]=text,["_size"]=Math.Clamp(ProfileMath.N(row,"Font Size",12),1,72),["_color"]=ProfileMath.S(row,ProfileMath.B(row,"Related Situation",true)?"OK Color":"NG Color","#18A058")};
            DetectionOverlay2D.Add(ctx,p,Driver,"text1d",values,view:p.Int("IM In"));
        }
        return true;
    }
}

public sealed class ProfileCreateRoiOp:IOperator
{
    public string Driver=>"ProfileCreateROI";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var regions=ProfileMath.Regions(p.Raw("Region"));if(regions.Count is <1 or >100)throw new ArgumentException("轮廓ROI数量须为1..100");
        foreach(var r in regions){var a=ProfileMath.Point(C3.Sub(r,"Start"));var b=ProfileMath.Point(C3.Sub(r,"End"));if(a.X==b.X||a.Y==b.Y)throw new ArgumentException("轮廓ROI尺寸不能为零");}
        DetectionOverlay2D.Begin(ctx,Driver);int index=0;foreach(var r in regions){ProfileMath.Overlay(ctx,p,Driver,"Display ROI","window1d",new(r));ProfileMath.Text(ctx,p,Driver,"Display Index",ProfileMath.Point(C3.Sub(r,"Start")),(index++).ToString());}
        ctx.Vars.Set("ROI",regions.Cast<object>().ToList());return true;
    }
}
