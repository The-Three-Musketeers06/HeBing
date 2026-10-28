using AiVisionStudio.Algorithm;
using AiVisionStudio.Models;
using OpenCvSharp;

namespace AiVisionStudio.Services;

public partial class ExecutionEngine
{
    internal Func<ImageInputReference, (Mat Image, int Slot)?> ImageInputResolver { get; set; }
    internal Func<ImageInputReference, IReadOnlyList<NativeOverlay3D>> ImageInputOverlayResolver { get; set; }
    private IReadOnlySet<int> _allowedImageReads;

    private IDisposable PrepareImageInputs(Operation operation, ParamBag parameters)
    {
        var inputs = new List<(int Slot, Mat Image, string Owner)>();
        try
        {
            foreach (var (path, reference) in parameters.ImageInputs)
            {
                if (!MainViewModel.IsImageInputActive(operation, path)) continue;
                var program = _programs.Values.FirstOrDefault(operations => operations.Any(item => item.Uuid == operation.Uuid));
                if (string.IsNullOrEmpty(reference.OperationId) || program == null
                    || !program.TakeWhile(item => item.Uuid != operation.Uuid).Any(item => item.Uuid == reference.OperationId))
                    throw new InvalidOperationException("图像输入来源已删除、未选择或不在当前控件之前，请重新选择：" + path);
                var resolved = ImageInputResolver?.Invoke(reference);
                if (resolved is not { } frame) throw new InvalidOperationException("所选控件尚未产生该图像输出，请先运行前置控件或重新选择：" + path);
                var image = frame.Image;
                if (_allowedImageReads != null && !_allowedImageReads.Contains(frame.Slot))
                { image.Dispose(); throw new InvalidOperationException($"并行流程没有所选输入图像IM{frame.Slot}的读取权限"); }
                int slot = frame.Slot;
                if (slot < 0 || inputs.Any(input => input.Slot == slot))
                {
                    slot = 32;
                    while (inputs.Any(input => input.Slot == slot) || Context.Images.All.ContainsKey(slot)
                           || Context.Clouds.Get(slot) != null || Context.Profiles.ContainsKey(slot)) slot++;
                }
                inputs.Add((slot, image, reference.OperationId));
                parameters.SetImageInputIndex(path, slot);
            }
            return Context.Images.UseInputs(inputs);
        }
        catch { foreach (var input in inputs) input.Image.Dispose(); InvalidateResults([operation.Uuid]); throw; }
    }
}
