using System.Globalization;
using System.Text;
using Playnite.Core.Tests.Serialization;
using Playnite.Fuzz;
using Playnite.Steam;
using Xunit;

namespace Playnite.Core.Tests.Steam;

public sealed class SteamParserTests(ITestOutputHelper output)
{
    private static string Corpus => RepositoryPaths.Combine("tools", "Playnite.Fuzz", "Corpus", "p5-001");
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static KeyValue Parse(string text) => KeyValues.Parse(Bytes(text), "synthetic.vdf");
    private static string Manifest(string extra = "") =>
        "AppState { appid 42 name \"Synthetic\" installdir \"Game\" StateFlags 4 " + extra + " }";
    private static SteamParseResult<SteamManifest> Acf(string text, string file = "appmanifest_42.acf") =>
        SteamManifestParser.Parse(Bytes(text), file);

    [Fact]
    public void Quoted_and_bare_tokens_comments_empty_objects_case_and_escapes_decode_exactly()
    {
        var root = Parse("\uFEFF// comment\r\nroot { Key bare key \"quoted // not comment\" child {} escaped \"a\\\\b\\\"c\\n\\t\\r\" } // eof");
        Assert.Equal("root", root.Key);
        var obj = Assert.IsType<KeyValuesObject>(root.Object);
        Assert.Equal(["Key", "key", "child", "escaped"], obj.Entries.Select(e => e.Key));
        Assert.Equal("bare", obj.Text("Key"));
        Assert.Equal("quoted // not comment", obj.Text("key"));
        Assert.Empty(obj.Find("child")!.Object!.Entries);
        Assert.Equal("a\\b\"c\n\t\r", obj.Text("escaped"));
    }

