using Dapper;
using Microsoft.Data.Sqlite;

namespace Playnite.Core.Tests.Database;

internal sealed class DatabaseTestDirectory : IDisposable
{
    internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "playnite-sqlite-" + Guid.NewGuid().ToString("N"));
    internal string PathFor(string name = "library.sqlite") => Path.Combine(DirectoryPath, name);

    internal DatabaseTestDirectory() => Directory.CreateDirectory(DirectoryPath);

    internal static SqliteConnection Connect(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, ForeignKeys = true, DefaultTimeout = 5,
        }.ToString());
        connection.Open();
        connection.Execute("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
        return connection;
    }

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
