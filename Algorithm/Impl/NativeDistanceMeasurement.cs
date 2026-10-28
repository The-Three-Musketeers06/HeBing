namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Native-format distance modes. Geometric calculations retain all three coordinates.</summary>
internal static class NativeDistanceMeasurement
{
    private readonly record struct Geometry(string Type,MeasureVector3 Position,MeasureVector3 Direction,Dictionary<string,object> Body);
    private static Geometry Read(object value)
    {
        if(value is PlaneObj plane)
        {
            var n=new MeasureVector3(plane.NX,plane.NY,plane.NZ);var u=n.Unit();
            if(!double.IsFinite(plane.D))throw new ArgumentException("平面方程无效");
            var planePosition=u*(-plane.D/n.Length);return new("plane",planePosition,u,new(){["Position"]=planePosition.Native(),["Normal"]=u.Native()});
        }
        if(value is not IReadOnlyDictionary<string,object> map)throw new ArgumentException("距离几何输入为空或类型无效");
        var type=map.GetValueOrDefault("Type")?.ToString()??map.GetValueOrDefault("_type")?.ToString()??(map.ContainsKey("Normal")?"plane":map.ContainsKey("Direction")?"line3d":"point3d");
        var body=C3.Sub(map,type)??map;
        var pos=type=="point3d"?MeasureVector3.Read(new Dictionary<string,object>{{"p",body}},"p"):MeasureVector3.Read(body,"Position");
        var dir=type switch{"point3d"=>default,"line3d"=>MeasureVector3.Read(body,"Direction").Unit(),"plane"=>MeasureVector3.Read(body,"Normal").Unit(),_=>throw new ArgumentException("不支持的距离几何类型："+type)};
        return new(type,pos,dir,body.ToDictionary(x=>x.Key,x=>x.Value));
    }
    private static MeasureVector3 Project(MeasureVector3 v,string direction)=>direction switch{
        "shortest distance"=>v,"parallel to X-axis"=>new(v.X,0,0),"parallel to Y-axis"=>new(0,v.Y,0),"parallel to Z-axis"=>new(0,0,v.Z),
        "in XY-plane"=>new(v.X,v.Y,0),"in XZ-plane"=>new(v.X,0,v.Z),"in YZ-plane"=>new(0,v.Y,v.Z),_=>throw new ArgumentException("未知距离测量方向："+direction)};
    private static MeasureVector3 Nearest(MeasureVector3 point,Geometry geometry,string direction)
    {
        if(geometry.Type=="point3d")return point+Project(geometry.Position-point,direction);
        if(geometry.Type=="plane")
        {
            // Constrain the displacement to the requested axis/plane. Its minimum
            // norm solution is parallel to the projected normal, not the full normal.
            var normal=Project(geometry.Direction,direction);var denominator=normal.Dot(normal);
            if(denominator<1e-20)throw new ArgumentException("输入平面与测量方向平行，无法计算距离");
            return point+normal*(geometry.Direction.Dot(geometry.Position-point)/denominator);
        }
        if(direction.StartsWith("parallel to",StringComparison.Ordinal))
        {
            var excluded=geometry.Direction-Project(geometry.Direction,direction);var offset=point-geometry.Position;var rest=offset-Project(offset,direction);var norm=excluded.Dot(excluded);
            if(norm<1e-20)
            {
                if(rest.Length>1e-8)throw new ArgumentException("直线与测量轴平行，无法取得该方向交点");
                return point;
            }
            var t=rest.Dot(excluded)/norm;
            if((excluded*t-rest).Length>1e-7*Math.Max(1,rest.Length))throw new ArgumentException("直线与指定方向测量线不相交");
            return geometry.Position+geometry.Direction*t;
        }
        var origin=point+Project(geometry.Position-point,direction);var vector=Project(geometry.Direction,direction);var length=vector.Dot(vector);
        return length<1e-20?origin:origin+vector*((point-origin).Dot(vector)/length);
    }
    private static (MeasureVector3 A,MeasureVector3 B) Closest(Geometry a,Geometry b)
    {
        if(a.Type!="line3d"||b.Type!="line3d")throw new ArgumentException("异面直线模式必须输入两条直线");
        var offset=a.Position-b.Position;var dot=a.Direction.Dot(b.Direction);var denominator=1-dot*dot;
        if(denominator<1e-14)return(a.Position,b.Position+b.Direction*((a.Position-b.Position).Dot(b.Direction)));
        var t=(dot*b.Direction.Dot(offset)-a.Direction.Dot(offset))/denominator;
        var s=(b.Direction.Dot(offset)-dot*a.Direction.Dot(offset))/denominator;
        return(a.Position+a.Direction*t,b.Position+b.Direction*s);
    }
    public static bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var mode=p.Str("Mode");
        if(mode is not("Distance from point" or "Distance between lines - closest approach" or "Distance between lines or planes - nearest third point"))throw new ArgumentException("未知距离模式："+mode);
        var group=p.Group(mode);
        object Input(string key)=>p.Bound(mode+"."+key)??group?.GetValueOrDefault(key);
        var first=Input("First Object");var second=Input("Second Object");
        var reference=mode=="Distance between lines or planes - nearest third point"?Input("Point where the distance should be measured"):null;
        object[] Items(object v)=>v is IEnumerable<object> list&&v is not IReadOnlyDictionary<string,object>?list.ToArray():new[]{v};
        var a=Items(first);var b=Items(second);var r=Items(reference);var count=Math.Max(a.Length,Math.Max(b.Length,r.Length));
        if(count==0||new[]{a.Length,b.Length,r.Length}.Any(n=>n!=1&&n!=count))throw new ArgumentException("距离几何数组长度不匹配");
        var direction=p.Group("Measurement direction")?.GetValueOrDefault("Direction")?.ToString()??"shortest distance";Project(default,direction);
        var measurements=new List<(Geometry First,Geometry Second,MeasureVector3? Ref,MeasureVector3 Start,MeasureVector3 End,double Value)>();
        for(int i=0;i<count;i++)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();var one=Read(a[a.Length==1?0:i]);var two=Read(b[b.Length==1?0:i]);MeasureVector3 start,end;MeasureVector3? refpoint=null;
            if(mode=="Distance from point")
            {
                if(one.Type!="point3d")throw new ArgumentException("到点模式的几何1必须为3D点");
                start=one.Position;end=Nearest(start,two,direction);
            }
            else if(mode=="Distance between lines - closest approach")(start,end)=Closest(one,two);
            else
            {
                if(one.Type=="point3d"||two.Type=="point3d")throw new ArgumentException("第三点模式的目标必须为直线或平面");
                var q=Read(r[r.Length==1?0:i]);if(q.Type!="point3d")throw new ArgumentException("参考点必须为3D点");refpoint=q.Position;
                start=Nearest(q.Position,one,direction);end=Nearest(q.Position,two,direction);
            }
            var value=(end-start).Length;if(!double.IsFinite(value))throw new ArgumentException("距离结果非有限值");measurements.Add((one,two,refpoint,start,end,value));
        }
        DetectionOverlay2D.Begin(ctx,"3D Distance");int view=p.Int("IM In");
        foreach(var m in measurements)
        {
            if(p.Bool("Display First Object"))DetectionOverlay2D.Add(ctx,p,"3D Distance",m.First.Type,m.First.Body,view:view);
            if(p.Bool("Display Second Object"))DetectionOverlay2D.Add(ctx,p,"3D Distance",m.Second.Type,m.Second.Body,view:view);
            if(p.Bool("Display Segment"))DetectionOverlay2D.Add(ctx,p,"3D Distance","segment3d",new(){["Start"]=m.Start.Native(),["End"]=m.End.Native()},view:view);
            if(p.Bool("Display Point where the distance should be measured")&&m.Ref.HasValue)DetectionOverlay2D.Add(ctx,p,"3D Distance","point3d",m.Ref.Value.Native(),view:view);
            if(p.Bool("Display Distance Text"))DetectionOverlay2D.Add(ctx,p,"3D Distance","text3d",new(){["Position"]=((m.Start+m.End)*.5).Native(),["Text"]=m.Value.ToString("F3",System.Globalization.CultureInfo.InvariantCulture),["Size"]=12d},view:view);
        }
        object result=first is IEnumerable<object>||second is IEnumerable<object>||reference is IEnumerable<object>?measurements.Select(m=>(object)m.Value).ToList():measurements[0].Value;
        ctx.Vars.Set("distance",result);ctx.Vars.Set("Distance",result);return true;
    }
}