    [Theory]
    [InlineData("", "expected-token", 0)]
    [InlineData("root", "expected-root-object", 4)]
    [InlineData("root scalar", "expected-root-object", 5)]
    [InlineData("root {", "unclosed-object", 6)]
    [InlineData("root { key", "expected-token", 10)]
    [InlineData("root { key }", "expected-token", 11)]
    [InlineData("root { key \"x", "unclosed-string", 13)]
    [InlineData("root { key \"x\\", "incomplete-escape", 14)]
    [InlineData("root { key \"\\q\" }", "unknown-escape", 13)]
    [InlineData("root { key bare\\q }", "unknown-escape", 16)]
    [InlineData("root { key \"x\nx\" }", "newline-in-string", 13)]
    [InlineData("root { key x key y }", "duplicate-key", 13)]
    [InlineData("root {} garbage", "trailing-garbage", 8)]
    [InlineData("root {} other {}", "trailing-garbage", 8)]
    [InlineData("root {} }", "trailing-garbage", 8)]
    [InlineData("root {} /* comment */", "trailing-garbage", 8)]
    [InlineData("root {}\r", "lone-cr", 7)]
    [InlineData("root {}\0", "control-character", 7)]
    [InlineData("root {}\u0001", "control-character", 7)]
    [InlineData("root {}\u007f", "control-character", 7)]
    [InlineData("root {}\u0085", "control-character", 7)]
    [InlineData("\uFEFF\uFEFFroot {}", "unexpected-bom", 3)]
    public void Syntax_errors_are_atomic_with_stable_reason_and_byte_offset(string text, string reason, int offset)
    {
        var exception = Assert.Throws<KeyValuesException>(() => Parse(text));
        Assert.Equal(reason, exception.Reason);
        Assert.Equal(offset, exception.ByteOffset);
        Assert.Equal("synthetic.vdf", exception.File);
        Assert.Contains($"byte {offset}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_error_identifies_the_exact_key_and_utf8_byte_offset_and_line()
    {
        const string text = "root {\r\n café x\r\n café y }";
        var exception = Assert.Throws<KeyValuesException>(() => Parse(text));
        Assert.Equal("café", exception.Key);
        Assert.Equal(3, exception.Line);
        Assert.Equal(Encoding.UTF8.GetByteCount(text[..text.LastIndexOf("café", StringComparison.Ordinal)]), exception.ByteOffset);
    }

    public static TheoryData<byte[]> InvalidUtf8 => new()
    {
        new byte[] { 0xFF }, new byte[] { 0xC0, 0x80 }, new byte[] { 0xED, 0xA0, 0x80 },
        new byte[] { 0xF4, 0x90, 0x80, 0x80 }, new byte[] { 0xE2, 0x82 }, new byte[] { 0x80 },
    };

    [Theory]
    [MemberData(nameof(InvalidUtf8))]
    public void Invalid_utf8_is_rejected_even_in_comments_without_replacement(byte[] invalid)
    {
        var bytes = Bytes("root {} // ").Concat(invalid).ToArray();
        var exception = Assert.Throws<KeyValuesException>(() => KeyValues.Parse(bytes, "bytes.vdf"));
        Assert.Equal("invalid-utf8", exception.Reason);
        Assert.Equal(11, exception.ByteOffset);
    }

    [Fact]
    public void Depth_and_byte_limits_bound_adversarial_input_but_large_values_and_64_levels_work()
    {
        var accepted = "root {" + string.Concat(Enumerable.Repeat("a {", 63)) + new string('}', 64);
        Assert.NotNull(Parse(accepted));
        var deep = "root {" + string.Concat(Enumerable.Repeat("a {", 512)) + new string('}', 513);
        Assert.Equal("depth-limit", Assert.Throws<KeyValuesException>(() => Parse(deep)).Reason);
        var large = "root { value \"" + new string('é', 30_000) + "\" }";
        Assert.Equal(new string('é', 30_000), Parse(large).Object!.Text("value"));
        Assert.Equal("size-limit", Assert.Throws<KeyValuesException>(() =>
            KeyValues.Parse(Bytes(large), "large.vdf", 60_000)).Reason);
    }

    [Fact]
    public void Synthetic_fixtures_round_trip_semantically_and_canonical_writing_is_idempotent()
    {
        foreach (var seed in SteamFuzzTarget.Load(Corpus).Where(s => s.RelativePath != "broken.vdf"))
        {
            var root = KeyValues.Parse(seed.Bytes, seed.RelativePath);
            var written = KeyValues.Write(root);
            var roundTrip = KeyValues.Parse(written, seed.RelativePath);
            Assert.Equal(written, KeyValues.Write(roundTrip));
            Assert.Equal(Flatten(root), Flatten(roundTrip));
        }

        static string[] Flatten(KeyValue entry) => new[] { entry.Key, entry.Text ?? "<object>" }
            .Concat(entry.Object?.Entries.SelectMany(Flatten) ?? []).ToArray();
    }

    [Fact]
    public void Seeded_property_round_trip_covers_unicode_and_every_supported_escape()
    {
        var random = new Random(5001);
        string[] alphabet = ["é", "🚀", "中", "e\u0301", "\\", "\"", "\n", "\r", "\t", " ", "//", "{}"];
        for (var i = 0; i < 256; i++)
        {
            var text = string.Concat(Enumerable.Range(0, random.Next(1, 80)).Select(_ => alphabet[random.Next(alphabet.Length)]));
            var encoded = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
            var root = Parse("root { value \"" + encoded + "\" }");
            Assert.Equal(text, root.Object!.Text("value"));
            Assert.Equal(text, KeyValues.Parse(KeyValues.Write(root), "roundtrip.vdf").Object!.Text("value"));
        }
    }

    [Fact]
    public void Modern_and_legacy_libraries_normalize_paths_preserve_order_and_report_first_appid_wins()
    {
        var result = SteamLibraryParser.Parse(File.ReadAllBytes(Path.Combine(Corpus, "libraryfolders.vdf")), "libraryfolders.vdf");
        var libraries = Assert.IsType<SteamLibraries>(result.Value).Libraries;
        Assert.Equal(["0", "1", "2"], libraries.Select(l => l.Index));
        Assert.Equal("/synthetic/steam/root", libraries[0].Path);
        Assert.Equal(66_000_000_001ul, libraries[0].Apps![42]);
        Assert.False(libraries[1].Apps!.ContainsKey(42));
        Assert.Equal(2048ul, libraries[1].Apps![44]);
        Assert.Null(libraries[2].Apps);
        Assert.Single(libraries[0].UnknownFields);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("1", issue.Identity);
        Assert.Equal("duplicate-appid", issue.Reason);
        Assert.Equal("42", issue.Field);
    }

    [Theory]
    [InlineData("0 {}", "invalid-library-path")]
    [InlineData("0 { path relative }", "invalid-library-path")]
    [InlineData("0 { path \"\" }", "invalid-library-path")]
    [InlineData("0 { path /valid apps broken }", "invalid-apps-map")]
    [InlineData("0 { path /valid apps { x 1 } }", "invalid-apps-map")]
    [InlineData("0 { path /valid apps { 42 -1 } }", "invalid-apps-map")]
    [InlineData("0 { path /valid apps { 42 {} } }", "invalid-apps-map")]
    [InlineData("0 { path /valid apps { 42 1 042 2 } }", "invalid-apps-map")]
    public void Bad_library_records_are_reported_without_losing_siblings(string bad, string reason)
    {
        var result = SteamLibraryParser.Parse(Bytes("libraryfolders { " + bad + " 1 /other }"), "libraries.vdf");
        Assert.NotNull(result.Value);
        Assert.Equal("0", Assert.Single(result.Issues).Identity);
        Assert.Equal(reason, result.Issues[0].Reason);
        Assert.Equal("/other", result.Value.Libraries[^1].Path);
        if (reason == "invalid-apps-map") { Assert.Empty(result.Value.Libraries[0].Apps!); }
    }

    [Fact]
    public void Malformed_library_file_returns_no_partial_library_and_wrong_root_is_rejected()
    {
        var result = SteamLibraryParser.Parse(Bytes("libraryfolders { 0 /valid 1 {"), "bad.vdf");
        Assert.Null(result.Value);
        Assert.Equal("unclosed-object", Assert.Single(result.Issues).Reason);
        Assert.Equal("bad.vdf", result.Issues[0].File);
        Assert.Null(SteamLibraryParser.Parse(Bytes("LibraryFolders {}"), "wrong.vdf").Value);
    }

    [Fact]
    public void Complete_manifest_binds_typed_fields_and_never_interprets_install_scripts_as_posix_paths()
    {
        var result = SteamManifestParser.Parse(File.ReadAllBytes(Path.Combine(Corpus, "appmanifest_42.acf")), "appmanifest_42.acf");
        var app = Assert.IsType<SteamManifest>(result.Value);
        Assert.Empty(result.Issues);
        Assert.Equal(42u, app.AppId);
        Assert.Equal("Synthetic Café™ 🚀", app.Name);
        Assert.Equal(66_000_000_001ul, app.SizeOnDisk);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), app.LastUpdated);
        Assert.Equal(52u, app.InstalledDepots![51].DlcAppId);
        Assert.Equal(9_000_000_000_000_000_001ul, app.InstalledDepots[51].Manifest);
        Assert.Equal(54u, app.SharedDepots![53]);
        Assert.Equal("addins\\synthetic\\installScript.vdf", app.InstallScripts!["51"]);
        Assert.Equal("english", Assert.Single(app.UserConfig!).Text);
        Assert.Empty(app.MountedConfig!);
        Assert.Single(app.UnknownFields);
    }

