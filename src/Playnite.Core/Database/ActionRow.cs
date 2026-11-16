using Playnite.Core.Models;

namespace Playnite.Core.Database;

internal sealed class ActionRow
{
    public int Type { get; set; }
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string? Arguments { get; set; }
    public string? WorkingDir { get; set; }
    public string? AdditionalArguments { get; set; }
    public bool OverrideDefaultArgs { get; set; }
    public bool IsPlayAction { get; set; }
    public string EmulatorId { get; set; } = "";
    public string? EmulatorProfileId { get; set; }
    public int TrackingMode { get; set; }
    public string? TrackingPath { get; set; }
    public string? Script { get; set; }
    public int InitialTrackingDelay { get; set; }
    public int TrackingFrequency { get; set; }

    internal GameAction ToAction() => new()
    {
        Type = (GameActionType)Type, Name = Name, Path = Path, Arguments = Arguments, WorkingDir = WorkingDir,
        AdditionalArguments = AdditionalArguments, OverrideDefaultArgs = OverrideDefaultArgs, IsPlayAction = IsPlayAction,
        EmulatorId = Guid.Parse(EmulatorId), EmulatorProfileId = EmulatorProfileId, TrackingMode = (TrackingMode)TrackingMode,
        TrackingPath = TrackingPath, Script = Script, InitialTrackingDelay = InitialTrackingDelay, TrackingFrequency = TrackingFrequency,
    };
}
