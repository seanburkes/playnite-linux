using Playnite.Platform.DesktopIntegration;

namespace Playnite.Platform.Linux.DesktopIntegration;

/// <summary>
/// Tries inhibitor backends in order. An unavailable facility still returns a lease, so game start
/// is never blocked by missing logind or a missing systemd-inhibit binary.
/// </summary>
public sealed class LinuxSleepInhibitionService : ISleepInhibitionService
{
    private readonly IReadOnlyList<ISleepInhibitorBackend> backends;

    public LinuxSleepInhibitionService(IEnumerable<ISleepInhibitorBackend> backends) =>
        this.backends = (backends ?? throw new ArgumentNullException(nameof(backends))).ToArray();

    public static LinuxSleepInhibitionService CreateDefault(IExecutableLocator? locator = null) =>
        new([new SystemdInhibitBackend(locator ?? new LinuxExecutableLocator())]);

    public async Task<SleepInhibitionLease> InhibitAsync(
        SleepInhibitionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var failures = new List<string>();
        foreach (var backend in backends)
        {
            if (!backend.IsAvailable)
            {
                failures.Add($"{backend.Mechanism}: not available");
                continue;
            }

            SleepInhibitorAcquireResult result;
            try
            {
                result = await backend.TryAcquireAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add($"{backend.Mechanism}: {exception.Message}");
                continue;
            }

            if (result.Handle is not null)
            {
                return SleepInhibitionLease.Held(
                    DesktopCapability.Available(
                        DesktopFacility.SleepInhibition,
                        backend.Mechanism,
                        result.Detail),
                    result.Handle);
            }

            failures.Add($"{backend.Mechanism}: {result.Detail}");
        }

        return SleepInhibitionLease.NotHeld(
            DesktopCapability.Unavailable(
                DesktopFacility.SleepInhibition,
                failures.Count == 0
                    ? "no sleep inhibitor backend is configured"
                    : string.Join("; ", failures)));
    }
}
