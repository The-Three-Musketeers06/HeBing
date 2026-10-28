using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Measured sealant sections in reference X/Z coordinates, with independent defect verdicts.</summary>
public sealed class ProfileSealantOp:IOperator
{
    public string Driver=>"3D ProfileSealant";
    private sealed record Sample(int Index,double X,double Y,double Z);
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;int im=p.Int("IM In");var source=ctx.Clouds.Get(im);if(source==null||source.Count==0)throw new ArgumentException("胶路检测输入点云为空");
        var selected=CloudRegionSelector.Select(source,p);var transform=CloudRegionSelector.CreateReferenceTransform(p.Group("Reference"));
        var samples=selected.Select(i=>{var q=transform(source.X[i],source.Y[i],source.Z[i]);return new Sample(i,q.X,q.Y,q.Z);}).Where(q=>double.IsFinite(q.X+q.Y+q.Z)).OrderBy(q=>q.Y).ToArray();
        if(samples.Length==0)throw new ArgumentException("胶路检测区域为空");
        double step=p.Dbl("Step Length",1),reference=p.Dbl("Reference Height"),detection=p.Dbl("Detection Height",.1);int minimum=p.Int("Points Limit of Broken Sealant",100);
        var settings=p.Group("Profile Capture");double width=ProfileMath.N(settings,"Extract Width",.1),resolution=ProfileMath.N(settings,"X Resolution",.1);string mode=ProfileMath.S(settings,"Capture Pattern","Nearest");
        if(!double.IsFinite(step+reference+detection)||step<=0||detection<0||width<=0||width>=step||resolution<=0||minimum<0)throw new ArgumentException("胶路步长、参考高度、检测高度、点数或提取宽度无效（提取宽度需小于步长）");
        string heightMode=p.Str("Height Measurement","maximum height");if(heightMode is not("maximum height" or "minimum height"))throw new ArgumentException("未知胶路高度测量模式");
        if(mode is not("Nearest" or "Fusion(average)"))throw new ArgumentException("未知胶路提取模式");
        var widthJudge=Validate(p.Group("Width Result Judgement"),"Width");var heightJudge=Validate(p.Group("Height Result Judgement"),"Height");var brokenJudge=Validate(p.Group("Fracture Result Judgement"),null);
        var rows=new List<Sample[]>();if(mode=="Nearest")
        {
            var raster=source.Raster??OwnedProfileCaptureOp.InferGrid(source);var sampleMap=samples.ToDictionary(q=>q.Index);var indexRows=new Dictionary<int,int>();
            for(int row=0;row<raster.Height;row++)for(int col=0;col<raster.Width;col++){int index=raster.PointIndices[row*raster.Width+col];if(index>=0)indexRows[index]=row;}
            rows=samples.Where(q=>indexRows.ContainsKey(q.Index)).GroupBy(q=>indexRows[q.Index]).Select(g=>g.OrderBy(q=>q.X).ToArray()).OrderBy(g=>g.Average(q=>q.Y)).ToList();
            if(rows.Count==0)throw new ArgumentException("区域有序点云行为空");
        }
        double low=samples[0].Y,high=samples[^1].Y;double rawCount=Math.Ceiling((high-low)/step);if(rawCount>100000)throw new ArgumentException("胶路剖面超过100000个，请增加步长");int count=Math.Max(1,(int)rawCount);
        var widths=new List<object>();var heights=new List<object>();var broken=new List<bool>();var widthBad=new List<bool>();var heightBad=new List<bool>();var sections=new List<object>();DetectionOverlay2D.Begin(ctx,Driver);
        var origin=transform(0,0,0);var tx=transform(1,0,0);var ty=transform(0,1,0);var tz=transform(0,0,1);
        Dictionary<string,object> World(double x,double y,double z){x-=origin.X;y-=origin.Y;z-=origin.Z;return new(){["X"]=(tx.X-origin.X)*x+(tx.Y-origin.Y)*y+(tx.Z-origin.Z)*z,["Y"]=(ty.X-origin.X)*x+(ty.Y-origin.Y)*y+(ty.Z-origin.Z)*z,["Z"]=(tz.X-origin.X)*x+(tz.Y-origin.Y)*y+(tz.Z-origin.Z)*z};}
        void Draw(string toggle,string type,Dictionary<string,object> values,bool ok=true){if(p.Bool(toggle))DetectionOverlay2D.Add(ctx,p,Driver,type,values,ok:ok,view:im);}
        int left=0,right=0,stripLeft=0,stripRight=0,rowCursor=0;
        for(int n=0;n<count;n++)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();double y=low+Math.Min(high-low,(n+.5)*step);
            while(stripLeft<samples.Length&&samples[stripLeft].Y<low+n*step)stripLeft++;stripRight=Math.Max(stripRight,stripLeft);while(stripRight<samples.Length&&(n==count-1?samples[stripRight].Y<=high:samples[stripRight].Y<low+(n+1)*step))stripRight++;
            int above=0;for(int i=stripLeft;i<stripRight;i++)if(samples[i].Z>reference+detection)above++;
            List<Point2d> profile;
            if(mode=="Nearest")
            {
                while(rowCursor<rows.Count-1&&Math.Abs(rows[rowCursor+1].Average(q=>q.Y)-y)<Math.Abs(rows[rowCursor].Average(q=>q.Y)-y))rowCursor++;
                profile=rows[rowCursor].Select(q=>new Point2d(q.X,q.Z)).ToList();
            }
            else
            {
                while(left<samples.Length&&samples[left].Y<y-width)left++;right=Math.Max(right,left);while(right<samples.Length&&samples[right].Y<=y+width)right++;
                var band=samples[left..right];double x0=band.Length==0?0:band.Min(q=>q.X);
                profile=band.GroupBy(q=>Math.Floor((q.X-x0)/resolution+1e-6)).Select(g=>new Point2d(g.Average(q=>q.X),g.Average(q=>q.Z))).OrderBy(q=>q.X).ToList();
            }
            var bead=profile.Where(q=>q.Y>reference+detection).ToList();bool isBroken=above<minimum||bead.Count<2;
            double measuredWidth=0,measuredHeight=0;Point2d first=default,last=default,heightPoint=default;
            if(bead.Count>0)
            {
                first=bead[0];last=bead[^1];heightPoint=heightMode=="maximum height"?bead.MaxBy(q=>q.Y):bead.MinBy(q=>q.Y);measuredWidth=last.X-first.X;measuredHeight=heightPoint.Y-reference;
                var firstIndex=profile.IndexOf(first);var lastIndex=profile.LastIndexOf(last);double level=reference+detection;
                Point2d Crossing(Point2d a,Point2d b)=>a+(b-a)*((level-a.Y)/(b.Y-a.Y));
                if(firstIndex>0&&profile[firstIndex-1].Y<=level)first=Crossing(profile[firstIndex-1],first);
                if(lastIndex+1<profile.Count&&profile[lastIndex+1].Y<=level)last=Crossing(last,profile[lastIndex+1]);
                measuredWidth=last.X-first.X;
            }
            bool badWidth=isBroken||measuredWidth<widthJudge.Min||measuredWidth>widthJudge.Max,badHeight=isBroken||measuredHeight<heightJudge.Min||measuredHeight>heightJudge.Max;
            widths.Add(measuredWidth);heights.Add(measuredHeight);broken.Add(isBroken);widthBad.Add(badWidth);heightBad.Add(badHeight);
            sections.Add(new Dictionary<string,object>{["Y"]=y,["width"]=measuredWidth,["height"]=measuredHeight,["broken"]=isBroken,["point_count"]=above,["valid"]=bead.Count>0});
            if(bead.Count>0){Draw("Display Height Points","point3d",World(heightPoint.X,y,heightPoint.Y),!badHeight);Draw("Display Width Points","point3d",World(first.X,y,first.Y),!badWidth);Draw("Display Width Points","point3d",World(last.X,y,last.Y),!badWidth);Draw("Display Width Text","text3d",new(){["Position"]=World(first.X,y,first.Y),["Text"]=$"W={measuredWidth:0.###}",["_size"]=12},!badWidth);Draw("Display Height Text","text3d",new(){["Position"]=World(heightPoint.X,y,heightPoint.Y),["Text"]=$"H={measuredHeight:0.###}",["_size"]=12},!badHeight);}
            Draw("Display Width Polygons","trace3d",new(){["Points"]=profile.Select(q=>(object)World(q.X,y,q.Y)).ToList()},!isBroken);
            if(isBroken)Draw("Display Broken Text","text3d",new(){["Position"]=World(profile.Count==0?0:profile[0].X,y,reference),["Text"]="Broken",["_size"]=12},false);
        }
        bool ok=Accept(broken,brokenJudge)&&Accept(widthBad,widthJudge)&&Accept(heightBad,heightJudge);
        ctx.Vars.Set("result",ok?"OK":"NG");ctx.Vars.Set("width",widths);ctx.Vars.Set("height",heights);ctx.Vars.Set("sections",sections);ctx.Vars.Set("fracture_result",Accept(broken,brokenJudge));ctx.Vars.Set("width_result",Accept(widthBad,widthJudge));ctx.Vars.Set("height_result",Accept(heightBad,heightJudge));
        if(p.Bool("Pointset Cut")&&p.Bool("Use Region"))source.DisplayIndices=selected.ToArray();
        ctx.Log($"胶路轮廓：{count} 个剖面，断裂 {broken.Count(v=>v)}，结果 {(ok?"OK":"NG")}",false);return true;
    }
    private sealed record Judge(double Min,double Max,int Allowed,bool Continuous);
    private static Judge Validate(IReadOnlyDictionary<string,object> group,string axis)
    {
        double minimum=axis==null?0:ProfileMath.N(group,"Minimum "+axis,.1),maximum=axis==null?0:ProfileMath.N(group,"Maximum "+axis,1),allowed=ProfileMath.N(group,"Number of Outliers",axis==null?5:1);string mode=ProfileMath.S(group,"Judgement Mode",axis==null?"continuity":"arbitrary");
        if(minimum>maximum||minimum<0||allowed<0||allowed>int.MaxValue||allowed!=Math.Truncate(allowed)||mode is not("arbitrary" or "continuity"))throw new ArgumentException("胶路判定参数非法");return new(minimum,maximum,(int)allowed,mode=="continuity");
    }
    private static bool Accept(List<bool> bad,Judge judge)
    {if(!judge.Continuous)return bad.Count(v=>v)<=judge.Allowed;int consecutive=0;foreach(var v in bad){consecutive=v?consecutive+1:0;if(consecutive>judge.Allowed)return false;}return true;}
}
