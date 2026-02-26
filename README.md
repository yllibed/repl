# Repl Toolkit

**Repl Toolkit** is a **foundational building block** for .NET applications that need a serious command surface.

It is a **POSIX-like**, cross-platform command framework that works **everywhere .NET runs**: Windows, Linux, macOS, containers, CI, servers, including wasm, mobile, and embedded environments.

Think of it as a **command-line parser** that naturally grows with your app:

- start as a **CLI**
- upgrade to a **scoped REPL**
- extend to **session-based terminal hosts** (local or remote)
- expose a **machine-friendly surface** for automation and AI agents

Most libraries stop at “parse args and exit”.  
Repl Toolkit is designed for the moment your tool becomes an **operational surface**: discoverable, stateful, testable, automatable — and portable.

- **One command graph** for CLI, REPL, and hosted sessions
- **POSIX-like semantics** (commands, options, piping-friendly output modes, predictable behavior)
- **Hierarchical scopes** for stateful workflows (no custom shell to reinvent)
- **Deterministic outputs** for humans, scripts, and agents (`human/json/xml/yaml/markdown`)
- **Typed interactions** (prompts, progress, status) instead of ad-hoc text
- **Session-aware DI** and rich **session metadata**
- **Multi-session testing** without brittle string assertions
- **Cross-platform by design**: same behavior on Windows, Linux, macOS...

---

## A core building block in your app architecture

Repl Toolkit is not a product. It is a **building block** you compose into your application.

It provides:
- a **routing model** for commands (shared by CLI, REPL, and sessions),
- a **runtime** to execute them in different modes,
- a **session model** with metadata and per-session services,
- and **hosting primitives** to wire this to terminals, sockets, or other carriers.

Your application:
- decides **which commands exist**,
- defines **what they do**,
- chooses **how they are hosted**,
- and shapes the **UX and policies** around them.

Because it is just .NET, the same command surface can run:
- as a local CLI tool,
- inside a long-running service,
- in a container,
- in CI,
- or behind a remote terminal — without changing your command model.

---

## POSIX-like, but for modern .NET apps

Repl Toolkit follows **POSIX-like command-line conventions** where it matters:

- clear separation between **command paths**, **options**, and **arguments**
- predictable **flag syntax** (`--name value`, `--name=value`, `--json`, `--output:<format>`)
- explicit `--` to stop option parsing
- stable, script-friendly output modes
- consistent exit and result semantics

At the same time, it goes beyond classic POSIX tools by adding:

- **scoped navigation** (stateful REPL contexts),
- **typed results** instead of raw strings,
- **typed interactions** (prompts, progress, status),
- and **session-aware execution** for hosted terminals.

The result: tools that still *feel* like good CLI citizens, but scale to real operational workflows.

---

## Designed as a first-class client for AI agents

Repl Toolkit is explicitly designed to be a **great surface for automated clients and LLM agents**:

- **Validated input data**  
  Every command can receive its input as named parameters, which can be typed or inferred from the command signature.
  Parameter format validation is done at runtime, and extensible with custom constraints.

- **Deterministic output modes**  
  Every command can be rendered as `json`, `xml`, `yaml`, `markdown`, or `human`, selected via flags like `--json` or `--output:<format>`.  
  This avoids screen-scraping and fragile parsing. You can also create custom output formats.

- **Machine-readable contracts**  
  With `Repl.Protocol`, help, errors, and tool contracts can be exported as structured documents.

- **Typed results, not strings**  
  Commands return semantic results (`Ok`, `Error`, `Validation`, `NotFound`, `Cancelled`, …) with payloads, which can be asserted in tests or consumed by automation.

- **Deterministic prompts for non-interactive runs**  
  Interactive flows (questions, choices, confirmations) can be pre-answered with `--answer:<name>[=value]`, making the same command usable by humans and agents.

- **Session metadata**  
  Agents (and tools) can reason about terminal capabilities, window size, transport identity, and other context carried by the session.

In short: Repl Toolkit is meant to be a **control-plane surface** for both humans and AI-driven automation.

---

## What you can build with it

Because it is a building block, not a product, typical uses include:

- **In-app admin / ops consoles** embedded in services
- **Power CLIs** that stay coherent when workflows become stateful
- **Terminal sessions** (local, browser, sockets, etc.) hosted by your app
- **Backends-first workflows**: build the command surface first, then layer a GUI on top
- **A single command contract** shared by humans, scripts, and LLM agents

> Repl Toolkit does **not** ship an opinionated “remote admin app”.  
> It ships **command routing, sessions, metadata, and hosting primitives**.  
> The samples demonstrate what *you* can build on top of those primitives.

---

## See it for yourself

A few short glimpses from the samples:

Remote hosting sample (browser terminal, multiple transports, session visibility):  
See `samples/05-hosting-remote/`.

Scoped navigation and discovery:  
See `samples/02-scoped-contacts/`.

Multi-session tests (typed results + session metadata):  
See `samples/06-testing/`.

---

## One more thing

You define routes and handlers **once**. Then you can:

- run them as one-shot CLI commands,
- explore them in a scoped REPL (with `..`, `help`, completion, history),
- host them in **session-based terminals** with per-session DI and metadata,
- use them directly from unit tests with typed results,
- and drive them from **automation or AI agents** with deterministic outputs.

Repl Toolkit gives you the **runtime and the rules**.  
Your app defines the **product, policies, and UX**.

---

## A small but expressive example (C#)

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

One-shot CLI:

```text
$ myapp client list --json
{
  "clients": ["ACME", "Globex"]
}
```

Interactive REPL (same command model):

