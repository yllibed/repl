# Repl.Logging

`Repl.Logging` provides ambient session-aware logging context for apps built with Repl Toolkit.

It keeps `Microsoft.Extensions.Logging` as the logging contract and exposes Repl execution metadata so apps can enrich or route operator logs when needed.

`AddReplLogging()` registers the minimal logging services needed for `ILogger<T>` injection and adds REPL execution metadata through ambient context and scopes.

What this package does:

- keeps logging provider selection in application code
- does not add a visible logging sink by default
- does not treat logs as user-facing output

Use `IReplInteractionChannel` for user-facing notices, warnings, and problem summaries.
