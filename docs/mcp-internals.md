# MCP Internals: Concepts and Under-the-Hood Behavior

This guide explains how the Repl MCP integration works internally and what problems the advanced features are solving.

> If you just want recipes and snippets, use [mcp-server.md](mcp-server.md) and [mcp-advanced.md](mcp-advanced.md).

## Roots

In MCP, a **root** is a URI the client declares as being in scope for the session.

In practice, a root often represents:
- An opened project folder
- A working directory
- A boundary for what the agent should inspect or modify

Roots are useful because they give the server session-specific workspace context without inventing its own protocol.

## Native roots vs soft roots

There are two ways a Repl MCP session can get roots:

| Kind | Source | When to use |
|---|---|---|
| Native roots | The MCP client implements the roots capability | Preferred |
| Soft roots | Your app asks the agent to initialize workspace paths manually | Fallback |

`IMcpClientRoots` abstracts over both.

If native roots are available, `GetAsync()` requests them from the client.
If not, your app can establish soft roots with `SetSoftRoots()`.

## Why `IMcpClientRoots` is MCP-only

Roots are session-scoped MCP data. They don't make sense as a generic `Repl.Core` concept for terminal or non-MCP execution.

That's why `IMcpClientRoots` lives in `Repl.Mcp` and is injected only for MCP execution.

## Session-aware routing

Repl supports dynamic module presence. For MCP, this matters because available tools may depend on:
- Client capabilities
- Session state
- Roots
- Authentication
- Tenant or workspace selection

To support that, the MCP integration builds its documentation model and MCP surfaces using the current MCP session service provider, not just the app root service provider.

That is what makes session-scoped services like `IMcpClientRoots` visible to:
- Module presence predicates
- Tool handlers
- Prompt handlers
- Resource handlers

## Why tools can be dynamic

In a normal CLI app, the command graph is usually static.

In MCP, the effective tool set may change during a session. Examples:
- A login tool disappears after authentication and admin tools appear
- Tools appear only after a workspace is known
- Session-scoped modules become active after an initialization call

When this happens, the app calls `InvalidateRouting()`.
The MCP layer then rebuilds the active graph and emits:

- `notifications/tools/list_changed`
- `notifications/resources/list_changed`
- `notifications/prompts/list_changed`

## Why the compatibility shim exists

The MCP protocol already has a way to refresh discovery: `list_changed`.

The problem is client support. Some agents:
- don't implement `list_changed`
- implement it partially
- receive the notification but keep using a stale tool list

That is what `DynamicToolCompatibilityMode.DiscoverAndCallShim` is solving.

Instead of assuming the client will refresh properly, the server can:

1. Expose `discover_tools`
2. Expose `call_tool`
3. Notify that the list changed
4. Fall back to the real tools on later `tools/list`

So even a weak client can still:
- discover the real tools manually
- call those tools manually

This mode is intentionally opt-in because it adds extra behavior only needed by dynamic-tool apps targeting imperfect clients.

## Why soft roots are useful

Native roots are the cleanest solution, but not all clients implement them.

Without roots, the server still needs some way to learn:
- which project the agent is working on
- which directories are allowed
- which workspace-specific tools should appear

Soft roots are a simple convention:
- expose an init tool only when roots are unavailable
- tell the agent to call it first
- store those roots in the MCP session
- invalidate routing so the session-aware tool graph can update

This pattern is not part of the MCP specification.
It is an application-level fallback for clients with incomplete capability support.

## Why `discover_tools` and `call_tool` are reserved names

When the compatibility shim is enabled, those two names become part of the protocol surface exposed by the app.

To avoid ambiguity, Repl reserves:
- `discover_tools`
- `call_tool`

If a real command would flatten to one of those names while the shim is enabled, startup fails with an explicit collision error.

## Transport separation

Custom transports and HTTP hosting are advanced too, but they are a separate concern.

They answer:
- How does MCP traffic reach the server?

The topics in this page answer:
- How does the tool graph adapt per session?
- How does the server reason about roots and client capability gaps?

For transport and hosting integration, see [mcp-transports.md](mcp-transports.md).