    [Theory]
    [InlineData("appid", "")]
    [InlineData("appid", "-1")]
    [InlineData("appid", "+42")]
    [InlineData("appid", "4294967296")]
    [InlineData("appid", "\" 42\"")]
    [InlineData("appid", "{}")]
    [InlineData("name", "")]
    [InlineData("name", "\"\"")]
    [InlineData("installdir", "")]
    [InlineData("installdir", "\"\"")]
    [InlineData("installdir", "/absolute")]
    [InlineData("installdir", "../escape")]
    [InlineData("installdir", "nested/../../escape")]
    [InlineData("installdir", "\"..\\\\..\\\\escape\"")]
    [InlineData("installdir", "\"nested/..\\\\../escape\"")]
    [InlineData("installdir", "\"a\\\\b/../../escape\"")]
    [InlineData("appid", "٤٢")]
    [InlineData("StateFlags", "４")]
    [InlineData("StateFlags", "")]
    [InlineData("StateFlags", "-4")]
    [InlineData("StateFlags", "4294967296")]
    public void Missing_or_invalid_required_fields_skip_with_filename_identity(string field, string replacement)
    {
        var original = field switch { "appid" => "42", "name" => "\"Synthetic\"", "installdir" => "\"Game\"", _ => "4" };
        var text = Manifest().Replace(field + " " + original, replacement.Length == 0 ? "" : field + " " + replacement, StringComparison.Ordinal);
        var result = Acf(text);
        Assert.Null(result.Value);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("42", issue.Identity);
        Assert.Equal("invalid-required-field", issue.Reason);
        Assert.Equal(field, issue.Field);
    }

    [Fact]
    public void Appid_mismatch_skips_rather_than_silently_rebinding_identity()
    {
        var result = Acf(Manifest(), "appmanifest_43.acf");
        Assert.Null(result.Value);
        Assert.Equal("43", Assert.Single(result.Issues).Identity);
        Assert.Equal("appid-mismatch", result.Issues[0].Reason);
    }

    [Theory]
    [InlineData("AppState {} AppState {}", "trailing-garbage")]
    [InlineData("appstate {}", "invalid-root")]
    [InlineData("", "expected-token")]
    [InlineData("AppState { appid 42 appid 42 }", "duplicate-key")]
    public void Missing_wrong_or_duplicate_appstate_root_and_duplicate_fields_reject_the_file(string text, string reason)
    {
        var result = Acf(text);
        Assert.Null(result.Value);
        Assert.Equal(reason, Assert.Single(result.Issues).Reason);
        Assert.Equal("42", result.Issues[0].Identity);
    }

    [Theory]
    [InlineData("manifest_42.acf")]
    [InlineData("appmanifest_.acf")]
    [InlineData("appmanifest_4294967296.acf")]
    [InlineData("appmanifest_+42.acf")]
    public void Invalid_filename_is_an_identity_preserving_skip(string file)
    {
        var result = Acf(Manifest(), file);
        Assert.Null(result.Value);
        Assert.Equal(file, Assert.Single(result.Issues).Identity);
        Assert.Equal("invalid-manifest-filename", result.Issues[0].Reason);
    }

