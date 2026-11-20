using Playnite.Core.Metadata;

namespace Playnite.Core.Tests.Metadata;

internal sealed class FakeMetadataProvider : IMetadataProvider
{
    private readonly Func<MetadataQuery, CancellationToken, Task<MetadataProviderResult>> handler;

    public FakeMetadataProvider(
        string identity = "fake",
        Func<MetadataQuery, CancellationToken, Task<MetadataProviderResult>>? handler = null)
    {
        Identity = identity;
        this.handler = handler ?? ((_, _) => Task.FromResult(
            MetadataProviderResult.Failure("not-configured")));
    }

    public string Identity { get; }

    public List<MetadataQuery> Calls { get; } = [];

    public int CallCount => Calls.Count;

    public Task<MetadataProviderResult> FetchAsync(
        MetadataQuery query,
        CancellationToken cancellationToken)
    {
        Calls.Add(query);
        return handler(query, cancellationToken);
    }

    public static FakeMetadataProvider Returning(string identity, MetadataProviderResult result)
    {
        return new FakeMetadataProvider(identity, (_, _) => Task.FromResult(result));
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan delta) => UtcNow += delta;
}
