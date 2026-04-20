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

## Session isolation

Each connection or HTTP session is isolated:

- its own MCP session
- its own I/O capture
- its own session-aware routing state

That matters especially when using dynamic tools, roots, or session-specific modules.

For those higher-level patterns, see [mcp-advanced.md](mcp-advanced.md).
