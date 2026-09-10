# Repl.Defaults

**Website:** [repl.yllibed.org](https://repl.yllibed.org/)

`Repl.Defaults` layers DI and “batteries included” composition on top of `Repl.Core`.

It provides:

- `ReplApp` facade
- default composition profiles (e.g. `UseDefaultInteractive`)
- hosted-session primitives (`StreamedReplHost`) used by transport integrations

## Install

```bash
dotnet add package Repl.Defaults
```

## Minimal app

```csharp
using Repl;

var app = ReplApp.Create().UseDefaultInteractive();
app.Map("hello", () => "world");

return app.Run(args);
```

## Process signals

Process-owning profiles cooperatively translate a first Ctrl+C event, Ctrl+Break on Windows, or SIGTERM on supported Unix platforms into handler cancellation. An unprofiled app and embedded hosts remain caller-owned by default; Repl does not claim their standalone signals. Configure this per run with `ReplRunOptions.ProcessSignalHandling`; see the [configuration reference](https://repl.yllibed.org/reference/configuration/#process-signal-handling) for exit codes `130`/`143`, second-signal escalation, token lifetime, and platform limits.

## Docs

- [REPL Mode](https://repl.yllibed.org/getting-started/repl-mode/) — interactive session, scopes, history
- [Configuration](https://repl.yllibed.org/reference/configuration/) — `ReplRunOptions`, profiles
- [Architecture](https://repl.yllibed.org/reference/architecture/) — how the layers fit together
- [Dependency Injection](https://repl.yllibed.org/reference/dependency-injection/) — DI patterns for handlers
