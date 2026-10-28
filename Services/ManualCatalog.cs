using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AiVisionStudio.Services;

public sealed class ManualFeature
{
    public string Category {get;set;}="";
    public string Title {get;set;}="";
    public string Url {get;set;}="";
    public string Driver {get;set;}="";
    public string RelatedDrivers {get;set;}="";
    public string Status {get;set;}="";
    public string Evidence {get;set;}="";
    public string Gaps {get;set;}="";
}
public static class ManualCatalog
{
    public const string Home="https://ai-vision.tydi.fun/docs/";
    public static IReadOnlyList<ManualFeature> Read()
    {
        var path=Path.Combine(AppContext.BaseDirectory,"Assets","manual-audit.json");
        return File.Exists(path)?JsonSerializer.Deserialize<List<ManualFeature>>(File.ReadAllText(path),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??new():new();
    }
    public static string UrlFor(string driver)=>Read().FirstOrDefault(x=>x.Driver.Split(" | ").Contains(driver)||x.RelatedDrivers.Split(" / ").Contains(driver))?.Url??Home;
    public static void Open(string url)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="ai-vision.tydi.fun"||!uri.AbsolutePath.StartsWith("/docs/",StringComparison.Ordinal))throw new ArgumentException("手册链接无效");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});
    }
}
