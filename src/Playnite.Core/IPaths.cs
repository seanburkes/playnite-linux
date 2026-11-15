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
    /// <summary>Ordered probes, not discovered libraries; see the Steam discovery contract.</summary>
    IReadOnlyList<string> SteamRootCandidates { get; }
}
