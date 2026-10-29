using AiVisionStudio.Algorithm;
using AiVisionStudio.Algorithm.Impl;
using AiVisionStudio.Models;

namespace AiVisionStudio.Services;

public partial class MainViewModel
{
    public IReadOnlyList<NativeOverlay3D> GetSelectedPreviewOverlays(int slot, bool inputOnly = false)
    {
        if (!UseNativeKernel)
            lock (_visualSnapshotGate)
            {
                if (!inputOnly)
                {
                    var own = _visualSnapshots.Values.FirstOrDefault(snapshot => snapshot.Key.OperationId == SelectedOperation?.Uuid
                        && snapshot.Key.Kind == VisualResultKind.Image && snapshot.AlgorithmSlot == slot);
                    if (own != null) return own.Overlays.ToArray();
                }
                var reference = ReadImageInput(SelectedOperation, "IM In");
                if (reference != null && slot == RegionInputIndex) return FindImageInput(reference)?.Overlays.ToArray() ?? [];
            }
        var overlays = NativeOverlays.Where(item => item.ViewIndex < 0 || item.ViewIndex == slot);
        if (!UseNativeKernel && ContourTemplateEngine.IsTemplateTool(SelectedOperation?.Driver))
            overlays = overlays.Where(item => IsTemplateOverlay(item, SelectedOperation));
        return overlays.ToArray();
    }

    internal static bool IsTemplateOverlay(NativeOverlay3D overlay, Operation operation)
        => overlay.Values.TryGetValue("_owned_operation", out var owner) && (Equals(owner, operation.Uuid) || Equals(owner, operation.Driver));

    internal static bool IsTemplateOutputAlias(string driver, string name) => ContourTemplateEngine.IsTemplateTool(driver) && name == "Elapsed Time";

    private bool ShowSelectedOutput(string name)
    {
        if (VisualResultNames.IsOutputRoute(SelectedOperation.Driver, name)) return false;
        if (!ContourTemplateEngine.IsTemplateTool(SelectedOperation.Driver)) return true;
        return !IsTemplateOutputAlias(SelectedOperation.Driver, name);
    }
}
