using System.Globalization;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Regular ROI grids in reference coordinates, sorted before binding to measurements.</summary>
public sealed class CreateRoiArrayOp : IOperator
{
    public string Driver => "CreateROIArray";
    public bool Run(OpContext ctx, ParamBag p)
    {
        DetectionOverlay2D.Begin(ctx, Driver);
        var mode = p.Str("Mode", "Manual");
        var group = p.Group(mode) ?? throw new ArgumentException("ROI 阵列模式无效：" + mode);
        object region; int rows, columns; double dx, dy;
        double N(string key, double fallback = 0) => C3.N(group, key, fallback);
        if (mode == "Manual")
        {
            region = group.GetValueOrDefault("Region") ?? throw new ArgumentException("ROI 阵列缺少起始区域");
            rows = Count(N("Number of Rows", 2)); columns = Count(N("Number of Columns", 2));
            dx = N("Interval of Columns", 2); dy = N("Interval of Rows", 2);
        }
        else if (mode == "Automatic")
        {
            var boundary = Body(group.GetValueOrDefault("Boundary"), out var boundaryType);
            if (boundaryType is not ("box" or "window2d")) throw new ArgumentException("ROI 阵列边界必须为盒子或窗口");
            var start = C3.Sub(boundary, "Start"); var end = C3.Sub(boundary, "End");
            var x1 = Math.Min(C3.N(start,"X",0),C3.N(end,"X",0)); var x2 = Math.Max(C3.N(start,"X",0),C3.N(end,"X",0));
            var y1 = Math.Min(C3.N(start,"Y",0),C3.N(end,"Y",0)); var y2 = Math.Max(C3.N(start,"Y",0),C3.N(end,"Y",0));
            var suffix = boundaryType == "box" ? "(Box)" : "(Window)";
            var type = group.GetValueOrDefault("ROI Type"+suffix)?.ToString() ?? boundaryType;
            if(type is not ("box" or "window2d" or "circlewindow2d" or "cylinderbox"))throw new ArgumentException("自动 ROI 类型无效："+type);
            var width = N("ROI Length"+suffix, 5); var height = N("ROI Width"+suffix, 5);
            if(type is "circlewindow2d" or "cylinderbox")height=width;
            if (!(width > 0 && height > 0 && width <= x2-x1 && height <= y2-y1)) throw new ArgumentException("ROI 尺寸超出阵列边界");
            if (Convert.ToString(group.GetValueOrDefault("Mode")) == "Based on rows and columns")
            {
                rows=Count(N("Number of Rows",2)); columns=Count(N("Number of Columns",2));
                dx=columns>1?(x2-x1-width)/(columns-1):0; dy=rows>1?(y2-y1-height)/(rows-1):0;
                if(columns==1)x1+=(x2-x1-width)/2; if(rows==1)y1+=(y2-y1-height)/2;
            }
            else if(Convert.ToString(group.GetValueOrDefault("Mode")) == "Based on interval")
            {
                dx=N("Interval of Columns",2); dy=N("Interval of Rows",2);
                if(!(dx>0&&dy>0))throw new ArgumentException("自动阵列间隔必须为正");
                columns=Count(Math.Floor((x2-x1-width)/dx)+1); rows=Count(Math.Floor((y2-y1-height)/dy)+1);
            }
            else throw new ArgumentException("自动 ROI 阵列模式无效");
            if(type is "circlewindow2d" or "cylinderbox")
            {
                var z1=Math.Min(C3.N(start,"Z",0),C3.N(end,"Z",0));var z2=Math.Max(C3.N(start,"Z",0),C3.N(end,"Z",0));
                region=new Dictionary<string,object>{["Type"]=type,[type=="cylinderbox"?"Bottom":"Center"]=Point(x1+width/2,y1+height/2,z1),["Radius"]=width/2,["Height"]=z2-z1};
            }
            else region = new Dictionary<string,object> { ["Type"]=type,
                ["Start"]=Point(x1,y1,C3.N(start,"Z",0)), ["End"]=Point(x1+width,y1+height,C3.N(end,"Z",0)) };
        }
        else throw new ArgumentException("ROI 阵列模式无效："+mode);
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || (long)rows*columns>100000) throw new ArgumentException("ROI 阵列尺寸或间隔无效");
        var body=Body(region,out var shape);
        var entries = new List<(object Region,double X,double Y)>();
        for(int column=0;column<columns;column++) for(int row=0;row<rows;row++)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            var offsetX=column*dx; var offsetY=row*dy;
            var value=Translate(body,shape,offsetX,offsetY);
            var center=C3.Sub(value, "Start") ?? C3.Sub(value,"Center") ?? C3.Sub(value,"Bottom");
            entries.Add((value,C3.N(center,"X",0),C3.N(center,"Y",0)));
        }
        var sort=p.Str("Sort Mode","+X,-Y").Split(',');
        if(sort.Length!=2 || sort.Any(s=>s is not ("+X" or "-X" or "+Y" or "-Y")) || sort[0][1]==sort[1][1])throw new ArgumentException("ROI 阵列排序方式无效");
        double Coordinate((object Region,double X,double Y) e,string axis)=>(axis[1]=='X'?e.X:e.Y)*(axis[0]=='-'?-1:1);
        var reference=p.Group("Reference");
        var identity=new[]{"X","Y","Z","X Angle","Y Angle","Z Angle"}.All(key=>C3.N(reference,key,0)==0);
        var output=entries.OrderBy(e=>Coordinate(e,sort[0])).ThenBy(e=>Coordinate(e,sort[1]))
            .Select(e=>identity?e.Region:(object)new Dictionary<string,object> { ["Region"]=e.Region, ["Reference"]=reference }).ToList();
        CloudRegionSelector.SelectGroups(new PointCloud(),output,ctx);
        ctx.Vars.Set("roi",output); ctx.Vars.Set("ROI.Count",output.Count);
        if(p.Bool("Display ROI")) foreach(var entry in entries) DrawRegion(ctx,p,Driver,(IReadOnlyDictionary<string,object>)entry.Region,reference);
        ctx.Log($"创建 ROI 阵列：{rows} 行 × {columns} 列，共 {output.Count} 个区域",false);
        return true;
    }
    private static int Count(double number) => double.IsFinite(number)&&number>=1&&number<=100000&&number==Math.Truncate(number)?(int)number:throw new ArgumentException("阵列行列数必须为正整数");
    internal static Dictionary<string,object> Point(double x,double y,double z)=>new(){["X"]=x,["Y"]=y,["Z"]=z};
    private static IReadOnlyDictionary<string,object> Body(object region,out string type)
    {
        var map=region as IReadOnlyDictionary<string,object> ?? throw new ArgumentException("ROI 几何无效");
        type=Convert.ToString(map.GetValueOrDefault("Type")??map.GetValueOrDefault("_type")) ?? (map.ContainsKey("Start")?"box":"");
        return C3.Sub(map,type)??map;
    }
    private static Dictionary<string,object> Translate(IReadOnlyDictionary<string,object> body,string type,double dx,double dy)
    {
        if(type is not ("box" or "window2d" or "circlewindow2d" or "cylinderbox"))throw new NotSupportedException("ROI 阵列形状不支持："+type);
        var copy=body.ToDictionary(k=>k.Key,k=>k.Value); copy["Type"]=type; copy["_type"]=type;
        foreach(var key in new[]{"Start","End","Center","Bottom"}) if(C3.Sub(body,key) is {} point)
        {var shifted=point.ToDictionary(k=>k.Key,k=>k.Value); shifted["X"]=C3.N(point,"X",0)+dx; shifted["Y"]=C3.N(point,"Y",0)+dy;copy[key]=shifted;}
        return copy;
    }
    internal static void DrawRegion(OpContext ctx,ParamBag p,string driver,IReadOnlyDictionary<string,object> region,IReadOnlyDictionary<string,object> reference)
    {
        var body=Body(region,out var type);
        if(type=="box")
        {
            var start=MeasureVector3.Read(body,"Start");var end=MeasureVector3.Read(body,"End");
            var center=Forward((start+end)*.5,reference);
            DetectionOverlay2D.Add(ctx,p,driver,"orientedbox",new(){["Center"]=center.Native(),["Size"]=(end-start).Native(),
                ["Rotation"]=Point(C3.N(reference,"X Angle",0),C3.N(reference,"Y Angle",0),C3.N(reference,"Z Angle",0))});
        }
        else DetectionOverlay2D.Add(ctx,p,driver,type,body.ToDictionary(k=>k.Key,k=>k.Value));
    }
    internal static MeasureVector3 Forward(MeasureVector3 v,IReadOnlyDictionary<string,object> reference)
    {
        var rx=C3.N(reference,"X Angle",0)*Math.PI/180;var ry=C3.N(reference,"Y Angle",0)*Math.PI/180;var rz=C3.N(reference,"Z Angle",0)*Math.PI/180;
        var y=Math.Cos(rx)*v.Y-Math.Sin(rx)*v.Z;var z=Math.Sin(rx)*v.Y+Math.Cos(rx)*v.Z;
        var x=Math.Cos(ry)*v.X+Math.Sin(ry)*z;z=-Math.Sin(ry)*v.X+Math.Cos(ry)*z;
        return new(Math.Cos(rz)*x-Math.Sin(rz)*y+C3.N(reference,"X",0),Math.Sin(rz)*x+Math.Cos(rz)*y+C3.N(reference,"Y",0),z+C3.N(reference,"Z",0));
    }
}

