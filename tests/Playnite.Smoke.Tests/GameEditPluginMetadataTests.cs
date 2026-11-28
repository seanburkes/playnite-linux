using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Core.Plugins;
using Playnite.Desktop.Shell;
using Xunit;

namespace Playnite.Smoke.Tests;

[Collection("HeadlessScreens")]
public sealed class GameEditPluginMetadataTests
{
    [Fact]
    public async Task Search_previews_plugin_results_and_only_selected_fields_are_saved()
    {
        var token = TestContext.Current.CancellationToken;
        var folder = Directory.CreateTempSubdirectory("playnite-game-edit-metadata-");
        try
        {
            var path = Path.Combine(folder.FullName, "library.sqlite");
            var database = await LibraryDatabase.OpenAsync(path, token);
            var id = Guid.NewGuid();
            await database.WriteAsync(async session =>
            {
                await session.Games.AddAsync(new StoredGame(new Game(id, "store-id", "Original")
                {
                    Description = "Keep this description",
                    UserScore = 30,
                }));
                return true;
            }, token);

            using var host = new PluginHost(PluginSurface.Desktop);
            var provider = new TestProvider();
            Assert.True(host.RegisterBuiltIn(provider));
            var store = await GameEditStore.OpenAsync(path, token);
            var model = new GameEditViewModel(store, host);
            await model.LoadAsync(id, token);

            await using var ui = HeadlessUnitTestSession.StartNew(typeof(HeadlessScreenApplication));
            await ui.Dispatch(() =>
            {
                var view = new GameEditView();
                view.Apply(model);
                var search = view.FindControl<Button>("SearchMetadataButton")!;
                var delayed = new TaskCompletionSource<PluginGameMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);
                provider.PendingResult = delayed.Task;
                view.SetTextForSmoke("NameBox", "Old search name");
                search.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var oldQueryToken = provider.QueryToken;
                Assert.False(oldQueryToken.IsCancellationRequested);

                view.SetTextForSmoke("NameBox", "Search name");
                Assert.True(oldQueryToken.IsCancellationRequested);
                Assert.True(search.IsEnabled);
                delayed.SetResult(new PluginGameMetadata(provider.ProviderId, "Old summary", 10));
                provider.PendingResult = null;
                search.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal("Search name", provider.Query?.Name);
                Assert.Equal("store-id", provider.Query?.GameId);
                Assert.Contains("Provider summary", view.TextOf("MetadataPreview"));
                Assert.Contains("region: EU", view.TextOf("MetadataPreview"));
                Assert.Contains("Community score: 81", view.TextOf("MetadataPreview"));
                Assert.DoesNotContain("Community score=81", view.TextOf("MetadataText"));

                view.SetCheckForSmoke("ImportScore", true);
                view.FindControl<Button>("ImportMetadataButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("Community score=81", view.TextOf("MetadataText"));
                Assert.Equal("Keep this description", view.InputTextOf("DescriptionBox"));
                view.SetTextForSmoke("NameBox", "Edited name");
                Assert.False(view.IsVisibleOf("MetadataResults"));
                Assert.False(view.IsVisibleOf("ImportMetadataButton"));
                view.ClickSaveForSmoke();
            }, token);

            Assert.Null((await store.GetAsync(id, token))!.Value.CommunityScore);
            Assert.True(await model.SaveAsync(token));
            var saved = (await store.GetAsync(id, token))!.Value;
            Assert.Equal(81, saved.CommunityScore);
            Assert.Equal(30, saved.UserScore);
            Assert.Equal("Keep this description", saved.Description);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private sealed class TestProvider : IPlaynitePlugin, IMetadataCapability
    {
        public PluginDescriptor Descriptor { get; } = new(
            "test.editor-metadata", Guid.Empty, "Editor metadata test", new Version(1, 0),
            PluginLoadKind.BuiltIn, PluginSurface.Desktop, PluginCapabilityKind.Metadata, new PluginEntryPoints());

        public PluginGameSnapshot? Query { get; private set; }
        public CancellationToken QueryToken { get; private set; }
        public Task<PluginGameMetadata?>? PendingResult { get; set; }
        public string ProviderId => Descriptor.Id;
        public string DisplayName => Descriptor.Name;

        public PluginRegistration Setup(PluginContext context) => new() { MetadataProviders = [this] };

        public Task<PluginGameMetadata?> FetchMetadataAsync(PluginGameSnapshot game, CancellationToken cancellationToken)
        {
            Query = game;
            QueryToken = cancellationToken;
            return PendingResult ?? Task.FromResult<PluginGameMetadata?>(new(
                ProviderId, "Provider summary", 81, new Dictionary<string, string> { ["region"] = "EU" }));
        }
    }
}
