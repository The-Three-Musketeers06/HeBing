using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>几何参数解析与计算。</summary>
internal static class Geo
{
    private static double Num(IReadOnlyDictionary<string, object> d, string key, double def = 0)
    {
        if (d == null || !d.TryGetValue(key, out var v) || v == null) return def;
        return double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : def;
    }

    private static IReadOnlyDictionary<string, object> Sub(IReadOnlyDictionary<string, object> d, string key)
        => d != null && d.TryGetValue(key, out var v) && v is IReadOnlyDictionary<string, object> sub ? sub : null;

    public static Point2d Point(ParamBag p, string key)
    {
        if (p.Bound(key) is PointObj po) return new Point2d(po.X, po.Y);
        var g = p.Group(key);
        if (g == null) return new Point2d(0, 0);
        var inner = Sub(g, "point2d") ?? Sub(g, "Point") ?? g;
        return new Point2d(Num(inner, "X"), Num(inner, "Y"));
    }

    public static (Point2d a, Point2d b) Segment(ParamBag p, string key)
    {
        if (p.Bound(key) is SegmentObj so)
            return (new Point2d(so.X1, so.Y1), new Point2d(so.X2, so.Y2));
        var g = p.Group(key);
        if (g == null) return (new Point2d(0, 0), new Point2d(0, 0));
        var inner = Sub(g, "segment2d") ?? Sub(g, "line2d") ?? g;
        var s = Sub(inner, "Start") ?? inner;
        var e = Sub(inner, "End") ?? inner;
        return (new Point2d(Num(s, "X"), Num(s, "Y")), new Point2d(Num(e, "X"), Num(e, "Y")));
    }

    public static (Point2d c, double r) Circle(ParamBag p, string key)
    {
        if (p.Bound(key) is CircleObj co) return (new Point2d(co.CenterX, co.CenterY), co.Radius);
        var g = p.Group(key);
        if (g == null) return (new Point2d(0, 0), 0);
        var inner = Sub(g, "circle2d") ?? g;
        var c = Sub(inner, "Center") ?? inner;
        return (new Point2d(Num(c, "X"), Num(c, "Y")), Num(inner, "Radius"));
    }

    public static (Point2d c, double rx, double ry, double angle) Ellipse(ParamBag p, string key)
    {
        var g = p.Group(key);
        if (g == null) return (new Point2d(0, 0), 0, 0, 0);
        var inner = Sub(g, "ellipse2d") ?? g;
        var c = Sub(inner, "Center") ?? inner;
        var size = Sub(inner, "Size");
        return (new Point2d(Num(c, "X"), Num(c, "Y")),
            Num(inner,"Long Axis",Num(size, "Width")) / 2, Num(inner,"Short Axis",Num(size, "Height")) / 2, Num(inner,"Rotation",Num(inner, "Angle")));
    }

    public static List<Point2d> PointArray(ParamBag p, string key)
    {
        var list = new List<Point2d>();
        var raw=p.Raw(key);
        if(raw is IReadOnlyDictionary<string,object> group)raw=group.GetValueOrDefault("point2d array")??group.GetValueOrDefault("Points");
        if(raw is not IEnumerable<object> arr)return list;
        foreach (var item in arr)
            if (item is IReadOnlyDictionary<string, object> d) list.Add(new Point2d(Num(d, "X"), Num(d, "Y")));
            else if(item is PointObj point)list.Add(new(point.X,point.Y));
        return list;
    }

    public static double Dist(Point2d a, Point2d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static double AngleDeg(Point2d a, Point2d b) => Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;

    /// <summary>点到直线（无限延长）的距离与垂足。</summary>
    public static (double dist, Point2d foot) PointToLine(Point2d p, Point2d a, Point2d b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 == 0) return (Dist(p, a), a);
        var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
        var foot = new Point2d(a.X + t * dx, a.Y + t * dy);
        return (Dist(p, foot), foot);
    }

    /// <summary>两直线夹角（0~180）。</summary>
    public static double LineAngle(Point2d a1, Point2d b1, Point2d a2, Point2d b2)
    {
        var a = AngleDeg(a1, b1);
        var b = AngleDeg(a2, b2);
        var d = Math.Abs(a - b) % 180;
        return d > 90 ? 180 - d : d;
    }
}

