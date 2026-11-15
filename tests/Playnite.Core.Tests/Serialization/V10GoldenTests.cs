using Newtonsoft.Json.Linq;
using Playnite.Core.Serialization;
using Xunit;

namespace Playnite.Core.Tests.Serialization;

/// <summary>
/// Round-trip evidence grounded in the frozen v10 oracle, not a hand-written expectation.
/// </summary>
/// <remarks>
/// <para>
/// JSON: <c>tests/golden/settings/config.json</c> is a v10 settings document produced by the
/// v10 writer (the <c>ToJson(settings, formatted: true)</c> path with v10's resolver). The test
/// pins its SHA-256, reads it through the ported helper, writes it back and requires semantic
/// equality of the two JSON trees.
/// </para>
/// <para>
/// YAML: <c>tests/golden/manifests/*.extension.yaml</c> are the v10 manifest shape that
/// <c>ExtensionManifest.FromFile</c> consumed with <c>DeserializerBuilder().IgnoreUnmatchedProperties()</c>.
/// The test pins their SHA-256, binds them with a model mirroring
/// <c>legacy/source/Playnite/Manifests/ExtensionManifest.cs</c> (including enum-by-name binding),
/// and round-trips the result.
/// </para>
/// <para>
/// INI: <c>legacy/source/Tests/Playnite.Tests/Resources/TestIni.ini</c> is the fixture v10's own
/// <c>IniParserTests.StandardTest</c> reads. The test replays v10's assertions through the port.
/// </para>
/// <para>
/// TOML: v10 has no TOML writer and no TOML artifact: its only TOML surface is the reader
/// (<c>Serialization.FromToml</c> via Nett) and the empty resources under
/// <c>legacy/source/Tests/Playnite.Tests/Resources/Serialization/</c>. The test records that fact
/// and asserts the reader-side contract (an empty document, human-authored tables and arrays);
/// it does not claim a v10-produced TOML golden.
/// </para>
/// </remarks>
public sealed class V10GoldenTests
{
    private const string ConfigJsonSha256 = "ad0345e8d3f7472f3d0c1e43482c6ef507c09783b9a37f168e1775b931cb1465";
    private const string TestPluginManifestSha256 = "eabccd40dbec668b7f3e60ba78bf55bbadd2d5c1d5c15ccbd06ed666c953a36a";
    private const string TestGameLibraryManifestSha256 = "be7a7d7cbbf5f25fa30501bb07d7bae4501bc17fafc8ec986b27169ea7a61885";
    private const string V10IniFixtureSha256 = "551524459280522dd8f31c1217a6df8ca476b6700c837313bce31d3abb0952c5";

    [Fact]
    public void Json_round_trips_the_v10_settings_golden_semantically()
    {
        var path = RepositoryPaths.Combine("tests", "golden", "settings", "config.json");
        Assert.Equal(ConfigJsonSha256, RepositoryPaths.Sha256(path));

        var original = File.ReadAllText(path);
        var value = JsonSerialization.FromJson<Dictionary<string, object>>(original);
        Assert.NotNull(value);
        Assert.Equal(7L, Assert.IsType<long>(value["Version"]));

        var written = JsonSerialization.ToJson(value);
        Assert.True(
            JToken.DeepEquals(JToken.Parse(original), JToken.Parse(written)),
            "The v10 settings golden did not round-trip semantically through JsonSerialization.");
    }

    [Fact]
    public void Yaml_reads_and_round_trips_the_v10_manifest_goldens()
    {
        var pluginPath = RepositoryPaths.Combine("tests", "golden", "manifests", "TestPlugin.extension.yaml");
        Assert.Equal(TestPluginManifestSha256, RepositoryPaths.Sha256(pluginPath));
        var plugin = YamlSerialization.FromYaml<ExtensionManifestGolden>(File.ReadAllText(pluginPath))!;
        Assert.Equal("TestPlugin.dll", plugin.Module);
        Assert.Equal("Test_Generic_Plugin", plugin.Id);
        Assert.Equal("Test Generic Plugin", plugin.Name);
        Assert.Equal("Josef Nemec", plugin.Author);
        Assert.Equal("1.0", plugin.Version);
        Assert.Equal(ExtensionTypeGolden.GenericPlugin, plugin.Type);
        AssertYamlRoundTrips(plugin);

        var libraryPath = RepositoryPaths.Combine("tests", "golden", "manifests", "TestGameLibrary.extension.yaml");
        Assert.Equal(TestGameLibraryManifestSha256, RepositoryPaths.Sha256(libraryPath));
        var library = YamlSerialization.FromYaml<ExtensionManifestGolden>(File.ReadAllText(libraryPath))!;
        Assert.Equal("TestGameLibrary.dll", library.Module);
        Assert.Equal("Test_GameLibrary", library.Id);
        Assert.Equal("Test GameLibrary", library.Name);
        Assert.Equal("Josef Nemec", library.Author);
        Assert.Equal("1.0", library.Version);
        Assert.Equal(ExtensionTypeGolden.GameLibrary, library.Type);
        AssertYamlRoundTrips(library);
    }

