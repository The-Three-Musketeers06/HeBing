using System.Globalization;
using System.IO;
using System.Text.Json;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

internal static class CalibrationMatrices
{
    internal static double[] Identity()=>new double[]{1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1};
    internal static double[] Validate(IEnumerable<double> values)
    {
        var m=values.ToArray();if(m.Length==12)m=m.Concat(new double[]{0,0,0,1}).ToArray();
        if(m.Length!=16||m.Any(v=>!double.IsFinite(v))||Math.Abs(m[12])+Math.Abs(m[13])+Math.Abs(m[14])+Math.Abs(m[15]-1)>1e-8)throw new ArgumentException("标定矩阵必须为 12/16 个有限元素的齐次矩阵");
        var a=new MeasureVector3(m[0],m[4],m[8]);var b=new MeasureVector3(m[1],m[5],m[9]);var c=new MeasureVector3(m[2],m[6],m[10]);
        if(Math.Abs(a.Length-1)+Math.Abs(b.Length-1)+Math.Abs(c.Length-1)>1e-4||Math.Abs(a.Dot(b))+Math.Abs(a.Dot(c))+Math.Abs(b.Dot(c))>1e-4||Math.Abs(a.Cross(b).Dot(c)-1)>1e-4)throw new ArgumentException("标定矩阵不是右手刚体变换");return m;
    }
    internal static double[] Multiply(double[] a,double[] b){var r=new double[16];for(int i=0;i<4;i++)for(int j=0;j<4;j++)for(int k=0;k<4;k++)r[4*i+j]+=a[4*i+k]*b[4*k+j];return r;}
    internal static double[] Inverse(double[] a){var r=Identity();for(int i=0;i<3;i++)for(int j=0;j<3;j++)r[4*i+j]=a[4*j+i];for(int i=0;i<3;i++)r[4*i+3]=-(r[4*i]*a[3]+r[4*i+1]*a[7]+r[4*i+2]*a[11]);return r;}
    internal static double[] Read(string file)
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(file));var root=doc.RootElement;if(root.ValueKind==JsonValueKind.Object){if(root.TryGetProperty("A2B",out var values)||root.TryGetProperty("matrix",out values)||root.TryGetProperty("Matrix",out values))root=values;else throw new ArgumentException("标定文件缺少 A2B/matrix 字段");}
        if(root.ValueKind!=JsonValueKind.Array)throw new ArgumentException("标定矩阵必须为数字数组");return Validate(root.EnumerateArray().SelectMany(e=>e.ValueKind==JsonValueKind.Array?e.EnumerateArray().Select(v=>v.GetDouble()):new[]{e.GetDouble()}));
    }
    internal static Mat Rotation(double[] m){var r=new Mat(3,3,MatType.CV_64F);for(int i=0;i<3;i++)for(int j=0;j<3;j++)r.Set(i,j,m[i*4+j]);return r;}
    internal static Mat Translation(double[] m){var t=new Mat(3,1,MatType.CV_64F);for(int i=0;i<3;i++)t.Set(i,0,m[i*4+3]);return t;}
    internal static double[] From(Mat r,Mat t){var m=Identity();for(int i=0;i<3;i++){m[i*4+3]=t.At<double>(i);for(int j=0;j<3;j++)m[i*4+j]=r.At<double>(i,j);}return Validate(m);}
    internal static double[] RigidFit(IReadOnlyList<MeasureVector3> source,IReadOnlyList<MeasureVector3> target)
    {
        if(source.Count!=target.Count||source.Count<3)throw new ArgumentException("刚体标定至少需要三组对应点");var a=MetrologyMath.Mean(source);var b=MetrologyMath.Mean(target);using var h=new Mat(3,3,MatType.CV_64F,Scalar.All(0));
        for(int k=0;k<source.Count;k++){var u=source[k]-a;var v=target[k]-b;double[] x={u.X,u.Y,u.Z},y={v.X,v.Y,v.Z};for(int i=0;i<3;i++)for(int j=0;j<3;j++)h.Set(i,j,h.At<double>(i,j)+x[i]*y[j]);}
        using var w=new Mat();using var u1=new Mat();using var vt=new Mat();Cv2.SVDecomp(h,w,u1,vt);if(w.At<double>(1)<=Math.Max(1e-12,w.At<double>(0)*1e-10))throw new ArgumentException("标定对应点退化或共线");
        var m=Identity();double determinant=0;for(int i=0;i<3;i++)for(int j=0;j<3;j++)for(int k=0;k<3;k++)m[4*i+j]+=vt.At<double>(k,i)*u1.At<double>(j,k);for(int i=0;i<3;i++)m[4*i+i]-=1;
        determinant=new MeasureVector3(m[0],m[4],m[8]).Cross(new(m[1],m[5],m[9])).Dot(new(m[2],m[6],m[10]));
        if(determinant<0)for(int i=0;i<3;i++)for(int j=0;j<3;j++)m[4*i+j]-=2*vt.At<double>(2,i)*u1.At<double>(j,2);
        m[3]=b.X-m[0]*a.X-m[1]*a.Y-m[2]*a.Z;m[7]=b.Y-m[4]*a.X-m[5]*a.Y-m[6]*a.Z;m[11]=b.Z-m[8]*a.X-m[9]*a.Y-m[10]*a.Z;return Validate(m);
    }
}

