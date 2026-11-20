using Playnite.Core.Addons;
using Playnite.Core.Manifests;
using Xunit;

namespace Playnite.Core.Tests.Addons;

public sealed class AddonManifestValidationTests
{
    [Theory]
    [InlineData("TestPlugin.extension.yaml", "Test_Generic_Plugin", "TestPlugin.dll", ExtensionType.GenericPlugin)]
    [InlineData("TestGameLibrary.extension.yaml", "Test_GameLibrary", "TestGameLibrary.dll", ExtensionType.GameLibrary)]
    public void Real_v10_extension_goldens_validate_without_diagnostics(
        string file,
        string expectedId,
        string expectedModule,
        ExtensionType expectedType)
    {
        var result = AddonManifestValidator.ValidateExtension(Golden(file), file);

        Assert.True(result.IsValid, Describe(result.Diagnostics));
        Assert.Equal(expectedId, result.Value!.Id);
        Assert.Equal("1.0", result.Value.Version);
        Assert.Equal(expectedModule, result.Value.Module);
        Assert.Equal(expectedType, result.Value.Type);
    }

    [Theory]
    [InlineData("generic.yaml", ExtensionType.GenericPlugin)]
    [InlineData("library.yaml", ExtensionType.GameLibrary)]
    [InlineData("metadata.yaml", ExtensionType.MetadataProvider)]
    public void Real_v10_extension_fixtures_validate_without_diagnostics(string file, ExtensionType type)
    {
        var result = AddonManifestValidator.ValidateExtension(Fixture(file), file);

        Assert.True(result.IsValid, Describe(result.Diagnostics));
        Assert.Equal(type, result.Value!.Type);
    }

    [Theory]
    [InlineData("desktop.yaml", ApplicationMode.Desktop)]
    [InlineData("fullscreen.yaml", ApplicationMode.Fullscreen)]
    public void Real_v10_theme_fixtures_validate_without_diagnostics(string file, ApplicationMode mode)
    {
        var result = AddonManifestValidator.ValidateTheme(Fixture(file), file);

        Assert.True(result.IsValid, Describe(result.Diagnostics));
        Assert.Equal(mode, result.Value!.Mode);
    }

    [Fact]
    public void Real_v10_catalogue_metadata_validates_without_diagnostics()
    {
        var result = AddonManifestValidator.ValidateAddon(Fixture("addon.json"), "addon.json");

        Assert.True(result.IsValid, Describe(result.Diagnostics));
        Assert.Equal("test-addon", result.Value!.AddonId);
        Assert.Equal(AddonType.GameLibrary, result.Value.Type);
    }

    [Theory]
    [InlineData("Id: [broken", "$")]
    [InlineData("Version: 1.0\nModule: x\nType: GenericPlugin", "$.Id")]
    [InlineData("Id: x\nVersion: nope\nModule: x\nType: GenericPlugin", "$.Version")]
    [InlineData("Id: x\nVersion: 1.0\nType: GenericPlugin", "$.Module")]
    [InlineData("Id: x\nVersion: 1.0\nModule: x\nType: NotAType", "$")]
    [InlineData("Id: x\nVersion: 1.0\nModule: x\nType: null", "$.Type")]
    [InlineData("Id: x\nVersion: 1.0\nModule: [x]\nType: GenericPlugin", "$")]
    [InlineData("Id: x\nId: y\nVersion: 1.0\nModule: x\nType: GenericPlugin", "$")]
    [InlineData("Id: x\nVersion: 1.0\nModule: x\nType: GenericPlugin\nLinks: [null]", "$.Links[0]")]
    public void Malformed_extension_documents_report_the_offending_member(string document, string expectedPath)
    {
        var result = AddonManifestValidator.ValidateExtension(document, "hostile.yaml");

        Assert.False(result.IsValid);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(AddonManifestValidator.InvalidManifestCode, diagnostic.Code);
        Assert.Equal(expectedPath, diagnostic.Path);
        Assert.Contains("hostile.yaml", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_type_uses_the_v10_default_generic_plugin()
    {
        var result = AddonManifestValidator.ValidateExtension(
            "Id: x\nVersion: 1.0\nModule: x",
            "extension.yaml");

        Assert.True(result.IsValid, Describe(result.Diagnostics));
        Assert.Equal(ExtensionType.GenericPlugin, result.Value!.Type);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("/tmp/evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("C:\\evil")]
    public void Add_on_ids_that_are_not_one_safe_segment_are_rejected(string id)
    {
        var result = AddonManifestValidator.ValidateExtension(ExtensionYaml(id, "Plugin.dll"), "hostile.yaml");

        Assert.False(result.IsValid);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(AddonManifestValidator.UnsafeIdCode, diagnostic.Code);
        Assert.Equal("$.Id", diagnostic.Path);
        Assert.Contains(id, diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../../evil.dll")]
    [InlineData("/usr/lib/evil.dll")]
    [InlineData("lib/../../evil.dll")]
    [InlineData("lib\\evil.dll")]
    public void Modules_that_escape_the_install_directory_are_rejected(string module)
    {
        var result = AddonManifestValidator.ValidateExtension(
            ExtensionYaml("Test_Plugin", module),
            "hostile.yaml");

        Assert.False(result.IsValid);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(AddonManifestValidator.UnsafeModuleCode, diagnostic.Code);
        Assert.Equal("$.Module", diagnostic.Path);
    }

    [Theory]
    [InlineData("../icon.png")]
    [InlineData("/tmp/icon.png")]
    public void Icons_that_escape_the_install_directory_are_rejected(string icon)
    {
        var result = AddonManifestValidator.ValidateExtension(
            ExtensionYaml("Test_Plugin", "Plugin.dll", icon: icon),
            "hostile.yaml");

        Assert.False(result.IsValid);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(AddonManifestValidator.UnsafeIconCode, diagnostic.Code);
        Assert.Equal("$.Icon", diagnostic.Path);
    }

    [Fact]
    public void Nested_module_and_icon_paths_inside_the_add_on_directory_are_accepted()
    {
        var result = AddonManifestValidator.ValidateExtension(
            ExtensionYaml("Test_Plugin", "lib/Plugin.dll", icon: "images/icon.png"),
            "valid.yaml");

        Assert.True(result.IsValid, Describe(result.Diagnostics));
    }

    private static string ExtensionYaml(string id, string module, string? icon = null)
    {
        var iconLine = icon is null ? string.Empty : $"Icon: '{icon}'\n";
        return $"Id: '{id}'\nName: Test\nAuthor: Test\nVersion: 1.0\nModule: '{module}'\n{iconLine}Type: GenericPlugin\n";
    }

    private static string Golden(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden-manifests", name));

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifests-v10", name));

    private static string Describe(IEnumerable<AddonDiagnostic> diagnostics) =>
        string.Join(" | ", diagnostics);
}
