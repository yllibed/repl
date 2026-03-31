# Command Reference

This page documents **framework-level commands and flags** provided by Repl Toolkit.
Your application commands are defined by your own route mappings.

## Discover commands

- CLI help at current scope: `myapp --help`
- Help for a scoped path: `myapp contact --help`
- Interactive help: `help` or `?`

## Global flags

These flags are parsed before route execution:

- `--help`
- `--interactive`
- `--no-interactive`
- `--no-logo`
- `--output:<format>`
- output aliases mapped by `OutputOptions.Aliases` (defaults include `--json`, `--xml`, `--yaml`, `--yml`, `--markdown`)
- `--answer:<name>[=value]` for non-interactive prompt answers
- custom global options registered via `options.Parsing.AddGlobalOption<T>(...)`

Global parsing notes:

- unknown command options are validation errors by default (`options.Parsing.AllowUnknownOptions = false`)
- option name matching is case-sensitive by default (`options.Parsing.OptionCaseSensitivity = CaseSensitive`)
- option value syntaxes accepted by command parsing: `--name value`, `--name=value`, `--name:value`
- use `--` to stop option parsing and force remaining tokens to positional arguments
- response files are supported with `@file.rsp` (enabled by default); nested `@` expansion is not supported

## Declaring command options

Handler parameters can declare explicit option behavior with attributes:

- `[ReplOption]` for named options
- `[ReplArgument]` for positional behavior
- `[ReplValueAlias]` for token-to-value injection
- `[ReplEnumFlag]` on enum members for enum-token aliases

Example:

```csharp
using Repl.Parameters;

app.Map(
    "render",
    ([ReplOption(Aliases = ["-m"])] RenderMode mode = RenderMode.Fast,
     [ReplOption(ReverseAliases = ["--no-verbose"])] bool verbose = false) =>
        $"{mode}:{verbose}");
```

Root help now includes a dedicated `Global Options:` section with built-ins plus custom options registered through `options.Parsing.AddGlobalOption<T>(...)`.

### Accessing global options outside handlers

Parsed global option values are available via `IGlobalOptionsAccessor`, registered in DI automatically. This enables access from middleware, DI service factories, and handlers:

```csharp
// Register a global option
app.Options(o => o.Parsing.AddGlobalOption<string>("tenant"));

// Access in middleware
app.Use(async (ctx, next) =>
{
    var globals = ctx.Services.GetRequiredService<IGlobalOptionsAccessor>();
    var tenant = globals.GetValue<string>("tenant");
    await next();
});

// Access in a DI factory (lazy — resolved after parsing)
services.AddSingleton<ITenantClient>(sp =>
{
    var globals = sp.GetRequiredService<IGlobalOptionsAccessor>();
    return new TenantClient(globals.GetValue<string>("tenant", "default")!);
});

// Access in a handler
app.Map("show", (IGlobalOptionsAccessor globals) =>
    globals.GetValue<string>("tenant") ?? "none");
```

For applications with many global options, use `UseGlobalOptions<T>()` to register a typed class:

```csharp
public class MyGlobalOptions
{
    public string? Tenant { get; set; }
    public int Port { get; set; } = 8080;
}

app.UseGlobalOptions<MyGlobalOptions>();

// Access via DI
app.Map("show", (MyGlobalOptions opts) => $"{opts.Tenant}:{opts.Port}");
```

Property names are converted to kebab-case option names (`Port` → `--port`). Use `[ReplOption]` on properties for custom names or aliases.

You can also register global options using a type name string instead of a generic parameter:

```csharp
app.Options(o => o.Parsing.AddGlobalOption("port", "int"));
```

Supported type names: `string`, `int`, `long`, `bool`, `guid`, `uri`, `date`, `datetime`, `timespan`.

### Session-sticky behavior in interactive mode

Global options passed at CLI launch persist as session defaults throughout the interactive session. Per-command overrides are temporary — they apply to that single command, then the session defaults take effect again:

