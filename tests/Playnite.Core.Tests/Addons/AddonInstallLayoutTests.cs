using Playnite.Core.Addons;
using Xunit;

namespace Playnite.Core.Tests.Addons;

public sealed class AddonInstallLayoutTests
{
    private static string Root =>
        Path.Combine(Path.GetTempPath(), "playnite-p6-010", "layout-root");

    [Theory]
    [InlineData("1.0/Plugin.dll", true)]
    [InlineData("1.0/../1.0/Plugin.dll", true)]
    [InlineData("../escape/Plugin.dll", false)]
    [InlineData("1.0/../../escape/Plugin.dll", false)]
    public void Containment_check_accepts_only_a_path_below_the_normalized_root(
        string relative,
        bool expected)
    {
        var candidate = Path.GetFullPath(Path.Combine(Root, relative));

        Assert.Equal(expected, AddonInstallLayout.IsWithin(Root, candidate));
    }

    [Fact]
    public void Containment_check_rejects_a_sibling_that_shares_the_root_prefix()
    {
        var sibling = Root + "abc";

        Assert.False(AddonInstallLayout.IsWithin(Root, sibling));
        Assert.False(AddonInstallLayout.IsWithin(
            Root,
            Path.Combine(sibling, "Plugin.dll")));
    }

    [Fact]
    public void Containment_check_rejects_an_absolute_path_outside_the_root()
    {
        Assert.False(AddonInstallLayout.IsWithin(Root, Path.GetFullPath("/etc/passwd")));
    }

    [Fact]
    public void Containment_check_accepts_a_child_of_a_root_with_a_trailing_separator()
    {
        Assert.True(AddonInstallLayout.IsWithin(
            Root + Path.DirectorySeparatorChar,
            Path.Combine(Root, "1.0", "Plugin.dll")));
    }
}
