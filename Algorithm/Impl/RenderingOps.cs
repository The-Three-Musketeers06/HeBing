using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

// ---------------- 拟合 ----------------

public class LineFitOp : IOperator
{
    public string Driver => "Basic Line Fit";

    public bool Run(OpContext ctx, ParamBag p)
    {
        DetectionOverlay2D.Begin(ctx,Driver);
        var pts = Geo.PointArray(p, "Points Array");
        if (pts.Count < 2) { ctx.Log("拟合直线：点数不足", true); return false; }
        var fit = FitLine(pts);
        var ang = Math.Atan2(fit.vy, fit.vx) * 180 / Math.PI;
        ctx.Vars.Set("LineFit.StartX", fit.x);
        ctx.Vars.Set("LineFit.StartY", fit.y);
        ctx.Vars.Set("LineFit.Vx", fit.vx);
        ctx.Vars.Set("LineFit.Vy", fit.vy);
        ctx.Vars.Set("LineFit.Angle", ang);
        ctx.Vars.Set("LineFit.Count", pts.Count);
        var origin=new Point2d(fit.x,fit.y);var direction=new Point2d(fit.vx,fit.vy);
        var projected=pts.Select(q=>(q.X-fit.x)*fit.vx+(q.Y-fit.y)*fit.vy).ToArray();
        var start=new Point2d(fit.x+projected.Min()*fit.vx,fit.y+projected.Min()*fit.vy);var end=new Point2d(fit.x+projected.Max()*fit.vx,fit.y+projected.Max()*fit.vy);
        ctx.Vars.Set("Segment",new SegmentObj{X1=start.X,Y1=start.Y,X2=end.X,Y2=end.Y});
        DetectionOverlay2D.Line(ctx,p,Driver,"Through Line",origin,direction);DetectionOverlay2D.Segment(ctx,p,Driver,"Fitted Line",start,end);
        foreach(var point in pts)DetectionOverlay2D.Marker(ctx,p,Driver,"Input Point",point);
        ctx.Log($"拟合直线：{pts.Count} 点，角度 {ang:0.000}°，过点 ({fit.x:0.00},{fit.y:0.00})", false);
        return true;
    }

    /// <summary>总体最小二乘（PCA）直线拟合，返回过质心的方向。</summary>
    internal static (double x, double y, double vx, double vy) FitLine(IReadOnlyList<Point2d> pts)
    {
        double mx = 0, my = 0;
        foreach (var q in pts) { mx += q.X; my += q.Y; }
        mx /= pts.Count; my /= pts.Count;
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var q in pts)
        {
            var dx = q.X - mx;
            var dy = q.Y - my;
            sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
        }
        var theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        return (mx, my, Math.Cos(theta), Math.Sin(theta));
    }
}

public class CircleFitOp : IOperator
{
    public string Driver => "Basic Circle Fit";

    public bool Run(OpContext ctx, ParamBag p)
    {
        DetectionOverlay2D.Begin(ctx,Driver);
        var pts = Geo.PointArray(p, "Points Array");
        if (pts.Count < 3) { ctx.Log("拟合圆：点数不足", true); return false; }

        var (cx, cy, r) = Fit(pts);
        if (double.IsNaN(cx)) { ctx.Log("拟合圆：求解失败", true); return false; }
        ctx.Vars.Set("CircleFit.CenterX", cx);
        ctx.Vars.Set("CircleFit.CenterY", cy);
        ctx.Vars.Set("CircleFit.Radius", r);
        ctx.Vars.Set("CircleFit.Count", pts.Count);
        ctx.Vars.Set("Circle",new CircleObj{CenterX=cx,CenterY=cy,Radius=r});ctx.Vars.Set("Center",new PointObj{X=cx,Y=cy});
        DetectionOverlay2D.Circle(ctx,p,Driver,"Circle",new(cx,cy),r);DetectionOverlay2D.Marker(ctx,p,Driver,"Circle Center",new(cx,cy));
        foreach(var point in pts)DetectionOverlay2D.Marker(ctx,p,Driver,"Input Point",point);
        ctx.Log($"拟合圆：圆心 ({cx:0.000},{cy:0.000}) R={r:0.000}（{pts.Count} 点）", false);
        return true;
    }

