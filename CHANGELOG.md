# Changelog

Notable consumer-facing changes to the Repl packages. Versions are assigned automatically by
Nerdbank.GitVersioning at pack time; this file groups changes by theme instead of by release.

## Unreleased

### Added — execution outcomes and exit-code policy

- Every run now ends in a structured `ReplExecutionOutcome` whose `ReplExecutionOutcomeKind`
  distinguishes `Success`, `Help`, `UsageError`, `BindingError`, `HandlerError`, `HandlerExitCode`,
  `HandlerException`, `Cancelled`, `Interrupted`, and `FrameworkError`. The kind is mapped to an
  integer by the new `ReplOptions.ExitCodes` (`ExitCodeOptions`) table, then passed to the optional
  `ExitCodes.Resolver` hook whose return value is the final exit code. An explicit `Results.Exit(n)`
  keeps its code verbatim (`HandlerExitCode`) but is still visible to the resolver. See
  `docs/execution-pipeline.md` (stage 12) and `docs/configuration-reference.md`.
- `ExitCodes.Cancelled` (`int?`) turns a cancellation through the caller's own token into an exit
  code instead of letting `OperationCanceledException` escape `RunAsync`. It is unset by default,
  which preserves the existing throwing behaviour; setting a `Resolver` also opts in to observing
  cancellation, and the code the resolver is then handed is `130` (`128 + SIGINT`), not the
  framework-error code — an aborted run stays distinguishable from a broken one.
- `ExitCodes.Interrupted` (`int?`) maps a process signal turned into a cooperative shutdown by a
  process-signal handler; the core pipeline never produces this kind. Unset, the conventional
  `128 + signal` code the handler supplies is used, falling back to `130` when it supplies none.
- `ReplExecutionOutcome.Scope` (`ReplExitCodeScope`) tells a resolver whether it is computing the
  process exit code (`Process`, once per run) or one interactive command's shell-integration
  command-end mark (`ShellIntegrationMark`, only when a mark actually carries a code — so never with
  shell integration off, for a protocol-passthrough command, or for an abandoned prompt cycle).
- A resolver that throws does not escape the run: the table-mapped code is used and one diagnostic
  line is written to the session's error stream. Interactive sessions survive a faulty resolver, and
  a resolver failure on a failed command never replaces the original exception.
- `ReplExecutionContext.Result` exposes the handler's return value to middleware registered with
  `app.Use(...)`: readable and replaceable after `await next()`, settable by a short-circuiting
  middleware. `ReplNext` and the `Use` signature are unchanged.

### Changed — breaking: framework exit codes

These land together in **PR #85**, closing issue #81 — a consumer whose pipeline started seeing
exit `2` can search for either. (Package versions come from Nerdbank.GitVersioning at pack time, so
this file cannot name the build; the PR and issue numbers are the durable anchors.)

- Framework refusals now exit `2` instead of `1`: unknown command, ambiguous prefix, invalid global
  or command option, option collision, context validation failure, unknown `--output` format,
  ambient-command misuse in one-shot mode (`exit` while disabled, `..`, `complete` without
  `--target`), help that cannot be rendered (`UsageError`), and arguments that cannot be bound,
  converted, or resolved from context/services (`BindingError`). Handler
  failures (`Results.Error`/`Validation`/`NotFound`, exceptions) still exit `1`, help and success
  still exit `0`. Set `ExitCodes.UsageError`/`BindingError` back to `1` to restore the old numbers.
  The interactive loop reports the same resolved codes in shell-integration `D;<code>` marks,
  including the mark for a command whose dispatch threw, which previously always reported `1`.
- The `RunAsync` overloads that receive an already-built service provider now observe an
  already-cancelled caller `CancellationToken` before touching that provider:
  `ReplApp.RunAsync(args, IServiceProvider, …)` before starting hosted services, and
  `ReplApp.RunAsync(args, IReplHost, IServiceProvider, …)` before building the session overlay from
  it. A cancelled token throws `OperationCanceledException` (or returns `ExitCodes.Cancelled` when
  mapped, and `130` when only a `Resolver` is set). Previously only `CoreReplApp.RunAsync` performed
  any such check. The guarantee is per-overload and scoped to the caller's provider, not blanket:
  session setup and terminal overrides run before the check on the `IReplHost` overload, and the
  overloads that build the shared provider themselves (`Run(args)`,
  `RunAsync(args, options, …)`) construct it before the check is reached further down the chain.
