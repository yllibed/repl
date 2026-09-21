# MCP Transports: Custom Transports and HTTP Integration

> **This page is for you if** you need WebSocket, named pipe, SSH, or HTTP transports instead of stdio.
>
> **Purpose:** Custom transport and HTTP hosting scenarios.
> **Prerequisite:** [MCP overview](mcp-overview.md)
> **Related:** [Reference](mcp-reference.md) · [Advanced patterns](mcp-advanced.md)

## Scenario A: Stdio-over-anything

The MCP protocol is JSON-RPC over stdin/stdout. The `TransportFactory` option lets you replace the physical transport while keeping the same protocol.

Use this for:

- WebSocket bridges
- Named pipes
- SSH tunnels
- Any other stream-based transport

```csharp
app.UseMcpServer(o =>
{
    o.TransportFactory = (serverName, io) =>
    {
        var (inputStream, outputStream) = CreateWebSocketBridge();
        return new StreamServerTransport(inputStream, outputStream, serverName);
    };
});
```

The app still launches via `myapp mcp serve`. This gives you one MCP session per process.

### Multi-session custom transports

If your transport accepts multiple concurrent connections, build `McpServerOptions` once and create a server per connection:

```csharp
var mcpOptions = app.Core.BuildMcpServerOptions();

async Task HandleConnectionAsync(Stream input, Stream output, CancellationToken ct)
{
    var transport = new StreamServerTransport(input, output, "my-server");
    var server = McpServer.Create(transport, mcpOptions);
    await server.RunAsync(ct);
    await server.DisposeAsync();
}
```

Client capabilities (sampling, elicitation, roots) resolve per **request** on this path, so each
connection sees its own — that is a property of the request, not of the options instance.

> **Known limitation:** cross-call state does not resolve per request, and three consequences are
> worth knowing before you choose this shape. The `2026-07-28` revision removed protocol-level
> sessions, so this path has no per-connection identity to hang state on.
>
> [Soft roots](mcp-advanced.md#soft-roots-fallback) set by one connection are visible to every other
> connection built from the same options — they are host-set state with no request to belong to. If
> your commands rely on them, host one server per process (`mcp serve`) or pass the workspace as an
> explicit command argument. Note that on `2026-07-28` soft roots never reveal commands on any
> transport: the advertised set must not change as a side effect of a `tools/call`. See
> [Conformance](mcp-conformance.md#tool-list-invariance-on-2026-07-28).
>
> The command catalog is frozen when `BuildMcpServerOptions()` returns. A server built from it never
> emits `*/list_changed`, even though the SDK advertises the capability, so a client that would
> refresh on that notification never does. Commands whose visibility changes at runtime — dynamic
> tools — need `mcp serve`. Presence predicates are a separate matter on `2026-07-28`: discovery
> resolves every per-connection question to a constant there, so the predicate's value under those
> constants decides presence once and for all. One that evaluates true is advertised to every client;
> one that evaluates false — a data gate, or a negated capability gate such as `!roots.IsSupported` —
> is advertised to none, and a frozen catalog has no later chance to change its mind. See
> [Conformance](mcp-conformance.md#what-this-means-when-you-write-commands).
>
> Native roots are safe here: they are resolved per request rather than cached per connection, so one
> client never sees another's workspace. The cost is one `roots/list` round-trip per request that asks
> for them. Under stateless HTTP the SDK reports no client capabilities at all, so native roots are
> unavailable on that transport whatever you do.

## Scenario B: MCP-over-HTTP

The MCP spec also defines an HTTP transport. For that, you typically host MCP inside ASP.NET Core rather than through `mcp serve`.

```csharp
var app = ReplApp.Create();
app.Map("greet {name}", (string name) => $"Hello, {name}!");
app.Map("status", () => "all systems go").ReadOnly();

var mcpOptions = app.Core.BuildMcpServerOptions(configure: o =>
{
    o.ServerName = "MyApi";
    o.ResourceUriScheme = "myapi";
});
```

You can then pass those options to the MCP SDK's HTTP integration.

## What is isolated, and at which boundary

The `2026-07-28` revision removed protocol-level sessions: the client declares its capabilities on
every request rather than once per connection. So the boundaries are not all the same size.

| Isolated per | What |
|---|---|
| Request | Client capabilities, the requested log level, the destination for sampling, elicitation and progress, and — on a reused `BuildMcpServerOptions()` result — native roots, resolved once per request and never cached across connections |
| Invocation | I/O capture — each tool call gets its own capture scope, not one per connection |
| Connection (`mcp serve` only) | The MCP session object, the native roots cache, soft roots, and session-aware routing state |

A server created from a reused `BuildMcpServerOptions()` result has everything above except the
connection row; see the known limitation above. That matters especially when using dynamic tools,
roots, or session-specific modules.

For those higher-level patterns, see [mcp-advanced.md](mcp-advanced.md).