/// <summary>测量类算子基类。</summary>
public abstract class MeasureOp : IOperator
{
    public abstract string Driver { get; }
    protected abstract bool Measure(OpContext ctx, ParamBag p, out string summary);

    public bool Run(OpContext ctx, ParamBag p)
    {
        DetectionOverlay2D.Begin(ctx,Driver);
        try
        {
            var ok = Measure(ctx, p, out var summary);
            ctx.Log($"{Driver}：{summary}", !ok);
            return ok;
        }
        catch (Exception ex)
        {
            ctx.Log($"{Driver} 失败：{ex.Message}", true);
            return false;
        }
    }

    protected static void Report(OpContext ctx, string key, double value) => ctx.Vars.Set($"{key}", value);
}

// ---------------- 12 个测量算子 ----------------

public class P2PMeasureOp : MeasureOp
{
    public override string Driver => "Basic P2P Measure";
    protected override bool Measure(OpContext ctx, ParamBag p, out string summary)
    {
        var a = Geo.Point(p, "Point1 Input");
        var b = Geo.Point(p, "Point2 Input");
        var d = Geo.Dist(a, b);
        var ang = Geo.AngleDeg(a, b);
        Report(ctx, "P2P.Distance", d);
        Report(ctx, "P2P.Angle", ang);
        Report(ctx, "P2P.MidX", (a.X + b.X) / 2);
        Report(ctx, "P2P.MidY", (a.Y + b.Y) / 2);
        DetectionOverlay2D.Segment(ctx,p,Driver,"Segment Result",a,b);
        if(p.Bool("Start Point"))DetectionOverlay2D.Point(ctx,p,Driver,a.X,a.Y,"Start Point Attribute");
        if(p.Bool("End Point"))DetectionOverlay2D.Point(ctx,p,Driver,b.X,b.Y,"End Point Attribute");
        summary = $"距离 {d:0.000}，角度 {ang:0.000}°";
        return true;
    }
}

public class P2LMeasureOp : MeasureOp
{
    public override string Driver => "Basic P2L Measure";
    protected override bool Measure(OpContext ctx, ParamBag p, out string summary)
    {
        var pt = Geo.Point(p, "Point Input");
        var (a, b) = Geo.Segment(p, "Line Input");
        var (d, foot) = Geo.PointToLine(pt, a, b);
        Report(ctx, "P2L.Distance", d);
        Report(ctx, "P2L.FootX", foot.X);
        Report(ctx, "P2L.FootY", foot.Y);
        DetectionOverlay2D.Segment(ctx,p,Driver,"Line Segment",a,b);
        DetectionOverlay2D.Distance(ctx,p,Driver,pt,foot,d);
        if(p.Bool("Start Point"))DetectionOverlay2D.Point(ctx,p,Driver,pt.X,pt.Y,"Start Point Attribute");
        if(p.Bool("Foot Point"))DetectionOverlay2D.Point(ctx,p,Driver,foot.X,foot.Y,"Foot Point Attribute");
        var length=Geo.Dist(a,b);
        if(p.Bool("Through Line")&&length>0)DetectionOverlay2D.Add(ctx,p,Driver,"line2d",new(){{"Position",DetectionOverlay2D.XY(a.X,a.Y)},{"Direction",DetectionOverlay2D.XY((b.X-a.X)/length,(b.Y-a.Y)/length)}},"Through Line Attribute");
        summary = $"距离 {d:0.000}，垂足 ({foot.X:0.000},{foot.Y:0.000})";
        return true;
    }
}

public class P2CMeasureOp : MeasureOp
{
    public override string Driver => "Basic P2C Measure";
    protected override bool Measure(OpContext ctx, ParamBag p, out string summary)
    {
        var pt = Geo.Point(p, "Point Input");
        var (c, r) = Geo.Circle(p, "Circle Input");
        var d = Geo.Dist(pt, c) - r;
        Report(ctx, "P2C.Distance", d);
        if(r<=0)throw new ArgumentException("圆半径无效");
        var length=Geo.Dist(pt,c);var nearest=length>1e-12?new Point2d(c.X+(pt.X-c.X)*r/length,c.Y+(pt.Y-c.Y)*r/length):new Point2d(c.X+r,c.Y);
        DetectionOverlay2D.Distance(ctx,p,Driver,pt,nearest,d);DetectionOverlay2D.Marker(ctx,p,Driver,"Start Point",pt);DetectionOverlay2D.Marker(ctx,p,Driver,"Circle Center",c);DetectionOverlay2D.Circle(ctx,p,Driver,"Circle",c,r);
        summary = $"点圆心距 {Geo.Dist(pt, c):0.000}，距圆周 {d:0.000}";
        return true;
    }
}

