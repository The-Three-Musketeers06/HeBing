using AiVisionStudio.Algorithm;
using AiVisionStudio.Models;
using OpenCvSharp;

namespace AiVisionStudio.Views;

/// <summary>The HMI consumes the same project state and execution commands as the studio.</summary>
public interface IHmiRuntimeContext
{
    object Read(string reference);
    bool Write(string reference, object value, out string error);
    Mat Image(int index);
    PointCloud Cloud(int index);
    ProfileData Profile(int index) => null;
    IReadOnlyList<NativeOverlay3D> NativeOverlays(int index) => Array.Empty<NativeOverlay3D>();
    IReadOnlyList<Box3> BoxOverlays(int index) => Array.Empty<Box3>();
    IReadOnlyList<Text3D> TextOverlays(int index) => Array.Empty<Text3D>();
    Task ExecuteAsync(string action, HmiElement element = null);
    string Status { get; }
    bool IsRunning { get; }
    event EventHandler Changed;
    IReadOnlyList<IReadOnlyDictionary<string, object>> QueryTable(string name, DateTime? from = null, DateTime? to = null);
    IReadOnlyList<string> BindingReferences() => Array.Empty<string>();
    IReadOnlyList<string> ProgramNames() => Array.Empty<string>();
    IReadOnlyList<(string Id, string Name)> ProgramOperators(string program) => Array.Empty<(string, string)>();
}