public sealed class CalibrationMatrixCompletionOp:IOperator
{
    public string Driver=>"3D CalibrationMatrixComputation";
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;int count=p.Int("Number of Calibration Matrices",2);if(count<2||count>7)throw new ArgumentException("标定矩阵数量必须为 2..7");var output=new List<object>();var cumulative=CalibrationMatrices.Identity();
        for(int i=1;i<=count;i++)
        {
            var g=p.Group("Matrix "+i)??throw new ArgumentException("缺少标定矩阵 "+i);double[] m;var source=g.GetValueOrDefault("Data Source")?.ToString()??"From Matrix";
            if(source=="From File"){var path=g.GetValueOrDefault("File Path")?.ToString();if(string.IsNullOrWhiteSpace(path))throw new ArgumentException("标定文件路径为空");m=CalibrationMatrices.Read(ctx.ResolvePath(path));}
            else if(source=="From Matrix"&&g.GetValueOrDefault("Matrix") is IEnumerable<object> a)m=CalibrationMatrices.Validate(a.Select(Convert.ToDouble));else throw new ArgumentException("标定矩阵数据源无效");
            cumulative=CalibrationMatrices.Multiply(cumulative,m);output.Add(cumulative.Cast<object>().ToList());
        }
        var pathOut=p.Str("Calibration File Path");if(string.IsNullOrWhiteSpace(pathOut))throw new ArgumentException("输出标定文件路径不能为空");var file=ctx.ResolveOutputPath(pathOut);Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var document=new Dictionary<string,object>{{"format","ES-Vision-Calibration-Chain-1"},{"matrices",output}};for(int i=0;i<output.Count;i++)document[((char)('B'+i))+"2A"]=(List<object>)output[i];
        var temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllText(temp,JsonSerializer.Serialize(document,new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,file,true);}finally{if(File.Exists(temp))File.Delete(temp);}
        ctx.Vars.Set("matrices",output);ctx.Vars.Set("matrix",output[^1]);MetrologyMath.Warning(ctx,"输入相邻相机到前一相机的刚体矩阵，按顺序累乘；输出为 ES 具名矩阵集合，原版集合文件兼容性待验证");return true;
    }
}

