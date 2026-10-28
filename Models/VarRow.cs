using System.Collections.ObjectModel;
using System.Text.Json;
using System.Collections;
using System.Globalization;
using AiVisionStudio.Algorithm;

namespace AiVisionStudio.Models;

/// <summary>全局变量一行（名称/类型/值）。</summary>
public class VarRow
{
    public string Name { get; set; } = "";
    public string DisplayName => _outputDriver == null ? Name : OutputLabels.Translate(Name, _outputDriver);
    public string DisplayType => _outputDriver == null ? Type : Type switch
    { "bool" => "布尔", "float" or "double" => "实数", "int" => "整数", "string" => "文本", "array" => "列表", "group" => "参数组", "nil" => "空", _ => OutputLabels.Translate(Type, _outputDriver) };
    public string DisplayValue => _outputDriver != null && Type == "bool" && bool.TryParse(Value, out var flag) ? flag ? "是" : "否" : Value;
    public string Type { get; set; } = "";
    public string Value { get; set; } = "";
    public bool IsFail => Value != null && (Value.Contains("NG") || Value.Contains("False"));
    public ObservableCollection<VarRow> Children { get; } = new();
    private JsonElement? _native;
    private bool _loaded;
    private object _owned;
    private bool _hasOwned;
    private string _outputDriver;
    internal object OwnedValue => _hasOwned ? _owned : null;

    public static VarRow FromOwned(string name,object value,string outputDriver = null)
    {
        string type;
        if(value is PointSet3D points)
        {
            type="pointset";
            value=new Dictionary<string,object>{{"Point Count",points.Count}};
        }
        else if(value?.GetType().Name=="PlaneObj")
        {
            type="plane";
            value=new Dictionary<string,object>{{"Position",value.GetType().GetProperty("Position")?.GetValue(value)},
                {"Normal",value.GetType().GetProperty("Normal")?.GetValue(value)}};
        }
        else type=value switch
        {
            null=>"nil",bool=>"bool",byte or short or int or long or uint or ulong=>"int",
            float or double or decimal=>"float",IDictionary=>"group",IEnumerable when value is not string=>"array",_=>"string"
        };
        var count=value is ICollection collection?collection.Count:0;
        var row=new VarRow{Name=name,Type=type,Value=type is "group" or "plane" or "pointset"?"":type=="array"?$"[{count}]":value==null?"nil":Convert.ToString(value,CultureInfo.InvariantCulture),_owned=value,_hasOwned=true,_outputDriver=outputDriver};
        if(value is float or double or decimal)row.Value=Convert.ToDouble(value,CultureInfo.InvariantCulture).ToString("F"+AiVisionStudio.Services.StudioSettings.Current.DecimalPlaces,CultureInfo.InvariantCulture);
        if(type is "group" or "plane" or "pointset" || type=="array"&&count>0)row.Children.Add(new(){Name="…"});
        return row;
    }

    public static VarRow FromNative(JsonElement field, string name = null, string type = null, string groupType = null, string outputDriver = null)
    {
        bool descriptor = field.ValueKind == JsonValueKind.Object && field.TryGetProperty("type", out _);
        var value = descriptor && field.TryGetProperty("value", out var data) ? data : field;
        type ??= descriptor ? field.GetProperty("type").GetString() : value.ValueKind switch
        { JsonValueKind.Array => "array", JsonValueKind.Object => "group", JsonValueKind.Number => "float", JsonValueKind.True or JsonValueKind.False => "bool", _ => "string" };
        groupType ??= descriptor && field.TryGetProperty("group_type", out var gt) ? gt.GetString() : null;
        var row = new VarRow
        {
            Name = name ?? (descriptor && field.TryGetProperty("name", out var n) ? n.GetString() : ""),
            _outputDriver = outputDriver,
            Type = type == "group" && !string.IsNullOrEmpty(groupType) ? groupType : type,
            Value = value.ValueKind switch
            {
                JsonValueKind.Array => type == "array" ? $"[{value.GetArrayLength()}]" : "",
                JsonValueKind.Object => "", JsonValueKind.String => value.GetString(),
                JsonValueKind.Null => "nil", _ => value.GetRawText()
            },
            _native = descriptor ? field.Clone() : JsonSerializer.SerializeToElement(new { type, group_type = groupType, value })
        };
        if (value.ValueKind is JsonValueKind.Array && value.GetArrayLength() > 0 || value.ValueKind == JsonValueKind.Object)
            row.Children.Add(new VarRow { Name = "…" });
        return row;
    }

    public void Expand()
    {
        if(_loaded)return;
        if(_hasOwned)
        {
            _loaded=true;Children.Clear();
            if(_owned is IDictionary map)
                foreach(DictionaryEntry item in map)Children.Add(FromOwned(item.Key.ToString(),item.Value,_outputDriver));
            else if(_owned is IEnumerable items&&_owned is not string)
            {int index=0;foreach(var item in items)Children.Add(FromOwned($"[{index++}]",item,_outputDriver));}
            return;
        }
        if (!_native.HasValue) return;
        _loaded = true;
        Children.Clear();
        var field = _native.Value;
        var value = field.GetProperty("value");
        var type = field.GetProperty("type").GetString();
        var elementType = field.TryGetProperty("array_type", out var at) ? at.GetString() : null;
        var groupType = field.TryGetProperty("group_type", out var gt) ? gt.GetString() : null;
        if (value.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (var item in value.EnumerateArray())
                Children.Add(type == "array" ? FromNative(item, $"[{index++}]", elementType, groupType, _outputDriver) : FromNative(item, outputDriver: _outputDriver));
        }
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var item in value.EnumerateObject()) Children.Add(FromNative(item.Value, item.Name, outputDriver: _outputDriver));
    }
}