- A handler that raises `OperationCanceledException` without the caller having asked for cancellation
  is now a `HandlerException`: the message is rendered and the run exits `1`, where it previously
  either propagated silently or, with `ExitCodes.Cancelled` mapped, returned the cancellation code
  with no diagnostic at all. Only the caller's own token yields `Cancelled`. The interactive loop's
  Ctrl+C semantics are unchanged.
- Interactive `help` / `?` is now classified `Help` rather than a generic success, so an application
  that maps `ExitCodes.Help` separately sees its own code in the command-end mark. An ambient command
  that *failed* is still a `UsageError`, whatever it would have reported on success.
- Hosted-service start and stop failures in `ReplApp.RunAsync` now go through the exit-code policy as
  `FrameworkError` instead of returning a hard-coded `1`. The code is resolved once, after the whole
  lifecycle, so a failed shutdown outranks the command's own outcome and a resolver is handed exactly
  one outcome per run. An already-cancelled caller token also follows `ExitCodes.Cancelled` on that
  overload, without starting hosted services.
- A binding failure and a handler exception both carry the rendered refusal in
  `ReplExecutionOutcome.Result` alongside the `Exception`, as routing refusals do, so a resolver can
  map on the framework's own diagnostic for a thrown failure and not only for a refused invocation.
- Every framework refusal and failure is now reported through one guarded path, so an
  application-supplied `IOutputTransformer` that throws can no longer escape the pipeline from any of
  them. Six refusal sites (ambiguous prefix, option collision, option parse error, context
  deep-link, context validation, global option diagnostics) sat outside any exception handler and
  ended the run with no classified outcome and no exit code; they are all guarded now, and each still
  reports `UsageError` whether the diagnostic was rendered, refused for an unknown format, or written
  unformatted because the transformer failed.
- An output transformer that throws while the framework is reporting a failure no longer escapes the
  run. Reporting a failure re-invokes the requested transformer, so one that fails consistently used
  to throw a second time from inside the catch block handling its first failure, leaving the run with
  no classified outcome and no exit code. The message now degrades to an unformatted line on stderr
  and the run keeps its `HandlerException` classification. A cancellation raised while that fallback
  runs is excluded and propagates to the cancellation policy, so `ExitCodes.Cancelled` still governs a
  run that was asked to stop.
- A hosted-service failure carries its exception in the outcome, and a startup stopped by the
  caller's own token is a `Cancelled` outcome rather than a `FrameworkError`: it prints no startup
  error and, with no cancellation policy configured, propagates the `OperationCanceledException` like
  every other path. A shutdown that fails still outranks everything the run produced, including any
  exception the pipeline was propagating — but that exception is no longer discarded: the outcome
  then carries an `AggregateException` of the stop failure and the suppressed cause, in that order,
  and both are reported.
- Framework diagnostics now go to **stderr** instead of stdout: the hosted-lifecycle failures
  (`Error: Failed to start/stop hosted service …`, which also name the wrapped cause) and the
  `Error: unknown output format '…'` refusal. The lifecycle writes are best-effort. A framework error on stdout corrupts the
  machine-readable payload of a headless run, and a torn-down transport could previously turn a
  reportable shutdown failure into an escaping write with no exit code at all. A test asserting these
  lines on a merged stdout capture needs to read stderr.
- An unknown `--output` format is a `UsageError` on every path, including while a failure was being
  reported, for an `EnterInteractive` payload — the interactive loop is then not entered — for a
  hosted protocol-passthrough refusal, and for a bare non-interactive invocation, which used to print
  help and exit `Help` without reporting the format at all. A bare invocation with a *valid* format
  still prints the human help: `--output` selects a format for a command result, and a bare
  invocation produces none. The same now holds for a scoped-context invocation that does not enter
  interactive mode (`contact --no-interactive --output:bogus`), its sibling path.
