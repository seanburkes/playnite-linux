using System.Diagnostics;
using System.Text;
using Playnite.Core;
using Playnite.Platform.Integration;

namespace Playnite.Platform.Linux.Integration;

/// <summary>Minimal file surface used by protocol registration so tests never touch the host.</summary>
public interface IProtocolFileSystem
{
    bool FileExists(string path);
    void CreateDirectory(string path);
    string ReadAllText(string path);
    void DeleteFile(string path);
    void ReplaceAtomically(string path, string contents);
}

/// <summary>Production file access for the desktop entry and mimeapps list.</summary>
public sealed class SystemProtocolFileSystem : IProtocolFileSystem
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public bool FileExists(string path) => File.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public void DeleteFile(string path) => File.Delete(path);

    public void ReplaceAtomically(string path, string contents)
    {
        var temporary = path + ".tmp." + Environment.ProcessId + "." + Path.GetRandomFileName();
        try
        {
            File.WriteAllText(temporary, contents, Utf8NoBom);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The rename succeeded or the primary failure is already propagating.
                Debug.WriteLine(exception);
            }
        }
    }
}

/// <summary>
/// Registers <c>playnite://</c> with the freedesktop desktop: a desktop entry under
/// <c>$XDG_DATA_HOME/applications</c> and a <c>[Default Applications]</c> entry in
/// <c>$XDG_CONFIG_HOME/mimeapps.list</c>. It deliberately does not execute <c>xdg-mime</c>, so
/// registration needs no child process and keeps every write inside the XDG directories.
/// A sandbox, an unwritable directory or a malformed existing file returns a diagnostic result
/// and startup continues.
/// </summary>
public sealed class PlayniteProtocolHandler : IProtocolHandler
{
    public const string SchemeName = PlayniteUri.Scheme;
    public const string DesktopFileName = "playnite-handler.desktop";

    private const string MimeType = "x-scheme-handler/" + SchemeName;
    private const string DefaultApplicationsSection = "[Default Applications]";

    private readonly XdgDirectories directories;
    private readonly string executablePath;
    private readonly IEnvironment environment;
    private readonly IProtocolFileSystem fileSystem;

    public PlayniteProtocolHandler(
        XdgDirectories directories,
        string executablePath,
        IEnvironment environment,
        IProtocolFileSystem fileSystem)
    {
        this.directories = directories;
        this.executablePath = executablePath;
        this.environment = environment;
        this.fileSystem = fileSystem;
    }

    public string Scheme => SchemeName;

    /// <summary>The desktop entry path that registration writes when permitted.</summary>
    public string DesktopEntryPath => Path.Combine(directories.DataHome, "applications", DesktopFileName);

    /// <summary>The freedesktop default-associations file that registration updates.</summary>
    public string MimeAppsPath => Path.Combine(directories.ConfigHome, "mimeapps.list");

