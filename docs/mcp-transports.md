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

> **Known limitation:** cross-call state does not. The options carry one command catalog, so
> [soft roots](mcp-advanced.md#soft-roots-fallback) set by one connection are visible to every other
> connection built from the same options. The `2026-07-28` revision removed protocol-level sessions,
> and this path has no per-connection identity to hang that state on. If your commands rely on soft
> roots, host one server per process (`mcp serve`) or pass the workspace as an explicit command
> argument.

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
| Request | Client capabilities, the requested log level, and the destination for sampling, elicitation and progress |
| Invocation | I/O capture — each tool call gets its own capture scope, not one per connection |
| Connection (`mcp serve` only) | The MCP session object, the native roots cache, soft roots, and session-aware routing state |

A server created from a reused `BuildMcpServerOptions()` result has the first two but not the third;
see the known limitation above. That matters especially when using dynamic tools, roots, or
session-specific modules.

For those higher-level patterns, see [mcp-advanced.md](mcp-advanced.md).
