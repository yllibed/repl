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

Colour and terminal control (screen clearing, arrow-key menus, pager, shell-integration marks, advanced
progress) are decided separately; colour forced onto a file or a pipe does not enable terminal control.
See [Output System: ANSI Detection](output-system.md#ansi-detection).

Configure explicitly:

```csharp
app.Options(o => o.Output.AnsiMode = AnsiMode.Never);  // force off
// or
app.RunAsync(args, new ReplRunOptions { AnsiSupport = AnsiMode.Always });
```

See also: [Configuration Reference](configuration-reference.md) | [Terminal Metadata](terminal-metadata.md) | [Output System](output-system.md)
