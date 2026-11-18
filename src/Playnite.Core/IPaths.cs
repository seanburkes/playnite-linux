namespace Playnite.Core;

/// <summary>Startup-captured directories. Reading these properties never creates directories.</summary>
public interface IPaths
{
    string Home { get; }
    string Documents { get; }
    string Program { get; }
    string UserData { get; }
    string Config { get; }
    string Cache { get; }
    string State { get; }
    string Logs { get; }
    /// <summary>
    /// Ordered probes, not discovered libraries; see the Steam discovery contract. The explicit
    /// <c>STEAM_ROOT</c> override is kept verbatim even when it is not absolute, so discovery can
    /// report it invalid instead of silently dropping it. Absolute entries are lexically
    /// normalized and an exact duplicate appears once.
    /// </summary>
    IReadOnlyList<string> SteamRootCandidates { get; }
}
