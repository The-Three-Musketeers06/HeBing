using System.Globalization;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Owned detection graphics, kept separate from input pixels and shared with HMI.</summary>
internal static class DetectionOverlay2D
{
    public static Dictionary<string, object> XY(double x, double y) => new() { ["X"] = x, ["Y"] = y };
    public static void Begin(OpContext ctx, string driver)
    {
        var owner = string.IsNullOrEmpty(ctx.OperationUuid) ? driver : ctx.OperationUuid;
        ctx.NativeOverlays.RemoveAll(o => o.Values.TryGetValue("_owned_operation", out var value) && Equals(value, owner));
        ctx.Texts.RemoveAll(t => t.Owner == owner);
    }
    public static void Add(OpContext ctx, ParamBag p, string driver, string type, Dictionary<string, object> values,
        string attribute = null, bool ok = true, int? view = null)
    {
        values = new Dictionary<string, object>(values, StringComparer.OrdinalIgnoreCase);
        var style = attribute == null ? null : p.Group(attribute);
        if(style==null&&attribute!=null)style=p.Group(attribute.Replace(" Attribute"," Display"));
        values["_color"] = style?.GetValueOrDefault(ok ? "OK Color" : "NG Color") ?? values.GetValueOrDefault("_color") ?? (ok ? "#00FF00" : "#FF0000");
        values["_thickness"] = style?.GetValueOrDefault("Thickness") ?? values.GetValueOrDefault("_thickness") ?? 1;
        values["_size"] = style?.GetValueOrDefault("Size") ?? style?.GetValueOrDefault("Font Size") ?? values.GetValueOrDefault("_size") ?? (type == "point2d" ? 5 : 12);
        values["_owned_operation"] = string.IsNullOrEmpty(ctx.OperationUuid) ? driver : ctx.OperationUuid;
        ctx.NativeOverlays.Add(new() { Type = type, ViewIndex = view ?? p.Int("IM In"), Values = values });
        if(type=="text3d"&&values.GetValueOrDefault("Position") is IReadOnlyDictionary<string,object> position)
            ctx.Texts.Add(new Text3D {Owner=values["_owned_operation"].ToString(),ViewIndex=view??p.Int("IM In"),X=Number(position,"X"),Y=Number(position,"Y"),Z=Number(position,"Z"),Text=values.GetValueOrDefault("Text")?.ToString()??"",Color=values["_color"].ToString(),Size=Number(values,"_size",12),SizeIsPixels=true});
    }
    public static Dictionary<string, object> Rect(RotatedRect rect) => new()
    {
        ["Center"] = XY(rect.Center.X, rect.Center.Y),
        ["Size"] = new Dictionary<string, object> { ["Width"] = (double)rect.Size.Width, ["Height"] = (double)rect.Size.Height },
        ["Rotation"] = (double)rect.Angle
    };
    public static void Point(OpContext ctx, ParamBag p, string driver, double x, double y, string attribute = null, int? view = null)
    {
        var values = XY(x, y); values["_marker"] = "cross";
        Add(ctx, p, driver, "point2d", values, attribute, view: view);
    }
    public static void ReferencePoints(OpContext ctx, ParamBag p, string driver)
    {
        var info = p.Group("Points Information");
        if (info == null) return;
        foreach (var (toggle, key) in new[] { ("Base Point", "Base Point"), ("Running Point", "Run Point") })
        {
            if (!p.Bool(toggle) || info.GetValueOrDefault(key) is not IReadOnlyDictionary<string, object> group
                || group.GetValueOrDefault("Point") is not IReadOnlyDictionary<string, object> point) continue;
            Point(ctx, p, driver, Number(point, "X"), Number(point, "Y"), toggle + " Display");
        }
    }
    internal static double Number(IReadOnlyDictionary<string, object> values, string key, double fallback = 0)
        => values != null && values.TryGetValue(key, out var v)
           && double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    public static void Segment(OpContext ctx,ParamBag p,string driver,string toggle,Point2d a,Point2d b)
    {if(p.Bool(toggle))Add(ctx,p,driver,"segment2d",new(){{"Start",XY(a.X,a.Y)},{"End",XY(b.X,b.Y)}},toggle+" Attribute");}
    public static void Distance(OpContext ctx,ParamBag p,string driver,Point2d a,Point2d b,double distance)
    {
        Segment(ctx,p,driver,"Segment Result",a,b);
        if(p.Bool("Distance Text"))Add(ctx,p,driver,"text2d",new(){{"Position",XY((a.X+b.X)/2,(a.Y+b.Y)/2)},{"Text",distance.ToString("0.###",CultureInfo.InvariantCulture)}},"Distance Text Attribute");
    }
    public static void Marker(OpContext ctx,ParamBag p,string driver,string toggle,Point2d point)
    {if(p.Bool(toggle))Point(ctx,p,driver,point.X,point.Y,toggle+" Attribute");}
    public static void Circle(OpContext ctx,ParamBag p,string driver,string toggle,Point2d center,double radius)
    {if(p.Bool(toggle)&&radius>0)Add(ctx,p,driver,"circle2d",new(){{"Center",XY(center.X,center.Y)},{"Radius",radius}},toggle+" Attribute");}
    public static void Ellipse(OpContext ctx,ParamBag p,string driver,string toggle,Point2d center,double rx,double ry,double angle)
    {if(p.Bool(toggle)&&rx>0&&ry>0)Add(ctx,p,driver,"ellipse2d",new(){{"Center",XY(center.X,center.Y)},{"Long Axis",rx*2},{"Short Axis",ry*2},{"Rotation",angle}},toggle+" Attribute");}
    public static void Line(OpContext ctx,ParamBag p,string driver,string toggle,Point2d position,Point2d direction)
    {if(p.Bool(toggle)&&direction.X*direction.X+direction.Y*direction.Y>1e-24)Add(ctx,p,driver,"line2d",new(){{"Position",XY(position.X,position.Y)},{"Direction",XY(direction.X,direction.Y)}},toggle+" Attribute");}
    public static void Text(OpContext ctx,ParamBag p,string driver,string toggle,Point2d position,string text,bool ok=true)
    {if(p.Bool(toggle))Add(ctx,p,driver,"text2d",new(){{"Position",XY(position.X,position.Y)},{"Text",text}},toggle+" Attribute",ok);}
    public static void Trace(OpContext ctx,ParamBag p,string driver,string toggle,IEnumerable<Point2d> points,bool ok=true)
    {
        if(!p.Bool(toggle))return;var array=points.ToArray();
        if(array.Length==1){var values=XY(array[0].X,array[0].Y);values["_marker"]="cross";Add(ctx,p,driver,"point2d",values,toggle+" Attribute",ok);}
        else if(array.Length>1)Add(ctx,p,driver,"trace2d",new(){{"Points",array.Select(q=>(object)XY(q.X,q.Y)).ToArray()}},toggle+" Attribute",ok);
    }
    public static void MaskOutline(OpContext ctx,ParamBag p,string driver,Mat mask,string toggle)
    {
        if(!p.Bool(toggle))return;Cv2.FindContours(mask,out var contours,out _,RetrievalModes.List,ContourApproximationModes.ApproxSimple);
        foreach(var contour in contours.Where(c=>c.Length>1))Add(ctx,p,driver,contour.Length>2?"polygon2d":"trace2d",new(){{"Points",contour.Select(q=>(object)XY(q.X,q.Y)).ToArray()}},toggle+" Attribute");
    }
}