    /// <summary>Kasa 最小二乘圆拟合。</summary>
    internal static (double cx, double cy, double r) Fit(IReadOnlyList<Point2d> pts)
    {
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0, sz = 0;
        foreach (var q in pts)
        {
            var z = q.X * q.X + q.Y * q.Y;
            sx += q.X; sy += q.Y; sxx += q.X * q.X; syy += q.Y * q.Y;
            sxy += q.X * q.Y; sxz += q.X * z; syz += q.Y * z; sz += z;
        }
        int n = pts.Count;
        double[,] a = { { sx, sy, n }, { sxx, sxy, sx }, { sxy, syy, sy } };
        double[] b = { sz, sxz, syz };
        var sol = Solve3(a, b);
        if (sol == null) return (double.NaN, double.NaN, 0);
        var cx = sol[0] / 2;
        var cy = sol[1] / 2;
        var r = Math.Sqrt(Math.Max(0, sol[2] + cx * cx + cy * cy));
        return (cx, cy, r);
    }

    private static double[] Solve3(double[,] a, double[] b)
    {
        var m = new double[3, 4];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++) m[i, j] = a[i, j];
            m[i, 3] = b[i];
        }
        for (int i = 0; i < 3; i++)
        {
            var piv = i;
            for (int k = i + 1; k < 3; k++) if (Math.Abs(m[k, i]) > Math.Abs(m[piv, i])) piv = k;
            if (Math.Abs(m[piv, i]) < 1e-12) return null;
            if (piv != i)
                for (int j = 0; j < 4; j++) (m[i, j], m[piv, j]) = (m[piv, j], m[i, j]);
            for (int k = 0; k < 3; k++)
            {
                if (k == i) continue;
                var f = m[k, i] / m[i, i];
                for (int j = i; j < 4; j++) m[k, j] -= f * m[i, j];
            }
        }
        return new[] { m[0, 3] / m[0, 0], m[1, 3] / m[1, 1], m[2, 3] / m[2, 2] };
    }
}

public class EllipseFitOp : IOperator
{
    public string Driver => "Basic Ellipse Fit";

    public bool Run(OpContext ctx, ParamBag p)
    {
        DetectionOverlay2D.Begin(ctx,Driver);
        var pts = Geo.PointArray(p, "Points Array");
        if (pts.Count < 5) { ctx.Log("拟合椭圆：点数不足（至少 5 点）", true); return false; }
        var e = Cv2.FitEllipse(pts.Select(q => new Point2f((float)q.X, (float)q.Y)));
        ctx.Vars.Set("EllipseFit.CenterX", e.Center.X);
        ctx.Vars.Set("EllipseFit.CenterY", e.Center.Y);
        ctx.Vars.Set("EllipseFit.RadiusX", e.Size.Width / 2);
        ctx.Vars.Set("EllipseFit.RadiusY", e.Size.Height / 2);
        ctx.Vars.Set("EllipseFit.Angle", e.Angle);
        ctx.Vars.Set("Ellipse",new Dictionary<string,object>{{"Center",DetectionOverlay2D.XY(e.Center.X,e.Center.Y)},{"Long Axis",(double)e.Size.Width},{"Short Axis",(double)e.Size.Height},{"Rotation",(double)e.Angle}});
        DetectionOverlay2D.Ellipse(ctx,p,Driver,"Ellipse",new(e.Center.X,e.Center.Y),e.Size.Width/2,e.Size.Height/2,e.Angle);
        DetectionOverlay2D.Marker(ctx,p,Driver,"Ellipse Center",new(e.Center.X,e.Center.Y));foreach(var point in pts)DetectionOverlay2D.Marker(ctx,p,Driver,"Input Point",point);
        ctx.Log($"拟合椭圆：中心 ({e.Center.X:0.00},{e.Center.Y:0.00}) 半径 {e.Size.Width / 2:0.00}×{e.Size.Height / 2:0.00} 角度 {e.Angle:0.000}°", false);
        return true;
    }
}

// ---------------- 几何构造 ----------------

