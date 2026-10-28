using System.Globalization;
using V3=AiVisionStudio.Algorithm.Impl.MeasureVector3;

namespace AiVisionStudio.Algorithm.Impl;

internal static class CircleCompletion3D
{
    private sealed record Circle(double X,double Y,double Radius);
    public static bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var cloud=ctx.Clouds.Get(p.Int("IM In"))??throw new ArgumentException("3D 圆输入为空");var groups=CloudRegionSelector.SelectGroups(cloud,p);
        if(groups.Count==0)throw new ArgumentException("3D 圆区域数组为空");
        var calipers=p.Int("Caliper Num",180);if(calipers<3||calipers>100000)throw new ArgumentException("圆卡尺数量必须在 3..100000 之间");
        var direction=p.Str("Direction","Probe From Outside In");if(direction is not ("Probe From Outside In" or "Probe From Inside Out"))throw new ArgumentException("未知圆边缘搜索方向");
        var search=p.Str("Search Pattern","Outermost Point");if(search!="Outermost Point")throw new NotSupportedException("未知圆边缘搜索模式："+search);
        var angle=p.Dbl("Probing Angle",45);if(!double.IsFinite(angle)||angle<=0||angle>=90)throw new ArgumentException("圆锥探针角度必须在 0..90 度之间");
        var method=p.Str("Fitting Method","Ransac");if(method is not ("Ransac" or "Least Squares"))throw new ArgumentException("无效圆拟合方法："+method);
        var threshold=C3.N(p.Group("Ransac Parameters"),"Distance Threshold",.02);var iterations=C3.N(p.Group("Ransac Parameters"),"Max Iterations",500);
        if(method=="Ransac"&&(!double.IsFinite(threshold)||threshold<=0||!double.IsFinite(iterations)||iterations<1||iterations>100000||iterations!=Math.Truncate(iterations)))throw new ArgumentException("无效圆 RANSAC 参数");
        var circles=new List<object>();var centers=new List<object>();var sets=new List<object>();var radii=new List<object>();var flatnesses=new List<object>();var radiusTexts=new List<object>();var edgeSets=new List<PointSet3D>();
        var reference=GeometryCompletion.Reference(p);
        var regions=p.Bool("Use Region")?CloudRegionSelector.SplitRegions(p.Raw("Region")):new List<object>();
        foreach(var group in groups)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();var selected=group.Where(i=>GeometryCompletion.Point(cloud,i).Finite).ToList();if(selected.Count<3)throw new ArgumentException("圆区域有效点不足 3 个");
            var samples=selected.Select(i=>GeometryCompletion.Point(cloud,i)).ToArray();var principal=GeometryCompletion.Principal(samples,ctx);
            if(principal.MiddleVariance<=1e-18)throw new ArgumentException("圆拟合点共线或重合");var origin=principal.Center;var normal=principal.Z;
            if(p.Bool("Force Horizontal Fit"))normal=reference.Direction(new(0,0,1));
            if(reference.Local(normal)-reference.Local(new()) is var n&&n.Z<0)normal=normal*-1;
            var u=Math.Abs(normal.X)<.9?new V3(1,0,0):new V3(0,1,0);u=(u-normal*u.Dot(normal)).Unit();var v=normal.Cross(u).Unit();
            // The ROI centre is the radial search origin, as specified by the manual.
            var searchOrigin=origin;
            if(regions.Count==groups.Count&&regions[circles.Count] is IReadOnlyDictionary<string,object> map)
            {
                var type=Convert.ToString(map.GetValueOrDefault("Type"));var body=type==null?map:C3.Sub(map,type)??map;
                if(type=="box")searchOrigin=reference.World((V3.Read(body,"Start")+V3.Read(body,"End"))*.5);
                else if(type=="window2d")
                {
                    var start=C3.Sub(body,"Start");var end=C3.Sub(body,"End");var local=reference.Local(origin);
                    searchOrigin=reference.World(new((C3.N(start,"X",0)+C3.N(end,"X",0))*.5,(C3.N(start,"Y",0)+C3.N(end,"Y",0))*.5,local.Z));
                }
                else if(type is "circlewindow2d" or "orientedbox")
                {var center=C3.Sub(body,"Center");var local=reference.Local(origin);searchOrigin=reference.World(new(C3.N(center,"X",0),C3.N(center,"Y",0),C3.N(center,"Z",local.Z)));}
            }
            var coordinates=selected.ToDictionary(i=>i,i=>{var d=GeometryCompletion.Point(cloud,i)-origin;return new V3(d.Dot(u),d.Dot(v),d.Dot(normal));});
            var radialOffset=searchOrigin-origin;var ox=radialOffset.Dot(u);var oy=radialOffset.Dot(v);var buckets=new Dictionary<int,(int Index,double Score)>();var outside=direction=="Probe From Outside In";
            var slope=1/Math.Tan(angle*Math.PI/180);
            foreach(var i in selected)
            {
                var q=coordinates[i];var dx=q.X-ox;var dy=q.Y-oy;var radius=Math.Sqrt(dx*dx+dy*dy);if(radius<=1e-12)continue;
                var bin=Math.Min(calipers-1,(int)((Math.Atan2(dy,dx)+Math.PI)/(2*Math.PI)*calipers));
                // Cone support penalizes displacement normal to the fitted search plane.
                var score=outside?radius-Math.Abs(q.Z)*slope:radius+Math.Abs(q.Z)*slope;
                if(!buckets.TryGetValue(bin,out var current)||(outside?score>current.Score:score<current.Score))buckets[bin]=(i,score);
            }
            var edges=buckets.OrderBy(kv=>kv.Key).Select(kv=>kv.Value.Index).ToList();if(edges.Count<3)throw new ArgumentException("圆搜索有效边缘点不足");
            Circle fit;
            if(method=="Ransac")
            {
                var random=new Random(0);List<int> best=new();double error=double.PositiveInfinity;
                for(int k=0;k<(int)iterations;k++)
                {
                    if((k&31)==0)ctx.CancellationToken.ThrowIfCancellationRequested();var triple=Enumerable.Range(0,3).Select(_=>edges[random.Next(edges.Count)]).ToArray();if(triple.Distinct().Count()!=3)continue;
                    Circle candidate;try{candidate=Fit(triple.Select(i=>coordinates[i]).ToArray());}catch(ArgumentException){continue;}
                    var inliers=edges.Where(i=>Residual(coordinates[i],candidate)<=threshold).ToList();var residual=inliers.Sum(i=>Residual(coordinates[i],candidate));
                    if(inliers.Count>best.Count||inliers.Count==best.Count&&residual<error){best=inliers;error=residual;}if(best.Count==edges.Count)break;
                }
                if(best.Count<3)throw new ArgumentException("RANSAC 没有找到圆");edges=best;fit=Fit(edges.Select(i=>coordinates[i]).ToArray());
            }
            else fit=Fit(edges.Select(i=>coordinates[i]).ToArray());
            if(p.Bool("Remove Outliers"))
            {
                var distance=p.Dbl("Minimum Distance Threshold",2);if(!double.IsFinite(distance)||distance<=0)throw new ArgumentException("圆离群过滤距离必须为正");
                edges=edges.Where(i=>Residual(coordinates[i],fit)<=distance).ToList();if(edges.Count<3)throw new ArgumentException("圆滤波后有效点不足");fit=Fit(edges.Select(i=>coordinates[i]).ToArray());
            }
            // Place the fitted centre at the accepted edge's normal coordinate,
            // instead of retaining the ROI background's average elevation.
            var edgeMean=GeometryCompletion.Mean(edges.Select(i=>GeometryCompletion.Point(cloud,i)));var center3=origin+u*fit.X+v*fit.Y+normal*(edgeMean-origin).Dot(normal);
            var flat=edges.Max(i=>coordinates[i].Z)-edges.Min(i=>coordinates[i].Z);var circle=new Dictionary<string,object>{{"Type","circle3d"},{"_type","circle3d"},{"Center",center3.Native()},{"Radius",fit.Radius},{"Rotation",GeometryCompletion.Rotation(u,v,normal)},{"Normal",normal.Native()},{"CenterX",center3.X},{"CenterY",center3.Y},{"CenterZ",center3.Z},{"Flatness",flat}};
            circles.Add(circle);centers.Add(GeometryCompletion.Typed(center3));radii.Add(fit.Radius);flatnesses.Add(flat);radiusTexts.Add(fit.Radius.ToString("0.000",CultureInfo.InvariantCulture));
            var set=new PointSet3D(cloud,edges);sets.Add(set);edgeSets.Add(set);
        }
        var array=GeometryCompletion.IsArray(p);
        ctx.Vars.Set("circle",GeometryCompletion.Pack(circles,array));ctx.Vars.Set("Center Point 3D",GeometryCompletion.Pack(centers,array));ctx.Vars.Set("edge_cloud_point",GeometryCompletion.Pack(sets,array));ctx.Vars.Set("inliers",GeometryCompletion.Pack(sets,array));
        ctx.Vars.Set("Circle.Radius",GeometryCompletion.Pack(radii,array));ctx.Vars.Set("Circle.Flatness",GeometryCompletion.Pack(flatnesses,array));ctx.Vars.Set("Rradius Text",GeometryCompletion.Pack(radiusTexts,array));ctx.Vars.Set("Radius Text",GeometryCompletion.Pack(radiusTexts,array));
        ctx.Clouds.SetPointSets(p.Int("IM In"),edgeSets);DetectionOverlay2D.Begin(ctx,"3D Circle");
        for(int i=0;i<circles.Count;i++)
        {
            if(p.Bool("Circle 3D"))DetectionOverlay2D.Add(ctx,p,"3D Circle","circle3d",(Dictionary<string,object>)circles[i]);
            if(p.Bool("Center Point 3D"))DetectionOverlay2D.Add(ctx,p,"3D Circle","point3d",(Dictionary<string,object>)centers[i]);
            if(p.Bool("Radius Text"))DetectionOverlay2D.Add(ctx,p,"3D Circle","text3d",new(){{"Position",centers[i]},{"Text","R="+radiusTexts[i]}});
            if(p.Bool("Edge Point 3D"))GeometryCompletion.Pointset(ctx,p,"3D Circle",cloud,edgeSets[i].Indices);
        }
        if(p.Bool("Pointset Cut"))GeometryCompletion.Pointset(ctx,p,"3D Circle",cloud,groups.SelectMany(g=>g).Distinct());
        ctx.Vars.Set("Warning",true);ctx.Vars.Set("Message","自有三维圆：径向锥卡尺及拟合已实现，边缘选择参数仍需与原版逐项校准");return true;
    }
    private static double Residual(V3 p,Circle c)=>Math.Abs(Math.Sqrt((p.X-c.X)*(p.X-c.X)+(p.Y-c.Y)*(p.Y-c.Y))-c.Radius);
    private static Circle Fit(IReadOnlyList<V3> points)
    {
        // Centred algebraic least squares; normal equations are only 3x3.
        var mean=GeometryCompletion.Mean(points);var a=new double[3,4];
        foreach(var point in points){var q=point-mean;var row=new[]{q.X,q.Y,1d};var rhs=-(q.X*q.X+q.Y*q.Y);for(int i=0;i<3;i++){for(int j=0;j<3;j++)a[i,j]+=row[i]*row[j];a[i,3]+=row[i]*rhs;}}
        for(int i=0;i<3;i++){int pivot=i;for(int j=i+1;j<3;j++)if(Math.Abs(a[j,i])>Math.Abs(a[pivot,i]))pivot=j;if(Math.Abs(a[pivot,i])<1e-15)throw new ArgumentException("圆边缘共线或退化");for(int j=i;j<4;j++)(a[i,j],a[pivot,j])=(a[pivot,j],a[i,j]);var div=a[i,i];for(int j=i;j<4;j++)a[i,j]/=div;for(int k=0;k<3;k++)if(k!=i){var factor=a[k,i];for(int j=i;j<4;j++)a[k,j]-=factor*a[i,j];}}
        var x=-a[0,3]/2;var y=-a[1,3]/2;var squared=x*x+y*y-a[2,3];if(!double.IsFinite(squared)||squared<=0)throw new ArgumentException("圆半径退化");return new(x+mean.X,y+mean.Y,Math.Sqrt(squared));
    }
}
