using Playnite.Core.Manifests;
using Playnite.Core.Serialization;
using Xunit;

namespace Playnite.Core.Tests;

public class ManifestTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifests-v10", name));

    private static void YamlRoundTrip<T>(string name) where T : class
    {
        var first = ManifestSerialization.FromYaml<T>(Fixture(name));
        var written = ManifestSerialization.ToYaml(first);
        Assert.Equal(written, ManifestSerialization.ToYaml(ManifestSerialization.FromYaml<T>(written)));
    }

    private static void JsonRoundTrip<T>(string name) where T : class
    {
        var first = ManifestSerialization.FromJson<T>(Fixture(name));
        var written = ManifestSerialization.ToJson(first);
        Assert.True(Newtonsoft.Json.Linq.JToken.DeepEquals(
            Newtonsoft.Json.Linq.JToken.Parse(Fixture(name)), Newtonsoft.Json.Linq.JToken.Parse(written)));
        Assert.Equal(written, ManifestSerialization.ToJson(ManifestSerialization.FromJson<T>(written)));
        var yaml = ManifestSerialization.ToYaml(first);
        Assert.Equal(written, ManifestSerialization.ToJson(ManifestSerialization.FromYaml<T>(yaml)));
    }

    [Theory]
    [InlineData("library.yaml", ExtensionType.GameLibrary)]
    [InlineData("metadata.yaml", ExtensionType.MetadataProvider)]
    [InlineData("generic.yaml", ExtensionType.GenericPlugin)]
    public void Legacy_extension_shapes_round_trip(string file, ExtensionType type)
    {
        var manifest = ManifestSerialization.FromYaml<ExtensionManifest>(Fixture(file));
        Assert.Equal(type, manifest.Type);
        Assert.Equal("1.0", manifest.Version);
        Assert.EndsWith(".dll", manifest.Module);
        YamlRoundTrip<ExtensionManifest>(file);
    }

    [Theory]
    [InlineData("desktop.yaml", ApplicationMode.Desktop)]
    [InlineData("fullscreen.yaml", ApplicationMode.Fullscreen)]
    public void Legacy_theme_shapes_round_trip(string file, ApplicationMode mode)
    {
        Assert.Equal(mode, ManifestSerialization.FromYaml<ThemeManifest>(Fixture(file)).Mode);
        YamlRoundTrip<ThemeManifest>(file);
    }

    [Fact]
    public void Legacy_emulation_documents_round_trip()
    {
        var emulator = ManifestSerialization.FromYaml<EmulatorDefinition>(Fixture("emulator.yaml"));
        Assert.Equal("dolphin", emulator.Id);
        Assert.Equal("--exec=\"{ImagePath}\" --batch", emulator.Profiles![0].StartupArguments);
        Assert.Equal("^Dolphin\\.exe$", emulator.Profiles[0].StartupExecutable);
        Assert.Contains("nintendo_gamecube", emulator.Profiles[0].Platforms!);
        var platforms = ManifestSerialization.FromYaml<List<EmulatedPlatform>>(Fixture("platforms.yaml"));
        Assert.Contains(platforms, p => p.Id == "nintendo_gamecube");
        var regions = ManifestSerialization.FromYaml<List<EmulatedRegion>>(Fixture("regions.yaml"));
        Assert.Contains(regions, r => r.Id == "europe" && r.DefaultImport && r.IgdbId == 1);
        YamlRoundTrip<EmulatorDefinition>("emulator.yaml");
        YamlRoundTrip<List<EmulatedPlatform>>("platforms.yaml");
        YamlRoundTrip<List<EmulatedRegion>>("regions.yaml");
    }

    [Fact]
    public void Service_shapes_round_trip_with_unicode_escaping_versions_and_dates()
    {
        JsonRoundTrip<AddonManifest>("addon.json");
        JsonRoundTrip<AddonInstallerManifest>("installer.json");
        JsonRoundTrip<UpdateManifest>("update.json");
        JsonRoundTrip<ReleaseNoteData>("release-note.json");
        var addon = ManifestSerialization.FromJson<AddonManifest>(Fixture("addon.json"));
        Assert.Equal("日本語 🎮 \"quoted\"\\path\nnext", addon.Name);
        Assert.Equal(DateTimeKind.Utc, addon.UserAgreement!.Updated.Kind);
        Assert.Equal(new Version(10, 64), ManifestSerialization.FromJson<UpdateManifest>(Fixture("update.json")).Version);
    }

    [Theory]
    [InlineData("1.0", "1.1")]
    [InlineData("1.1", "1.2")]
    [InlineData("2.0", "1.3")]
    public void Package_selection_matches_v10_test(string api, string expected)
    {
        var installer = ManifestSerialization.FromJson<AddonInstallerManifest>(Fixture("installer.json"));
        Assert.Equal(new Version(expected), installer.GetCompatiblePackages(new Version(api))![0].Version);
        Assert.Empty(installer.GetCompatiblePackages(new Version(3, 0))!);
        Assert.Null(new AddonInstallerManifest().GetCompatiblePackages(new Version(api)));
    }

    [Fact]
    public void Unknown_fields_follow_v10_ignore_rule_including_nested_fields()
    {
        var yaml = Fixture("library.yaml") + "\nFuture: {Nested: [one, two]}\nDirectoryPath: injected\nLinks:\n- Name: site\n  Url: https://example.invalid\n  Future: ignored\n";
        var manifest = ManifestSerialization.FromYaml<ExtensionManifest>(yaml);
        Assert.Null(manifest.DirectoryPath);
        Assert.Single(manifest.Links!);
        Assert.DoesNotContain("Future", ManifestSerialization.ToYaml(manifest));
        var json = Fixture("addon.json").TrimEnd().TrimEnd('}') + ",\"Future\":{\"nested\":[1]}}";
        Assert.DoesNotContain("Future", ManifestSerialization.ToJson(ManifestSerialization.FromJson<AddonManifest>(json)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("Id: [broken")]
    [InlineData("Id: x\nId: y")]
    [InlineData("Id: x\nVersion: nope\nModule: x\nType: GameLibrary")]
    [InlineData("Id: x\nVersion: 1.0\nModule: x")]
    [InlineData("Id: x\nVersion: 1.0\nType: 99\nModule: x")]
    [InlineData("Id: x\nVersion: 1.0\nType: null\nModule: x")]
    [InlineData("Id: x\nVersion: 1.0\nType: GameLibrary\nModule: []")]
    [InlineData("Id: x\nVersion: 1.0\nType: GameLibrary\nModule: x\nLinks: [null]")]
    [InlineData("Id: x\nVersion: 1.0\nType: GameLibrary\nModule: x\n---\nId: y")]
    [InlineData("Id: x\nVersion: 1.0\nType: !evil GameLibrary\nModule: x")]
    public void Invalid_extension_documents_fail_explicitly(string yaml)
    {
        Assert.Throws<SerializationException>(() => ManifestSerialization.FromYaml<ExtensionManifest>(yaml));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"AddonId\":\"x\",\"Type\":99}")]
    [InlineData("{\"AddonId\":\"x\",\"Type\":0,\"Name\":12}")]
    [InlineData("{\"AddonId\":\"x\",\"Type\":0,\"Tags\":[null]}")]
    [InlineData("{\"AddonId\":\"x\",\"Type\":0,\"Tags\":{}}")]
    [InlineData("{\"AddonId\":\"x\",\"Type\":0,\"UserAgreement\":{\"Updated\":\"bad\"}}")]
    [InlineData("{\"AddonId\":\"x\",\"Type\":0,\"UserAgreement\":{\"Updated\":null}}")]
    [InlineData("{\"AddonId\":\"x\",\"Type\":0,\"Screenshots\":[{\"Image\":true}]}")]
    [InlineData("{\"AddonId\":\"x\",\"Type\":0} trailing")]
    [InlineData("{\"AddonId\":\"x\",\"Type\":0,\"Type\":1}")]
    [InlineData("{\"AddonId\":\"x\",\"Type\":")]
    public void Invalid_addon_documents_fail_explicitly(string json)
    {
        Assert.Throws<SerializationException>(() => ManifestSerialization.FromJson<AddonManifest>(json));
    }

    [Theory]
    [InlineData("Id: x\nProfiles: []")]
    [InlineData("Id: x\nProfiles: [null]")]
    [InlineData("Id: x\nProfiles: [{ScriptStartup: 1}]")]
    [InlineData("Id: x\nProfiles: [{Platforms: x}]")]
    public void Invalid_emulators_fail(string yaml) => Assert.Throws<SerializationException>(() => ManifestSerialization.FromYaml<EmulatorDefinition>(yaml));

    [Theory]
    [InlineData("- Id: x\n  IgdbId: -1")]
    [InlineData("- Id: x\n  IgdbId: 18446744073709551616")]
    [InlineData("- Id: x\n  IgdbId: 1.5")]
    [InlineData("- Id: x\n  DefaultImport: maybe")]
    [InlineData("- Name: missing id")]
    [InlineData("- null")]
    public void Invalid_regions_fail(string yaml) => Assert.Throws<SerializationException>(() => ManifestSerialization.FromYaml<List<EmulatedRegion>>(yaml));

    [Fact]
    public void Optional_absence_and_empty_values_keep_v10_meaning()
    {
        var theme = ManifestSerialization.FromYaml<ThemeManifest>("Id: x\nVersion: 1.0\nName: ''");
        Assert.Equal(ApplicationMode.Desktop, theme.Mode);
        Assert.False(theme.ForceLTRLayout);
        Assert.Equal("", theme.Name);
        Assert.Null(theme.Links);
        var region = ManifestSerialization.FromYaml<List<EmulatedRegion>>("- Id: x")[0];
        Assert.Equal(0UL, region.IgdbId);
        Assert.False(region.DefaultImport);
        var installer = ManifestSerialization.FromJson<AddonInstallerManifest>("{\"AddonId\":\"x\"}");
        Assert.Null(installer.Packages);
        var package = ManifestSerialization.FromJson<AddonInstallerManifest>("{\"AddonId\":\"x\",\"Packages\":[{\"Version\":\"1.0\",\"PackageUrl\":\"x\"}]}");
        var reread = ManifestSerialization.FromYaml<AddonInstallerManifest>(ManifestSerialization.ToYaml(package));
        Assert.Null(reread.Packages![0].RequiredApiVersion);
    }

    [Fact]
    public void Required_update_and_installer_members_fail()
    {
        Assert.Throws<SerializationException>(() => ManifestSerialization.FromJson<UpdateManifest>("{}"));
        Assert.Throws<SerializationException>(() => ManifestSerialization.FromJson<UpdateManifest>("{\"Version\":\"bad\"}"));
        Assert.Throws<SerializationException>(() => ManifestSerialization.FromJson<UpdateManifest>("{\"Version\":\"1.0\"}"));
        Assert.Throws<SerializationException>(() => ManifestSerialization.FromJson<UpdateManifest>("{\"Version\":\"1.0\",\"Sha256\":\"abc\",\"PackageUrls\":[]}"));
        Assert.Throws<SerializationException>(() => ManifestSerialization.FromYaml<ThemeManifest>("Id: x\nVersion: 1.0\nThemeApiVersion: bad"));
        Assert.Throws<SerializationException>(() => ManifestSerialization.FromJson<AddonInstallerManifest>("{\"AddonId\":\"x\",\"Packages\":[{}]}"));
        Assert.Throws<SerializationException>(() => ManifestSerialization.FromJson<AddonInstallerManifest>("{\"AddonId\":\"x\",\"Packages\":[{\"Version\":\"1.0\"}]}"));
    }

    [Theory]
    [InlineData("0.0")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4")]
    [InlineData("2147483647.2147483647.2147483647.2147483647")]
    public void Yaml_version_converter_preserves_component_count_value_and_runtime_type(string version)
    {
        var note = new ReleaseNoteData { Version = new Version(version), Note = "日本語" };
        var yaml = ManifestSerialization.ToYaml(note);
        Assert.Contains("\"" + version + "\"", yaml);
        var parsed = ManifestSerialization.FromYaml<ReleaseNoteData>(yaml);
        Assert.IsType<Version>(parsed.Version);
        Assert.Equal(version, parsed.Version.ToString());
        Assert.Equal(note.Version, parsed.Version);
    }

    [Theory]
    [InlineData("1.2.3-alpha")]
    [InlineData("1.2.3+build.5")]
    [InlineData("1.2.3-alpha+build.5")]
    public void System_version_fields_reject_semver_suffixes_instead_of_losing_them(string version)
    {
        Assert.Throws<SerializationException>(() => ManifestSerialization.FromYaml<ReleaseNoteData>($"Version: '{version}'"));
        Assert.Throws<SerializationException>(() => ManifestSerialization.FromJson<ReleaseNoteData>($"{{\"Version\":\"{version}\"}}"));
    }

    [Theory]
    [InlineData("addon.json")]
    [InlineData("installer.json")]
    [InlineData("update.json")]
    [InlineData("library.yaml")]
    [InlineData("emulator.yaml")]
    [InlineData("regions.yaml")]
    public void Seeded_manifest_mutation_fuzz_returns_values_or_explicit_errors(string file)
    {
        var source = Fixture(file);
        var random = new Random(5005);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 64; i++)
        {
            var index = random.Next(source.Length);
            var mutation = i % 2 == 0 ? source[..index] : source[..index] + "[{:!\u0000" + source[(index + 1)..];
            var exception = Record.Exception(() => ParseFuzz(file, mutation));
            Assert.True(exception is null or SerializationException,
                $"{file} mutation {i} at {index}: {exception}; input={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(mutation))}");
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"{file} exceeded the 20-second fuzz budget");
    }

    private static void ParseFuzz(string file, string text)
    {
        switch (file)
        {
            case "addon.json": ManifestSerialization.FromJson<AddonManifest>(text); break;
            case "installer.json": ManifestSerialization.FromJson<AddonInstallerManifest>(text); break;
            case "update.json": ManifestSerialization.FromJson<UpdateManifest>(text); break;
            case "library.yaml": ManifestSerialization.FromYaml<ExtensionManifest>(text); break;
            case "emulator.yaml": ManifestSerialization.FromYaml<EmulatorDefinition>(text); break;
            case "regions.yaml": ManifestSerialization.FromYaml<List<EmulatedRegion>>(text); break;
            default: throw new ArgumentException(file);
        }
    }

    [Fact]
    public void Manifest_boundaries_keep_shared_depth_size_and_bom_rules()
    {
        var oversized = new string(' ', SerializationLimits.DefaultMaxDocumentBytes + 1);
        Assert.Throws<SerializationException>(() => ManifestSerialization.FromYaml<ExtensionManifest>(oversized));
        Assert.Throws<SerializationException>(() => ManifestSerialization.FromJson<AddonManifest>(oversized));
        var nestedJson = "{\"Future\":" + new string('[', 130) + "0" + new string(']', 130) + "}";
        Assert.Throws<SerializationException>(() => ManifestSerialization.FromJson<AddonManifest>(nestedJson));
        var nestedYaml = "Future: " + new string('[', 130) + "0" + new string(']', 130);
        Assert.Throws<SerializationException>(() => ManifestSerialization.FromYaml<ExtensionManifest>(nestedYaml));
        Assert.Equal("Test_GameLibrary", ManifestSerialization.FromYaml<ExtensionManifest>("\uFEFF" + Fixture("library.yaml")).Id);
        Assert.Equal("test-addon", ManifestSerialization.FromJson<AddonManifest>("\uFEFF" + Fixture("addon.json")).AddonId);
    }

    [Fact]
    public void Seeded_text_property_round_trips_and_truncation_fuzz_never_succeeds_as_default()
    {
        var random = new Random(5005);
        const string alphabet = "日本語abc\"\\\n\r\t:[]{}#'";
        for (var i = 0; i < 128; i++)
        {
            var text = new string(Enumerable.Range(0, random.Next(1, 100)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var addon = new AddonManifest { AddonId = "x", Type = AddonType.Generic, Name = text };
            Assert.Equal(text, ManifestSerialization.FromJson<AddonManifest>(ManifestSerialization.ToJson(addon)).Name);
            Assert.Equal(text, ManifestSerialization.FromYaml<AddonManifest>(ManifestSerialization.ToYaml(addon)).Name);
        }

        var json = Fixture("addon.json").TrimEnd();
        for (var length = 0; length < json.Length; length++)
            Assert.Throws<SerializationException>(() => ManifestSerialization.FromJson<AddonManifest>(json[..length]));
    }
}
