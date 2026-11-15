using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
#if V10_MODELS
using Playnite.SDK.Models;
using PlatformModel = Playnite.SDK.Models.Platform;
#else
using Playnite.Core.Models;
using PlatformModel = Playnite.Core.Models.Platform;
#endif

namespace Playnite.Core.Tests.Models;

// Compiled twice: against the frozen sources by generate_model_goldens.py, and Core by xUnit.
// No expected results live here. The only conditional behavior is identity construction.
internal static class ModelProbe
{
    private static readonly Guid Id = new("12345678-1234-5678-90ab-cdef01234567");
    private static readonly Guid OtherId = new("87654321-4321-8765-ba09-fedc76543210");

    internal static Game NewGame()
    {
#if V10_MODELS
        return new Game { Id = Id, GameId = "provider" };
#else
        return new Game(Id, "provider");
#endif
    }

    private static DatabaseObject Entity(Type type, Guid id)
    {
#if V10_MODELS
        var value = (DatabaseObject)Activator.CreateInstance(type)!;
        value.Id = id;
        return value;
#else
        return (DatabaseObject)Activator.CreateInstance(type, id)!;
#endif
    }

    internal static SortedDictionary<string, string> Observe()
    {
        var rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Add(string key, object? value) => rows.Add(key, Format(value));
        void Hash(string key, object value)
        {
            Add(key + ".stable", value.GetHashCode() == value.GetHashCode());
            Add(key + ".implementation", value.GetType().GetMethod(nameof(GetHashCode))!.DeclaringType!.Name);
        }

        Type[] entities = [typeof(DatabaseObject), typeof(Game), typeof(AgeRating), typeof(Category),
            typeof(Company), typeof(Developer), typeof(Publisher), typeof(CompletionStatus),
            typeof(GameFeature), typeof(GameSource), typeof(Genre), typeof(PlatformModel), typeof(Region),
            typeof(Series), typeof(Tag)];
        foreach (var type in entities)
        {
            var a = type == typeof(Game) ? NewGame() : Entity(type, Id);
            var b = type == typeof(Game) ? NewGame() : Entity(type, Id);
            a.Name = "one";
            b.Name = "different";
            var key = "entity." + type.Name;
            Add(key + ".different-reference", !ReferenceEquals(a, b));
            Add(key + ".equal-id", a.Equals(b));
            Add(key + ".operator", a == b);
            Add(key + ".cross-type", a.Equals(Entity(typeof(Category), Id)));
            Add(key + ".hash-equal", a.GetHashCode() == b.GetHashCode());
            Add(key + ".wrong-type", a.Equals("one"));
            b.Id = OtherId;
            Add(key + ".different-id", a.Equals(b));
            b.Id = Guid.Empty;
            Add(key + ".empty-hash", b.GetHashCode());
            Hash(key + ".hash", a);
            Add(key + ".null", a.Equals(null));
        }

        var dbo = Entity(typeof(DatabaseObject), Id);
        Add("entity.null-name-string", dbo.ToString());
        Add("entity.null-compare", ExceptionName(() => dbo.CompareTo(null!)));
        Add("entity.wrong-compare", ExceptionName(() => dbo.CompareTo("wrong")));
        var named = Entity(typeof(DatabaseObject), OtherId);
        named.Name = "name";
        Add("entity.empty-sorts-last", Math.Sign(dbo.CompareTo(named)));
        dbo.Name = "NAME";
        Add("entity.case-insensitive-compare", dbo.CompareTo(named));
        Add("copy.self-error", ExceptionName(() => dbo.CopyDiffTo(dbo)));
        Add("copy.null-error", ExceptionName(() => dbo.CopyDiffTo(null!)));
        Add("copy.type-error", ExceptionName(() => dbo.CopyDiffTo("wrong")));

        var link = new Link("Name", "https://example.invalid");
        var rom = new GameRom("Name", "/games/a.rom");
        var action = new GameAction { Type = GameActionType.Emulator, TrackingMode = TrackingMode.ProcessName,
            Name = "Play", EmulatorId = Id, EmulatorProfileId = "profile", TrackingFrequency = 50 };
        Typed("link", link, link.GetCopy());
        Typed("rom", rom, rom.GetCopy());
        Typed("action", action, action.GetCopy());
        Add("action.static-both-null", GameAction.Equals(null!, null!));
        Add("action.static-left-null", GameAction.Equals(null!, action));
        Add("action.static-right-null", GameAction.Equals(action, null!));
        Add("action.static-value", GameAction.Equals(action, action.GetCopy()));
        foreach (var type in new[] { typeof(Link), typeof(GameRom), typeof(GameAction) })
        {
            foreach (var p in Writable(type))
            {
                var a = Activator.CreateInstance(type)!;
                var b = Activator.CreateInstance(type)!;
                var eq = type.GetMethod("Equals", [type])!;
                p.SetValue(b, p.PropertyType == typeof(bool) ? !(bool)p.GetValue(b)! : Sample(p.PropertyType));
                Add($"typed.{type.Name}.{p.Name}.differs", eq.Invoke(a, [b]));
            }
        }

        void Typed<T>(string key, T a, T b) where T : class, IEquatable<T>
        {
            Add(key + ".typed-equal", a.Equals(b));
            Add(key + ".object-equal", ((object)a).Equals(b));
            Add(key + ".operator", a == b);
            Add(key + ".sequence", Enumerable.SequenceEqual(new[] { a }, new[] { b }));
            Hash(key + ".hash", a);
            Add(key + ".null", a.Equals(null));
        }

        MetadataProperty[] metadata = [new MetadataNameProperty("Name"), new MetadataSpecProperty("Name"),
            new MetadataIdProperty(Id)];
        foreach (var a in metadata)
        {
            var b = (MetadataProperty)Activator.CreateInstance(a.GetType(), a is MetadataIdProperty ? Id : "Name")!;
            var key = "metadata." + a.GetType().Name;
            Add(key + ".equal", a.Equals(b));
            Add(key + ".operator", a == b);
            Add(key + ".wrong-type", a.Equals("Name"));
            Add(key + ".different-reference", !ReferenceEquals(a, b));
            Add(key + ".hash-equal", a.GetHashCode() == b.GetHashCode());
            Add(key + ".set-count", new HashSet<MetadataProperty> { a, b }.Count);
            Add(key + ".text", a.ToString());
            Hash(key + ".hash", a);
            Add(key + ".null", a.Equals(null));
        }
        Add("metadata.cross-type", metadata[0].Equals(metadata[1]));
        Add("metadata.case-sensitive", metadata[0].Equals(new MetadataNameProperty("name")));
        foreach (var text in new string?[] { null, "", " ", "\t" })
        {
            var key = "metadata.invalid." + (text == null ? "null" : Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(text)));
            Add(key + ".name", ExceptionName(() => new MetadataNameProperty(text!)));
            Add(key + ".spec", ExceptionName(() => new MetadataSpecProperty(text!)));
        }
        Add("metadata.empty-id", ExceptionName(() => new MetadataIdProperty(Guid.Empty)));
        Add("metadata.mixed-set", new HashSet<MetadataProperty>(metadata).Count);

