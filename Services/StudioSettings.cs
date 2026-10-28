using System.IO;
using System.Text.Json;

namespace AiVisionStudio.Services;

public sealed class StudioSettings
{
    public bool LoadProjectOnStartup {get;set;}
    public string ProjectPath {get;set;}="";
    public string ProgramName {get;set;}="";
    public bool OpenHmiOnStartup {get;set;}
    public bool RunOnStartup {get;set;}
    public int StartupDelayMs {get;set;}=1000;
    public bool FastMode {get;set;}
    public int DecimalPlaces {get;set;}=3;
    public static string SettingsPath=>Path.Combine(AppContext.BaseDirectory,"UserData","studio-settings.json");
    public static StudioSettings Current {get;private set;}=new();
    public static string Load(string path=null)
    {
        path??=SettingsPath;
        if(!File.Exists(path)){Current=new();return null;}
        try{var settings=JsonSerializer.Deserialize<StudioSettings>(File.ReadAllText(path))??throw new InvalidDataException("系统参数为空");settings.Validate(false);Current=settings;return null;}
        catch(Exception ex)when(ex is IOException or JsonException or ArgumentException){Current=new();return "系统参数未载入："+ex.Message;}
    }
    public void Validate(bool checkFile=true)
    {
        if(StartupDelayMs<0||StartupDelayMs>3600000)throw new ArgumentException("启动延时须为 0..3600000 毫秒");
        if(DecimalPlaces<3||DecimalPlaces>6)throw new ArgumentException("小数位数须在 3..6 范围");
        if((RunOnStartup||OpenHmiOnStartup)&&!LoadProjectOnStartup)throw new ArgumentException("自动运行或自动打开 HMI 需要启用自动加载工程");
        if(LoadProjectOnStartup&&(string.IsNullOrWhiteSpace(ProjectPath)||!Path.IsPathRooted(ProjectPath)||checkFile&&!File.Exists(ProjectPath)))throw new ArgumentException("请选择有效的工程绝对路径");
    }
    public void Save(string path=null)
    {
        Validate();path??=SettingsPath;var file=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary=file+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temporary,JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}));File.Move(temporary,file,true);Current=Copy();}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public StudioSettings Copy()=>(StudioSettings)MemberwiseClone();
}
