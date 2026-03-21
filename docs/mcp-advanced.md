# MCP Advanced: Dynamic Tools, Roots, and Session-Aware Patterns

This guide covers advanced MCP usage patterns for Repl apps:

- Tool visibility that changes per session
- Native MCP client roots
- Soft roots for clients that don't support roots
- Compatibility shims for clients that don't refresh dynamic tool lists well

> **Prerequisite**: read [mcp-server.md](mcp-server.md) first for the basic setup.
>
> **Need the plumbing details?** See [mcp-internals.md](mcp-internals.md).
>
> **Need custom transports or HTTP hosting?** See [mcp-transports.md](mcp-transports.md).

## When this page matters

Most Repl MCP servers don't need any of this.

Use the techniques in this page when:
- Available tools depend on login state, tenant, feature flags, or workspace
- The agent needs to know which directories it is allowed to work in
- Your MCP client does not support native roots
- Your MCP client does not seem to refresh its tool list after `list_changed`

If your tool list is static, stay with the default setup from [mcp-server.md](mcp-server.md).

## Client roots

A **root** is a workspace or directory that the MCP client declares as being in scope for the session.

Examples:
- The folder the user opened in the editor
- The project workspace attached to the agent
- A set of directories the agent is allowed to inspect

When the client supports native MCP roots, `Repl.Mcp` exposes them through `IMcpClientRoots`.

```csharp
app.Map("workspace roots", async (IMcpClientRoots roots, CancellationToken ct) =>
    {
        var current = await roots.GetAsync(ct);
        return current.Select(r => new { r.Name, Uri = r.Uri.ToString() });
    })
    .WithDescription("List the current MCP workspace roots")
    .ReadOnly();
```

Useful members:

| Member | Meaning |
|---|---|
| `IsSupported` | The connected client supports native MCP roots |
| `Current` | Current effective roots for the session |
| `GetAsync()` | Refreshes native roots if supported |
| `HasSoftRoots` | Fallback roots were initialized manually |
| `SetSoftRoots()` / `ClearSoftRoots()` | Manage fallback roots for the current session |

## Session-aware routing

Because `IMcpClientRoots` is injectable, you can use it in command handlers and in module presence predicates.

That lets you expose tools only when a certain MCP capability or session state is available.

```csharp
app.MapModule(
    new WorkspaceModule(),
    (IMcpClientRoots roots) => roots.IsSupported);
```

Typical session-aware conditions:
- Roots are available
- Soft roots were initialized
- The current tenant or login is known
- A module should appear only for one agent session

## Guidance: MCP-only vs workspace-aware commands

`IMcpClientRoots` is MCP-scoped, but that does not automatically mean every command using it must be MCP-only.

There are two useful patterns:

### Pattern 1: MCP-only commands

Use this when the command only makes sense inside an MCP session.

```csharp
app.MapModule(
    new WorkspaceBootstrapModule(),
    (IMcpClientRoots? roots) => roots is not null);
```

This is the simplest option when:
- the command exists only to help an agent initialize MCP session state
- the command depends directly on MCP capabilities
- showing it in CLI or interactive Repl would be confusing

### Pattern 2: Workspace-aware commands

Use this when the command should work both inside and outside MCP.

In that case, treat MCP roots as just one possible source of workspace context, not the only source.

Typical workspace sources:

1. native MCP roots
2. MCP soft roots
3. session state in Repl
4. a command-line argument or explicit option
5. the process current directory

For example:

```csharp
app.Map("workspace status", async (IMcpClientRoots? roots, IReplSessionState state, CancellationToken ct) =>
    {
        var workspace =
            roots is not null
                ? (await roots.GetAsync(ct)).FirstOrDefault()?.Uri?.ToString()
                : state.Get<string>("workspace.path");

        return workspace is null
            ? "No workspace selected."
            : $"Workspace: {workspace}";
    })
    .ReadOnly();
```

