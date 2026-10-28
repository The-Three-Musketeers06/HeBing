using System.Globalization;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

internal static class EdgeProfile2D
{
    internal sealed record Edge(double Offset,double Gradient,Point2d Position);
    public static List<Edge> Find(Mat gray,Point2d center,Point2d direction,int start,int end,int threshold,string polarity,int width=1)
    {
        width=Math.Clamp(width,1,31);var imageWidth=gray.Width;var imageHeight=gray.Height;
        double Sample(int offset)
        {
            double sum=0;var count=0;
            for(var i=-(width/2);i<=width/2;i++)
            {
                var x=(int)Math.Round(center.X+direction.X*offset-direction.Y*i);var y=(int)Math.Round(center.Y+direction.Y*offset+direction.X*i);
                if(x>=0&&y>=0&&x<imageWidth&&y<imageHeight){sum+=gray.At<byte>(y,x);count++;}
            }
            return count>0?sum/count:double.NaN;
        }
        var found=new List<Edge>();Edge best=null;
        for(var offset=start;offset<=end;offset++)
        {
            var gradient=(Sample(offset+1)-Sample(offset-1))/2;
            var magnitude=polarity.Contains("Bright -> Dark",StringComparison.OrdinalIgnoreCase)||polarity.Contains("white to black",StringComparison.OrdinalIgnoreCase)?-gradient
                :polarity.Contains("Dark -> Bright",StringComparison.OrdinalIgnoreCase)||polarity.Contains("black to white",StringComparison.OrdinalIgnoreCase)?gradient:Math.Abs(gradient);
            if(double.IsFinite(magnitude)&&magnitude>=Math.Max(1,threshold))
            {if(best==null||magnitude>Math.Abs(best.Gradient))best=new(offset,gradient,new(center.X+direction.X*offset,center.Y+direction.Y*offset));}
            else if(best!=null){found.Add(best);best=null;}
        }
        if(best!=null)found.Add(best);return found;
    }
    public static Edge Select(IReadOnlyList<Edge> edges,string selection)=>edges.Count==0?null:selection.ToLowerInvariant() switch
    {"last"=>edges[^1],"strongest" or "best"=>edges.MaxBy(e=>Math.Abs(e.Gradient)),_=>edges[0]};
    public static (Point2d center,double width,double height,double rotation) Rect(ParamBag p,Mat image)
    {
        var rect=p.Group("Region")?.GetValueOrDefault("rect2d") as IReadOnlyDictionary<string,object>;
        var c=rect?.GetValueOrDefault("Center") as IReadOnlyDictionary<string,object>;var s=rect?.GetValueOrDefault("Size") as IReadOnlyDictionary<string,object>;
        var width=DetectionOverlay2D.Number(s,"Width",image.Width);var height=DetectionOverlay2D.Number(s,"Height",image.Height);
        if(width<=0||height<=0)throw new ArgumentException("请设置有效的矩形检测区域");
        return(new(DetectionOverlay2D.Number(c,"X",image.Width/2.0),DetectionOverlay2D.Number(c,"Y",image.Height/2.0)),width,height,DetectionOverlay2D.Number(rect,"Rotation"));
    }
}

public sealed class EdgePointFind2DOp : IOperator
{
    public string Driver => "Basic Edge Point Find";
    public bool Run(OpContext context, ParamBag parameters) => HalconGeometry2D.Run(context, parameters, Driver);
}

public sealed class EllipseFind2DOp:IOperator
{
    public string Driver=>"Basic Ellipse Find";
    public bool Run(OpContext ctx,ParamBag p)
    {
        DetectionOverlay2D.Begin(ctx,Driver);var image=ctx.Images.Get(p.Int("IM In"));if(image==null)throw new InvalidOperationException("输入图像为空");
        using var gray=Img.ToGray8(image);using var mask=ImageRegions2D.Mask(ctx,p,gray.Size(),true);using var edges=new Mat();
        Cv2.Canny(gray,edges,p.Int("Min Contrast",10),Math.Max(20,p.Int("Min Contrast",10)*3));Cv2.BitwiseAnd(edges,mask,edges);
        Cv2.FindContours(edges,out var contours,out _,RetrievalModes.List,ContourApproximationModes.ApproxNone);
        var contour=contours.Where(c=>c.Length>=5).MaxBy(c=>Math.Abs(Cv2.ContourArea(c)));if(contour==null){ctx.Log("查找椭圆：有效边缘点不足",true);return false;}
        var fit=Cv2.FitEllipse(contour);if(fit.Size.Width<=0||fit.Size.Height<=0)return false;
        ctx.Vars.Set("Ellipse",new Dictionary<string,object>{{"Center",DetectionOverlay2D.XY(fit.Center.X,fit.Center.Y)},{"Long Axis",(double)fit.Size.Width},{"Short Axis",(double)fit.Size.Height},{"Rotation",(double)fit.Angle}});
        ctx.Vars.Set("Center",new PointObj{X=fit.Center.X,Y=fit.Center.Y});ctx.Vars.Set("EdgePointCount",contour.Length);
        if(p.Bool("Display Ellipse",true))DetectionOverlay2D.Add(ctx,p,Driver,"ellipse2d",new(){{"Center",DetectionOverlay2D.XY(fit.Center.X,fit.Center.Y)},{"Long Axis",(double)fit.Size.Width},{"Short Axis",(double)fit.Size.Height},{"Rotation",(double)fit.Angle}});
        foreach(var point in contour)DetectionOverlay2D.Marker(ctx,p,Driver,"Display Points",new(point.X,point.Y));DetectionOverlay2D.ReferencePoints(ctx,p,Driver);
        ctx.Log($"查找椭圆：中心 ({fit.Center.X:0.##},{fit.Center.Y:0.##})，{contour.Length} 边缘点",false);return true;
    }
}