public class P2EMeasureOp : MeasureOp
{
    public override string Driver => "Basic P2E Measure";
    protected override bool Measure(OpContext ctx, ParamBag p, out string summary)
    {
        var pt = Geo.Point(p, "Point Input");
        var (c, rx, ry, rotation) = Geo.Ellipse(p, "Ellipse Input");
        if (rx <= 0 || ry <= 0) { summary = "椭圆半径无效"; return false; }
        var nearest=Geometry2DMath.ClosestEllipse(pt,c,rx,ry,rotation);var d=Geo.Dist(pt,nearest);
        Report(ctx, "P2E.Distance", d);
        DetectionOverlay2D.Distance(ctx,p,Driver,pt,nearest,d);DetectionOverlay2D.Marker(ctx,p,Driver,"Start Point",pt);DetectionOverlay2D.Marker(ctx,p,Driver,"Ellipse Center",c);DetectionOverlay2D.Ellipse(ctx,p,Driver,"Ellipse",c,rx,ry,rotation);
        summary = $"点到椭圆距离 {d:0.000}";
        return true;
    }
}

public class C2CMeasureOp : MeasureOp
{
    public override string Driver => "Basic C2C Measure";
    protected override bool Measure(OpContext ctx, ParamBag p, out string summary)
    {
        var (c1, r1) = Geo.Circle(p, "Circle 1 Input");
        var (c2, r2) = Geo.Circle(p, "Circle 2 Input");
        var d = Geo.Dist(c1, c2);
        Report(ctx, "C2C.Distance", d);
        Report(ctx, "C2C.Gap", d - r1 - r2);
        DetectionOverlay2D.Distance(ctx,p,Driver,c1,c2,d);
        foreach(var (n,c,r) in new[]{(1,c1,r1),(2,c2,r2)})
        {
            if(p.Bool("Circle"+n))DetectionOverlay2D.Add(ctx,p,Driver,"circle2d",new(){{"Center",DetectionOverlay2D.XY(c.X,c.Y)},{"Radius",r}},"Circle"+n+" Attribute");
            if(p.Bool("Circle Center"+n))DetectionOverlay2D.Point(ctx,p,Driver,c.X,c.Y,"Circle Center"+n+" Attribute");
        }
        if(d>0&&d<=r1+r2&&d>=Math.Abs(r1-r2))
        {
            var along=(r1*r1-r2*r2+d*d)/(2*d);var height=Math.Sqrt(Math.Max(0,r1*r1-along*along));
            var x=c1.X+along*(c2.X-c1.X)/d;var y=c1.Y+along*(c2.Y-c1.Y)/d;
            if(p.Bool("Intersection Point1"))DetectionOverlay2D.Point(ctx,p,Driver,x-height*(c2.Y-c1.Y)/d,y+height*(c2.X-c1.X)/d,"Intersection Point1 Attribute");
            if(p.Bool("Intersection Point2"))DetectionOverlay2D.Point(ctx,p,Driver,x+height*(c2.Y-c1.Y)/d,y-height*(c2.X-c1.X)/d,"Intersection Point2 Attribute");
        }
        summary = $"圆心距 {d:0.000}，间隙 {d - r1 - r2:0.000}";
        return true;
    }
}

public class L2LMeasureOp : MeasureOp
{
    public override string Driver => "Basic L2L Measure";
    protected override bool Measure(OpContext ctx, ParamBag p, out string summary)
    {
        var (a1, b1) = Geo.Segment(p, "Line 1 Input");
        var (a2, b2) = Geo.Segment(p, "Line 2 Input");
        var d1 = Geo.PointToLine(a2, a1, b1).dist;
        var d2 = Geo.PointToLine(b2, a1, b1).dist;
        var mid = new Point2d((a2.X + b2.X) / 2, (a2.Y + b2.Y) / 2);
        var d = Geo.PointToLine(mid, a1, b1).dist;
        Report(ctx, "L2L.Distance", d);
        Report(ctx, "L2L.DistanceStart", d1);
        Report(ctx, "L2L.DistanceEnd", d2);
        DetectionOverlay2D.Segment(ctx,p,Driver,"Line1",a1,b1);DetectionOverlay2D.Segment(ctx,p,Driver,"Line2",a2,b2);
        if(Geometry2DMath.Intersection(a1,b1,a2,b2,out var intersection))DetectionOverlay2D.Marker(ctx,p,Driver,"Intersection",intersection);
        summary = $"距离 {d:0.000}（端点到线 {d1:0.000} / {d2:0.000}）";
        return true;
    }
}

