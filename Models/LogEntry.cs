using System.Threading;
namespace AiVisionStudio.Models;

/// <summary>日志一行。</summary>
public class LogEntry
{
    private static long _nextsequence;  
    public long Sequence { get; } = Interlocked.Increment(ref _nextsequence);
    public string Time { get; set; } = "";
    public string Level { get; set; } = "info";
    public string Message { get; set; } = "";
    public bool IsError => Level == "error";
}
