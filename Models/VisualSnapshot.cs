using System;
using System.Collections.Generic;
using OpenCvSharp;
using AiVisionStudio.Algorithm;

namespace AiVisionStudio.Models;

// 可显示结果的类型
public enum VisualResultKind
{
    Image,
    Cloud,
    Profile
}

// 用来唯一确定某个控件的某项显示结果
public sealed record VisualSourceKey(string ProgramName, string OperationId, string OutputKey, VisualResultKind Kind)
{
    private static string OutputIdentity(string key) => key?.StartsWith("output:", StringComparison.Ordinal) == true ? key : ChannelSplitChannels.Identity(key);
    public bool Equals(VisualSourceKey other)
    {
        return other is not null && ProgramName == other.ProgramName && OperationId == other.OperationId
            && OutputIdentity(OutputKey) == OutputIdentity(other.OutputKey);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            ProgramName,
            OperationId, OutputIdentity(OutputKey));
    }
}

// 保存控件执行结束时的独立快照
public sealed class VisualSnapshot : IDisposable
{
    // 重命名流程时，由缓存管理代码迁移这个键
    public VisualSourceKey Key { get; internal set; }

    public long Version { get; internal set; }

    public DateTimeOffset CapturedAt { get; init; }= DateTimeOffset.Now;

    // 记录生成这张图时，该控件使用的参数
    public string ParameterSignature { get; init; } = "";

    public string OperationName { get; init; } = "";

    public string OutputName { get; init; } = "";
    public string OutputParameter { get; init; } = "";

    //算法实际要使用的IM编号
    public int AlgorithmSlot { get; init; }
    // 保存当时的全局显示文字
    public string OverlayText { get; init; } = "";

    public Mat Image { get; init; }

    public PointCloud Cloud { get; init; }

    public ProfileData Profile { get; init; }

    public IReadOnlyList<Text3D> Texts { get; init; }
        = Array.Empty<Text3D>();

    public IReadOnlyList<Box3> Boxes { get; init; }
        = Array.Empty<Box3>();

    public IReadOnlyList<NativeOverlay3D> Overlays { get; init; }
        = Array.Empty<NativeOverlay3D>();

    public void Dispose()
    {
        Image?.Dispose();
    }
    // 菜单只读取这些基本信息，不直接持有图像对象。
    public sealed record VisualSourceInfo(VisualSourceKey Key,string OperationName, int AlgorithmSlot)
    {
        public string OutputName { get; init; } = "";
    public string OutputParameter { get; init; } = "";

        public long Version { get; init; }

        public DateTimeOffset CapturedAt { get; init; }

        public string ParameterSignature { get; init; } = "";
    }
}