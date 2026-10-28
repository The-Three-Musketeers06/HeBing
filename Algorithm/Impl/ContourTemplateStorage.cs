using System.IO;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Trained templates travel with their operation in the project document.</summary>
public static class ContourTemplateStorage
{
    public const string ModelKey = "_Contour Template";
    public const string DirtyKey = "_Contour Template Dirty";
    public static bool IsInternal(string key) => key is ModelKey or DirtyKey;

    public static ContourTemplateModel Read(ParamBag parameters, OpContext context)
    {
        if (parameters.Bool(DirtyKey)) throw new InvalidOperationException("模板区域或示教参数已变化，请先点击“训练模板”");
        if (parameters.Has(ModelKey))
        {
            var payload = parameters.Str(ModelKey);
            if (string.IsNullOrWhiteSpace(payload)) throw new InvalidOperationException("尚未训练模板，请先加载当前图像、画区域并训练模板");
            return ContourTemplateEngine.DeserializeModel(payload);
        }
        // Older projects keep working without exposing file management in the editor.
        var path = context.ResolvePath(parameters.Str("Model File"));
        if (!string.IsNullOrWhiteSpace(path) && !File.Exists(path))
        {
            var alternative = Path.Combine(context.ProjectDir ?? ".", Path.GetFileName(path));
            if (File.Exists(alternative)) path = alternative;
        }
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("尚未训练模板，请先加载当前图像、画区域并训练模板");
        return ContourTemplateEngine.Load(path);
    }
}