        var year = new ReleaseDate(2024);
        var month = new ReleaseDate(2024, 1);
        var day = new ReleaseDate(2024, 1, 1);
        Add("release.precision-compare", year.CompareTo(day));
        Add("release.precision-equal", year.Equals(day));
        Add("release.month-equal", year == month);
        Add("release.day-equal", day == new ReleaseDate(new DateTime(2024, 1, 1)));
        Add("release.not-equal", year != day);
        Add("release.boxed-equal", ((object)day).Equals(new ReleaseDate(2024, 1, 1)));
        Add("release.null-equal", day.Equals(null));
        Add("release.null-compare", day.CompareTo(null));
        Add("release.empty-default", ReleaseDate.Empty == default);
        Add("release.xor-collision", new ReleaseDate(2024, 1, 2).GetHashCode() == new ReleaseDate(2024, 2, 1).GetHashCode());
        foreach (var value in new[] { ReleaseDate.Empty, year, month, day })
        {
            Add("release.hash." + value.Serialize(), value.GetHashCode());
            Hash("release.hash." + value.Serialize(), value);
            Add("release.roundtrip." + value.Serialize(), ReleaseDate.Deserialize(value.Serialize()) == value);
        }
        ReleaseDate? absent = null;
        Add("release.nullable-both-null", absent == null);
        Add("release.nullable-empty", absent == ReleaseDate.Empty);

        foreach (var p in Writable(typeof(Game)))
        {
            var a = NewGame();
            var b = NewGame();
            p.SetValue(b, p.PropertyType == typeof(bool) ? !(bool)p.GetValue(b)! : Sample(p.PropertyType));
            Add("game.diff." + p.Name, string.Join(",", a.GetDifferences(b)));
            var copy = b.GetCopy();
            Add("game.copy." + p.Name, Format(p.GetValue(copy)));
            Add("game.default." + p.Name, Format(p.GetValue(a)));
            if (p.GetValue(b) is IList original)
            {
                Add("game.copy-distinct." + p.Name, !ReferenceEquals(original, p.GetValue(copy)));
                if (original.Count > 0 && original[0] is not Guid)
                    Add("game.copy-item-distinct." + p.Name, !ReferenceEquals(original[0], ((IList)p.GetValue(copy)!)[0]));
            }
            b.CopyDiffTo(a);
            Add("game.copydiff." + p.Name, Format(p.GetValue(a)));
            if (p.GetValue(b) is IList)
                Add("game.copydiff-shares." + p.Name, ReferenceEquals(p.GetValue(a), p.GetValue(b)));
        }
        var first = NewGame();
        var second = NewGame();
        first.GenreIds = [Id, Id, OtherId];
        second.GenreIds = [Id, OtherId, OtherId];
        Add("game.diff.duplicate-multiplicity", string.Join(",", first.GetDifferences(second)));
        second.GenreIds = [OtherId, Id, Id];
        Add("game.diff.id-order", string.Join(",", first.GetDifferences(second)));
        first.Links = [link, new Link("Other", "other")];
        second.Links = [first.Links[1].GetCopy(), link.GetCopy()];
        Add("game.diff.link-order", string.Join(",", first.GetDifferences(second)));
        first.Links = null!;
        second.Links = [];
        Add("game.diff.null-empty", string.Join(",", first.GetDifferences(second)));
        Add("game.diff.null-error", ExceptionName(() => first.GetDifferences(null!)));

