namespace Playnite.Platform.DesktopIntegration;

public enum DesktopFacility
{
    Tray,
    Notifications,
    SleepInhibition
}

public enum DesktopFacilityState
{
    Available,
    Unavailable
}

/// <summary>
/// One facility's negotiated state. An unavailable capability is a reported fallback, never a
/// startup failure; callers keep running without the facility.
/// </summary>
public sealed record DesktopCapability(
    DesktopFacility Facility,
    DesktopFacilityState State,
    string Mechanism,
    string Detail)
{
    public bool IsAvailable => State == DesktopFacilityState.Available;

    public static DesktopCapability Available(DesktopFacility facility, string mechanism, string detail) =>
        new(facility, DesktopFacilityState.Available, mechanism, detail);

    public static DesktopCapability Unavailable(DesktopFacility facility, string detail) =>
        new(facility, DesktopFacilityState.Unavailable, "none", detail);
}

public sealed record DesktopIntegrationReport(
    DesktopSession Session,
    IReadOnlyList<DesktopCapability> Facilities)
{
    public DesktopCapability For(DesktopFacility facility) => Facilities.Single(item => item.Facility == facility);
}

/// <summary>Read-only startup report; probing never throws for an unavailable facility.</summary>
public interface IDesktopIntegrationDiagnostics
{
    Task<DesktopIntegrationReport> DiagnoseAsync(CancellationToken cancellationToken = default);
}
