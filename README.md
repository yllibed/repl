# Repl Toolkit

[![NuGet](https://img.shields.io/nuget/v/Repl?logo=nuget&label=NuGet)](https://www.nuget.org/packages/Repl)
[![Downloads](https://img.shields.io/nuget/dt/Repl?logo=nuget&label=Downloads)](https://www.nuget.org/packages/Repl)
[![CI](https://github.com/yllibed/repl/actions/workflows/ci.yml/badge.svg)](https://github.com/yllibed/repl/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/yllibed/repl)

**A .NET framework for building composable command surfaces.**
Define your commands once — run them as a CLI, explore them in an interactive REPL,
host them in session-based terminals, or drive them from automation and AI agents.

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

## Features

- **Unified command graph** — one route map shared by CLI, REPL, and hosted sessions
- **POSIX-like semantics** — familiar flag syntax, `--` separator, predictable parsing
- **Hierarchical scopes** — stateful navigation with `..`, contexts, route constraints (`{id:int}`, `{when:date}`)
- **Multiple output formats** — `--json`, `--xml`, `--yaml`, `--markdown`, or `--human`
- **AI/agent-friendly** — machine-readable contracts, deterministic outputs, pre-answered prompts (`--answer:*`)
- **Typed results** — `Ok`, `Error`, `NotFound`, `Cancelled` with payloads — not raw strings
- **Typed interactions** — prompts, progress, status, timeouts, cancellation
- **Session-aware DI** — per-session services and metadata (transport, terminal, window size)
- **Hosting primitives** — run sessions over WebSocket, Telnet, or custom carriers
- **Shell completion** — Bash, PowerShell, Zsh, Fish, Nushell with auto-install
- **Testing toolkit** — in-memory multi-session harness with typed assertions
- **Cross-platform** — same behavior on Windows, Linux, macOS, containers, and CI

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

## Packages

| Package | Description |
|---------|-------------|
| **[`Repl`](https://www.nuget.org/packages/Repl)** | Meta-package — bundles Core + Defaults + Protocol (**start here**) |
| [`Repl.Core`](https://www.nuget.org/packages/Repl.Core) | Runtime: routing, parsing, binding, results, help, middleware |
| [`Repl.Defaults`](https://www.nuget.org/packages/Repl.Defaults) | DI, host composition, interactive mode, terminal UX |
| [`Repl.Protocol`](https://www.nuget.org/packages/Repl.Protocol) | Machine-readable contracts (help, errors, tool schemas) |
| [`Repl.WebSocket`](https://www.nuget.org/packages/Repl.WebSocket) | Session hosting over WebSocket |
| [`Repl.Telnet`](https://www.nuget.org/packages/Repl.Telnet) | Telnet framing, negotiation, session adapters |
| [`Repl.Testing`](https://www.nuget.org/packages/Repl.Testing) | In-memory multi-session test harness |

## Samples

Progressive learning path — start with 01:

1. **[Core Basics](samples/01-core-basics/)** — routing, constraints, help, output modes
2. **[Scoped Contacts](samples/02-scoped-contacts/)** — dynamic scoping, `..` navigation
3. **[Modular Ops](samples/03-modular-ops/)** — composable modules, generic CRUD
4. **[Interactive Ops](samples/04-interactive-ops/)** — prompts, progress, timeouts, cancellation
5. **[Hosting Remote](samples/05-hosting-remote/)** — WebSocket / Telnet session hosting
6. **[Testing](samples/06-testing/)** — multi-session typed assertions

## Documentation

| Topic | Link |
|-------|------|
| Architecture blueprint | [`docs/architecture.md`](docs/architecture.md) |
| Command reference | [`docs/commands.md`](docs/commands.md) |
| Parameter system | [`docs/parameter-system.md`](docs/parameter-system.md) |
| Terminal & session metadata | [`docs/terminal-metadata.md`](docs/terminal-metadata.md) |
| Testing toolkit | [`docs/testing-toolkit.md`](docs/testing-toolkit.md) |
| Shell completion | [`docs/shell-completion.md`](docs/shell-completion.md) |
| Conditional module presence | [`docs/module-presence.md`](docs/module-presence.md) |
| Publishing & deployment | [`docs/publishing.md`](docs/publishing.md) |
| Interactive docs & AI Q\&A | [deepwiki.com/yllibed/repl](https://deepwiki.com/yllibed/repl) |

## Contributing

Contributions welcome — please discuss new features first to keep the toolkit aligned with its goals.
See [`CONTRIBUTING.md`](CONTRIBUTING.md), [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md), and [`SECURITY.md`](SECURITY.md).

## License

[MIT](LICENSE) — Copyright (c) 2026 Yllibed project / Carl de Billy
