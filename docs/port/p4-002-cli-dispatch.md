# p4-002 — CLI command dispatch wiring

This is a wiring change, not a parity claim: no command implementation, UI, or
network surface is added. `import-v10` keeps its arguments, output and exit codes.

## Registry shape

`src/Playnite.Cli/ICliCommand.cs` defines one command:

```csharp
internal interface ICliCommand
{
    string Name { get; }
    Task<int> RunAsync(string[] args); // args after Name
}
```

`src/Playnite.Cli/CliCommandRegistry.cs` builds the table once by scanning the
`Playnite.Cli` assembly for concrete `ICliCommand` types. Discovery fails at
startup if a type has no public parameterless constructor, has an empty name, or
duplicates another command name. `CliCommandRegistry.Find(name)` is the only
lookup `Program` uses. `Register(ICliCommand)` exists for tests and is internal,
so it is not part of the shipped contract.

`Program.RunAsync` parses only the first argument:

| Invocation | Behaviour | Exit |
| --- | --- | --- |
| no arguments, or `--version` | prints the assembly version | 0 |
| registered command name | dispatches the remaining arguments to that command | command's own code |
| unknown name (not starting with `-`) | prints `Unknown command '<name>'.` and the usage line to stderr | 1 |
| unknown option or malformed invocation | prints `Usage: Playnite.Cli [--version]` to stderr | 1 |

User errors never print a stack trace; the unknown-command and malformed-usage
paths both end at exit code 1. The option-shaped path is unchanged, including
`Playnite.Cli --unknown`, which still emits only the usage line.

## Extension recipe

Another unit adds one new file and nothing else:

```csharp
namespace Playnite.Cli;

internal sealed class ListCommand : ICliCommand
{
    public string Name => "list";

    public Task<int> RunAsync(string[] args) => // implementation
}
```

No edit to `Program.cs`, to `CliCommandRegistry.cs`, or to a registration list is
needed; the assembly scan finds the class. Command classes must be concrete,
non-abstract, and have a public parameterless constructor. A name that collides
with an existing command fails the host at startup instead of silently winning.

## Tests

`tests/Playnite.Core.Tests/Cli/CliCommandRegistryTests.cs` covers:

- `import-v10` is discovered from `ImportCommand.cs` and still dispatches with its
  own usage text and exit code 1 for a missing argument list;
- an unknown command prints its name plus usage and exits 1;
- `SyntheticCommand.cs`, a separate file, is registered and dispatched by
  `Program.RunAsync`, proving the extension point needs no dispatch edit.

`src/Playnite.Cli/AssemblyInfo.cs` grants `InternalsVisibleTo` to
`Playnite.Core.Tests` so the tests can call `RunAsync` and `Register` without
making either public.