    [Fact]
    public void Ini_reads_the_v10_parser_fixture_with_the_v10_assertions()
    {
        var path = RepositoryPaths.Combine(
            "legacy", "source", "Tests", "Playnite.Tests", "Resources", "TestIni.ini");
        Assert.Equal(V10IniFixtureSha256, RepositoryPaths.Sha256(path));

        // v10 called IniParser.Parse(File.ReadAllLines(path)); replay that overload.
        var data = IniParser.Parse(File.ReadAllLines(path));

        // The two assertions in legacy/source/Tests/Playnite.Tests/IniParserTests.cs.
        Assert.Equal("true", data["RegionDoesRequirePermission"]?["GameDev"]);
        Assert.Null(data["test"]?["test2"]);
        Assert.NotEmpty(data.Sections);
    }

    [Fact]
    public void Toml_has_no_v10_writer_or_v10_artifact_so_the_golden_is_the_reader_contract()
    {
        var serializationSource = File.ReadAllText(
            RepositoryPaths.Combine("legacy", "source", "Playnite", "Common", "Serialization.cs"));
        Assert.Contains("Toml.ReadString<T>", serializationSource);
        Assert.DoesNotContain("Toml.Write", serializationSource);
        Assert.DoesNotContain("ToToml", serializationSource);

        var sdkSource = File.ReadAllText(
            RepositoryPaths.Combine("legacy", "source", "PlayniteSDK", "Data", "DataSerialization.cs"));
        Assert.Contains("FromToml<T>", sdkSource);
        Assert.DoesNotContain("ToToml<T>", sdkSource);

        var v10Resources = RepositoryPaths.Combine(
            "legacy", "source", "Tests", "Playnite.Tests", "Resources", "Serialization");
        foreach (var empty in new[] { "valid.toml", "invalid.toml" })
        {
            var file = new FileInfo(Path.Combine(v10Resources, empty));
            Assert.True(file.Exists, $"{empty} is part of the frozen v10 fixture set.");
            Assert.Equal(0, file.Length);
        }

        // The reader-side contract v10 had: tables and arrays read, nulls unrepresentable.
        var sample = TomlSerialization.FromToml<TomlSample>(
            """
            Name = "héllo"
            Count = 3
            Tags = ["a", "b"]
            [First]
            Label = "child"
            Values = [1]
            """)!;
        Assert.Equal("héllo", sample.Name);
        Assert.Equal(3, sample.Count);
        Assert.Equal(["a", "b"], sample.Tags);
        Assert.Equal("child", sample.First!.Label);

        var exception = Assert.Throws<SerializationException>(
            () => TomlSerialization.ToToml(new TomlSample { Name = "present", Count = 1, First = new SerializationChild() }));
        Assert.Contains("null is not representable in TOML", exception.Message);
    }

    private static void AssertYamlRoundTrips(ExtensionManifestGolden manifest)
    {
        var written = YamlSerialization.ToYaml(manifest);
        var again = YamlSerialization.FromYaml<ExtensionManifestGolden>(written)!;
        Assert.Equal(manifest.Module, again.Module);
        Assert.Equal(manifest.Id, again.Id);
        Assert.Equal(manifest.Name, again.Name);
        Assert.Equal(manifest.Author, again.Author);
        Assert.Equal(manifest.Version, again.Version);
        Assert.Equal(manifest.Type, again.Type);
    }

    // Mirrors legacy/source/Playnite/Manifests/ExtensionManifest.cs: member names and the
    // ExtensionType enum bound by name.
    public sealed class ExtensionManifestGolden
    {
        public string? Module { get; set; }

        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Author { get; set; }

        public string? Version { get; set; }

        public ExtensionTypeGolden Type { get; set; }
    }

    public enum ExtensionTypeGolden
    {
        GenericPlugin,
        GameLibrary,
        Script,
        MetadataProvider,
    }
}
