using System.Diagnostics;
using System.Text;
using Playnite.Core;
using Playnite.Core.Manifests;
using Playnite.Core.Plugins;
using Playnite.Core.Themes;
using Playnite.Platform.Linux;
using Xunit;

namespace Playnite.Core.Tests.Themes;

public sealed class ThemePackParserTests : IDisposable
{
    private static readonly IRegularFileProbe Probe = new LinuxRegularFileProbe();
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "playnite-theme-tests-" + Path.GetRandomFileName());

    [Theory]
    [InlineData("#F50", 0xFF, 0xFF, 0x55, 0x00)]
    [InlineData("#8AF2", 0x88, 0xAA, 0xFF, 0x22)]
    [InlineData("#112233", 0xFF, 0x11, 0x22, 0x33)]
    [InlineData("#55112233", 0x55, 0x11, 0x22, 0x33)]
    [InlineData("#aabbccdd", 0xAA, 0xBB, 0xCC, 0xDD)]
    public void Hex_color_parser_accepts_valid_hex_formats(string raw, byte a, byte r, byte g, byte b)
    {
        Assert.True(ThemeRgbaColor.TryParse(raw, out var color));
        Assert.Equal(new ThemeRgbaColor(a, r, g, b), color);
        Assert.Equal(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"#{a:X2}{r:X2}{g:X2}{b:X2}"), color.ToHexString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" #112233")]
    [InlineData("#112233 ")]
    [InlineData("Red")]
    [InlineData("112233")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#123456789")]
    [InlineData("#GGHHII")]
    public void Hex_color_parser_rejects_invalid_or_named_colors(string? raw)
    {
        Assert.False(ThemeRgbaColor.TryParse(raw, out _));
    }

    [Fact]
    public void Manifest_parser_loads_inline_tokens_and_supported_api_versions()
    {
        const string yaml = """
            Id: nordic-slate
            Name: Nordic Slate
            Author: Playnite Community
            Version: 1.2.3
            ThemeApiVersion: 2.10.0
            Mode: Desktop
            BaseVariant: Light
            Description: Clean slate palette
            Colors:
              accent: "#38BDF8"
              PlayniteSurfaceColor: "#FF1E293B"
            Metrics:
              cardWidth: 260
              LibraryDetailsWidth: 360
            Typography:
              FontFamily: Noto Sans, sans-serif
              WindowFontSize: 15
            """;

        var result = ThemePackParser.ParseManifest(yaml, tempRoot);

        Assert.True(result.IsValid);
        Assert.Empty(result.Diagnostics);
        var doc = result.Document!;
        Assert.Equal("nordic-slate", doc.Descriptor.Id);
        Assert.Equal("Nordic Slate", doc.Descriptor.Name);
        Assert.Equal(new Version(1, 2, 3), doc.Descriptor.Version);
        Assert.Equal(new Version(2, 10, 0), doc.Descriptor.ThemeApiVersion);
        Assert.Equal(ApplicationMode.Desktop, doc.Descriptor.Mode);
        Assert.Equal(ThemeBaseVariant.Light, doc.Descriptor.BaseVariant);
        Assert.Equal(new ThemeRgbaColor(0xFF, 0x38, 0xBD, 0xF8), doc.Colors["PlayniteAccentColor"]);
        Assert.Equal(new ThemeRgbaColor(0xFF, 0x1E, 0x29, 0x3B), doc.Colors["PlayniteSurfaceColor"]);
        Assert.Equal(260, doc.Metrics[ThemePackDefaults.LibraryCardWidthKey]);
        Assert.Equal(360, doc.Metrics[ThemePackDefaults.LibraryDetailsWidthKey]);
        Assert.Equal(15, doc.Metrics[ThemePackDefaults.WindowFontSizeKey]);
        Assert.Equal("Noto Sans, sans-serif", doc.FontFamily);
    }

    [Theory]
    [InlineData("2.0.0", true)]
    [InlineData("2.10.0", true)]
    [InlineData("2.10.0.0", true)]
    [InlineData("2.10.0.1", false)]
    [InlineData("1.0.0", false)]
    [InlineData("2.11.0", false)]
    [InlineData("3.0.0", false)]
    [InlineData("not-a-version", false)]
    public void Manifest_parser_enforces_theme_api_version_compatibility(string apiVersion, bool expectedValid)
    {
        var yaml = $"""
            Id: api-check
            Name: API Check
            Version: 1.0.0
            ThemeApiVersion: {apiVersion}
            """;

        var result = ThemePackParser.ParseManifest(yaml, tempRoot);

        Assert.Equal(expectedValid, result.IsValid);
        if (!expectedValid)
        {
            Assert.NotEmpty(result.Diagnostics);
        }
    }

    [Fact]
    public void Manifest_parser_requires_theme_api_version()
    {
        const string yaml = """
            Id: missing-api
            Name: Missing API
            Version: 1.0.0
            """;

        var result = ThemePackParser.ParseManifest(yaml, tempRoot);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Code == ThemePackParser.MissingApiVersionCode);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("bad/id")]
    [InlineData(" leading-space")]
    [InlineData(PluginHost.HostStatePluginId)]
    [InlineData(ThemePackDefaults.DefaultDesktopThemeId)]
    [InlineData(ThemePackDefaults.DefaultFullscreenThemeId)]
    public void Manifest_parser_rejects_unsafe_or_reserved_ids(string themeId)
    {
        var yaml = $"""
            Id: "{themeId}"
            Name: Unsafe Theme
            Version: 1.0.0
            ThemeApiVersion: 2.10.0
            """;

        var result = ThemePackParser.ParseManifest(yaml, tempRoot);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void Manifest_parser_rejects_unknown_properties_duplicate_keys_tags_and_anchors()
    {
        const string unknownProp = """
            Id: unknown-prop
            Name: Unknown Prop
            Version: 1.0.0
            ThemeApiVersion: 2.10.0
            CustomScript: echo pwned
            """;
        Assert.Contains(
            ThemePackParser.ParseManifest(unknownProp, tempRoot).Diagnostics,
            d => d.Code == ThemePackParser.UnknownPropertyCode);

        const string duplicateKey = """
            Id: dup-key
            Name: Dup Key
            Version: 1.0.0
            ThemeApiVersion: 2.10.0
            Colors:
              accent: "#112233"
              PlayniteAccentColor: "#445566"
            """;
        Assert.False(ThemePackParser.ParseManifest(duplicateKey, tempRoot).IsValid);

        const string yamlTag = """
            Id: tagged
            Name: !System.IO.FileInfo tagged
            Version: 1.0.0
            ThemeApiVersion: 2.10.0
            """;
        Assert.False(ThemePackParser.ParseManifest(yamlTag, tempRoot).IsValid);

        const string yamlAnchor = """
            Id: anchored
            Name: Anchored
            Version: 1.0.0
            ThemeApiVersion: 2.10.0
            Description: &a hello
            """;
        Assert.False(ThemePackParser.ParseManifest(yamlAnchor, tempRoot).IsValid);
    }

    [Theory]
    [InlineData("WindowFontSize", 7.5, false)]
    [InlineData("WindowFontSize", 8.0, true)]
    [InlineData("WindowFontSize", 48.0, true)]
    [InlineData("WindowFontSize", 48.5, false)]
    [InlineData("LibraryCardWidth", 119.0, false)]
    [InlineData("LibraryCardWidth", 481.0, false)]
    [InlineData("LibraryDetailsWidth", 219.0, false)]
    [InlineData("LibraryDetailsWidth", 641.0, false)]
    [InlineData("CardCornerRadius", -1.0, false)]
    [InlineData("CardCornerRadius", 25.0, false)]
    public void Metric_bounds_are_enforced_strictly(string metric, double value, bool expectedValid)
    {
        var yaml = $"""
            Id: metric-test
            Name: Metric Test
            Version: 1.0.0
            ThemeApiVersion: 2.10.0
            Metrics:
              {metric}: {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}
            """;

        Assert.Equal(expectedValid, ThemePackParser.ParseManifest(yaml, tempRoot).IsValid);
    }

    [Fact]
    public void Declarative_axaml_parser_loads_colors_brushes_gradient_metrics_and_window_styles()
    {
        const string axaml = """
            <Styles xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Styles.Resources>
                <Color x:Key="PlayniteAccentColor">#FFFF6600</Color>
                <Color x:Key="PlayniteCanvasTopColor">#FF221100</Color>
                <Color x:Key="PlayniteCanvasBottomColor">#FF110800</Color>
                <SolidColorBrush x:Key="PlayniteAccentBrush" Color="{StaticResource PlayniteAccentColor}" />
                <LinearGradientBrush x:Key="PlayniteWindowBackgroundBrush" StartPoint="0%,0%" EndPoint="100%,100%">
                  <GradientStop Color="{StaticResource PlayniteCanvasTopColor}" Offset="0.0" />
                  <GradientStop Color="{StaticResource PlayniteCanvasBottomColor}" Offset="1.0" />
                </LinearGradientBrush>
                <x:Double x:Key="LibraryCardWidth">280</x:Double>
                <FontFamily x:Key="PlayniteFontFamily">Liberation Sans</FontFamily>
              </Styles.Resources>
              <Style Selector=":is(Window)">
                <Setter Property="FontSize" Value="17" />
              </Style>
            </Styles>
            """;

        var result = ThemePackParser.ParseDeclarativeAxaml(axaml, "ember");

        Assert.True(result.IsValid);
        var doc = result.Document!;
        Assert.Equal(new ThemeRgbaColor(0xFF, 0xFF, 0x66, 0x00), doc.Colors["PlayniteAccentColor"]);
        Assert.Equal(new ThemeRgbaColor(0xFF, 0xFF, 0x66, 0x00), doc.BrushColors["PlayniteAccentBrush"]);
        Assert.NotNull(doc.WindowBackgroundGradient);
        Assert.Equal(2, doc.WindowBackgroundGradient!.Stops.Count);
        Assert.Equal(280, doc.Metrics[ThemePackDefaults.LibraryCardWidthKey]);
        Assert.Equal(17, doc.Metrics[ThemePackDefaults.WindowFontSizeKey]);
        Assert.Equal("Liberation Sans", doc.FontFamily);
    }

    [Theory]
    [InlineData("<!DOCTYPE Styles><Styles xmlns=\"https://github.com/avaloniaui\" />")]
    [InlineData("<ResourceDictionary xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><Button x:Key=\"Evil\" /></ResourceDictionary>")]
    [InlineData("<ResourceDictionary xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><SolidColorBrush x:Key=\"PlayniteAccentBrush\" Color=\"{Binding Foo}\" /></ResourceDictionary>")]
    [InlineData("<ResourceDictionary xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><Color x:Key=\"UnknownColor\">#FFFFFFFF</Color></ResourceDictionary>")]
    [InlineData("<Styles xmlns=\"https://github.com/avaloniaui\"><Style Selector=\"Button\"><Setter Property=\"FontSize\" Value=\"20\" /></Style></Styles>")]
    [InlineData("<Styles xmlns=\"https://github.com/avaloniaui\"><Style Selector=\"Window\"><Setter Property=\"Opacity\" Value=\"0.5\" /></Style></Styles>")]
    public void Declarative_axaml_parser_rejects_unsafe_xml_controls_bindings_and_unknown_tokens(string axaml)
    {
        var result = ThemePackParser.ParseDeclarativeAxaml(axaml, "unsafe");

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void Randomized_mutations_never_throw_unhandled_exceptions_from_theme_parsers()
    {
        var rng = new Random(20261127);
        const string validYaml = "Id: fuzz\nName: Fuzz\nVersion: 1.0.0\nThemeApiVersion: 2.10.0\nColors:\n  accent: \"#112233\"\n";
        const string validAxaml = "<ResourceDictionary xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><Color x:Key=\"PlayniteAccentColor\">#FF112233</Color></ResourceDictionary>";
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789:#{}[]<>/\\\"' \n\t!&*";

        for (var i = 0; i < 500; i++)
        {
            var yamlMutated = Mutate(validYaml, alphabet, rng);
            var axamlMutated = Mutate(validAxaml, alphabet, rng);

            var manifestResult = ThemePackParser.ParseManifest(yamlMutated, tempRoot);
            Assert.True(manifestResult.IsValid || manifestResult.Diagnostics.Count > 0);

            var tokensResult = ThemePackParser.ParseTokensYaml(yamlMutated, "fuzz");
            Assert.True(tokensResult.IsValid || tokensResult.Diagnostics.Count > 0);

            var axamlResult = ThemePackParser.ParseDeclarativeAxaml(axamlMutated, "fuzz");
            Assert.True(axamlResult.IsValid || axamlResult.Diagnostics.Count > 0);
        }
    }

    [Fact]
    public void Directory_loader_rejects_symlinks_traversal_missing_resources_and_oversized_files()
    {
        Directory.CreateDirectory(tempRoot);
        var packDir = Path.Combine(tempRoot, "broken-pack");
        Directory.CreateDirectory(packDir);

        File.WriteAllText(Path.Combine(packDir, "theme.yaml"), """
            Id: broken-pack
            Name: Broken Pack
            Version: 1.0.0
            ThemeApiVersion: 2.10.0
            Resources:
              - ../outside.axaml
            """);
        Assert.False(ThemePackParser.LoadFromDirectory(packDir, Probe, tempRoot).IsValid);

        File.WriteAllText(Path.Combine(packDir, "theme.yaml"), """
            Id: broken-pack
            Name: Broken Pack
            Version: 1.0.0
            ThemeApiVersion: 2.10.0
            Resources:
              - missing.axaml
            """);
        var missingRes = ThemePackParser.LoadFromDirectory(packDir, Probe, tempRoot);
        Assert.False(missingRes.IsValid);
        Assert.Contains(missingRes.Diagnostics, d => d.Code == ThemePackParser.MissingResourceCode);

        // Oversized tokens.yaml (> 128 KiB).
        File.WriteAllText(Path.Combine(packDir, "theme.yaml"), """
            Id: broken-pack
            Name: Broken Pack
            Version: 1.0.0
            ThemeApiVersion: 2.10.0
            """);
        File.WriteAllText(
            Path.Combine(packDir, "tokens.yaml"),
            new string(' ', ThemePackParser.MaxResourceFileBytes + 32));
        var oversized = ThemePackParser.LoadFromDirectory(packDir, Probe, tempRoot);
        Assert.False(oversized.IsValid);
        Assert.Contains(oversized.Diagnostics, d => d.Code == ThemePackParser.SizeLimitCode);

        if (OperatingSystem.IsLinux())
        {
            File.Delete(Path.Combine(packDir, "tokens.yaml"));
            var outsideFile = Path.Combine(tempRoot, "outside.axaml");
            File.WriteAllText(outsideFile, "<ResourceDictionary xmlns=\"https://github.com/avaloniaui\" />");
            var linkPath = Path.Combine(packDir, "theme.axaml");
            File.CreateSymbolicLink(linkPath, outsideFile);
            var symlinked = ThemePackParser.LoadFromDirectory(packDir, Probe, tempRoot);
            Assert.False(symlinked.IsValid);
            Assert.Contains(symlinked.Diagnostics, d => d.Code == ThemePackParser.UnsafePathCode);
        }
    }

    [Fact]
    public async Task Fifo_theme_manifest_is_rejected_without_blocking()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Directory.CreateDirectory(tempRoot);
        var packDir = Path.Combine(tempRoot, "fifo-pack");
        Directory.CreateDirectory(packDir);
        var fifoManifest = Path.Combine(packDir, "theme.yaml");
        CreateFifo(fifoManifest);

        var result = await Task.Run(() => ThemePackParser.LoadFromDirectory(packDir, Probe, tempRoot))
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void Catalog_discovers_builtin_charcoal_first_deduplicates_ids_and_surfaces_invalid_packs()
    {
        var root1 = Path.Combine(tempRoot, "root1");
        var root2 = Path.Combine(tempRoot, "root2");
        var pack1 = Path.Combine(root1, "amber");
        var packDup = Path.Combine(root2, "Desktop", "amber-copy");
        var packInvalid = Path.Combine(root1, "corrupt");
        Directory.CreateDirectory(pack1);
        Directory.CreateDirectory(packDup);
        Directory.CreateDirectory(packInvalid);

        File.WriteAllText(Path.Combine(pack1, "theme.yaml"), """
            Id: amber
            Name: Amber Glow
            Version: 1.0.0
            ThemeApiVersion: 2.10.0
            Colors:
              accent: "#F59E0B"
            """);
        File.WriteAllText(Path.Combine(packDup, "theme.yaml"), """
            Id: amber
            Name: Amber Duplicate
            Version: 2.0.0
            ThemeApiVersion: 2.10.0
            """);
        File.WriteAllText(Path.Combine(packInvalid, "theme.yaml"), "Id: corrupt\nName: Corrupt\nVersion: 1.0.0\nThemeApiVersion: 9.0.0\n");

        var discovery = ThemePackCatalog.Discover([root1, root2], Probe);

        Assert.Equal(ThemePackDefaults.DefaultDesktopThemeId, discovery.Entries[0].Id);
        Assert.True(discovery.Entries[0].IsValid);
        Assert.Contains(discovery.Entries, e => e.Id == "amber" && e.IsValid && e.DisplayName == "Amber Glow");
        Assert.Contains(discovery.Entries, e => e.Id == "amber" && !e.IsValid && e.Diagnostics.Any(d => d.Code == "duplicate-theme-id"));
        Assert.Contains(discovery.Entries, e => e.Id == "corrupt" && !e.IsValid);
    }

    [Fact]
    public void Plugin_host_persists_active_theme_id_and_lists_theme_plugin_descriptors()
    {
        Directory.CreateDirectory(tempRoot);
        var storeDir = Path.Combine(tempRoot, "plugin-settings");
        var themePluginDir = Path.Combine(tempRoot, "plugins", "sunset-theme");
        Directory.CreateDirectory(themePluginDir);
        File.WriteAllText(Path.Combine(themePluginDir, "theme.axaml"),
            "<ResourceDictionary xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><Color x:Key=\"PlayniteAccentColor\">#FFFF5500</Color></ResourceDictionary>");
        File.WriteAllText(Path.Combine(themePluginDir, "plugin.json"), """
            {
              "id": "sunset-theme",
              "name": "Sunset Theme",
              "version": "1.0.0",
              "surfaces": ["theme"],
              "capabilities": ["theme"],
              "entryPoints": { "theme": "theme.axaml" }
            }
            """);

        using (var host1 = new PluginHost(
            PluginSurface.Core | PluginSurface.Desktop | PluginSurface.Theme,
            settingsStore: new FilePluginSettingsStore(storeDir, regularFileProbe: Probe)))
        {
            host1.DiscoverExternal(Path.Combine(tempRoot, "plugins"), Probe);
            Assert.Single(host1.GetThemeDescriptors(), d => d.Id == "sunset-theme");
            Assert.Null(host1.GetActiveThemeId());
            Assert.False(host1.SetActiveThemeId("../evil"));
            Assert.True(host1.SetActiveThemeId("sunset-theme"));
        }

        using (var host2 = new PluginHost(
            PluginSurface.Core | PluginSurface.Desktop | PluginSurface.Theme,
            settingsStore: new FilePluginSettingsStore(storeDir, regularFileProbe: Probe)))
        {
            Assert.Equal("sunset-theme", host2.GetActiveThemeId());
            Assert.False(host2.SetActiveThemeId(PluginHost.HostStatePluginId));
            Assert.False(host2.SetActiveThemeId(ThemePackDefaults.DefaultFullscreenThemeId));
            Assert.True(host2.SetActiveThemeId("playnite_builtin_defaultdesktop"));
            Assert.Null(host2.GetActiveThemeId());
        }
    }

    [Fact]
    public void Manifest_parser_rejects_yaml_alias_bomb_before_expansion()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id: alias-bomb");
        sb.AppendLine("Name: Alias Bomb");
        sb.AppendLine("Version: 1.0.0");
        sb.AppendLine("ThemeApiVersion: 2.10.0");
        sb.AppendLine("Links:");
        sb.AppendLine("  - &lnk { Name: x, Url: https://example.com }");
        for (var i = 0; i < 10000; i++)
        {
            sb.AppendLine("  - *lnk");
        }

        var result = ThemePackParser.ParseManifest(sb.ToString(), tempRoot);

        Assert.False(result.IsValid);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("anchors", StringComparison.OrdinalIgnoreCase)
            || d.Message.Contains("aliases", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("https://attacker.invalid/font.ttf#Evil")]
    [InlineData("file:///etc/passwd#Font")]
    [InlineData("avares://MyAssembly/Fonts#Inter")]
    [InlineData("#")]
    [InlineData(",,,")]
    [InlineData(",Inter")]
    [InlineData("A, B, C, D, E, F, G, H, I")]
    public void Font_family_validation_rejects_uri_schemes_and_malformed_lists(string fontFamily)
    {
        var yaml = $"""
            Id: bad-font
            Name: Bad Font
            Version: 1.0.0
            ThemeApiVersion: 2.10.0
            Typography:
              FontFamily: "{fontFamily}"
            """;

        Assert.False(ThemePackParser.ParseManifest(yaml, tempRoot).IsValid);
    }

    [Fact]
    public void Manifest_parser_rejects_cross_section_and_case_variant_duplicates()
    {
        const string crossSection = """
            Id: dup-cross
            Name: Dup Cross
            Version: 1.0.0
            ThemeApiVersion: 2.10.0
            Tokens:
              Colors:
                accent: "#112233"
            Colors:
              PlayniteAccentColor: "#445566"
            """;
        Assert.False(ThemePackParser.ParseManifest(crossSection, tempRoot).IsValid);

        const string typoDup = """
            Id: dup-typo
            Name: Dup Typo
            Version: 1.0.0
            ThemeApiVersion: 2.10.0
            Typography:
              FontFamily: Inter
              fontFamily: Noto Sans
            """;
        Assert.False(ThemePackParser.ParseManifest(typoDup, tempRoot).IsValid);
    }

    [Fact]
    public void Directory_loader_respects_empty_resources_list_and_symmetric_tokens_variant()
    {
        var emptyResPack = Path.Combine(tempRoot, "empty-res");
        Directory.CreateDirectory(emptyResPack);
        File.WriteAllText(Path.Combine(emptyResPack, "theme.yaml"), """
            Id: empty-res
            Name: Empty Res
            Version: 1.0.0
            ThemeApiVersion: 2.10.0
            Resources: []
            """);
        File.WriteAllText(Path.Combine(emptyResPack, "tokens.yaml"), "Colors:\n  accent: \"#FF0000\"\n");

        var emptyResult = ThemePackParser.LoadFromDirectory(emptyResPack, Probe, tempRoot);
        Assert.True(emptyResult.IsValid);
        Assert.Empty(emptyResult.Document!.Colors);

        var darkOverridePack = Path.Combine(tempRoot, "dark-override");
        Directory.CreateDirectory(darkOverridePack);
        File.WriteAllText(Path.Combine(darkOverridePack, "theme.yaml"), """
            Id: dark-override
            Name: Dark Override
            Version: 1.0.0
            ThemeApiVersion: 2.10.0
            BaseVariant: Light
            Resources:
              - tokens.yaml
            """);
        File.WriteAllText(Path.Combine(darkOverridePack, "tokens.yaml"), "BaseVariant: Dark\nColors:\n  accent: \"#00AAFF\"\n");

        var darkResult = ThemePackParser.LoadFromDirectory(darkOverridePack, Probe, tempRoot);
        Assert.True(darkResult.IsValid);
        Assert.Equal(ThemeBaseVariant.Dark, darkResult.Document!.Descriptor.BaseVariant);
    }

    [Fact]
    public void Catalog_discovers_plugin_json_theme_packages_without_theme_yaml()
    {
        var pluginThemeDir = Path.Combine(tempRoot, "plugins", "sunset-theme");
        Directory.CreateDirectory(pluginThemeDir);
        File.WriteAllText(Path.Combine(pluginThemeDir, "theme.axaml"),
            "<ResourceDictionary xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><Color x:Key=\"PlayniteAccentColor\">#FFFF5500</Color></ResourceDictionary>");
        var descriptor = new PluginDescriptor(
            "sunset-theme",
            Guid.Empty,
            "Sunset Theme",
            new Version(1, 0, 0),
            PluginLoadKind.ExternalAssembly,
            PluginSurface.Theme,
            PluginCapabilityKind.Theme,
            new PluginEntryPoints(Theme: "theme.axaml"),
            RootDirectory: pluginThemeDir,
            Description: "Plugin-packaged theme");

        var discovery = ThemePackCatalog.Discover([], Probe, themePlugins: [descriptor]);
        var entry = Assert.Single(discovery.Entries, e => e.Id == "sunset-theme");
        Assert.True(entry.IsValid);
        var loaded = ThemePackParser.LoadFromPluginDescriptor(descriptor, Probe);
        Assert.True(loaded.IsValid);
        Assert.Equal(new ThemeRgbaColor(0xFF, 0xFF, 0x55, 0x00), loaded.Document!.Colors["PlayniteAccentColor"]);
    }

    private static string Mutate(string seed, string alphabet, Random rng)
    {
        var sb = new StringBuilder(seed);
        var edits = 1 + rng.Next(4);
        for (var i = 0; i < edits; i++)
        {
            var op = rng.Next(3);
            if (op == 0 && sb.Length > 0)
            {
                sb.Remove(rng.Next(sb.Length), 1);
            }
            else if (op == 1)
            {
                sb.Insert(rng.Next(sb.Length + 1), alphabet[rng.Next(alphabet.Length)]);
            }
            else if (sb.Length > 0)
            {
                sb[rng.Next(sb.Length)] = alphabet[rng.Next(alphabet.Length)];
            }
        }

        return sb.ToString();
    }

    private static void CreateFifo(string path)
    {
        var start = new ProcessStartInfo("mkfifo")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(path);
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