    public Task<ProtocolRegistrationResult> RegisterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sandbox = SandboxName();
        if (sandbox is not null)
        {
            return Task.FromResult(new ProtocolRegistrationResult(
                ProtocolRegistrationStatus.Skipped,
                $"Protocol registration is not permitted inside {sandbox}; the host desktop must register {SchemeName}://."));
        }

        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath)
            || executablePath.Any(character => character < ' ' || character == '\u007f' || (character >= '\u0080' && character <= '\u009f')))
        {
            return Task.FromResult(new ProtocolRegistrationResult(
                ProtocolRegistrationStatus.Failed,
                $"Cannot register {SchemeName}://: the executable path {executablePath} is not an absolute path without control characters."));
        }

        var desktopExisted = false;
        var desktopWritten = false;
        try
        {
            desktopExisted = fileSystem.FileExists(DesktopEntryPath);
            fileSystem.CreateDirectory(Path.GetDirectoryName(DesktopEntryPath)!);
            var entry = DesktopEntry(executablePath);
            if (!desktopExisted || fileSystem.ReadAllText(DesktopEntryPath) != entry)
            {
                fileSystem.ReplaceAtomically(DesktopEntryPath, entry);
                desktopWritten = true;
            }

            fileSystem.CreateDirectory(Path.GetDirectoryName(MimeAppsPath)!);
            var mimeApps = fileSystem.FileExists(MimeAppsPath) ? fileSystem.ReadAllText(MimeAppsPath) : string.Empty;
            WriteIfChanged(MimeAppsPath, UpdateMimeApps(mimeApps));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            if (desktopWritten && !desktopExisted)
            {
                try
                {
                    fileSystem.DeleteFile(DesktopEntryPath);
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    // The diagnostic below still names the original failure.
                    Debug.WriteLine(rollback);
                }
            }

            return Task.FromResult(new ProtocolRegistrationResult(
                ProtocolRegistrationStatus.Failed,
                $"Cannot register {SchemeName}:// in the XDG directories: {exception.Message}"));
        }

        return Task.FromResult(new ProtocolRegistrationResult(ProtocolRegistrationStatus.Registered, null));
    }

    public static string DesktopEntry(string executablePath) =>
        string.Join('\n',
            "[Desktop Entry]",
            "Type=Application",
            "Name=Playnite",
            "Comment=Playnite game library",
            $"Exec={QuoteExec(executablePath)} --uridata %u",
            $"MimeType={MimeType};",
            "Terminal=false",
            "NoDisplay=true",
            "StartupNotify=false",
            string.Empty);

    /// <summary>
    /// Adds or replaces the default handler line while preserving every other section, key and
    /// comment. The file's CRLF or LF line-ending style is retained.
    /// </summary>
    public static string UpdateMimeApps(string contents)
    {
        var newline = contents.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = contents.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Length == 0 ? [] : normalized.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var section = lines.FindIndex(line => line.Trim() == DefaultApplicationsSection);
        if (section < 0)
        {
            if (lines.Count > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add(DefaultApplicationsSection);
            lines.Add(MimeType + "=" + DesktopFileName);
            return string.Join(newline, lines) + newline;
        }

        var sectionEnd = lines.FindIndex(section + 1, line => line.TrimStart().StartsWith('['));
        if (sectionEnd < 0)
        {
            sectionEnd = lines.Count;
        }

        var existing = lines.FindIndex(section + 1, sectionEnd - section - 1, IsMimeKeyLine);
        if (existing >= 0)
        {
            lines[existing] = MimeType + "=" + DesktopFileName;
        }
        else
        {
            lines.Insert(section + 1, MimeType + "=" + DesktopFileName);
        }

        return string.Join(newline, lines) + newline;
    }

    private void WriteIfChanged(string path, string contents)
    {
        if (!fileSystem.FileExists(path) || fileSystem.ReadAllText(path) != contents)
        {
            fileSystem.ReplaceAtomically(path, contents);
        }
    }

    private static bool IsMimeKeyLine(string line)
    {
        var trimmed = line.TrimStart();
        var separator = trimmed.IndexOf('=');
        return separator > 0 && trimmed[..separator].Trim() == MimeType;
    }

    private string? SandboxName()
    {
        if (!string.IsNullOrEmpty(environment.GetVariable("FLATPAK_ID")))
        {
            return "Flatpak";
        }

        if (!string.IsNullOrEmpty(environment.GetVariable("SNAP")) || !string.IsNullOrEmpty(environment.GetVariable("SNAP_NAME")))
        {
            return "Snap";
        }

        return null;
    }

    private static string QuoteExec(string value)
    {
        // Desktop Entry Exec quoting, not shell quoting. % is doubled because Exec interprets
        // field codes; the URI placeholder is appended after this quoted argument.
        var quoted = value
            .Replace("%", "%%", StringComparison.Ordinal)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal);
        return $"\"{quoted}\"";
    }
}