/// <summary>1D Line：卡尺找边 + 拟合直线。</summary>
public class CaliperLineOp : IOperator
{
    public string Driver => "1D Line";

    public bool Run(OpContext ctx, ParamBag p)
    {
        DetectionOverlay2D.Begin(ctx,Driver);
        var src = ctx.Images.Get(p.Int("IM In", 0));
        if (src == null) { ctx.Log("1D直线：输入图像为空", true); return false; }
        var cal = p.Group("Caliper");
        if (cal == null) { ctx.Log("1D直线：未设置 Caliper", true); return false; }

        double Get(IReadOnlyDictionary<string, object> d, string k, double def)
            => d.TryGetValue(k, out var v) && v != null &&
               double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any,
                   System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : def;
        IReadOnlyDictionary<string, object> Sub(IReadOnlyDictionary<string, object> d, string k)
            => d.TryGetValue(k, out var v) && v is IReadOnlyDictionary<string, object> s ? s : null;

        var s = Sub(cal, "Start");
        var e = Sub(cal, "End");
        if (s == null || e == null) { ctx.Log("1D直线：Caliper 起点/终点缺失", true); return false; }
        var p1 = new Point2d(Get(s, "X", 0), Get(s, "Y", 0));
        var p2 = new Point2d(Get(e, "X", 0), Get(e, "Y", 0));
        var dir = cal.TryGetValue("Direction", out var dv) ? dv?.ToString() : "Down";
        var threshold = P.D(p, 50, "Threshold");
        var sigma = P.D(p, 2, "Sigma");

        using var gray = Img.ToGray8(src);
        using var blur = new Mat();
        Cv2.GaussianBlur(gray, blur, new Size(0, 0), Math.Max(0.5, sigma));

        var len = Geo.Dist(p1, p2);
        if (len < 1) { ctx.Log("1D直线：卡尺长度无效", true); return false; }

        // 搜索方向（垂直于卡尺）
        var dx = (p2.X - p1.X) / len;
        var dy = (p2.Y - p1.Y) / len;
        var (nx, ny) = dir?.ToLowerInvariant() switch
        {
            "up" => (0.0, -1.0),
            "left" => (-1.0, 0.0),
            "right" => (1.0, 0.0),
            "down" => (0.0, 1.0),
            _ => (dy, -dx) // 沿卡尺法向
        };

        const int search = 20;
        var edgePoints = new List<Point2d>();
        int samples = Math.Max(4, (int)len / 4);

        for (int i = 0; i <= samples; i++)
        {
            var t = i / (double)samples;
            var bx = p1.X + (p2.X - p1.X) * t;
            var by = p1.Y + (p2.Y - p1.Y) * t;
            double bestGrad = 0, bestOffset = 0;
            for (int k = -search; k <= search; k++)
            {
                var x = (int)Math.Round(bx + nx * k);
                var y = (int)Math.Round(by + ny * k);
                if (x < 1 || y < 1 || x >= blur.Width - 1 || y >= blur.Height - 1) continue;
                var g = Math.Abs((int)blur.At<byte>(y, x + 1) - blur.At<byte>(y, x - 1))
                      + Math.Abs((int)blur.At<byte>(y + 1, x) - blur.At<byte>(y - 1, x));
                if (g > bestGrad) { bestGrad = g; bestOffset = k; }
            }
            if (bestGrad >= threshold)
                edgePoints.Add(new Point2d(bx + nx * bestOffset, by + ny * bestOffset));
        }

        if (edgePoints.Count < 2) { ctx.Log($"1D直线：有效边缘点 {edgePoints.Count} 个，未找到直线", true); return false; }

        var fit = LineFitOp.FitLine(edgePoints);
        var ang = Math.Atan2(fit.vy, fit.vx) * 180 / Math.PI;
        ctx.Vars.Set("1DLine.StartX", fit.x);
        ctx.Vars.Set("1DLine.StartY", fit.y);
        ctx.Vars.Set("1DLine.Vx", fit.vx);
        ctx.Vars.Set("1DLine.Vy", fit.vy);
        ctx.Vars.Set("1DLine.Angle", ang);
        ctx.Vars.Set("1DLine.Count", edgePoints.Count);
        var positions=edgePoints.Select(q=>(q.X-fit.x)*fit.vx+(q.Y-fit.y)*fit.vy).ToArray();var a=new Point2d(fit.x+positions.Min()*fit.vx,fit.y+positions.Min()*fit.vy);var b=new Point2d(fit.x+positions.Max()*fit.vx,fit.y+positions.Max()*fit.vy);
        ctx.Vars.Set("Segment",new SegmentObj{X1=a.X,Y1=a.Y,X2=b.X,Y2=b.Y});
        DetectionOverlay2D.Segment(ctx,p,Driver,"Segment",a,b);DetectionOverlay2D.Marker(ctx,p,Driver,"Mid Point",new((a.X+b.X)/2,(a.Y+b.Y)/2));DetectionOverlay2D.Marker(ctx,p,Driver,"End Point",a);DetectionOverlay2D.Marker(ctx,p,Driver,"End Point",b);
        ctx.Log($"1D直线：边缘点 {edgePoints.Count}，角度 {ang:0.000}°，过点 ({fit.x:0.00},{fit.y:0.00})", false);
        return true;
    }
}

