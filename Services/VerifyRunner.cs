using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AiVisionStudio.Models;
using AiVisionStudio.Services;

namespace AiVisionStudio.Services;

/// <summary>批量验证：打开目录下所有 .aivs，运行并输出报告。</summary>
public static class VerifyRunner
{
    public static void Run(string root, string outFile)
    {
        var lines = new List<string> { $"root\t{root}", $"time\t{DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
        var files = File.Exists(root) ? new List<string> { Path.GetFullPath(root) } : Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.aivs", SearchOption.AllDirectories).OrderBy(x => x).ToList()
            : new List<string>();

        int pass = 0, fail = 0, skip = 0;

        foreach (var f in files)
        {
            var rel = Path.GetRelativePath(root, f);
            MainViewModel vm;
            try
            {
                vm = new MainViewModel(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outFile))!, "verification-settings"));
                vm.OpenProject(f);
                vm.WriteRoot = Path.GetFullPath(Directory.Exists(root) ? root : Path.GetDirectoryName(root)!);
            }
            catch (Exception ex)
            {
                lines.Add($"ERROR\t{rel}\t{ex.Message}");
                fail++;
                continue;
            }

            if (vm.Operations.Count == 0) { skip++; vm.Dispose(); continue; }

            try
            {
                vm.RunAll();
            }
            catch (Exception ex)
            {
                lines.Add($"ERROR\t{rel}\t运行异常 {ex.Message}");
                fail++;
                continue;
            }

            var all = vm.Programs.SelectMany(p => p.Operations).ToList();
            var ok = all.Count(o => o.State == OpState.Ok);
            var ng = all.Count(o => o.State == OpState.Fail);
            var dis = all.Count(o => o.State == OpState.Disabled);
            var pending = all.Count(o => o.State == OpState.None);
            var passed = ng == 0 && vm.ExecutionFailureCount == 0 && !vm.IsExecutionPaused && vm.IsExecutionCompleted;
            if (passed) pass++; else fail++;

            lines.Add($"{(passed ? "PASS" : "FAIL")}\t{rel}\tops={all.Count} ok={ok} ng={ng} disabled={dis} pending={pending} paused={vm.IsExecutionPaused}");
            foreach (var program in vm.Programs)
            {
                var programOk = program.Operations.Count(o => o.State == OpState.Ok);
                var programFail = program.Operations.Count(o => o.State == OpState.Fail);
                var programDisabled = program.Operations.Count(o => o.State == OpState.Disabled);
                var programPending = program.Operations.Count(o => o.State == OpState.None);
                lines.Add($"PROGRAM\t{program.Name}\tops={program.Operations.Count} visited={programOk + programFail + programDisabled} ok={programOk} ng={programFail} disabled={programDisabled} pending={programPending}");
            }

            foreach (var o in all.Where(x => x.State == OpState.Fail))
            {
                lines.Add($"    NG\t{o.Driver}\t{o.Name}\t{Trim(o.ErrorMessage ?? "", 150)}");
                var tb = Algorithm.ParamBag.FromYaml(o.ParametersRaw as YamlDotNet.RepresentationModel.YamlNode);
                lines.Add($"    DBG\tbinds=[{string.Join("|", tb.Bindings.Keys)}] globals=[{string.Join("|", tb.Globals.Keys)}]");
            }
            if (vm.GlobalVars.Count > 0)
                lines.Add("    GLOB	" + string.Join(" | ", vm.GlobalVars.Select(g => $"{g.Name}={g.Value}")) + " || draw=" + vm.DrawText.Replace(Environment.NewLine, " / "));
            {
                var logs = vm.Logs.Skip(Math.Max(0, vm.Logs.Count - 40)).Take(40).ToList();
                foreach (var l in logs)
                    lines.Add($"    LOG	{l.Message}");
            }
            vm.Dispose();
            File.WriteAllLines(outFile, lines, System.Text.Encoding.UTF8);
            Console.WriteLine($"[{pass + fail + skip}/{files.Count}] {(passed ? "PASS" : "FAIL")} {rel}");
        }

        lines.Add($"SUMMARY\tfiles={files.Count} pass={pass} fail={fail} skip={skip}");
        File.WriteAllLines(outFile, lines, System.Text.Encoding.UTF8);
        Console.WriteLine($"verify done: pass={pass} fail={fail} skip={skip} -> {outFile}");
    }

    private static string Trim(string s, int n)
        => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n) + "…";
}
