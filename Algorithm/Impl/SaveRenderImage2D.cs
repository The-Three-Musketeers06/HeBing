using System.IO;
using AiVisionStudio.Services;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

public sealed class SaveRenderImage2DOp:IOperator
{
    public string Driver=>"Save Render Image";
    public bool Run(OpContext ctx,ParamBag p)
    {
        var input=p.Int("IM In");var image=ctx.Images.Get(input);if(image==null)throw new InvalidOperationException("没有可导出的 2D 图像");
        if(p.Str("Method","Fixed")!="Fixed")throw new NotSupportedException("结果存图当前支持固定文件路径，目录命名策略尚未实现");
        var path=ctx.ResolveOutputPath(p.Str("File"));if(string.IsNullOrWhiteSpace(path))throw new ArgumentException("请设置结果图像文件路径");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if(p.Bool("With Overlay"))ResultImageWriter2D.Write(image,ctx.NativeOverlays.Where(o=>o.ViewIndex==input||o.ViewIndex<0).ToArray(),path);else if(!Cv2.ImWrite(path,image))throw new IOException("结果图像写入失败");
        ctx.Vars.Set("File",path);ctx.Log("检测结果已保存："+path,false);return true;
    }
}