/// <summary>两条线/点的几何构造（中垂线、平行线、垂线、角平分线）。</summary>
public class GeometryConstructOp : IOperator
{
    private readonly string _driver;
    private readonly string _kind;

    public GeometryConstructOp(string driver, string kind) { _driver = driver; _kind = kind; }
    public string Driver => _driver;

    public bool Run(OpContext ctx, ParamBag p)
    {
        DetectionOverlay2D.Begin(ctx,Driver);
        (Point2d a,Point2d b) Read(params string[] keys)=>Geo.Segment(p,keys.FirstOrDefault(p.Has)??keys[0]);
        Point2d a,b;
        string prefix;
        if(_kind=="bisector")
        {
            var first=Read("Segment1 Input","Line 1 Input");var second=Read("Segment2 Input","Line 2 Input");
            if(!Geometry2DMath.Intersection(first.a,first.b,second.a,second.b,out a))throw new InvalidOperationException("角平分线输入平行或长度无效");
            var angle=(Geo.AngleDeg(first.a,first.b)+Geo.AngleDeg(second.a,second.b))*Math.PI/360;var length=Math.Max(Geo.Dist(first.a,first.b),Geo.Dist(second.a,second.b));b=new(a.X+length*Math.Cos(angle),a.Y+length*Math.Sin(angle));prefix="Bisector";
            DetectionOverlay2D.Segment(ctx,p,Driver,"Segment1 Overlay",first.a,first.b);DetectionOverlay2D.Segment(ctx,p,Driver,"Segment2 Overlay",second.a,second.b);DetectionOverlay2D.Segment(ctx,p,Driver,"Angle Bisector",a,b);DetectionOverlay2D.Marker(ctx,p,Driver,"Intersection Point",a);
        }
        else if(_kind=="middle")
        {
            var first=Read("First Line","Line 1 Input");var second=Read("Second Line","Line 2 Input");
            if((first.b.X-first.a.X)*(second.b.X-second.a.X)+(first.b.Y-first.a.Y)*(second.b.Y-second.a.Y)<0)second=(second.b,second.a);
            a=new((first.a.X+second.a.X)/2,(first.a.Y+second.a.Y)/2);b=new((first.b.X+second.b.X)/2,(first.b.Y+second.b.Y)/2);prefix="MiddleLine";
            DetectionOverlay2D.Segment(ctx,p,Driver,"First Line Overlay",first.a,first.b);DetectionOverlay2D.Segment(ctx,p,Driver,"Second Line Overlay",second.a,second.b);DetectionOverlay2D.Segment(ctx,p,Driver,"Middle Line Overlay",a,b);
        }
        else
        {
            var input=Read("Segment Input","Line Input");var length=Geo.Dist(input.a,input.b);if(length<1e-12)throw new InvalidOperationException("输入线段长度无效");
            var nx=-(input.b.Y-input.a.Y)/length;var ny=(input.b.X-input.a.X)/length;var point=Geo.Point(p,"Point Input");
            DetectionOverlay2D.Segment(ctx,p,Driver,"Segment Overlay",input.a,input.b);DetectionOverlay2D.Marker(ctx,p,Driver,"Point Overlay",point);
            if(_kind=="parallel")
            {
                var offset=P.D(p,10,"Offset","Distance");
                if(p.Str("Calculation Type").Contains("point",StringComparison.OrdinalIgnoreCase))offset=(point.X-input.a.X)*nx+(point.Y-input.a.Y)*ny;
                a=new(input.a.X+offset*nx,input.a.Y+offset*ny);b=new(input.b.X+offset*nx,input.b.Y+offset*ny);prefix="Parallel";
                DetectionOverlay2D.Segment(ctx,p,Driver,"Segment1 Result Overlay",a,b);DetectionOverlay2D.Segment(ctx,p,Driver,"Segment2 Result Overlay",new(input.a.X-offset*nx,input.a.Y-offset*ny),new(input.b.X-offset*nx,input.b.Y-offset*ny));
            }
            else
            {
                var foot=Geo.PointToLine(point,input.a,input.b).foot;a=point;b=foot;if(Geo.Dist(a,b)<1e-12)b=new(a.X+length*nx,a.Y+length*ny);prefix="Perp";
                DetectionOverlay2D.Marker(ctx,p,Driver,"Perpendicular Foot",foot);
            }
            DetectionOverlay2D.Segment(ctx,p,Driver,"Segment Result Overlay",a,b);
        }
        if(Geo.Dist(a,b)<1e-12)throw new InvalidOperationException("结果线段长度为零");
        ctx.Vars.Set("Segment",new SegmentObj{X1=a.X,Y1=a.Y,X2=b.X,Y2=b.Y});
        ctx.Vars.Set(prefix+".StartX",a.X);ctx.Vars.Set(prefix+".StartY",a.Y);ctx.Vars.Set(prefix+".EndX",b.X);ctx.Vars.Set(prefix+".EndY",b.Y);ctx.Vars.Set(prefix+".X",a.X);ctx.Vars.Set(prefix+".Y",a.Y);ctx.Vars.Set(prefix+".Angle",Geo.AngleDeg(a,b));
        ctx.Log(Driver+"：已发布结果线段",false);return true;
    }
}

