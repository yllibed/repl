# Changelog

Notable consumer-facing changes to the Repl packages. Versions are assigned automatically by
Nerdbank.GitVersioning at pack time; this file groups changes by theme instead of by release.

## Unreleased

### Changed — MCP SDK 2.2.0

- `Repl.Mcp` now builds on `ModelContextProtocol` **2.2.0** (from 1.4.1). The SDK is a transitively
  public dependency, so a consumer referencing `Repl.Mcp` must move to the 2.x line. The server stays
  multi-revision: it keeps the `initialize` handshake for existing hosts while also serving the
  sessionless `2026-07-28` revision.
- **Breaking:** `IMcpFeedback.SendMessageAsync` takes a Repl-owned `McpMessageLevel` instead of the
  SDK's `LoggingLevel`. `LoggingLevel` carries the SDK's `MCP9005` deprecation, and Repl's internal
  `#pragma` never covered a *consumer's* compilation — anyone building with warnings as errors got a
  hard error on a Repl signature. The new enum has the same members and numeric values.
- **User feedback delivery depends on the negotiated revision.** `2026-07-28` removed
  `logging/setLevel` and forbids emitting `notifications/message` for a request that declared no
  `_meta/io.modelcontextprotocol/logLevel` (SEP-2575), so Repl now honours that. Messages that cannot
  be sent as notifications are appended to the **tool result** after the command's own payload, so no
  host loses feedback. Initialize-era clients keep the previous session-wide behaviour, unchanged.
  A caller reading only the first content block, or `StructuredContent`, is unaffected.
- **Discovery notifications are delivered by the SDK's own fan-out** rather than broadcast by Repl.
  Initialize-era clients keep receiving unsolicited `*/list_changed`; a `2026-07-28` client receives
  only the types it requested through `subscriptions/listen`, tagged with its listen request id. A
  modern client that opens no subscription receives none — as the specification requires. List
  results carry `ttlMs: 0`, so such a client re-lists on demand instead of caching.
- `.LongRunning()` remains Repl-local metadata and emits nothing on the protocol surface; SDK 2.x
  removed the per-tool `Tool.Execution` augmentation. Protocol-level task support returns once Repl
  integrates `ModelContextProtocol.Extensions.Tasks` (tracked in issue #72).

### Compatibility notes — MCP

- **Known limitation.** Soft roots ([`docs/mcp-advanced.md`](docs/mcp-advanced.md#soft-roots-fallback))
  set by one connection are visible to every other connection created from the same
  `BuildMcpServerOptions()` result. Client capabilities *are* isolated per request on that path;
  cross-call state is not, because `2026-07-28` removed protocol sessions and that path has no
  per-connection identity. Host one server per process (`mcp serve`), or take the workspace as an
  explicit command argument.
- `docs/mcp-transports.md` previously claimed each connection has "its own I/O capture" and "its own
  session-aware routing state". I/O capture is per *invocation*, and session-aware routing state
  exists only under `mcp serve`. The doc now states what is isolated at which boundary.

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
