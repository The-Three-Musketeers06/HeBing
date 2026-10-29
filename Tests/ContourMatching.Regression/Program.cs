using System.IO;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--post-training")) { PostTrainingMatchCases.Run(); return 0; }
            if (args.Contains("--contour-display")) { ContourDisplayCases.Run(); return 0; }
            if (args.Contains("--mask-editor")) { TemplateMaskEditingCases.Run(); EditorCases.Run(); TemplateCleanupCases.Run(); return 0; }
            if (args.Contains("--region-list")) { TemplateMaskEditingCases.RunRegionList(); return 0; }
            if (args.Contains("--shape-template")) { ShapeEngineCases.Run(); NativeTemplateCases.Run(); TemplateCleanupCases.Run(); MatchingSyncCases.Run(); return 0; }
            if (args.Contains("--matching-sync")) { MatchingSyncCases.Run(); return 0; }
            if (args.Contains("--matching-cleanup")) { TemplateCleanupCases.Run(); return 0; }
            if (args.Contains("--native-template")) { NativeTemplateCases.Run(); return 0; }
            if (args.Contains("--templates")) { EngineCases.Run(); WorkflowCases.Run(); return 0; }
            if (args.Contains("--codes")) { MatrixCodeCases.Run(); return 0; }
            if (args.Contains("--run-drag")) { RunListDragCases.Run(); return 0; }
            if (args.Contains("--pan")) { PanCases.Run(); return 0; }
            if (args.Contains("--color-convert")) { ColorConversionCases.Run(); return 0; }
            if (args.Contains("--image-inputs")) { ImageInputCases.Run(); return 0; }
            if (args.Contains("--visual-outputs")) { VisualOutputCases.Run(); return 0; }
            if (args.Contains("--channels")) { ChannelSplitCases.Run(); return 0; }
            if (args.Contains("--halcon")) { HalconGeometryCases.Run(); return 0; }
            var total = EngineCases.Run();
            total += ShapeEngineCases.Run();
            total += ContourDisplayCases.Run();
            total += PostTrainingMatchCases.Run();
            total += TemplateMaskEditingCases.Run();
            total += TemplateMaskEditingCases.RunRegionList();
            total += WorkflowCases.Run();
            total += RegionCases.Run();
            total += EditorCases.Run();
            total += MatrixCodeCases.Run();
            total += IntegrationCases.Run();
            total += PanCases.Run();
            total += HalconGeometryCases.Run();
            total += ChannelSplitCases.Run();
            total += VisualOutputCases.Run();
            total += ImageInputCases.Run();
            total += ColorConversionCases.Run();
            total += RunListDragCases.Run();
            total += NativeTemplateCases.Run();
            total += TemplateCleanupCases.Run();
            total += MatchingSyncCases.Run();
            Console.WriteLine($"PASS {total} regression cases");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
