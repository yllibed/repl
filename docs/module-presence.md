# Conditional Module Presence

This page explains how to make modules appear/disappear dynamically at runtime.

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
