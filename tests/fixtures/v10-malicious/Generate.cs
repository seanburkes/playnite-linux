// Offline additive fixture producer. No mapper or typed collection is used.
using LiteDB;

var original = Path.GetFullPath(args[0]);
var destination = Path.GetFullPath(args[1]);
if (Directory.Exists(destination)) throw new IOException("Output must not exist.");
Directory.CreateDirectory(destination);
foreach (var file in Directory.GetFiles(original)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
using var engine = new LiteEngine(Path.Combine(destination, "games.db"));
var document = engine.FindAll("Game").First();
document["_type"] = "Playnite.Core.Tests.Import.LibraryMigrationTests+ConstructorTrap, Playnite.Core.Tests";
document["$type"] = "Untrusted.Payload, nonexistent-untrusted-assembly";
document["UnmappedAdversarial"] = new BsonDocument
{
    ["_type"] = "System.Diagnostics.Process, System.Diagnostics.Process",
    ["$type"] = "System.IO.FileInfo, System.Private.CoreLib",
    ["Array"] = new BsonArray { new BsonDocument { ["_type"] = "Untrusted.Payload, nonexistent-untrusted-assembly" } },
};
engine.Update("Game", document);
