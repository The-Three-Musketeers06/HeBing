using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

internal static class SealantTrajectory2D
{
    public static bool Run(OpContext ctx,ParamBag p,Mat source,Point2d[] path)
    {
        const string driver="2D Sealant";
        using var gray=Img.ToGray8(source);var width=Math.Clamp((int)p.GroupDbl("ROI Settings","ROI Width",2),1,31);var half=Math.Clamp((int)p.GroupDbl("ROI Settings","ROI Length",20)/2,2,500);
        var spacing=Math.Max(1,width+p.GroupDbl("ROI Settings","Central Interval",0));var threshold=(int)p.GroupDbl("Detection Settings","Gray Threshold",30);
        var settings=p.Group("Detection Settings");var records=new List<object>();var widths=new List<double>();var samples=0;var abnormal=0;var continuousLeft=new List<Point2d>();var continuousRight=new List<Point2d>();
        var closed=p.Group("Trajectory Options")?.GetValueOrDefault("Closed") is true;
        DetectionOverlay2D.Trace(ctx,p,driver,"Trajectory",closed?path.Append(path[0]):path);
        void Flush(){DetectionOverlay2D.Trace(ctx,p,driver,"Edge of Sealant",continuousLeft);DetectionOverlay2D.Trace(ctx,p,driver,"Edge of Sealant",continuousRight);continuousLeft.Clear();continuousRight.Clear();}
        for(var segment=0;segment<path.Length-1+(closed?1:0);segment++)
        {
            var a=path[segment%path.Length];var b=path[(segment+1)%path.Length];var length=Geo.Dist(a,b);if(length<1e-9)continue;var normal=new Point2d(-(b.Y-a.Y)/length,(b.X-a.X)/length);var count=Math.Max(1,(int)Math.Ceiling(length/spacing));
            for(var i=0;i<count;i++)
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();var t=(i+.5)/count;var center=new Point2d(a.X+(b.X-a.X)*t,a.Y+(b.Y-a.Y)*t);samples++;
                EdgeProfile2D.Edge Edge(string prefix,Point2d direction)=>EdgeProfile2D.Select(EdgeProfile2D.Find(gray,center,direction,0,half,threshold,settings?.GetValueOrDefault(prefix+" Mode")?.ToString()??"black to white",width),settings?.GetValueOrDefault(prefix+" Options")?.ToString()??"first");
                var left=Edge("Point1",new(-normal.X,-normal.Y));var right=Edge("Point2",normal);
                if(left==null||right==null){abnormal++;Flush();if(p.Bool("Abnormal Position"))DetectionOverlay2D.Add(ctx,p,driver,"point2d",new(){{"X",center.X},{"Y",center.Y},{"_marker","cross"}},ok:false);continue;}
                var value=Geo.Dist(left.Position,right.Position);widths.Add(value);var judged=!(p.Group("Width Result Judgement")?.GetValueOrDefault("Enable") is true)||value>=p.GroupDbl("Width Result Judgement","Minimum Value",0)&&value<=p.GroupDbl("Width Result Judgement","Maximum Value",double.MaxValue);
                continuousLeft.Add(left.Position);continuousRight.Add(right.Position);DetectionOverlay2D.Marker(ctx,p,driver,"Boundary Point",left.Position);DetectionOverlay2D.Marker(ctx,p,driver,"Boundary Point",right.Position);
                if(!judged){abnormal++;if(p.Bool("Abnormal Position"))DetectionOverlay2D.Add(ctx,p,driver,"segment2d",new(){{"Start",DetectionOverlay2D.XY(left.Position.X,left.Position.Y)},{"End",DetectionOverlay2D.XY(right.Position.X,right.Position.Y)}},ok:false);}
                records.Add(new Dictionary<string,object>{{"Width",value},{"Point0",DetectionOverlay2D.XY(left.Position.X,left.Position.Y)},{"Point1",DetectionOverlay2D.XY(right.Position.X,right.Position.Y)},{"OK",judged}});
            }
        }
        Flush();var coverage=samples>0?(double)widths.Count/samples:0;
        ctx.Vars.Set("Sealant.AverageWidth",widths.Count>0?widths.Average():0);ctx.Vars.Set("Sealant.MaxWidth",widths.Count>0?widths.Max():0);ctx.Vars.Set("Sealant.GapColumns",samples-widths.Count);ctx.Vars.Set("Sealant.Coverage",coverage);ctx.Vars.Set("Measurements",records);ctx.Vars.Set("Abnormal Count",abnormal);
        ctx.Log($"胶路：沿轨迹 {samples} 组采样，检出 {widths.Count} 对边界，覆盖 {coverage:P1}",coverage<=.5);return coverage>.5;
    }
}
