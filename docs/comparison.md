# Comparison & Migration

Repl Toolkit is a command-surface framework — not just a CLI parser. It builds a single command graph that runs as a CLI, an interactive REPL, or a hosted remote session. This page compares it with the two most prominent .NET CLI frameworks and provides migration guidance.

**Legend:** ✅ Native / built-in | ⚠️ Partial or manual effort | ❌ Not supported

## Command Modeling & Parsing

| Feature | System.CommandLine | Spectre.Console.Cli | Repl Toolkit |
|---|---|---|---|
| CLI argument parsing | ✅ | ✅ | ✅ |
| Hierarchical subcommands | ✅ | ✅ `AddBranch` | ✅ `Context` |
| Dynamic route segments (`{id:int}`) | ❌ Literal names only | ❌ Literal names only | ✅ |
| Route constraints (int, date, guid...) | ❌ | ❌ | ✅ |
| Option aliases | ✅ | ✅ | ✅ |
| Response files (`@file.rsp`) | ✅ | ❌ | ✅ |
| POSIX `--` separator | ✅ | ✅ | ✅ |
| Type conversion (FileInfo, enums...) | ✅ Widest built-in set | ✅ Via TypeConverter | ✅ |
| Reusable options groups | ⚠️ Via custom composition | ⚠️ Via shared settings patterns | ✅ `[ReplOptionsGroup]` |
| Temporal range literals (`start..end`, `start@duration`) | ⚠️ Via custom parser/binder | ⚠️ Via custom converter/binder | ✅ Built-in range types |
| Global / recursive options | ✅ `Recursive = true` | ⚠️ Settings inheritance | ✅ `AddGlobalOption` |
| Parse diagnostics with suggestions | ✅ | ✅ | ✅ |

`⚠️` indicates the capability is achievable, but not as a first-class built-in abstraction.

## Interactive & Session

| Feature | System.CommandLine | Spectre.Console.Cli | Repl Toolkit |
|---|---|---|---|
| Interactive REPL loop | ❌ | ❌ | ✅ |
| Session state | ❌ | ❌ | ✅ Per-session typed store |
| Scoped navigation (`..`) | ❌ | ❌ | ✅ |
| Scope-aware prompt | ❌ | ❌ | ✅ `[client/42]>` |
| Command history | ❌ | ❌ | ✅ Pluggable `IHistoryProvider` |
| Interactive autocomplete | ❌ | ❌ | ✅ Off / Auto / Basic / Rich |
| Typed prompts & confirmations | ❌ | ⚠️ Via Spectre.Console prompts | ✅ `IReplInteractionChannel` |
| Pre-answered prompts (`--answer:*`) | ❌ | ❌ | ✅ |
| Graceful cancellation (Ctrl+C / Esc) | ⚠️ Process-level | ⚠️ Process-level | ✅ Per-command + per-prompt |
| Multi-session support | ❌ | ❌ | ✅ |

## Output & Rendering

| Feature | System.CommandLine | Spectre.Console.Cli | Repl Toolkit |
|---|---|---|---|
| Human-readable output | ⚠️ Manual `Console.Write` | ✅ Rich (Markup, Table, Tree...) | ✅ |
| JSON output (`--json`) | ❌ Manual | ❌ Manual | ✅ Built-in |
| XML / YAML / Markdown output | ❌ | ❌ | ✅ Built-in |
| Custom output transformers | ❌ | ❌ | ✅ `AddTransformer` |
| Typed result objects | ❌ | ❌ | ✅ Ok, Error, NotFound... |
| Rich terminal UI (charts, trees...) | ❌ Removed | ✅ Full Spectre.Console | ⚠️ Via add-on |
| ANSI color auto-detection | ❌ | ✅ | ✅ |
| `NO_COLOR` / `CLICOLOR_FORCE` | ❌ | ✅ | ✅ |
| Machine-readable help (`--help --json`) | ❌ | ❌ | ✅ |

## Architecture & Extensibility

