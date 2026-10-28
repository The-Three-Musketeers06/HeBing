using System.Windows;

namespace AiVisionStudio.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateBar();
    }

    private double _ratio;

    public void SetProgress(int current, int total, string component)
    {
        _ratio = total <= 0 ? 0 : Math.Clamp((double)current / total, 0, 1);
        StepText.Text = $"[{current}/{total}] {component}";
        UpdateBar();
    }

    private void UpdateBar()
    {
        var w = BarHost.ActualWidth;
        if (w > 1) Bar.Width = Math.Max(0, w * _ratio);
    }
}
