using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using Version = System.Version;

namespace Playnite.Core.Manifests;

// Version has read-only component properties, not a writable YAML object representation.
internal sealed class ManifestVersionConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(Version);

    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer) =>
        Version.Parse(parser.Consume<Scalar>().Value);

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        emitter.Emit(value is null
            ? new Scalar(null, null, "null", ScalarStyle.Plain, true, false)
            : new Scalar(null, null, value.ToString()!, ScalarStyle.DoubleQuoted, false, true));
    }
}
