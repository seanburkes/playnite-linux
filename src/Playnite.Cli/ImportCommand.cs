using System.Diagnostics;
using System.Text.Json;
using Playnite.Core.Import;

namespace Playnite.Cli;

internal static class ImportCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length < 3 || args.Skip(3).Any(a => a != "--dry-run" && !a.StartsWith("--pause-at=", StringComparison.Ordinal)))
                throw new ArgumentException("Usage: Playnite.Cli import-v10 <reader.dll> <v10-library> <destination.sqlite> [--dry-run] [--pause-at=STATE]");
            var source = Path.GetFullPath(args[1]);
            var destination = Path.GetFullPath(args[2]);
            RejectLinks(source);
            RejectLinks(Path.GetDirectoryName(destination)!);
            if (destination == source || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException("Destination and migration artifacts must be outside the source directory.");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.GetFullPath(args[0]));
            start.ArgumentList.Add(source);
            using var process = Process.Start(start) ?? throw new IOException("Cannot start legacy reader.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var intermediate = await output;
            var error = await errors;
            if (process.ExitCode != 0) throw new InvalidDataException($"Legacy reader failed: {error}");
            var pause = args.Skip(3).FirstOrDefault(a => a.StartsWith("--pause-at=", StringComparison.Ordinal))?[11..];
            var report = await LibraryMigration.RunAsync(intermediate, source, destination, args.Contains("--dry-run", StringComparer.Ordinal), state =>
            {
                if (state == pause)
                {
                    Console.Error.WriteLine($"checkpoint:{state}");
                    // Fault-injection rendezvous: tests kill the process rather than throw.
                    Console.In.ReadLine();
                }
            });
            Console.WriteLine(JsonSerializer.Serialize(report));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void RejectLinks(string path)
    {
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic-link source/destination ancestors are not supported.");
    }
}
