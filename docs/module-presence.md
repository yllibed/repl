# Conditional Module Presence

This page explains how to make modules appear/disappear dynamically at runtime.

> **Serving MCP?** On revision `2026-07-28` the advertised tool set must not vary per connection or
> change as a side effect of another request, so discovery answers every session-scoped question with
> fixed answers: capability checks read as supported, soft roots as absent, the root list as empty,
> **and the session state as empty**. Whatever your predicate returns under those answers is what
> every client is offered, so a negated gate such as `!roots.IsSupported` matches for nobody even
> though it reads a capability — and the sign-in flow below reveals nothing, because the state it
> writes is not what discovery reads. Gate on something that does not change, or map the command
> unconditionally and refuse inside it. A command that *is* advertised stays callable, so refusing
> inside it is what the caller can act on. The predicate still runs everywhere else, and the earlier
> revisions are unaffected — see
> [Conformance](mcp-conformance.md#what-this-means-when-you-write-commands).

## Why

Sometimes the command surface depends on session state:

- signed-out vs signed-in experience
- local CLI vs hosted session
- feature toggles

Repl supports this at the **module** level, not at command/context level.

## API

Use the `MapModule` overload with a presence predicate:

```csharp
app.MapModule(
    new SignedInModule(),
    context =>
    {
        return context.Channel != ReplRuntimeChannel.Session
            && context.SessionState.TryGet<bool>("auth.signed_in", out var signedIn)
            && signedIn;
    });
```

Predicate inputs:

- `ModulePresenceContext context`:
  - `Channel`: `Cli`, `Interactive`, or `Session`.
  - `SessionState`: current mutable session state.
  - `SessionInfo`: current read-only session metadata.

## Injectable predicates in ReplApp

In `ReplApp` (Defaults), you can also use an injectable delegate:

```csharp
app.MapModule(
    new SignedInModule(),
    (IReplSessionState state, ReplRuntimeChannel channel) =>
    {
        return channel != ReplRuntimeChannel.Session
            && state.TryGet<bool>("auth.signed_in", out var signedIn)
            && signedIn;
    });
```

Rules for this overload:

- Return type must be `bool`.
- Delegate parameters are resolved from DI.
- Special parameters:
  - `ModulePresenceContext` gets the current module-presence context.
  - `ReplRuntimeChannel` gets the current runtime channel.
  - `IReplSessionState` gets current session state.
  - `IReplSessionInfo` gets current session metadata.

Note: `CoreReplApp` exposes the typed predicate overload `Func<ModulePresenceContext, bool>`.

## Runtime behavior

- Predicates are evaluated when the active routing graph is resolved (routing, help, completion/autocomplete paths).
- Resolved presence is cached in the active routing graph.
- Module presence can change during the same interactive session, but cache invalidation is explicit.
- If a module is not present, its routes/contexts are treated as absent.
- In `ReplApp` (Defaults), injectable predicate delegates are adapted to compiled invokers (not per-call `DynamicInvoke`).

When the state that drives module presence changes, call:

```csharp
app.InvalidateRouting();
```

Example flow:

1. Signed-out module is present.
2. User runs `auth login` (updates session state).
3. App invalidates routing cache.
4. Signed-in module becomes present on next command resolution.

This flow works in the console, over the earlier MCP revisions, and anywhere else. It does **not**
change what an MCP client on `2026-07-28` is offered: that revision forbids the advertised set from
moving as a side effect of another request, which is exactly what step 2 would be. See the note at the
top of this page.

## Conflict policy

If two **active** modules map the same route, **last registration wins**.

This lets you intentionally layer experiences:

- base module first
- override module later

## Channel-aware modules

You can scope a module to CLI only:

```csharp
app.MapModule(
    new ShellCompletionModule(),
    context => context.Channel == ReplRuntimeChannel.Cli);
```

In `Interactive` and `Session`, that module surface is absent.

## Guidelines

- Keep predicates fast, side-effect free, and allocation-light.
- Avoid I/O or network calls inside predicates.
- Prefer reading precomputed state from session/app services instead of recalculating expensive checks in predicates.
- Put state checks in session/app services, not in globals.
- Prefer module-level switching for coherent experiences.
