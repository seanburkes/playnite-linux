using Playnite.Core.Import;

await LibraryMigration.RunAsync(await File.ReadAllTextAsync(args[0]), args[1], args[2], checkpoint: state =>
{
    if (state != "AssetPartialCopy") return;
    Console.WriteLine(state);
    Console.In.ReadLine();
});