public class L2LAngleOp : MeasureOp
{
    public override string Driver => "Basic L2L Angle";
    protected override bool Measure(OpContext ctx, ParamBag p, out string summary)
    {
        var (a1, b1) = Geo.Segment(p, "Line 1 Input");
        var (a2, b2) = Geo.Segment(p, "Line 2 Input");
        var ang = Geo.LineAngle(a1, b1, a2, b2);
        Report(ctx, "L2L.Angle", ang);
        Report(ctx, "L2L.Angle1", Geo.AngleDeg(a1, b1));
        Report(ctx, "L2L.Angle2", Geo.AngleDeg(a2, b2));
        DetectionOverlay2D.Segment(ctx,p,Driver,"Line 1",a1,b1);DetectionOverlay2D.Segment(ctx,p,Driver,"Line 2",a2,b2);
        summary = $"夹角 {ang:0.000}°";
        return true;
    }
}

public class L2EMeasureOp : MeasureOp
{
    public override string Driver => "Basic L2E Measure";
    protected override bool Measure(OpContext ctx, ParamBag p, out string summary)
    {
        var (a, b) = Geo.Segment(p, "Line Input");
        var (c, rx, ry, rotation) = Geo.Ellipse(p, "Ellipse Input");if(rx<=0||ry<=0)throw new ArgumentException("椭圆半轴无效");
        var nearest=Geometry2DMath.EllipseToLine(a,b,c,rx,ry,rotation);var d=nearest.distance;
        Report(ctx, "L2E.Distance", d);
        DetectionOverlay2D.Segment(ctx,p,Driver,"Line Segment",a,b);DetectionOverlay2D.Ellipse(ctx,p,Driver,"Ellipse",c,rx,ry,rotation);DetectionOverlay2D.Marker(ctx,p,Driver,"Ellipse Center",c);
        foreach(var point in Geometry2DMath.LineEllipse(a,b,c,rx,ry,rotation))DetectionOverlay2D.Marker(ctx,p,Driver,"Intersection Points",point);
        DetectionOverlay2D.Text(ctx,p,Driver,"Distance Text",new((nearest.point.X+nearest.foot.X)/2,(nearest.point.Y+nearest.foot.Y)/2),d.ToString("0.###",CultureInfo.InvariantCulture));
        summary = $"线到椭圆距离 {d:0.000}";
        return true;
    }
}

public sealed class L2CMeasureOp:MeasureOp
{
    public override string Driver=>"Basic L2C Measure";
    protected override bool Measure(OpContext ctx,ParamBag p,out string summary)
    {
        var (a,b)=Geo.Segment(p,"Line Input");var (center,radius)=Geo.Circle(p,"Circle Input");
        if(Geo.Dist(a,b)<1e-12||radius<=0)throw new ArgumentException("直线或圆无效");
        var projected=Geo.PointToLine(center,a,b);var distance=Math.Max(0,projected.dist-radius);
        var nearest=projected.dist>1e-12?new Point2d(center.X+(projected.foot.X-center.X)*radius/projected.dist,center.Y+(projected.foot.Y-center.Y)*radius/projected.dist):new Point2d(center.X+radius,center.Y);
        Report(ctx,"L2C.Distance",distance);Report(ctx,"L2C.CenterDistance",projected.dist);
        DetectionOverlay2D.Segment(ctx,p,Driver,"Line Segment",a,b);DetectionOverlay2D.Line(ctx,p,Driver,"Through Line",a,new(b.X-a.X,b.Y-a.Y));DetectionOverlay2D.Circle(ctx,p,Driver,"Circle",center,radius);DetectionOverlay2D.Marker(ctx,p,Driver,"Circle Center",center);DetectionOverlay2D.Marker(ctx,p,Driver,"Foot Point",projected.foot);
        var crossings=Geometry2DMath.LineEllipse(a,b,center,radius,radius,0);
        DetectionOverlay2D.Distance(ctx,p,Driver,crossings.Length>0?crossings[0]:nearest,crossings.Length>0?crossings[0]:projected.foot,distance);
        for(var i=0;i<crossings.Length;i++)DetectionOverlay2D.Marker(ctx,p,Driver,"Intersection Point"+(i+1),crossings[i]);
        summary=$"线圆距离 {distance:0.###}";return true;
    }
}