```
$ myapp --env staging         # launches interactive with env=staging
> deploy                      # env=staging (inherited from session)
> deploy --env prod           # env=prod (override for this command only)
> status                      # env=staging (session default restored)
```

This eliminates the need to re-specify global options on every interactive command.

## Parse diagnostics model

Command option parsing returns structured diagnostics through the internal `OptionParsingResult` model:

- `Diagnostics`: list of `ParseDiagnostic`
- `HasErrors`: true when any diagnostic has `Severity = Error`
- `ParseDiagnostic` fields:
  - `Severity`: `Error` or `Warning`
  - `Message`: user-facing explanation
  - `Token`: source token when available
  - `Suggestion`: optional typo hint (for example `--output`)

Runtime behavior:

- when at least one parsing error is present, command execution stops and the first error is rendered as a validation result
- warnings do not block execution

## Response file examples

`@file.rsp` is expanded before command option parsing.

Example file:

```text
--output json
# comments are ignored outside quoted sections
"two words"
```

Command:

```text
myapp echo @args.rsp
```

Notes:

- quotes and escapes are supported by the response-file tokenizer
- a standalone `@` token is treated as a normal positional token
- in interactive sessions, response-file expansion is disabled by default
- response-file paths are read from the local filesystem as provided; treat `@file` input as trusted CLI input

## Options groups

Handler parameters can use a class annotated with `[ReplOptionsGroup]` to declare reusable parameter groups:

```csharp
using Repl.Parameters;

[ReplOptionsGroup]
public class OutputOptions
{
    [ReplOption(Aliases = ["-f"])]
    [Description("Output format.")]
    public string Format { get; set; } = "text";

    [ReplOption(ReverseAliases = ["--no-verbose"])]
    public bool Verbose { get; set; }
}

app.Map("list", (OutputOptions output, int limit) => $"{output.Format}:{limit}");
app.Map("show", (OutputOptions output, string id) => $"{output.Format}:{id}");
```

Options group behavior:

- group properties become individual command options (the same as regular handler parameters)
- PascalCase property names are automatically lowered to camelCase (`Format` → `--format`)
- property initializer values serve as defaults when options are not provided
- `[ReplOption]`, `[ReplArgument]`, `[ReplValueAlias]` attributes work on properties
- the same group class can be reused across multiple commands
- groups and regular parameters can be mixed in the same handler
- group properties are `OptionOnly` by default; use explicit attributes to opt into positional binding
- when a group property receives both named and positional values in one invocation, parsing fails with a validation error
- parameter name collisions between group properties and regular parameters cause an `InvalidOperationException` at registration
- positional group properties cannot be mixed with positional regular handler parameters in the same command
- abstract, interface, or nested group types are rejected at registration

## Temporal range types

Handler parameters can use temporal range types for date/time intervals:

```csharp
app.Map("report", (ReplDateRange period) =>
    $"{period.From:yyyy-MM-dd} to {period.To:yyyy-MM-dd}");

app.Map("logs", (ReplDateTimeRange window) =>
    $"{window.From:HH:mm} to {window.To:HH:mm}");

app.Map("audit", (ReplDateTimeOffsetRange span) =>
    $"{span.From} to {span.To}");
```

Two syntaxes are supported:

- range: `--period 2024-01-15..2024-02-15`
- duration: `--period 2024-01-15@30d`

Available types:

| Type | From/To type | Example |
|------|-------------|---------|
| `ReplDateRange` | `DateOnly` | `2024-01-15..2024-02-15` |
| `ReplDateTimeRange` | `DateTime` | `2024-01-15T10:00..2024-01-15T18:00` |
| `ReplDateTimeOffsetRange` | `DateTimeOffset` | `2024-01-15T10:00+02:00..2024-01-15T18:00+02:00` |

Duration syntax uses the same format as `TimeSpan` literals (`30d`, `8h`, `1h30m`, `PT1H`, etc.).
Reversed ranges (`To < From`) produce a validation error.
For `ReplDateRange` (`DateOnly`), duration syntax must resolve to whole days.

