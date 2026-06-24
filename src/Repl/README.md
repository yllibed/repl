# Repl

`Repl` is the recommended starting point for **Repl Toolkit**.

Repl Toolkit lets .NET applications define one command graph and expose it as CLI commands, an interactive REPL, hosted terminal sessions, structured output for automation, and MCP tools for AI agents.

This meta-package brings the default dependencies most apps need:

- `Repl.Core`
- `Repl.Defaults`
- `Repl.Protocol`

## Install

```bash
dotnet add package Repl
```

## Minimal app

```csharp
using Repl;

var app = ReplApp.Create().UseDefaultInteractive();
app.Map("hello", static () => "world");

return app.Run(args);
```

Run it as a CLI command:

```bash
myapp hello --json
```

Run it with no arguments to enter the interactive REPL:

```bash
myapp
```

## When to use Repl Toolkit

Use `Repl` when your application needs more than a one-off argument parser:

- CLI commands for humans, scripts, or CI;
- interactive exploration with a REPL;
- structured output such as JSON, XML, YAML, or Markdown;
- hosted sessions over WebSocket or Telnet;
- MCP tools, resources, prompts, or MCP Apps for AI agents;
- tests that exercise the same command surface end-to-end.

If you only need to parse a couple of simple command-line flags, a smaller parser may be enough. Repl Toolkit is most useful when the command surface should become a durable interface for humans, scripts, tests, hosted sessions, and agents.

## Add MCP later

When the same commands should be available to AI agents, add `Repl.Mcp`:

```bash
dotnet add package Repl.Mcp
```

```csharp
using Repl.Mcp;

app.UseMcpServer();
```

Then run:

```bash
myapp mcp serve
```

## Docs

Full documentation at **[repl.yllibed.org](https://repl.yllibed.org/)**:

- [Installation & first app](https://repl.yllibed.org/getting-started/installation/)
- [Coming from CLI frameworks](https://repl.yllibed.org/getting-started/migrating/) — System.CommandLine, Spectre.Console.Cli, and related tools
- [MCP Mode](https://repl.yllibed.org/getting-started/mcp-mode/) — expose commands to AI agents
- [Agent-Native Development](https://repl.yllibed.org/reference/agent-native/) — design command surfaces for agent workflows
- [For coding agents](https://github.com/yllibed/repl/blob/main/docs/for-coding-agents.md) — decision rules and copyable instructions for coding agents
- [Cookbook](https://repl.yllibed.org/cookbook/core-basics/) — guided examples from basics to MCP
- [Reference](https://repl.yllibed.org/reference/routes-and-parameters/) — routes, DI, modules, interactivity, MCP, and more
- [API reference](https://repl.yllibed.org/api/index.html)
