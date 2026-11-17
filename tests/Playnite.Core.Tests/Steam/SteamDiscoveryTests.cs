using System.Security.Cryptography;
using Playnite.Core;
using Playnite.Core.Tests.Serialization;
using Playnite.Core.Tests.TestDoubles;
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
    private readonly SteamFileSystem fileSystem = new();

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

        var result = SteamRootResolver.Discover(NewPaths(steamRoot: overridden), fileSystem);

        Assert.Equal([SteamRootKind.Override, SteamRootKind.HomeSteam, SteamRootKind.HomeLocalShare, SteamRootKind.Flatpak],
            result.Candidates.Select(c => c.Kind));
        Assert.Equal(RealPath(overridden), result.Root);
        Assert.Equal([SteamRootState.Accepted, SteamRootState.Missing, SteamRootState.Shadowed, SteamRootState.Missing],
            result.Candidates.Select(c => c.State));
    }

    [Fact]
    public void Symlinked_user_candidates_resolve_to_one_root_and_the_duplicate_is_reported()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        Directory.CreateDirectory(Path.Combine(home, ".steam"));
        Directory.CreateSymbolicLink(Path.Combine(home, ".steam", "steam"), Path.Combine("..", ".local", "share", "Steam"));

        var result = SteamRootResolver.Discover(NewPaths(), fileSystem);

        Assert.Equal(RealPath(root), result.Root);
        Assert.Equal(SteamRootState.Accepted, result.Candidates[0].State);
        Assert.Equal(SteamRootState.DuplicateRealPath, result.Candidates[1].State);
    }

    [Fact]
    public void Flatpak_root_is_used_when_it_is_the_only_install()
    {
        var flatpak = CreateRoot(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));

        var result = SteamRootResolver.Discover(NewPaths(), fileSystem);

        Assert.Equal(RealPath(flatpak), result.Root);
        Assert.Equal([SteamRootState.Missing, SteamRootState.Missing, SteamRootState.Accepted],
            result.Candidates.Select(c => c.State));
        Assert.Equal(SteamRootKind.Flatpak, result.Candidates[^1].Kind);
    }

    [Fact]
    public void Directory_without_steamapps_is_reported_and_the_next_candidate_wins()
    {
        Directory.CreateDirectory(Path.Combine(home, ".steam", "steam"));
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));

        var result = SteamRootResolver.Discover(NewPaths(), fileSystem);

        Assert.Equal(SteamRootState.SteamAppsMissing, result.Candidates[0].State);
        Assert.Equal(RealPath(root), result.Root);
    }

    [Fact]
    public void Candidate_that_is_a_file_is_reported_not_a_directory()
    {
        Directory.CreateDirectory(Path.Combine(home, ".steam"));
        File.WriteAllText(Path.Combine(home, ".steam", "steam"), "synthetic");

        var result = SteamRootResolver.Discover(NewPaths(), fileSystem);

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

        var result = SteamRootResolver.Discover(NewPaths(), fileSystem);

        Assert.Null(result.Root);
        Assert.Equal(SteamRootState.SteamAppsOutsideRoot, result.Candidates[1].State);
    }

    [Fact]
    public void No_candidate_yields_a_null_root_without_throwing()
    {
        var discovery = SteamDiscovery.Discover(NewPaths(), fileSystem);

        Assert.Null(discovery.Root);
        Assert.Empty(discovery.Libraries);
        Assert.Null(discovery.LibraryFilePath);
        Assert.Empty(discovery.Issues);
        Assert.All(discovery.Roots.Candidates, candidate => Assert.Equal(SteamRootState.Missing, candidate.State));
    }

    [Fact]
    public void Relative_override_and_repeated_candidate_are_each_reported_and_ignored()
    {
        var root = CreateRoot(Path.Combine(home, "root"));
        var result = SteamRootResolver.Probe(
        [
            new SteamRootCandidate(SteamRootKind.Override, "relative/steam"),
            new SteamRootCandidate(SteamRootKind.HomeSteam, root),
            new SteamRootCandidate(SteamRootKind.HomeSteam, root),
        ], fileSystem);

        Assert.Equal(RealPath(root), result.Root);
        Assert.Equal([SteamRootState.InvalidPath, SteamRootState.Accepted, SteamRootState.DuplicateRealPath],
            result.Candidates.Select(c => c.State));
    }

    [Fact]
    public void Symlink_loop_is_reported_as_unreadable_and_never_throws()
    {
        Directory.CreateDirectory(Path.Combine(home, ".steam"));
        Directory.CreateSymbolicLink(Path.Combine(home, ".steam", "steam"), "steam");

        var result = SteamRootResolver.Discover(NewPaths(), fileSystem);

        Assert.Null(result.Root);
        Assert.Equal(SteamRootState.Unreadable, result.Candidates[0].State);
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
    public void Candidate_classification_marks_the_override_and_keeps_the_p3_001_order()
    {
        var paths = NewPaths(steamRoot: "/synthetic/override");
        var candidates = SteamRootResolver.Candidates(paths);

        Assert.Equal(["Override", "HomeSteam", "HomeLocalShare", "Flatpak"], candidates.Select(c => c.Kind.ToString()));
        Assert.Equal(paths.SteamRootCandidates, candidates.Select(c => c.Path));
    }

    // ---- library discovery ----------------------------------------------------------------

    [Fact]
    public void Modern_file_reports_every_library_apps_and_first_appid_wins()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var second = CreateRoot(Path.Combine(home, "second-library"));
        var file = WriteLibraries(root, "libraryfolders.modern.vdf", ("${ROOT}", root), ("${SECOND}", second));

        var result = SteamDiscovery.Discover(NewPaths(), fileSystem);

        Assert.Equal(RealPath(root), result.Root);
        Assert.Equal(file, result.LibraryFilePath);
        Assert.Equal(["0", "1"], result.Libraries.Select(l => l.Index));
        Assert.All(result.Libraries, library => Assert.Equal(SteamLibraryStatus.Available, library.Status));
        Assert.Equal(RealPath(root), result.Libraries[0].ResolvedPath);
        Assert.Equal(RealPath(second), result.Libraries[1].ResolvedPath);
        Assert.Equal([42u, 43u], result.Libraries[0].Apps.Keys.Order());
        Assert.Equal([44u], result.Libraries[1].Apps.Keys);
        Assert.Contains(result.Issues, issue => issue is { Identity: "1", Reason: "duplicate-appid", Field: "42" });
    }

    [Fact]
    public void Legacy_index_form_is_accepted()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var second = CreateRoot(Path.Combine(home, "second-library"));
        WriteLibraries(root, "libraryfolders.legacy.vdf", ("${ROOT}", root), ("${SECOND}", second));

        var result = SteamDiscovery.Discover(NewPaths(), fileSystem);

        Assert.Equal(2, result.Libraries.Count);
        Assert.All(result.Libraries, library => Assert.Equal(SteamLibraryStatus.Available, library.Status));
        Assert.Equal(RealPath(second), result.Libraries[1].ResolvedPath);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Unavailable_library_keeps_its_apps_visible()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        WriteLibraries(root, "libraryfolders.unavailable.vdf", ("${ROOT}", root), ("${MISSING}", Path.Combine(home, "unmounted")));

        var result = SteamDiscovery.Discover(NewPaths(), fileSystem);

        var library = result.Libraries.Single(l => l.Index == "1");
        Assert.Equal(SteamLibraryStatus.MissingPath, library.Status);
        Assert.False(library.IsAvailable);
        Assert.Equal([1510u, 1520u], library.Apps.Keys.Order());
        Assert.Equal(Path.Combine(home, "unmounted", "SteamLibrary"), library.DeclaredPath);
        Assert.Equal(2, library.Apps.Count);
    }

    [Fact]
    public void Libraries_resolving_to_the_same_realpath_are_deduplicated_in_file_order()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var link = Path.Combine(home, "library-link");
        Directory.CreateSymbolicLink(link, root);
        WriteLibraries(root, "libraryfolders.duplicates.vdf",
            ("${ROOT}", root), ("${LINK}", link), ("${MISSING}", Path.Combine(home, "unmounted")));

        var result = SteamDiscovery.Discover(NewPaths(), fileSystem);

        Assert.Equal(["0", "1", "2"], result.Libraries.Select(l => l.Index));
        Assert.Equal(SteamLibraryStatus.Available, result.Libraries[0].Status);
        Assert.Equal(SteamLibraryStatus.DuplicateLibrary, result.Libraries[1].Status);
        Assert.Equal([45u], result.Libraries[1].Apps.Keys);
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
        WriteLibraries(root, "libraryfolders.no-steamapps.vdf", ("${ROOT}", root), ("${LIBRARY}", bare));

        var result = SteamDiscovery.Discover(NewPaths(), fileSystem);

        var library = result.Libraries.Single(l => l.Index == "1");
        Assert.Equal(SteamLibraryStatus.SteamAppsMissing, library.Status);
        Assert.Equal([44u], library.Apps.Keys);
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
        WriteLibraries(root, "libraryfolders.no-steamapps.vdf", ("${ROOT}", root), ("${LIBRARY}", library));

        var result = SteamDiscovery.Discover(NewPaths(), fileSystem);

        Assert.Equal(SteamLibraryStatus.SteamAppsOutsideLibrary, result.Libraries.Single(l => l.Index == "1").Status);
    }

    [Fact]
    public void Malformed_library_file_keeps_the_root_library_and_reports_offset()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var file = WriteLibraries(root, "libraryfolders.malformed.vdf", ("${ROOT}", root));

        var result = SteamDiscovery.Discover(NewPaths(), fileSystem);

        var library = Assert.Single(result.Libraries);
        Assert.Equal(SteamDiscovery.RootLibraryIndex, library.Index);
        Assert.Equal(SteamLibraryStatus.Available, library.Status);
        Assert.Empty(library.Apps);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("unclosed-object", issue.Reason);
        Assert.Equal(file, issue.File);
        Assert.True(issue.ByteOffset > 0);
    }

    [Fact]
    public void Missing_library_file_keeps_the_root_library_and_reports_it()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));

        var result = SteamDiscovery.Discover(NewPaths(), fileSystem);

        Assert.Equal(SteamDiscovery.RootLibraryIndex, Assert.Single(result.Libraries).Index);
        Assert.Null(result.LibraryFilePath);
        Assert.Contains(result.Issues, issue => issue is { Identity: "libraryfolders", Reason: "library-file-missing" });
    }

    [Fact]
    public void Root_library_is_added_when_the_file_only_lists_external_libraries()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var first = CreateRoot(Path.Combine(home, "first"));
        var second = CreateRoot(Path.Combine(home, "second"));
        WriteLibraries(root, "libraryfolders.legacy.vdf", ("${ROOT}", first), ("${SECOND}", second));

        var result = SteamDiscovery.Discover(NewPaths(), fileSystem);

        Assert.Equal([SteamDiscovery.RootLibraryIndex, "0", "1"], result.Libraries.Select(l => l.Index));
        Assert.Equal(RealPath(root), result.Libraries[0].ResolvedPath);
        Assert.Empty(result.Libraries[0].Apps);
        Assert.Equal(3, result.Libraries.Count(l => l.IsAvailable));
    }

    [Fact]
    public void Config_copy_is_used_when_the_steamapps_copy_is_absent()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        var second = CreateRoot(Path.Combine(home, "second"));
        var text = File.ReadAllText(Path.Combine(Corpus, "libraryfolders.modern.vdf"))
            .Replace("${ROOT}", root, StringComparison.Ordinal)
            .Replace("${SECOND}", second, StringComparison.Ordinal);
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var file = Path.Combine(root, "config", "libraryfolders.vdf");
        File.WriteAllText(file, text);

        var result = SteamDiscovery.Discover(NewPaths(), fileSystem);

        Assert.Equal(file, result.LibraryFilePath);
        Assert.Equal(2, result.Libraries.Count);
    }

    [Fact]
    public void Oversized_library_file_is_rejected_before_it_is_read()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        WriteLibraries(root, "libraryfolders.modern.vdf", ("${ROOT}", root), ("${SECOND}", root));

        var result = SteamDiscovery.Discover(NewPaths(), fileSystem, maxBytes: 16);

        Assert.Equal(SteamDiscovery.RootLibraryIndex, Assert.Single(result.Libraries).Index);
        Assert.Contains(result.Issues, issue => issue is { Identity: "libraryfolders", Reason: "size-limit" });
    }

    [Fact]
    public void Unreadable_library_file_is_reported_and_the_root_library_is_kept()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        WriteLibraries(root, "libraryfolders.modern.vdf", ("${ROOT}", root), ("${SECOND}", root));

        var result = SteamDiscovery.Discover(NewPaths(), new FaultyFileSystem(fileSystem, failReads: true));

        Assert.Equal(SteamDiscovery.RootLibraryIndex, Assert.Single(result.Libraries).Index);
        Assert.Contains(result.Issues, issue => issue is { Identity: "libraryfolders", Reason: "unreadable-library-file" });
    }

    [Fact]
    public void Invalid_options_and_apps_map_keep_the_library_and_report_without_values()
    {
        var root = CreateRoot(Path.Combine(home, ".local", "share", "Steam"));
        WriteLibraries(root, "libraryfolders.invalid-apps.vdf", ("${ROOT}", root));

        var result = SteamDiscovery.Discover(NewPaths(), fileSystem);

        var library = Assert.Single(result.Libraries);
        Assert.Equal(SteamLibraryStatus.Available, library.Status);
        Assert.Empty(library.Apps);
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
        WriteLibraries(root, "libraryfolders.modern.vdf", ("${ROOT}", root), ("${SECOND}", second));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "libraryfolders.vdf"), "synthetic unrelated copy");
        Directory.CreateSymbolicLink(Path.Combine(home, "link"), root);

        var before = Snapshot(home);
        _ = SteamDiscovery.Discover(NewPaths(), fileSystem);
        Assert.Equal(before, Snapshot(home));
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

    // ---- helpers --------------------------------------------------------------------------

    private IPaths NewPaths(string? steamRoot = null)
    {
        var environment = new MemoryEnvironment { Home = home };
        if (steamRoot is not null)
        {
            environment.Variables["STEAM_ROOT"] = steamRoot;
        }

        return new LinuxPaths(environment, new LinuxFileSystem());
    }

    private static string CreateRoot(string path)
    {
        Directory.CreateDirectory(Path.Combine(path, "steamapps"));
        return path;
    }

    private static string WriteLibraries(string root, string template, params (string Token, string Value)[] tokens)
    {
        var text = File.ReadAllText(Path.Combine(Corpus, template));
        foreach (var (token, value) in tokens)
        {
            text = text.Replace(token, value, StringComparison.Ordinal);
        }

        var file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }

    private string RealPath(string path) => Assert.IsType<string>(fileSystem.GetRealPath(path));

    private static string[] Snapshot(string path)
    {
        var lines = new List<string>();
        Walk(path);
        return [.. lines.Order(StringComparer.Ordinal)];

        void Walk(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var info = new FileInfo(entry);
                if (info.LinkTarget is { } target)
                {
                    lines.Add($"{entry}|link|{target}");
                }
                else if (Directory.Exists(entry))
                {
                    lines.Add($"{entry}|dir");
                    Walk(entry);
                }
                else
                {
                    lines.Add($"{entry}|file|{info.Length}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry)))}");
                }
            }
        }
    }

    private sealed class FaultyFileSystem(ISteamFileSystem inner, bool failReads = false) : ISteamFileSystem
    {
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public bool FileExists(string path) => inner.FileExists(path);
        public long GetFileLength(string path) => inner.GetFileLength(path);
        public byte[] ReadAllBytes(string path) => failReads ? throw new IOException("synthetic read failure") : inner.ReadAllBytes(path);
        public string? GetRealPath(string path) => inner.GetRealPath(path);
    }
}
