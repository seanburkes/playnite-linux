using Dapper;
using Playnite.Core.Models;

namespace Playnite.Core.Database;

/// <summary>A v10 metadata value and its uninterpreted, preserved unknown fields.</summary>
public sealed record StoredMetadata<T>(T Value, string LegacyJson = "{}") where T : DatabaseObject;

/// <summary>CRUD for v10 named metadata; deleting referenced metadata fails rather than dropping relations.</summary>
public sealed class MetadataRepository<T> where T : DatabaseObject
{
    private readonly LibrarySession session;
    private readonly string table;

    internal MetadataRepository(LibrarySession session)
    {
        this.session = session;
        // Identifiers are a closed source-code whitelist, never caller-supplied SQL.
        table = typeof(T).Name switch
        {
            nameof(Tag) when typeof(T) == typeof(Tag) => "tag",
            nameof(Genre) when typeof(T) == typeof(Genre) => "genre",
            nameof(Company) when typeof(T) == typeof(Company) => "company",
            nameof(GameFeature) when typeof(T) == typeof(GameFeature) => "feature",
            nameof(Series) when typeof(T) == typeof(Series) => "series",
            nameof(Category) when typeof(T) == typeof(Category) => "category",
            nameof(GameSource) when typeof(T) == typeof(GameSource) => "source",
            nameof(AgeRating) when typeof(T) == typeof(AgeRating) => "age_rating",
            nameof(Region) when typeof(T) == typeof(Region) => "region",
            nameof(CompletionStatus) when typeof(T) == typeof(CompletionStatus) => "completion_status",
            nameof(Platform) when typeof(T) == typeof(Platform) => "platform",
            _ => throw new ArgumentException($"{typeof(T).Name} is not library metadata."),
        };
    }

    /// <summary>Inserts a metadata value; duplicate identity is an error.</summary>
    public async Task AddAsync(StoredMetadata<T> item)
    {
        var row = ToRow(item);
        var sql = table == "platform"
            ? "INSERT INTO platform(Id, Name, SpecificationId, Icon, Cover, Background, LegacyJson) VALUES (@Id, @Name, @SpecificationId, @Icon, @Cover, @Background, @LegacyJson);"
            : $"INSERT INTO {table}(Id, Name, LegacyJson) VALUES (@Id, @Name, @LegacyJson);";
        await session.Connection.ExecuteAsync(session.Command(sql, row)).ConfigureAwait(false);
    }

    /// <summary>Replaces a metadata value; missing identity is an error.</summary>
    public async Task UpdateAsync(StoredMetadata<T> item)
    {
        var row = ToRow(item);
        var sql = table == "platform"
            ? "UPDATE platform SET Name=@Name, SpecificationId=@SpecificationId, Icon=@Icon, Cover=@Cover, Background=@Background, LegacyJson=@LegacyJson WHERE Id=@Id;"
            : $"UPDATE {table} SET Name=@Name, LegacyJson=@LegacyJson WHERE Id=@Id;";
        if (await session.Connection.ExecuteAsync(session.Command(sql, row)).ConfigureAwait(false) != 1)
        {
            throw new KeyNotFoundException($"Metadata {item.Value.Id} was not found.");
        }
    }

    /// <summary>Reads one identity, or null if absent.</summary>
    public async Task<StoredMetadata<T>?> GetAsync(Guid id)
    {
        var row = await session.Connection.QuerySingleOrDefaultAsync<MetadataRow>(session.Command($"SELECT * FROM {table} WHERE Id=@Id;", new { Id = id.ToString("D") })).ConfigureAwait(false);
        return row == null ? null : FromRow(row);
    }

    /// <summary>Reads all values ordered by canonical identity.</summary>
    public async Task<IReadOnlyList<StoredMetadata<T>>> GetAllAsync()
    {
        var rows = await session.Connection.QueryAsync<MetadataRow>(session.Command($"SELECT * FROM {table} ORDER BY Id;")).ConfigureAwait(false);
        return rows.Select(FromRow).ToArray();
    }

    /// <summary>Deletes a value, returning false if absent.</summary>
    public async Task<bool> DeleteAsync(Guid id)
        => await session.Connection.ExecuteAsync(session.Command($"DELETE FROM {table} WHERE Id=@Id;", new { Id = id.ToString("D") })).ConfigureAwait(false) == 1;

    private static MetadataRow ToRow(StoredMetadata<T> item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(item.Value);
        StorageValues.RequireIdentity(item.Value.Id);
        var platform = item.Value as Platform;
        return new MetadataRow
        {
            Id = item.Value.Id.ToString("D"), Name = item.Value.Name, LegacyJson = item.LegacyJson,
            SpecificationId = platform?.SpecificationId, Icon = platform?.Icon, Cover = platform?.Cover, Background = platform?.Background,
        };
    }

    private StoredMetadata<T> FromRow(MetadataRow row)
    {
        var id = Guid.Parse(row.Id);
        DatabaseObject value = table switch
        {
            "tag" => new Tag(id), "genre" => new Genre(id), "company" => new Company(id),
            "feature" => new GameFeature(id), "series" => new Series(id), "category" => new Category(id),
            "source" => new GameSource(id), "age_rating" => new AgeRating(id), "region" => new Region(id),
            "completion_status" => new CompletionStatus(id),
            "platform" => new Platform(id) { SpecificationId = row.SpecificationId, Icon = row.Icon, Cover = row.Cover, Background = row.Background },
            _ => throw new InvalidOperationException("Unrecognized metadata table."),
        };
        value.Name = row.Name;
        return new StoredMetadata<T>((T)value, row.LegacyJson);
    }

    private sealed class MetadataRow
    {
        public string Id { get; set; } = "";
        public string? Name { get; set; }
        public string LegacyJson { get; set; } = "{}";
        public string? SpecificationId { get; set; }
        public string? Icon { get; set; }
        public string? Cover { get; set; }
        public string? Background { get; set; }
    }
}
