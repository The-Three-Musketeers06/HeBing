using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AiVisionStudio.Views;

public sealed class RunIconConverter:IValueConverter
{
    public object Convert(object value,Type type,object parameter,CultureInfo culture)
    {
        var driver=value?.ToString()??"";
        var path=driver switch
        {
            "LoadImage" or "SaveImage"=>"M1,2 L17,2 L17,16 L1,16 Z M2,15 L7,9 L11,13 L14,8 L17,11 M5,5 L6,5 L6,6 L5,6 Z",
            "LoadPointCloud"=>"M1,1 L17,1 L17,17 L1,17 Z M9,3 L9,6 M9,12 L9,15 M3,9 L6,9 M12,9 L15,9 M4,4 L6,6 M12,12 L14,14 M4,14 L6,12 M12,6 L14,4",
            "Basic Blob Detect" or "3D Defect"=>"M1,3 L17,3 L17,15 L1,15 Z M3,8 A3,3 0 1,0 9,8 A3,3 0 1,0 3,8 M11,11 A2,2 0 1,0 15,11 A2,2 0 1,0 11,11",
            "Basic Line Find"=>"M2,15 L15,3 M2,11 L6,15 M6,7 L10,11 M10,3 L14,7",
            "Basic Circle Find"=>"M2,9 A7,7 0 1,0 16,9 A7,7 0 1,0 2,9 M9,1 L9,4 M9,14 L9,17 M1,9 L4,9 M14,9 L17,9",
            "Lua"=>"M1,2 L17,2 L17,16 L1,16 Z M6,6 L3,9 L6,12 M12,6 L15,9 L12,12 M10,5 L8,13",
            "Group" or "End Group"=>"M2,4 L8,9 L2,14 M8,4 L14,9 L8,14",
            _ when driver.StartsWith("3D")=>"M3,6 L9,2 L15,6 L15,13 L9,17 L3,13 Z M3,6 L9,10 L15,6 M9,10 L9,17",
            _=>"M1,3 L17,3 L17,15 L1,15 Z M5,7 L13,7 M5,11 L10,11"
        };
        var geometry=Geometry.Parse(path);geometry.Freeze();return geometry;
    }
    public object ConvertBack(object value,Type type,object parameter,CultureInfo culture)=>Binding.DoNothing;
}
