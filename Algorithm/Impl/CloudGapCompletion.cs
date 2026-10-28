using System.IO;
using OpenCvSharp;
using AiVisionStudio.Services;

namespace AiVisionStudio.Algorithm.Impl;

internal static class CloudGapCompletionRegistration
{
    public static void Register(){OperatorRegistry.Register(new TiffPointCloudOp());OperatorRegistry.Register(new CloudInterpolationOp());OperatorRegistry.Register(new CloudHoleFillOp());OperatorRegistry.Register(new CloudMultiSpliceOp());}
}

public sealed class TiffPointCloudOp:IOperator
{
    public string Driver=>"TIFFToPointCloud";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var images=new List<Mat>();var mode=p.Str("Mode Selection","Three-channel Mode");
        Mat Read(string key,int channels){var path=ctx.ResolvePath(p.Str(key));if(path==null||!File.Exists(path))throw new FileNotFoundException("TIFF 坐标图不存在："+key,path);if(Path.GetExtension(path).ToLowerInvariant() is not(".tiff" or ".tif"))throw new ArgumentException("坐标图必须为 TIFF");using var m=Cv2.ImRead(path,ImreadModes.Unchanged);if(m.Empty()||m.Channels()!=channels)throw new ArgumentException($"{key} 需要 {channels} 通道 TIFF");var result=new Mat();m.ConvertTo(result,MatType.CV_32F);images.Add(result);return result;}
        try
        {
            Mat x=null,y=null,z=null,xyz=null;
            if(mode=="Single-channel Mode"){x=Read("Image X",1);y=Read("Image Y",1);z=Read("Image Z",1);if(x.Size()!=y.Size()||x.Size()!=z.Size())throw new ArgumentException("XYZ 坐标图尺寸必须相同");}
            else if(mode=="Three-channel Mode")xyz=Read("Image XYZ",3);else throw new ArgumentException("未知 TIFF 加载模式");
            int width=(xyz??x).Width,height=(xyz??x).Height;if((long)width*height>16_000_000)throw new ArgumentException("TIFF 点云超过 1600 万点");
            bool remove=p.Group("Remove Invalid Points")?.GetValueOrDefault("Enable") is true;double invalid=p.GroupDbl("Remove Invalid Points","Invalid Value",0);
            var result=new PointCloud();var cells=Enumerable.Repeat(-1,width*height).ToArray();
            for(int row=0;row<height;row++){ctx.CancellationToken.ThrowIfCancellationRequested();for(int col=0;col<width;col++){
                // OpenCV exposes TIFF RGB samples in BGR order. XYZ is stored as RGB = X,Y,Z.
                var v=xyz==null?new Vec3f(x.At<float>(row,col),y.At<float>(row,col),z.At<float>(row,col)):xyz.At<Vec3f>(row,col);float xx=xyz==null?v.Item0:v.Item2,yy=v.Item1,zz=xyz==null?v.Item2:v.Item0;
                if(!float.IsFinite(xx)||!float.IsFinite(yy)||!float.IsFinite(zz)||remove&&zz==invalid)continue;
                cells[row*width+col]=result.Count;result.Add(xx,yy,zz);
            }}
            result.OrderedGrid=new(width,height,cells);CloudSetOp.Publish(ctx,p.Int("IM Out"),result);ctx.Vars.Set("Width",width);ctx.Vars.Set("Height",height);return true;
        }
        finally{foreach(var image in images)image.Dispose();}
    }
}

