using LiteDB;

var dir = Path.Combine(Path.GetTempPath(), "playnite-litedb-spike");
if (Directory.Exists(dir)) Directory.Delete(dir, true);
Directory.CreateDirectory(dir);
var path = Path.Combine(dir, "games.db");

using (var db = new LiteDatabase($"Filename={path};Mode=Exclusive;Cache Size=0"))
{
    var games = db.GetCollection<Game>("games");
    games.EnsureIndex(g => g.Id, true);
    games.Insert(new Game { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Name = "Half-Life" });
    games.Insert(new Game { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Name = "Portal" });
    Console.WriteLine($"written={games.Count()} names={string.Join(",", games.FindAll().Select(g => g.Name))}");
}

using (var db = new LiteDatabase($"Filename={path};Mode=Exclusive;Cache Size=0"))
{
    var games = db.GetCollection<Game>("games");
    var all = games.FindAll().OrderBy(g => g.Name).ToList();
    Console.WriteLine($"read={all.Count} names={string.Join(",", all.Select(g => g.Name))}");
    foreach (var name in db.GetCollectionNames()) Console.WriteLine($"collection={name}");
}

Console.WriteLine($"file={new FileInfo(path).Length} bytes");
Console.WriteLine("SPIKE OK");

public sealed class Game
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}