        foreach (var p in Writable(typeof(GameMetadata)))
        {
            var value = new GameMetadata();
            Add("gamemetadata.default." + p.Name, Format(p.GetValue(value)));
            p.SetValue(value, Sample(p.PropertyType));
            Add("gamemetadata.shape." + p.Name, Format(p.GetValue(value)));
        }
        TypedReference("gamemetadata", new GameMetadata { Name = "same" }, new GameMetadata { Name = "same" });
        var file = new MetadataFile("image.png", [], "https://example.invalid/image.png");
        TypedReference("metadatafile", file, new MetadataFile("image.png", [], file.Path));
        Add("metadatafile.empty-bytes-content", file.HasContent);
        Add("metadatafile.has-image", file.HasImageData);
        file.FileName = null!;
        Add("metadatafile.null-name-content", file.HasContent);
        Add("metadatafile.path-only-image", file.HasImageData);
        Add("metadatafile.default-content", new MetadataFile().HasContent);
        Add("metadatafile.default-image", new MetadataFile().HasImageData);

        void TypedReference(string key, object a, object b)
        {
            Add(key + ".object-equal", a.Equals(b));
            Hash(key + ".hash", a);
        }

        foreach (var type in new[] { typeof(GameField), typeof(GameActionType), typeof(TrackingMode),
            typeof(AgeRatingOrg), typeof(ScoreRating), typeof(ScoreGroup), typeof(PastTimeSegment),
            typeof(PlaytimeCategory), typeof(InstallSizeGroup), typeof(Playnite.SDK.Models.InstallationStatus) })
        {
            foreach (var name in Enum.GetNames(type))
                Add("enum." + type.Name + "." + name, Convert.ToInt32(Enum.Parse(type, name), CultureInfo.InvariantCulture));
        }
        return rows;
    }

    private static IEnumerable<PropertyInfo> Writable(Type type) => type.GetProperties()
        .Where(p => p.CanWrite && p.SetMethod!.IsPublic).OrderBy(p => p.Name, StringComparer.Ordinal);

    private static object Sample(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return "changed";
        if (type == typeof(bool)) return true;
        if (type == typeof(Guid)) return OtherId;
        if (type == typeof(int)) return 7;
        if (type == typeof(ulong)) return 42UL;
        if (type == typeof(DateTime)) return new DateTime(2000, 1, 2);
        if (type == typeof(ReleaseDate)) return new ReleaseDate(2000, 1, 2);
        if (type.IsEnum) return Enum.ToObject(type, 1);
        if (type == typeof(MetadataProperty)) return new MetadataNameProperty("changed");
        if (type == typeof(MetadataFile)) return new MetadataFile("image.png", [1, 2]);
        if (type == typeof(GameAction)) return new GameAction { Name = "changed" };
        if (type == typeof(Link)) return new Link("changed", "https://example.invalid");
        if (type == typeof(GameRom)) return new GameRom("changed", "/games/a.rom");
        if (type.IsGenericType)
        {
            var instance = Activator.CreateInstance(type)!;
            type.GetMethod("Add")!.Invoke(instance, [Sample(type.GetGenericArguments()[0])]);
            return instance;
        }
        throw new NotSupportedException(type.FullName);
    }

    private static string Format(object? value) => value switch
    {
        null => "<null>",
        string s => s,
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        ReleaseDate date => date.Serialize(),
        MetadataFile file => $"{file.FileName}|{file.Path}|{Format(file.Content)}",
        Link link => $"{link.Name}|{link.Url}",
        GameRom rom => $"{rom.Name}|{rom.Path}",
        IEnumerable items => "[" + string.Join(";", items.Cast<object?>().Select(Format)) + "]",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "<null>"
    };

    private static string ExceptionName(Action action)
    {
        try { action(); return "none"; }
        catch (Exception e) { return e.GetType().Name + (e is ArgumentException a ? ":" + a.ParamName : ""); }
    }
}
