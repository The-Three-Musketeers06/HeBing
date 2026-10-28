using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

internal static class ImageRegions2D
{
    public static Mat Mask(OpContext ctx,ParamBag p,Size size,bool useRegion=false)
    {
        if(p.Str("ROI Region Type")=="Image type")
        {
            var image=ctx.Images.Get(p.Int("Mask Im In",1));if(image==null||image.Size()!=size)throw new InvalidOperationException("掩膜图像为空或尺寸不符");
            var mask=Img.ToGray8(image);Cv2.Threshold(mask,mask,0,255,ThresholdTypes.Binary);return mask;
        }
        if(p.Bool("Use Region",useRegion)&&p.Raw("Region")!=null)return BlobDetectOp.RegionMask(size,p.Raw("Region"),p);
        return new Mat(size,MatType.CV_8UC1,Scalar.White);
    }
}
