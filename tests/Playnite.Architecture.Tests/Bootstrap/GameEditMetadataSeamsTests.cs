using Playnite.Core.Database;
using Playnite.Core.Models;
using Playnite.Desktop.Shell;
using Xunit;

namespace Playnite.Desktop.Tests;

/// <summary>
/// p7-005 game edit/metadata seams over the real XDG-library write path and the moved host.
/// Every test uses a fresh temporary library; the frozen fixture is never read here.
/// </summary>
public sealed class GameEditMetadataSeamsTests
{
    private static readonly Guid GameId = Guid.Parse("87654321-abcd-4abc-9def-123456789abc");
    private static readonly Guid TagId = Guid.Parse("12345678-abcd-4abc-9def-123456789abc");
    private const string Legacy = "{ \"future\": {\"n\":1} }";

    [Fact]
    public void A_new_edit_model_has_no_game_until_a_row_is_loaded()
    {
        using var temp = new TemporaryLibrary();
        var store = GameEditStore.OpenAsync(temp.LibraryPath).GetAwaiter().GetResult();
        var model = new GameEditViewModel(store);

        Assert.False(model.IsLoaded);
        Assert.Equal("empty", model.Status);
        Assert.Equal(string.Empty, model.Name);
        Assert.Equal("(none)", model.MetadataSummary);
    }

    [Fact]
    public async Task Loading_a_selected_game_populates_bounded_fields_and_read_only_metadata()
    {
        using var temp = new TemporaryLibrary();
        await SeedAsync(temp.LibraryPath);
        var store = await GameEditStore.OpenAsync(temp.LibraryPath);
        var model = new GameEditViewModel(store);

        await model.LoadAsync(GameId);

        Assert.True(model.IsLoaded);
        Assert.Equal("loaded", model.Status);
        Assert.Equal(GameId, model.GameId);
        Assert.Equal("game-001", model.GameIdentity);
        Assert.Equal("Alpha Game", model.Name);
        Assert.Equal("original description", model.Description);
        Assert.Equal("1.0", model.Version);
        Assert.Equal("/games/alpha", model.InstallDirectory);
        Assert.True(model.IsInstalled);
        Assert.False(model.Hidden);
        Assert.False(model.Favorite);
        Assert.Equal(55, model.UserScore);
        Assert.Equal("icon-original", model.Icon);
        Assert.Equal("Release=2020; Platforms=0; Tags=1; Developers=0", model.MetadataSummary);
    }

    [Fact]
    public async Task Saving_preserves_untouched_fields_relations_and_legacy_json()
    {
        using var temp = new TemporaryLibrary();
        await SeedAsync(temp.LibraryPath);
        var store = await GameEditStore.OpenAsync(temp.LibraryPath);
        var model = new GameEditViewModel(store);
        await model.LoadAsync(GameId);

        model.Name = "  Edited Name  ";
        model.Description = "edited description";
        model.UserScore = 77;
        model.Favorite = true;
        model.Icon = "icon-raw-reference";
        model.CoverImage = "cover-raw-reference";
        model.BackgroundImage = "background-raw-reference";

        Assert.True(await model.SaveAsync());
        Assert.Equal("saved", model.Status);

        var stored = await store.GetAsync(GameId);
        Assert.NotNull(stored);
        Assert.Equal("Edited Name", stored!.Value.Name);
        Assert.Equal("edited description", stored.Value.Description);
        Assert.Equal(77, stored.Value.UserScore);
        Assert.True(stored.Value.Favorite);
        Assert.Equal("icon-raw-reference", stored.Value.Icon);
        Assert.Equal("cover-raw-reference", stored.Value.CoverImage);
        Assert.Equal("background-raw-reference", stored.Value.BackgroundImage);

        // Untouched fields, every relation and the original legacy JSON survive the full-graph write.
        Assert.Equal("1.0", stored.Value.Version);
        Assert.Equal("untouched notes", stored.Value.Notes);
        Assert.Equal("Alpha Sorting", stored.Value.SortingName);
        Assert.Equal("/games/alpha", stored.Value.InstallDirectory);
        Assert.True(stored.Value.IsInstalled);
        Assert.False(stored.Value.Hidden);
        Assert.Equal(Legacy, stored.LegacyJson);
        Assert.Equal(new[] { TagId }, stored.Value.TagIds);
    }

    [Fact]
    public async Task Saving_a_blank_name_is_rejected_without_a_write()
    {
        using var temp = new TemporaryLibrary();
        await SeedAsync(temp.LibraryPath);
        var store = await GameEditStore.OpenAsync(temp.LibraryPath);
        var model = new GameEditViewModel(store);
        await model.LoadAsync(GameId);

        model.Name = "   ";
        Assert.False(await model.SaveAsync());
        Assert.Equal("invalid-name", model.Status);

        var stored = await store.GetAsync(GameId);
        Assert.Equal("Alpha Game", stored!.Value.Name);
        Assert.Equal("original description", stored.Value.Description);
    }

