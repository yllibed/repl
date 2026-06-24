# Repl

`Repl` is the recommended starting point for **Repl Toolkit**.

Repl Toolkit lets .NET applications define one command graph and expose it as CLI commands, an interactive REPL, remote interactive REPL sessions hosted by your app, structured output for automation, and MCP tools for AI agents.

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

Use `Repl` for small and large command surfaces alike when your application benefits from:

- CLI commands for humans, scripts, or CI;
- interactive exploration with a REPL;
- structured output such as JSON, XML, YAML, or Markdown;
- remote interactive REPL sessions on remote connections such as Telnet, WebSocket, or other stream-based integrations;
- MCP tools, resources, prompts, or MCP Apps for AI agents;
- tests that exercise the same command surface end-to-end.

Repl Toolkit keeps tiny command surfaces concise while making the same command graph ready for humans, scripts, tests, remote sessions, and agents as the application grows.

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