And you can pair that with a general-purpose Repl command:

```csharp
app.Map("workspace set {path}", (IReplSessionState state, string path) =>
    {
        state.Set("workspace.path", path);
        return "Workspace updated.";
    });
```

This pattern is often better than making everything MCP-only.

### Recommendation

When a command needs a working directory or workspace, design it around a **workspace resolution strategy** instead of assuming one single source.

That usually makes the command:
- more reusable
- easier to test
- usable from CLI, hosted sessions, and MCP
- easier to adapt when some clients support roots and others do not

## Soft roots fallback

Some clients do not support MCP roots at all. In that case, a practical workaround is to expose an initialization tool only when roots are unavailable.

The agent can call that tool first to establish one or more **soft roots** for the session.

```csharp
app.MapModule(
    new SoftRootsInitModule(),
    (IMcpClientRoots roots) => !roots.IsSupported);

app.MapModule(
    new WorkspaceModule(),
    (IMcpClientRoots roots) => roots.IsSupported || roots.HasSoftRoots);

sealed class SoftRootsInitModule : IReplModule
{
    public void Map(IReplMap app)
    {
        app.Map("workspace init {path}", (IMcpClientRoots roots, string path) =>
            {
                // SetSoftRoots invalidates routing for the current MCP session.
                roots.SetSoftRoots([new McpClientRoot(new Uri(path, UriKind.Absolute), "workspace")]);
                return "Workspace initialized.";
            })
            // Message to agent asking it to set soft routes
            .WithDescription("Before using other workspace tools, call this to set the working directory.");
    }
}
```

Recommended instruction to give the agent:

> If `workspace_init` is available, call it first with the working directory you should operate in.

This is often the simplest fallback for editor integrations or agent hosts that don't implement native roots.

## Dynamic tool compatibility shim

Some MCP clients receive `notifications/tools/list_changed` but do not refresh their tool list correctly.

If your app has a dynamic tool list, you can opt in to a compatibility shim:

```csharp
app.UseMcpServer(o =>
{
    o.DynamicToolCompatibility = DynamicToolCompatibilityMode.DiscoverAndCallShim;
});
```

When enabled:

1. The first `tools/list` returns only `discover_tools` and `call_tool`
2. The server emits `notifications/tools/list_changed`
3. Later `tools/list` calls return the real tool set

This lets limited clients continue operating:
- `discover_tools` returns the current real tools and schemas
- `call_tool` invokes a real tool by name and arguments

Use this only when you need it.

Good candidates:
- Tools appear after authentication
- Tools depend on roots or soft roots
- Tools vary by session or runtime context

Avoid it when:
- Your tool list is static
- Your client already handles `list_changed` correctly

## Choosing the right fallback

| Problem | Recommended approach |
|---|---|
| Client supports roots and refreshes tools correctly | Use the default MCP setup |
| Client does not support roots | Add a soft-roots initialization tool |
| Client supports tools but misses dynamic refreshes | Enable `DiscoverAndCallShim` |
| Client has both issues | Use soft roots and, if needed, the dynamic tool shim |

## Troubleshooting patterns

### The agent doesn't see tools that should appear later

Check:
- Your app calls `InvalidateRouting()` when session-driven state changes
- The client actually refreshes after `list_changed`
- `DynamicToolCompatibility` is enabled if the client is weak on dynamic discovery

If needed, see [mcp-server.md](mcp-server.md#troubleshooting) for the quick checklist and [mcp-internals.md](mcp-internals.md) for the behavior details.

### The agent doesn't know which workspace to use

Check:
- Whether the client supports native roots
- Whether a roots-aware tool can inspect `IMcpClientRoots`
- Whether you need a soft-roots init tool

### My module predicate depends on roots but never activates

Check:
- Whether the client actually advertises roots support
- Whether you need `await roots.GetAsync(...)` in a handler rather than only a predicate
- Whether soft roots are a better fit for that client