// ---------------- ROI / 匹配 ----------------

/// <summary>创建 ROI：把矩形/圆/扇区/多边形/环区域画成 mask。</summary>
public class CreateRoiOp : IOperator
{
    private readonly string _driver;
    public CreateRoiOp(string driver = "Basic Create ROI") => _driver = driver;
    public string Driver => _driver;

    public bool Run(OpContext ctx, ParamBag p)
    {
        // 3D 版：把 Region 解析成窗口数组，供后续算子绑定引用
        if (_driver == "CreateROI")
        {
            if (!p.Str("Mode", "region").Equals("region", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("CreateROI：尚未实现从配方创建区域");
            var reference = p.Group("Reference");
            var regions = CloudRegionSelector.SplitRegions(p.Raw("Region"))
                .Select(region => (object)new Dictionary<string, object> { ["Region"] = region, ["Reference"] = reference }).ToList();
            // Validate the geometry without requiring a loaded cloud or changing its shape.
            CloudRegionSelector.SelectGroups(new PointCloud(), regions, ctx);
            ctx.Vars.Set("ROI", regions);
            ctx.Vars.Set("ROI.Count", regions.Count);
            ctx.Log($"创建 ROI：{regions.Count} 个窗口", false);
            return regions.Count > 0;
        }

        DetectionOverlay2D.Begin(ctx,Driver);
        var src=ctx.Images.Get(p.Int("IM In"));var size=src?.Size()??new Size(640,480);
        var mask=new Mat(size,MatType.CV_8UC1,Scalar.Black);var shapes=0;
        try
        {
            foreach(var (name,type) in new[]{("Rect","rect2d"),("Circle","circle2d"),("Polygon","polygon2d"),("Sector","sector2d"),("Ring","ring2d")})
            {
                if(!p.Bool("Enable "+name+" Region")||p.Group(name+" Region")?.GetValueOrDefault(type+" array") is not IEnumerable<object> regions)continue;
                foreach(var shape in regions.OfType<IReadOnlyDictionary<string,object>>())
                {
                    using var part=BlobDetectOp.RegionMask(size,new Dictionary<string,object>{{"Type",type},{type,shape}},p);Cv2.BitwiseOr(mask,part,mask);shapes++;
                }
            }
            DetectionOverlay2D.MaskOutline(ctx,p,Driver,mask,"Display ROI");
            ctx.SetImage(p.Int("IM Out",1),mask);
        }
        catch{mask.Dispose();throw;}
        ctx.Vars.Set("ROI.ShapeCount", shapes);
        ctx.Log($"创建 ROI：{shapes} 个区域（{size.Width}x{size.Height}）", false);
        return true;
    }
}

/// <summary>两条直线的交点（2D Line Intersection）。</summary>
public class LineIntersectionOp : IOperator
{
    public string Driver => "2D Line Intersection";

    private static (Point2d a, Point2d b) Seg(ParamBag p, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (p.Bound(k) is SegmentObj) return Geo.Segment(p, k);
            if (p.Group(k) != null) return Geo.Segment(p, k);
        }
        return (new Point2d(0, 0), new Point2d(0, 0));
    }

    public bool Run(OpContext ctx, ParamBag p)
    {
        DetectionOverlay2D.Begin(ctx,Driver);
        // 原版参数名是 First Line / Second Line；其它版本用 Line 1 Input 等
        var (a1, b1) = Seg(p, "First Line", "Line 1 Input", "Line 1", "Caliper 1", "Line A");
        var (a2, b2) = Seg(p, "Second Line", "Line 2 Input", "Line 2", "Caliper 2", "Line B");
        if (Geo.Dist(a1, b1) < 1e-9 || Geo.Dist(a2, b2) < 1e-9)
        {
            ctx.Log($"直线交点：直线长度无效（L1={Geo.Dist(a1, b1):0.###} L2={Geo.Dist(a2, b2):0.###} 绑定={p.Bindings.Count}）", true);
            return false;
        }

        double x1 = a1.X, y1 = a1.Y, x2 = b1.X, y2 = b1.Y;
        double x3 = a2.X, y3 = a2.Y, x4 = b2.X, y4 = b2.Y;
        var den = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
        if (Math.Abs(den) < 1e-9)
        {
            ctx.Log("直线交点：两线平行，无交点", true);
            return false;
        }
        var px = ((x1 * y2 - y1 * x2) * (x3 - x4) - (x1 - x2) * (x3 * y4 - y3 * x4)) / den;
        var py = ((x1 * y2 - y1 * x2) * (y3 - y4) - (y1 - y2) * (x3 * y4 - y3 * x4)) / den;

        ctx.Vars.Set("Intersection.X", px);
        ctx.Vars.Set("Intersection.Y", py);
        ctx.Vars.Set("Intersection",new PointObj{X=px,Y=py});
        DetectionOverlay2D.Segment(ctx,p,Driver,"First Line Overlay",a1,b1);DetectionOverlay2D.Segment(ctx,p,Driver,"Second Line Overlay",a2,b2);DetectionOverlay2D.Marker(ctx,p,Driver,"Line Intersection",new(px,py));
        ctx.Log($"直线交点：({px:0.000}, {py:0.000})", false);
        return true;
    }
}

/// <summary>Geometric contour matching with rotation, scale, ROI, and running coordinate-system outputs.</summary>
public class ShapeMatchOp : IOperator
{
    private readonly string _driver;
    public ShapeMatchOp(string driver = "Basic Shape Match") { _driver = driver; }
    public string Driver => _driver;

