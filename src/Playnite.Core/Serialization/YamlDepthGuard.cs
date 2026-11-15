using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Playnite.Core.Serialization;

/// <summary>
/// Rejects YAML nesting deeper than <see cref="YamlSerialization.MaxDepth"/> before any
/// recursive reader runs.
/// </summary>
/// <remarks>
/// <see cref="YamlDotNet.RepresentationModel.YamlStream"/> builds its node tree recursively and
/// overflows the stack on a deeply nested document, which a size limit alone cannot prevent.
/// The event parser is iterative, so counting collection starts and ends here bounds every
/// later recursion.
/// </remarks>
internal static class YamlDepthGuard
{
    internal static void Validate(string yaml)
    {
        var parser = new Parser(new StringReader(yaml));
        var depth = 0;

        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case MappingStart:
                case SequenceStart:
                    depth++;
                    if (depth > YamlSerialization.MaxDepth)
                    {
                        throw new SerializationException(
                            FormatNames.Yaml,
                            $"nesting exceeds the maximum depth of {YamlSerialization.MaxDepth} at line {parser.Current.Start.Line}, column {parser.Current.Start.Column}.");
                    }

                    break;

                case MappingEnd:
                case SequenceEnd:
                    depth--;
                    break;
            }
        }
    }
}
