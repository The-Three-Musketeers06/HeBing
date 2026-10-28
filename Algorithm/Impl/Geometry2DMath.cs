using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

internal static class Geometry2DMath
{
    public static bool Intersection(Point2d a,Point2d b,Point2d c,Point2d d,out Point2d point)
    {
        var ux=b.X-a.X;var uy=b.Y-a.Y;var vx=d.X-c.X;var vy=d.Y-c.Y;var denominator=ux*vy-uy*vx;
        if(Math.Abs(denominator)<1e-12){point=default;return false;}
        var t=((c.X-a.X)*vy-(c.Y-a.Y)*vx)/denominator;point=new(a.X+t*ux,a.Y+t*uy);return true;
    }
    public static Point2d EllipsePoint(Point2d center,double rx,double ry,double angle,double t)
    {var rad=angle*Math.PI/180;var x=rx*Math.Cos(t);var y=ry*Math.Sin(t);return new(center.X+x*Math.Cos(rad)-y*Math.Sin(rad),center.Y+x*Math.Sin(rad)+y*Math.Cos(rad));}
    public static Point2d ClosestEllipse(Point2d point,Point2d center,double rx,double ry,double angle)
    {
        const int samples=256;var step=2*Math.PI/samples;var best=0;var distance=double.PositiveInfinity;
        double Cost(double t){var q=EllipsePoint(center,rx,ry,angle,t);return(q.X-point.X)*(q.X-point.X)+(q.Y-point.Y)*(q.Y-point.Y);}
        for(var i=0;i<samples;i++){var cost=Cost(i*step);if(cost<distance){distance=cost;best=i;}}
        var left=(best-1)*step;var right=(best+1)*step;
        for(var i=0;i<70;i++){var a=left+(right-left)/3;var b=right-(right-left)/3;if(Cost(a)<Cost(b))right=b;else left=a;}
        return EllipsePoint(center,rx,ry,angle,(left+right)/2);
    }
    public static Point2d[] LineEllipse(Point2d a,Point2d b,Point2d c,double rx,double ry,double angle)
    {
        var rad=-angle*Math.PI/180;Point2d Local(Point2d p)=>new((p.X-c.X)*Math.Cos(rad)-(p.Y-c.Y)*Math.Sin(rad),(p.X-c.X)*Math.Sin(rad)+(p.Y-c.Y)*Math.Cos(rad));
        var u=Local(a);var v=Local(b);var dx=v.X-u.X;var dy=v.Y-u.Y;
        var aa=dx*dx/(rx*rx)+dy*dy/(ry*ry);var bb=2*(u.X*dx/(rx*rx)+u.Y*dy/(ry*ry));var cc=u.X*u.X/(rx*rx)+u.Y*u.Y/(ry*ry)-1;
        var disc=bb*bb-4*aa*cc;if(aa<1e-24||disc< -1e-10)return Array.Empty<Point2d>();
        var roots=disc<=1e-10?new[]{-bb/(2*aa)}:new[]{(-bb-Math.Sqrt(disc))/(2*aa),(-bb+Math.Sqrt(disc))/(2*aa)};
        return roots.Select(t=>new Point2d(a.X+(b.X-a.X)*t,a.Y+(b.Y-a.Y)*t)).ToArray();
    }
    public static (Point2d point,Point2d foot,double distance) EllipseToLine(Point2d a,Point2d b,Point2d center,double rx,double ry,double angle)
    {
        var intersections=LineEllipse(a,b,center,rx,ry,angle);if(intersections.Length>0)return(intersections[0],intersections[0],0);
        var length=Geo.Dist(a,b);if(length<1e-12)throw new ArgumentException("直线长度无效");
        var nx=-(b.Y-a.Y)/length;var ny=(b.X-a.X)/length;var rad=angle*Math.PI/180;
        var u=nx*Math.Cos(rad)+ny*Math.Sin(rad);var v=-nx*Math.Sin(rad)+ny*Math.Cos(rad);var support=Math.Sqrt(rx*rx*u*u+ry*ry*v*v);
        var sign=Math.Sign((center.X-a.X)*nx+(center.Y-a.Y)*ny);var x=-sign*rx*rx*u/support;var y=-sign*ry*ry*v/support;
        var point=new Point2d(center.X+x*Math.Cos(rad)-y*Math.Sin(rad),center.Y+x*Math.Sin(rad)+y*Math.Cos(rad));var projected=Geo.PointToLine(point,a,b);return(point,projected.foot,projected.dist);
    }
}