public sealed class Gap3DOp : IOperator
{
    public string Driver => "3D Gap";
    public bool Run(OpContext ctx,ParamBag p)
    {
        DetectionOverlay2D.Begin(ctx,Driver);
        var cloud=ctx.Clouds.Get(p.Int("IM In"))??throw new InvalidOperationException("段差：输入点云为空");
        var reference=p.Group("Reference"); var transform=CloudRegionSelector.CreateReferenceTransform(reference);
        var firstGroups=CloudRegionSelector.SelectGroups(cloud,p.Raw("Region1"),ctx,reference);
        var secondGroups=CloudRegionSelector.SelectGroups(cloud,p.Raw("Region2"),ctx,reference);
        var count=Math.Max(firstGroups.Count,secondGroups.Count);
        if(count==0||firstGroups.Count!=secondGroups.Count&&firstGroups.Count!=1&&secondGroups.Count!=1)
            throw new ArgumentException("段差区域数量必须相同，或一侧为单个基准区域");
        (double Value,Dictionary<string,object> Point,List<int> Indices) Measure(int number,List<int> indices)
        {
            if(indices.Count==0)throw new InvalidOperationException($"段差区域 {number} 内无点");
            var values=indices.Select(i=>(Index:i,Height:transform(cloud.X[i],cloud.Y[i],cloud.Z[i]).Z)).Where(v=>double.IsFinite(v.Height)).OrderBy(v=>v.Height).ToList();
            var settings=p.Group("Region"+number+" Settings");
            double Quantile(double percentile)
            {
                if(!double.IsFinite(percentile)||percentile<0||percentile>100)throw new ArgumentException("段差百分位无效");
                var pos=(values.Count-1)*percentile/100;var lo=(int)pos;var hi=(int)Math.Ceiling(pos);
                return values[lo].Height+(values[hi].Height-values[lo].Height)*(pos-lo);
            }
            if(values.Count==0)throw new InvalidOperationException("段差区域没有有限坐标");
            var removal=settings?.GetValueOrDefault("Outlier Removal Method")?.ToString()??"None";
            if(removal.Equals("Percentile Trimming",StringComparison.OrdinalIgnoreCase))
            {
                var trim=C3.Sub(settings,"Percentile Trimming");var low=C3.N(trim,"Percent to Trim from Bottom",10);var high=C3.N(trim,"Percent to Trim from Top",10);
                if(low<0||high<0||low+high>=100)throw new ArgumentException("段差剔除比例无效");
                var a=Quantile(low);var b=Quantile(100-high);values=values.Where(v=>v.Height>=a&&v.Height<=b).ToList();
                if(values.Count==0)throw new InvalidOperationException("段差剔除后没有测量点");
            }
            else if(!removal.Equals("None",StringComparison.OrdinalIgnoreCase))throw new NotSupportedException("段差剔除方式不支持："+removal);
            var mode=settings?.GetValueOrDefault("Height Mode")?.ToString()??"Median";
            var height=mode switch {"Minimum"=>values[0].Height,"Maximum"=>values[^1].Height,"Mean"=>values.Average(v=>v.Height),"Median"=>Quantile(50),"Percentile"=>Quantile(C3.N(settings,"Percentile",10)),_=>throw new NotSupportedException("段差高度模式不支持："+mode)};
            var i=values.MinBy(v=>Math.Abs(v.Height-height)).Index;
            return(height,CreateRoiArrayOp.Point(cloud.X[i],cloud.Y[i],cloud.Z[i]),indices);
        }
        var gaps=new List<object>();var heights1=new List<object>();var heights2=new List<object>();
        var points1=new List<object>();var points2=new List<object>();var pointsets=new List<PointSet3D>();
        for(int regionIndex=0;regionIndex<count;regionIndex++)
        {
        ctx.CancellationToken.ThrowIfCancellationRequested();
        var first=Measure(1,firstGroups[firstGroups.Count==1?0:regionIndex]);var second=Measure(2,secondGroups[secondGroups.Count==1?0:regionIndex]);
        var gap=p.Str("Gap Mode","Region1-Region2") switch {"Region1-Region2"=>first.Value-second.Value,"Region2-Region1"=>second.Value-first.Value,"Absolute" or "Absolute Value" or "|Region1-Region2|"=>Math.Abs(first.Value-second.Value),var mode=>throw new NotSupportedException("段差模式不支持："+mode)};
        gaps.Add(gap);heights1.Add(first.Value);heights2.Add(second.Value);points1.Add(first.Point);points2.Add(second.Point);
        foreach(var item in new[]{first,second})
        {
            if(p.Bool("Height Points"))DetectionOverlay2D.Add(ctx,p,Driver,"point3d",item.Point);
            if(p.Bool("Height Value"))Text(item.Point,item.Value);
        }
        if(p.Bool("Gap Value"))Text(first.Point,gap);
        if(p.Bool("Gap Segment"))DetectionOverlay2D.Add(ctx,p,Driver,"segment3d",new(){["Start"]=first.Point,["End"]=second.Point});
        if(p.Bool("Pointset Cut")){pointsets.Add(new PointSet3D(cloud,first.Indices));pointsets.Add(new PointSet3D(cloud,second.Indices));}
        ctx.Log($"3D 段差：{gap:0.000000}（区域1 {first.Value:0.000000}，区域2 {second.Value:0.000000}）",false);
        }
        ctx.Vars.Set("gap",gaps);ctx.Vars.Set("height_1_value",heights1);ctx.Vars.Set("height_2_value",heights2);
        ctx.Vars.Set("height_1_position_point",points1);ctx.Vars.Set("height_2_position_point",points2);
        if(pointsets.Count>0)ctx.Clouds.SetPointSets(p.Int("IM In"),pointsets);
        return true;
        void Text(IReadOnlyDictionary<string,object> point,double value)=>ctx.Texts.Add(new(){Owner=string.IsNullOrEmpty(ctx.OperationUuid)?Driver:ctx.OperationUuid,ViewIndex=p.Int("IM In"),X=C3.N(point,"X",0),Y=C3.N(point,"Y",0),Z=C3.N(point,"Z",0),Text=value.ToString("F3",CultureInfo.InvariantCulture),Size=12,SizeIsPixels=true,Color="#00FF00"});
    }
}