## Supported parameter conversions

Handler parameters support native conversion for:

- `FileInfo` from string path tokens (for example `--path ./file.txt`)
- `DirectoryInfo` from string path tokens (for example `--path ./folder`)

Path existence is not validated at parse time; handlers decide validation policy.

## Handler return types

Handlers can return any type. The framework renders the return value through the active output transformer (`--human`, `--json`, etc.).

### Supported return patterns

| Return type | Behavior |
|---|---|
| `string` | Rendered as plain text |
| Object / anonymous type | Rendered as key-value pairs (human) or serialized (JSON/XML/YAML) |
| `IEnumerable<T>` | Rendered as a table (human) or collection (structured formats) |
| `IReplResult` | Structured result with kind prefix (`Results.Ok`, `Error`, `NotFound`...) |
| `ReplNavigationResult` | Renders payload and navigates scope (`Results.NavigateUp`, `NavigateTo`) |
| `IExitResult` | Renders optional payload and sets process exit code (`Results.Exit`) |
| `EnterInteractiveResult` | Renders optional payload and enters interactive REPL mode (`Results.EnterInteractive`) |
| `void` / `null` | No output |

### Result factory helpers

```csharp
Results.Ok("done")                          // plain text
Results.Text("content")                     // plain text (alias)
Results.Success("created", details)         // success with optional details object
Results.Error("not_allowed", "message")     // error with code
Results.Validation("invalid input")         // validation error
Results.NotFound("no such item")            // not-found
Results.Cancelled("user declined")          // cancellation
Results.NavigateUp(payload)                 // navigate up one scope level
Results.NavigateTo("client/42", payload)    // navigate to explicit scope
Results.Exit(0, payload)                    // explicit exit code
Results.EnterInteractive()                  // enter interactive REPL after command
Results.EnterInteractive(payload)           // render payload then enter interactive REPL
```

### Multiple return values (tuples)

Handlers can return C# tuples to produce multiple results rendered separately in sequence:

```csharp
app.Map("status", () => (
    "Current user: alice",
    Results.Success("All systems operational")
));
```

Each tuple element goes through the full rendering pipeline independently. This works with all handler signatures — sync, `Task<(T1, T2)>`, and `ValueTask<(T1, T2)>` — and supports up to 8 elements.

Tuple semantics:

- each element is rendered as a separate output block
- navigation results (`NavigateUp`, `NavigateTo`) are only applied on the **last** element
- `EnterInteractive` as the last element enters interactive mode after rendering prior elements
- exit code is determined by the last element
- null elements are silently skipped
- nested tuples are not flattened — use a flat tuple instead

## Interactive prompts

Handlers can use `IReplInteractionChannel` for guided prompts (text, choice, confirmation, secret, multi-choice), progress reporting, and status messages. Extension methods add enum prompts, numeric input, validated text, and more.

When the terminal supports ANSI and key reads, choice and multi-choice prompts automatically upgrade to rich arrow-key menus with mnemonic shortcuts. Labels using the `_X` underscore convention get keyboard shortcuts (e.g. `"_Abort"` → press `A`).

See the full guide: [interaction.md](interaction.md)

## Ambient commands

These commands are handled by the runtime (not by your mapped routes):

- `help` or `?`
- `..` (interactive scope navigation; interactive mode only)
- `exit` (leave interactive session when enabled)
- `history [--limit <n>]` (interactive mode only)
- `complete <command path> --target <name> [--input <text>]`
- `autocomplete [show]`
- `autocomplete mode <off|auto|basic|rich>` (interactive mode only)

Notes:

- `history` and `autocomplete` return explicit errors outside interactive mode.
- `complete` requires a terminal route and a registered `WithCompletion(...)` provider for the selected target.

### Custom ambient commands

You can register your own ambient commands that are available in every interactive scope.
Custom ambient commands are dispatched after the built-in ones, appear in `help` output under Global Commands, and participate in interactive autocomplete.

