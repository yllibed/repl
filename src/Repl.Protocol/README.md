# Repl.Protocol

[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/yllibed/repl)

`Repl.Protocol` provides **machine-readable contracts** for tooling and automation.

Use it when you want to represent things like help or errors as structured objects, and map those contracts to other shapes (e.g. MCP tool descriptors).

## Install

```bash
dotnet add package Repl.Protocol
```

## Example

Create a help document and project it to an MCP tool descriptor:

```csharp
using Repl.Protocol;

var help = ProtocolContracts.CreateHelpDocument(
    scope: "root",
    commands: new[]
    {
        new HelpCommand(
            Name: "contact list",
            Description: "List contacts",
            Usage: "contact list [--json]")
    });

var tool = ProtocolContracts.CreateMcpTool(help.Commands[0]);
```

## Docs

- Project overview: [README.md](https://github.com/yllibed/repl/blob/main/README.md)
- Community DeepWiki (unofficial): [deepwiki.com/yllibed/repl](https://deepwiki.com/yllibed/repl)
