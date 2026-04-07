# Repl Toolkit

[![NuGet](https://img.shields.io/nuget/v/Repl?logo=nuget&label=NuGet)](https://www.nuget.org/packages/Repl)
[![Downloads](https://img.shields.io/nuget/dt/Repl?logo=nuget&label=Downloads)](https://www.nuget.org/packages/Repl)
[![CI](https://github.com/yllibed/repl/actions/workflows/ci.yml/badge.svg)](https://github.com/yllibed/repl/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/yllibed/repl)

**A .NET framework for building composable command surfaces.**

- Define your commands once — run them as a CLI, explore them in an interactive REPL,
- host them in session-based terminals, expose them as MCP servers for AI agents,
- or drive them from automation scripts.

> **New here?** The [DeepWiki](https://deepwiki.com/yllibed/repl) has full architecture docs, diagrams, and an AI assistant you can ask questions about the toolkit.

## Quick start

```bash
dotnet add package Repl
```

```csharp
using Repl;

var app = ReplApp.Create().UseDefaultInteractive();
app.Map("hello", () => "world");
return app.Run(args);
```

## Example

```csharp
using Repl;

var app = ReplApp.Create().UseDefaultInteractive();

app.Context("client", client =>
{
    client.Map("list", () => new { Clients = new[] { "ACME", "Globex" } });

    client.Context("{id:int}", scoped =>
    {
        scoped.Map("show", (int id) => new { Id = id, Name = "ACME" });
        scoped.Map("remove", (int id) => Results.Cancelled($"Remove {id} cancelled."));
    });
});

return app.Run(args);
```

**CLI mode:**

```text
$ myapp client list --json
{
  "clients": ["ACME", "Globex"]
}
```

**REPL mode** (same command graph):

```text
$ myapp
> client 42 show --json
{ "id": 42, "name": "ACME" }

> client
[client]> list
ACME
Globex
```

**MCP mode** (same command graph, exposed to AI agents):

```csharp
using Repl.Mcp;

app.UseMcpServer();  // add one line
```

```json
{ "command": "myapp", "args": ["mcp", "serve"] }
```

One command graph. CLI, REPL, remote sessions, and AI agents — all from the same code.

## What's included

| Feature | Package | Guides |
|---------|---------|--------|
| Unified command graph — routing, constraints, binding | [![Repl.Core](https://img.shields.io/nuget/vpre/Repl.Core?logo=nuget&label=Repl.Core)](https://www.nuget.org/packages/Repl.Core) | <ul><li>[Route system](docs/route-system.md)</li><li>[Execution pipeline](docs/execution-pipeline.md)</li><li>[Commands](docs/commands.md)</li></ul> |
| Interactive REPL — scopes, history, autocomplete | [![Repl.Defaults](https://img.shields.io/nuget/vpre/Repl.Defaults?logo=nuget&label=Repl.Defaults)](https://www.nuget.org/packages/Repl.Defaults) | <ul><li>[Interactive loop](docs/interactive-loop.md)</li><li>[Configuration](docs/configuration-reference.md)</li></ul> |
| Parameters & options — typed binding, options groups, response files | [![Repl.Core](https://img.shields.io/nuget/vpre/Repl.Core?logo=nuget&label=Repl.Core)](https://www.nuget.org/packages/Repl.Core) | <ul><li>[Parameter system](docs/parameter-system.md)</li><li>[Route system](docs/route-system.md)</li></ul> |
| Multiple output formats — JSON, XML, YAML, Markdown | [![Repl.Core](https://img.shields.io/nuget/vpre/Repl.Core?logo=nuget&label=Repl.Core)](https://www.nuget.org/packages/Repl.Core) | <ul><li>[Output system](docs/output-system.md)</li></ul> |
| MCP server — expose commands as AI agent tools and inline MCP Apps | [![Repl.Mcp](https://img.shields.io/nuget/vpre/Repl.Mcp?logo=nuget&label=Repl.Mcp)](https://www.nuget.org/packages/Repl.Mcp) | <ul><li>[MCP server](docs/mcp-server.md)</li><li>[MCP advanced](docs/mcp-advanced.md)</li><li>[MCP sample](samples/08-mcp-server/)</li></ul> |
| Typed results & interactions — prompts, progress, cancellation | [![Repl.Core](https://img.shields.io/nuget/vpre/Repl.Core?logo=nuget&label=Repl.Core)](https://www.nuget.org/packages/Repl.Core) | <ul><li>[Interaction channel](docs/interaction.md)</li></ul> |
| Session hosting — WebSocket, Telnet, remote terminals | [![Repl.WebSocket](https://img.shields.io/nuget/vpre/Repl.WebSocket?logo=nuget&label=Repl.WebSocket)](https://www.nuget.org/packages/Repl.WebSocket) [![Repl.Telnet](https://img.shields.io/nuget/vpre/Repl.Telnet?logo=nuget&label=Repl.Telnet)](https://www.nuget.org/packages/Repl.Telnet) | <ul><li>[Runtime channels](docs/runtime-channels.md)</li><li>[Terminal metadata](docs/terminal-metadata.md)</li></ul> |
| Shell completion — Bash, PowerShell, Zsh, Fish, Nushell | [![Repl.Core](https://img.shields.io/nuget/vpre/Repl.Core?logo=nuget&label=Repl.Core)](https://www.nuget.org/packages/Repl.Core) | <ul><li>[Shell completion](docs/shell-completion.md)</li></ul> |
| Spectre.Console — rich prompts, tables, charts | [![Repl.Spectre](https://img.shields.io/nuget/vpre/Repl.Spectre?logo=nuget&label=Repl.Spectre)](https://www.nuget.org/packages/Repl.Spectre) | <ul><li>[Interaction channel](docs/interaction.md)</li><li>[Sample](samples/07-spectre/)</li></ul> |
| Testing toolkit — in-memory multi-session harness | [![Repl.Testing](https://img.shields.io/nuget/vpre/Repl.Testing?logo=nuget&label=Repl.Testing)](https://www.nuget.org/packages/Repl.Testing) | <ul><li>[Testing toolkit](docs/testing-toolkit.md)</li></ul> |
| Machine-readable contracts — help schemas, error contracts | [![Repl.Protocol](https://img.shields.io/nuget/vpre/Repl.Protocol?logo=nuget&label=Repl.Protocol)](https://www.nuget.org/packages/Repl.Protocol) | <ul><li>[Help system](docs/help-system.md)</li></ul> |
| Conditional modules — channel-aware, feature-gated commands | [![Repl.Core](https://img.shields.io/nuget/vpre/Repl.Core?logo=nuget&label=Repl.Core)](https://www.nuget.org/packages/Repl.Core) | <ul><li>[Module presence](docs/module-presence.md)</li></ul> |

[**`Repl`**](https://www.nuget.org/packages/Repl) is the meta-package that bundles Core + Defaults + Protocol — **start here**.

## Learn by example

Progressive learning path — each sample builds on the previous:

1. **[Core Basics](samples/01-core-basics/)** — routing, constraints, help, output modes
2. **[Scoped Contacts](samples/02-scoped-contacts/)** — dynamic scoping, `..` navigation
3. **[Modular Ops](samples/03-modular-ops/)** — composable modules, generic CRUD
4. **[Interactive Ops](samples/04-interactive-ops/)** — prompts, progress, timeouts, cancellation
5. **[Hosting Remote](samples/05-hosting-remote/)** — WebSocket / Telnet session hosting
6. **[Testing](samples/06-testing/)** — multi-session typed assertions
7. **[Spectre](samples/07-spectre/)** — Spectre.Console renderables, visualizations, rich prompts
8. **[MCP Server](samples/08-mcp-server/)** — expose commands as MCP tools for AI agents, including a minimal MCP Apps UI

## More documentation

| | |
|---|---|
| [Architecture blueprint](docs/architecture.md) | [Best practices](docs/best-practices.md) |
| [Comparison & migration](docs/comparison.md) | [Publishing & deployment](docs/publishing.md) |
| [Glossary](docs/glossary.md) | [Interactive docs & AI Q&A](https://deepwiki.com/yllibed/repl) |

## AI-assisted development

If you use an AI coding agent (Claude Code, Cursor, Windsurf, Copilot, Codex, OpenCode, etc.) to build your Repl Toolkit app, install [Context7](https://context7.com) to give it access to Repl Toolkit documentation and code snippets.

**Library ID:** `/yllibed/repl`

Most agents that support [MCP](https://modelcontextprotocol.io/) can use Context7 directly. Refer to your agent's documentation for MCP server configuration.

> You can also explore the toolkit architecture and ask questions on [DeepWiki](https://deepwiki.com/yllibed/repl).

## Contributing

Contributions welcome — please discuss new features first to keep the toolkit aligned with its goals.
See [`CONTRIBUTING.md`](CONTRIBUTING.md), [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md), and [`SECURITY.md`](SECURITY.md).

## License

[MIT](LICENSE) — Copyright (c) 2026 Yllibed project / Carl de Billy