    public bool Run(OpContext ctx, ParamBag p)
    {
        DetectionOverlay2D.Begin(ctx, Driver);
        ContourTemplateOutputs.Clear(ctx);
        var image = ctx.Images.Get(p.Int("IM In", 0));
        if (image == null || image.IsDisposed || image.Empty()) throw new ArgumentException("输入图像为空，请先采集或加载图像。");
        var model = ContourTemplateStorage.Read(p, ctx);
        if ((Driver == ContourTemplateEngine.GrayDriver) != (model.Backend == "HALCON.NCC"))
            throw new InvalidOperationException("模板类型不匹配，请在当前控件重新训练模板。");
        using var mask = TemplateSearchMask.Create(ctx, p, image);
        var result = ContourTemplateEngine.Match(image, model, ContourMatchOptions.FromParameters(p), mask, ctx.CancellationToken);
        ctx.Vars.Set("ShapeMatch.ElapsedMs", result.ElapsedMilliseconds); ctx.Vars.Set("Elapsed Time", result.ElapsedMilliseconds);
        ctx.Vars.Set("ShapeMatch.TimedOut", result.TimedOut);
        if (result.TimedOut) { ctx.Log("轮廓匹配超时；请缩小搜索区域或角度/缩放范围，或增大超时时间。", true); return false; }
        if (result.Matches.Count == 0) { ctx.Log($"轮廓匹配：未找到满足分数的目标，耗时 {result.ElapsedMilliseconds:0.0} ms", true); return false; }
        var records = new List<object>();
        foreach (var match in result.Matches)
        {
            var pose = ContourTemplateOutputs.Pose(match.X, match.Y, match.Angle, match.Scale);
            records.Add(new Dictionary<string, object> { ["Center"] = DetectionOverlay2D.XY(match.X, match.Y), ["Score"] = match.Score, ["Angle"] = match.Angle, ["Rotation"] = match.Rotation, ["Scale"] = match.Scale, ["Run Point"] = pose });
            DetectionOverlay2D.Marker(ctx, p, Driver, "Display Center Point", new(match.X, match.Y));
            bool rectangle = Driver == ContourTemplateEngine.GrayDriver || p.Str("Match Display", p.Bool("Display Rect Region") ? "Rectangle" : "Contour") == "Rectangle";
            if (rectangle)
            {
                var center = ContourTemplateEngine.TransformPoint(model, match, (model.Width - 1) / 2d, (model.Height - 1) / 2d);
                DetectionOverlay2D.Add(ctx, p, Driver, "rect2d", DetectionOverlay2D.Rect(new RotatedRect(new((float)center.X, (float)center.Y), new((float)(model.Width * match.Scale), (float)(model.Height * match.Scale)), (float)match.Rotation)), "Display Rect Region Attribute");
            }
            if (!rectangle) foreach (var contour in model.Contours)
                DetectionOverlay2D.Add(ctx, p, Driver, "trace2d", new() { ["Points"] = contour.Select(point => { var transformed = ContourTemplateEngine.TransformPoint(model, match, point.X, point.Y); return (object)DetectionOverlay2D.XY(transformed.X, transformed.Y); }).ToArray() }, "Display Contours Attribute");
        }
        var best = result.Matches[0];
        ctx.Vars.Set("ShapeMatch.Score", best.Score); ctx.Vars.Set("ShapeMatch.X", best.X); ctx.Vars.Set("ShapeMatch.Y", best.Y);
        ctx.Vars.Set("ShapeMatch.OffsetX", best.X - image.Width / 2d); ctx.Vars.Set("ShapeMatch.OffsetY", best.Y - image.Height / 2d);
        ctx.Vars.Set("ShapeMatch.Angle", best.Angle); ctx.Vars.Set("ShapeMatch.Rotation", best.Rotation); ctx.Vars.Set("ShapeMatch.Scale", best.Scale);
        ctx.Vars.Set("Matches", records); ctx.Vars.Set("Matched Num", records.Count);
        var basePoint = ContourTemplateOutputs.Pose(model.OriginX, model.OriginY, model.OriginAngle, 1);
        var runPoint = ContourTemplateOutputs.Pose(best.X, best.Y, best.Angle, best.Scale);
        ctx.Vars.Set("Base Point", basePoint); ctx.Vars.Set("Run Point", runPoint);
        ctx.Vars.Set("Points Information", new Dictionary<string, object> { ["Base Point"] = basePoint, ["Run Point"] = runPoint });
        ctx.Log($"轮廓匹配：{records.Count} 个，分数 {best.Score:0.000}，原点 ({best.X:0.00},{best.Y:0.00})，角度 {best.Angle:0.00}°，缩放 {best.Scale:0.000}，{result.ElapsedMilliseconds:0.0} ms", false);
        return true;
    }
}
