using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AiVisionStudio.Models;

public enum OpState { None, Running, Ok, Fail, Disabled }

/// <summary>运行列表里的一行算子。</summary>
public class Operation : INotifyPropertyChanged
{
    public string Uuid { get; set; } = Guid.NewGuid().ToString();
    public string Driver { get; set; } = "";
    public int Version { get; set; } = 1;
    private string _name = "";
    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public string Comment { get; set; } = "";
    private bool _enabled = true;
    public bool Enabled { get => _enabled; set { _enabled = value; OnPropertyChanged(); } }
    private bool _breakpoint;
    public bool Breakpoint { get => _breakpoint; set { _breakpoint = value; OnPropertyChanged(); } }
    /// <summary>原始 YAML 参数（parameters 节），第一版原样保留。</summary>
    public object ParametersRaw { get; set; }
    public YamlDotNet.RepresentationModel.YamlMappingNode OriginalRaw { get; set; }

    private int _index;
    public int Index
    {
        get => _index;
        set { _index = value; OnPropertyChanged(); }
    }

    private double _elapsedMs;
    public double ElapsedMs
    {
        get => _elapsedMs;
        set { _elapsedMs = value; OnPropertyChanged(); OnPropertyChanged(nameof(ElapsedText)); }
    }

    public string ElapsedText => _elapsedMs <= 0 ? "" : _elapsedMs < 1 ? "<1" : _elapsedMs.ToString("0");

    private OpState _state = OpState.None;
    public OpState State
    {
        get => _state;
        set
        {
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateBrushKey));
            OnPropertyChanged(nameof(IsOk));
            OnPropertyChanged(nameof(IsFail));
        }
    }

    public string StateBrushKey => State switch
    {
        OpState.Ok => "Ok",
        OpState.Fail => "Fail",
        OpState.Running => "Warn",
        _ => "TextDim"
    };

    public bool IsOk => State == OpState.Ok;
    public bool IsFail => State == OpState.Fail;

    /// <summary>最近一次失败原因（验证/诊断用）。</summary>
    private string _errorMessage;
    public string ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
