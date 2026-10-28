using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using AiVisionStudio.Services;

namespace AiVisionStudio.Models;

public sealed class ParameterGroup
{
    public string Title { get; init; }
    public string Field { get; init; }
    public ObservableCollection<ParameterNode> Items { get; } = new();
    public ObservableCollection<ParameterNode> AdvancedItems { get; } = new();
}

public sealed class OptionItem
{
    public string Value { get; init; }
    public string Label { get; init; }
    public override string ToString() => Label;
}

public sealed class ParameterBindingChoice
{
    public string Label { get; init; }
    public string OperationId { get; init; }
    public string Variable { get; init; }
    public bool IsGlobal { get; init; }
    public bool IsNone { get; init; }
    public override string ToString() => Label;
}

/// <summary>A typed parameter editor. Paths are segments, so dots in native names remain literal.</summary>
public sealed class ParameterNode : INotifyPropertyChanged
{
    public string Name { get; init; }
    public string OperationId { get; init; }
    public string DisplayName { get; init; }
    public string[] Path { get; init; } = [];
    public string PathLabel => string.Join(" / ", Path);
    public string Kind { get; init; } = "string";
    public string RegionCornerKey { get; init; }
    public ParamSpec Specification { get; init; }
    public ParameterNode Parent { get; init; }
    public ObservableCollection<ParameterNode> Children { get; } = new();
    public ObservableCollection<OptionItem> Options { get; } = new();
    public bool IsArray { get; init; }
    public bool IsGroup { get; init; }
    public bool IsBoolean => !IsGroup && Kind == "bool";
    public bool IsEnum => !IsGroup && (Kind == "enum" || Kind == "im");
    public bool IsFreeformEnum { get; set; }
    public bool IsImageInput { get; init; }
    public ImageInputReference ImageInputReference { get; set; }
    public Func<IReadOnlyList<ImageInputSource>> GetImageInputChoices { get; init; }
    public Action<ParameterNode, ImageInputSource> SelectImageInput { get; init; }
    private string _imageInputLabel = "请选择图像来源";
    public string ImageInputLabel { get => _imageInputLabel; set { if (_imageInputLabel == value) return; _imageInputLabel = value; Notify(); } }
    public bool IsNumeric => !IsGroup && Kind is "float" or "double" or "int" or "integer" or "number";
    public bool IsFile => Kind is "openfile" or "savefile" or "file" or "opendirectory" or "savedirectory";
    public bool IsMultiline => Kind == "script" || Name is "Script" or "Expression";
    public bool CanEditValue => IsEnabled && !IsBound;
    public Action<ParameterNode, string> Commit { get; init; }
    public RelayCommand IncrementCommand { get; }
    public RelayCommand DecrementCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand AddItemCommand { get; set; }
    public RelayCommand RemoveItemCommand { get; set; }

    public ParameterNode()
    {
        IncrementCommand = new RelayCommand(_ => Step(1), _ => CanEditValue && IsNumeric);
        DecrementCommand = new RelayCommand(_ => Step(-1), _ => CanEditValue && IsNumeric);
        BrowseCommand = new RelayCommand(_ => Browse(), _ => CanEditValue);
    }

    private string _value = "";
    public string Value
    {
        get => _value;
        set
        {
            value ??= "";
            if (_value == value) return;
            _value = value;
            NotifyValue();
            Commit?.Invoke(this, value);
        }
    }
    public void SetValueWithoutCommit(string value) { value ??= ""; if (_value == value) return; _value = value; NotifyValue(); }
    private void NotifyValue() { Notify(nameof(Value)); Notify(nameof(BoolValue)); Notify(nameof(SelectedOption)); }
    public bool BoolValue { get => bool.TryParse(Value, out var value) && value; set => Value = value ? "true" : "false"; }
    public OptionItem SelectedOption
    {
        get => Options.FirstOrDefault(x => x.Value == Value);
        set { if (value != null) Value = value.Value; }
    }
    private string _error = "";
    public string Error { get => _error; set { value ??= ""; if (_error == value) return; _error = value; Notify(); Notify(nameof(HasError)); } }
    public bool HasError => Error.Length != 0;
    private bool _isVisible = true;
    public bool IsVisible { get => _isVisible; set { if (_isVisible == value) return; _isVisible = value; Notify(); } }
    private bool _isEnabled = true;
    public bool IsEnabled { get => _isEnabled; set { if (_isEnabled == value) return; _isEnabled = value; Notify(); Notify(nameof(CanEditValue)); } }
    private bool _isBound;
    public bool IsBound { get => _isBound; set { if (_isBound == value) return; _isBound = value; Notify(); Notify(nameof(CanEditValue)); } }
    private bool _canBind;
    public bool CanBind { get => _canBind; set { if (_canBind == value) return; _canBind = value; Notify(); } }
    private string _bindingLabel = "未绑定";
    public string BindingLabel { get => _bindingLabel; set { if (_bindingLabel == value) return; _bindingLabel = value; Notify(); } }
    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set { _isExpanded = value; Notify(); } }

    private void Step(int direction)
    {
        if (!double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) number = 0;
        var step = Kind is "int" or "integer" ? 1.0 : 0.001;
        number += direction * step;
        if (Specification?.Minimum is {} min) number = Math.Max(min, number);
        if (Specification?.Maximum is {} max) number = Math.Min(max, number);
        Value = number.ToString("0.############", CultureInfo.InvariantCulture);
    }

    private void Browse()
    {
        if (Kind.EndsWith("directory", StringComparison.Ordinal))
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog();
            if (dialog.ShowDialog() == true) Value = dialog.FolderName;
        }
        else if (Kind == "savefile")
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = Value, Filter = FileFilter };
            if (dialog.ShowDialog() == true) Value = dialog.FileName;
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = FileFilter };
            if (dialog.ShowDialog() == true) Value = dialog.FileName;
        }
    }

    public string FileFilter
    {
        get
        {
            if (Specification?.Raw is not {} raw || !raw.TryGetProperty("filters", out var filters) || filters.ValueKind != System.Text.Json.JsonValueKind.Array) return "所有文件|*.*";
            var choices = new List<string>();
            foreach (var filter in filters.EnumerateArray())
            {
                if (!filter.TryGetProperty("extensions", out var extensions)) continue;
                var patterns = string.Join(";", extensions.EnumerateArray().Select(x => "*." + (x.GetString() ?? "*").TrimStart('*', '.')));
                if (patterns.Length == 0) continue;
                var name = filter.TryGetProperty("name", out var label) ? DriverCatalog.Translate(label.GetString()) : "支持的文件";
                choices.Add($"{name} ({patterns})|{patterns}");
            }
            return choices.Count == 0 ? "所有文件|*.*" : string.Join("|", choices) + "|所有文件|*.*";
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
    private void Notify([CallerMemberName] string property = null) => PropertyChanged?.Invoke(this, new(property));
}
