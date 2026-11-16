// Additive derivative of v10-malicious. Instrument each nested type-selection path.
using LiteDB;

var original = Path.GetFullPath(args[0]);
var destination = Path.GetFullPath(args[1]);
if (Directory.Exists(destination)) throw new IOException("Output must not exist.");
Directory.CreateDirectory(destination);
foreach (var file in Directory.GetFiles(original)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
using var engine = new LiteEngine(Path.Combine(destination, "games.db"));
var document = engine.FindAll("Game").First();
const string trap = "Playnite.Core.Tests.Import.LibraryMigrationTests+NestedConstructorTrap, Playnite.Core.Tests";
document["UnmappedAdversarial"] = new BsonDocument
{
    ["_type"] = trap, ["$type"] = trap,
    ["Array"] = new BsonArray { new BsonDocument { ["_type"] = trap, ["$type"] = trap } },
};
engine.Update("Game", document);
