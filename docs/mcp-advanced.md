# MCP Advanced: Dynamic Tools, Roots, and Session-Aware Patterns

> **This page is for you if** your tool list changes per session, you need workspace roots, your client misses dynamic tool refreshes, or you need advanced MCP Apps patterns.
>
> **Purpose:** Advanced MCP patterns for dynamic, session-aware, or complex apps.
> **Prerequisite:** [MCP overview](mcp-overview.md)
> **Related:** [Reference](mcp-reference.md) · [Sampling & elicitation](mcp-agent-capabilities.md) · [Transports](mcp-transports.md)

## When this page matters

Most Repl MCP servers don't need any of this. Use the techniques here when:

- Available tools depend on login state, tenant, feature flags, or workspace
- The agent needs to know which directories it is allowed to work in
- Your MCP client does not support native roots
- Your MCP client does not refresh its tool list after `list_changed`
- You need advanced MCP Apps patterns (WebAssembly, display modes, launcher text)

If your tool list is static, stay with the default setup from [mcp-overview.md](mcp-overview.md).

## Client roots

A **root** is a URI the client declares as being in scope for the session — typically an opened project folder, a working directory, or a boundary for what the agent should inspect or modify.

Roots give the server session-specific workspace context without inventing a custom protocol. When the client supports native MCP roots, `Repl.Mcp` exposes them through `IMcpClientRoots`.

```csharp
using Repl.Mcp;

app.Map("workspace roots", async (IMcpClientRoots roots, CancellationToken ct) =>
    {
        var current = await roots.GetAsync(ct);
        return current.Select(r => new { r.Name, Uri = r.Uri.ToString() });
    })
    .WithDescription("List the current MCP workspace roots")
    .ReadOnly();
```

| Member | Meaning |
|---|---|
| `IsSupported` | The connected client supports native MCP roots |
| `Current` | Current effective roots for the session |
| `GetAsync()` | Refreshes native roots if supported |
| `HasSoftRoots` | Fallback roots were initialized manually |
| `SetSoftRoots()` / `ClearSoftRoots()` | Manage fallback roots for the current session |

> **Why `IMcpClientRoots` is MCP-only:** Roots are session-scoped MCP data. They don't make sense as a generic `Repl.Core` concept for terminal or non-MCP execution. That's why the interface lives in `Repl.Mcp` and is injected only for MCP sessions.

## Session-aware routing

Because `IMcpClientRoots` is injectable, you can use it in command handlers and in module presence predicates. That lets you expose tools only when a certain MCP capability or session state is available.

```csharp
using Repl.Mcp;

app.MapModule(
    new WorkspaceModule(),
    (IMcpClientRoots roots) => roots.IsSupported);
```

> **How this works internally:** The MCP integration builds its documentation model and MCP surfaces using the current MCP session service provider, not just the app root service provider. This makes session-scoped services like `IMcpClientRoots` visible to module presence predicates, tool handlers, prompt handlers, and resource handlers.

Typical session-aware conditions:

- Roots are available
- Soft roots were initialized
- The current tenant or login is known
- A module should appear only for one agent session

### MCP-only vs workspace-aware commands

**Pattern 1: MCP-only** — the command only makes sense inside an MCP session:

```csharp
app.MapModule(
    new WorkspaceBootstrapModule(),
    (IMcpClientRoots? roots) => roots is not null);
```

Use when: the command helps an agent initialize MCP session state or depends directly on MCP capabilities.

**Pattern 2: Workspace-aware** — the command works both inside and outside MCP:

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

**Recommendation:** When a command needs a working directory, design it around a workspace resolution strategy (native roots → soft roots → session state → CLI argument → current directory) instead of assuming one single source. This makes the command more reusable, easier to test, and usable from CLI, hosted sessions, and MCP.

## Soft roots fallback

Some clients do not support MCP roots at all. Soft roots are a simple application-level convention (not part of the MCP specification): expose an init tool when roots are unavailable, store the paths in the MCP session, and invalidate routing so the session-aware tool graph updates.

```csharp
using Repl.Mcp;

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
            // Message to agent asking it to set soft roots
            .WithDescription("Before using other workspace tools, call this to set the working directory.");
    }
}
```

Recommended agent instruction:

> If `workspace_init` is available, call it first with the working directory you should operate in.

## Dynamic tool compatibility shim

In MCP, the effective tool set may change during a session — a login tool disappears after authentication, tools appear after a workspace is known, session-scoped modules activate. When this happens, the app calls `InvalidateRouting()`, and the MCP layer rebuilds the active graph and emits `notifications/tools/list_changed`, `notifications/resources/list_changed`, and `notifications/prompts/list_changed`.

The problem is client support. Some agents don't implement `list_changed`, implement it partially, or receive the notification but keep using a stale tool list.

`DynamicToolCompatibilityMode.DiscoverAndCallShim` solves this:

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

Even a weak client can then discover the real tools manually and call them via `call_tool`.

> **Reserved names:** When the shim is enabled, `discover_tools` and `call_tool` become part of the protocol surface. If a real command flattens to one of those names, startup fails with a collision error.

