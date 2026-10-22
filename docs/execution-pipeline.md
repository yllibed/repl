# Execution Pipeline

This document describes the command execution pipeline in the Repl Toolkit,
from raw input to final output. The pipeline is driven by `CoreReplApp.ExecuteCoreAsync`.

Applications integrating an existing CLI or a larger host can opt into
[advanced input adaptation and validation](advanced-input.md). Adapters run before
the global-option stage; typed input validators run before handler binding. The
ordinary command-definition workflow does not require these hooks.

[Startup preparation and hooks](advanced-startup.md#callback-and-lifecycle-order)
document the outer lifecycle: preparation before composition, startup hooks in the
run/session scope, then hosted-service startup and the dispatch pipeline below.
Startup hooks do not repeat for each interactive command or nested tool call.

```text
Input
  |
  v
+---------------------------+
| 1. Global Option Parsing  |
+---------------------------+
  |
  v
+---------------------------+
| 2. Prefix Resolution      |
+---------------------------+
  |
  v
+---------------------------+     +--- Shell completion
| 3. Pre-Execution Handling |---->+--- Help rendering
+---------------------------+     +--- Empty invocation (enter interactive)
  |  (short-circuits if matched)  +--- Ambient commands
  |
  v
+---------------------------+
| 4. Route Resolution       |
+---------------------------+
  |
  v
+---------------------------+
| 5. Banner Rendering       |
+---------------------------+
  |
  v
+---------------------------+
| 6. Command Option Parsing |
+---------------------------+
  |
  v
+---------------------------+
| 7. Argument Binding       |
+---------------------------+
  |
  v
+---------------------------+
| 8. Context Validation     |
+---------------------------+
  |
  v
+---------------------------+
| 9. Middleware + Handler    |
+---------------------------+
  |
  v
+---------------------------+
| 10. Result Processing     |
+---------------------------+
  |
  v
+---------------------------+
| 11. Output Transformation |
+---------------------------+
  |
  v
+---------------------------+
| 12. Exit Code             |
+---------------------------+
```

## Pipeline Stages

### 1. Global Option Parsing

`GlobalOptionParser.Parse()` extracts global options from the raw arguments before
any routing takes place. Recognized options:

- `--help` — triggers help rendering
- `--interactive` — enters interactive session mode
- `--no-logo` — suppresses the banner
- `--output:format` — selects the output transformer
- `--answer:key=value` — supplies pre-answered prompt values
- Custom global options registered through configuration

These tokens are consumed and removed from the argument list before the next stage.
Parsed custom global option values are stored in `IGlobalOptionsAccessor` (registered in DI),
making them available to middleware, DI service factories, and handlers in subsequent stages.

In interactive mode, CLI-level global options become session defaults — they persist across
all commands unless explicitly overridden per-command. Overrides are temporary and revert
to the session baseline on the next command.

### 2. Prefix Resolution

`ResolveUniquePrefixes()` expands abbreviated command names to their full registered
forms. If a prefix matches multiple commands, it is flagged as ambiguous and an error
is reported with the list of candidates.

### 3. Pre-Execution Handling

`TryHandlePreExecutionAsync()` checks for conditions that bypass normal command
dispatch:

- **Shell completion requests** — returns completion candidates for the current token.
- **Help rendering** — displays help for the resolved command or context.
- **Empty invocation** — no arguments provided; enters interactive mode.
- **Ambient commands** — built-in commands (e.g., `exit`, `clear`) handled directly.

If any of these conditions match, execution short-circuits and the remaining stages
are skipped.

### 4. Route Resolution

`RouteResolver.ResolveWithDiagnostics()` matches the remaining tokens against all
registered route templates. The resolver scores matches by segment type: literal
segments score higher than dynamic (parameterized) ones. The best-scoring match is
selected along with extracted route values.

On failure, the resolver produces diagnostics including similar command suggestions
computed via Levenshtein distance. See [commands.md](commands.md) for how commands
and routes are defined.

### 5. Banner Rendering

`TryRenderBannerAsync()` renders the application banner configured via `WithBanner()`.
The banner is suppressed when:

- `--no-logo` was passed
- The invocation is a protocol passthrough

### 6. Command Option Parsing

`InvocationOptionParser.Parse()` processes command-specific options from the remaining
tokens. Supported syntax:

```csharp
// All equivalent for valued options
--name value
--name=value
--name:value

// Boolean flags
--verbose       // true
--no-verbose    // false (negation prefix)

// End-of-options separator
-- remaining args are positional

// Response files
@responsefile.txt
```

See [configuration-reference.md](configuration-reference.md) for options configuration.

### 7. Argument Binding

`HandlerArgumentBinder.Bind()` resolves each handler parameter using the following
precedence:

1. `CancellationToken` — injected from the execution context
2. Explicit attributes — `[FromServices]`, `[FromContext]`
3. Parameters Repl supplies — progress reporters, the interaction channel and other framework services
4. Typed global options — a `UseGlobalOptions<T>()` type, resolved from services
5. Options groups — `[ReplOptionsGroup]` for multi-property binding
6. Route values — values extracted during route resolution
7. Named options — values from command option parsing
8. Context/services — resolved from the DI container or execution context
9. Positional arguments — remaining unmatched tokens, in order
10. Default values — parameter defaults from the method signature
11. Null — for nullable types without other source

See [parameter-system.md](parameter-system.md) for full parameter binding details.

### 8. Context Validation

`ValidateContextsForMatchAsync()` runs optional validation delegates registered on
each context in the matched route's path hierarchy. Validation failures prevent
handler invocation and produce an error result.

### 9. Middleware and Handler Invocation

`ExecuteWithMiddlewareAsync()` builds the middleware chain from delegates registered
via `app.Use()`, then invokes the pipeline. The final stage calls
`CommandInvoker.InvokeAsync()` which executes the handler delegate.

The invoker supports synchronous, `Task`, `Task<T>`, `ValueTask`, and `ValueTask<T>` handlers:

```csharp
// Synchronous, returns data
int Count() => items.Length;

// Async, returns data
async Task<Contact[]> ListAsync() => await store.ListAsync();
async ValueTask<string> DescribeAsync() => "ready";

// Void (implicit success)
void Run() { }
async Task RunAsync() { }
```

> **A handler's return value is always rendered as data — including `int`.** It never becomes the
> process exit code: `int Count() => 3` prints `3` and exits `0`. To set an explicit exit code,
> return `Results.Exit(code)` (or any `IExitResult`).

Once the handler has run, its return value is exposed as `ReplExecutionContext.Result`, so a
middleware registered with `app.Use(...)` can inspect or replace it after awaiting `next()`. A
middleware that short-circuits (never calls `next()`) may set `context.Result` to supply the result
that is rendered and classified in the handler's place.

### 10. Result Processing

The raw handler return value is unwrapped and interpreted:

- `IExitResult` — carries an explicit exit code and optional message.
- `ICompletedWithFailuresResult` — the command completed and reports failures; only its payload renders.
- `ITuple` — destructured into result components.
- `EnterInteractiveResult` — transitions the session into interactive mode.
- Navigation transformations are applied if the result triggers context changes.
- Registered observers are notified of the result.

### 11. Output Transformation

`RenderOutputAsync()` selects an output format using this precedence:

1. Explicit format flag on the command
2. `--output:format` global option
3. `ReplOptions.Output.DefaultFormat`
4. `"human"` (fallback)

The matching `IOutputTransformer` formats the result and writes it to stdout.

### 12. Exit Code

Every run ends in exactly one `ReplExecutionOutcomeKind`, decided once after every pipeline layer has
run — including a hosted-service start or stop failure, which is classified like any other outcome
(`FrameworkError`, or `Cancelled` when the caller's own token stopped the startup). The kind is
mapped to an integer by `ReplOptions.ExitCodes` (`ExitCodeOptions`), then handed to the optional
`ExitCodes.Resolver` hook whose return value is final:

| Kind | Produced by | Default code |
|---|---|---|
| `Success` | success-like handler result (`text`/`success`, plain data, `void`), ambient commands (`exit`, `..`) that did their job, clean interactive exit | `0` |
| `Help` | `--help`, bare invocation that prints help, scoped-context help, interactive `help` / `?` | `0` |
| `UsageError` | unknown command, ambiguous prefix, invalid global or command option, context validation failure, an ambient command that failed, and any unknown `--output` format — including on a bare non-interactive invocation, which reports the refusal rather than printing help — which outranks whatever was being reported, since a diagnostic the caller never saw cannot stand as the run's outcome. When it displaces a failure already being reported, the outcome still carries that failure's `Exception` and `Result` | `2` |
| `BindingError` | a handler argument could not be bound: token conversion failed or was missing, or a binder-resolved value (context value, `[FromServices]` dependency, typed global options service) was unavailable — including a service factory that threw. The discriminator is whether argument **binding** completed, not whether the handler body ran: a validator, banner or middleware that throws after binding is a `HandlerException` | `2` |
| `HandlerError` | handler returned `Results.Error` / `Validation` / `NotFound` / `Cancelled`, or any `IReplResult` whose kind the framework does not recognize as success-like | `1` |
| `HandlerExitCode` | handler returned an `IExitResult` — its code is used verbatim, the table is bypassed | `IExitResult.ExitCode` |
| `HandlerException` | the handler, a middleware, or user code running after binding threw — including a handler that raised `OperationCanceledException` without the caller having asked for cancellation, and an output transformer that failed. The outcome carries the exception and the refusal the framework reported for it — reported, not necessarily rendered: when the transformer itself is what failed, the message is written unformatted to stderr instead, and the run keeps this kind because the caller still saw it | `1` |
| `Cancelled` | the caller's own `CancellationToken` stopped the run — already cancelled at the call, during the command, while an interactive session waits for input, or while hosted services were starting | unmapped: the exception propagates unless `ExitCodes.Cancelled` or a `Resolver` is set, and a `Resolver` alone is handed `130` |
| `Interrupted` | a process signal (SIGINT, Ctrl+Break, SIGTERM) the framework claimed, for a standalone run that opted in through `ReplRunOptions.ProcessSignalHandling`. Only a clean or cancelled run is reclassified: one that already produced a refusal or failure keeps reporting it | `ExitCodes.Interrupted`, else the `128 + signal` code the handler supplies, else `130` |
| `FrameworkError` | incompatible programmatic adapter, unsupported hosting capability, hosted-service start or stop failure | `1` |
| `CompletedWithFailures` | handler returned `Results.CompletedWithFailures` — the command ran to completion and its report contains failures (tests that failed). Not success-like: it does not enter interactive mode, even with `--interactive`, and a process signal does not reclassify it. It keeps its outcome when a cancellation lands while its payload renders, whatever code it maps to | `1` |

The table is the **process** exit code. Ctrl+C during an interactive command is also a `Cancelled`
outcome, but it decorates that command's shell-integration command-end mark and the loop continues —
the session itself still exits `Success`, so a headless reader should not expect a process code from
it. See [Terminal & Shell Integration](terminal-shell-integration.md) and `ReplExitCodeScope` below.

The two modes draw the `Cancelled` line differently, deliberately. A one-shot run reserves it for the
caller's own token: a handler that raises `OperationCanceledException` on its own account is a
`HandlerException`, rendered and exiting `1`, so a real failure cannot pass for an operator abort. An
interactive session instead treats a **command-scoped** `OperationCanceledException` as an abort —
Ctrl+C and a self-cancelling handler alike: it prints `Cancelled.`, decorates the mark with the
`Cancelled` code, and does not render the exception.

Two interactive cases are not `Cancelled` cycles, and neither puts a code on its mark: an abandoned prompt
(empty line, Escape, end of input), and a cancellation of the session's own token — host shutdown —
which closes the cycle with an aborted mark and then propagates out of the loop, ending the run as a cancellation.

> **Changed in 0.12.** A cancellation of the session's own token while the line editor waited for a key, in a
> hosted session (`StreamedReplHost.RunSessionAsync`, `ReplWebSocketSession.RunAsync`, `ReplTelnetSession.RunAsync`) or
> with advanced input, used to end the session as if its input had ended, with exit code `0`; a prompt waiting there
> answered its default. The cycle's mark still carries no code, but the run now ends as a cancellation, as everywhere
> else: the `OperationCanceledException` propagates, or `ExitCodes.Cancelled` or the resolver maps it to an exit code.
> A session whose client closes it still ends normally; a host that passes a request-abort token should catch the
> `OperationCanceledException` raised when that token is cancelled.
>
> **Changed in 0.12.** `ReplTelnetSession.RunAsync` used to keep waiting for input once the peer had closed its side
> of the connection, until its token was cancelled; it now ends the session, with exit code `0`. Once the session ends,
> it also returns without waiting for the peer to close its side: it sends its remaining output, giving up on output
> the peer leaves unread for two seconds, then stops reading, and over a WebSocket it closes the connection normally. A
> peer gone before the opening negotiation could be sent ends the session instead of leaving it waiting for input. The
> building blocks change with it: `TelnetFraming.DisposeAsync` waits for the negotiation and the output to be sent, then
> stops the read `RunAsync` has pending, and `RunAsync` no longer fails with an `IOException` when the negotiation
> cannot be sent; cancelling a `WebSocketStream` read closes the WebSocket normally instead of aborting it, disposing
> the stream asynchronously closes it too, and a failed connection surfaces as an `IOException` rather than a
> `WebSocketException`.
>
> **Changed in 0.12.** `ReplWebSocketSession.RunAsync` used to abort its WebSocket when the session ended, so the peer
> saw the connection closed without a close handshake. It now closes it normally, waiting up to a second for the peer
> to answer and aborting the connection if it does not, so `RunAsync` can return up to a second later. A session ended
> by cancelling its token still aborts the connection at once.
>
> **Changed in 0.12.** A cancellation of a hosted session's token while it detected the terminal (VT probe, Telnet
> NAWS wait) used to propagate even with `ExitCodes.Cancelled` or a resolver set; it now goes through them too.

A hosted-service failure is resolved once, after the whole lifecycle, and the outcome carries the
exception. A shutdown that fails outranks everything the run had produced — including any exception
the pipeline was propagating — because the process is leaving dirty; a consumer still observes
exactly one outcome per run. The suppressed cause is not lost: with a propagating exception in
flight, the outcome carries an `AggregateException` of the stop failure and that exception, in that
order, and both are named on stderr. A startup the caller cancelled is the one case that is not a
hosting defect: it is a `Cancelled` outcome, it prints no startup error, and with no cancellation
policy configured it propagates the `OperationCanceledException` like every other path.

Framework diagnostics go to **stderr** — the hosted-lifecycle failures and the unknown-`--output`
refusal alike — so a headless run's stdout carries the command payload alone. Interaction feedback
follows it there whenever the payload is for a program (a machine-readable format, or a redirected
stdout); see [Progress](progress.md#which-stream-receives-progress).

Exit codes should stay within `0`-`255`: POSIX `wait` exposes only the low eight bits to the parent
process, so `Help = 300` reaches a shell as `44`. Repl does not clamp — a code outside the range is
passed through as configured.

```csharp
app.Options(options =>
{
    options.ExitCodes.UsageError = 64;          // EX_USAGE for scripts that follow sysexits
    options.ExitCodes.Help = 3;                 // a bare invocation must not look like success in CI
    options.ExitCodes.Cancelled = 130;          // return 128+SIGINT instead of throwing
    options.ExitCodes.Resolver = outcome =>     // final say, sees the structured outcome
        outcome.Kind == ReplExecutionOutcomeKind.HandlerException ? 70 : outcome.ExitCode;
});
```

The resolver receives a `ReplExecutionOutcome` (`Kind`, the `ExitCode` mapped by the command's module
codes and then the table, the final `Result` object when one exists, the `Exception` that ended the
run when applicable, a `Scope`, and the `Route` template of the command the invocation resolved to
or addressed — `null` otherwise, for example for help, an unknown command or ambiguous abbreviation,
a refusal before routing, a framework failure, or a cancellation or interruption that stopped the run
around the command).
`Scope` and `Route` are init-only properties rather than positional parameters, so they are not part
of the record's generated `Deconstruct` — read them by name.

#### Exit codes declared by a module

A module that follows another tool's conventions declares its own codes by implementing
`IReplModuleExitCodes`. They are read once, when the module is mapped, travel with the module wherever
it is mounted, and apply to the outcomes of its commands before the application's table:

```csharp
public sealed class TestRunnerModule : IReplModuleExitCodes
{
    // dotnet test (Microsoft.Testing.Platform): 2 = tests failed, 5 = invalid command line.
    public ReplExitCodeOverrides ExitCodes { get; } = new() { CompletedWithFailures = 2, UsageError = 5 };

    public void Map(IReplMap map) => map.Map("tests run", RunTests);
}
```

- A code left `null` is inherited from the module that mapped this one, then from `ReplOptions.ExitCodes`.
- The codes cover `Success`, `UsageError`, `BindingError`, `HandlerError`, `HandlerException` and
  `CompletedWithFailures`. Help, cancellation, interruption and framework errors describe the run,
  not one module's command, and keep the application's codes; a `Results.Exit` code is never remapped.
- An outcome belongs to the module of the command the invocation resolved to, a refused option
  included. An argument that fails a route constraint, or a missing argument, belongs to that route's
  module, and is reported as that argument's error, as long as no other route, and no context, matches
  more of the words typed; the word a parameter rejects counts, since it was typed where the route
  expects one, so `user bob` against `user {id:int}` and `user {name} rename {newName}` is still an
  invalid `id`. Inside an interactive context, a word of the scope is not on the line typed: a route
  rejecting it does not take the line's refusal from the context that accepted it. Among the routes
  addressed that way, a value rejected on a line the route fits comes first, then a route only waiting
  for more words, then a value rejected on a line too short for its route. When no failure is
  addressed, the invocation is refused as an unknown command among those words. An unknown command
  belongs to the
  module whose routes share the most leading words with it (`tests bogus` is refused as the `tests`
  module would refuse it). In both cases literal words count before parameters that accept a word, so a
  route such as `{env} {region} deploy` does not take `tests` away from the module that spells it out.

  > **Changed in 0.12.** Such an invocation used to be refused with the argument error of the failed
  > route routing ranked first, even when another route or a context matched more of its words. It is
  > now refused with the argument error of a failed route that no other route or context outscores,
  > which can name another route, parameter and module, or else as an unknown command: under `--json`,
  > an error result with the code `unknown_command` rather than a validation result. Conversely, a value
  > a route rejects on a line too short for that route, which used to be refused as an unknown command,
  > is now refused with that argument's error. The exit code is a usage error either way.

- An ambiguous abbreviation belongs to the modules of what it could expand to. Each expansion, its
  other abbreviations expanded too, is weighed as an unknown command is, and the expansions matching
  the most words decide: `t run`, between a module's `tests run` and `tools run`, is refused as that
  module would refuse it, even though `t` is not a whole word of either. When those expansions belong
  to different modules, or to a module and the application, they are settled as tied modules are,
  below.
- A context decides only when it matches more words than every route — within one expansion, for an
  ambiguous abbreviation — so a module mounted under one of the application's contexts still owns the
  commands under its words. Modules that tie and apply the same codes share them; otherwise the module
  that mapped them all decides — the application when none did.
- A context's failed validation belongs to the module that mapped the context, unless the invocation
  resolved to a command, whose module then decides.
- Global options, input hooks and argument preprocessors run before any command is resolved: their
  refusals and the results they answer with keep the application's codes. An unknown `--output`
  format is the exception: a command reports it when it renders, so it takes that command's module
  code, even when the command could not run, as a hosted protocol-passthrough command without
  `IReplIoContext`.
- The resolver runs after the module's codes, and receives them in `ExitCode`. Shell-integration
  marks use them too, interactive refusals included; nested sub-invocations keep the built-in defaults.

`Scope` says what the code is for, so a hook with side effects can tell one from the other:

- `ReplExitCodeScope.Process` — the process exit code. Raised once per top-level run, including a run
  that entered and left an interactive session. This is the only scope a one-shot run ever sees.
  Setting a resolver is itself enough to make a caller-token cancellation observable here: it is then
  reported as `Cancelled` rather than letting the `OperationCanceledException` propagate, even with
  `ExitCodes.Cancelled` unset.
- `ReplExitCodeScope.ShellIntegrationMark` — the code decorating one interactive command's
  command-end mark. Raised once per committed command whose mark actually carries a code, so never
  with shell integration off (the default), never for a protocol-passthrough command, and never for
  an abandoned prompt cycle (empty line, Escape, end of input, session cancellation).

The resolver must not throw. An exception from it is swallowed and the mapped code — the module's,
then the table's — is used instead, with one diagnostic line written to the session's error stream on
a best-effort basis — a faulty exit-code hook cannot replace the run's own outcome nor tear down an
interactive session, and a failing error stream cannot turn the report of that hook's failure into the
failure itself.

Nested sub-invocations (MCP tool calls executed through the Repl pipeline) always use the built-in
defaults and skip the resolver: the policy describes the *process* exit, and nested callers only test
for non-zero.

## Error Handling

Errors at each stage produce targeted diagnostics:

- **Route resolution failure** — suggests similar commands using Levenshtein distance
  and shows context-level help.
- **Option parsing errors** — suggests the correct option name when a close match
  exists.
- **Binding errors** — renders a message identifying the missing or invalid parameter.
- **Handler exceptions** — caught and unwrapped from `TargetInvocationException`,
  then rendered as an error result through the active output format, on stdout.
- **Cancellation** — in one-shot mode the caller's own token produces
  `ReplExecutionOutcomeKind.Cancelled`; a handler that raises `OperationCanceledException` on its own
  account is a `HandlerException` instead. The interactive loop treats both as an abort and prints
  `Cancelled.`. Stage 12 above carries the full contract, including when the exception propagates.

## Interactive Session Loop

When the application enters interactive mode (empty invocation or `--interactive`),
execution follows a loop:

```text
Prompt --> Input Tokenization --> History
  ^                                  |
  |                                  v
  |                          Ambient Commands
  |                                  |
  |                                  v
  |                      Prefix + Route Resolution
  |                                  |
  |                                  v
  |                          Command Dispatch
  |                                  |
  |                                  v
  |                              Output
  |                                  |
  +----------------------------------+
```

Each iteration runs through the same pipeline stages (4 through 12) described above,
preceded by ambient command checks. The session persists until an explicit `exit`
command or cancellation signal.

## See Also

- [commands.md](commands.md) — Command and route definition
- [parameter-system.md](parameter-system.md) — Parameter binding and attributes
- [configuration-reference.md](configuration-reference.md) — Options and app configuration
