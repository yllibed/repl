# Runtime Channels

## Channel Types

The `ReplRuntimeChannel` enum identifies the current execution mode:

| Channel | Value | Description |
|---|---|---|
| `Cli` | 0 | One-shot command-line invocation |
| `Interactive` | 1 | Local interactive REPL loop |
| `Session` | 2 | Hosted session (WebSocket, Telnet, remote terminal) |
| `Programmatic` | 3 | External agent, script, or API-driven execution |

## Channel Detection

The framework determines the channel automatically at runtime, in this order:

1. `ReplSessionIO.IsProgrammatic` → `Programmatic`
2. `ReplSessionIO.IsHostedSession` → `Session`
3. `_runtimeState.IsInteractiveSession` → `Interactive`
4. Default → `Cli`

## Module Presence by Channel

Use channel detection to control which commands are visible:

```csharp
// MCP server module only in CLI mode (where stdio transport runs)
app.UseMcpServer();  // internally uses: context.Channel is ReplRuntimeChannel.Cli

// Admin tools only in interactive mode
app.MapModule(new AdminModule(),
    static context => context.Channel is ReplRuntimeChannel.Interactive);

// Diagnostics hidden from agents
app.MapModule(new DiagModule(),
    static context => context.Channel is not ReplRuntimeChannel.Programmatic);
```

## ANSI Color Detection

The framework resolves ANSI support through a 9-step chain (highest priority first):

1. **Session override** — `ReplSessionIO.AnsiSupport` if a hosted session is active
2. **Explicit mode** — `AnsiMode.Always` forces on, `AnsiMode.Never` forces off
3. **Host capability** — `CapabilityOptions.SupportsAnsi` (default: `true`)
4. **`FORCE_COLOR` env var** — any non-empty value, including `0`, forces colour on (the rule `System.Console` applies on .NET 11)
5. **`NO_COLOR` env var** — if non-empty, ANSI is disabled (see no-color.org)
6. **`CLICOLOR_FORCE` env var** — if `"1"`, ANSI is forced on
7. **Output redirection** — `Console.IsOutputRedirected` → disabled (piped output)
8. **`TERM` env var** — if `"dumb"`, ANSI is disabled
9. **Default** — enabled

This chain decides colour. Terminal control sequences (shell-integration marks, advanced progress) follow a stricter rule: a forced colour on a redirected stream does not enable them, and `NO_COLOR` or `TERM=dumb` disables them, unless the application set `AnsiMode.Always` or a hosted session states its own ANSI support.

Configure explicitly:

```csharp
app.Options(o => o.Output.AnsiMode = AnsiMode.Never);  // force off
// or
app.RunAsync(args, new ReplRunOptions { AnsiSupport = AnsiMode.Always });
```

See also: [Configuration Reference](configuration-reference.md) | [Terminal Metadata](terminal-metadata.md) | [Output System](output-system.md)
