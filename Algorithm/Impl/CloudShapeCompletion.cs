using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

internal static class CloudPrincipalAxes
{
    public static (MeasureVector3 Center,MeasureVector3[] Axes,double[] Values) Fit(IReadOnlyList<MeasureVector3> points)
    {
        if(points.Count<2)throw new ArgumentException("三维拟合点数不足");var center=new MeasureVector3(points.Average(q=>q.X),points.Average(q=>q.Y),points.Average(q=>q.Z));
        var a=new double[3,3];var vectors=new double[3,3];for(int i=0;i<3;i++)vectors[i,i]=1;
        foreach(var q in points){var d=q-center;double[] v=[d.X,d.Y,d.Z];for(int i=0;i<3;i++)for(int j=0;j<3;j++)a[i,j]+=v[i]*v[j];}
        for(int iteration=0;iteration<32;iteration++)
        {
            int p=0,r=1;for(int i=0;i<3;i++)for(int j=i+1;j<3;j++)if(Math.Abs(a[i,j])>Math.Abs(a[p,r])){p=i;r=j;}
            if(Math.Abs(a[p,r])<1e-13*Math.Max(1,a[0,0]+a[1,1]+a[2,2]))break;
            double phi=.5*Math.Atan2(2*a[p,r],a[r,r]-a[p,p]),c=Math.Cos(phi),s=Math.Sin(phi);double app=a[p,p],arr=a[r,r],apr=a[p,r];
            a[p,p]=c*c*app-2*c*s*apr+s*s*arr;a[r,r]=s*s*app+2*c*s*apr+c*c*arr;a[p,r]=a[r,p]=0;
            for(int k=0;k<3;k++){if(k!=p&&k!=r){double akp=a[k,p],akr=a[k,r];a[k,p]=a[p,k]=c*akp-s*akr;a[k,r]=a[r,k]=s*akp+c*akr;}double vkp=vectors[k,p],vkr=vectors[k,r];vectors[k,p]=c*vkp-s*vkr;vectors[k,r]=s*vkp+c*vkr;}
        }
        int[] order=Enumerable.Range(0,3).OrderByDescending(i=>a[i,i]).ToArray();return(center,order.Select(i=>new MeasureVector3(vectors[0,i],vectors[1,i],vectors[2,i]).Unit()).ToArray(),order.Select(i=>Math.Max(0,a[i,i])/points.Count).ToArray());
    }
    public static (MeasureVector3 U,MeasureVector3 V) Basis(MeasureVector3 normal)
    {normal=normal.Unit();var seed=Math.Abs(normal.X)<.8?new MeasureVector3(1,0,0):new MeasureVector3(0,1,0);var u=(seed-normal*seed.Dot(normal)).Unit();return(u,normal.Cross(u).Unit());}
}

