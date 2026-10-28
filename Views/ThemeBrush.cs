using System.Windows;
using System.Windows.Media;

namespace AiVisionStudio.Views;

internal static class ThemeBrush
{
    public static Brush From(string hex)
    {
        var key=hex.ToUpperInvariant() switch
        {
            "#27272A"=>"Panel","#323233"=>"PanelHead","#2D3943"=>"AccentSoft","#4D98BD"=>"Accent",
            "#35353A"=>"Border","#D0D0D4" or "#C0C0C5"=>"Text","#9DA1A9"=>"TextDim","#29292D"=>"RowBg",_=>null
        };
        return key!=null&&Application.Current?.TryFindResource(key) is Brush brush?brush:(Brush)new BrushConverter().ConvertFromString(hex);
    }
}
