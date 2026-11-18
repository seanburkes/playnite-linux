using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Playnite.Core;
using Playnite.Core.Tests.Serialization;
using Playnite.Core.Tests.TestDoubles;
using Playnite.Fuzz;
using Playnite.Platform.Linux;
using Playnite.Steam;
using Xunit;

namespace Playnite.Core.Tests.Steam;

/// <summary>
/// Synthetic-only discovery tests. Every tree is materialised under one temporary directory;
/// no test reads, changes or needs the developer's Steam install.
/// </summary>
public sealed class SteamDiscoveryTests : IDisposable
{
    private static string Corpus => RepositoryPaths.Combine("tools", "Playnite.Fuzz", "Corpus", "p5-002");
    private readonly string home = Directory.CreateTempSubdirectory("playnite-steam-discovery-").FullName;
    private readonly MemoryEnvironment environment;
    private readonly SteamFileSystem fileSystem = new(new LinuxBoundedFileReader());
    private readonly ITestOutputHelper output;

    public SteamDiscoveryTests(ITestOutputHelper output)
    {
        this.output = output;
        environment = new MemoryEnvironment { Home = home };
    }

    public void Dispose()
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(home))
        {
            var info = new FileInfo(entry);
            if (info.LinkTarget is not null)
            {
                File.Delete(entry);
            }
            else if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }

        Directory.Delete(home, recursive: true);
    }

    // ---- root resolution ------------------------------------------------------------------

    [Fact]
    public void Explicit_override_wins_and_every_candidate_is_reported_in_spec_order()
    {
        var overridden = CreateRoot(Path.Combine(home, "override"));
        CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        environment.Variables["STEAM_ROOT"] = overridden;

        var result = ResolveRoots();

        Assert.Equal(
            [SteamRootKind.Override, SteamRootKind.HomeSteam, SteamRootKind.HomeSteamRoot, SteamRootKind.HomeLocalShare, SteamRootKind.Flatpak],
            result.Candidates.Select(c => c.Kind));
        Assert.Equal(RealPath(overridden), result.Root);
        Assert.Equal(
            [SteamRootState.Accepted, SteamRootState.Missing, SteamRootState.Missing, SteamRootState.Shadowed, SteamRootState.Missing],
            result.Candidates.Select(c => c.State));
    }

    [Fact]
    public void Symlinked_user_candidates_resolve_to_one_root_and_the_duplicate_is_reported()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        Directory.CreateDirectory(Path.Combine(home, ".steam"));
        Directory.CreateSymbolicLink(Path.Combine(home, ".steam", "steam"), Path.Combine("..", ".local", "share", "Steam"));

        var result = ResolveRoots();

        Assert.Equal(RealPath(root), result.Root);
        Assert.Equal(SteamRootState.Accepted, result.Candidates[0].State);
        Assert.Equal(SteamRootState.DuplicateRealPath, result.Candidates[2].State);
    }

    [Fact]
    public void Home_steam_root_alias_is_probed_as_its_own_candidate()
    {
        var root = CreateRoot(Path.Combine(home, ".steam", "root"));

        var result = ResolveRoots();

        Assert.Equal(RealPath(root), result.Root);
        Assert.Equal(SteamRootState.Accepted, result.Candidates[1].State);
        Assert.Equal(SteamRootKind.HomeSteamRoot, result.Candidates[1].Kind);
    }

    [Fact]
    public void Xdg_data_home_candidate_is_probed_when_it_is_not_the_default_location()
    {
        environment.Variables["XDG_DATA_HOME"] = Path.Combine(home, "xdg-data");
        var root = CreateRoot(Path.Combine(home, "xdg-data", "Steam"));

        var result = ResolveRoots();

        Assert.Equal(RealPath(root), result.Root);
        Assert.Equal(SteamRootKind.XdgData, result.Candidates[2].Kind);
        Assert.Equal(SteamRootState.Accepted, result.Candidates[2].State);
    }

    [Fact]
    public void Flatpak_root_is_used_when_it_is_the_only_install()
    {
        var flatpak = CreateRoot(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));

        var result = ResolveRoots();

        Assert.Equal(RealPath(flatpak), result.Root);
        Assert.Equal([SteamRootState.Missing, SteamRootState.Missing, SteamRootState.Missing, SteamRootState.Accepted],
            result.Candidates.Select(c => c.State));
        Assert.Equal(SteamRootKind.Flatpak, result.Candidates[^1].Kind);
    }

    [Fact]
    public void Directory_without_steamapps_is_reported_and_the_next_candidate_wins()
    {
        Directory.CreateDirectory(Path.Combine(home, ".steam", "steam"));
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));

        var result = ResolveRoots();

        Assert.Equal(SteamRootState.SteamAppsMissing, result.Candidates[0].State);
        Assert.Equal(RealPath(root), result.Root);
    }

    [Fact]
    public void Candidate_that_is_a_file_is_reported_not_a_directory()
    {
        Directory.CreateDirectory(Path.Combine(home, ".steam"));
        File.WriteAllText(Path.Combine(home, ".steam", "steam"), "synthetic");

        var result = ResolveRoots();

        Assert.Equal(SteamRootState.NotDirectory, result.Candidates[0].State);
        Assert.Null(result.Root);
    }

    [Fact]
    public void Root_whose_steamapps_escapes_through_a_link_is_rejected_without_following_it()
    {
        var root = Path.Combine(home, ".local", "share", "Steam");
        Directory.CreateDirectory(root);
        var outside = Path.Combine(home, "outside-steamapps");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "steamapps"), outside);

        var result = ResolveRoots();

        Assert.Null(result.Root);
        Assert.Equal(SteamRootState.SteamAppsOutsideRoot, result.Candidates[2].State);
    }

    [Fact]
    public void No_candidate_yields_a_null_root_without_throwing()
    {
        var discovery = Discover();

        Assert.Null(discovery.Root);
        Assert.Empty(discovery.Libraries);
        Assert.Null(discovery.LibraryFilePath);
        Assert.Empty(discovery.Issues);
        Assert.All(discovery.Roots.Candidates, candidate => Assert.Equal(SteamRootState.Missing, candidate.State));
    }

    [Fact]
    public void Relative_override_is_reported_invalid_through_the_production_paths_object()
    {
        environment.Variables["STEAM_ROOT"] = "relative/steam";
        CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var paths = Paths();

        // LinuxPaths deliberately omits a relative override from its own probe list; discovery
        // still receives the captured variable and must report it rather than lose it silently.
        Assert.DoesNotContain("relative/steam", paths.SteamRootCandidates);
        var result = SteamRootResolver.Discover(environment, paths, fileSystem);

        Assert.Equal(SteamRootKind.Override, result.Candidates[0].Kind);
        Assert.Equal("relative/steam", result.Candidates[0].Path);
        Assert.Equal(SteamRootState.InvalidPath, result.Candidates[0].State);
        Assert.Equal(SteamRootState.Accepted, result.Candidates[^2].State);
    }

    [Fact]
    public void Repeated_candidate_path_is_reported_once()
    {
        var root = CreateRoot(Path.Combine(home, "root"));
        var result = SteamRootResolver.Probe(
        [
            new SteamRootCandidate(SteamRootKind.HomeSteam, root),
            new SteamRootCandidate(SteamRootKind.HomeSteam, root),
        ], fileSystem);

        Assert.Equal(RealPath(root), result.Root);
        Assert.Equal([SteamRootState.Accepted, SteamRootState.DuplicateRealPath], result.Candidates.Select(c => c.State));
    }

    [Fact]
    public void Symlink_loop_is_reported_as_unreadable_and_never_throws()
    {
        Directory.CreateDirectory(Path.Combine(home, ".steam"));
        Directory.CreateSymbolicLink(Path.Combine(home, ".steam", "steam"), "steam");

        var result = ResolveRoots();

        Assert.Null(result.Root);
        Assert.Equal(SteamRootState.Unreadable, result.Candidates[0].State);
    }

    [Fact]
    public void Permission_denied_candidate_is_unreadable_not_missing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var locked = Path.Combine(home, "locked");
        Directory.CreateDirectory(locked);
        var candidate = Path.Combine(locked, "steam");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var result = SteamRootResolver.Probe([new SteamRootCandidate(SteamRootKind.HomeSteam, candidate)], fileSystem);

            Assert.Equal(SteamRootState.Unreadable, result.Candidates[0].State);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Realpath_resolves_chains_relative_links_parent_links_and_missing_paths()
    {
        var real = Path.Combine(home, "real");
        Directory.CreateDirectory(Path.Combine(real, "child"));
        Directory.CreateSymbolicLink(Path.Combine(home, "a"), "real");
        Directory.CreateSymbolicLink(Path.Combine(home, "b"), "a");

        Assert.Equal(RealPath(real), fileSystem.GetRealPath(Path.Combine(home, "b")));
        Assert.Equal(RealPath(Path.Combine(real, "child")), fileSystem.GetRealPath(Path.Combine(home, "b", "child")));
        Assert.Null(fileSystem.GetRealPath(Path.Combine(home, "missing")));
        Assert.Throws<ArgumentException>(() => fileSystem.GetRealPath("relative"));
    }

    [Fact]
    public void Realpath_resolves_dotdot_after_a_link_before_collapsing_it()
    {
        var directory = Path.Combine(home, "real", "dir");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(home, "real", "x"));
        Directory.CreateSymbolicLink(Path.Combine(home, "link"), Path.Combine("real", "dir"));

        Assert.Equal(RealPath(Path.Combine(home, "real", "x")), fileSystem.GetRealPath(Path.Combine(home, "link", "..", "x")));
        Assert.Equal(RealPath(Path.Combine(home, "real")), fileSystem.GetRealPath(Path.Combine(home, "link", "..")));
        Assert.Null(fileSystem.GetRealPath(Path.Combine(home, "link", "..", "missing")));
    }

    [Fact]
    public void Self_referential_steamapps_link_is_accepted_inside_the_root()
    {
        var root = Path.Combine(home, ".local", "share", "Steam");
        Directory.CreateDirectory(root);
        Directory.CreateSymbolicLink(Path.Combine(root, "steamapps"), ".");
        var second = CreateRoot(Path.Combine(home, "second"));
        WriteLibraries(Path.Combine(root, "steamapps"), "libraryfolders.modern.vdf",
            ("${ROOT}", root), ("${SECOND}", second));

        var result = Discover();

        Assert.Equal(RealPath(root), result.Root);
        Assert.Equal(["0", "1"], result.Libraries.Select(l => l.Index));
        Assert.Equal(SteamLibraryStatus.Available, result.Libraries.Single(l => l.Index == "0").Status);
    }

    // ---- library discovery ----------------------------------------------------------------

    [Theory]
    [InlineData("missing")]
    [InlineData("file")]
    [InlineData("dangling")]
    [InlineData("directory-link")]
    public void Raw_override_dotdot_requires_a_real_directory_through_production_paths(string component)
    {
        var root = CreateRoot(Path.Combine(home, "root"));
        var preceding = Path.Combine(home, "preceding");
        if (component == "file")
        {
            File.WriteAllText(preceding, "not a directory");
        }
        else if (component == "dangling")
        {
            Directory.CreateSymbolicLink(preceding, "missing-target");
        }
        else if (component == "directory-link")
        {
            Directory.CreateDirectory(Path.Combine(home, "directory"));
            Directory.CreateSymbolicLink(preceding, "directory");
        }

        var raw = home + "/preceding/../root";
        environment.Variables["STEAM_ROOT"] = raw;
        var result = SteamRootResolver.Discover(environment, new LinuxPaths(environment, new LinuxFileSystem()), fileSystem);

        Assert.Equal(raw, result.Candidates[0].Path);
        Assert.Equal(component == "directory-link" ? SteamRootState.Accepted : SteamRootState.Missing,
            result.Candidates[0].State);
        Assert.Equal(component == "directory-link" ? root : null, result.Root);
    }

    [Fact]
    public void Candidate_order_includes_xdg_and_reports_realpaths_even_when_not_accepted()
    {
        var bare = Path.Combine(home, "bare");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(Path.Combine(home, ".steam"));
        Directory.CreateSymbolicLink(Path.Combine(home, ".steam", "steam"), bare);
        environment.Variables["STEAM_ROOT"] = bare;
        environment.Variables["XDG_DATA_HOME"] = Path.Combine(home, "xdg");
        var result = ResolveRoots();

        Assert.Equal([SteamRootKind.Override, SteamRootKind.HomeSteam, SteamRootKind.HomeSteamRoot,
            SteamRootKind.XdgData, SteamRootKind.HomeLocalShare, SteamRootKind.Flatpak], result.Candidates.Select(c => c.Kind));
        Assert.Equal(SteamRootState.SteamAppsMissing, result.Candidates[0].State);
        Assert.Equal(bare, result.Candidates[0].RealPath);
        Assert.Equal(SteamRootState.DuplicateRealPath, result.Candidates[1].State);
        Assert.Equal(bare, result.Candidates[1].RealPath);
    }

    [Fact]
    public void Whitespace_override_is_reported_invalid_not_dropped()
    {
        environment.Variables["STEAM_ROOT"] = " \t ";
        var result = ResolveRoots();
        Assert.Equal(SteamRootKind.Override, result.Candidates[0].Kind);
        Assert.Equal(" \t ", result.Candidates[0].Path);
        Assert.Equal(SteamRootState.InvalidPath, result.Candidates[0].State);
    }

    [Fact]
    public void Zero_app_id_is_valid_input_to_the_discovery_fuzz_invariants()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("libraryfolders { 0 { path /synthetic apps { 0 1 } } }");
        var parsed = Assert.IsType<SteamLibraries>(SteamDiscoveryFuzzTarget.Parse(bytes, 1024));
        Assert.True(Assert.Single(parsed.Libraries).Apps?.ContainsKey(0));
    }

    [Theory]
    [InlineData("leaf")]
    [InlineData("parent")]
    [InlineData("fifo")]
    public void Canonical_path_swapped_after_containment_is_rejected_promptly(string swap)
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var file = WriteLibrariesRaw(Path.Combine(root, "steamapps"), "libraryfolders { }");
        var outside = CreateRoot(Path.Combine(home, "outside"));
        File.WriteAllText(Path.Combine(outside, "libraryfolders.vdf"), "libraryfolders { 9 /escaped }");
        var racing = new BeforeReadFileSystem(fileSystem, () =>
        {
            if (swap == "parent")
            {
                Directory.Move(Path.GetDirectoryName(file) ?? throw new InvalidOperationException(), Path.Combine(root, "saved"));
                Directory.CreateSymbolicLink(Path.Combine(root, "steamapps"), outside);
            }
            else
            {
                File.Delete(file);
                if (swap == "fifo")
                {
                    MakeFifo(file);
                }
                else
                {
                    File.CreateSymbolicLink(file, Path.Combine(outside, "libraryfolders.vdf"));
                }
            }
        });

        var work = Task.Run(() => SteamDiscovery.Discover(environment, Paths(), racing));
        Assert.True(work.Wait(TimeSpan.FromSeconds(5)), "swapped read blocked");
        var result = work.Result;
        Assert.Equal(SteamDiscovery.RootLibraryIndex, Assert.Single(result.Libraries).Index);
        Assert.Contains(result.Issues, issue => issue.Reason == (swap == "fifo" ? "not-a-regular-file" : "unreadable-library-file"));
    }

    [Fact]
    public void Fifo_replacement_after_handle_stat_never_reopens_the_path()
    {
        var file = Path.Combine(home, "stat-swap.vdf");
        var bytes = System.Text.Encoding.UTF8.GetBytes("libraryfolders { }");
        File.WriteAllBytes(file, bytes);
        var work = Task.Run(() => new LinuxBoundedFileReader().Read(file, 1024, () =>
        {
            File.Delete(file);
            MakeFifo(file);
        }));

        Assert.True(work.Wait(TimeSpan.FromSeconds(5)), "post-stat FIFO replacement blocked");
        Assert.Equal(Playnite.Platform.BoundedFileReadStatus.Ok, work.Result.Status);
        Assert.Equal(bytes, work.Result.Bytes);
    }

    [Fact]
    public void Library_file_path_is_recorded_when_file_exists_preflight_would_hide_it()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        Directory.CreateDirectory(file);
        Assert.False(fileSystem.FileExists(file));
        var result = Discover();
        Assert.Equal(file, result.LibraryFilePath);
        Assert.Contains(result.Issues, issue => issue.Reason == "not-a-regular-file");
    }

    [Fact]
    public void Modern_file_reports_every_library_apps_and_first_appid_wins()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var second = CreateRoot(Path.Combine(home, "second-library"));
        var file = WriteLibraries(Path.Combine(root, "steamapps"), "libraryfolders.modern.vdf",
            ("${ROOT}", root), ("${SECOND}", second));

        var result = Discover();

        Assert.Equal(RealPath(root), result.Root);
        Assert.Equal(file, result.LibraryFilePath);
        Assert.Equal(["0", "1"], result.Libraries.Select(l => l.Index));
        Assert.All(result.Libraries, library => Assert.Equal(SteamLibraryStatus.Available, library.Status));
        Assert.Equal(RealPath(root), result.Libraries[0].ResolvedPath);
        Assert.Equal(RealPath(second), result.Libraries[1].ResolvedPath);
        Assert.Equal([42u, 43u], Apps(result.Libraries[0]).Keys.Order());
        Assert.Equal([44u], Apps(result.Libraries[1]).Keys);
        Assert.Contains(result.Issues, issue => issue is { Identity: "1", Reason: "duplicate-appid", Field: "42" });
    }

    [Fact]
    public void Legacy_index_form_is_accepted()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var second = CreateRoot(Path.Combine(home, "second-library"));
        WriteLibraries(Path.Combine(root, "steamapps"), "libraryfolders.legacy.vdf",
            ("${ROOT}", root), ("${SECOND}", second));

        var result = Discover();

        Assert.Equal(2, result.Libraries.Count);
        Assert.All(result.Libraries, library => Assert.Equal(SteamLibraryStatus.Available, library.Status));
        Assert.Equal(RealPath(second), result.Libraries[1].ResolvedPath);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Absent_apps_stays_null_and_a_present_empty_map_stays_non_null()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var second = CreateRoot(Path.Combine(home, "second-library"));
        WriteLibraries(Path.Combine(root, "steamapps"), "libraryfolders.legacy.vdf",
            ("${ROOT}", root), ("${SECOND}", second));

        var absent = Discover();
        Assert.Null(absent.Libraries.Single(l => l.Index == "0").Apps);
        Assert.Null(absent.Libraries.Single(l => l.Index == "1").Apps);

        WriteLibrariesRaw(Path.Combine(root, "steamapps"),
            "\"libraryfolders\"\n{\n\t\"0\" { \"path\" \"" + root + "\" \"apps\" { } }\n}\n");
        var present = Discover();
        Assert.NotNull(Assert.Single(present.Libraries).Apps);
        Assert.Empty(Apps(present.Libraries[0]));
    }

    [Fact]
    public void Distinct_libraries_on_the_same_disk_are_not_merged()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var first = CreateRoot(Path.Combine(home, "first"));
        var second = CreateRoot(Path.Combine(home, "second"));
        WriteLibraries(Path.Combine(root, "steamapps"), "libraryfolders.legacy.vdf",
            ("${ROOT}", first), ("${SECOND}", second));

        var result = Discover();

        Assert.Equal([SteamDiscovery.RootLibraryIndex, "0", "1"], result.Libraries.Select(l => l.Index));
        Assert.Equal(RealPath(first), result.Libraries[1].ResolvedPath);
        Assert.Equal(RealPath(second), result.Libraries[2].ResolvedPath);
        Assert.Equal(3, result.Libraries.Count(l => l.IsAvailable));
    }

    [Fact]
    public void Unavailable_library_keeps_its_apps_visible()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        WriteLibraries(Path.Combine(root, "steamapps"), "libraryfolders.unavailable.vdf",
            ("${ROOT}", root), ("${MISSING}", Path.Combine(home, "unmounted")));

        var result = Discover();

        var library = result.Libraries.Single(l => l.Index == "1");
        Assert.Equal(SteamLibraryStatus.MissingPath, library.Status);
        Assert.False(library.IsAvailable);
        Assert.Equal([1510u, 1520u], Apps(library).Keys.Order());
        Assert.Equal(Path.Combine(home, "unmounted", "SteamLibrary"), library.DeclaredPath);
        Assert.Equal(2, Apps(library).Count);
    }

    [Fact]
    public void Libraries_resolving_to_the_same_realpath_are_deduplicated_in_file_order()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var link = Path.Combine(home, "library-link");
        Directory.CreateSymbolicLink(link, root);
        WriteLibraries(Path.Combine(root, "steamapps"), "libraryfolders.duplicates.vdf",
            ("${ROOT}", root), ("${LINK}", link), ("${MISSING}", Path.Combine(home, "unmounted")));

        var result = Discover();

        Assert.Equal(["0", "1", "2"], result.Libraries.Select(l => l.Index));
        Assert.Equal(SteamLibraryStatus.Available, result.Libraries[0].Status);
        Assert.Equal(SteamLibraryStatus.DuplicateLibrary, result.Libraries[1].Status);
        Assert.Equal([45u], Apps(result.Libraries[1]).Keys);
        Assert.Equal(SteamLibraryStatus.MissingPath, result.Libraries[2].Status);
        Assert.Contains(result.Issues, issue => issue is { Identity: "1", Reason: "duplicate-library" });
        Assert.Contains(result.Issues, issue => issue is { Identity: "3", Reason: "invalid-library-path" });
        Assert.DoesNotContain(result.Libraries, library => library.Index == SteamDiscovery.RootLibraryIndex);
    }

    [Fact]
    public void Library_without_steamapps_is_unavailable_but_kept()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var bare = Path.Combine(home, "bare-library");
        Directory.CreateDirectory(bare);
        WriteLibraries(Path.Combine(root, "steamapps"), "libraryfolders.no-steamapps.vdf",
            ("${ROOT}", root), ("${LIBRARY}", bare));

        var result = Discover();

        var library = result.Libraries.Single(l => l.Index == "1");
        Assert.Equal(SteamLibraryStatus.SteamAppsMissing, library.Status);
        Assert.Equal([44u], Apps(library).Keys);
    }

    [Fact]
    public void Stale_mount_point_without_steamapps_is_reported_as_unmounted()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var mount = Path.Combine(home, "mounted-drive");
        Directory.CreateDirectory(mount);
        WriteLibraries(Path.Combine(root, "steamapps"), "libraryfolders.no-steamapps.vdf",
            ("${ROOT}", root), ("${LIBRARY}", mount));

        var result = SteamDiscovery.Discover(environment, Paths(), new FaultyFileSystem(fileSystem, mountPoint: true));

        Assert.Equal(SteamLibraryStatus.StaleMountpoint, result.Libraries.Single(l => l.Index == "1").Status);
    }

    [Fact]
    public void Library_steamapps_link_outside_the_library_is_not_followed()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var library = Path.Combine(home, "linked-library");
        Directory.CreateDirectory(library);
        var outside = Path.Combine(home, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(library, "steamapps"), outside);
        WriteLibraries(Path.Combine(root, "steamapps"), "libraryfolders.no-steamapps.vdf",
            ("${ROOT}", root), ("${LIBRARY}", library));

        var result = Discover();

        Assert.Equal(SteamLibraryStatus.SteamAppsOutsideLibrary, result.Libraries.Single(l => l.Index == "1").Status);
    }

    [Fact]
    public void Malformed_library_file_keeps_the_root_library_and_reports_basename_and_offset()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var file = WriteLibraries(Path.Combine(root, "steamapps"), "libraryfolders.malformed.vdf", ("${ROOT}", root));

        var result = Discover();

        var library = Assert.Single(result.Libraries);
        Assert.Equal(SteamDiscovery.RootLibraryIndex, library.Index);
        Assert.Equal(SteamLibraryStatus.Available, library.Status);
        Assert.Empty(Apps(library));
        Assert.Equal(file, result.LibraryFilePath);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("unclosed-object", issue.Reason);
        // Shipped diagnostics carry the bare filename deliberately so host layout is not leaked;
        // PR #111 makes this the parser-wide contract (and lands first).
        Assert.Equal("libraryfolders.vdf", issue.File);
        Assert.True(issue.ByteOffset > 0);
    }

    [Fact]
    public void Library_file_symlink_outside_the_root_is_rejected_without_reading_it()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var outside = Path.Combine(home, "outside-libraryfolders.vdf");
        File.WriteAllText(outside, "\"libraryfolders\" { }");
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        Directory.CreateSymbolicLink(Path.Combine(root, "steamapps", "libraryfolders.vdf"), outside);
        var recording = new RecordingFileSystem(fileSystem);

        var result = SteamDiscovery.Discover(environment, Paths(), recording);

        Assert.Equal(0, recording.Reads);
        Assert.Contains(result.Issues, issue => issue is { File: "libraryfolders.vdf", Reason: "library-file-outside-root" });
        Assert.Equal(SteamDiscovery.RootLibraryIndex, Assert.Single(result.Libraries).Index);
    }

    [Fact]
    public void Read_targets_the_resolved_path_when_the_declared_link_is_swapped()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var second = CreateRoot(Path.Combine(home, "second-library"));
        var target = WriteLibraries(Path.Combine(root, "steamapps"), "libraryfolders.legacy.vdf",
            ("${ROOT}", root), ("${SECOND}", second));
        var declared = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        File.Move(target, Path.Combine(root, "real-libraryfolders.vdf"));
        var real = Path.Combine(root, "real-libraryfolders.vdf");
        Directory.CreateSymbolicLink(declared, real);
        var outside = Path.Combine(home, "outside-libraryfolders.vdf");
        File.WriteAllText(outside, "\"libraryfolders\" { }");
        var racing = new SwapFileSystem(fileSystem, declared, outside);

        var result = SteamDiscovery.Discover(environment, Paths(), racing);

        Assert.Equal(2, result.Libraries.Count);
        Assert.Empty(result.Issues);
        Assert.Equal(outside, new FileInfo(declared).LinkTarget);
    }

    [Fact]
    public void Missing_library_file_keeps_the_root_library_and_reports_it()
    {
        CreateRoot(Path.Combine(home, ".local", "share", "Steam"));

        var result = Discover();

        Assert.Equal(SteamDiscovery.RootLibraryIndex, Assert.Single(result.Libraries).Index);
        Assert.Null(result.LibraryFilePath);
        Assert.Contains(result.Issues, issue => issue is { File: "libraryfolders.vdf", Identity: "libraryfolders", Reason: "library-file-missing" });
    }

    [Fact]
    public void Root_library_is_added_when_the_file_only_lists_external_libraries()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var first = CreateRoot(Path.Combine(home, "first"));
        var second = CreateRoot(Path.Combine(home, "second"));
        WriteLibraries(Path.Combine(root, "steamapps"), "libraryfolders.legacy.vdf",
            ("${ROOT}", first), ("${SECOND}", second));

        var result = Discover();

        Assert.Equal([SteamDiscovery.RootLibraryIndex, "0", "1"], result.Libraries.Select(l => l.Index));
        Assert.Equal(RealPath(root), result.Libraries[0].ResolvedPath);
        Assert.Empty(Apps(result.Libraries[0]));
        Assert.Equal(3, result.Libraries.Count(l => l.IsAvailable));
    }

    [Fact]
    public void Unknown_root_fields_are_discarded_without_affecting_discovery()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        WriteLibrariesRaw(Path.Combine(root, "steamapps"),
            "\"libraryfolders\"\n{\n\t\"future\" \"kept\"\n\t\"nested\" { \"x\" \"y\" }\n\t\"0\" { \"path\" \"" + root + "\" \"apps\" { \"42\" \"1\" } }\n}\n");

        var result = Discover();

        var library = Assert.Single(result.Libraries);
        Assert.Equal(RealPath(root), library.ResolvedPath);
        Assert.Equal([42u], Apps(library).Keys);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Config_copy_is_used_when_the_steamapps_copy_is_absent()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var second = CreateRoot(Path.Combine(home, "second"));
        var file = WriteLibraries(Path.Combine(root, "config"), "libraryfolders.modern.vdf",
            ("${ROOT}", root), ("${SECOND}", second));

        var result = Discover();

        Assert.Equal(file, result.LibraryFilePath);
        Assert.Equal(2, result.Libraries.Count);
    }

    [Fact]
    public void Config_copy_is_used_when_the_primary_copy_is_rejected()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var second = CreateRoot(Path.Combine(home, "second"));
        WriteLibraries(Path.Combine(root, "steamapps"), "libraryfolders.malformed.vdf", ("${ROOT}", root));
        var config = WriteLibraries(Path.Combine(root, "config"), "libraryfolders.modern.vdf",
            ("${ROOT}", root), ("${SECOND}", second));

        var result = Discover();

        Assert.Equal(config, result.LibraryFilePath);
        Assert.Equal(2, result.Libraries.Count);
        Assert.Contains(result.Issues, issue => issue is { File: "libraryfolders.vdf", Reason: "unclosed-object" });
    }

    [Fact]
    public void Oversized_library_file_is_rejected_before_it_is_read()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var file = WriteLibraries(Path.Combine(root, "steamapps"), "libraryfolders.modern.vdf",
            ("${ROOT}", root), ("${SECOND}", root));

        var result = Discover(maxBytes: 16);

        Assert.Equal(SteamDiscovery.RootLibraryIndex, Assert.Single(result.Libraries).Index);
        Assert.Equal(file, result.LibraryFilePath);
        Assert.Contains(result.Issues, issue => issue is { File: "libraryfolders.vdf", Identity: "libraryfolders", Reason: "size-limit" });
    }

    [Fact]
    public void Unreadable_library_file_is_reported_and_the_root_library_is_kept()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var file = WriteLibraries(Path.Combine(root, "steamapps"), "libraryfolders.modern.vdf",
            ("${ROOT}", root), ("${SECOND}", root));

        var result = SteamDiscovery.Discover(environment, Paths(), new FaultyFileSystem(fileSystem, failReads: true));

        Assert.Equal(SteamDiscovery.RootLibraryIndex, Assert.Single(result.Libraries).Index);
        Assert.Equal(file, result.LibraryFilePath);
        Assert.Contains(result.Issues, issue => issue is { File: "libraryfolders.vdf", Identity: "libraryfolders", Reason: "unreadable-library-file" });
    }

    [Fact]
    public void Fifo_library_file_is_rejected_without_blocking()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        MakeFifo(file);

        var discovery = Task.Run(() => Discover());
        Assert.True(discovery.Wait(TimeSpan.FromSeconds(10)), "discovery blocked on a FIFO");
        var result = discovery.Result;

        Assert.Equal(SteamDiscovery.RootLibraryIndex, Assert.Single(result.Libraries).Index);
        Assert.Equal(file, result.LibraryFilePath);
        Assert.Contains(result.Issues, issue => issue is { File: "libraryfolders.vdf", Reason: "not-a-regular-file" });
    }

    [Fact]
    public void Zero_length_paths_are_rejected_before_opening()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var empty = Path.Combine(home, "empty.vdf");
        File.WriteAllText(empty, string.Empty);
        var fifo = Path.Combine(home, "fifo");
        MakeFifo(fifo);

        Assert.Equal(SteamFileReadStatus.NotRegularFile, fileSystem.ReadFile(empty, 1024).Status);
        Assert.Equal(SteamFileReadStatus.NotRegularFile, fileSystem.ReadFile(fifo, 1024).Status);
        Assert.Equal(SteamFileReadStatus.NotRegularFile, fileSystem.ReadFile("/dev/null", 1024).Status);
        Assert.Equal(SteamFileReadStatus.Missing, fileSystem.ReadFile(Path.Combine(home, "missing"), 1024).Status);
    }

    [Fact]
    public void Empty_library_file_is_reported_without_parsing()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        File.WriteAllText(file, string.Empty);

        var result = Discover();

        Assert.Equal(SteamDiscovery.RootLibraryIndex, Assert.Single(result.Libraries).Index);
        Assert.Equal(file, result.LibraryFilePath);
        Assert.Contains(result.Issues, issue => issue is { File: "libraryfolders.vdf", Reason: "not-a-regular-file" });
    }

    [Fact]
    public void Read_is_bounded_when_the_file_grows_after_the_length_stat()
    {
        var path = Path.Combine(home, "growing.vdf");
        const int initial = 1024;
        File.WriteAllBytes(path, new byte[initial]);
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            while (!stop.IsCancellationRequested)
            {
                stream.WriteByte(0x20);
                stream.Flush();
            }
        });

        try
        {
            for (var i = 0; i < 50; i++)
            {
                var read = fileSystem.ReadFile(path, initial);
                Assert.True(read.Status is SteamFileReadStatus.Ok or SteamFileReadStatus.TooLarge);
                if (read.Status == SteamFileReadStatus.Ok)
                {
                    Assert.True(read.Bytes!.Length <= initial);
                }
            }
        }
        finally
        {
            stop.Cancel();
            writer.Wait(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void Invalid_options_and_apps_map_keep_the_library_and_report_without_values()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        WriteLibraries(Path.Combine(root, "steamapps"), "libraryfolders.invalid-apps.vdf", ("${ROOT}", root));

        var result = Discover();

        var library = Assert.Single(result.Libraries);
        Assert.Equal(SteamLibraryStatus.Available, library.Status);
        Assert.Empty(Apps(library));
        Assert.Null(library.Declared.Label);
        Assert.Null(library.Declared.ContentId);
        Assert.Null(library.Declared.TotalSize);
        Assert.Null(library.Declared.TimeLastUpdateVerified);
        Assert.Contains(library.Declared.UnknownFields, field => field.Key == "future");
        Assert.Contains(result.Issues, issue => issue.Reason == "invalid-optional-string");
        Assert.Contains(result.Issues, issue => issue.Reason == "invalid-optional-number");
        Assert.Contains(result.Issues, issue => issue.Reason == "invalid-optional-timestamp");
        Assert.Contains(result.Issues, issue => issue.Reason == "invalid-apps-map");
    }

    [Fact]
    public void Discovery_is_read_only_and_leaves_the_tree_byte_identical()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var second = CreateRoot(Path.Combine(home, "second-library"));
        WriteLibraries(Path.Combine(root, "steamapps"), "libraryfolders.modern.vdf",
            ("${ROOT}", root), ("${SECOND}", second));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "libraryfolders.vdf"), "synthetic unrelated copy");
        Directory.CreateSymbolicLink(Path.Combine(home, "link"), root);

        var before = Snapshot(home);
        _ = Discover();
        var after = Snapshot(home);

        Assert.Equal(before.Select(entry => entry.Path), after.Select(entry => entry.Path));
        for (var i = 0; i < before.Count; i++)
        {
            // A read-only call may legitimately advance atime by reading; every other property is
            // byte-identical, and atime never moves backwards.
            Assert.Equal(before[i] with { Atime = 0 }, after[i] with { Atime = 0 });
            Assert.True(after[i].Atime >= before[i].Atime);
        }
    }

    [Fact]
    public void Synthetic_corpus_is_hash_pinned_and_fully_listed()
    {
        var manifest = Path.Combine(Corpus, "SHA256SUMS");
        var pinned = File.ReadAllLines(manifest)
            .Select(line => line.Split("  ", 2, StringSplitOptions.None))
            .ToList();

        foreach (var parts in pinned)
        {
            Assert.Equal(2, parts.Length);
            Assert.Equal(64, parts[0].Length);
            Assert.Equal(parts[0], RepositoryPaths.Sha256(Path.Combine(Corpus, parts[1])));
        }

        var files = Directory.EnumerateFiles(Corpus, "*.vdf").Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(files, pinned.Select(parts => parts[1]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Libraryfolders_discovery_fuzz_exhausts_the_existing_budget_without_throw_or_hang()
    {
        using var log = new StringWriter(CultureInfo.InvariantCulture);
        var result = SteamDiscoveryFuzzTarget.Run(new FuzzOptions
        {
            CorpusRoot = Corpus,
            Seconds = 45,
            Iterations = 100_000,
            MaxInputBytes = 65_536,
            CaseTimeoutMilliseconds = 2000,
            MaxManagedBytes = 256L * 1024 * 1024,
        }, log);
        output.WriteLine(log.ToString());
        Assert.True(result.Cases > 0);
        Assert.True(result.Clean > 0);
        Assert.True(result.Rejected > 0);
        Assert.True(result.Budget == "iterations" ? result.Cases == 100_000 : result.ElapsedMilliseconds >= 45_000);
        Assert.Contains("STEAM DISCOVERY FUZZ COMPLETE", log.ToString(), StringComparison.Ordinal);
    }

    // ---- helpers --------------------------------------------------------------------------

    private IPaths Paths() => new LinuxPaths(environment, new LinuxFileSystem());

    private SteamDiscoveryResult Discover(int maxBytes = KeyValues.DefaultMaxBytes) =>
        SteamDiscovery.Discover(environment, Paths(), fileSystem, maxBytes);

    private SteamRootDiscovery ResolveRoots() => SteamRootResolver.Discover(environment, Paths(), fileSystem);

    private static string CreateRoot(string path)
    {
        Directory.CreateDirectory(Path.Combine(path, "steamapps"));
        return path;
    }

    private static string WriteLibraries(string directory, string template, params (string Token, string Value)[] tokens)
    {
        var text = File.ReadAllText(Path.Combine(Corpus, template));
        foreach (var (token, value) in tokens)
        {
            text = text.Replace(token, value, StringComparison.Ordinal);
        }

        return WriteLibrariesRaw(directory, text);
    }

    private static string WriteLibrariesRaw(string directory, string text)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "libraryfolders.vdf");
        File.WriteAllText(file, text);
        return file;
    }

    private static void MakeFifo(string path)
    {
        using var process = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { path } });
        Assert.NotNull(process);
        process!.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private string RealPath(string path) => Assert.IsType<string>(fileSystem.GetRealPath(path));

    private static IReadOnlyDictionary<uint, ulong> Apps(SteamDiscoveredLibrary library) =>
        Assert.IsAssignableFrom<IReadOnlyDictionary<uint, ulong>>(library.Apps);

    private static List<SnapshotEntry> Snapshot(string path)
    {
        var entries = new List<SnapshotEntry>();
        Walk(path);
        entries.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        return entries;

        void Walk(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var info = new FileInfo(entry);
                if (info.LinkTarget is { } target)
                {
                    entries.Add(new(entry, "link", 0, 0, 0, "n/a", target));
                }
                else if (Directory.Exists(entry))
                {
                    entries.Add(new(entry, "dir", 0, info.LastWriteTimeUtc.Ticks, info.LastAccessTimeUtc.Ticks, Mode(entry), string.Empty));
                    Walk(entry);
                }
                else
                {
                    entries.Add(new(entry, "file", info.Length, info.LastWriteTimeUtc.Ticks, info.LastAccessTimeUtc.Ticks,
                        Mode(entry), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry)))));
                }
            }
        }

        static string Mode(string path) => OperatingSystem.IsWindows() ? "n/a" : File.GetUnixFileMode(path).ToString();
    }

    private sealed record SnapshotEntry(string Path, string Kind, long Length, long Mtime, long Atime, string Mode, string Detail);

    private sealed class RecordingFileSystem(ISteamFileSystem inner) : ISteamFileSystem
    {
        public int Reads { get; private set; }
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public bool FileExists(string path) => inner.FileExists(path);
        public SteamFileRead ReadFile(string path, int maxBytes)
        {
            Reads++;
            return inner.ReadFile(path, maxBytes);
        }

        public string? GetRealPath(string path) => inner.GetRealPath(path);
        public bool IsMountPoint(string path) => inner.IsMountPoint(path);
    }

    private sealed class BeforeReadFileSystem(ISteamFileSystem inner, Action beforeRead) : ISteamFileSystem
    {
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public bool FileExists(string path) => inner.FileExists(path);
        public SteamFileRead ReadFile(string path, int maxBytes)
        {
            beforeRead();
            return inner.ReadFile(path, maxBytes);
        }
        public string? GetRealPath(string path) => inner.GetRealPath(path);
        public bool IsMountPoint(string path) => inner.IsMountPoint(path);
    }

    private sealed class SwapFileSystem(ISteamFileSystem inner, string declaredLink, string replacement) : ISteamFileSystem
    {
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public bool FileExists(string path) => inner.FileExists(path);
        public SteamFileRead ReadFile(string path, int maxBytes)
        {
            Assert.NotEqual(declaredLink, path);
            File.Delete(declaredLink);
            Directory.CreateSymbolicLink(declaredLink, replacement);
            return inner.ReadFile(path, maxBytes);
        }

        public string? GetRealPath(string path) => inner.GetRealPath(path);
        public bool IsMountPoint(string path) => inner.IsMountPoint(path);
    }

    private sealed class FaultyFileSystem(ISteamFileSystem inner, bool failReads = false, bool mountPoint = false) : ISteamFileSystem
    {
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public bool FileExists(string path) => inner.FileExists(path);
        public SteamFileRead ReadFile(string path, int maxBytes) =>
            failReads ? throw new IOException("synthetic read failure") : inner.ReadFile(path, maxBytes);
        public string? GetRealPath(string path) => inner.GetRealPath(path);
        public bool IsMountPoint(string path) => mountPoint || inner.IsMountPoint(path);
    }
}
