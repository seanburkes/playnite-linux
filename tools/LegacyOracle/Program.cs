using System;
using System.IO;
using System.Linq;
using Playnite;
using Playnite.Common;

namespace LegacyOracle
{
    /// <summary>
    /// Windows-only oracle harness. It calls existing v10 public APIs and writes their
    /// output to stdout. It does not define a new export format or normalise bytes.
    /// </summary>
    public static class Program
    {
        private static readonly string[] SettingsFiles =
        {
            PlaynitePaths.ConfigFileName,
            PlaynitePaths.FullscreenConfigFileName,
            PlaynitePaths.WindowPositionsFileName
        };

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "settings-bytes" || args[0] == "library-export"))
            {
                if (args.Length != 3)
                {
                    Console.Error.WriteLine("usage: LegacyOracle <settings-bytes|library-export> <synthetic-source-directory> <new-scratch-directory>");
                    return 2;
                }

                try
                {
                    var source = Path.GetFullPath(args[1]);
                    var destination = Path.GetFullPath(args[2]);
                    ValidateDirectories(source, destination);
                    if (args[0] == "settings-bytes")
                    {
                        CaptureSettings(source, destination);
                    }
                    else
                    {
                        CopyDirectory(source, destination);
                    }

                    return 0;
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error.Message);
                    return 1;
                }
            }

            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: LegacyOracle <manifest|theme|raw> <path>");
                return 2;
            }

            var mode = args[0];
            var path = args.Length > 1 ? args[1] : null;
            if (path == null || !File.Exists(path))
            {
                Console.Error.WriteLine($"file not found: {path}");
                return 2;
            }

            switch (mode)
            {
                case "manifest":
                    Console.Out.Write(Serialization.ToYaml(ExtensionManifest.FromFile(path)));
                    return 0;
                case "theme":
                    Console.Out.Write(Serialization.ToYaml(new ThemeManifest(path)));
                    return 0;
                case "raw":
                    using (var stream = File.OpenRead(path))
                    {
                        stream.CopyTo(Console.OpenStandardOutput());
                    }
                    return 0;
                default:
                    Console.Error.WriteLine($"unknown mode: {mode}");
                    return 2;
            }
        }

        private static void ValidateDirectories(string source, string destination)
        {
            if (!Directory.Exists(source))
            {
                throw new DirectoryNotFoundException("synthetic source directory not found");
            }

            if (Directory.Exists(destination) || File.Exists(destination))
            {
                throw new IOException("scratch destination must not exist");
            }

            var sourcePrefix = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var destinationPrefix = destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (destination.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("scratch destination must not overlap the source directory");
            }

            RejectLinkedAncestors(source);
            RejectLinkedAncestors(destination);
        }

        private static void RejectLinkedAncestors(string path)
        {
            for (var current = path; current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException("linked files and directories are not supported");
                }
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            RejectLinkedAncestors(source);
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
            {
                RejectLinkedAncestors(file);
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }

            foreach (var directory in Directory.GetDirectories(source))
            {
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }

        private static void CaptureSettings(string source, string destination)
        {
            foreach (var name in SettingsFiles)
            {
                RejectLinkedAncestors(Path.Combine(source, name));
            }

            Directory.CreateDirectory(destination);
            foreach (var name in SettingsFiles)
            {
                var path = Path.Combine(source, name);
                RejectLinkedAncestors(path);
                File.Copy(path, Path.Combine(destination, name));
            }

            PlaynitePaths.UpdateUserDataDir(destination);
            // Validate only scratch files before the v10 loader can fall back to defaults.
            if (Serialization.FromJsonFile<PlayniteSettings>(Path.Combine(destination, SettingsFiles[0])) == null ||
                Serialization.FromJsonFile<FullscreenSettings>(Path.Combine(destination, SettingsFiles[1])) == null ||
                Serialization.FromJsonFile<WindowPositions>(Path.Combine(destination, SettingsFiles[2])) == null)
            {
                throw new InvalidDataException("all three synthetic settings files must contain settings objects");
            }

            var settings = PlayniteSettings.LoadSettings();
            // SaveSettings can swallow errors. Remove only our scratch copies so stale input
            // cannot be mistaken for successfully emitted bytes.
            foreach (var name in SettingsFiles)
            {
                File.Delete(Path.Combine(destination, name));
            }

            settings.SaveSettings();
            if (SettingsFiles.Any(name => !File.Exists(Path.Combine(destination, name)) ||
                new FileInfo(Path.Combine(destination, name)).Length == 0))
            {
                throw new IOException("v10 did not write all three settings files");
            }
        }
    }
}
