namespace Playnite.SDK.Events;

/// <summary>Arguments passed to a registered plugin URI source, mirroring the v10 shape.</summary>
public class PlayniteUriEventArgs
{
    /// <summary>Gets or sets the decoded arguments after the source segment.</summary>
    public string[] Arguments { get; set; } = [];
}
