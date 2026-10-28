using System.Collections;
using System.Reflection;

namespace AiVisionStudio.Algorithm;

public sealed record OutputPathPart(string Name, int? Index = null);

/// <summary>Resolve the ordered member/index path stored in parameters.bind._target._variable.</summary>
public static class OutputBindingResolver
{
    public static object Resolve(VarPool source, IReadOnlyList<OutputPathPart> path)
    {
        if (path == null || path.Count == 0) throw new InvalidOperationException("输出绑定路径为空");
        if (!source.TryGet(path[0].Name, out var current))
            throw new InvalidOperationException($"绑定输出不存在：{path[0].Name}");
        for (int step = 0; step < path.Count; step++)
        {
            var part = path[step];
            if (step > 0) current = Member(current, part.Name);
            if (part.Index is { } index)
            {
                if (current is not IList list || index < 0 || index >= list.Count)
                    throw new InvalidOperationException($"绑定数组索引越界：{part.Name}[{index}]");
                current = list[index];
            }
        }
        return current;
    }

    private static object Member(object target, string name)
    {
        if (target is SegmentObj segment)
        {
            if (name.Equals("Start", StringComparison.OrdinalIgnoreCase)) return new PointObj { X = segment.X1, Y = segment.Y1 };
            if (name.Equals("End", StringComparison.OrdinalIgnoreCase)) return new PointObj { X = segment.X2, Y = segment.Y2 };
        }
        if (target is IReadOnlyDictionary<string, object> readOnly)
        {
            if (readOnly.TryGetValue(name, out var value)) return value;
            foreach (var pair in readOnly)
                if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            throw new InvalidOperationException($"绑定输出成员不存在：{name}");
        }
        if (target is IDictionary dictionary)
        {
            if (dictionary.Contains(name)) return dictionary[name];
            foreach (DictionaryEntry entry in dictionary)
                if (string.Equals(entry.Key?.ToString(), name, StringComparison.OrdinalIgnoreCase)) return entry.Value;
            throw new InvalidOperationException($"绑定输出成员不存在：{name}");
        }
        if (target == null) throw new InvalidOperationException($"无法读取nil输出的成员：{name}");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        var type = target.GetType();
        var property = type.GetProperty(name, flags);
        if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(target);
        var field = type.GetField(name, flags);
        return field != null ? field.GetValue(target) : throw new InvalidOperationException($"绑定输出成员不存在：{name}");
    }
}