public sealed class CloudInterpolationOp:IOperator
{
    public string Driver=>"3D Interpolation";
    public bool Run(OpContext ctx,ParamBag p)
    {
        var source=CloudSetOp.Require(ctx,p.Int("IM In"));var grid=source.OrderedGrid??(source.Raster is {} r?new CloudPointGrid(r.Width,r.Height,r.PointIndices):null)??throw new ArgumentException("仅支持有序点云，请先运行点云有序化");
        int nx=p.Int("Number of Interpolation Points Each Row"),ny=p.Int("Number of Interpolation Points Each Column");if(nx is <0 or >3||ny is <0 or >3)throw new ArgumentException("每行/列插值数量须为 0..3");
        if(p.Str("Algorithm","Nearest Neighbour")!="Nearest Neighbour")throw new ArgumentException("仅支持 Nearest Neighbour");
        string kind=p.Str("Nearest Neighbour Number","4 neighbour");int radius=kind=="24 neighbour"?2:kind is "4 neighbour" or "8 neighbour"?1:throw new ArgumentException("无效邻域类型");
        int width=checked((grid.Width-1)*(nx+1)+1),height=checked((grid.Height-1)*(ny+1)+1);if((long)width*height>16_000_000)throw new ArgumentException("插值输出超过 1600 万点");
        var result=new PointCloud();var mapping=Enumerable.Repeat(-1,width*height).ToArray();
        for(int row=0;row<height;row++){ctx.CancellationToken.ThrowIfCancellationRequested();for(int col=0;col<width;col++){
            double gx=(double)col/(nx+1),gy=(double)row/(ny+1);int ix=(int)gx,iy=(int)gy;
            int original=grid.PointIndices[iy*grid.Width+ix];
            if(col%(nx+1)==0&&row%(ny+1)==0){if(original>=0){mapping[row*width+col]=result.Count;result.Add(source.X[original],source.Y[original],source.Z[original],source.I[original]);}continue;}
            var candidates=new List<(int Index,double Distance,int X,int Y)>();
            for(int yy=Math.Max(0,iy-radius+1);yy<=Math.Min(grid.Height-1,iy+radius);yy++)for(int xx=Math.Max(0,ix-radius+1);xx<=Math.Min(grid.Width-1,ix+radius);xx++){
                if(kind=="4 neighbour"&&Math.Abs(xx-gx)+Math.Abs(yy-gy)>1.5)continue;int index=grid.PointIndices[yy*grid.Width+xx];if(index>=0)candidates.Add((index,(xx-gx)*(xx-gx)+(yy-gy)*(yy-gy),xx,yy));
            }
            if(candidates.Count==0)continue;var closest=candidates.MinBy(q=>q.Distance);double sx=0,sy=0,weight=0;
            // Bilinear XY locates the inserted sample; nearest valid sample supplies Z/intensity.
            foreach(var q in candidates){double w=Math.Max(0,1-Math.Abs(q.X-gx))*Math.Max(0,1-Math.Abs(q.Y-gy));sx+=w*source.X[q.Index];sy+=w*source.Y[q.Index];weight+=w;}
            if(weight<=0)continue;mapping[row*width+col]=result.Count;result.Add((float)(sx/weight),(float)(sy/weight),source.Z[closest.Index],source.I[closest.Index]);
        }}
        result.OrderedGrid=new(width,height,mapping);if(source.Raster is {} raster)result.Raster=new(width,height,raster.OriginX,raster.OriginY,raster.ResolutionX/(nx+1),raster.ResolutionY/(ny+1),mapping);
        CloudSetOp.Publish(ctx,p.Int("IM Out"),result);ctx.Vars.Set("Width",width);ctx.Vars.Set("Height",height);return true;
    }
}

