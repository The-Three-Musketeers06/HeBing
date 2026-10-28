using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

internal static class NominalFit
{
    internal static (MeasureVector3 P,MeasureVector3 D) Line(List<MeasureVector3> points)
    {if(points.Count<2)throw new ArgumentException("直线至少需要两个点");var e=MetrologyMath.Principal(points);if(e.Values[0]<1e-14)throw new ArgumentException("直线点重合");return(e.Center,e.Axes[0]);}
    internal static (MeasureVector3 Center,double Radius,MeasureVector3 Normal) Circle(List<MeasureVector3> points)
    {
        var plane=MetrologyMath.FitPlane(points);var u=plane.N.Cross(Math.Abs(plane.N.Z)<.9?new(0,0,1):new MeasureVector3(0,1,0)).Unit();var v=plane.N.Cross(u).Unit();
        using var a=new Mat(points.Count,3,MatType.CV_64F);using var b=new Mat(points.Count,1,MatType.CV_64F);using var solution=new Mat();
        for(int i=0;i<points.Count;i++){var q=points[i]-plane.P;double x=q.Dot(u),y=q.Dot(v);a.Set(i,0,2*x);a.Set(i,1,2*y);a.Set(i,2,1d);b.Set(i,0,x*x+y*y);}
        if(!Cv2.Solve(a,b,solution,DecompTypes.SVD))throw new ArgumentException("理论圆拟合失败");double x0=solution.At<double>(0),y0=solution.At<double>(1),r=Math.Sqrt(solution.At<double>(2)+x0*x0+y0*y0);if(!double.IsFinite(r)||r<=0)throw new ArgumentException("理论圆半径无效");return(plane.P+u*x0+v*y0,r,plane.N);
    }
    internal static List<MeasureVector3> Translate(OpContext ctx,ParamBag p,List<MeasureVector3> points,List<MeasureVector3> target)
    {
        if(target.Count<2)throw new ArgumentException("贴合点云不足");int iterations=(int)p.GroupDbl("Fit Settings","Maximum Iterations",100);double tolerance=p.GroupDbl("Fit Settings","Tolerance",.0001),step=p.GroupDbl("Fit Settings","Step",double.MaxValue);
        if(iterations<1||iterations>2000||!double.IsFinite(tolerance)||tolerance<=0||!double.IsFinite(step)||step<=0)throw new ArgumentException("贴合迭代参数无效");
        // Translation-only ICP; no scaling, rotation or invented nominal geometry.
        var shift=MetrologyMath.Mean(target)-MetrologyMath.Mean(points);var transformed=points.Select(q=>q+shift).ToList();
        for(int i=0;i<iterations;i++)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();var offsets=new List<MeasureVector3>();foreach(var q in transformed){var nearest=target.MinBy(v=>(v-q).Dot(v-q));offsets.Add(nearest-q);}var update=MetrologyMath.Mean(offsets);if(update.Length<tolerance)break;if(update.Length>step)update=update.Unit()*step;transformed=transformed.Select(q=>q+update).ToList();
        }
        MetrologyMath.Warning(ctx,"自有贴合使用保持理论尺寸和方向的平移 ICP；局部最优与原版步进贴合数值需校准");return transformed;
    }
    internal static List<MeasureVector3> ProfileCoordinates(List<MeasureVector3> points,string plane=null)
    {
        if(plane==null)
        {double dx=points.Max(q=>q.X)-points.Min(q=>q.X),dy=points.Max(q=>q.Y)-points.Min(q=>q.Y),dz=points.Max(q=>q.Z)-points.Min(q=>q.Z);plane=dx<1e-5?"YZ":dy<1e-5?"XZ":dz<1e-5?"XY":throw new ArgumentException("理论轮廓点必须位于 XY/XZ/YZ 平面");}
        return points.Select(q=>plane switch{"XZ"=>new MeasureVector3(q.X,0,q.Z),"YZ"=>new MeasureVector3(q.Y,0,q.Z),"XY"=>new MeasureVector3(q.X,0,q.Y),_=>throw new ArgumentException("未知理论轮廓平面")}).ToList();
    }
    internal static List<MeasureVector3> ProfileTarget(OpContext ctx,ParamBag p)
    {
        var points=ProfileMath.Input(ctx,p.Int("IM In"));return ProfileMath.Groups(points,p).SelectMany(g=>g).Distinct().Select(q=>new MeasureVector3(q.X,0,q.Y)).ToList();
    }
    internal static Dictionary<string,object> LineNative(MeasureVector3 point,MeasureVector3 direction,bool profile)=>new(){["Position"]=profile?new Dictionary<string,object>{{"X",point.X},{"Z",point.Z}}:point.Native(),["Direction"]=profile?new Dictionary<string,object>{{"X",direction.X},{"Z",direction.Z}}:direction.Native()};
    internal static (MeasureVector3 P,MeasureVector3 D) ActualLine(object raw,bool profile)
    {
        if(raw is IEnumerable<object> array&&raw is not IReadOnlyDictionary<string,object>){var items=array.ToArray();if(items.Length!=1)throw new ArgumentException("该线轮廓度输入要求一条直线，请绑定数组的具体索引");raw=items[0];}
        if(!profile)return MetrologyMath.Geometry(raw,"line3d");var g=ProfileMath.Unwrap(raw,"line1d");var pos=ProfileMath.Point(C3.Sub(g,"Position"));var d=ProfileMath.Point(C3.Sub(g,"Direction"));return(new(pos.X,0,pos.Y),new MeasureVector3(d.X,0,d.Y).Unit());
    }
    internal static void Method(ParamBag p){if(p.Str("Fitting Method","Least Squares")!="Least Squares")throw new NotSupportedException("该自有名义几何测量暂支持最小二乘法，RANSAC 模式尚待实现");}
}

