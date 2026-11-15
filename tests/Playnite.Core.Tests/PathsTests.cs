using System.Text.Json;
using Playnite.Core.Tests.TestDoubles;
using Xunit;

namespace Playnite.Core.Tests;

public sealed class PathsTests
{
    [Fact]
    public void LegacyDerivedHelpersMatchGolden()
    {
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "helpers.json")));
        var fileSystem = new MemoryFileSystem();
        foreach (var item in golden.RootElement.GetProperty("cases").EnumerateArray())
        {
            var inputs = item.GetProperty("inputs").EnumerateArray().Select(value => value.GetString()!).ToArray();
            object? actual = item.GetProperty("helper").GetString() switch
            {
                "FixSeparators" => Paths.FixSeparators(inputs[0]),
                "Normalize" => Paths.Normalize(inputs[0], "/working"),
                "Combine" => Paths.Combine(inputs),
                "AreEqual" => Paths.AreEqual(inputs[0], inputs[1], "/working"),
                "IsFullPath" => Paths.IsFullPath(inputs[0]),
                "ContainsInvalidPathChars" => Paths.ContainsInvalidPathChars(inputs[0]),
                "GetSafePathName" => Paths.GetSafePathName(inputs[0]),
                "GetCommonDirectory" => Paths.GetCommonDirectory(inputs),
                "IsValidFilePath" => Paths.IsValidFilePath(inputs[0], fileSystem),
                _ => throw new InvalidDataException("Unknown helper in golden.")
            };
            var expected = item.GetProperty("expected");
            Assert.Equal(expected.ValueKind == JsonValueKind.String ? expected.GetString() : (object)expected.GetBoolean(), actual);
        }
    }

    [Fact]
    public void NullPathsKeepLegacyAbsenceSemantics()
    {
        Assert.Null(Paths.FixSeparators(null));
        Assert.False(Paths.AreEqual(null, null, "/working"));
        Assert.False(Paths.AreEqual(null, "/a", "/working"));
        Assert.False(Paths.AreEqual("/a", null, "/working"));
        Assert.False(Paths.IsFullPath(null));
        Assert.False(Paths.IsValidFilePath(null, new MemoryFileSystem()));
    }

    [Fact]
    public void NormalizationIsIdempotentAndPreservesRootForGeneratedPaths()
    {
        foreach (var name in new[] { "a", "space name", "これはユニコード", "back\\slash", ".hidden" })
        {
            foreach (var suffix in new[] { "", "/", "//", "/./", "/child/..", "/../" + name })
            {
                var normalized = Paths.Normalize(Paths.Combine("/root", name) + suffix, "/working");
                Assert.Equal(normalized, Paths.Normalize(normalized, "/working"));
                Assert.True(Paths.IsFullPath(normalized));
                Assert.True(Paths.AreEqual(normalized, normalized + "/.", "/working"));
            }
        }

        Assert.Equal("/", Paths.Normalize("/..//", "/working"));
        Assert.False(Paths.AreEqual("/Case", "/case", "/working"));
        Assert.False(Paths.AreEqual("bad\0path", "/path", "/working"));
    }
}