internal sealed record CylinderModel(MeasureVector3 Center,MeasureVector3 Axis,double Radius,double MinAxial,double MaxAxial,double Residual);
internal static class SurfaceCylinderFit
{
    public static CylinderModel Fit(IReadOnlyList<MeasureVector3> points,CancellationToken cancellation)
    {
        if(points.Count<6||points.Any(q=>!q.Finite))throw new ArgumentException("圆柱拟合至少需要6个有限坐标点");
        var sampled=points.Count<=12000?points:Enumerable.Range(0,12000).Select(i=>points[(int)((long)i*points.Count/12000)]).ToArray();
        var pca=CloudPrincipalAxes.Fit(sampled);if(pca.Values[1]<1e-15)throw new ArgumentException("点云退化，不能拟合圆柱");CylinderModel best=null;
        CylinderModel FitAxis(MeasureVector3 axis)
        {
            axis=axis.Unit();var basis=CloudPrincipalAxes.Basis(axis);var projection=sampled.Select(q=>new Point2d((q-pca.Center).Dot(basis.U),(q-pca.Center).Dot(basis.V))).ToList();
            var circle=ProfileMath.FitCircle(projection);var center=pca.Center+basis.U*circle.center.X+basis.V*circle.center.Y;var residual=projection.Average(q=>Math.Pow(Geo.Dist(q,circle.center)-circle.radius,2));
            return new(center,axis,circle.radius,0,0,residual);
        }
        foreach(var axis in pca.Axes){cancellation.ThrowIfCancellationRequested();try{var fit=FitAxis(axis);if(best==null||fit.Residual<best.Residual)best=fit;}catch(ArgumentException){}}
        if(best==null)throw new ArgumentException("圆柱轴估计失败");
        double angle=.12;for(int iteration=0;iteration<22&&angle>1e-7;iteration++)
        {
            cancellation.ThrowIfCancellationRequested();var previous=best;var basis=CloudPrincipalAxes.Basis(best.Axis);
            foreach(var offset in new[]{basis.U,basis.U*(-1),basis.V,basis.V*(-1)}){try{var fit=FitAxis((previous.Axis+offset*angle).Unit());if(fit.Residual<best.Residual)best=fit;}catch(ArgumentException){}}
            if(ReferenceEquals(previous,best))angle*=.5;
        }
        // Canonical sign fixes unroll orientation for identical input ordering.
        var direction=best.Axis;double dominant=Math.Abs(direction.X)>Math.Abs(direction.Y)&&Math.Abs(direction.X)>Math.Abs(direction.Z)?direction.X:Math.Abs(direction.Y)>Math.Abs(direction.Z)?direction.Y:direction.Z;if(dominant<0)direction*= -1;
        var distances=points.Select(q=>(q-best.Center).Dot(direction));return best with{Axis=direction,MinAxial=distances.Min(),MaxAxial=distances.Max()};
    }
}

