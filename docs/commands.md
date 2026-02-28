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
- CGI-style process protocols (stdin/stdout contract), including AGI-style integrations

Not typical passthrough targets:

- socket-first variants such as FastCGI/FastAGI (their protocol stream is on TCP, not app `stdout`)

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
- `stdout` remains reserved for protocol payloads
- interactive follow-up is skipped after command execution
