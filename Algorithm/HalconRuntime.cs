using System.IO;
using System.Runtime.InteropServices;
using HalconDotNet;

namespace AiVisionStudio.Algorithm;

internal static class HalconRuntime
{
    private static readonly Lazy<string> RuntimeVersion = new(Initialize);
    public static string Version => RuntimeVersion.Value;

    private static string Initialize()
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("HALCON 26.05 需要以 x64 方式运行");
        NativeLibrary.SetDllImportResolver(typeof(HOperatorSet).Assembly, (name, assembly, searchPath) =>
        {
            if (name is not ("halcon" or "halcon.dll")) return IntPtr.Zero;
            var root = Environment.GetEnvironmentVariable("HALCONROOT");
            if (string.IsNullOrWhiteSpace(root)) return IntPtr.Zero;
            var path = Path.Combine(root, "bin", "x64-win64", "halcon.dll");
            return File.Exists(path) ? NativeLibrary.Load(path, assembly, DllImportSearchPath.UseDllDirectoryForDependencies) : IntPtr.Zero;
        });
        try
        {
            HOperatorSet.GetSystem("version", out var version);
            using (version)
            {
                if (!version.S.StartsWith("26.05", StringComparison.Ordinal))
                    throw new InvalidOperationException($"HALCON .NET 接口为26.05，当前运行库为{version.S}，请使用匹配版本");
                return version.S;
            }
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or HOperatorException or TypeInitializationException)
        { throw new InvalidOperationException("HALCON 26.05 初始化失败，请检查64位运行库、HALCONROOT及运行许可。" + error.Message, error); }
    }
}
