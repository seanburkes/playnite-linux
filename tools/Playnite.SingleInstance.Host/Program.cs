using Playnite.Platform.Linux;
using Playnite.Platform.Linux.Integration;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: Playnite.SingleInstance.Host <instance-id> <runtime-dir>");
    return 2;
}

Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", args[1]);
var directories = XdgDirectories.Resolve(new LinuxEnvironment());
await using var service = SingleInstanceService.Start(args[0], directories);
if (!service.Result.IsOwner)
{
    Console.WriteLine($"not-owner: {service.Result.Diagnostic}");
    return 3;
}

Console.WriteLine("ready");
Console.Out.Flush();
await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;