public sealed class CloudUnrollOp:IOperator
{
    public string Driver=>"3D Unroll";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;int im=p.Int("IM In");var source=ctx.Clouds.Get(im)??throw new ArgumentException("曲面展开输入为空");var indices=CloudRegionSelector.Select(source,p);if(indices.Count<2)throw new ArgumentException("曲面展开区域点数不足");
        var points=indices.Select(i=>new MeasureVector3(source.X[i],source.Y[i],source.Z[i])).Where(q=>q.Finite).ToList();CylinderModel cylinder;
        switch(p.Str("Ways to Get Cylinder Parameters","Fit Cylinder"))
        {
            case "Direct Access":
                object rawGeometry=p.Raw("Geometry");
                if(rawGeometry is System.Collections.IList cylinders){if(cylinders.Count!=1)throw new ArgumentException("曲面展开需要单个圆柱，请绑定圆柱数组的具体索引");rawGeometry=cylinders[0];}
                var geometry=ProfileMath.Unwrap(rawGeometry,"cylinder");var bottom=MeasureVector3.Read(geometry,"Bottom");var top=MeasureVector3.Read(geometry,"Top");var radius=ProfileMath.N(geometry,"Radius");if(radius<=0)throw new ArgumentException("圆柱半径必须为正");cylinder=new(bottom,(top-bottom).Unit(),radius,0,(top-bottom).Length,0);break;
            case "Fit Cylinder":
                var limits=p.Group("Points Limit");var minimum=ProfileMath.N(limits,"Minimum number of points",0);var maximum=ProfileMath.N(limits,"Maximum number of points",1000000);if(minimum<0||maximum<minimum||points.Count<minimum||points.Count>maximum)throw new ArgumentException("圆柱拟合点数超出限制");
                var filter=p.Group("Statistical Filter");if(ProfileMath.B(filter,"Enable"))
                {
                    int k=(int)ProfileMath.N(filter,"Set the Number of Neighboring Points",100);double factor=ProfileMath.N(filter,"Set Standard Deviation Multiple Threshold",.15);if(k<1||k>=points.Count||factor<0)throw new ArgumentException("圆柱统计滤波参数无效");
                    var cloud=new PointCloud();foreach(var q in points)cloud.Add((float)q.X,(float)q.Y,(float)q.Z);var tree=new CloudNeighbors3D(cloud,ctx.CancellationToken);var heap=new double[k];var d=new double[points.Count];for(int i=0;i<d.Length;i++){if((i&255)==0)ctx.CancellationToken.ThrowIfCancellationRequested();d[i]=tree.MeanDistance(i,heap);}var average=d.Average();var deviation=Math.Sqrt(d.Average(v=>(v-average)*(v-average)));points=points.Where((q,i)=>d[i]<=average+factor*deviation).ToList();
                }
                cylinder=SurfaceCylinderFit.Fit(points,ctx.CancellationToken);
                var threshold=p.GroupDbl("Set RANSAC Parameters","Set Distance Threshold",.15);if(threshold<=0||!double.IsFinite(threshold))throw new ArgumentException("圆柱距离阈值无效");
                var inliers=points.Where(q=>{var v=q-cylinder.Center;return Math.Abs((v-cylinder.Axis*v.Dot(cylinder.Axis)).Length-cylinder.Radius)<=threshold;}).ToList();if(inliers.Count<6)throw new ArgumentException("圆柱内点不足，请调整距离阈值");if(inliers.Count<points.Count)cylinder=SurfaceCylinderFit.Fit(inliers,ctx.CancellationToken);break;
            default:throw new ArgumentException("未知圆柱来源");
        }
        var (u,vAxis)=CloudPrincipalAxes.Basis(cylinder.Axis);var output=new PointCloud();var plane=p.Str("Target Plane","XZ plane");if(plane is not("XY plane" or "XZ plane" or "YZ plane"))throw new ArgumentException("未知展开平面");
        foreach(var i in indices)
        {
            if((output.Count&4095)==0)ctx.CancellationToken.ThrowIfCancellationRequested();var q=new MeasureVector3(source.X[i],source.Y[i],source.Z[i]);if(!q.Finite)continue;var relative=q-cylinder.Center;double axial=relative.Dot(cylinder.Axis),a=relative.Dot(u),b=relative.Dot(vAxis),radial=Math.Sqrt(a*a+b*b);if(radial<1e-12)throw new ArgumentException("圆柱轴上的点没有可定义的展开角度");double arc=Math.Atan2(b,a)*cylinder.Radius,residual=radial-cylinder.Radius;
            var result=plane=="XY plane"?new MeasureVector3(arc,axial,residual):plane=="XZ plane"?new MeasureVector3(arc,residual,axial):new MeasureVector3(residual,arc,axial);output.Add((float)result.X,(float)result.Y,(float)result.Z,i<source.I.Count?source.I[i]:0);
        }
        if(output.Count==0)throw new ArgumentException("展开结果为空");int slot=p.Int("IM Out");ctx.Images.Remove(slot);ctx.Profiles.Remove(slot);ctx.Clouds.Set(slot,output);ctx.Vars.Set("cylinder",new Dictionary<string,object>{["Bottom"]=(cylinder.Center+cylinder.Axis*cylinder.MinAxial).Native(),["Top"]=(cylinder.Center+cylinder.Axis*cylinder.MaxAxial).Native(),["Radius"]=cylinder.Radius});ctx.Vars.Set("Radius",cylinder.Radius);ctx.Vars.Set("FitResidual",Math.Sqrt(cylinder.Residual));
        if(p.Str("Ways to Get Cylinder Parameters","Fit Cylinder")=="Fit Cylinder")ProfileMath.Warn(ctx,"圆柱拟合采用PCA初值及径向最小二乘轴迭代，不是原版法线RANSAC；K近邻(Set Search Radius)参数尚未接入，超过12000点时按源顺序均匀采样拟合");return true;
    }
}

