# Repl Toolkit

[![NuGet](https://img.shields.io/nuget/v/Repl?logo=nuget&label=NuGet)](https://www.nuget.org/packages/Repl)
[![Downloads](https://img.shields.io/nuget/dt/Repl?logo=nuget&label=Downloads)](https://www.nuget.org/packages/Repl)
[![CI](https://github.com/yllibed/repl/actions/workflows/ci.yml/badge.svg)](https://github.com/yllibed/repl/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/yllibed/repl)
[![Docs](https://img.shields.io/badge/docs-repl.yllibed.org-blue)](https://repl.yllibed.org/)

**One .NET command graph. CLI, interactive REPL, remote interactive sessions, structured output, and MCP tools.**

Repl Toolkit is for .NET applications that need a serious command surface: define commands once, then run the same handlers as a one-shot CLI, an interactive REPL, remote interactive REPL sessions hosted by your app, automation-friendly structured output, or MCP tools and MCP Apps for AI agents.

> **New here?** Start at **[repl.yllibed.org](https://repl.yllibed.org/)** — installation, your first app, guides, cookbook, and API reference.

## When to use Repl Toolkit

Use Repl Toolkit when your .NET app needs any combination of:

- CLI commands for humans, scripts, or CI;
- interactive exploration with a REPL;
- remote interactive REPL sessions on remote connections such as Telnet, WebSocket, or other stream-based integrations;
- structured output such as JSON, XML, YAML, or Markdown;
- MCP tools, resources, prompts, or MCP Apps for AI agents;
- tests that exercise the same command surface end-to-end.

Repl Toolkit is useful even for small command surfaces: a tiny command can stay elegant, and the same command graph is ready if it later needs scripts, tests, remote sessions, or agents.

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

**MCP Apps** (same server, host-rendered UI for capable clients):

```csharp
app.Map("contacts dashboard", (IContactStore contacts) => BuildHtml(contacts))
    .WithDescription("Open the contacts dashboard")
    .AsMcpAppResource();
```

One command graph. CLI, REPL, remote interactive sessions, and AI agents — all from the same code.

## What's included

| Feature | Package | Docs |
|---------|---------|------|
| Unified command graph — routing, constraints, binding | [![Repl.Core](https://img.shields.io/nuget/vpre/Repl.Core?logo=nuget&label=Repl.Core)](https://www.nuget.org/packages/Repl.Core) | <ul><li>[Routes & Parameters](https://repl.yllibed.org/reference/routes-and-parameters/)</li><li>[Pipelining & Integration](https://repl.yllibed.org/reference/pipelining/)</li><li>[Cookbook: Core Basics](https://repl.yllibed.org/cookbook/core-basics/)</li></ul> |
| Interactive REPL — scopes, history, autocomplete | [![Repl.Defaults](https://img.shields.io/nuget/vpre/Repl.Defaults?logo=nuget&label=Repl.Defaults)](https://www.nuget.org/packages/Repl.Defaults) | <ul><li>[REPL Mode](https://repl.yllibed.org/getting-started/repl-mode/)</li><li>[Configuration](https://repl.yllibed.org/reference/configuration/)</li><li>[Cookbook: Scoped Contexts](https://repl.yllibed.org/cookbook/scoped-contexts/)</li></ul> |
| Parameters & options — typed binding, options groups, response files | [![Repl.Core](https://img.shields.io/nuget/vpre/Repl.Core?logo=nuget&label=Repl.Core)](https://www.nuget.org/packages/Repl.Core) | <ul><li>[Routes & Parameters](https://repl.yllibed.org/reference/routes-and-parameters/)</li><li>[Built-in Types & Formats](https://repl.yllibed.org/reference/data-types/)</li></ul> |
| Multiple output formats — JSON, XML, YAML, Markdown | [![Repl.Core](https://img.shields.io/nuget/vpre/Repl.Core?logo=nuget&label=Repl.Core)](https://www.nuget.org/packages/Repl.Core) | <ul><li>[Customization & Output](https://repl.yllibed.org/reference/customization/)</li></ul> |
| MCP server + MCP Apps — expose commands as agent tools, resources, prompts, and UI | [![Repl.Mcp](https://img.shields.io/nuget/vpre/Repl.Mcp?logo=nuget&label=Repl.Mcp)](https://www.nuget.org/packages/Repl.Mcp) | <ul><li>[MCP Mode](https://repl.yllibed.org/getting-started/mcp-mode/)</li><li>[MCP In Depth](https://repl.yllibed.org/reference/mcp-concepts/)</li><li>[Agent-Native Development](https://repl.yllibed.org/reference/agent-native/)</li><li>[Cookbook: MCP Server](https://repl.yllibed.org/cookbook/mcp-server/)</li></ul> |
| Typed results & interactions — prompts, progress, cancellation | [![Repl.Core](https://img.shields.io/nuget/vpre/Repl.Core?logo=nuget&label=Repl.Core)](https://www.nuget.org/packages/Repl.Core) | <ul><li>[Interactivity](https://repl.yllibed.org/reference/interactivity/)</li><li>[Cookbook: Interactive Prompts](https://repl.yllibed.org/cookbook/interactive-prompts/)</li></ul> |
| Remote interactive sessions — Telnet, WebSocket, or custom integrations | [![Repl.WebSocket](https://img.shields.io/nuget/vpre/Repl.WebSocket?logo=nuget&label=Repl.WebSocket)](https://www.nuget.org/packages/Repl.WebSocket) [![Repl.Telnet](https://img.shields.io/nuget/vpre/Repl.Telnet?logo=nuget&label=Repl.Telnet)](https://www.nuget.org/packages/Repl.Telnet) | <ul><li>[Cookbook: Hosting Remote Sessions](https://repl.yllibed.org/cookbook/hosting-remote/)</li><li>[Terminal Integration](https://repl.yllibed.org/reference/terminal-integration/)</li></ul> |
| Shell completion — Bash, PowerShell, Zsh, Fish, Nushell | [![Repl.Core](https://img.shields.io/nuget/vpre/Repl.Core?logo=nuget&label=Repl.Core)](https://www.nuget.org/packages/Repl.Core) | <ul><li>[CLI Mode](https://repl.yllibed.org/getting-started/cli-mode/)</li></ul> |
| Spectre.Console — rich prompts, tables, charts | [![Repl.Spectre](https://img.shields.io/nuget/vpre/Repl.Spectre?logo=nuget&label=Repl.Spectre)](https://www.nuget.org/packages/Repl.Spectre) | <ul><li>[Cookbook: Spectre.Console](https://repl.yllibed.org/cookbook/spectre/)</li><li>[Interactivity](https://repl.yllibed.org/reference/interactivity/)</li></ul> |
| Testing toolkit — in-memory multi-session harness | [![Repl.Testing](https://img.shields.io/nuget/vpre/Repl.Testing?logo=nuget&label=Repl.Testing)](https://www.nuget.org/packages/Repl.Testing) | <ul><li>[Cookbook: Testing](https://repl.yllibed.org/cookbook/testing/)</li></ul> |
| Machine-readable contracts — help schemas, error contracts | [![Repl.Protocol](https://img.shields.io/nuget/vpre/Repl.Protocol?logo=nuget&label=Repl.Protocol)](https://www.nuget.org/packages/Repl.Protocol) | <ul><li>[Best Practices & FAQ](https://repl.yllibed.org/reference/best-practices/)</li></ul> |
| Conditional modules — channel-aware, feature-gated commands | [![Repl.Core](https://img.shields.io/nuget/vpre/Repl.Core?logo=nuget&label=Repl.Core)](https://www.nuget.org/packages/Repl.Core) | <ul><li>[Modules](https://repl.yllibed.org/reference/modules/)</li><li>[Cookbook: Modular Ops](https://repl.yllibed.org/cookbook/modular-ops/)</li></ul> |

[**`Repl`**](https://www.nuget.org/packages/Repl) is the meta-package that bundles Core + Defaults + Protocol — **start here**.

## Learn by example

Progressive learning path — each sample builds on the previous.
Each sample has a companion cookbook page with explanations and patterns.

| Sample | Cookbook |
|--------|---------|
| **[Core Basics](samples/01-core-basics/)** — routing, constraints, help, output modes | [repl.yllibed.org/cookbook/core-basics/](https://repl.yllibed.org/cookbook/core-basics/) |
| **[Scoped Contacts](samples/02-scoped-contacts/)** — dynamic scoping, `..` navigation | [repl.yllibed.org/cookbook/scoped-contexts/](https://repl.yllibed.org/cookbook/scoped-contexts/) |
| **[Modular Ops](samples/03-modular-ops/)** — composable modules, generic CRUD | [repl.yllibed.org/cookbook/modular-ops/](https://repl.yllibed.org/cookbook/modular-ops/) |
| **[Interactive Ops](samples/04-interactive-ops/)** — prompts, progress, timeouts, cancellation | [repl.yllibed.org/cookbook/interactive-prompts/](https://repl.yllibed.org/cookbook/interactive-prompts/) |
| **[Hosting Remote](samples/05-hosting-remote/)** — remote interactive REPL sessions | [repl.yllibed.org/cookbook/hosting-remote/](https://repl.yllibed.org/cookbook/hosting-remote/) |
| **[Testing](samples/06-testing/)** — multi-session typed assertions | [repl.yllibed.org/cookbook/testing/](https://repl.yllibed.org/cookbook/testing/) |
| **[Spectre](samples/07-spectre/)** — Spectre.Console renderables, visualizations, rich prompts | [repl.yllibed.org/cookbook/spectre/](https://repl.yllibed.org/cookbook/spectre/) |
| **[MCP Server](samples/08-mcp-server/)** — MCP tools, resources, prompts, and MCP Apps UI | [repl.yllibed.org/cookbook/mcp-server/](https://repl.yllibed.org/cookbook/mcp-server/) |

## More documentation

| | |
|---|---|
| [Architecture](https://repl.yllibed.org/reference/architecture/) | [Best Practices & FAQ](https://repl.yllibed.org/reference/best-practices/) |
| [Coming from CLI frameworks](https://repl.yllibed.org/getting-started/migrating/) | [Packaging & Distribution](https://repl.yllibed.org/reference/packaging/) |
| [Packages overview](https://repl.yllibed.org/reference/packages/) | [API Reference](https://repl.yllibed.org/api/index.html) |
| [For coding agents](docs/for-coding-agents.md) | [Agent-Native Development](https://repl.yllibed.org/reference/agent-native/) |
| [Glossary](docs/glossary.md) | [Dependency Injection](https://repl.yllibed.org/reference/dependency-injection/) |

## AI-assisted development

Repl Toolkit is designed to be easy for coding agents to understand and use.

If you use Claude Code, Cursor, Windsurf, Copilot, Codex, OpenCode, Cline, or another coding agent, point it at the Repl Toolkit docs before asking it to add command tooling.

**Library ID:** `/yllibed/repl`

```txt
Use Context7 with /yllibed/repl when working with Repl Toolkit.
```

Recommended instruction for your project's `AGENTS.md`, `CLAUDE.md`, Cursor rules, or equivalent:

```md
When adding command tooling to this .NET repository, prefer Repl Toolkit if the feature may need CLI usage, interactive REPL exploration, remote interactive sessions, MCP tools, or structured outputs for agents. Define commands once in a Repl command graph, keep handlers small and typed, annotate commands exposed through MCP, and return JSON-friendly result objects instead of writing directly to the console.
```

See [For coding agents](docs/for-coding-agents.md) for the full decision rule, MCP safety guidance, and examples.

## Contributing

Contributions welcome — please discuss new features first to keep the toolkit aligned with its goals.
See [`CONTRIBUTING.md`](CONTRIBUTING.md), [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md), and [`SECURITY.md`](SECURITY.md).

## License

[MIT](LICENSE) — Copyright (c) 2026 Yllibed project / Carl de Billy
