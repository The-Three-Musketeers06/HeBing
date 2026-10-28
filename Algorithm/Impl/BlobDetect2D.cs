using System.Globalization;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Blob measurements and non-destructive display graphics in image coordinates.</summary>
public sealed class BlobDetectOp : IOperator
{
    public string Driver => "Basic Blob Detect";
    public bool Run(OpContext ctx, ParamBag p)
    {
        DetectionOverlay2D.Begin(ctx, Driver);
        var input = p.Int("IM In");
        var src = ctx.Images.Get(input);
        if (src == null) { ctx.Log("Blob分析：输入为空", true); return false; }
        using var gray = Img.ToGray8(src);
        using var binary = new Mat();
        Cv2.InRange(gray, new Scalar(p.GroupDbl("Threshold Range", "min", 127)),
            new Scalar(p.GroupDbl("Threshold Range", "max", 255)), binary);
        if (p.Bool("Invert")) Cv2.BitwiseNot(binary, binary);
        if (p.Str("ROI Region Type", "Graphic type") == "Image type")
        {
            var raw = ctx.Images.Get(p.Int("Mask Im In", 2));
            if (raw == null || raw.Size() != binary.Size()) { ctx.Log("Blob分析：掩膜为空或尺寸不符", true); return false; }
            using var mask = Img.ToGray8(raw);
            Cv2.Threshold(mask, mask, 0, 255, ThresholdTypes.Binary);
            Cv2.BitwiseAnd(binary, mask, binary);
        }
        else if (p.Bool("Use Region"))
        {
            using var mask = RegionMask(binary.Size(), p.Raw("Region"), p);
            Cv2.BitwiseAnd(binary, mask, binary);
        }
        // Preserve contours including their hole boundaries for the detection-area display.
        Cv2.FindContours(binary, out var contours, out var hierarchy, RetrievalModes.Tree, ContourApproximationModes.ApproxSimple);
        var blobs = new List<object>();
        var max = Math.Max(1, p.Int("Max Nums", 100));
        var displaySlot = p.Bool("IM Out Enable") ? p.Int("IM Out", 1) : input;
        for (var index = 0; index < contours.Length && blobs.Count < max; index++)
        {
            if (hierarchy[index].Parent >= 0) continue;
            var contour = contours[index];
            var area = Cv2.ContourArea(contour);
            var perimeter = Cv2.ArcLength(contour, true);
            var moments = Cv2.Moments(contour);
            if (moments.M00 <= 0) continue;
            var center = new Point2d(moments.M10 / moments.M00, moments.M01 / moments.M00);
            var circularity = perimeter > 0 ? Math.Min(1, 4 * Math.PI * area / (perimeter * perimeter)) : 0;
            var box = Cv2.BoundingRect(contour);
            var rotated = Cv2.MinAreaRect(contour.Select(q => new Point2f(q.X, q.Y)));
            var ellipse = contour.Length >= 5 ? Cv2.FitEllipse(contour) : rotated;
            Cv2.MinEnclosingCircle(contour, out var circleCenter, out var circleRadius);
            if (!Range(p,"Area","Range",area,10,1e9,true) || !Range(p,"Circularity","Range",circularity,0,1)
                || !Range(p,"Perimeter","Range",perimeter) || !Range(p,"Center","X Range",center.X,-30000,30000)
                || !Range(p,"Center","Y Range",center.Y,-30000,30000)
                || !Range(p,"Bounding Rect","Width Range",box.Width) || !Range(p,"Bounding Rect","Height Range",box.Height)
                || !Range(p,"Bounding Rect","Aspect Ratio Range",box.Width/(double)Math.Max(1,box.Height))
                || !RectRange(p,"Min Area Rect",rotated)
                || !Range(p,"Min Ellipse","Long Axis Range",Math.Max(ellipse.Size.Width,ellipse.Size.Height))
                || !Range(p,"Min Ellipse","Short Axis Range",Math.Min(ellipse.Size.Width,ellipse.Size.Height))
                || !Range(p,"Min Ellipse","Aspect Ratio Range",Math.Max(ellipse.Size.Width,ellipse.Size.Height)/Math.Max(1e-9,Math.Min(ellipse.Size.Width,ellipse.Size.Height)))
                || !Range(p,"Min Ellipse","Angle Range",ellipse.Angle,-180,180)
                || !Range(p,"Min Area Circle","Radius Range",circleRadius)) continue;

            using var local = new Mat(binary, box);
            using var filled = Mat.Zeros(box.Height, box.Width, MatType.CV_8UC1).ToMat();
            var localContour = contour.Select(q => new Point(q.X-box.X,q.Y-box.Y)).ToArray();
            Cv2.FillPoly(filled, new[] { localContour }, Scalar.White);
            Cv2.BitwiseAnd(local, filled, filled);
            Rect inner = default;
            if (Enabled(p,"Max Inner Rect") || p.Bool("Max Inner Rect Display")) inner = LargestInsideRect(filled);
            var innerRotated = new RotatedRect(new Point2f(box.X+inner.X+inner.Width/2f,box.Y+inner.Y+inner.Height/2f),new Size2f(inner.Width,inner.Height),0);
            if (!RectRange(p,"Max Inner Rect",innerRotated)) continue;
            double innerRadius=0; Point innerCenter=default;
            if (Enabled(p,"Max Inner Circle") || p.Bool("Max Inner Circle Display"))
            {
                // A zero border is needed when the component fills its bounding rectangle.
                using var padded = new Mat(); using var distance = new Mat();
                Cv2.CopyMakeBorder(filled,padded,1,1,1,1,BorderTypes.Constant,Scalar.Black);
                Cv2.DistanceTransform(padded,distance,DistanceTypes.L2,DistanceTransformMasks.Precise);
                Cv2.MinMaxLoc(distance,out _,out innerRadius,out _,out var at);
                innerCenter = new Point(box.X+at.X-1,box.Y+at.Y-1);
            }
            if (!Range(p,"Max Inner Circle","Radius Range",innerRadius)) continue;
            var n=blobs.Count;
            var record = new Dictionary<string,object>
            {
                ["Area"]=area,["Perimeter"]=perimeter,["Circularity"]=circularity,["Center"]=DetectionOverlay2D.XY(center.X,center.Y),
                ["Bounding Rect"]=DetectionOverlay2D.Rect(new RotatedRect(new Point2f(box.X+box.Width/2f,box.Y+box.Height/2f),new Size2f(box.Width,box.Height),0)),
                ["Min Area Rect"]=DetectionOverlay2D.Rect(rotated),["Max Inner Rect"]=DetectionOverlay2D.Rect(innerRotated),
                ["Min Ellipse"]=DetectionOverlay2D.Rect(ellipse),
                ["Min Area Circle"]=new Dictionary<string,object>{{"Center",DetectionOverlay2D.XY(circleCenter.X,circleCenter.Y)},{"Radius",(double)circleRadius}},
                ["Max Inner Circle"]=new Dictionary<string,object>{{"Center",DetectionOverlay2D.XY(innerCenter.X,innerCenter.Y)},{"Radius",innerRadius}}
            };
            blobs.Add(record);
            ctx.Vars.Set($"Blob[{n}].Area",area);ctx.Vars.Set($"Blob[{n}].CenterX",center.X);ctx.Vars.Set($"Blob[{n}].CenterY",center.Y);
            ctx.Vars.Set($"Blob[{n}].Width",box.Width);ctx.Vars.Set($"Blob[{n}].Height",box.Height);
            if (p.Bool("Detection Area"))
            {
                DrawContour(contour);
                // Draw nested holes and islands, using the same coordinate system and style.
                for(var child=0;child<contours.Length;child++)
                {
                    var parent=hierarchy[child].Parent;
                    while(parent>=0 && parent!=index)parent=hierarchy[parent].Parent;
                    if(parent==index)DrawContour(contours[child]);
                }
            }
            void DrawContour(Point[] points)
            {
                if(points.Length<2)return;
                DetectionOverlay2D.Add(ctx,p,Driver,points.Length==2?"trace2d":"polygon2d",
                    new(){{"Points",points.Select(q=>(object)DetectionOverlay2D.XY(q.X,q.Y)).ToArray()}},"Detection Area Display",view:displaySlot);
            }
            if(p.Bool("Center Display"))DetectionOverlay2D.Point(ctx,p,Driver,center.X,center.Y,"Center Attribute",displaySlot);
            if(p.Bool("Area Text Display"))DetectionOverlay2D.Add(ctx,p,Driver,"text2d",new()
            {
                ["Position"]=DetectionOverlay2D.XY(Math.Max(0,center.X-18),Math.Max(0,center.Y-12)),
                ["Text"]=$"#{n+1}\nA={area.ToString("0.##",CultureInfo.InvariantCulture)}"
            },"Area Text Attribute",view:displaySlot);
            if(p.Bool("Bounding Rect Display"))DetectionOverlay2D.Add(ctx,p,Driver,"rect2d",(Dictionary<string,object>)record["Bounding Rect"],"Bounding Rect Attribute",view:displaySlot);
            foreach(var name in new[]{"Min Area Rect","Max Inner Rect"})
                if(p.Bool(name+" Display"))DetectionOverlay2D.Add(ctx,p,Driver,"rect2d",(Dictionary<string,object>)record[name],name+" Attribute",view:displaySlot);
            if(p.Bool("Min Ellipse Display"))DetectionOverlay2D.Add(ctx,p,Driver,"ellipse2d",new()
            {
                ["Center"]=DetectionOverlay2D.XY(ellipse.Center.X,ellipse.Center.Y),["Long Axis"]=(double)ellipse.Size.Width,
                ["Short Axis"]=(double)ellipse.Size.Height,["Rotation"]=(double)ellipse.Angle
            },"Min Ellipse Attribute",view:displaySlot);
            foreach(var name in new[]{"Min Area Circle","Max Inner Circle"})
                if(p.Bool(name+" Display"))DetectionOverlay2D.Add(ctx,p,Driver,"circle2d",(Dictionary<string,object>)record[name],name+" Attribute",view:displaySlot);
        }
        ctx.Vars.Set("Blob.Count",blobs.Count);ctx.Vars.Set("Count",blobs.Count);ctx.Vars.Set("Blobs",blobs);
        DetectionOverlay2D.ReferencePoints(ctx,p,Driver);
        if(p.Bool("IM Out Enable"))
        {
            // Output pixels remain reusable by downstream algorithms. Graphics are a separate layer.
            ctx.SetImage(displaySlot,src.Clone());ctx.Clouds.Remove(displaySlot);ctx.Profiles.Remove(displaySlot);
        }
        ctx.Log($"Blob分析：命中 {blobs.Count} 个；检测图形 -> IM{displaySlot}",false);
        return blobs.Count>0;
    }
    private static bool Enabled(ParamBag p,string group)=>Params2.GroupBool(p,group,"Enable",false);
    private static bool Range(ParamBag p,string group,string range,double value,double min=0,double max=1e9,bool enabled=false)
        => !Params2.GroupBool(p,group,"Enable",enabled) || value>=Params2.Nested(p,group,range,"min",min)&&value<=Params2.Nested(p,group,range,"max",max);
    private static bool RectRange(ParamBag p,string group,RotatedRect rect)
    {
        var length=Math.Max(rect.Size.Width,rect.Size.Height);var width=Math.Min(rect.Size.Width,rect.Size.Height);
        return Range(p,group,"Length Range",length)&&Range(p,group,"Width Range",width)
            &&Range(p,group,"Aspect Ratio Range",length/Math.Max(1e-9,width))&&Range(p,group,"Angle Range",rect.Angle,-180,180);
    }
    private static Rect LargestInsideRect(Mat mask)
    {
        var columns=mask.Width;var rows=mask.Height;
        var heights=new int[columns];var best=new Rect();
        for(var y=0;y<rows;y++)
        {
            for(var x=0;x<columns;x++)heights[x]=mask.At<byte>(y,x)>0?heights[x]+1:0;
            var stack=new Stack<int>();
            for(var x=0;x<=columns;x++)
            {
                var h=x==columns?0:heights[x];
                while(stack.Count>0 && heights[stack.Peek()]>h)
                {
                    var height=heights[stack.Pop()];var left=stack.Count>0?stack.Peek()+1:0;var width=x-left;
                    if(width*height>best.Width*best.Height)best=new(left,y-height+1,width,height);
                }
                if(x<columns)stack.Push(x);
            }
        }
        return best;
    }
    internal static Mat RegionMask(Size size,object raw,ParamBag p)
    {
        var mask=Mat.Zeros(size,MatType.CV_8UC1).ToMat();
        try
        {
            foreach(var region in SplitGraphicRegions(raw))
            {
                if(region is not IReadOnlyDictionary<string,object> r)throw new InvalidOperationException("Blob区域数据为空");
                var type=r.GetValueOrDefault("Type")?.ToString()??"rect2d";
                if(r.GetValueOrDefault(type) is not IReadOnlyDictionary<string,object> shape)throw new InvalidOperationException("Blob区域缺少 "+type);
                var center=shape.GetValueOrDefault("Center") as IReadOnlyDictionary<string,object>;
                var cx=DetectionOverlay2D.Number(center,"X");var cy=DetectionOverlay2D.Number(center,"Y");
                double N(string key)=>DetectionOverlay2D.Number(shape,key);
                Point2d[] points;
                if(type=="rect2d")
                {
                    var s=shape.GetValueOrDefault("Size") as IReadOnlyDictionary<string,object>;
                    points=new RotatedRect(new((float)cx,(float)cy),new((float)DetectionOverlay2D.Number(s,"Width"),(float)DetectionOverlay2D.Number(s,"Height")),(float)N("Rotation")).Points().Select(q=>new Point2d(q.X,q.Y)).ToArray();
                }
                else if(type=="polygon2d")
                    points=(shape.GetValueOrDefault("Points") as IEnumerable<object>??Array.Empty<object>()).OfType<IReadOnlyDictionary<string,object>>().Select(q=>new Point2d(DetectionOverlay2D.Number(q,"X"),DetectionOverlay2D.Number(q,"Y"))).ToArray();
                else if(type is "circle2d" or "ring2d" or "sector2d")
                {
                    var outer=type=="circle2d"?N("Radius"):N("Outer Radius");var inner=type=="circle2d"?0:N("Inner Radius");
                    var start=type=="sector2d"?N("Start Angle"):0;var range=type=="sector2d"?N("Angle Range"):360;
                    Point2d At(double radius,int i){var angle=(start+range*i/180)*Math.PI/180;return new(cx+radius*Math.Cos(angle),cy+radius*Math.Sin(angle));}
                    points=Enumerable.Range(0,181).Select(i=>At(outer,i)).Concat(Enumerable.Range(0,181).Reverse().Select(i=>At(inner,i))).ToArray();
                }
                else throw new InvalidOperationException("Blob区域类型尚未支持："+type);
                if(points.Length<3)throw new InvalidOperationException("Blob区域点数不足");
                if(p.Bool("Position Fix"))points=Transform(points,p);
                Cv2.FillPoly(mask,new[]{points.Select(q=>new Point((int)Math.Round(q.X),(int)Math.Round(q.Y))).ToArray()},Scalar.White);
            }
            return mask;
        }
        catch{mask.Dispose();throw;}
    }
    private static IEnumerable<object> SplitGraphicRegions(object raw)
    {
        if(raw is IEnumerable<object> list && raw is not IReadOnlyDictionary<string,object>)
        {
            foreach(var item in list)foreach(var region in SplitGraphicRegions(item))yield return region;
            yield break;
        }
        if(raw is IReadOnlyDictionary<string,object> map && map.GetValueOrDefault("Type")?.ToString() is { } type
           && type.EndsWith(" array",StringComparison.Ordinal))
        {
            if(map.GetValueOrDefault(type) is not IEnumerable<object> entries)throw new InvalidOperationException("区域数组不存在："+type);
            var elementType=type[..^6];
            foreach(var entry in entries)yield return new Dictionary<string,object>{{"Type",elementType},{elementType,entry}};
            yield break;
        }
        yield return raw;
    }
    internal static Point2d[] Transform(Point2d[] points,ParamBag p)
    {
        var info=p.Group("Points Information");
        if(info?.GetValueOrDefault("Base Point") is not IReadOnlyDictionary<string,object> b
           ||info.GetValueOrDefault("Run Point") is not IReadOnlyDictionary<string,object> r)throw new InvalidOperationException("缺少定位点参数");
        var bp=b.GetValueOrDefault("Point") as IReadOnlyDictionary<string,object>;var rp=r.GetValueOrDefault("Point") as IReadOnlyDictionary<string,object>;
        var baseAngle=-DetectionOverlay2D.Number(b,"Angle")*Math.PI/180;var runAngle=DetectionOverlay2D.Number(r,"Angle")*Math.PI/180;
        var sx=DetectionOverlay2D.Number(b,"ScaleX",1);var sy=DetectionOverlay2D.Number(b,"ScaleY",1);
        if(Math.Abs(sx)<1e-12||Math.Abs(sy)<1e-12)throw new InvalidOperationException("定位基准缩放不能为零");
        return points.Select(q=>
        {
            var x=q.X-DetectionOverlay2D.Number(bp,"X");var y=q.Y-DetectionOverlay2D.Number(bp,"Y");
            var u=(x*Math.Cos(baseAngle)-y*Math.Sin(baseAngle))*DetectionOverlay2D.Number(r,"ScaleX",1)/sx;
            var v=(x*Math.Sin(baseAngle)+y*Math.Cos(baseAngle))*DetectionOverlay2D.Number(r,"ScaleY",1)/sy;
            return new Point2d(u*Math.Cos(runAngle)-v*Math.Sin(runAngle)+DetectionOverlay2D.Number(rp,"X"),u*Math.Sin(runAngle)+v*Math.Cos(runAngle)+DetectionOverlay2D.Number(rp,"Y"));
        }).ToArray();
    }
}
