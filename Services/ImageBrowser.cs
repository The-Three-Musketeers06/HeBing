using System.IO;
using AiVisionStudio.Algorithm;
using AiVisionStudio.Models;
using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Services;

public sealed record ImageBrowserSource(string Key, string[] Files, string CurrentFile, string OperationId);

public partial class MainViewModel
{
    private readonly Dictionary<string, (string Path, long Stamp, string[] Files)> _imageFileLists = new();
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".bmp", ".jpeg", ".jpg", ".jpe", ".png", ".tiff", ".tif" };

    private Operation ImageInputOperation(int viewIndex, string operationId)
    {
        if (SelectedProgram == null) return null;
        var operations = SelectedProgram.Operations;
        if (operationId != null)
        {
            var inputs = _engine.GetImageInputSources(operationId);
            return operations.LastOrDefault(operation => operation.Driver == "LoadImage" && inputs.Contains(operation.Uuid));
        }
        return operations.LastOrDefault(operation => operation.Driver == "LoadImage"
            && ParamBag.FromYaml(operation.ParametersRaw as YamlNode, null, _engine.Context).Int("IM Out") == viewIndex);
    }

    public ImageBrowserSource GetImageBrowserSource(int viewIndex, string operationId = null)
    {
        try
        {
            var source = ImageInputOperation(viewIndex, operationId);
            if (source == null) return null;
            var parameters = ParamBag.FromYaml(source.ParametersRaw as YamlNode, null, _engine.Context);
            var file = parameters.Str("File");
            var directory = parameters.Str("Directory");
            var mode = parameters.Str("Type", string.IsNullOrWhiteSpace(file) && !string.IsNullOrWhiteSpace(directory) ? "Directory" : "File");
            var isDirectory = mode.Equals("Directory", StringComparison.OrdinalIgnoreCase);
            if (!isDirectory && !mode.Equals("File", StringComparison.OrdinalIgnoreCase)) return null;
            var fullPath = ResolveProjectFile(isDirectory ? directory : file);
            if (string.IsNullOrWhiteSpace(fullPath)) return null;
            fullPath = Path.GetFullPath(fullPath);
            string[] files;
            if (isDirectory)
            {
                if (!Directory.Exists(fullPath)) return null;
                var stamp = Directory.GetLastWriteTimeUtc(fullPath).Ticks;
                if (!_imageFileLists.TryGetValue(source.Uuid, out var cached) || cached.Path != fullPath || cached.Stamp != stamp)
                {
                    files = Directory.EnumerateFiles(fullPath).Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
                        .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ThenBy(path => path, StringComparer.Ordinal).ToArray();
                    _imageFileLists[source.Uuid] = (fullPath, stamp, files);
                }
                else files = cached.Files;
            }
            else files = File.Exists(fullPath) ? [fullPath] : [];
            var current = _engine.Context.OpOutputs.GetValueOrDefault(source.Uuid)?.Get("Fullpath") as string;
            if (!files.Contains(current, StringComparer.OrdinalIgnoreCase)) current = files.FirstOrDefault();
            return new ImageBrowserSource($"{ProjectPath}|{SelectedProgram.Name}|{source.Uuid}|{mode}|{fullPath}", files, current, source.Uuid);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidCastException or InvalidOperationException)
        { return null; }
    }

    public async Task SelectImageAndRunAsync(string operationId, string file)
    {
        if (!CanEdit || !ApplyParameters()) return;
        if (UseNativeKernel) { Status = "请切换到自有内核后使用选图运行"; return; }
        var source = GetImageBrowserSource(0, operationId);
        if (source?.OperationId != operationId || !source.Files.Contains(file, StringComparer.OrdinalIgnoreCase) || !File.Exists(file))
        { Status = "所选图片不存在或已不在当前图像源中"; return; }
        foreach (var operation in SelectedProgram.Operations.Where(operation => operation.Driver == "LoadImage"))
        {
            var current = _engine.Context.OpOutputs.GetValueOrDefault(operation.Uuid)?.Get("Fullpath") as string;
            if (!string.IsNullOrEmpty(current)) _engine.Context.SelectedInputFiles[operation.Uuid] = current;
        }
        _engine.Context.SelectedInputFiles[operationId] = file;
        _engine.InvalidateExecution();
        _engine.InvalidateResults([operationId]);
        try { await RunInteractiveAsync(); }
        finally { _engine.Context.SelectedInputFiles.Clear(); }
    }
}