    [Fact]
    public void Optional_absence_stays_absent_and_invalid_numbers_are_reported_without_values()
    {
        var missing = Assert.IsType<SteamManifest>(Acf(Manifest()).Value);
        Assert.Null(missing.SizeOnDisk);
        Assert.Null(missing.LastUpdated);
        Assert.Null(missing.UserConfig);
        Assert.Null(missing.MountedConfig);
        Assert.Empty(missing.OptionalNumbers);
        var result = Acf(Manifest("SizeOnDisk -1 lastupdated 18446744073709551615 buildid nope LastPlayed -1"));
        var app = Assert.IsType<SteamManifest>(result.Value);
        Assert.Null(app.SizeOnDisk);
        Assert.Null(app.LastUpdated);
        Assert.Empty(app.OptionalNumbers);
        Assert.Equal(4, result.Issues.Count);
        Assert.All(result.Issues, issue => Assert.Equal("42", issue.Identity));
        Assert.DoesNotContain("18446744073709551615", string.Join(";", result.Issues), StringComparison.Ordinal);
        Assert.Null(Assert.IsType<SteamManifest>(Acf(Manifest("LastUpdated 1700000000")).Value).LastUpdated);
    }

    [Theory]
    [InlineData("Universe")]
    [InlineData("SizeOnDisk")]
    [InlineData("StagingSize")]
    [InlineData("lastupdated")]
    [InlineData("LastPlayed")]
    [InlineData("LastOwner")]
    [InlineData("buildid")]
    [InlineData("DownloadType")]
    [InlineData("UpdateResult")]
    [InlineData("BytesToDownload")]
    [InlineData("BytesDownloaded")]
    [InlineData("BytesToStage")]
    [InlineData("BytesStaged")]
    [InlineData("BytesToVerify")]
    [InlineData("BytesVerified")]
    [InlineData("TargetBuildID")]
    [InlineData("AutoUpdateBehavior")]
    [InlineData("AllowOtherDownloadsWhileRunning")]
    [InlineData("ScheduledAutoUpdate")]
    public void Every_optional_numeric_field_reports_invalid_input_without_inventing_zero(string field)
    {
        var result = Acf(Manifest(field + " 18446744073709551616"));
        var app = Assert.IsType<SteamManifest>(result.Value);
        Assert.Equal(field == "LastOwner" ? null : field, Assert.Single(result.Issues).Field);
        Assert.Equal("invalid-optional-number", result.Issues[0].Reason);
        Assert.False(app.OptionalNumbers.ContainsKey(field));
        Assert.Null(app.SizeOnDisk);
        Assert.Null(app.LastUpdated);
    }

    [Fact]
    public void Full_unsigned_ranges_and_exact_timestamp_boundaries_are_not_truncated_or_culture_dependent()
    {
        var result = Acf("AppState { appid 4294967295 name test installdir game StateFlags 4294967295 SizeOnDisk 18446744073709551615 lastupdated 253402300799 LastPlayed 0 }",
            "appmanifest_4294967295.acf");
        var app = Assert.IsType<SteamManifest>(result.Value);
        Assert.Empty(result.Issues);
        Assert.Equal(uint.MaxValue, app.AppId);
        Assert.Equal(uint.MaxValue, app.StateFlags);
        Assert.Equal(ulong.MaxValue, app.SizeOnDisk);
        Assert.Equal(253402300799L, app.LastUpdated!.Value.ToUnixTimeSeconds());
        Assert.Equal(0ul, app.OptionalNumbers["LastPlayed"]);
        var overflow = Acf(Manifest("LastPlayed 253402300800 ScheduledAutoUpdate 253402300800"));
        Assert.Equal(2, overflow.Issues.Count);
        Assert.Empty(overflow.Value!.OptionalNumbers);
    }

    [Fact]
    public void Internal_dot_segments_are_normalized_only_at_path_projection_and_backslashes_remain_literal()
    {
        var result = Acf(Manifest().Replace("\"Game\"", "\"parent/../Game\\\\literal\"", StringComparison.Ordinal));
        var app = Assert.IsType<SteamManifest>(result.Value).ToApp("/synthetic/root", true, true, false);
        Assert.Equal("parent/../Game\\literal", app.InstallDir);
        Assert.Equal("/synthetic/root/steamapps/common/Game\\literal", app.InstallPath);
    }

    [Fact]
    public void Unknown_root_fields_and_unknown_depot_objects_are_retained_in_file_order()
    {
        var libraries = SteamLibraryParser.Parse(Bytes("libraryfolders { future { nested yes } 0 /outside/../elsewhere futureScalar kept }"), "libraryfolders.vdf");
        Assert.Equal(["future", "futureScalar"], libraries.Value!.UnknownFields.Select(e => e.Key));
        Assert.Equal("/elsewhere", Assert.Single(libraries.Value.Libraries).Path);
        var app = Acf(Manifest("InstalledDepots { 51 { manifest 1 size 2 extra { nested yes } } } Future retained"));
        Assert.Equal("extra", Assert.Single(app.Value!.InstalledDepots![51].UnknownFields).Key);
        Assert.Equal("Future", Assert.Single(app.Value.UnknownFields).Key);
    }