public sealed class PointPositionCompletionOp:IOperator
{
    public string Driver=>"3D PointPosition";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var cloud=CloudSetOp.Require(ctx,p.Int("IM In"));NominalFit.Method(p);var actual=ProfileMath.Unwrap(p.Raw("Actral Position"),"circle3d");var actualCenter=MeasureVector3.Read(actual,"Center");
        var theoretical=MetrologyMath.ReadPoints(ctx,p.Str("Theoretical Position File"));if(p.Bool("Fit Cloud"))theoretical=NominalFit.Translate(ctx,p,theoretical,CloudRegionSelector.Select(cloud,p).Select(i=>MetrologyMath.At(cloud,i)).ToList());
        var circle=NominalFit.Circle(theoretical);var value=(actualCenter-circle.Center).Length;MetrologyMath.Threshold(ctx,p,value);DetectionOverlay2D.Begin(ctx,Driver);
        if(p.Bool("Display Actral Position"))DetectionOverlay2D.Add(ctx,p,Driver,"circle3d",actual.ToDictionary(k=>k.Key,k=>k.Value),view:p.Int("IM In"));
        if(p.Bool("Display Theoretical Position"))DetectionOverlay2D.Add(ctx,p,Driver,"circle3d",new(){["Center"]=circle.Center.Native(),["Radius"]=circle.Radius,["Normal"]=circle.Normal.Native()},view:p.Int("IM In"));
        if(p.Bool("Display Segment"))DetectionOverlay2D.Add(ctx,p,Driver,"segment3d",new(){["Start"]=actualCenter.Native(),["End"]=circle.Center.Native()},view:p.Int("IM In"));if(p.Bool("Position"))MetrologyMath.Text(ctx,p,Driver,actualCenter,$"{value:0.000}");
        ctx.Vars.Set("positional_degree",value);ctx.Vars.Set("theoretical_center",circle.Center.Native());return true;
    }
}

public sealed class NominalLineProfileOp:IOperator
{
    public string Driver=>"3D ProfileofLineLine";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;NominalFit.Method(p);var mode=p.Str("Mode","3D");if(mode is not("1D" or "3D"))throw new ArgumentException("直线轮廓度模式无效");bool profile=mode=="1D";
        var input=MetrologyMath.ReadPoints(ctx,p.Str("Theoretical Line File"));var actual1=NominalFit.ActualLine(p.Raw(profile?"Actral Line 1D":"Actral Line1"),profile);
        List<List<MeasureVector3>> groups=new();var actual=new List<(MeasureVector3 P,MeasureVector3 D)>{actual1};
        if(profile){ProfileMath.Input(ctx,p.Int("IM In"));groups.Add(NominalFit.ProfileCoordinates(input));}
        else
        {
            CloudSetOp.Require(ctx,p.Int("IM In"));actual.Add(NominalFit.ActualLine(p.Raw("Actral Line2"),false));if(input.Count<4)throw new ArgumentException("两条理论线至少需要四个点");
            double best=double.PositiveInfinity;List<MeasureVector3> first=null,second=null;
            // Fit every contiguous split; the file format lists each nominal line consecutively.
            for(int split=2;split<=input.Count-2;split++)
            {
                var a=input.Take(split).ToList();var b=input.Skip(split).ToList();var l1=NominalFit.Line(a);var l2=NominalFit.Line(b);double error=a.Sum(q=>(q-l1.P).Cross(l1.D).Dot((q-l1.P).Cross(l1.D)))+b.Sum(q=>(q-l2.P).Cross(l2.D).Dot((q-l2.P).Cross(l2.D)));
                if(error<best){best=error;first=a;second=b;}
            }
            groups.Add(first);groups.Add(second);MetrologyMath.Warning(ctx,"两条理论线按文件中连续点段分组拟合；任意交错点序尚不支持");
        }
        var fits=new List<(MeasureVector3 P,MeasureVector3 D)>();double value=0;DetectionOverlay2D.Begin(ctx,Driver);
        for(int i=0;i<groups.Count;i++)
        {
            var points=groups[i];if(p.Bool("Fit Cloud"))
            {
                var target=profile?NominalFit.ProfileTarget(ctx,p):CloudRegionSelector.SelectGroups(ctx.Clouds.Get(p.Int("IM In")),p.Raw("Region"+(i+1)),ctx,p.Group("Reference")).SelectMany(g=>g).Distinct().Select(k=>MetrologyMath.At(ctx.Clouds.Get(p.Int("IM In")),k)).ToList();points=NominalFit.Translate(ctx,p,points,target);
            }
            var fit=NominalFit.Line(points);fits.Add(fit);var line=actual[i];value=Math.Max(value,2*points.Max(q=>(q-line.P).Cross(line.D).Length));
            if(p.Bool(profile?"Theoretical Line":"Theoretical Line"+(i+1)))DetectionOverlay2D.Add(ctx,p,Driver,profile?"line1d":"line3d",NominalFit.LineNative(fit.P,fit.D,profile),view:p.Int("IM In"));
            if(p.Bool(profile?"Actral Fitting Line":"Actral Fitting Line"+(i+1)))DetectionOverlay2D.Add(ctx,p,Driver,profile?"line1d":"line3d",NominalFit.LineNative(line.P,line.D,profile),view:p.Int("IM In"));
            foreach(var q in points){var foot=line.P+line.D*(q-line.P).Dot(line.D);if(p.Bool("Intersections"))MetrologyMath.Point(ctx,p,Driver,foot);if(p.Bool("Line"))DetectionOverlay2D.Add(ctx,p,Driver,"segment3d",new(){["Start"]=q.Native(),["End"]=foot.Native()},view:p.Int("IM In"));}
        }
        MetrologyMath.Threshold(ctx,p,value);if(p.Bool("Profile"))MetrologyMath.Text(ctx,p,Driver,actual1.P,$"{value:0.000}");ctx.Vars.Set("parallelism",value);ctx.Vars.Set("profile_of_line",value);for(int i=0;i<fits.Count;i++)ctx.Vars.Set(profile?"theoretical_line":"theoretical_line"+(i+1),NominalFit.LineNative(fits[i].P,fits[i].D,profile));return true;
    }
}