public class P2PMidOp : MeasureOp
{
    public override string Driver => "Basic P2P Mid";
    protected override bool Measure(OpContext ctx, ParamBag p, out string summary)
    {
        var a = Geo.Point(p, "Point1 Input");
        var b = Geo.Point(p, "Point2 Input");
        var mx = (a.X + b.X) / 2;
        var my = (a.Y + b.Y) / 2;
        Report(ctx, "P2PMid.X", mx);
        Report(ctx, "P2PMid.Y", my);
        Report(ctx, "P2PMid.Distance", Geo.Dist(a, b));
        ctx.Vars.Set("Mid Point",new PointObj{X=mx,Y=my});
        DetectionOverlay2D.Marker(ctx,p,Driver,"Point 1",a);DetectionOverlay2D.Marker(ctx,p,Driver,"Point 2",b);DetectionOverlay2D.Marker(ctx,p,Driver,"Mid Point",new(mx,my));
        summary = $"中点 ({mx:0.000},{my:0.000})，距离 {Geo.Dist(a, b):0.000}";
        return true;
    }
}

public class PixelStatisticsOp : MeasureOp
{
    public override string Driver => "Basic Pixel Statistics";
    protected override bool Measure(OpContext ctx, ParamBag p, out string summary)
    {
        var src = ctx.Images.Get(p.Int("IM In", 0));
        if (src == null) { summary = "输入图像为空"; return false; }
        using var gray = Img.ToGray8(src);using var mask=ImageRegions2D.Mask(ctx,p,gray.Size());
        if(Cv2.CountNonZero(mask)==0)throw new ArgumentException("检测区域没有有效像素");
        Cv2.MinMaxLoc(gray, out var min, out var max, out var minLoc, out var maxLoc,mask);
        Cv2.MeanStdDev(gray, out var mean, out var std,mask);
        DetectionOverlay2D.MaskOutline(ctx,p,Driver,mask,"Detection Area");DetectionOverlay2D.ReferencePoints(ctx,p,Driver);
        Report(ctx, "Pixel.Min", min);
        Report(ctx, "Pixel.Max", max);
        Report(ctx, "Pixel.Mean", mean.Val0);
        Report(ctx, "Pixel.Std", std.Val0);
        summary = $"Min {min} Max {max} Mean {mean.Val0:0.00} Std {std.Val0:0.00}";
        return true;
    }
}

public class ClarityOp : MeasureOp
{
    public override string Driver => "Basic Clarity";
    protected override bool Measure(OpContext ctx, ParamBag p, out string summary)
    {
        var src = ctx.Images.Get(p.Int("IM In", 0));
        if (src == null) { summary = "输入图像为空"; return false; }
        using var gray = Img.ToGray8(src);
        using var mask=ImageRegions2D.Mask(ctx,p,gray.Size());if(Cv2.CountNonZero(mask)==0)throw new ArgumentException("检测区域没有有效像素");
        using var lap = new Mat();
        Cv2.Laplacian(gray, lap, MatType.CV_64F);
        Cv2.MeanStdDev(lap, out _, out var std,mask);
        var clarity = std.Val0 * std.Val0;
        Report(ctx, "Clarity.Value", clarity);
        DetectionOverlay2D.MaskOutline(ctx,p,Driver,mask,"Detection Area");DetectionOverlay2D.ReferencePoints(ctx,p,Driver);
        summary = $"清晰度 {clarity:0.00}";
        return true;
    }
}

public class EdgeWidthOp : MeasureOp
{
    public override string Driver => "Basic Edge Width";
    protected override bool Measure(OpContext ctx, ParamBag p, out string summary)
        =>EdgePairResults2D.Measure(ctx,p,Driver,out summary);
}
