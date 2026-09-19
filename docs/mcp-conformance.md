# MCP Specification Conformance

> **This page is for you if** you need to know exactly what Repl guarantees on a given MCP protocol revision, or why a behaviour differs between two clients.
>
> **Purpose:** One place to answer "what does Repl do on revision X". Every claim names the regression test that pins it.
> **Prerequisite:** [MCP overview](mcp-overview.md)
> **Related:** [Reference](mcp-reference.md) · [Transports](mcp-transports.md) · [Module presence](module-presence.md)

## Supported revisions

| Revision | Era | How Repl serves it |
| --- | --- | --- |
| `2026-07-28` | Modern — version, identity and capabilities travel as per-request `_meta`; there is no session | Default for an SDK client that does not pin a version |
| `2025-11-25` | Legacy — a session is established by an `initialize` handshake | Served when the client pins it, or opens with `initialize` |

Repl is a dual-era server: a request carrying modern `_meta` is served statelessly, and an
`initialize` request selects legacy semantics. Pinned by
`Given_McpIntegration.When_ClientPinsLegacyProtocolVersion_Then_InitializeHandshakeAndToolsWork`.

The era is a property of the request, not of the transport. **Statelessness is not an HTTP mode**: on
`2026-07-28` stdio has no session either, even though the process and its pipe outlive many requests.
Repl still caches per connection — that is an optimisation the protocol says nothing about — but
nothing a client can *observe* may depend on the connection.

## What differs by revision

| Behaviour | `2025-11-25` | `2026-07-28` | Pinned by |
| --- | --- | --- | --- |
| Advertised tool set may vary by client capabilities | Yes | **No** — invariant | `Given_McpConcurrentSessions.When_LegacySessionsShareAGatedGraph_Then_EachSeesItsOwnTools` / `...When_ModernSessionsShareAGatedGraph_Then_TheAdvertisedSetIsInvariant` |
| Soft roots reveal commands | Yes | **No** — they still resolve at execution | `Given_McpRootsAndDynamicTools.When_LegacyClientDoesNotSupportRoots_Then_SoftRootsCanInitializeWorkspace` / `...When_ModernClientSetsSoftRoots_Then_TheyResolveWithoutChangingTheAdvertisedSet` |
| Compatibility bootstrap (`DynamicToolCompatibilityMode.DiscoverAndCallShim`) | First `tools/list` answers `discover_tools` / `call_tool`, the next the real catalog | Not served — the real catalog from the first request | `Given_McpConcurrentSessions.When_ShimEnabledAndTwoLegacySessionsList_Then_EachSessionGetsTheIntro` / `...When_ShimEnabledAndAModernSessionLists_Then_TheCatalogIsTheSameEveryTime` |
| Feedback on a failed tool, prompt or resource | Emitted as notifications | Carried in the surfaced error | `Given_McpUserFeedback.When_AFailingPromptDeclaresNoLogLevel_Then_FeedbackRidesInTheError` / `...When_AFailingResourceReadDeclaresNoLogLevel_Then_FeedbackRidesInTheError` / `Given_McpApps.When_AFailingUiResourceReadEmitsFeedback_Then_ItRidesInTheError` |
| `cacheScope` / `ttlMs` on list results | Absent — not in the schema | Set to private, zero TTL | `Given_McpIntegration.When_ClientPinsLegacyProtocolVersion_Then_ListResultsCarryNoCacheHints` / `Given_McpConcurrentSessions.When_ModernClientListsTools_Then_ListResultIsTaggedPrivateAndStale` |
| Message notifications for a request that declared no log level | Emitted, subject to the session threshold | **Not emitted** — the feedback rides in the result instead | `Given_McpUserFeedback.When_RequestDeclaresNoLogLevel_Then_FeedbackRidesInTheToolResultInstead` |
| Same, through `prompts/get` | Emitted | Rides in the prompt result after the payload, and in the surfaced error when the prompt fails | `Given_McpUserFeedback.When_APromptDeclaresNoLogLevel_Then_FeedbackRidesInThePromptResultInstead` / `...When_AFailingPromptDeclaresNoLogLevel_Then_FeedbackRidesInTheError` |

## Tool list invariance on `2026-07-28`

The tools chapter of that revision states:

> This set **MAY** be empty and **MAY** change over time (see List Changed Notification), but
> **MUST NOT** vary per-connection or as a side effect of other requests on the connection. The set
> **MAY** vary by the authorization presented on the request — for example, returning only the tools
> the caller's granted scopes permit — since credentials are per-request input, not connection state.

The earlier revisions carry no such rule, which is why the behaviours above are split by era rather
than changed outright.

Two consequences, and they are different problems:

- **Per-connection variance.** A module gated on `IMcpClientRoots.IsSupported` would advertise a
  different set to a client that declares roots. On `2026-07-28` discovery answers every
  per-connection question with a constant, so the set no longer depends on who asked.