public sealed class NominalArcProfileOp:IOperator
{
    public string Driver=>"3D ProfileofLine";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;NominalFit.Method(p);ProfileMath.Input(ctx,p.Int("IM In"));var body=ProfileMath.Unwrap(p.Raw("Actual Circle"),"circle1d");var ac=ProfileMath.Point(C3.Sub(body,"Center"));var center=new MeasureVector3(ac.X,0,ac.Y);var radius=ProfileMath.N(body,"Radius");if(radius<=0)throw new ArgumentException("实际圆半径必须为正");
        var points=NominalFit.ProfileCoordinates(MetrologyMath.ReadPoints(ctx,p.Str("Theoretical Circle File")),p.Str("Theoretical Circle Plane","XZ"));if(p.Bool("Fit Cloud"))points=NominalFit.Translate(ctx,p,points,NominalFit.ProfileTarget(ctx,p));var nominal=NominalFit.Circle(points);double deviation=0;var feet=new List<MeasureVector3>();
        foreach(var q in points)
        {
            var d=(q-nominal.Center).Unit();var delta=nominal.Center-center;double b=d.Dot(delta),disc=b*b-delta.Dot(delta)+radius*radius;if(disc<0)throw new ArgumentException("理论径向线与实际圆无交点");double root=Math.Sqrt(disc);double t=-b+root;if(t<0)t=-b-root;if(t<0)throw new ArgumentException("理论圆外向射线与实际圆无交点");var foot=nominal.Center+d*t;feet.Add(foot);deviation=Math.Max(deviation,2*(q-foot).Length);
        }
        MetrologyMath.Threshold(ctx,p,deviation);DetectionOverlay2D.Begin(ctx,Driver);
        if(p.Bool("Actral Fitting Circle"))DetectionOverlay2D.Add(ctx,p,Driver,"circle1d",body.ToDictionary(v=>v.Key,v=>v.Value),view:p.Int("IM In"));
        if(p.Bool("Theoretical Circle"))DetectionOverlay2D.Add(ctx,p,Driver,"circle1d",new(){["Center"]=new Dictionary<string,object>{{"X",nominal.Center.X},{"Z",nominal.Center.Z}},["Radius"]=nominal.Radius},view:p.Int("IM In"));
        for(int i=0;i<points.Count;i++){if(p.Bool("Intersections"))MetrologyMath.Point(ctx,p,Driver,feet[i]);if(p.Bool("Line"))DetectionOverlay2D.Add(ctx,p,Driver,"segment3d",new(){["Start"]=points[i].Native(),["End"]=feet[i].Native()},view:p.Int("IM In"));}
        if(p.Bool("Profile"))MetrologyMath.Text(ctx,p,Driver,center,$"{deviation:0.000}");ctx.Vars.Set("profile_of_line",deviation);ctx.Vars.Set("center_distance",(nominal.Center-center).Length);return true;
    }
}
