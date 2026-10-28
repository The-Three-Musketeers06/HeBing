using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>
/// Owned height-field solder measurements. No vendor DLL or service is called here.
/// XY Delaunay surface and grid volume are independent reconstruction choices.
/// Cross-section point bands and centroid angular ordering follow retained reference captures.
/// </summary>
public sealed class SolderJoint3DOp : IOperator
{
    public string Driver => "3D Solder Joint";
    private readonly record struct Sample(int Index,double X,double Y,double Z);
    private readonly record struct Triangle(MeasureVector3 A,MeasureVector3 B,MeasureVector3 C);
    private sealed record Section(double Height,Point2f[] Polygon,double Length,double Width,double Area,double Perimeter,RotatedRect Rectangle);
    private static bool Enabled(IReadOnlyDictionary<string,object> g) => g!=null && g.TryGetValue("Enable",out var enabled)&&enabled is true;
    private static string Text(IReadOnlyDictionary<string,object> g,string key,string fallback) => g!=null&&g.TryGetValue(key,out var v)?v?.ToString()??fallback:fallback;
    private static double Positive(IReadOnlyDictionary<string,object> g,string key,double fallback)
    {
        var value=C3.N(g,key,fallback);
        return double.IsFinite(value)&&value>0?value:throw new ArgumentException(key+" 必须是正有限数值");
    }

    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;ctx.CancellationToken.ThrowIfCancellationRequested();
        var im=p.Int("IM In");var cloud=ctx.Clouds.Get(im);
        if(cloud==null||cloud.Count==0){ctx.Log("焊点检测：输入点云为空",true);return false;}
        var reference=p.Group("Reference");var toLocal=CloudRegionSelector.CreateReferenceTransform(reference);
        var referenceHeight=p.Dbl("Reference Height");
        if(!double.IsFinite(referenceHeight))throw new ArgumentException("焊点基准高度必须为有限数值");
        var cross=p.Group("Extract Cross Section");var vertical=p.Group("Extract Vertical Section");
        var surface=p.Group("Surface Area");var volume=p.Group("Volume");
        if(Enabled(volume)&&Enabled(C3.Sub(volume,"Outline Setting")))
            throw new NotSupportedException("自有焊点内核的轮廓切片体积模式尚待校准；请关闭 Volume.Outline Setting.Enable");
        var groups=CloudRegionSelector.SelectGroups(cloud,p);
        if(groups.Count==0||groups.Any(g=>g.Count==0))throw new ArgumentException("焊点区域为空");
        var outputs=new List<object>();var overlays=new List<NativeOverlay3D>();var texts=new List<Text3D>();
        var sets=new List<PointSet3D>();
        foreach(var indices in groups)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            var points=new List<Sample>(indices.Count);
            foreach(var index in indices)
            {
                if((points.Count&4095)==0)ctx.CancellationToken.ThrowIfCancellationRequested();
                var q=toLocal(cloud.X[index],cloud.Y[index],cloud.Z[index]);
                if(double.IsFinite(q.X)&&double.IsFinite(q.Y)&&double.IsFinite(q.Z))points.Add(new(index,q.X,q.Y,q.Z));
            }
            if(points.Count==0)throw new ArgumentException("焊点区域没有有效点");
            var top=points.MaxBy(q=>q.Z);var height=top.Z-referenceHeight;
            var result=new Dictionary<string,object>(StringComparer.Ordinal){["height"]=(double)(float)height};
            if(Enabled(surface))result["surface_area"]=(double)(float)Mesh(points,surface,ctx).Sum(t=>(t.B-t.A).Cross(t.C-t.A).Length/2);
            if(Enabled(volume))result["volume"]=(double)(float)Volume(points,volume,ctx);
            var sections=Enabled(cross)?Sections(points,cross,referenceHeight,ctx):new List<Section>();
            if(Enabled(cross))
            {
                result["length"]=sections.Select(s=>(object)(double)(float)s.Length).ToList();
                result["width"]=sections.Select(s=>(object)(double)(float)s.Width).ToList();
                result["cross_section_area"]=sections.Select(s=>(object)(double)(float)s.Area).ToList();
                result["cross_section_circumference"]=sections.Select(s=>(object)(double)(float)s.Perimeter).ToList();
                // Captured original results establish length/width (not 4*pi*area/perimeter^2).
                result["cross_section_circularity"]=sections.Select(s=>(object)(double)(float)(s.Length/s.Width)).ToList();
            }
            foreach(var section in sections)
            {
                if(p.Bool("Display Polygon(Cross Section)"))overlays.Add(Polygon(section.Polygon.Select(q=>new MeasureVector3(q.X,q.Y,section.Height)),reference,im));
                if(p.Bool("Display Oriented Bounding Window"))overlays.Add(Polygon(section.Rectangle.Points().Select(q=>new MeasureVector3(q.X,q.Y,section.Height)),reference,im));
            }
            if(Enabled(vertical))
            {
                var profile=VerticalSection(points,vertical);
                if(p.Bool("Display Polygon(Vertical Section)"))overlays.Add(Polygon(profile,reference,im,false));
            }
            if(p.Bool("Display Height Point"))overlays.Add(new(){Type="point3d",ViewIndex=im,Values=World(new(top.X,top.Y,top.Z),reference).Native()});
            var labels=new List<string>();
            void Label(string toggle,string name,string key,string unit)
            {
                if(p.Bool(toggle)&&result.TryGetValue(key,out var value))labels.Add(name+": "+Convert.ToDouble(value).ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+unit);
            }
            Label("Display Text(Height)","H","height"," mm");Label("Display Text(Surface Area)","S","surface_area"," mm²");Label("Display Text(Volume)","V","volume"," mm³");
            foreach(var s in sections)
            foreach(var field in new[]{("Length",s.Length),("Width",s.Width),("Circumference",s.Perimeter),("Area",s.Area),("Circularity",s.Length/s.Width)})
                if(p.Bool("Display Text(Cross Section "+field.Item1+")"))labels.Add(field.Item1+": "+field.Item2.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture));
            if(labels.Count>0){var q=World(new(top.X,top.Y,top.Z),reference);texts.Add(new(){X=q.X,Y=q.Y,Z=q.Z,ViewIndex=im,Size=14,SizeIsPixels=true,Text=string.Join("\n",labels),Color="#00FF00"});}
            sets.Add(new PointSet3D(cloud,points.Select(q=>q.Index)));outputs.Add(result);
        }
        // Commit only after every ROI has a complete measurement. Failed sections never publish partial groups.
        ctx.Vars.Set("solider_joint",outputs);
        var calibrated= !Enabled(surface)&&!Enabled(cross)&&!Enabled(volume)&&!Enabled(vertical);
        if(!calibrated)
        {
            ctx.Vars.Set("Warning",true);
            ctx.Vars.Set("Message","自有源码焊点测量：表面积、栅格体积及截面浮点数值的原版校准尚未全部完成");
        }
        ctx.NativeOverlays.AddRange(overlays);ctx.Texts.AddRange(texts);
        if(p.Bool("Pointset Cut"))
        {
            ctx.Clouds.SetPointSets(im,sets);
            var selected=sets.SelectMany(s=>s.Indices).Distinct().OrderBy(i=>i).ToArray();
            cloud.DisplayIndices=selected;
            var display=new PointCloud();foreach(var index in selected)display.Add(cloud.X[index],cloud.Y[index],cloud.Z[index],cloud.I[index]);
            ctx.NativeOverlays.Add(new(){Type="pointset",ViewIndex=im,Cloud=display,Values=new(){["_color"]="#00FF00"}});
        }
        ctx.Log($"焊点检测（自有源码）：{outputs.Count} 个区域"+(calibrated?"":"；测量结果仍在校准"),false);
        return true;
    }

    private static List<Section> Sections(List<Sample> points,IReadOnlyDictionary<string,object> settings,double reference,OpContext ctx)
    {
        var thickness=(float)C3.N(settings,"Thickness",.03);
        if(!float.IsFinite(thickness)||thickness<0)throw new ArgumentException("截面厚度必须是非负有限数值");
        var mode=Text(settings,"Extract Mode","Fixed Height");var heights=new List<double>();
        if(mode=="Fixed Height")heights.Add(C3.N(settings,"Extract Height",0));
        else if(mode=="Multiple Sections")
        {
            var count=C3.N(settings,"Number of Sections",3);
            if(count<2||count>5||count!=Math.Truncate(count))throw new ArgumentException("截面数量必须是 2..5 的整数");
            var sampling=Text(settings,"Mode","Automatic");
            for(int i=0;i<count;i++)
                heights.Add(sampling=="Automatic"?reference+(points.Max(p=>p.Z)-reference)*(i+1)/(count+1)
                    :sampling=="Manual"?C3.N(settings,"Extract Height"+(i+1),0):throw new ArgumentException("无效的截面高度模式："+sampling));
        }
        else throw new ArgumentException("无效的截面提取模式："+mode);
        var result=new List<Section>();
        var centerX=points.Average(p=>p.X);var centerY=points.Average(p=>p.Y);
        foreach(var rawHeight in heights)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            var height=(float)rawHeight;
            if(!double.IsFinite(height))throw new ArgumentException("截面高度必须为有限数值");
            // Native captures contain every source XY in [height,height+thickness],
            // projected to height and ordered about the centroid of the entire ROI.
            // Using only a convex hull discards physical sections and changes perimeter/area.
            var polygon=points.Where(p=>p.Z>=height&&p.Z<=height+thickness)
                .OrderBy(p=>Math.Atan2(p.Y-centerY,p.X-centerX))
                .Select(p=>new Point2f((float)p.X,(float)p.Y)).ToArray();
            if(polygon.Length<3)throw new ArgumentException($"高度 {height:G6} 的截面有效点不足3个，请检查提取高度和厚度");
            var area=Math.Abs(Cv2.ContourArea(polygon));
            var rect=PrincipalRectangle(polygon);var length=Math.Max(rect.Size.Width,rect.Size.Height);var width=Math.Min(rect.Size.Width,rect.Size.Height);
            if(width<=1e-12||area<=1e-12)throw new ArgumentException("截面退化，无法计算宽度、面积或圆度");
            float perimeter=0;
            for(int i=0;i<polygon.Length;i++)
            {var delta=polygon[(i+1)%polygon.Length]-polygon[i];perimeter+=MathF.Sqrt(delta.X*delta.X+delta.Y*delta.Y);}
            result.Add(new(height,polygon,length,width,area,perimeter,rect));
        }
        return result;
    }

    private static RotatedRect PrincipalRectangle(Point2f[] points)
    {
        // Retained native sections use PCA axes, rather than a minimum-area rectangle.
        double cx=points.Average(p=>(double)p.X),cy=points.Average(p=>(double)p.Y);
        double xx=0,yy=0,xy=0;
        foreach(var p in points){var x=p.X-cx;var y=p.Y-cy;xx+=x*x;yy+=y*y;xy+=x*y;}
        var angle=.5*Math.Atan2(2*xy,xx-yy);var cosine=Math.Cos(angle);var sine=Math.Sin(angle);
        double minX=double.PositiveInfinity,maxX=double.NegativeInfinity,minY=minX,maxY=maxX;
        foreach(var p in points)
        {
            var x=cosine*(p.X-cx)+sine*(p.Y-cy);var y=-sine*(p.X-cx)+cosine*(p.Y-cy);
            minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y);
        }
        var mx=(minX+maxX)/2;var my=(minY+maxY)/2;
        return new(new((float)(cx+cosine*mx-sine*my),(float)(cy+sine*mx+cosine*my)),
            new((float)(maxX-minX),(float)(maxY-minY)),(float)(angle*180/Math.PI));
    }

    private static double Volume(List<Sample> points,IReadOnlyDictionary<string,object> settings,OpContext ctx)
    {
        var dx=Positive(settings,"X Resolution",.1);var dy=Positive(settings,"Y Resolution",.1);
        var mode=Text(settings,"Z Mode","Mean");
        var direction=Text(settings,"Cut Direction","X");
        if(direction!="X")throw new NotSupportedException("自有焊点内核的 Y 向体积切片尚待实现，请使用 Cut Direction: X");
        if(mode is not ("Minimum" or "Maximum" or "Mean" or "Median"))throw new ArgumentException("无效的体积 Z 融合模式："+mode);
        var ox=points.Min(p=>p.X);var oy=points.Min(p=>p.Y);
        // Original offset/reference-height captures establish a per-ROI minimum-Z base.
        // Reference Height changes the height measurement; it does not change volume.
        var reference=points.Min(p=>p.Z);
        var cells=new Dictionary<(long,long),List<double>>();
        foreach(var point in points)
        {
            if((cells.Count&4095)==0)ctx.CancellationToken.ThrowIfCancellationRequested();
            var bx=Math.Floor((point.X-ox)/dx+1e-6);var by=Math.Floor((point.Y-oy)/dy+1e-6);
            if(bx>long.MaxValue||by>long.MaxValue)throw new ArgumentException("体积分辨率过小");
            var key=((long)bx,(long)by);if(!cells.TryGetValue(key,out var values))cells[key]=values=new();values.Add(point.Z);
        }
        double sum=0;
        foreach(var values in cells.Values)
        {
            double z;
            switch(mode)
            {
                case "Minimum":z=values.Min();break;case "Maximum":z=values.Max();break;
                case "Median":values.Sort();z=(values[(values.Count-1)/2]+values[values.Count/2])/2;break;
                default:z=values.Average();break;
            }
            sum+=Math.Max(0,z-reference)*dx*dy;
        }
        return sum;
    }

    private static List<Triangle> Mesh(List<Sample> points,IReadOnlyDictionary<string,object> settings,OpContext ctx)
    {
        var radius=Positive(settings,"Search Radius",5);var neighbours=Positive(settings,"Search Neighbours",30);
        var multiplier=Positive(settings,"Nearest Neighbor Distance Multiplier",3.5);
        if(neighbours<3||neighbours!=Math.Truncate(neighbours))throw new ArgumentException("表面法线邻居数量至少为3");
        if(neighbours!=30)throw new NotSupportedException("自有表面积内核尚未接入可变邻居数的法线估计，目前仅接受默认 Search Neighbours: 30");
        var ox=points.Min(p=>p.X);var oy=points.Min(p=>p.Y);var span=Math.Max(points.Max(p=>p.X)-ox,points.Max(p=>p.Y)-oy);
        if(span<=1e-12)throw new ArgumentException("焊点表面在 XY 平面退化");
        var scale=4094/span;
        var vertices=new Dictionary<Point2f,List<Sample>>();
        foreach(var p in points)
        {
            var xy=new Point2f((float)((p.X-ox)*scale+1),(float)((p.Y-oy)*scale+1));
            if(!vertices.TryGetValue(xy,out var list))vertices[xy]=list=new();list.Add(p);
        }
        if(vertices.Count<3)throw new ArgumentException("表面三角化有效点不足");
        using var subdiv=new Subdiv2D(new Rect(0,0,4096,4096));
        foreach(var xy in vertices.Keys){ctx.CancellationToken.ThrowIfCancellationRequested();subdiv.Insert(xy);}
        var samples=vertices.ToDictionary(kv=>kv.Key,kv=>new MeasureVector3(kv.Value[0].X,kv.Value[0].Y,kv.Value.Average(p=>p.Z)));
        var triangles=subdiv.GetTriangleList();var edges=new List<double>();
        foreach(var tri in triangles)
            if(samples.TryGetValue(new(tri.Item0,tri.Item1),out var a)&&samples.TryGetValue(new(tri.Item2,tri.Item3),out var b)&&samples.TryGetValue(new(tri.Item4,tri.Item5),out var c))
                edges.Add(Math.Min((a-b).Length,Math.Min((b-c).Length,(a-c).Length)));
        if(edges.Count==0)throw new ArgumentException("点云表面无法建立三角网格");
        edges.Sort();var limit=Math.Min(radius,edges[edges.Count/2]*multiplier);
        var mesh=new List<Triangle>();
        foreach(var tri in triangles)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            if(!samples.TryGetValue(new(tri.Item0,tri.Item1),out var a)||!samples.TryGetValue(new(tri.Item2,tri.Item3),out var b)||!samples.TryGetValue(new(tri.Item4,tri.Item5),out var c))continue;
            if((a-b).Length>limit||(b-c).Length>limit||(a-c).Length>limit)continue;
            mesh.Add(new(a,b,c));
        }
        if(mesh.Count==0)throw new ArgumentException("表面搜索范围内没有有效三角形");
        return mesh;
    }

    private static List<MeasureVector3> VerticalSection(List<Sample> points,IReadOnlyDictionary<string,object> settings)
    {
        var resolution=Positive(settings,"Resolution",.1);var width=Positive(settings,"Extract Width",.1);
        var direction=Text(settings,"Extract Direction","XZ-Plane");
        if(direction is not ("XZ-Plane" or "YZ-Plane"))throw new ArgumentException("无效的垂直截面方向");
        bool xz=direction=="XZ-Plane";var center=xz?points.Average(p=>p.Y):points.Average(p=>p.X);
        var slab=points.Where(p=>Math.Abs((xz?p.Y:p.X)-center)<=width).ToList();
        if(slab.Count<2)throw new ArgumentException("垂直截面有效点不足");
        var origin=points.Min(p=>xz?p.X:p.Y);
        return slab.GroupBy(p=>Math.Floor(((xz?p.X:p.Y)-origin)/resolution+.5)).OrderBy(g=>g.Key)
            .Select(g=>xz?new MeasureVector3(origin+g.Key*resolution,center,g.Average(p=>p.Z))
                :new MeasureVector3(center,origin+g.Key*resolution,g.Average(p=>p.Z))).ToList();
    }
    private static MeasureVector3 World(MeasureVector3 p,IReadOnlyDictionary<string,object> reference)
    {
        var rx=C3.N(reference,"X Angle",0)*Math.PI/180;var ry=C3.N(reference,"Y Angle",0)*Math.PI/180;var rz=C3.N(reference,"Z Angle",0)*Math.PI/180;
        var y=Math.Cos(rx)*p.Y-Math.Sin(rx)*p.Z;var z=Math.Sin(rx)*p.Y+Math.Cos(rx)*p.Z;
        var x=Math.Cos(ry)*p.X+Math.Sin(ry)*z;z=-Math.Sin(ry)*p.X+Math.Cos(ry)*z;
        return new(Math.Cos(rz)*x-Math.Sin(rz)*y+C3.N(reference,"X",0),Math.Sin(rz)*x+Math.Cos(rz)*y+C3.N(reference,"Y",0),z+C3.N(reference,"Z",0));
    }
    private static NativeOverlay3D Polygon(IEnumerable<MeasureVector3> points,IReadOnlyDictionary<string,object> reference,int im,bool closed=true)
        =>new(){Type=closed?"polygon3d":"trace3d",ViewIndex=im,Values=new(){["Points"]=points.Select(p=>(object)World(p,reference).Native()).ToList(),["_color"]="#00FF00",["Closed"]=closed}};
}
