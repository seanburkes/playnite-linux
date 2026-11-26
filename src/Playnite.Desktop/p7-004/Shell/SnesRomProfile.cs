using System.Security.Cryptography;
using System.Text;
using Playnite.Core.Models;

namespace Playnite.Desktop.Shell;

internal sealed record SnesRomProfile(Guid Id, string Name, string Executable)
{
    internal static readonly Guid SourceId = Guid.Parse("2f08d96b-e99c-45ed-915a-068bf5188db1");
    internal static readonly SnesRomProfile Standalone = new(
        Guid.Parse("925202f3-699c-413f-8a75-ec4c687d533e"), "Snes9x", "snes9x-gtk");
    internal static readonly SnesRomProfile RetroArch = new(
        Guid.Parse("85135189-e2d2-4171-a64c-f703cd12d783"), "RetroArch (Snes9x)", "retroarch");

    internal static Guid LibraryId(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{SourceId:D}:{path}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    internal static string[] Scan(string folder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
        };
        var paths = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.GetFullPath(folder), "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(path);
            if (extension.Equals(".sfc", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".smc", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(path);
            }
        }

        paths.Sort(StringComparer.Ordinal);
        return paths.ToArray();
    }

    internal GameAction CreateAction(string? core) => new()
    {
        Name = Name,
        Type = GameActionType.Emulator,
        IsPlayAction = true,
        EmulatorId = Id,
        Path = core,
    };

    internal static GameAction? FindAction(Game game) => game.PluginId == SourceId
        ? game.GameActions?.FirstOrDefault(action =>
            action is { Type: GameActionType.Emulator, IsPlayAction: true }
            && (action.EmulatorId == Standalone.Id || action.EmulatorId == RetroArch.Id))
        : null;

    internal static LaunchPlanResult Plan(Game game, GameAction action, Func<string, string?>? resolver = null)
    {
        var profile = action.EmulatorId == RetroArch.Id ? RetroArch : Standalone;
        var rom = game.Roms?.FirstOrDefault()?.Path;
        if (!Path.IsPathFullyQualified(rom ?? string.Empty) || !File.Exists(rom))
        {
            return new(null, "The SNES ROM file could not be found.");
        }

        var executable = GameLaunchPlanner.ResolveToolPath(profile.Executable, resolver)
            ?? (profile == Standalone ? GameLaunchPlanner.ResolveToolPath("snes9x", resolver) : null);
        if (executable is null)
        {
            return new(null, $"Install {profile.Name} to launch this SNES game.");
        }

        var core = action.Path;
        if (profile == RetroArch)
        {
            if (!Path.IsPathFullyQualified(core ?? string.Empty) || !File.Exists(core)
                || Path.GetFileName(core) != "snes9x_libretro.so")
            {
                return new(null, "The RetroArch Snes9x core could not be found. Rescan using its installed location.");
            }
        }

        var arguments = profile == RetroArch ? new[] { "-L", core!, rom! } : new[] { rom! };
        var plan = GameLaunchPlanner.CreatePlayAction(executable, GameLaunchPlanner.FormatArguments(arguments), null, false, false);
        return new(plan.Request, plan.Error);
    }
}
