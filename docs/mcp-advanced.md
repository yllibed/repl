# MCP Advanced: Dynamic Tools, Roots, MCP Apps, and Session-Aware Patterns

This guide covers advanced MCP usage patterns for Repl apps:

- Tool visibility that changes per session
- Native MCP client roots
- Soft roots for clients that don't support roots
- Compatibility shims for clients that don't refresh dynamic tool lists well
- Advanced MCP Apps patterns

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
- Your MCP App should render HTML without exposing that HTML as the model-facing tool result

If your tool list is static, stay with the default setup from [mcp-server.md](mcp-server.md).

## Client roots

A **root** is a workspace or directory that the MCP client declares as being in scope for the session.

Examples:

- The folder the user opened in the editor
- The project workspace attached to the agent
- A set of directories the agent is allowed to inspect

When the client supports native MCP roots, `Repl.Mcp` exposes them through `IMcpClientRoots`.

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
using Repl.Mcp;

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
using Repl.Mcp;

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
using Repl.Mcp;

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
using Repl.Mcp;

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

## MCP Apps advanced patterns

For the basic MCP Apps setup, start with [mcp-server.md](mcp-server.md#mcp-apps). This section covers the patterns that matter once the UI is more than a trivial inline HTML card.

### Launcher tool plus app-only resource

If a mapped command returns generated HTML, do not always expose that command directly to the model. Some hosts can show the tool result text in the chat transcript, which means the model may treat the HTML as normal content.

Prefer a small model-visible launcher tool plus a separate app-only HTML resource command:

```csharp
app.Map("contacts dashboard", () => "Opening the contacts dashboard.")
    .ReadOnly()
    .WithMcpApp("ui://contacts/dashboard");

app.Map("contacts dashboard app", (IContactDb contacts) => BuildHtml(contacts))
    .AsMcpAppResource(
        "ui://contacts/dashboard",
        resource =>
        {
            resource.Name = "Contacts Dashboard";
            resource.PrefersBorder = true;
        },
        visibility: McpAppVisibility.App);
```

The first command gives the model something useful and short to call. The second command is still a normal Repl mapping, so it can use dependency injection, cancellation tokens, and the usual command pipeline, but its tool metadata is `visibility: ["app"]`.

Use the single-command pattern only when the command returns both a good text fallback and useful UI metadata:

```csharp
app.Map("status dashboard", (IStatusStore store) => store.GetSummary())
    .ReadOnly()
    .WithMcpApp("ui://status/dashboard");
```

### Generated UI resource URIs

`AsMcpAppResource()` generates a `ui://` URI from the route path, matching how `AsResource()` generates `repl://` URIs:

```csharp
app.Map("contact {id:int} panel", (int id, IContactDb contacts) => BuildHtml(contacts.Get(id)))
    .AsMcpAppResource();
```

This produces a resource template like `ui://contact/{id}/panel`.

Pass an explicit URI when a launcher tool and app-only resource command need to share the same app resource:

```csharp
app.Map("contacts dashboard app", (IContactDb contacts) => BuildHtml(contacts))
    .AsMcpAppResource("ui://contacts/dashboard", visibility: McpAppVisibility.App);
```

### Display preferences

MCP Apps standard display modes are `inline`, `fullscreen`, and `pip`, but hosts decide what they support. Repl can express a preference:

```csharp
app.Map("contacts dashboard app", (IContactDb contacts) => BuildHtml(contacts))
    .AsMcpAppResource(
        "ui://contacts/dashboard",
        visibility: McpAppVisibility.App,
        preferredDisplayMode: McpAppDisplayModes.Fullscreen);
```

As of April 2026, VS Code renders MCP Apps inline only. Microsoft 365 Copilot declarative agents support fullscreen display requests for widgets. Other hosts vary; check [mcp-server.md](mcp-server.md#mcp-apps-host-compatibility) for the current compatibility notes.

If the HTML uses the MCP Apps JavaScript bridge, it should still ask the host what is available before requesting a different display mode:

```javascript
const modes = app.getHostContext()?.availableDisplayModes ?? [];
if (modes.includes("fullscreen")) {
  await app.requestDisplayMode({ mode: "fullscreen" });
}
```

For host-specific hints that are not yet modeled by Repl, use simple string metadata:

```csharp
app.Map("contacts dashboard app", (IContactDb contacts) => BuildHtml(contacts))
    .AsMcpAppResource(resource =>
    {
        resource.UiMetadata["presentation"] = "flyout";
    });
```

### HTML now, assets later

The v1 Repl API expects the UI resource handler to return generated HTML. This is enough for small cards, forms, and proof-of-concept dashboards.

For WebAssembly UIs such as Uno-Wasm, the likely shape is:

1. Map a `ui://` app resource that returns a small HTML shell.
2. Serve published static assets such as `embedded.js`, `_framework/*`, `.wasm`, fonts, and images from an HTTP endpoint.
3. Inject the HTTP base URL into the generated shell.
4. Set CSP metadata for asset and fetch domains.

```csharp
var assetBaseUri = new Uri("http://127.0.0.1:5123/");

app.Map("contacts dashboard app", () => BuildUnoShellHtml(assetBaseUri))
    .AsMcpAppResource("ui://contacts/dashboard", resource =>
    {
        resource.Csp = new McpAppCsp
        {
            ResourceDomains = [assetBaseUri.ToString()],
            ConnectDomains = [assetBaseUri.ToString()],
        };
    }, visibility: McpAppVisibility.App);
```

Keep the shell and asset server host-aware: clients may preload or cache UI resources, and not every host supports every display mode or browser capability.

## Troubleshooting patterns

### My MCP App shows HTML text in the chat

Use the launcher plus app-only resource pattern. The model-visible launcher should return a short text result and point at the `ui://` resource with `.WithMcpApp(...)`; the HTML-producing command should use `.AsMcpAppResource(..., visibility: McpAppVisibility.App)`.

Also restart or reload the MCP server in the client. Some hosts cache tool lists and will not pick up `_meta.ui.visibility` changes until the server is refreshed.

### My MCP App does not open fullscreen

Check whether the host supports fullscreen. VS Code currently renders MCP Apps inline only, even when Repl sets `preferredDisplayMode: McpAppDisplayModes.Fullscreen`.

For hosts that support display mode changes, request fullscreen from inside the HTML app after checking host capabilities.

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