```text
$ myapp
> client 42 show --json
{ "id": 42, "name": "ACME" }

> client
[client]> list
ACME
Globex

> exit
```

Hosted session (application-defined commands, from a sample):

```text
> sessions
Session       Transport  Remote       Screen   Terminal        Connected  Idle
ws-7c650a64   websocket  [::1]:60288  301x31   xterm-256color  1m 34s     1s
```

> Commands like `sessions` are **sample app commands**, not built-ins.

---

## What you get out of the box

- **Shared command graph** for CLI + REPL + hosted sessions
- **Hierarchical contexts** (scopes) with validation and navigation results (`NavigateUp`, `NavigateTo`)
- **Routing constraints** (`{id:int}`, `{when:date}`, `{x:guid}`…) plus custom constraints
- **Parsing and binding** for named options, positional args, route values, and injected services
- **Output pipeline** with transformers and aliases  
  (`--output:<format>`, `--json`, `--yaml`, `--markdown`, …)
- **Typed result model** (`Results.Ok/Error/Validation/NotFound/Cancelled`, etc.)
- **Typed interactions**: prompts, progress, status, timeouts, cancellation
- **Session model + metadata** (transport, terminal identity, window size, ANSI capabilities, etc.)
- **Hosting primitives** for running sessions over streams, sockets, or custom carriers
- **Testing toolkit** (`Repl.Testing`) for multi-step + multi-session, typed-first assertions
- **Cross-platform runtime**: same command surface on Windows, Linux, and macOS

> This makes Repl Toolkit suitable as a control surface for automated development, ops bots, and LLM-driven workflows.

---

## Packages

For most applications, you only need:

- **`Repl`** — the meta package that brings the default stack.
  > This is the package you should start with.

Additional packages, when you need them:

- `Repl.Core` — core runtime: routing, parsing/binding, results, help, middleware
- `Repl.Defaults` — DI + host composition: interactive mode, terminal UX, lifecycle helpers
- `Repl.Protocol` — machine-readable contracts (help, errors, MCP types)
- `Repl.WebSocket` — session hosting over raw WebSocket
- `Repl.Telnet` — telnet framing/negotiation + session adapters
- `Repl.Testing` — in-memory multi-session test harness

Package details:

- [`src/Repl/README.md`](src/Repl/README.md)
- [`src/Repl.Core/README.md`](src/Repl.Core/README.md)
- [`src/Repl.Defaults/README.md`](src/Repl.Defaults/README.md)
- [`src/Repl.Protocol/README.md`](src/Repl.Protocol/README.md)
- [`src/Repl.WebSocket/README.md`](src/Repl.WebSocket/README.md)
- [`src/Repl.Telnet/README.md`](src/Repl.Telnet/README.md)
- [`src/Repl.Testing/README.md`](src/Repl.Testing/README.md)

---

## Getting started

- Architecture blueprint: [`docs/architecture.md`](docs/architecture.md)
- Terminal/session metadata: [`docs/terminal-metadata.md`](docs/terminal-metadata.md)
- Testing toolkit: [`docs/testing-toolkit.md`](docs/testing-toolkit.md)
- Conditional module presence: [`docs/module-presence.md`](docs/module-presence.md)
- Shell completion bridge: [`docs/shell-completion.md`](docs/shell-completion.md)
- Samples (recommended learning path): [`samples/README.md`](samples/README.md)

## Shell completion (configurable)

Repl Toolkit includes a shell completion bridge for Bash and PowerShell.

Quick setup commands:
- `completion install [--shell bash|powershell] [--force]`
- `completion uninstall [--shell bash|powershell]`
- `completion status`

`completion ...` commands are CLI-only (not available in interactive or hosted session modes).
Use the app executable command directly (the CLI head must match the app binary).
Auto/prompt setup modes run only when entering interactive mode, never for one-shot terminal commands.

Guide and full snippets: [`docs/shell-completion.md`](docs/shell-completion.md)

## Conditional module presence

Modules can be conditionally present at runtime using `MapModule(module, predicate)`.
This enables dynamic surfaces like signed-out/signed-in experiences and channel-aware modules (`Cli`, `Interactive`, `Session`).
When predicate-driving state changes, call `app.InvalidateRouting()` so the active graph is recomputed.

Guide and examples: [`docs/module-presence.md`](docs/module-presence.md)

---

## Demos (learning path)

The fastest way to understand what the toolkit enables:

1. [01 core basics](samples/01-core-basics/) — smallest command surface, help, constraints, output modes >>**START HERE**<<
2. [02 scoped contacts](samples/02-scoped-contacts/) — dynamic scoping + `..` navigation
3. [03 modular ops](samples/03-modular-ops/) — compose modules across contexts
4. [04 interactive ops](samples/04-interactive-ops/) — prompts, progress, timeouts, cancellation
5. [05 hosting remote](samples/05-hosting-remote/) — session hosting over WebSocket/Telnet (sample app)
6. [06 testing](samples/06-testing/) — multi-session tests with typed assertions

---

## Non-goals

Repl Toolkit is not:

- a shell scripting language
- a TUI framework (Text-based User Interface)
- a “parse args and stop there” library (but you can use it for only that)
- an opinionated remote admin product (you have to build that yourself)

It’s a **building block** for **operational command surfaces**: interactive, discoverable, hostable, testable, automation-friendly, AI-friendly, and cross-platform.

---

## Contributing

Contributions are welcome — but please **discuss new features first** to keep the toolkit aligned with its goals.  
Pick something from the backlog/issues, or propose an idea with a clear use case and expected UX.

See: [`CONTRIBUTING.md`](CONTRIBUTING.md)