public sealed class DisplayData3DOp : IOperator
{
    public string Driver=>"3D Display Data";
    public bool Run(OpContext ctx,ParamBag p)
    {
        DetectionOverlay2D.Begin(ctx,Driver);int count=0;
        foreach(var key in p.StringList("_input_list"))
        {
            var g=p.Group(key);if(g==null||g.GetValueOrDefault("Display") is false)continue;
            var content=ControlValues.Read(p,key+".Content");if(content==null)continue;
            var decimals=(int)Math.Clamp(C3.N(g,"Decimals",3),0,12);
            string Format(object value)=>value switch {bool b=>b?"OK":"NG",float or double or decimal=>Convert.ToDouble(value).ToString("F"+decimals,CultureInfo.InvariantCulture),System.Collections.IEnumerable list when value is not string=>string.Join(", ",list.Cast<object>().Select(Format)),_=>Convert.ToString(value,CultureInfo.InvariantCulture)};
            var ok=ControlValues.Bool(ControlValues.Read(p,key+".Related Situation",true));
            ctx.Texts.Add(new(){Owner=string.IsNullOrEmpty(ctx.OperationUuid)?Driver:ctx.OperationUuid,ViewIndex=p.Int("IM In"),
                X=C3.N(g,"X",0),Y=C3.N(g,"Y",0),Z=C3.N(g,"Z",0),Size=C3.N(g,"Font Size",12),SizeIsPixels=true,
                Text=Convert.ToString(g.GetValueOrDefault("Prefix"))+Format(content)+Convert.ToString(g.GetValueOrDefault("Suffix")),
                Color=Convert.ToString(g.GetValueOrDefault(ok?"OK Color":"NG Color"))??(ok?"#18A058":"#D03050")});count++;
        }
        ctx.Vars.Set("DisplayedCount",count);ctx.Log($"3D 数据显示：{count} 项",false);return true;
    }
}
