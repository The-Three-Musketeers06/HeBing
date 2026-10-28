using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AiVisionStudio.Models;

/// <summary>工具列表树节点：组（有 children）或算子（driver）。</summary>
public class ToolNode : INotifyPropertyChanged
{
    public string Label { get; set; } = "";
    public string Cn { get; set; } = "";
    public ObservableCollection<ToolNode> Children { get; } = new();
    public int Depth { get; set; }
    public int ToolCount => IsLeaf ? 1 : Children.Sum(n=>n.ToolCount);

    public bool IsLeaf => Children.Count == 0;
    /// <summary>叶子节点即驱动名。</summary>
    public string Driver => IsLeaf ? Label : null;

    public string Display => string.IsNullOrEmpty(Cn) || Cn == Label ? Label : Cn;

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); }
    }

    public event PropertyChangedEventHandler PropertyChanged;
}