- A malformed global option is refused in the interactive loop as it is in a one-shot run. The loop
  parses globals per command and checked them on no path at all, so `hello --help --result:page-size`
  rendered help and reported the help code in its shell-integration mark. A diagnostic the caller never saw cannot stand as the run's
  outcome. When the usage error displaces a
  failure that was already being reported, `ReplExecutionOutcome.Exception` now carries that original
  failure, so a caller-chosen output format cannot erase why the run ended.

### Compatibility notes — exit codes

- MCP tool calls (nested sub-invocations) always use the built-in exit-code defaults and ignore
  `ExitCodes.Resolver`; they only test for non-zero, so `IsError` is unaffected by the policy. The
  agent-visible failure text now reads "exit code 2" for usage and binding refusals.
- `Repl.Testing`'s per-command timeout still surfaces as `TimeoutException` when the app under test
  maps `ExitCodes.Cancelled`: the handle observes the run's own outcome instead of relying on the
  exception escaping. `RunCommandAsync` documents that exception.
- A handler-thrown `InvalidOperationException` is still rendered as a validation message, but it
  is classified `HandlerException` (not `BindingError`); only exceptions raised while binding
  arguments are `BindingError`.
- A handler that returns a bare `int` (or any scalar) is unchanged: the value is rendered as data
  and the run is a `Success`. The documentation previously implied otherwise; `Results.Exit(n)`
  remains the only return-value route to an explicit exit code.
- `Repl.Testing`'s `CommandExecution.ExitCode` follows the configured policy, so application test
  suites asserting `1` for unknown commands or invalid options need to expect `2` (or configure
  `ExitCodes`).
- An `IReplResult` whose `Kind` is not `text` or `success` is a `HandlerError` (exit `1`), including
  a kind the framework does not recognize. An unclassifiable result never reports success to a
  pipeline; use `Results.Exit(n)` to choose a code deliberately.
- `ReplExecutionOutcomeKind.Interrupted` and `ExitCodes.Interrupted` are produced by automatic
  process-signal handling, described under *Added — standalone process signals* below. They are not
  reachable any other way: an application cannot supply an outcome to the table from outside the
  framework, so the kind only appears for a signal the framework itself claimed.
- Exit codes are not range-checked. Keep them within `0`-`255`: POSIX `wait` exposes only the low
  eight bits to the parent process.

### Added — standalone process signals

- `ReplRunOptions.ProcessSignalHandling` and `ProcessSignalHandlingMode` let internally configured standalone `Run`/`RunAsync` calls opt into or out of cooperative process-signal handling. The nullable option inherits the active profile default: CLI and default-interactive profiles use `Automatic`; an unprofiled `ReplApp.Create()` and `UseEmbeddedConsoleProfile()` use `None`, preserving caller-owned shutdown unless a process-owning profile is selected.
- In automatic mode, the first Ctrl+C console event—or Ctrl+Break on Windows—cancels all overlapping standalone runs in one process-wide ownership epoch and reports a successful or cancelled run as `ReplExecutionOutcomeKind.Interrupted`, which resolves through `ExitCodes.Interrupted` and defaults to exit code `130`. On supported Unix platforms, SIGTERM behaves the same way with `143`. A run that already produced a refusal or a failure keeps reporting it. A subsequent signal uses the operating-system default, and stderr diagnostics identify both steps. Explicit non-zero handler exit codes remain authoritative.

### Changed — process signal ownership

- Apps that select `UseCliProfile()` or `UseDefaultInteractive()` now take process signal ownership by
  default. Two observable changes follow for an existing consumer. Selecting
  `ProcessSignalHandlingMode.None` restores the previous behavior:
  - **Exit codes.** A run interrupted by Ctrl+C, Ctrl+Break on Windows, or SIGTERM on Unix now resolves
    to `130` or `143` where it previously produced whatever the operating-system default termination
    yielded. The interruption goes through the exit-code policy, so `ExitCodes.Interrupted` overrides
    those defaults and `ExitCodes.Resolver` observes it like any other outcome. A wrapper script or CI step that treats any non-zero code as a failure will start seeing
    these on interruption. An explicit non-zero handler exit code still takes precedence.
  - **Handler token identity.** One-shot handlers now receive a run-scoped token linked to the caller
    token instead of the caller token itself, and Repl disposes it when the run ends. No token Repl
    creates may outlive its run. A handler that stored one and used it afterwards — for detached or
    background work — sees `ObjectDisposedException` from `Register` or `WaitHandle`, and, worse,
    nothing at all from `IsCancellationRequested`, which keeps reporting `false`. Handlers that only
    await work within the run are unaffected. Apps with no profile, `UseEmbeddedConsoleProfile()`, and
    the external `IServiceProvider`/`IHost`/`IReplHost` overloads keep passing the caller token through
    unchanged.

