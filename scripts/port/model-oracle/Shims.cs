// Compile-only seams for the full frozen Game.cs. The probe never traverses a database
// or localizes a resource. Throwing here makes accidental use fail, not fake success.
using Playnite.SDK.Models;

namespace Playnite.SDK
{
    internal static class ResourceProvider
    {
        internal static string GetString(string key) => throw new NotSupportedException(key);
    }

    internal interface IGameDatabase
    {
        OracleCollection<Genre> Genres { get; }
        OracleCollection<Company> Companies { get; }
        OracleCollection<Tag> Tags { get; }
        OracleCollection<GameFeature> Features { get; }
        OracleCollection<Category> Categories { get; }
        OracleCollection<Platform> Platforms { get; }
        OracleCollection<Series> Series { get; }
        OracleCollection<AgeRating> AgeRatings { get; }
        OracleCollection<Region> Regions { get; }
        OracleCollection<GameSource> Sources { get; }
        OracleCollection<CompletionStatus> CompletionStatuses { get; }
    }

    internal sealed class OracleCollection<T>
    {
        public T this[Guid id] => throw new NotSupportedException();
        public IEnumerable<T> Get(IEnumerable<Guid> ids) => throw new NotSupportedException();
    }
}

namespace Playnite.SDK.Data
{
    // The marker from frozen Data/DataSerialization.cs; no serializer is exercised here.
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    internal sealed class DontSerializeAttribute : Attribute { }
}
