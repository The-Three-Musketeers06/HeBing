using System.Windows;
using System.Windows.Media;

namespace AiVisionStudio.Views;

/// <summary>Subtle geometric grid, drawn at native resolution for the ES workspace.</summary>
public sealed class TechBackdrop:FrameworkElement
{
    public TechBackdrop(){IsHitTestVisible=false;}
    protected override void OnRender(DrawingContext dc)
    {
        var width=ActualWidth;var height=ActualHeight;
        var line=new Pen(new SolidColorBrush(Color.FromArgb(19,35,133,164)),.6);
        for(var x=24d;x<width;x+=32)dc.DrawLine(line,new Point(x,0),new Point(x,height));
        for(var y=24d;y<height;y+=32)dc.DrawLine(line,new Point(0,y),new Point(width,y));
        var accent=new Pen(new SolidColorBrush(Color.FromArgb(75,0,159,178)),1.2);
        dc.DrawLine(accent,new Point(width-185,36),new Point(width-36,36));
        dc.DrawLine(accent,new Point(width-36,36),new Point(width-36,120));
        dc.DrawLine(accent,new Point(36,height-110),new Point(36,height-36));
        dc.DrawLine(accent,new Point(36,height-36),new Point(180,height-36));
        foreach(var p in new[]{new Point(width-185,36),new Point(width-36,120),new Point(36,height-110),new Point(180,height-36)})dc.DrawEllipse(Brushes.White,accent,p,3,3);
    }
}