public sealed class CloudHoleFillOp:IOperator
{
    public string Driver=>"3D HoleFillingFilter";
    public bool Run(OpContext ctx,ParamBag p)
    {
        var source=CloudSetOp.Require(ctx,p.Int("IM In"));double step=p.Dbl("Mesh Length",.05);int radius=p.Int("Search Radius",5);if(!double.IsFinite(step)||step<=0||radius is <0 or >999)throw new ArgumentException("网格长度须为正数，搜索半径须为 0..999");
        var (cloud,grid)=CloudRasterizer.Build(ctx,source,Enumerable.Range(0,source.Count).ToArray(),step,step,"Mean");var result=new PointCloud();var indices=Enumerable.Repeat(-1,grid.PointIndices.Length).ToArray();int filled=0;
        for(int y=0;y<grid.Height;y++){ctx.CancellationToken.ThrowIfCancellationRequested();for(int x=0;x<grid.Width;x++){
            int cell=y*grid.Width+x,index=grid.PointIndices[cell];double z=0,intensity=0;int pairs=0;
            if(index>=0){z=cloud.Z[index];intensity=cloud.I[index];}
            else{
                foreach(var (vx,vy) in new[]{(1,0),(0,1),(1,1),(1,-1)}){
                    (int Index,int Step) Find(int sign){for(int k=1;k<=radius;k++){int xx=x+vx*k*sign,yy=y+vy*k*sign;if(xx<0||yy<0||xx>=grid.Width||yy>=grid.Height)break;int a=grid.PointIndices[yy*grid.Width+xx];if(a>=0)return(a,k);}return(-1,0);}
                    var a=Find(-1);var b=Find(1);if(a.Index<0||b.Index<0)continue;double t=(double)a.Step/(a.Step+b.Step);z+=cloud.Z[a.Index]*(1-t)+cloud.Z[b.Index]*t;intensity+=cloud.I[a.Index]*(1-t)+cloud.I[b.Index]*t;pairs++;
                }
                if(pairs==0)continue;z/=pairs;intensity/=pairs;filled++;
            }
            indices[cell]=result.Count;result.Add((float)(grid.OriginX+x*step),(float)(grid.OriginY+y*step),(float)z,(float)intensity);
        }}
        result.Raster=grid with{PointIndices=indices};result.OrderedGrid=new(grid.Width,grid.Height,indices);CloudSetOp.Publish(ctx,p.Int("IM Out"),result);ctx.Vars.Set("Filled Points",filled);return true;
    }
}

public sealed class CloudMultiSpliceOp:IOperator
{
    public string Driver=>"3D MultiSplicingFilter";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;var result=CloudSetOp.Require(ctx,p.Int("IM Target",3)).Clone();double threshold=p.Dbl("Distance Threshold",.01);if(!double.IsFinite(threshold)||threshold<0)throw new ArgumentException("融合阈值无效");
        for(int input=1;input<=3;input++){
            if(input>1&&!p.Bool("Enable IM In"+input))continue;var source=CloudSetOp.Require(ctx,p.Int("IM In"+input,input-1));
            if(p.Raw("Transform Matrix"+input) is not IEnumerable<object> values)throw new ArgumentException("缺少变换矩阵"+input);var m=values.Select(Convert.ToDouble).ToArray();if(m.Length!=16||m.Any(v=>!double.IsFinite(v))||Math.Abs(m[12])+Math.Abs(m[13])+Math.Abs(m[14])+Math.Abs(m[15]-1)>1e-8)throw new ArgumentException("需要 4×4 仿射矩阵");
            for(int i=0;i<source.Count;i++){if((i&4095)==0)ctx.CancellationToken.ThrowIfCancellationRequested();double x=source.X[i],y=source.Y[i],z=source.Z[i];result.Add((float)(m[0]*x+m[1]*y+m[2]*z+m[3]),(float)(m[4]*x+m[5]*y+m[6]*z+m[7]),(float)(m[8]*x+m[9]*y+m[10]*z+m[11]),source.I[i]);}
        }
        var groups=threshold==0?Enumerable.Range(0,result.Count).GroupBy(result.At).Select(g=>g.ToList()).ToList():CloudComponents.Find(result,Enumerable.Range(0,result.Count),threshold,ctx.CancellationToken);
        var output=new PointCloud();foreach(var group in groups)output.Add((float)group.Average(i=>(double)result.X[i]),(float)group.Average(i=>(double)result.Y[i]),(float)group.Average(i=>(double)result.Z[i]),(float)group.Average(i=>(double)result.I[i]));CloudSetOp.Publish(ctx,p.Int("IM Out"),output);return true;
    }
}