public sealed class HandEyeCalibrationCompletionOp(bool eyeInHand):IOperator
{
    public string Driver=>"3D HandEyeCalibration "+(eyeInHand?"EyeInHand":"EyeToHand");
    private static double[] Tcp(string path)
    {
        var text=string.Join(" ",File.ReadLines(path).Select(l=>l.Split('#')[0]));var a=text.Split((char[])null,StringSplitOptions.RemoveEmptyEntries).Select(v=>double.Parse(v,CultureInfo.InvariantCulture)).ToArray();if(a.Length!=7||a.Any(v=>!double.IsFinite(v)))throw new ArgumentException("TCP 文件必须为 x y z W X Y Z 七个有限值");
        double w=a[3],x=a[4],y=a[5],z=a[6],norm=Math.Sqrt(w*w+x*x+y*y+z*z);if(norm<1e-12)throw new ArgumentException("TCP 四元数为零");w/=norm;x/=norm;y/=norm;z/=norm;
        return CalibrationMatrices.Validate(new[]{1-2*(y*y+z*z),2*(x*y-z*w),2*(x*z+y*w),a[0],2*(x*y+z*w),1-2*(x*x+z*z),2*(y*z-x*w),a[1],2*(x*z-y*w),2*(y*z+x*w),1-2*(x*x+y*y),a[2],0,0,0,1});
    }
    public bool Run(OpContext ctx,ParamBag p)
    {
        p.Context??=ctx;string relative=p.Group("Data Source Settings")?.GetValueOrDefault("Calibration Directory")?.ToString();if(string.IsNullOrWhiteSpace(relative))throw new ArgumentException("标定目录为空");string directory=ctx.ResolvePath(relative);if(!Directory.Exists(directory))throw new ArgumentException("标定目录不存在");
        if(p.Str("Calibration Object Type","chessboard")!="chessboard")throw new NotSupportedException("自有手眼标定目前需要棋盘格标定对象");if(p.Str("Camera Coordinate System Type","right handed system")!="right handed system")throw new NotSupportedException("左手相机坐标须先明确反射轴再转换，不能直接生成右手刚体位姿");
        var parameters=p.Group("Chessboard Parameters");int rows=(int)C3.N(parameters,"Rows",17),columns=(int)C3.N(parameters,"Columns",19);double square=C3.N(parameters,"Square Size(millimeter)",16);if(rows<2||columns<2||rows*columns>10000||square<=0||!double.IsFinite(square))throw new ArgumentException("棋盘格尺寸无效");
        var grips=new List<double[]>();var boards=new List<double[]>();var rotations=new List<Mat>();var translations=new List<Mat>();var targetRotations=new List<Mat>();var targetTranslations=new List<Mat>();Point2d? precedingRowDirection=null;
        try
        {
            foreach(var sample in Directory.GetDirectories(directory).Order(StringComparer.Ordinal))
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();string Pick(string pattern){var files=Directory.GetFiles(sample,pattern);if(files.Length!=1)throw new ArgumentException($"标定样本 {Path.GetFileName(sample)} 必须恰有一个 {pattern}");return files[0];}
                var tcp=Tcp(Pick("*.txt"));var imageFiles=Directory.GetFiles(sample).Where(f=>Path.GetExtension(f).Equals(".png",StringComparison.OrdinalIgnoreCase)||Path.GetExtension(f).Equals(".jpg",StringComparison.OrdinalIgnoreCase)||Path.GetExtension(f).Equals(".bmp",StringComparison.OrdinalIgnoreCase)).ToArray();if(imageFiles.Length!=1)throw new ArgumentException("标定样本必须恰有一张 RGB 图像");
                using var image=Cv2.ImRead(imageFiles[0],ImreadModes.Grayscale);if(image.Empty())throw new ArgumentException("无法读取标定图片");
                // Native fixtures use Rows as the horizontal corner count and Columns vertically.
                var pattern=new Size(rows,columns);bool found=Cv2.FindChessboardCornersSB(image,pattern,out Point2f[] corners,ChessboardFlags.NormalizeImage);
                if(!found){found=Cv2.FindChessboardCorners(image,pattern,out corners,ChessboardFlags.AdaptiveThresh|ChessboardFlags.NormalizeImage);if(found)corners=Cv2.CornerSubPix(image,corners,new Size(5,5),new Size(-1,-1),new TermCriteria(CriteriaTypes.Count|CriteriaTypes.Eps,40,.001));}
                if(!found||corners.Length!=rows*columns)throw new ArgumentException("未检测到完整棋盘格："+sample);
                // A symmetric chessboard has a 180-degree indexing ambiguity. SB and the
                // classic detector can choose opposite starts. Preserve row-axis continuity
                // across acquisition order instead of feeding artificial 180-degree jumps
                // into the hand-eye equations. Closed-loop residual still validates the fit.
                var rowDirection=new Point2d(corners[rows-1].X-corners[0].X,corners[rows-1].Y-corners[0].Y);
                if(precedingRowDirection is {} previous&&previous.X*rowDirection.X+previous.Y*rowDirection.Y<0){Array.Reverse(corners);rowDirection=new(-rowDirection.X,-rowDirection.Y);}
                precedingRowDirection=rowDirection;
                var cloud=PcdIO.Load(Pick("*.pcd"));var raster=cloud.OrderedGrid;if(raster==null||raster.Width!=image.Width||raster.Height!=image.Height)throw new ArgumentException("标定图像与有序 PCD 必须具有相同像素尺寸和行列对应关系");
                int imageWidth=image.Width,imageHeight=image.Height;var source=new List<MeasureVector3>();var target=new List<MeasureVector3>();
                for(int i=0;i<corners.Length;i++)
                {
                    int x=(int)Math.Round(corners[i].X),y=(int)Math.Round(corners[i].Y);var neighbors=new List<(MeasureVector3 P,double W)>();
                    for(int yy=Math.Max(0,y-1);yy<=Math.Min(imageHeight-1,y+1);yy++)for(int xx=Math.Max(0,x-1);xx<=Math.Min(imageWidth-1,x+1);xx++){int index=raster.PointIndices[yy*raster.Width+xx];if(index<0)continue;double weight=1/(.01+Math.Pow(xx-corners[i].X,2)+Math.Pow(yy-corners[i].Y,2));neighbors.Add((MetrologyMath.At(cloud,index),weight));}
                    if(neighbors.Count<3)continue;double weights=neighbors.Sum(q=>q.W);var point=new MeasureVector3(neighbors.Sum(q=>q.P.X*q.W)/weights,neighbors.Sum(q=>q.P.Y*q.W)/weights,neighbors.Sum(q=>q.P.Z*q.W)/weights);if(!point.Finite)continue;
                    source.Add(new(i%rows*square,i/rows*square,0));target.Add(point);
                }
                if(source.Count<rows*columns*.8)throw new ArgumentException("有效棋盘格深度角点不足 80%："+sample);var board=CalibrationMatrices.RigidFit(source,target);var grip=eyeInHand?tcp:CalibrationMatrices.Inverse(tcp);
                grips.Add(grip);boards.Add(board);rotations.Add(CalibrationMatrices.Rotation(grip));translations.Add(CalibrationMatrices.Translation(grip));targetRotations.Add(CalibrationMatrices.Rotation(board));targetTranslations.Add(CalibrationMatrices.Translation(board));
                ctx.Log($"手眼标定样本 {Path.GetFileName(sample)}：{source.Count} 个有效角点",false);
            }
            if(grips.Count<3)throw new ArgumentException("手眼标定至少需要三组具有不同旋转轴的有效样本");
            double[] calibration=null;double rms=double.PositiveInfinity;string solver="";
            foreach(var method in new[]{HandEyeCalibrationMethod.PARK,HandEyeCalibrationMethod.TSAI,HandEyeCalibrationMethod.HORAUD})
            {
                ctx.CancellationToken.ThrowIfCancellationRequested();using var r=new Mat();using var t=new Mat();double[] candidate;
                try{Cv2.CalibrateHandEye(rotations,translations,targetRotations,targetTranslations,r,t,method);candidate=CalibrationMatrices.From(r,t);}catch(ArgumentException){continue;}catch(OpenCVException){continue;}
                var fixedPoses=grips.Select((g,i)=>CalibrationMatrices.Multiply(CalibrationMatrices.Multiply(g,candidate),boards[i])).ToArray();var centers=fixedPoses.Select(m=>new MeasureVector3(m[3],m[7],m[11])).ToArray();var mean=MetrologyMath.Mean(centers);double error=Math.Sqrt(centers.Average(q=>(q-mean).Dot(q-mean)));
                if(double.IsFinite(error)&&error<rms){calibration=candidate;rms=error;solver=method.ToString();}
            }
            if(calibration==null)throw new ArgumentException("手眼求解未产生有效右手刚体矩阵，请检查机器人运动是否具有至少两个独立旋转轴");
            if(!double.IsFinite(rms)||rms>square)throw new ArgumentException($"手眼标定闭环平移残差 {rms:0.000} mm 超过一格尺寸，请检查角点顺序、同步与单位");
            ctx.Vars.Set("matrix",calibration.Cast<object>().ToList());ctx.Vars.Set("sample_count",grips.Count);ctx.Vars.Set("closure_rms_mm",rms);ctx.Vars.Set("calibration_method",solver);MetrologyMath.Warning(ctx,$"自有棋盘格 3D 角点拟合与 {solver} 求解；角点方向按采集顺序连续化，要求相邻棋盘图像转角小于90°；{grips.Count} 样本闭环 RMS={rms:0.000} mm，实际应用前需独立校验");return true;
        }
        finally{foreach(var mat in rotations.Concat(translations).Concat(targetRotations).Concat(targetTranslations))mat.Dispose();}
    }
}