| Use when | Avoid when |
|---|---|
| Tools appear after authentication | Tool list is static |
| Tools depend on roots or soft roots | Client handles `list_changed` correctly |
| Tools vary by session or runtime context | |

## Choosing the right fallback

| Problem | Recommended approach |
|---|---|
| Client supports roots and refreshes tools correctly | Use the default MCP setup |
| Client does not support roots | Add a soft-roots initialization tool |
| Client supports tools but misses dynamic refreshes | Enable `DiscoverAndCallShim` |
| Client has both issues | Use soft roots and the dynamic tool shim |

## MCP Apps advanced patterns

For the basic MCP Apps setup, start with [mcp-overview.md](mcp-overview.md#mcp-apps) and [mcp-reference.md](mcp-reference.md#mcp-apps). This section covers patterns for more complex UIs.

### One mapping, two MCP surfaces

`AsMcpAppResource()` keeps authoring simple: one mapping produces both the launcher tool metadata and the UI resource.

The handler return value is used for `resources/read` as `text/html;profile=mcp-app`. Tool calls return launcher text instead of raw HTML. Control the launcher text with `WithMcpAppLauncherText(...)`:

```csharp
app.Map("contacts dashboard", (IContactDb contacts) => BuildHtml(contacts))
    .WithDescription("Open the contacts dashboard")
    .AsMcpAppResource()
    .WithMcpAppLauncherText("Opening the contacts dashboard.");
```

`WithMcpApp("ui://...")` is available for advanced cases where a normal tool points at a separately registered UI resource:

```csharp
app.Map("status dashboard", (IStatusStore store) => store.GetSummary())
    .ReadOnly()
    .WithMcpApp("ui://status/dashboard");
```

### Generated UI resource URIs

`AsMcpAppResource()` generates a `ui://` URI from the route path, including nested contexts:

```csharp
app.Context("viewer", viewer =>
{
    viewer.Context("session {id:int}", session =>
    {
        session.Map("attach", (int id) => BuildHtml(id))
            .AsMcpAppResource();
    });
});
// Produces: ui://viewer/session/{id}/attach
```

MCP URI templates keep the variable name but not the Repl route constraint (`{id:int}` → `{id}`). Validation happens at dispatch time.

Pass an explicit URI only when you need a stable public URI decoupled from the route path.

### Display preferences

MCP Apps standard display modes are `inline`, `fullscreen`, and `pip`. Hosts decide what they support.

```csharp
app.Map("contacts dashboard", (IContactDb contacts) => BuildHtml(contacts))
    .AsMcpAppResource()
    .WithMcpAppDisplayMode(McpAppDisplayModes.Fullscreen);
```

If the HTML uses the MCP Apps JavaScript bridge, check host capabilities first:

```javascript
const modes = app.getHostContext()?.availableDisplayModes ?? [];
if (modes.includes("fullscreen")) {
  await app.requestDisplayMode({ mode: "fullscreen" });
}
```

For host-specific hints not yet modeled by Repl:

```csharp
.WithMcpAppUiMetadata("presentation", "flyout");
```

See [mcp-reference.md](mcp-reference.md#mcp-apps-host-compatibility) for host compatibility details.

### WebAssembly UIs

For WebAssembly UIs such as Uno-Wasm:

1. Map a `ui://` app resource that returns a small HTML shell
2. Serve published static assets (`embedded.js`, `_framework/*`, `.wasm`, fonts) from an HTTP endpoint
3. Inject the HTTP base URL into the generated shell
4. Set CSP metadata for asset and fetch domains

```csharp
var assetBaseUri = new Uri("http://127.0.0.1:5123/");

app.Map("contacts dashboard", () => BuildUnoShellHtml(assetBaseUri))
    .AsMcpAppResource()
    .WithMcpAppCsp(new McpAppCsp
    {
        ResourceDomains = [assetBaseUri.ToString()],
        ConnectDomains = [assetBaseUri.ToString()],
    });
```

Keep the shell and asset server host-aware: clients may preload or cache UI resources, and not every host supports every display mode or browser capability.

## Troubleshooting

### New tools are not visible to the agent

- Check that your app calls `InvalidateRouting()` when the effective command graph changes
- Check that the MCP client actually refreshes after `notifications/tools/list_changed`
- Check whether your tool list is truly dynamic per session
- If the client ignores dynamic tool refreshes, enable `DynamicToolCompatibilityMode.DiscoverAndCallShim`

### The agent does not see workspace roots

- Check whether the client supports native roots
- If not, add a soft-roots initialization tool (see [above](#soft-roots-fallback))

### My module predicate depends on roots but never activates

- Check whether the client actually advertises roots support
- Consider using `await roots.GetAsync(...)` in a handler rather than only a predicate
- Consider whether soft roots are a better fit for that client

### My MCP App shows HTML text in the chat

- Use `.AsMcpAppResource()` on the HTML-producing command — Repl returns launcher text for tool calls and reserves the HTML for `resources/read`
- Restart or reload the MCP server in the client — some hosts cache tool lists

### My MCP App does not open fullscreen

- Check whether the host supports fullscreen (VS Code currently renders inline only)
- For hosts that support display mode changes, request fullscreen from inside the HTML app after checking host capabilities
