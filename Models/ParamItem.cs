namespace AiVisionStudio.Models;

/// <summary>参数面板的一行：算子参数名 / 值 / 是否复杂（嵌套）。</summary>
public class ParamItem : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    private string _value = "";
    public string Value
    {
        get => _value;
        set { if (_value == value) return; _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
    }
    private string _error = "";
    public string Error
    {
        get => _error;
        set { _error = value; PropertyChanged?.Invoke(this, new(nameof(Error))); PropertyChanged?.Invoke(this, new(nameof(HasError))); }
    }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool IsComplex { get; set; }
    public string Kind { get; set; } = "text";   // text / number / bool / select / image
    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
}