| Feature | System.CommandLine | Spectre.Console.Cli | Repl Toolkit |
|---|---|---|---|
| Middleware pipeline | ❌ Removed in 2.0 | ⚠️ Single `ICommandInterceptor` | ✅ ASP.NET-style `Use()` chain |
| DI integration | ⚠️ Manual inside handler | ⚠️ `ITypeRegistrar` boilerplate | ✅ Handler parameter injection |
| Handler-first delegates | ✅ `SetAction` | ❌ Class per command | ✅ `Map()` delegates |
| Class-per-command support | ❌ Imperative only | ✅ `Command<TSettings>` | ⚠️ Via `IReplModule` |
| Composable modules | ❌ | ❌ | ✅ `MapModule` |
| Conditional module presence | ❌ | ❌ | ✅ Runtime predicates |
| Zero-dependency core | ✅ | ❌ ~2 MB Spectre.Console | ✅ Build-enforced |
| NativeAOT support | ✅ | ❌ Not documented | ⚠️ Planned |

## Hosting & Remote

| Feature | System.CommandLine | Spectre.Console.Cli | Repl Toolkit |
|---|---|---|---|
| WebSocket session hosting | ❌ | ❌ | ✅ `Repl.WebSocket` |
| Telnet session hosting | ❌ | ❌ | ✅ `Repl.Telnet` |
| Terminal metadata negotiation | ❌ | ❌ | ✅ NAWS, TTYPE, DTTERM |
| Per-session DI & state | ❌ | ❌ | ✅ `IReplSessionState` |
| Window size detection | ❌ | ✅ `Console` only | ✅ Local + remote |
| Transport-agnostic host | ❌ | ❌ | ✅ `StreamedReplHost` |

## Testing & AI / Automation

| Feature | System.CommandLine | Spectre.Console.Cli | Repl Toolkit |
|---|---|---|---|
| In-memory test harness | ⚠️ `StringWriter` injection | ✅ `CommandAppTester` | ✅ `ReplTestHost` |
| Interactive session testing | ❌ | ⚠️ `TestConsole` push prompts | ✅ Multi-step async flows |
| Typed output assertions | ❌ | ✅ Settings assertions | ✅ `GetResult<T>()` |
| Multi-session test scenarios | ❌ | ❌ | ✅ Concurrent sessions |
| Structured help output | ❌ Text only | ❌ Text only | ✅ JSON / XML / YAML |
| Documentation export | ❌ | ❌ | ✅ `doc export` command |
| Protocol passthrough (MCP, LSP...) | ❌ | ❌ | ✅ `AsProtocolPassthrough()` |
| Shell completion | ⚠️ Tab completion API | ❌ | ✅ Bash, PS, Zsh, Fish, Nu |

## When to Use What

### Choose System.CommandLine when

- You are building a pure CLI tool with no interactive mode
- NativeAOT or trimming is a hard requirement today
- You need alignment with `.NET` CLI / SDK conventions
- Zero dependency footprint is non-negotiable and no rich output is needed

### Choose Spectre.Console.Cli when

- Rich terminal UI (tables, trees, progress bars, charts) is the primary product differentiator
- The tool is CLI-only with no REPL requirement
- Your team is already invested in the Spectre.Console ecosystem

### Choose Repl Toolkit when

- Your application needs both CLI and interactive REPL modes
- Dynamic scoping is a requirement (drill into entities, scope-aware prompts)
- Machine-readable output (`--json`, `--yaml`) must be a framework-level concern
- Commands involve multi-step guided workflows (prompts, progress, confirmations)
- Remote terminal hosting is planned (WebSocket, Telnet)
- The command model must be testable in both one-shot and interactive contexts
- AI/LLM agent readiness matters (structured help, protocol passthrough, pre-answered prompts)

## Migration from System.CommandLine

### Concept Mapping

| System.CommandLine | Repl Toolkit |
|---|---|
| `RootCommand` | `ReplApp.Create()` |
| `new Command("name")` | `app.Context("name", ...)` |
| `command.SetAction(handler)` | `app.Map("name", handler)` |
| `new Option<T>("--name")` | `[ReplOption] T name` on handler parameter |
| `new Argument<T>("name")` | `[ReplArgument] T name` on handler parameter |
| `parseResult.GetValue(option)` | Direct parameter binding (automatic) |
| `Option.Recursive = true` | `ParsingOptions.AddGlobalOption<T>(...)` |
| `command.Subcommands.Add(child)` | `app.Context("parent", ctx => ctx.Map("child", ...))` |
| Manual DI inside `SetAction` | Handler parameter injection (automatic) |
| `command.Parse(args)` | `app.Run(args)` (CLI) or `app.Run(args)` with `UseDefaultInteractive()` (REPL) |

