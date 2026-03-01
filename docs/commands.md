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
app.Map(
    "render",
    ([ReplOption(Aliases = ["-m"])] RenderMode mode = RenderMode.Fast,
     [ReplOption(ReverseAliases = ["--no-verbose"])] bool verbose = false) =>
        $"{mode}:{verbose}");
```

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

## Supported parameter conversions

Handler parameters support native conversion for:

- `FileInfo` from string path tokens (for example `--path ./file.txt`)
- `DirectoryInfo` from string path tokens (for example `--path ./folder`)

Path existence is not validated at parse time; handlers decide validation policy.

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
