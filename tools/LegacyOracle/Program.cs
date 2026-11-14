using System;
using System.IO;
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
        public static int Main(string[] args)
        {
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
    }
}