```csharp
app.Options(o => o.AmbientCommands.MapAmbient(
    "clear",
    async (IReplInteractionChannel channel, CancellationToken ct) =>
    {
        await channel.ClearScreenAsync(ct);
    },
    "Clear the screen"));
```

Handler parameters are injected using the same binding rules as regular command handlers (DI services, `IReplInteractionChannel`, `CancellationToken`, etc.).

## Shell completion management commands

When shell completion is enabled, the `completion` context is available in CLI mode:

- `completion install [--shell bash|powershell|zsh|fish|nu] [--force] [--silent]`
- `completion uninstall [--shell bash|powershell|zsh|fish|nu] [--silent]`
- `completion status`
- `completion detect-shell`
- `completion __complete --shell <...> --line <input> --cursor <position>` (internal protocol bridge, hidden)

See full setup and profile snippets: [shell-completion.md](shell-completion.md)

## Optional documentation export command

If your app calls `UseDocumentationExport()`, it adds:

- `doc export [<path...>]`

By default this command is hidden from help/discovery unless you configure `HiddenByDefault = false`.

## Protocol passthrough commands

For stdio protocols (MCP/LSP/JSON-RPC), mark routes with `AsProtocolPassthrough()`.
This mode is especially well-suited for **MCP servers over stdio**, where the handler owns `stdin/stdout` end-to-end.

Example command surface:

```text
mytool mcp start        # protocol mode over stdin/stdout
mytool start            # normal CLI command
mytool status --json    # normal structured output
```

In this model, only `mcp start` should be marked as protocol passthrough.

Common protocol families that fit this mode:

- MCP over stdio
- LSP / JSON-RPC over stdio
- DAP over stdio
- CGI-style process protocols (stdin/stdout contract)

Not typical passthrough targets:

- socket-first variants such as FastCGI (their protocol stream is on TCP, not app `stdout`)

Execution scope note:

- protocol passthrough works out of the box for **local CLI/console execution**
- hosted terminal sessions (`IReplHost` / remote transports) require handlers to request `IReplIoContext`; console-bound toolings that use `Console.*` directly remain CLI-only

Why `IReplIoContext` is optional:

- many protocol SDKs (for example some MCP/JSON-RPC stacks) read/write `Console.*` directly; these handlers can still work in local CLI passthrough without extra plumbing
- requesting `IReplIoContext` is the recommended low-level path when you want explicit stream control, easier testing, or hosted-session support
- in local CLI passthrough, `io.Output` is the protocol stream (`stdout`), while framework diagnostics remain on `stderr`

In protocol passthrough mode:

- global and command banners are suppressed
- repl/framework diagnostics are written to `stderr`
- framework-rendered handler return payloads (if any) are also written to `stderr`
- `stdout` remains reserved for protocol payloads
- interactive follow-up is skipped after command execution

Practical guidance:

- for protocol commands, prefer writing protocol bytes/messages directly to `io.Output` (or `Console.Out` when SDK-bound)
- return `Results.Exit(code)` to keep framework rendering silent

## Route constraints

Route templates support typed dynamic segments via constraint syntax: `{name:type}`.
When no constraint is specified, the framework infers one from the handler parameter type.

See the full constraint table, custom constraint registration, and type inference rules in [`docs/route-system.md`](route-system.md).

## Parameter binding precedence

Handler parameters are resolved in priority order:

1. `CancellationToken` — injected from execution context
2. Explicit attributes — `[FromServices]`, `[FromContext]`
3. Options groups — `[ReplOptionsGroup]`
4. Route values — captured from dynamic segments (`{name}`)
5. Named options — from `--option value` syntax
6. Context values / DI services — from context stack or service provider
7. Positional arguments — remaining tokens consumed left-to-right
8. Parameter defaults — C# default values
9. Null — for nullable types without other source

See [`docs/route-system.md`](route-system.md) for details on constraint types and binding modes.
