# Samples

This folder is a progressive learning path for **Repl Toolkit**.

If you’re new, start with **01**, then follow the sequence.

## Index (recommended order)

1. [01 — Core Basics](01-core-basics/)  
   `Repl.Core` only: routing, parsing/binding, typed params + constraints, reusable options groups, temporal ranges, help/discovery, CLI + REPL from the same command graph.
2. [02 — Scoped Contacts](02-scoped-contacts/)  
   Dynamic scopes + REPL navigation (`..`) + DI-backed handlers.
3. [03 — Modular Ops](03-modular-ops/)  
   Composable modules, generic CRUD, open-generic DI, reuse the same module under multiple contexts.
4. [04 — Interactive Ops](04-interactive-ops/)  
   Prompts, progress, timeouts, cancellation, and deterministic automation via `--answer:*`.
5. [05 — Hosting Remote](05-hosting-remote/)  
   Session hosting over transports (raw WebSocket, Telnet-over-WebSocket, SignalR), shared state, session visibility, terminal metadata.
6. [06 — Testing](06-testing/)
   `Repl.Testing` harness: multi-step + multi-session, typed results, interaction/timeline events, metadata snapshots.
7. [07 — Spectre](07-spectre/)
   `Repl.Spectre` integration: FigletText, Table, Panel, Tree, BarChart, BreakdownChart, Calendar, JsonText, TextPath, Grid, Columns, Rule, Status, Progress, and all Spectre-powered prompts.
8. [08 — MCP Server](08-mcp-server/)
   MCP server mode: tools, resources, prompts, behavioral annotations, automation visibility, and a launcher-backed minimal MCP Apps UI.

## Run

Each sample is a runnable project:

```powershell
dotnet run --project samples/01-core-basics/CoreBasicsSample.csproj
```

Replace the project path with the one you want:

- `samples/02-scoped-contacts/ScopedContactsSample.csproj`
- `samples/03-modular-ops/ModularOpsSample.csproj`
- `samples/04-interactive-ops/InteractiveOpsSample.csproj`
- `samples/05-hosting-remote/HostingRemoteSample.csproj`
- `samples/06-testing/TestingSample.csproj`
- `samples/07-spectre/SpectreOpsSample.csproj`
- `samples/08-mcp-server/McpServerSample.csproj`

## Suggested reading (existing docs)

- [Architecture blueprint](../docs/architecture.md)
- [Terminal/session metadata](../docs/terminal-metadata.md)
- [Testing toolkit (guide)](../docs/testing-toolkit.md)
- [Project overview](../README.md)
