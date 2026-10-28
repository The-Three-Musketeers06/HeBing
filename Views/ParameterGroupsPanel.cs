using System.Windows;
using System.Windows.Controls;
using AiVisionStudio.Models;

namespace AiVisionStudio.Views;

/// <summary>Keep every native parameter section visible while its body scrolls independently.</summary>
public sealed class ParameterGroupsPanel : Panel
{
    private double[] _heights = [];
    private readonly Dictionary<string, double> _weights = new();

    private string GroupKey(int index)
    {
        var group = (InternalChildren[index] as FrameworkElement)?.DataContext as ParameterGroup;
        return group?.Field ?? group?.Title ?? index.ToString();
    }

    private int GroupIndex(ParameterGroup group)
    {
        for (int i = 0; i < InternalChildren.Count; i++)
            if (ReferenceEquals((InternalChildren[i] as FrameworkElement)?.DataContext, group)) return i;
        return -1;
    }

    public bool CanResizeAfter(ParameterGroup group)
    {
        int index = GroupIndex(group);
        return index >= 0 && index < InternalChildren.Count - 1;
    }

    public void ResizeAfter(ParameterGroup group, double change)
    {
        int index = GroupIndex(group);
        if (index < 0 || _heights.Length != InternalChildren.Count || index + 1 >= _heights.Length || !double.IsFinite(change)) return;
        double total = _heights[index] + _heights[index + 1];
        if (total <= 0) return;
        double minimum = Math.Min(48, total / 2);
        double first = Math.Clamp(_heights[index] + change, minimum, total - minimum);
        for (int i = 0; i < _heights.Length; i++)
            _weights[GroupKey(i)] = i == index ? first : i == index + 1 ? total - first : _heights[i];
        InvalidateMeasure();
    }

    public void ResetSizes()
    {
        _weights.Clear();
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 480 : availableSize.Width;
        var weights = new double[InternalChildren.Count];
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var group = (child as FrameworkElement)?.DataContext as ParameterGroup;
            weights[i] = _weights.GetValueOrDefault(GroupKey(i), group?.Field == "basic" ? 3.0 : 1.0);
        }
        var height = double.IsInfinity(availableSize.Height) ? 780 : availableSize.Height;
        _heights = new double[weights.Length];
        var pending = Enumerable.Range(0, weights.Length).ToList();
        double remaining = height;
        double minimum = weights.Length == 0 ? 0 : Math.Min(48, height / weights.Length);
        while (pending.Count > 0)
        {
            double totalWeight = pending.Sum(index => weights[index]);
            var limited = pending.Where(index => remaining * weights[index] / totalWeight < minimum).ToArray();
            if (limited.Length == 0)
            {
                foreach (int index in pending) _heights[index] = remaining * weights[index] / totalWeight;
                break;
            }
            foreach (int index in limited)
            {
                _heights[index] = minimum;
                remaining -= minimum;
                pending.Remove(index);
            }
        }
        for (var i = 0; i < InternalChildren.Count; i++) InternalChildren[i].Measure(new Size(width, Math.Max(0, _heights[i])));
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var top = 0.0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var height = i < _heights.Length ? _heights[i] : 0;
            InternalChildren[i].Arrange(new Rect(0, top, finalSize.Width, Math.Max(0, height)));
            top += height;
        }
        return finalSize;
    }
}
