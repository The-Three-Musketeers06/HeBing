using AiVisionStudio.Algorithm;
using System;
using System.Collections.Generic;
using System.Text;
using YamlDotNet.RepresentationModel;
using System.IO;

namespace AiVisionStudio.Services
{
    public partial class MainViewModel
    {
        public string GetImageName(int index, string operationId = null)
        {
            if (operationId != null)
            {
                var source = ImageInputOperation(index, operationId);
                if (source != null && _engine.Context.OpOutputs.TryGetValue(source.Uuid, out var output))
                {
                    var filename = Convert.ToString(output.Get("Filename") ?? output.Get("Fullpath"));
                    if (!string.IsNullOrWhiteSpace(filename)) return Path.GetFileName(filename);
                }
                return $"IM{index}";
            }
            var operations = Programs
                .SelectMany(program => program.Operations)
                .ToDictionary( operation => operation.Uuid,StringComparer.OrdinalIgnoreCase);

            var names = new Dictionary<int, string>();

            foreach (var (uuid, output) in _engine.Context.OpOutputs)
            {
                if (!operations.TryGetValue(uuid, out var operation))
                    continue;

                var parameters = ParamBag.FromYaml( operation.ParametersRaw as YamlNode,null,_engine.Context);

                if (operation.Driver == "LoadImage")
                {
                    // 优先读取实际加载的文件名，兼容目录轮流加载。
                    var filename = Convert.ToString(output.Get("Filename"));

                    if (string.IsNullOrWhiteSpace(filename)) filename = Convert.ToString(output.Get("Fullpath"));

                    if (string.IsNullOrWhiteSpace(filename) && operation.State != Models.OpState.Fail && parameters.Str("Type", "File").Equals("File",StringComparison.OrdinalIgnoreCase))
                    {
                        filename = parameters.Str("File");
                    }

                    if (!string.IsNullOrWhiteSpace(filename))
                    {
                        names[parameters.Int("IM Out", 0)] =Path.GetFileName(filename);
                    }
                }
                else if (parameters.Raw("IM Out") != null)
                {
                    var target = parameters.Int("IM Out");

                    if (names.TryGetValue(parameters.Int("IM In", 0),out var sourceName))
                    {
                        names[target] = sourceName;
                    }
                    else
                    {
                        names.Remove(target);
                    }
                }
            }

            return names.GetValueOrDefault(index, $"IM{index}");
        }

    }
}
