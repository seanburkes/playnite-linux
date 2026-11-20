using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using Xunit;

namespace Playnite.Plugin.Tests;

/// <summary>
/// Load evidence for the p6-009 samples: a fake host discovers each sample through the SDK-owned
/// <see cref="IPlayniteExtension"/> contract, instantiates it, and invokes its extension points
/// with concrete assertions.
/// </summary>
public sealed class SamplePluginLoadTests
{
    private static FakePluginHost Host() =>
        FakePluginHost.Load(SampleAssemblies.Names.Select(SampleAssemblies.PathFor).ToArray());

    [Fact]
    public void Fake_host_discovers_every_sample_through_the_sdk_contract()
    {
        Assert.Equal("Playnite.SDK", typeof(IPlayniteExtension).Assembly.GetName().Name);

        var host = Host();
        Assert.Equal(SampleAssemblies.Names.Count, host.Extensions.Count);
        Assert.Equal(
            [
                "Hello World",
                "Sample Library",
                "Sample Metadata",
            ],
            host.Extensions.Select(extension => extension.Name).Order(StringComparer.Ordinal).ToArray());

        Assert.IsAssignableFrom<GenericPlugin>(host.Find<GenericPlugin>());
        Assert.IsAssignableFrom<LibraryPlugin>(host.Find<LibraryPlugin>());
        Assert.IsAssignableFrom<MetadataPlugin>(host.Find<MetadataPlugin>());
        Assert.All(host.Extensions, extension => Assert.NotEqual(Guid.Empty, extension.Id));
        Assert.Equal(host.Extensions.Count, host.Extensions.Select(extension => extension.Id).Distinct().Count());
    }

    [Fact]
    public void Hello_world_start_extension_point_is_invoked()
    {
        var host = Host();
        var started = host.Start();

        Assert.Equal(3, started.Count);
        var hello = host.Extensions.Single(extension => extension.Name == "Hello World");
        var counter = hello.GetType().GetProperty("StartedCount", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(counter);
        Assert.Equal(1, counter.GetValue(hello));
    }

    [Fact]
    public void Library_provider_returns_concrete_games()
    {
        var host = Host();
        var library = host.Find<LibraryPlugin>();

        var games = library.GetGames(new LibraryGetGamesArgs(CancellationToken.None)).ToArray();

        Assert.Equal(2, games.Length);
        Assert.Equal(["Sample Quest", "Sample Rally"], games.Select(game => game.Name).ToArray());
        Assert.Equal(["sample-quest", "sample-rally"], games.Select(game => game.GameId).ToArray());
        Assert.All(games, game => Assert.Equal(["Sample Studio"], game.Developers));
        Assert.Equal(
            [new DateOnly(2024, 3, 14), new DateOnly(2025, 7, 1)],
            games.Select(game => game.ReleaseDate).ToArray());
    }

    [Fact]
    public void Library_provider_honours_cancellation_before_the_first_result()
    {
        var host = Host();
        var library = host.Find<LibraryPlugin>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var args = new LibraryGetGamesArgs(cancellation.Token);

        Assert.Throws<OperationCanceledException>(() => library.GetGames(args).ToArray());
    }

    [Fact]
    public void Metadata_provider_returns_the_requested_fields()
    {
        var host = Host();
        var metadata = host.Find<MetadataPlugin>();
        var game = new FakeGameView(Guid.Parse("11111111-2222-3333-4444-555555555555"), "Sample Quest");

        var result = metadata.GetMetadata(new GetMetadataArgs(game, metadata.SupportedFields));

        Assert.Equal("Sample Quest", result.Name);
        Assert.Equal("Sample metadata for Sample Quest.", result.Description);
        Assert.Equal(["Sample Metadata Studio"], result.Developers);
        Assert.Equal(["Sample"], result.Genres);
        Assert.Equal(new DateOnly(2026, 1, 2), result.ReleaseDate);
    }

    [Fact]
    public void Metadata_provider_returns_only_the_requested_fields()
    {
        var host = Host();
        var metadata = host.Find<MetadataPlugin>();
        var game = new FakeGameView(Guid.NewGuid(), "Sample Rally");

        var result = metadata.GetMetadata(new GetMetadataArgs(game, [MetadataField.Description]));

        Assert.Null(result.Name);
        Assert.Equal("Sample metadata for Sample Rally.", result.Description);
        Assert.Null(result.Developers);
        Assert.Null(result.ReleaseDate);
    }

    private sealed class FakeGameView(Guid id, string name) : IGameView
    {
        public Guid Id { get; } = id;

        public string Name { get; } = name;
    }
}