    [Fact]
    public async Task Cancellation_is_observed_without_a_partial_write()
    {
        using var temp = new TemporaryLibrary();
        await SeedAsync(temp.LibraryPath);
        var store = await GameEditStore.OpenAsync(temp.LibraryPath);
        var model = new GameEditViewModel(store);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.LoadAsync(GameId, cancelled.Token));
        await model.LoadAsync(GameId);
        model.Name = "must not be written";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.SaveAsync(cancelled.Token));

        var stored = await store.GetAsync(GameId);
        Assert.Equal("Alpha Game", stored!.Value.Name);
    }

    [Fact]
    public async Task Clear_discards_the_draft_without_touching_the_database()
    {
        using var temp = new TemporaryLibrary();
        await SeedAsync(temp.LibraryPath);
        var store = await GameEditStore.OpenAsync(temp.LibraryPath);
        var model = new GameEditViewModel(store);
        await model.LoadAsync(GameId);
        model.Name = "discarded";

        model.Clear();

        Assert.False(model.IsLoaded);
        Assert.Equal("empty", model.Status);
        var stored = await store.GetAsync(GameId);
        Assert.Equal("Alpha Game", stored!.Value.Name);
    }

    [Fact]
    public void The_moved_host_keeps_the_shell_namespace_class_and_all_p7_004_control_names()
    {
        Assert.Equal("Playnite.Desktop.Shell", typeof(ContentHostView).Namespace);
        Assert.Equal("Playnite.Desktop.Shell", typeof(GameEditView).Namespace);
        Assert.Equal("Playnite.Desktop.Shell.ContentHostView", typeof(ContentHostView).FullName);

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Playnite.slnx")))
        {
            root = root.Parent;
        }

        var basePath = root?.FullName ?? throw new InvalidOperationException("Cannot locate Playnite.slnx");
        var host = File.ReadAllText(Path.Combine(
            basePath, "src", "Playnite.Desktop", "p7-005", "Shell", "ContentHostView.axaml"));
        Assert.Contains("x:Class=\"Playnite.Desktop.Shell.ContentHostView\"", host, StringComparison.Ordinal);
        foreach (var name in new[] { "LibraryGrid", "GameEdit" })
        {
            Assert.Contains("x:Name=\"" + name + "\"", host, StringComparison.Ordinal);
        }

        // The approved move is only the host: the p7-004 grid guard path stays exactly where it was.
        Assert.True(File.Exists(Path.Combine(
            basePath, "src", "Playnite.Desktop", "p7-004", "Shell", "LibraryGridView.axaml")));
    }

    [Fact]
    public void The_edit_model_has_a_single_instance_constructor_without_a_service_locator()
    {
        var constructor = Assert.Single(typeof(GameEditViewModel).GetConstructors());
        var parameter = Assert.Single(constructor.GetParameters());
        Assert.Equal(typeof(GameEditStore), parameter.ParameterType);
        Assert.Null(typeof(GameEditViewModel).GetProperty("ServiceProvider"));
    }

    private static async Task SeedAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var database = await LibraryDatabase.OpenAsync(path);
        var game = new Game(GameId, "game-001", "Alpha Game")
        {
            Description = "original description",
            Version = "1.0",
            InstallDirectory = "/games/alpha",
            IsInstalled = true,
            Hidden = false,
            Favorite = false,
            UserScore = 55,
            Icon = "icon-original",
            CoverImage = "cover-original",
            BackgroundImage = "background-original",
            Notes = "untouched notes",
            SortingName = "Alpha Sorting",
            ReleaseDate = new ReleaseDate(2020),
            TagIds = [TagId],
        };
        await database.WriteAsync(async session =>
        {
            await session.Metadata<Tag>().AddAsync(new StoredMetadata<Tag>(new Tag(TagId, "Tag One")));
            await session.Games.AddAsync(new StoredGame(game, Legacy));
            return 0;
        });
    }

    private sealed class TemporaryLibrary : IDisposable
    {
        internal TemporaryLibrary()
        {
            Root = Path.Combine(Path.GetTempPath(), "playnite-p7-005-" + Path.GetRandomFileName());
            LibraryPath = Path.Combine(Root, "data", "playnite", "library.sqlite");
            Directory.CreateDirectory(Path.GetDirectoryName(LibraryPath)!);
        }

        internal string Root { get; }
        internal string LibraryPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
