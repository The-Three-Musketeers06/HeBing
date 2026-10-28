using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Source-owned occupancy segmentation, medial skeleton and physical cross sections.</summary>
public sealed class SegmentedSealant3DOp:IOperator
{
    public string Driver=>"3D Sealant";
    private sealed record Sample(int Source,double X,double Y,double Z);
    private sealed record Judge(double Min,double Max,int Allowed,bool Continuous);
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;int im=p.Int("IM In");var cloud=ctx.Clouds.Get(im)??throw new ArgumentException("胶路分割输入点云为空");var selection=CloudRegionSelector.Select(cloud,p);var local=CloudRegionSelector.CreateReferenceTransform(p.Group("Reference"));
        var points=selection.Select(i=>{var q=local(cloud.X[i],cloud.Y[i],cloud.Z[i]);return new Sample(i,q.X,q.Y,q.Z);}).Where(q=>double.IsFinite(q.X+q.Y+q.Z)).ToList();if(points.Count<3)throw new ArgumentException("胶路分割区域有效点不足");
        var skeletonSettings=p.Group("Skeleton Extract");string loop=ProfileMath.S(skeletonSettings,"Sealant Loop","closed");if(loop is not("closed" or "open"))throw new ArgumentException("无效胶路环类型");bool closed=loop=="closed";
        double step=ProfileMath.N(skeletonSettings,"Step Length",1),halfCut=ProfileMath.N(skeletonSettings,"Cut Length",5),resolution=p.Dbl("Resolution",.2),density=p.Dbl("Profile Density",1),project=p.Dbl("Project Height",5);int filter=p.Int("Filter Size",5);
        if(!double.IsFinite(step+halfCut+resolution+density+project)||step<=0||halfCut<=0||resolution<=0||density<.1||density>1||filter is not(3 or 5 or 7))throw new ArgumentException("胶路步长、切割半宽、分辨率、轮廓密度或滤波尺寸无效");
        var brokenSettings=p.Group("Broken Detection");double minimum=ProfileMath.N(brokenSettings,"Minimum Number of Sealant Points",1000),minimumSection=ProfileMath.N(brokenSettings,"Minimum Number of Segment Points",100),maxGap=ProfileMath.N(brokenSettings,"Maximal Tolerated Gap Length",.5);
        if(minimum<1||minimum!=Math.Truncate(minimum)||minimumSection<1||minimumSection!=Math.Truncate(minimumSection)||maxGap<0)throw new ArgumentException("胶路断裂检测点数或允许间隙无效");
        var heightMode=p.Str("Height Measurement","maximum height");if(heightMode is not("maximum height" or "minimum height"))throw new ArgumentException("无效高度测量模式");var widthJudge=ReadJudge(p.Group("Width Result Judgement"),"Width");var heightJudge=ReadJudge(p.Group("Height Result Judgement"),"Height");
        double x0=points.Min(q=>q.X)-resolution*2,y0=points.Min(q=>q.Y)-resolution*2;double wf=Math.Ceiling((points.Max(q=>q.X)-x0)/resolution)+3,hf=Math.Ceiling((points.Max(q=>q.Y)-y0)/resolution)+3;
        if(wf>10000||hf>10000||wf*hf>4_000_000)throw new ArgumentException("胶路分割栅格超过400万像素，请增大分辨率");int width=(int)wf,height=(int)hf;
        int Cell(Sample q)=>Math.Clamp((int)Math.Round((q.Y-y0)/resolution),0,height-1)*width+Math.Clamp((int)Math.Round((q.X-x0)/resolution),0,width-1);
        using var mask=new Mat(height,width,MatType.CV_8UC1,Scalar.All(0));var counts=new int[width*height];foreach(var q in points){int cell=Cell(q);counts[cell]++;mask.Set(cell/width,cell%width,(byte)255);}
        using var labels=new Mat();using var stats=new Mat();using var centers=new Mat();int components=Cv2.ConnectedComponentsWithStats(mask,labels,stats,centers,PixelConnectivity.Connectivity8,MatType.CV_32SC1);
        var componentCounts=new int[components];for(int y=0;y<height;y++)for(int x=0;x<width;x++)if(counts[y*width+x]>0)componentCounts[labels.At<int>(y,x)]+=counts[y*width+x];
        var accepted=Enumerable.Range(1,components-1).Where(i=>componentCounts[i]>=minimum).ToHashSet();if(accepted.Count==0)throw new ArgumentException("未找到达到最少点数的连通胶路，请检查分辨率及胶路最小点数");
        points=points.Where(q=>accepted.Contains(labels.At<int>(Cell(q)/width,Cell(q)%width))).ToList();for(int y=0;y<height;y++)for(int x=0;x<width;x++)if(!accepted.Contains(labels.At<int>(y,x)))mask.Set(y,x,(byte)0);
        using var smooth=new Mat();Cv2.GaussianBlur(mask,smooth,new Size(filter,filter),0);Cv2.Threshold(smooth,smooth,100,255,ThresholdTypes.Binary);
        var medial=Thin(smooth,ctx.CancellationToken);var pixels=Enumerable.Range(0,medial.Length).Where(i=>medial[i]).ToList();if(pixels.Count<3)throw new ArgumentException("胶路骨架不足，请减小分辨率或滤波尺寸");
        Point2d central;
        if(closed)central=new(points.Average(q=>q.X),points.Average(q=>q.Y));
        else{var center=ProfileMath.Unwrap(skeletonSettings?.GetValueOrDefault("Central Point"),"point3d");central=new(ProfileMath.N(center,"X"),ProfileMath.N(center,"Y"));}
        var path=pixels.Select(i=>new Point2d(x0+(i%width)*resolution,y0+(i/width)*resolution)).OrderBy(q=>Math.Atan2(q.Y-central.Y,q.X-central.X)).ToList();
        if(!closed)
        {
            var angles=path.Select(q=>Math.Atan2(q.Y-central.Y,q.X-central.X)).ToArray();int cut=0;double largest=angles[0]+2*Math.PI-angles[^1];for(int i=1;i<angles.Length;i++)if(angles[i]-angles[i-1]>largest){largest=angles[i]-angles[i-1];cut=i;}
            if(cut>0)path=path.Skip(cut).Concat(path.Take(cut)).ToList();
        }
        int pathStride=Math.Max(1,(int)Math.Round(1/density));
        if(pathStride>1){var last=path[^1];path=path.Where((q,i)=>i%pathStride==0).ToList();if(!closed&&Geo.Dist(path[^1],last)>1e-12)path.Add(last);}
        if(closed)path.Add(path[0]);var cumulative=new double[path.Count];for(int i=1;i<path.Count;i++)cumulative[i]=cumulative[i-1]+Geo.Dist(path[i-1],path[i]);double length=cumulative[^1];if(length<=resolution)throw new ArgumentException("胶路骨架长度不足");
        int sectionCount=(int)Math.Ceiling(length/step)+(closed?0:1);if(sectionCount>100000)throw new ArgumentException("胶路截面超过100000，请增大步长");sectionCount=Math.Max(2,sectionCount);
        Point2d At(double distance)
        {
            if(closed)distance=(distance%length+length)%length;else distance=Math.Clamp(distance,0,length);int index=Array.BinarySearch(cumulative,distance);if(index>=0)return path[index];index=~index;if(index<=0)return path[0];if(index>=path.Count)return path[^1];var distanceBetween=cumulative[index]-cumulative[index-1];return path[index-1]+(path[index]-path[index-1])*((distance-cumulative[index-1])/Math.Max(distanceBetween,1e-12));
        }
        var buckets=points.GroupBy(q=>((int)Math.Floor(q.X/halfCut),(int)Math.Floor(q.Y/halfCut))).ToDictionary(g=>g.Key,g=>g.ToArray());
        var origin=local(0,0,0);var tx=local(1,0,0);var ty=local(0,1,0);var tz=local(0,0,1);
        Dictionary<string,object> World(double x,double y,double z){x-=origin.X;y-=origin.Y;z-=origin.Z;return new(){["X"]=(tx.X-origin.X)*x+(tx.Y-origin.Y)*y+(tx.Z-origin.Z)*z,["Y"]=(ty.X-origin.X)*x+(ty.Y-origin.Y)*y+(ty.Z-origin.Z)*z,["Z"]=(tz.X-origin.X)*x+(tz.Y-origin.Y)*y+(tz.Z-origin.Z)*z};}
        DetectionOverlay2D.Begin(ctx,Driver);void Draw(string toggle,string type,Dictionary<string,object> value,bool ok=true){if(p.Bool(toggle))DetectionOverlay2D.Add(ctx,p,Driver,type,value,ok:ok,view:im);}
        var widths=new List<object>();var heights=new List<object>();var details=new List<object>();var gaps=new List<object>();var badWidth=new List<bool>();var badHeight=new List<bool>();var badSections=new List<bool>();var heightPoints=new List<object>();
        // Measure unsupported spans against the original occupancy, before smoothing; closing a path never heals a defect.
        bool Occupied(Point2d q){int x=(int)Math.Round((q.X-x0)/resolution),y=(int)Math.Round((q.Y-y0)/resolution);return x>=0&&y>=0&&x<width&&y<height&&mask.At<byte>(y,x)!=0;}
        double gapStart=-1;int samples=(int)Math.Ceiling(length/(resolution*.5));if(samples>2_000_000)throw new ArgumentException("胶路路径过长，请检查中心点和分辨率");
        var missing=new List<(double Start,double End)>();for(int i=0;i<=samples;i++){double d=i*length/samples;bool empty=!Occupied(At(d));if(empty&&gapStart<0)gapStart=d;if((!empty||i==samples)&&gapStart>=0){missing.Add((gapStart,d));gapStart=-1;}}
        if(closed&&missing.Count>1&&missing[0].Start<=resolution&&missing[^1].End>=length-resolution){var last=missing[^1];missing[0]=(last.Start-length,missing[0].End);missing.RemoveAt(missing.Count-1);}
        foreach(var gap in missing.Where(g=>g.End-g.Start>maxGap)){var location=At((gap.Start+gap.End)*.5);gaps.Add(new Dictionary<string,object>{["Length"]=gap.End-gap.Start,["Position"]=World(location.X,location.Y,project)});Draw("Display Broken Text","text3d",new(){["Position"]=World(location.X,location.Y,project),["Text"]=$"Broken {gap.End-gap.Start:0.###}",["_size"]=12},false);}
        for(int section=0;section<sectionCount;section++)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();double distance=closed?section*length/sectionCount:section*length/(sectionCount-1);var center=At(distance);var vector=At(distance+Math.Max(step*.5,resolution*filter))-At(distance-Math.Max(step*.5,resolution*filter));double norm=Math.Sqrt(ProfileMath.Dot(vector,vector));if(norm<1e-10)throw new ArgumentException("胶路骨架存在退化或分叉，无法确定截面方向");var tangent=vector*(1/norm);var normal=new Point2d(-tangent.Y,tangent.X);
            var found=new List<(Sample Point,double Across)>();int bx=(int)Math.Floor(center.X/halfCut),by=(int)Math.Floor(center.Y/halfCut),search=(int)Math.Ceiling(Math.Sqrt(halfCut*halfCut+step*step*.25)/halfCut);if(search>100)throw new ArgumentException("步长相对切割宽度过大");
            for(int dy=-search;dy<=search;dy++)for(int dx=-search;dx<=search;dx++)if(buckets.TryGetValue((bx+dx,by+dy),out var group))foreach(var q in group){var v=new Point2d(q.X-center.X,q.Y-center.Y);double across=ProfileMath.Dot(v,normal);if(Math.Abs(across)<=halfCut&&Math.Abs(ProfileMath.Dot(v,tangent))<=step*.5)found.Add((q,across));}
            bool valid=found.Count>0,broken=found.Count<minimumSection;double measuredWidth=valid?found.Max(q=>q.Across)-found.Min(q=>q.Across):0,measuredHeight=valid?(heightMode=="maximum height"?found.Max(q=>q.Point.Z):found.Min(q=>q.Point.Z)):0;
            bool widthBad=broken||measuredWidth<widthJudge.Min||measuredWidth>widthJudge.Max,heightBad=broken||measuredHeight<heightJudge.Min||measuredHeight>heightJudge.Max;
            widths.Add(measuredWidth);heights.Add(measuredHeight);badWidth.Add(widthBad);badHeight.Add(heightBad);badSections.Add(broken);details.Add(new Dictionary<string,object>{["Position"]=World(center.X,center.Y,project),["Width"]=measuredWidth,["Height"]=measuredHeight,["Point Count"]=found.Count,["Valid"]=valid,["Broken"]=broken});
            if(valid)
            {
                var highest=heightMode=="maximum height"?found.MaxBy(q=>q.Point.Z).Point:found.MinBy(q=>q.Point.Z).Point;var heightPoint=World(highest.X,highest.Y,highest.Z);heightPoints.Add(heightPoint);Draw("Display Height Points","point3d",heightPoint,!heightBad);
                var start=center+normal*found.Min(q=>q.Across);var end=center+normal*found.Max(q=>q.Across);Draw("Display Width Segments","segment3d",new(){["Start"]=World(start.X,start.Y,project),["End"]=World(end.X,end.Y,project)},!widthBad);
                Draw("Display Width Text","text3d",new(){["Position"]=World(start.X,start.Y,project),["Text"]=$"W={measuredWidth:0.###}",["_size"]=12},!widthBad);Draw("Display Height Text","text3d",new(){["Position"]=heightPoint,["Text"]=$"H={measuredHeight:0.###}",["_size"]=12},!heightBad);
            }
            if(broken)Draw("Display Broken Text","text3d",new(){["Position"]=World(center.X,center.Y,project),["Text"]="Broken",["_size"]=12},false);
        }
        if(closed&&heightPoints.Count>1)heightPoints.Add(heightPoints[0]);Draw("Display Height Segments","trace3d",new(){["Points"]=heightPoints});
        if(p.Bool("Display Width Polygons"))
        {
            Cv2.FindContours(mask,out var contours,out _,RetrievalModes.List,ContourApproximationModes.ApproxNone);int stride=Math.Max(1,(int)Math.Round(1/density));foreach(var contour in contours.Where(c=>c.Length>=3)){var vertices=contour.Where((q,i)=>i%stride==0).Select(q=>(object)World(x0+q.X*resolution,y0+q.Y*resolution,project)).ToList();if(vertices.Count>=3){vertices.Add(vertices[0]);Draw("Display Width Polygons","trace3d",new(){["Points"]=vertices});}}
        }
        bool ok=gaps.Count==0&&!badSections.Any(v=>v)&&Accept(badWidth,widthJudge,closed)&&Accept(badHeight,heightJudge,closed);ctx.Vars.Set("result",ok?"OK":"NG");ctx.Vars.Set("width",widths);ctx.Vars.Set("height",heights);ctx.Vars.Set("sections",details);ctx.Vars.Set("gaps",gaps);ctx.Vars.Set("skeleton",path.Select(q=>(object)World(q.X,q.Y,project)).ToList());ctx.Vars.Set("fracture_result",gaps.Count==0&&!badSections.Any(v=>v));ctx.Vars.Set("width_result",Accept(badWidth,widthJudge,closed));ctx.Vars.Set("height_result",Accept(badHeight,heightJudge,closed));
        if(p.Bool("Pointset Cut")&&p.Bool("Use Region"))cloud.DisplayIndices=selection.ToArray();ProfileMath.Warn(ctx,"自有分割胶路采用XY占用分割、Gaussian+Zhang-Suen骨架及极角排序截面；需为相对中心点可顺序排列的单条开口/闭合胶路。复杂分叉及原版骨架/密度数值仍待校准；缺测截面Valid=false，其0占位不代表测量值");ctx.Log($"胶路分割：{sectionCount} 个截面，缺口 {gaps.Count}，结果 {(ok?"OK":"NG")}",false);return true;
    }
    private static Judge ReadJudge(IReadOnlyDictionary<string,object> map,string name)
    {double low=ProfileMath.N(map,"Minimum "+name,.1),high=ProfileMath.N(map,"Maximum "+name,1),allowed=ProfileMath.N(map,"Number of Outliers",1);string mode=ProfileMath.S(map,"Judgement Mode","arbitrary");if(low<0||high<low||allowed<0||allowed>int.MaxValue||allowed!=Math.Truncate(allowed)||mode is not("arbitrary" or "continuity"))throw new ArgumentException("胶路宽高判断参数无效");return new(low,high,(int)allowed,mode=="continuity");}
    private static bool Accept(List<bool> errors,Judge judge,bool closed)
    {if(!judge.Continuous)return errors.Count(v=>v)<=judge.Allowed;int consecutive=0;for(int i=0;i<errors.Count*(closed?2:1);i++){consecutive=errors[i%errors.Count]?Math.Min(errors.Count,consecutive+1):0;if(consecutive>judge.Allowed)return false;}return true;}
    private static bool[] Thin(Mat image,CancellationToken token)
    {
        int width=image.Width,height=image.Height;var data=new bool[width*height];for(int y=0;y<height;y++)for(int x=0;x<width;x++)data[y*width+x]=image.At<byte>(y,x)>0;
        var removed=new List<int>();bool changed;int iteration=0;do
        {
            token.ThrowIfCancellationRequested();changed=false;for(int phase=0;phase<2;phase++)
            {
                removed.Clear();for(int y=1;y<height-1;y++)for(int x=1;x<width-1;x++)
                {
                    int i=y*width+x;if(!data[i])continue;bool n=data[i-width],ne=data[i-width+1],e=data[i+1],se=data[i+width+1],s=data[i+width],sw=data[i+width-1],w=data[i-1],nw=data[i-width-1];
                    int neighbors=(n?1:0)+(ne?1:0)+(e?1:0)+(se?1:0)+(s?1:0)+(sw?1:0)+(w?1:0)+(nw?1:0);if(neighbors<2||neighbors>6)continue;
                    int transitions=(!n&&ne?1:0)+(!ne&&e?1:0)+(!e&&se?1:0)+(!se&&s?1:0)+(!s&&sw?1:0)+(!sw&&w?1:0)+(!w&&nw?1:0)+(!nw&&n?1:0);if(transitions!=1)continue;
                    if(phase==0?(n&&e&&s)||(e&&s&&w):(n&&e&&w)||(n&&s&&w))continue;removed.Add(i);
                }
                foreach(var index in removed)data[index]=false;if(removed.Count>0)changed=true;
            }
            if(++iteration>4096)throw new ArgumentException("胶路骨架细化未收敛，请检查输入区域");
        }while(changed);return data;
    }
}
