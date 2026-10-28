using System.IO;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Select the active file parameter; inactive values are retained by original projects.</summary>
internal static class InputFileSelector
{
    internal readonly record struct Selection(string Path,string CursorKey)
    {
        public void Commit(OpContext context)
        {if(CursorKey!=null)context.InputFiles[CursorKey]=Path;}
    }

    public static Selection Select(OpContext context,ParamBag parameters,string driver,params string[] extensions)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        var file=parameters.Str("File");var directory=parameters.Str("Directory");
        var mode=parameters.Str("Type",string.IsNullOrWhiteSpace(file)&&!string.IsNullOrWhiteSpace(directory)?"Directory":"File");
        if(mode.Equals("File",StringComparison.OrdinalIgnoreCase))
        {
            var path=context.ResolvePath(file);
            if(path==null||!File.Exists(path))throw new FileNotFoundException("文件不存在："+(path??"未指定文件"),path);
            return new(path,null);
        }
        if(!mode.Equals("Directory",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("无效的加载类型："+mode);
        var folder=context.ResolvePath(directory);
        if(folder==null||!Directory.Exists(folder))throw new DirectoryNotFoundException("目录不存在："+(folder??"未指定目录"));
        var supported=new HashSet<string>(extensions,StringComparer.OrdinalIgnoreCase);
        var files=Directory.EnumerateFiles(folder).Where(path=>supported.Contains(Path.GetExtension(path)))
            .OrderBy(path=>Path.GetFileName(path),StringComparer.OrdinalIgnoreCase).ThenBy(path=>path,StringComparer.Ordinal).ToArray();
        if(files.Length==0)throw new FileNotFoundException("目录内没有可读取的文件（"+string.Join("、",extensions)+"）："+folder);
        var key=(string.IsNullOrEmpty(context.OperationUuid)?driver+"/IM"+parameters.Int("IM Out"):context.OperationUuid)+"|"+Path.GetFullPath(folder);
        int index=-1;
        if (context.SelectedInputFiles.TryGetValue(context.OperationUuid ?? "", out var selected))
        {
            var selectedIndex = Array.FindIndex(files, path => path.Equals(selected, StringComparison.OrdinalIgnoreCase));
            if (selectedIndex < 0) throw new FileNotFoundException("所选图片已不在当前目录中：" + selected, selected);
            return new(files[selectedIndex], key);
        }
        if(context.InputFiles.TryGetValue(key,out var previous))index=Array.FindIndex(files,path=>path.Equals(previous,StringComparison.OrdinalIgnoreCase));
        return new(files[(index+1)%files.Length],key);
    }
}