### Before / After

**System.CommandLine:**

```csharp
Option<string> urlOption = new("--url") { Required = true };
Option<string> nameOption = new("--name") { DefaultValueFactory = _ => "origin" };

Command addCommand = new("add", "Add a remote") { urlOption, nameOption };
addCommand.SetAction((ParseResult pr, CancellationToken ct) =>
{
    // Manual DI
    var repo = services.GetRequiredService<IRepositoryService>();
    repo.AddRemote(pr.GetValue(nameOption)!, pr.GetValue(urlOption)!);
    Console.WriteLine($"Remote '{pr.GetValue(nameOption)}' added.");
    return Task.FromResult(0);
});

Command remoteCommand = new("remote") { addCommand };
RootCommand root = new() { remoteCommand };
return await root.Parse(args).InvokeAsync();
```

**Repl Toolkit:**

```csharp
var app = ReplApp.Create().UseDefaultInteractive();

app.Context("remote", remote =>
{
    remote.Map("add", (
        [Description("Repository URL")] string url,
        [Description("Remote name")] string name = "origin",
        IRepositoryService repo) =>
    {
        repo.AddRemote(name, url);
        return $"Remote '{name}' added.";
    });
});

return app.Run(args);
// Works as CLI:  myapp remote add --url https://... --name upstream
// Works as REPL: myapp > remote > add --url https://...
```

## Migration from Spectre.Console.Cli

### Concept Mapping

| Spectre.Console.Cli | Repl Toolkit |
|---|---|
| `CommandApp` | `ReplApp.Create()` |
| `config.AddBranch<TSettings>("name", ...)` | `app.Context("name", ...)` |
| `config.AddCommand<TCommand>("name")` | `app.Map("name", handler)` |
| `Command<TSettings>` class | Delegate on `Map()` |
| `CommandSettings` with `[CommandOption]` | Handler parameters with `[ReplOption]` |
| `[CommandArgument(position)]` | `[ReplArgument]` on handler parameter |
| `ITypeRegistrar` / `ITypeResolver` | `ReplApp.Create(services => { ... })` |
| `CommandAppTester` | `ReplTestHost` |
| `TestConsole` | `ReplTestHost.OpenSessionAsync()` |
| Spectre.Console rendering (Table, Tree...) | Output transformers (`--json`, `--yaml`) or add-on |

### Before / After

**Spectre.Console.Cli:**

```csharp
// 1 settings class + 1 command class per command + 2 DI bridge classes
var registrar = new TypeRegistrar(services);
var app = new CommandApp(registrar);

app.Configure(config =>
{
    config.AddBranch<RemoteSettings>("remote", remote =>
    {
        remote.AddCommand<RemoteAddCommand>("add");
    });
});
return await app.RunAsync(args);

// RemoteSettings, RemoteAddSettings (inherits RemoteSettings),
// RemoteAddCommand : Command<RemoteAddSettings>,
// TypeRegistrar : ITypeRegistrar, TypeResolver : ITypeResolver
// Minimum 5 additional types required.
```

**Repl Toolkit:**

```csharp
var app = ReplApp.Create(services =>
{
    services.AddSingleton<IRepositoryService, RepositoryService>();
}).UseDefaultInteractive();

app.Context("remote", remote =>
{
    remote.Map("add", (
        [Description("Repository URL")] string url,
        [Description("Remote name")] string name = "origin",
        IRepositoryService repo) =>
    {
        repo.AddRemote(name, url);
        return $"Remote '{name}' added.";
    });
});

return app.Run(args);
// Zero additional types. Same handler works in CLI and REPL modes.
```

## References

- **System.CommandLine** — [Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/standard/commandline/) · [NuGet](https://www.nuget.org/packages/System.CommandLine) · [GitHub](https://github.com/dotnet/command-line-api)
- **Spectre.Console.Cli** — [Documentation](https://spectreconsole.net/cli) · [NuGet](https://www.nuget.org/packages/Spectre.Console.Cli/) · [GitHub](https://github.com/spectreconsole/spectre.console)
- **Repl Toolkit** — [NuGet](https://www.nuget.org/packages/Repl) · [GitHub](https://github.com/yllibed/repl) · [DeepWiki](https://deepwiki.com/yllibed/repl)