### Operational notes — process signals

- Exit codes `130` (`128 + SIGINT(2)`) and `143` (`128 + SIGTERM(15)`) follow the widely adopted Unix/Bash convention; they are not universal .NET or Windows exit-code guarantees. SIGTERM bridging is Unix-only.
- Automatic handling has no built-in grace-period timeout. A supervisor can send a second signal to force termination. The process callbacks are installed lazily once and remain inert outside automatic runs so runtime callback snapshots cannot race handler teardown.
- For one-shot handlers, automatic mode injects a linked, run-scoped token, while external host/provider overloads pass the caller token through unchanged. Interactive commands receive a separate command-scoped linked token so Ctrl+C can cancel only the active command. Handlers must not retain any Repl-created token beyond its scope. An explicit `Automatic` request on an external overload is ignored with a diagnostic on the active error channel.
- Android, browser, iOS (including Mac Catalyst), and tvOS do not install the unsupported process-signal bridge; `Automatic` emits a diagnostic and their platform host must provide cancellation. Consumer cancellation-callback failures are also diagnosed without replacing an established `130`/`143` exit policy.

### Added — option visibility

- `.Hidden(bool isHidden = true)` on the option builder (`WithOption(name, option => option.Hidden())`)
  hides an option's canonical token, aliases, description, default, and value candidates from help,
  generated documentation, interactive/shell completion, and MCP tool schemas. The option remains a
  fully parsable, invocable part of the command line — hiding is a discovery filter, not access
  control. Available for direct command-handler parameters, options-group properties, manually
  registered global options (`ParsingOptions.GlobalOption(name).Hidden()`), and typed global options.
- `.HiddenAlias(alias, isHidden = true)` and `[ReplOption(HiddenAliases = [...])]` mark specific
  legacy/deprecated token spellings as parser-only: the canonical token and any current aliases stay
  discoverable, while the hidden alias keeps binding from the CLI/REPL for backward compatibility.
- `doc export` (and `docs <command path>`) reports `isHidden` / `isAutomationHidden` per option so an
  app author can inventory what a given command hides. Aggregate documentation (no target path) and
  MCP's `tools/list` always omit hidden options entirely — see `docs/commands.md` for the full
  visibility matrix.
- A hidden option must remain omittable for every provider that can build a discovery surface.
  Hiding a required options-group property fails immediately at `Map` time. Hiding a required direct
  handler parameter defers that check to the first time discovery runs against a real service
  provider (aggregate documentation build or MCP startup), since a DI/synthesized-progress fallback
  is only knowable once one exists — see the "Provider-aware requiredness" section of
  `docs/commands.md`.

### Changed — breaking

- `WithOption(name, configure)` is now the only fluent entry point for configuring an existing
  option's metadata (visibility included). This lands within the same change that introduces it —
  no previously published `Option(...)` API is removed by this release.

### Compatibility notes

- `doc export --json` (and other structured documentation exports) now unconditionally include the
  `isHidden` and `isAutomationHidden` fields on every option. A consumer validating that output
  against a closed schema (`additionalProperties: false`) will need to allow these two additive
  fields.
- The historical six-parameter `ParsingOptions.AddGlobalOptionCore` descriptor is preserved as a
  distinct overload (not folded into a defaulted parameter) so an already-compiled `Repl.Defaults`
  binary continues to work against a newer `Repl.Core`. The reverse is not guaranteed: this release's
  `Repl.Defaults` calls APIs that only exist in this release's `Repl.Core`, so upgrading only one of
  the two packages independently is not supported — upgrade them together.