- **Variance as a side effect.** A module gated on `HasSoftRoots` would appear after a `tools/call`
  set them — changing the caller's own advertised set. This one needs no second connection to be
  observable, so it is the half that matters even on plain stdio. A module gated on session state is
  the same shape and reaches further: `IReplSessionState` is mutable, shared with execution, and a
  command can write it and call `InvalidateRouting()`. The compatibility bootstrap is that shape too —
  an intro catalog followed by the real one — and is therefore legacy-only.

The discovery view reaches no live session-scoped service at all, rather than neutralising member by
member: `IsSupported`, `HasSoftRoots`, `Current` and `GetAsync` are all connection state, and
forwarding any one of them reopens the hole. The frozen set is the four capability services plus
`IReplSessionState` and `IReplSessionInfo` — every session-scoped input a presence predicate can
receive by injection.

A predicate that injects an **application** service of its own is outside that set by construction:
Repl cannot know which of your singletons is stable and which a command mutates. Gate on something
that does not change, or keep the command mapped unconditionally and fail inside it.

What stays allowed is a set that **changes over time** for everyone: `InvalidateRouting()` is
application-global, and the resulting `notifications/*/list_changed` reaches every connection with the
same new graph.

### What this means when you write commands

Module presence predicates still work, and still work on both eras. On `2026-07-28` discovery runs
them against **fixed answers** instead of against the client:

| Member | What discovery answers |
| --- | --- |
| `IsSupported` (roots, sampling, elicitation), `IsLoggingSupported`, `IsProgressSupported` | `true` |
| `HasSoftRoots` | `false` |
| `Current`, `GetAsync()` | empty |

Whatever your predicate returns under those answers is what **every** client is offered. The bucket a
command lands in therefore follows the predicate's *result*, not which member it reads — a negated
gate lands in the opposite bucket from the plain one. Two consequences worth stating in full:

- A predicate that comes out **true** — `roots.IsSupported`, `sampling.IsSupported` — advertises its
  command to every client. Repl guarantees such a command is **reachable**: execution decides presence
  from the same fixed answers, so what was advertised can be called, and the handler runs with the
  real client rather than the catalog's view of it. Writing the failure is then yours — return an
  error naming the missing capability rather than relying on the command being absent. That is the
  shape the specification prescribes: a tool execution error is "actionable feedback that language
  models can use to self-correct".
- A predicate that comes out **false** — `!roots.IsSupported`, `roots.HasSoftRoots`,
  `roots.Current.Count > 0` — advertises its command to no client at all, and it disappears with no
  error to explain it. Map those commands unconditionally instead.

The soft-roots bootstrap pattern gates on `!roots.IsSupported`, so despite reading a capability it
lands in the second group. That is the reason to read the rule off the predicate's result rather than
off the member it consults.

Execution is untouched either way. `SetSoftRoots` still works, and `IMcpClientRoots.Current` answers
with the connection's real roots under `mcp serve`; on a reused `BuildMcpServerOptions()` result it
answers empty until *this request* has called `GetAsync`, which is that path's documented contract.

For state that must survive across calls, the specification's own answer is an explicit handle
returned by a creation tool and passed back as an argument, rather than implicit connection state.

## Deliberate gaps

| Gap | Why | Tracked |
| --- | --- | --- |
| No per-caller command graph | The one variance `2026-07-28` permits is by the authorization presented on the request. Repl has no request-authorization concept yet, so it advertises one graph to everyone. | [#97](https://github.com/yllibed/repl/issues/97) |
| Explicitly registered prompts cannot inject the MCP capability services | The SDK resolves their parameters from a scope taken from the inner container, which the service overlay does not reach. | [#96](https://github.com/yllibed/repl/issues/96) |
| `*/list_changed` is advertised on the reusable-options path but never fires there | The SDK forces the flag true for any non-null collection, and the pre-built catalog always supplies one. | [#94](https://github.com/yllibed/repl/issues/94) |
| A multi-connection custom transport sees considerations this page does not solve | `mcp serve` is one connection per process; a host that multiplexes connections over one options instance owns the isolation questions that follow. See [Transports](mcp-transports.md). | — |

## Extensions and SEPs

| Identifier | Status in Repl |
| --- | --- |
| SEP-2549 — `cacheScope` / `ttlMs` | Set on list and resource results, on `2026-07-28` only |
| SEP-2575 — stateless requests: per-request `_meta`, and no message notification without a declared log level | The protocol version in `_meta` is what selects the era on every request; the log-level rule is honoured, and the feedback is appended to the result instead |
| SEP-2577 — Roots, Sampling and Logging deprecated | Still supported for the compatibility path; the SDK reports them under diagnostic `MCP9005` |
| SEP-2567 — protocol sessions removed; list endpoints made session-independent | The source of the invariance rule above; cross-call state becomes an explicit handle passed as a tool argument, not connection state |
| Tasks (`io.modelcontextprotocol/tasks`) | Not advertised; the SDK moved it out of the core package |
