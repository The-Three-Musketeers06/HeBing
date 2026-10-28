using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AiVisionStudio.Views
{
    public partial class ImageView
    {
        private BitmapSource _pixelBitmap;
        private readonly byte[] _pixelBuffer = new byte[4];

        private void SetImageStatus(string imageName)
        {
            ImageNameText.Text = string.IsNullOrWhiteSpace(imageName) ? $"IM{ViewIndex}" : imageName;
            ImageStatusBar.Visibility = System.Windows.Visibility.Visible;

            UpdatePixelStatus(Host.IsMouseOver ? Mouse.GetPosition(Host) : null);
        }
        private void ClearImageStatus()
        {
            _pixelBitmap = null;
            ImageStatusBar.Visibility = System.Windows.Visibility.Collapsed;
            ImageNameText.Text = "";
            PixelInfoText.Text = "";
        }
        private void UpdatePixelStatus(Point?hostPosition)
        {
            if (_bitmap == null) return;

            var size = $"{_bitmap.PixelWidth}x{_bitmap.PixelHeight}";
            var text=$"{size}  | X:-- Y:--  | R:-- G:-- B:--";

            var inverse = Mat.Matrix;
            if (hostPosition is Point position&& position.X>=0&&position.Y>=0&&position.X<Host.ActualWidth&&position.Y<Host.ActualHeight&&inverse.HasInverse )
            {
                //将窗口坐标转换成原始图片坐标
                inverse.Invert();
                var imagePosition = inverse.Transform(position);
                if (imagePosition.X >= 0 && imagePosition.Y >= 0 && imagePosition.X < _bitmap.PixelWidth && imagePosition.Y < _bitmap.PixelHeight)
                {
                    var x = (int)Math.Floor(imagePosition.X);
                    var y = (int)Math.Floor(imagePosition.Y);

                    var (red, green, blue) = ReadImagePixel(x, y);

                    text = $"{size}  |  X:{x:D4} Y:{y:D4}"+ $"  |  R:{Value(red)} G:{Value(green)} B:{Value(blue)}";
                }
            }

            if(PixelInfoText.Text!=text) PixelInfoText.Text = text;
            static string Value(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private (double Red, double Green, double Blue) ReadImagePixel(
        int x, int y)
        {
            if (_rawImage != null)
            {
                // OpenCV 通道顺序是 BGR/BGRA。
                // 读取原始像素，不受灰度、反色、伪彩色显示影响。
                var pixel = _rawImage.Ptr(y, x);
                var blue = ReadChannel(0);

                // 灰度图：三个通道显示同一个灰度值。
                if (_rawImage.Channels() == 1)
                    return (blue, blue, blue);

                return (ReadChannel(2), ReadChannel(1), blue);

                double ReadChannel(int channel)
                {
                    var offset = channel * _rawImage.ElemSize1();
                    var depth = _rawImage.Depth();

                    if (depth == OpenCvSharp.MatType.CV_8U)
                        return Marshal.ReadByte(pixel, offset);

                    if (depth == OpenCvSharp.MatType.CV_8S)
                        return unchecked(
                            (sbyte)Marshal.ReadByte(pixel, offset));

                    if (depth == OpenCvSharp.MatType.CV_16U)
                        return unchecked(
                            (ushort)Marshal.ReadInt16(pixel, offset));

                    if (depth == OpenCvSharp.MatType.CV_16S)
                        return Marshal.ReadInt16(pixel, offset);

                    if (depth == OpenCvSharp.MatType.CV_32S)
                        return Marshal.ReadInt32(pixel, offset);

                    if (depth == OpenCvSharp.MatType.CV_32F)
                        return BitConverter.Int32BitsToSingle(
                            Marshal.ReadInt32(pixel, offset));

                    if (depth == OpenCvSharp.MatType.CV_64F)
                        return BitConverter.Int64BitsToDouble(
                            Marshal.ReadInt64(pixel, offset));

                    return (double)BitConverter.UInt16BitsToHalf(
                        unchecked(
                            (ushort)Marshal.ReadInt16(pixel, offset)));
                }
            }

            // 兼容通过 Load(path) 加载的 WPF 图片。
            if (_bitmap.Format == PixelFormats.Gray16)
            {
                _bitmap.CopyPixels(
                    new Int32Rect(x, y, 1, 1),
                    _pixelBuffer, 2, 0);

                var gray = BitConverter.ToUInt16(_pixelBuffer, 0);
                return (gray, gray, gray);
            }

            _pixelBitmap ??= new FormatConvertedBitmap(
                _bitmap, PixelFormats.Bgra32, null, 0);

            _pixelBitmap.CopyPixels(
                new Int32Rect(x, y, 1, 1),
                _pixelBuffer, 4, 0);

            return (
                _pixelBuffer[2],
                _pixelBuffer[1],
                _pixelBuffer[0]);
        }



    }
}
