using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Playnite.Architecture.Tests;

public sealed class RawImportTests
{
    private static readonly HashSet<string> Forbidden = new(StringComparer.Ordinal)
    {
        "BsonMapper", "LiteDatabase", "GetCollection", "ToObject", "Deserialize", "DeserializeObject",
        "Populate", "CreateInstance", "Activator", "Assembly", "GetType", "TypeNameHandling",
    };

    private static string[] Violations(string source) => CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
        .OfType<SimpleNameSyntax>().Select(n => n.Identifier.ValueText).Where(Forbidden.Contains).Distinct().Order().ToArray();

    [Fact]
    public void LiteDB_and_importer_types_cannot_escape_the_raw_reader_and_reader_cannot_load_SQLite_or_Core()
    {
        foreach (var product in ProductGraph.Products)
        {
            var dependencies = product.Dependencies();
            if (product.Name != "Playnite.LegacyImport")
                Assert.DoesNotContain(dependencies, d => d.Equals("LiteDB", StringComparison.OrdinalIgnoreCase) || d == "Playnite.LegacyImport");
            else
                Assert.DoesNotContain(dependencies, d => d is "Playnite.Core" or "Microsoft.Data.Sqlite" or "Microsoft.Data.Sqlite.Core" or "Dapper");
        }
        var sources = ProductGraph.Products.Single(p => p.Name == "Playnite.LegacyImport").Values("Source");
        Assert.NotEmpty(sources);
        foreach (var file in sources)
            Assert.Empty(Violations(File.ReadAllText(file)));
    }

    [Theory]
    [InlineData("new LiteDB.BsonMapper();", "BsonMapper")]
    [InlineData("using M = LiteDB.BsonMapper;", "BsonMapper")]
    [InlineData("db.GetCollection<Game>().FindAll();", "GetCollection")]
    [InlineData("doc.ToObject<Game>();", "ToObject")]
    [InlineData("JsonSerializer.Deserialize<Game>(json);", "Deserialize")]
    [InlineData("Activator.CreateInstance(type);", "Activator")]
    public void Negative_guard_rejects_typed_mapping_and_object_construction(string source, string violation)
        => Assert.Contains(violation, Violations(source));

    [Fact]
    public void Raw_engine_document_traversal_is_a_working_positive_control()
        => Assert.Empty(Violations("using var engine = new LiteEngine(stream, null); foreach (BsonDocument d in engine.FindAll(name)) { var x = d[\"_type\"]; }"));

    [Fact]
    public void The_only_audit_exception_is_the_owner_authorized_importer_advisory()
    {
        var importer = ProductGraph.Products.Single(p => p.Name == "Playnite.LegacyImport");
        // The scoped exception must be visible in the owning project, not a global switch.
        foreach (var product in ProductGraph.Products)
        {
            var project = System.Xml.Linq.XDocument.Load(product.ProjectFile);
            var exceptions = project.Descendants().Where(e => e.Name.LocalName == "NuGetAuditSuppress").Select(e => (string?)e.Attribute("Include")).ToArray();
            if (product.Name == importer.Name) Assert.Equal(["https://github.com/advisories/GHSA-3x49-g6rc-c284"], exceptions);
            else Assert.Empty(exceptions);
        }
    }
}
