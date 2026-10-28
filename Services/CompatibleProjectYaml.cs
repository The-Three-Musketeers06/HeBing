using System.IO;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace AiVisionStudio.Services;

/// <summary>
/// Accepts repeated, non-conflicting mappings found in some shipped projects.
/// All ordinary documents still use YamlDotNet's standard representation loader.
/// </summary>
internal static class CompatibleProjectYaml
{
    public static void Save(YamlNode root, TextWriter writer)
        => new YamlStream(new YamlDocument(root)).Save(new ExplicitCollectionTags(new Emitter(writer)), false);

    // RepresentationModel marks collection events implicit even when a node has
    // an explicit tag. Correct only those events; all other standard events,
    // including alias references and scalar quoting, pass through unchanged.
    private sealed class ExplicitCollectionTags(IEmitter output) : IEmitter
    {
        public void Emit(ParsingEvent value)
        {
            output.Emit(value switch
            {
                MappingStart map when !map.Tag.IsEmpty => new MappingStart(map.Anchor, map.Tag, false, map.Style, map.Start, map.End),
                SequenceStart sequence when !sequence.Tag.IsEmpty => new SequenceStart(sequence.Anchor, sequence.Tag, false, sequence.Style, sequence.Start, sequence.End),
                _ => value
            });
        }
    }

    public static YamlStream Load(string text)
    {
        var standard = new YamlStream();
        try
        {
            standard.Load(new StringReader(text));
            return standard;
        }
        catch (YamlException error) when (error.Message.Contains("Duplicate key", StringComparison.Ordinal))
        {
            return ReadCompatible(text);
        }
    }

    private static YamlStream ReadCompatible(string text)
    {
        var parser = new Parser(new StringReader(text));
        parser.MoveNext();
        Take<StreamStart>(parser);
        var stream = new YamlStream();
        while (parser.Current is not StreamEnd)
        {
            Take<DocumentStart>(parser);
            stream.Documents.Add(new YamlDocument(ReadNode(parser, "$")));
            Take<DocumentEnd>(parser);
        }
        Take<StreamEnd>(parser);
        return stream;
    }

    private static YamlNode ReadNode(IParser parser, string path)
    {
        switch (parser.Current)
        {
            case Scalar scalar:
                parser.MoveNext();
                return new YamlScalarNode(scalar.Value) { Tag = scalar.Tag, Anchor = scalar.Anchor, Style = scalar.Style };
            case SequenceStart sequence:
            {
                parser.MoveNext();
                var node = new YamlSequenceNode { Tag = sequence.Tag, Anchor = sequence.Anchor, Style = sequence.Style };
                while (parser.Current is not SequenceEnd)
                    node.Add(ReadNode(parser, path + "[" + node.Children.Count + "]"));
                Take<SequenceEnd>(parser);
                return node;
            }
            case MappingStart mapping:
            {
                parser.MoveNext();
                var node = new YamlMappingNode { Tag = mapping.Tag, Anchor = mapping.Anchor, Style = mapping.Style };
                while (parser.Current is not MappingEnd)
                {
                    var start = parser.Current.Start;
                    var end = parser.Current.End;
                    var key = ReadNode(parser, path + ".<key>");
                    var childPath = path + "." + key;
                    var value = ReadNode(parser, childPath);
                    if (node.Children.TryGetValue(key, out var existing))
                    {
                        var existingKey = node.Children.Keys.First(candidate => candidate.Equals(key));
                        if (!SameValue(existingKey, key)) Conflict(start, end, childPath, "key type or representation differs");
                        Merge(existing, value, start, end, childPath);
                    }
                    else node.Add(key, value);
                }
                Take<MappingEnd>(parser);
                return node;
            }
            case AnchorAlias alias:
                // The ordinary loader continues to support aliases. Merging a
                // duplicate mapping can change the object referenced by an alias;
                // reject this combination rather than silently changing its meaning.
                throw new YamlException(alias.Start, alias.End,
                    "Cannot safely merge duplicate YAML keys in a document containing aliases. Remove the duplicate keys while preserving the anchors first.");
            default:
                throw new YamlException(parser.Current?.Start ?? Mark.Empty, parser.Current?.End ?? Mark.Empty,
                    "Unexpected YAML event while reading project data: " + parser.Current?.GetType().Name);
        }
    }

    private static void Merge(YamlNode existing, YamlNode incoming, Mark start, Mark end, string path)
    {
        if (!SameMetadata(existing, incoming)) Conflict(start, end, path, "tag or anchor differs");
        if (existing is YamlMappingNode left && incoming is YamlMappingNode right)
        {
            foreach (var pair in right.Children)
            {
                if (left.Children.TryGetValue(pair.Key, out var child))
                {
                    var leftKey = left.Children.Keys.First(key => key.Equals(pair.Key));
                    if (!SameValue(leftKey, pair.Key)) Conflict(start, end, path + "." + pair.Key, "key type or representation differs");
                    Merge(child, pair.Value, start, end, path + "." + pair.Key);
                }
                else left.Add(pair.Key, pair.Value);
            }
            return;
        }
        if (!SameValue(existing, incoming)) Conflict(start, end, path, "values differ");
    }

    private static bool SameMetadata(YamlNode left, YamlNode right)
        => left.GetType() == right.GetType() && left.Tag.Equals(right.Tag) && left.Anchor.Equals(right.Anchor);

    private static bool SameValue(YamlNode left, YamlNode right)
    {
        if (!SameMetadata(left, right)) return false;
        return (left, right) switch
        {
            // Requiring the same scalar style also prevents treating a plain
            // number/bool/null as identical to a quoted string with the same text.
            (YamlScalarNode a, YamlScalarNode b) => a.Value == b.Value && a.Style == b.Style,
            (YamlSequenceNode a, YamlSequenceNode b) => a.Children.Count == b.Children.Count
                && a.Children.Zip(b.Children).All(pair => SameValue(pair.First, pair.Second)),
            (YamlMappingNode a, YamlMappingNode b) => a.Children.Count == b.Children.Count
                && a.Children.All(pair => b.Children.TryGetValue(pair.Key, out var other) && SameValue(pair.Value, other)
                    && SameValue(pair.Key, b.Children.Keys.First(key => key.Equals(pair.Key)))),
            _ => false
        };
    }

    private static void Conflict(Mark start, Mark end, string path, string reason)
        => throw new YamlException(start, end, $"Conflicting duplicate YAML key at {path}: {reason}. The project was not loaded.");

    private static void Take<T>(IParser parser) where T : ParsingEvent
    {
        if (parser.Current is not T)
            throw new YamlException(parser.Current?.Start ?? Mark.Empty, parser.Current?.End ?? Mark.Empty,
                "Expected YAML " + typeof(T).Name + ".");
        parser.MoveNext();
    }
}
