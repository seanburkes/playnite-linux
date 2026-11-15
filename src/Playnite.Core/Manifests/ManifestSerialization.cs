using System.Collections;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.Core.Serialization;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace Playnite.Core.Manifests;

/// <summary>Pure document boundary for v10 add-ons and emulation metadata.</summary>
public static class ManifestSerialization
{
    public static T FromYaml<T>(string document) where T : class
    {
        // The shared reader enforces size, duplicate, depth, tag and null-value limits first.
        var result = YamlSerialization.FromYaml<T>(document);
        var stream = new YamlStream();
        stream.Load(new StringReader(document.TrimStart('\uFEFF')));
        if (stream.Documents.Count != 1 || result is null)
        {
            throw Error("YAML", "$", "a non-null document is required");
        }

        CheckYaml(stream.Documents[0].RootNode, typeof(T), "$");
        Validate(result, "$");
        return result;
    }

    public static T FromJson<T>(string document) where T : class
    {
        var result = JsonSerialization.FromJson<T>(document);
        if (result is null)
        {
            throw Error("JSON", "$", "a non-null document is required");
        }

        using var text = new StringReader(document.TrimStart('\uFEFF'));
        using var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None };
        CheckJson(JToken.Load(reader), typeof(T), "$");
        Validate(result, "$");
        return result;
    }

    public static string ToYaml<T>(T value) where T : class
    {
        Validate(value, "$");
        // Literal YAML blocks normalize CR to LF. Quoted scalars preserve the wire text.
        return new SerializerBuilder().WithDefaultScalarStyle(YamlDotNet.Core.ScalarStyle.DoubleQuoted)
            .WithTypeConverter(new ManifestVersionConverter())
            .DisableAliases().WithMaximumRecursion(YamlSerialization.MaxDepth).Build().Serialize(value);
    }

    public static string ToJson<T>(T value) where T : class
    {
        Validate(value, "$");
        return JsonSerialization.ToJson(value, formatted: true);
    }

    private static void CheckYaml(YamlNode node, Type type, string path)
    {
        if (node is YamlScalarNode scalar)
        {
            if (scalar.Value is null || (scalar.Style == YamlDotNet.Core.ScalarStyle.Plain
                && scalar.Value is "null" or "Null" or "NULL" or "~" or ""))
            {
                if (type.IsValueType) throw Error("YAML", path, "null value member");
                return;
            }

            if (!IsScalar(type)) throw Error("YAML", path, "expected a mapping or sequence");
            if (type == typeof(bool) && !bool.TryParse(scalar.Value, out _))
                throw Error("YAML", path, "expected true or false");
            if (type == typeof(ulong) && !ulong.TryParse(scalar.Value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw Error("YAML", path, "expected an unsigned 64-bit integer");
            if (type.IsEnum && (!Enum.TryParse(type, scalar.Value, false, out var parsed) || !Enum.IsDefined(type, parsed)))
                throw Error("YAML", path, "undefined enum value");
            return;
        }

        if (node is YamlSequenceNode sequence && ListItem(type) is { } item)
        {
            for (var i = 0; i < sequence.Children.Count; i++) CheckYaml(sequence.Children[i], item, $"{path}[{i}]");
            return;
        }

        if (node is not YamlMappingNode map || IsScalar(type) || ListItem(type) is not null)
            throw Error("YAML", path, "wrong document shape");
        foreach (var pair in map.Children)
        {
            if (pair.Key is not YamlScalarNode key || key.Value is null)
                throw Error("YAML", path, "expected a scalar field name");
            var member = MemberType(type, key.Value);
            if (member is not null) CheckYaml(pair.Value, member, $"{path}.{key.Value}");
        }

        RequireDiscriminator(type, name => map.Children.Keys.OfType<YamlScalarNode>().Any(k => k.Value == name), "YAML", path);
    }

    private static void CheckJson(JToken node, Type type, string path)
    {
        if (node.Type == JTokenType.Null) return; // Semantic required members are checked below.
        if (node is JArray array && ListItem(type) is { } item)
        {
            for (var i = 0; i < array.Count; i++) CheckJson(array[i], item, $"{path}[{i}]");
            return;
        }

        if (node is JObject obj && !IsScalar(type) && ListItem(type) is null)
        {
            foreach (var pair in obj.Properties())
            {
                var member = MemberType(type, pair.Name, ignoreCase: true);
                if (member is not null) CheckJson(pair.Value, member, $"{path}.{pair.Name}");
            }

            RequireDiscriminator(type, name => obj.GetValue(name, StringComparison.OrdinalIgnoreCase) is not null, "JSON", path);
            return;
        }

        var valid = type == typeof(string) || type == typeof(Version) || type == typeof(DateTime)
            ? node.Type == JTokenType.String
            : type == typeof(bool) ? node.Type == JTokenType.Boolean
            : type == typeof(ulong) ? node.Type == JTokenType.Integer
            : type.IsEnum && node.Type is JTokenType.Integer or JTokenType.String
                && Enum.TryParse(type, node.ToString(), false, out var parsed) && Enum.IsDefined(type, parsed);
        if (!valid) throw Error("JSON", path, "wrong field type or undefined enum value");
    }

    private static void RequireDiscriminator(Type type, Func<string, bool> present, string format, string path)
    {
        var field = type == typeof(ExtensionManifest) || typeof(AddonManifestBase).IsAssignableFrom(type) ? "Type" : null;
        if (field is not null && !present(field)) throw Error(format, path, $"missing {field}");
    }

    private static Type? ListItem(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
        ? type.GetGenericArguments()[0] : null;

    private static bool IsScalar(Type type) => type == typeof(string) || type == typeof(Version)
        || type == typeof(DateTime) || type == typeof(bool) || type == typeof(ulong) || type.IsEnum;

    private static Type? MemberType(Type type, string name, bool ignoreCase = false)
    {
        if (type == typeof(Dictionary<string, string>)) return typeof(string);
        return type.GetProperties().FirstOrDefault(p => p.GetCustomAttribute<YamlIgnoreAttribute>() is null
            && string.Equals(p.Name, name, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))?.PropertyType;
    }

    private static void Validate(object? value, string path)
    {
        if (value is null) throw Error("manifest", path, "null document or collection item");
        switch (value)
        {
            case BaseExtensionManifest extension:
                Required(extension.Id, path + ".Id");
                if (!Version.TryParse(extension.Version, out _)) throw Error("manifest", path + ".Version", "expected a real version");
                if (extension is ExtensionManifest plugin) Required(plugin.Module, path + ".Module");
                if (extension is ThemeManifest theme && theme.ThemeApiVersion is not null
                    && !Version.TryParse(theme.ThemeApiVersion, out _)) throw Error("manifest", path + ".ThemeApiVersion", "expected a real version");
                break;
            case AddonManifestBase addon:
                Required(addon.AddonId, path + ".AddonId");
                break;
            case AddonInstallerManifestBase installer:
                Required(installer.AddonId, path + ".AddonId");
                break;
            case AddonInstallerPackage package:
                if (package.Version is null) throw Error("manifest", path + ".Version", "required");
                Required(package.PackageUrl, path + ".PackageUrl");
                break;
            case UpdateManifest update:
                if (update.Version is null) throw Error("manifest", path + ".Version", "required");
                Required(update.Sha256, path + ".Sha256");
                if (update.PackageUrls is null || update.PackageUrls.Count == 0) throw Error("manifest", path + ".PackageUrls", "required non-empty list");
                break;
            case EmulatorDefinition emulator:
                Required(emulator.Id, path + ".Id");
                if (emulator.Profiles is null || emulator.Profiles.Count == 0) throw Error("manifest", path + ".Profiles", "required non-empty list");
                break;
            case EmulatedPlatform platform: Required(platform.Id, path + ".Id"); break;
            case EmulatedRegion region: Required(region.Id, path + ".Id"); break;
        }

        var type = value.GetType();
        if (type.IsEnum && !Enum.IsDefined(type, value)) throw Error("manifest", path, "undefined enum value");
        if (IsScalar(type)) return;
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary) Validate(entry.Value, path + "." + entry.Key);
        }
        else if (value is IEnumerable items)
        {
            var i = 0;
            foreach (var item in items) Validate(item, $"{path}[{i++}]");
        }
        else
        {
            foreach (var property in type.GetProperties().Where(p => p.GetCustomAttribute<YamlIgnoreAttribute>() is null))
            {
                if (property.GetValue(value) is { } child) Validate(child, path + "." + property.Name);
            }
        }
    }

    private static void Required(string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Error("manifest", path, "required non-empty string");
    }

    private static SerializationException Error(string format, string path, string message) => new(format, $"{path}: {message}.");
}