    [Fact]
    public void Library_optional_metadata_failures_are_reported_and_malformed_app_maps_drop_all_members()
    {
        var result = SteamLibraryParser.Parse(Bytes("libraryfolders { 0 { path /valid label {} contentid nope totalsize -1 time_last_update_verified 253402300800 apps { 42 1 invalid 2 } } }"), "libraryfolders.vdf");
        var library = Assert.Single(result.Value!.Libraries);
        Assert.Null(library.Label);
        Assert.Null(library.ContentId);
        Assert.Null(library.TotalSize);
        Assert.Null(library.TimeLastUpdateVerified);
        Assert.Empty(library.Apps!);
        Assert.Equal(5, result.Issues.Count);
    }

    [Theory]
    [InlineData("UserConfig scalar", "invalid-optional-object")]
    [InlineData("MountedConfig scalar", "invalid-optional-object")]
    [InlineData("InstalledDepots { 51 {} }", "invalid-depot")]
    [InlineData("SharedDepots { nope 42 }", "invalid-shared-depot")]
    [InlineData("InstallScripts { 51 {} }", "invalid-install-script")]
    public void Invalid_optional_objects_are_reported_without_skipping_the_app(string extra, string reason)
    {
        var result = Acf(Manifest(extra));
        Assert.NotNull(result.Value);
        Assert.Equal(reason, Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public void Owner_values_never_enter_models_unknown_bags_or_diagnostics()
    {
        const string secret = "76561199999999999";
        var result = Acf(Manifest($"LastOwner {secret} future {{ LastOwner {secret} retained yes }} UserConfig {{ LastOwner {secret} }}"));
        var app = Assert.IsType<SteamManifest>(result.Value);
        Assert.Empty(result.Issues);
        Assert.Empty(app.UserConfig!);
        Assert.Equal("retained", Assert.Single(Assert.Single(app.UnknownFields).Object!.Entries).Key);
        Assert.DoesNotContain("LastOwner", app.OptionalNumbers.Keys);
        Assert.DoesNotContain(secret, app.ToString(), StringComparison.Ordinal);
        var invalid = Acf(Manifest("LastOwner private-owner-value"));
        Assert.DoesNotContain("private-owner-value", string.Join(";", invalid.Issues), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4u, true, true, SteamAppState.Installed)]
    [InlineData(6u, true, true, SteamAppState.UpdatePending)]
    [InlineData(6u, true, false, SteamAppState.Broken)]
    [InlineData(4u, false, true, SteamAppState.Unavailable)]
    [InlineData(12u, true, true, SteamAppState.Unknown)]
    [InlineData(0u, true, true, SteamAppState.Unknown)]
    [InlineData(2u, true, true, SteamAppState.Unknown)]
    public void Projection_uses_explicit_observations_preserves_raw_flags_and_never_guesses_proton(uint flags, bool library, bool directory, SteamAppState expected)
    {
        var manifest = Assert.IsType<SteamManifest>(Acf(Manifest().Replace("StateFlags 4", "StateFlags " + flags.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)).Value);
        var app = manifest.ToApp("/synthetic/root/./", library, directory, isProton: false);
        Assert.Equal(expected, app.State);
        Assert.Equal(flags, app.StateFlags);
        Assert.False(app.IsProton);
        Assert.Null(app.SizeOnDisk);
        Assert.Equal("/synthetic/root/steamapps/common/Game", app.InstallPath);
        Assert.True(manifest.ToApp("/synthetic/root", library, directory, isProton: true).IsProton);
    }

    [Fact]
    public void Update_pending_fixture_keeps_builds_and_mount_config_without_inventing_mount_options()
    {
        var result = SteamManifestParser.Parse(File.ReadAllBytes(Path.Combine(Corpus, "appmanifest_43.acf")), "appmanifest_43.acf");
        var manifest = Assert.IsType<SteamManifest>(result.Value);
        Assert.Empty(result.Issues);
        Assert.Equal(100ul, manifest.OptionalNumbers["buildid"]);
        Assert.Equal(101ul, manifest.OptionalNumbers["TargetBuildID"]);
        Assert.Equal(SteamAppState.UpdatePending, manifest.ToApp("/synthetic/root", true, true, false).State);
        Assert.Equal("english", Assert.Single(manifest.MountedConfig!).Text);
    }

    [Fact]
    public void The_vdf_acf_byte_fuzz_target_exhausts_the_existing_budget_with_no_crash_hang_or_allocation_growth()
    {
        using var log = new StringWriter(CultureInfo.InvariantCulture);
        var result = SteamFuzzTarget.Run(new FuzzOptions
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
        Assert.Contains("STEAM FUZZ COMPLETE", log.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("InstalledDepots", "51 { manifest 1 size 2 }")]
    [InlineData("SharedDepots", "51 42")]
    [InlineData("InstallScripts", "51 script.vdf")]
    [InlineData("UserConfig", "language english")]
    [InlineData("MountedConfig", "language english")]
    public void Optional_objects_distinguish_absent_empty_and_populated(string field, string entries)
    {
        Assert.Null(Count(Acf(Manifest()).Value!));
        Assert.Equal(0, Count(Acf(Manifest(field + " {} ")).Value!));
        Assert.Equal(1, Count(Acf(Manifest(field + " { " + entries + " } ")).Value!));
        Assert.Null(Count(Acf(Manifest(field + " scalar ")).Value!));

        int? Count(SteamManifest manifest) => field switch
        {
            "InstalledDepots" => manifest.InstalledDepots?.Count,
            "SharedDepots" => manifest.SharedDepots?.Count,
            "InstallScripts" => manifest.InstallScripts?.Count,
            "UserConfig" => manifest.UserConfig?.Count,
            _ => manifest.MountedConfig?.Count,
        };
    }

    [Theory]
    [InlineData("LastOwner")]
    [InlineData("lastowner")]
    [InlineData("LASTOWNER")]
    [InlineData("Lastowner")]
    [InlineData("lAsToWnEr")]
    public void Owner_casing_is_private_at_every_model_boundary_but_raw_trees_round_trip(string owner)
    {
        const string secret = "76561199999999999";
        var text = Manifest($"{owner} {secret} future {{ nested {{ {owner} {secret} retained yes }} }} " +
            $"UserConfig {{ {owner} {secret} }} MountedConfig {{ {owner} {secret} }} " +
            $"InstallScripts {{ {owner} {secret} }} SharedDepots {{ {owner} {secret} }} " +
            $"InstalledDepots {{ 51 {{ manifest 1 size 2 {owner} {secret} }} }}");
        var raw = Parse(text);
        Assert.Equal(secret, raw.Object!.Text(owner));
        Assert.Contains(secret, Encoding.UTF8.GetString(KeyValues.Write(raw)), StringComparison.Ordinal);
        Assert.All(Flatten(raw), entry => Assert.DoesNotContain(secret, entry.ToString(), StringComparison.Ordinal));
        var result = Acf(text);
        var app = Assert.IsType<SteamManifest>(result.Value);
        Assert.Empty(result.Issues);
        Assert.Empty(app.UserConfig!);
        Assert.Empty(app.MountedConfig!);
        Assert.Empty(app.InstallScripts!);
        Assert.Empty(app.SharedDepots!);
        Assert.Empty(app.InstalledDepots![51].UnknownFields);
        Assert.All(app.UnknownFields.SelectMany(Flatten), entry => Assert.False(string.Equals(owner, entry.Key, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(secret, app.ToString(), StringComparison.Ordinal);
        var library = SteamLibraryParser.Parse(Bytes($"libraryfolders {{ {owner} {secret} 0 {{ path /valid future {{ {owner} {secret} }} {owner} {secret} }} }}"), "libraries.vdf");
        Assert.Empty(library.Value!.UnknownFields);
        Assert.Empty(Assert.Single(Assert.Single(library.Value.Libraries).UnknownFields).Object!.Entries);
        foreach (var invalid in new[] { Manifest($"{owner} not-an-id"), Manifest($"{owner} {secret} {owner} {secret}"), Manifest($"{owner} {{ {secret} a {secret} b }}") })
        {
            var rendered = string.Join(";", Acf(invalid).Issues);
            Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("owner", rendered, StringComparison.OrdinalIgnoreCase);
        }

        static IEnumerable<KeyValue> Flatten(KeyValue entry) => new[] { entry }.Concat(entry.Object?.Entries.SelectMany(Flatten) ?? []);
    }

    [Theory]
    [InlineData("\"\" {}")]
    [InlineData("root { \"\" value }")]
    [InlineData("root { child { \"\" {} } }")]
    public void Empty_root_and_object_keys_are_rejected(string text) =>
        Assert.Equal("empty-key", Assert.Throws<KeyValuesException>(() => Parse(text)).Reason);

    [Theory]
    [InlineData("0", "00")]
    [InlineData("1", "01")]
    [InlineData("01", "1")]
    public void Numerically_ambiguous_library_indexes_reject_the_whole_file(string first, string second)
    {
        var text = $"libraryfolders {{ {first} /first {second} /second 2 /other }}";
        var result = SteamLibraryParser.Parse(Bytes(text), "libraries.vdf");
        Assert.Null(result.Value);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("duplicate-library-index", issue.Reason);
        Assert.Equal(first + "/" + second, issue.Field);
        Assert.Equal(text.IndexOf(second + " /second", StringComparison.Ordinal), issue.ByteOffset);
        Assert.Equal(2, SteamLibraryParser.Parse(Bytes("libraryfolders { 0 /first 01 /second }"), "libraries.vdf").Value!.Libraries.Count);
    }

    [Theory]
    [InlineData("InstalledDepots { bad {} }", "bad")]
    [InlineData("InstalledDepots { 51 { manifest nope size 2 } }", "manifest")]
    [InlineData("InstalledDepots { 51 { manifest 1 size nope } }", "size")]
    [InlineData("InstalledDepots { 51 { manifest 1 size 2 dlcappid nope } }", "dlcappid")]
    [InlineData("SharedDepots { bad 42 }", "bad")]
    [InlineData("InstallScripts { 51 {} }", "51")]
    public void Optional_entry_diagnostics_point_at_the_offending_token(string extra, string token)
    {
        var text = Manifest(extra);
        Assert.Equal(Encoding.UTF8.GetByteCount(text[..text.IndexOf(token, StringComparison.Ordinal)]), Assert.Single(Acf(text).Issues).ByteOffset);
    }

    [Fact]
    public void Library_path_diagnostics_point_at_path_and_never_disclose_caller_directories()
    {
        const string text = "libraryfolders { 0 { path relative } 1 /valid }";
        var issue = Assert.Single(SteamLibraryParser.Parse(Bytes(text), "/private/home/libraries.vdf").Issues);
        Assert.Equal(text.IndexOf("path", StringComparison.Ordinal), issue.ByteOffset);
        Assert.Equal("libraries.vdf", issue.File);
        Assert.DoesNotContain("/private", issue.ToString(), StringComparison.Ordinal);
        var syntax = Acf("AppState {", "/private/home/appmanifest_42.acf");
        Assert.Equal("appmanifest_42.acf", Assert.Single(syntax.Issues).File);
        Assert.DoesNotContain("/private", Assert.Throws<KeyValuesException>(() => KeyValues.Parse(Bytes("root {"), "/private/home/file.vdf")).ToString(), StringComparison.Ordinal);
        var malformed = SteamLibraryParser.Parse(Bytes("libraryfolders { 0 { path \"/bad\0path\" } }"), "libraries.vdf");
        Assert.Null(malformed.Value);
        Assert.Equal("control-character", Assert.Single(malformed.Issues).Reason);
    }

    [Theory]
    [InlineData("٤٢")]
    [InlineData("４２")]
    [InlineData("4٢")]
    public void Numeric_domains_accept_only_ascii_digits(string digits)
    {
        Assert.Null(Acf(Manifest(), "appmanifest_" + digits + ".acf").Value);
        var result = Acf(Manifest("buildid " + digits + " SharedDepots { 51 " + digits + " }"));
        Assert.Equal(2, result.Issues.Count);
        Assert.Empty(result.Value!.OptionalNumbers);
        Assert.Empty(result.Value.SharedDepots!);
        var library = SteamLibraryParser.Parse(Bytes("libraryfolders { 0 { path /valid apps { 42 " + digits + " } } }"), "libraries.vdf");
        Assert.Equal("invalid-apps-map", Assert.Single(library.Issues).Reason);
    }

    [Fact]
    public void Block_comment_looking_tokens_are_metadata_not_comments()
    {
        var root = Parse("root { /* retained */ also-retained }");
        Assert.Equal("retained", root.Object!.Text("/*"));
        Assert.Equal("also-retained", root.Object.Text("*/"));
    }

    [Fact]
    public void Fuzz_target_covers_model_rejects_projection_and_non_syntax_contract_failures()
    {
        Assert.IsType<KeyValue>(SteamFuzzTarget.Parse(Bytes(Manifest()), KeyValues.DefaultMaxBytes));
        Assert.IsType<KeyValue>(SteamFuzzTarget.Parse(Bytes(Manifest().Replace("Game", "../escape", StringComparison.Ordinal)), KeyValues.DefaultMaxBytes));
        Assert.IsType<KeyValue>(SteamFuzzTarget.Parse(Bytes("libraryfolders { 0 { path relative } }"), KeyValues.DefaultMaxBytes));
        Assert.Throws<ArgumentOutOfRangeException>(() => SteamFuzzTarget.Parse(Bytes(Manifest()), 0));
        Assert.Throws<ArgumentException>(() => Acf(Manifest()).Value!.ToApp("relative", true, true, false));
    }

    [Fact]
    public void Library_apps_preserve_absent_empty_populated_and_malformed_presence()
    {
        SteamLibrary Library(string extra) => Assert.Single(SteamLibraryParser.Parse(Bytes("libraryfolders { 0 { path /valid " + extra + " } }"), "libraries.vdf").Value!.Libraries);
        Assert.Null(Library("").Apps);
        Assert.Empty(Library("apps {}").Apps!);
        Assert.Equal(2ul, Library("apps { 42 2 }").Apps![42]);
        Assert.Empty(Library("apps scalar").Apps!);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("..\\..\\escape")]
    [InlineData("a\\b/../../escape")]
    [InlineData(".")]
    [InlineData("bad\0path")]
    public void Public_record_construction_and_with_cannot_bypass_install_path_containment(string directory)
    {
        var manifest = Acf(Manifest()).Value! with { InstallDir = directory };
        Assert.Throws<ArgumentException>(() => manifest.ToApp("/synthetic/root", true, true, false));
        var app = Acf(Manifest()).Value!.ToApp("/synthetic/root", true, true, false) with { InstallDir = directory };
        Assert.Throws<ArgumentException>(() => app.InstallPath);
        Assert.Throws<ArgumentException>(() => (app with { InstallDir = "valid", LibraryPath = "relative" }).InstallPath);
    }

    [Theory]
    [InlineData("C:\\escape")]
    [InlineData("\\escape")]
    [InlineData("C:relative")]
    [InlineData("a\\..")]
    public void Legitimate_posix_names_are_not_rejected_by_windows_name_rules(string directory)
    {
        var encoded = directory.Replace("\\", "\\\\", StringComparison.Ordinal);
        var result = Acf(Manifest().Replace("\"Game\"", "\"" + encoded + "\"", StringComparison.Ordinal));
        Assert.Empty(result.Issues);
        var app = Assert.IsType<SteamManifest>(result.Value).ToApp("/synthetic/root", true, true, false);
        Assert.Equal(directory, app.InstallDir);
        Assert.Equal("/synthetic/root/steamapps/common/" + directory, app.InstallPath);
    }

    [Fact]
    public void Writer_enforces_empty_keys_and_the_same_64_object_depth_bound()
    {
        var root = Parse("root {" + string.Concat(Enumerable.Repeat("a {", 63)) + new string('}', 64));
        Assert.NotEmpty(KeyValues.Write(root));
        var wrapper = (KeyValuesObject)Activator.CreateInstance(typeof(KeyValuesObject),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            [new List<KeyValue> { root }], CultureInfo.InvariantCulture)!;
        var exception = Assert.Throws<ArgumentException>(() => KeyValues.Write(new("wrapper", null, wrapper, 0)));
        Assert.Contains("depth-limit", exception.Message, StringComparison.Ordinal);
        Assert.Contains("empty-key", Assert.Throws<ArgumentException>(() => KeyValues.Write(root with { Key = "" })).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fuzz_rejects_unrepresentable_oversized_probe_budgets_before_allocation()
    {
        foreach (var limit in new[] { int.MaxValue, Array.MaxLength })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SteamFuzzTarget.Run(new FuzzOptions
            {
                CorpusRoot = Corpus, MaxInputBytes = limit,
            }, TextWriter.Null));
        }
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("  Synthetic  ")]
    public void Nonempty_names_preserve_whitespace_without_trimming(string name)
    {
        Assert.Equal(name, Acf(Manifest().Replace("\"Synthetic\"", "\"" + name + "\"", StringComparison.Ordinal)).Value!.Name);
    }

    [Theory]
    [InlineData("4294967296")]
    [InlineData("18446744073709551615")]
    [InlineData("18446744073709551616")]
    public void Out_of_uint_app_ids_are_rejected_never_wrapped(string id)
    {
        Assert.Null(Acf(Manifest(), "appmanifest_" + id + ".acf").Value);
        Assert.Null(Acf(Manifest().Replace("appid 42", "appid " + id, StringComparison.Ordinal)).Value);
        var result = SteamLibraryParser.Parse(Bytes("libraryfolders { 0 { path /valid apps { " + id + " 2 } } }"), "libraries.vdf");
        Assert.Empty(Assert.Single(result.Value!.Libraries).Apps!);
        Assert.Equal("invalid-apps-map", Assert.Single(result.Issues).Reason);
    }

    [Theory]
    [InlineData("LastOwner")]
    [InlineData("lastowner")]
    [InlineData("LASTOWNER")]
    public void Private_root_duplicate_keys_never_reveal_account_data(string root)
    {
        const string secret = "76561199999999999";
        var exception = Assert.Throws<KeyValuesException>(() => Parse($"{root} {{ {secret} a {secret} b }}"));
        Assert.Null(exception.Key);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("owner", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Diagnostic_filename_arguments_normalize_trailing_slashes_and_reject_null_bytes()
    {
        Assert.Equal("file.vdf", Assert.Throws<KeyValuesException>(() => KeyValues.Parse(Bytes("root {"), "/private/file.vdf/")).File);
        Assert.Equal("appmanifest_42.acf", Assert.Single(Acf("AppState {", "/private/appmanifest_42.acf/").Issues).File);
        Assert.Equal("file.vdf", Assert.Single(SteamLibraryParser.Parse(Bytes("libraryfolders {"), "/private/file.vdf/").Issues).File);
        Assert.Throws<ArgumentException>(() => KeyValues.Parse(Bytes("root {}"), "/private/\0file.vdf"));
        Assert.Throws<ArgumentException>(() => Acf(Manifest(), "/private/\0appmanifest_42.acf"));
        Assert.Throws<ArgumentException>(() => SteamLibraryParser.Parse(Bytes("libraryfolders {}"), "/private/\0file.vdf"));
    }

    [Theory]
    [InlineData("libraryfolders { 0 { path /valid apps { bad 2 } } }", "bad", "invalid-apps-map")]
    [InlineData("libraryfolders { 0 { path /valid apps { 42 1 } } 1 { path /other apps { 042 2 } } }", "042", "duplicate-appid")]
    public void Library_app_map_diagnostics_point_at_the_offending_member(string text, string token, string reason)
    {
        var issue = Assert.Single(SteamLibraryParser.Parse(Bytes(text), "libraries.vdf").Issues);
        Assert.Equal(reason, issue.Reason);
        Assert.Equal(text.IndexOf(token, StringComparison.Ordinal), issue.ByteOffset);
    }
}