internal static class EdgePairResults2D
{
    public static bool Measure(OpContext ctx,ParamBag p,string driver,out string summary)
    {
        var image=ctx.Images.Get(p.Int("IM In"));if(image==null)throw new InvalidOperationException("输入图像为空");
        using var gray=Img.ToGray8(image);var rect=EdgeProfile2D.Rect(p,gray);var rad=rect.rotation*Math.PI/180;var direction=new Point2d(Math.Cos(rad),Math.Sin(rad));
        if(p.Str("Search Direction")=="Backward")direction=new(-direction.X,-direction.Y);
        var first=new List<Point2d>();var second=new List<Point2d>();var widths=new List<double>();var records=new List<object>();
        var profiles=Math.Clamp(p.Int("Max Nums",20),1,512);var min=p.GroupDbl("Width Range","min",0);var max=p.GroupDbl("Width Range","max",double.MaxValue);
        for(var i=0;i<profiles;i++)
        {
            var y=(i+.5)*rect.height/profiles-rect.height/2;var center=new Point2d(rect.center.X-direction.Y*y,rect.center.Y+direction.X*y);
            List<EdgeProfile2D.Edge> Find(string group)=>EdgeProfile2D.Find(gray,center,direction,-(int)(rect.width/2),(int)(rect.width/2),(int)p.GroupDbl(group,"Min Contrast",10),p.Group(group)?.GetValueOrDefault("Polarity")?.ToString()??"All",(int)p.GroupDbl(group,"Filter Width",3));
            var left=Find("Edge0");var right=Find("Edge1");var candidates=left.SelectMany(a=>right.Where(b=>b.Offset>a.Offset&&b.Offset-a.Offset>=min&&b.Offset-a.Offset<=max).Select(b=>(a,b))).ToArray();
            if(candidates.Length==0)continue;var pair=candidates[0];var selection=p.Group("Edge0")?.GetValueOrDefault("Selection")?.ToString();if(selection=="Last")pair=candidates[^1];else if(selection=="Strongest")pair=candidates.MaxBy(q=>Math.Abs(q.a.Gradient)+Math.Abs(q.b.Gradient));
            var distance=Geo.Dist(pair.a.Position,pair.b.Position);first.Add(pair.a.Position);second.Add(pair.b.Position);widths.Add(distance);
            records.Add(new Dictionary<string,object>{{"Width",distance},{"Point0",DetectionOverlay2D.XY(pair.a.Position.X,pair.a.Position.Y)},{"Point1",DetectionOverlay2D.XY(pair.b.Position.X,pair.b.Position.Y)}});
            DetectionOverlay2D.Segment(ctx,p,driver,"Distance Line",pair.a.Position,pair.b.Position);DetectionOverlay2D.Marker(ctx,p,driver,"Edge Point",pair.a.Position);DetectionOverlay2D.Marker(ctx,p,driver,"Edge Point",pair.b.Position);
            DetectionOverlay2D.Text(ctx,p,driver,"Text",center,distance.ToString("0.###",CultureInfo.InvariantCulture));
        }
        DetectionOverlay2D.Trace(ctx,p,driver,"Edge Line",first);DetectionOverlay2D.Trace(ctx,p,driver,"Edge Line",second);DetectionOverlay2D.ReferencePoints(ctx,p,driver);
        var average=widths.Count>0?widths.Average():0;ctx.Vars.Set("Edge.Width",average);ctx.Vars.Set("Widths",records);ctx.Vars.Set("Matched Num",widths.Count);
        summary=$"边缘宽度 {average:0.###} px，{widths.Count} 对";return widths.Count>0;
    }
}