public sealed class CloudInflectionOp:IOperator
{
    public string Driver=>"3D InflectionPoints";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var cloud=ctx.Clouds.Get(p.Int("IM In"))??throw new ArgumentException("拐点输入点云为空");var selected=CloudRegionSelector.Select(cloud,p);var local=CloudRegionSelector.CreateReferenceTransform(p.Group("Reference"));string direction=p.Str("Direction to Search Inflection Points","X");if(direction is not("X" or "Y"))throw new ArgumentException("无效搜索方向");
        double width=p.Dbl("Coordinate Spacing Threshold",.1),minimum=p.GroupDbl("Angle Limit","Minimum Angle Value",90),maximum=p.GroupDbl("Angle Limit","Maximum Angle Value",150);if(!double.IsFinite(width+minimum+maximum)||width<=0||minimum<0||maximum>180||minimum>=maximum)throw new ArgumentException("拐点间距或角度阈值无效");
        var points=selected.Select(i=>{var q=local(cloud.X[i],cloud.Y[i],cloud.Z[i]);return(Index:i,Along:direction=="X"?q.X:q.Y,Across:direction=="X"?q.Y:q.X,Z:q.Z);}).Where(q=>double.IsFinite(q.Along+q.Across+q.Z)).ToList();if(points.Count<3)throw new ArgumentException("区域点数不足");double origin=points.Min(q=>q.Across);var found=new List<int>();
        foreach(var group in points.GroupBy(q=>Math.Floor((q.Across-origin)/width+1e-6)))
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();var curve=group.GroupBy(q=>q.Along).Select(g=>g.OrderBy(q=>q.Z).ElementAt(g.Count()/2)).OrderBy(q=>q.Along).ToArray();double best=double.PositiveInfinity;int bestIndex=-1;
            for(int j=1;j<curve.Length-1;j++){var a=new Point2d(curve[j-1].Along-curve[j].Along,curve[j-1].Z-curve[j].Z);var b=new Point2d(curve[j+1].Along-curve[j].Along,curve[j+1].Z-curve[j].Z);double norms=Math.Sqrt(ProfileMath.Dot(a,a)*ProfileMath.Dot(b,b));if(norms<=1e-20)continue;var angle=Math.Acos(Math.Clamp(ProfileMath.Dot(a,b)/norms,-1,1))*180/Math.PI;if(angle>=minimum&&angle<=maximum&&angle<best){best=angle;bestIndex=curve[j].Index;}}
            if(bestIndex>=0)found.Add(bestIndex);
        }
        if(found.Count==0)throw new ArgumentException("没有找到满足角度范围的拐点");var world=found.Select(i=>new MeasureVector3(cloud.X[i],cloud.Y[i],cloud.Z[i])).ToList();var center=new MeasureVector3(world.Average(q=>q.X),world.Average(q=>q.Y),world.Average(q=>q.Z));
        DetectionOverlay2D.Begin(ctx,Driver);ctx.Vars.Set("inflection_points",world.Select(q=>(object)q.Native()).ToList());ctx.Vars.Set("point",center.Native());
        var options=p.Group("Output Options");bool line=ProfileMath.B(options,"Enable")&&ProfileMath.S(options,"Output Type","Point")=="Line";
        if(ProfileMath.B(options,"Enable")&&ProfileMath.S(options,"Output Type","Point") is not("Point" or "Line"))throw new ArgumentException("无效输出类型");
        Dictionary<string,object> lineMap=null;if(line||p.Bool("Display Fitting Line of Inflection Points")){var fit=CloudPrincipalAxes.Fit(world);if(fit.Values[0]<1e-15)throw new ArgumentException("拐点不足以拟合直线");lineMap=new(){["Position"]=fit.Center.Native(),["Direction"]=fit.Axes[0].Native()};ctx.Vars.Set("line",lineMap);}
        if(p.Bool("Display Inflection Points"))foreach(var q in world)DetectionOverlay2D.Add(ctx,p,Driver,"point3d",q.Native());if(p.Bool("Display Center Point of Inflection Points"))DetectionOverlay2D.Add(ctx,p,Driver,"point3d",center.Native());if(p.Bool("Display Fitting Line of Inflection Points"))DetectionOverlay2D.Add(ctx,p,Driver,"line3d",lineMap);
        if(p.Bool("Pointset Cut")&&p.Bool("Use Region"))cloud.DisplayIndices=selected.ToArray();ProfileMath.Warn(ctx,"自有3D拐点按坐标带分组、取局部夹角最大曲率点；分带及噪声处理仍待原版数值校准");return true;
    }
}
