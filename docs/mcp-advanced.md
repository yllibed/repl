# MCP Advanced: Custom Transports & HTTP Integration

This guide covers two advanced integration scenarios beyond the default stdio transport.

> **Prerequisite**: read [mcp-server.md](mcp-server.md) first for the basics of exposing a Repl app as an MCP server.

## Scenario A: Stdio-over-anything

The MCP protocol is JSON-RPC over stdin/stdout. The `TransportFactory` option lets you replace the physical transport while keeping the same protocol — useful for WebSocket bridges, named pipes, SSH tunnels, etc.

### How it works

`TransportFactory` receives the server name and an I/O context, and returns an `ITransport`. The MCP server uses this transport instead of `StdioServerTransport`.

```csharp
app.UseMcpServer(o =>
{
    o.TransportFactory = (serverName, io) =>
    {
        // Bridge a WebSocket connection to MCP via streams.
        var (inputStream, outputStream) = CreateWebSocketBridge();
        return new StreamServerTransport(inputStream, outputStream, serverName);
    };
});
```

The app still launches via `myapp mcp serve` — the framework handles the full MCP lifecycle (tool registration, routing invalidation, shutdown).

### When to use

- You have a non-stdio transport (WebSocket, named pipe, TCP) that carries the standard MCP JSON-RPC protocol
- You want the framework to manage the server lifecycle
- Single-session per process is acceptable

## Scenario B: MCP-over-HTTP (Streamable HTTP)

The MCP spec defines a native HTTP transport: POST for client→server messages, GET/SSE for server→client streaming, with session management. This requires an HTTP host (typically ASP.NET Core) rather than a CLI command.

### How it works

`BuildMcpServerOptions()` constructs the full `McpServerOptions` (tools, resources, prompts, capabilities) from your Repl app's command graph — without starting a server. You pass these options to the MCP C# SDK's HTTP integration.

```csharp
var app = ReplApp.Create();
app.Map("greet {name}", (string name) => $"Hello, {name}!");
app.Map("status", () => "all systems go").ReadOnly();

// Build MCP options from the command graph.
var mcpOptions = app.Core.BuildMcpServerOptions(configure: o =>
{
    o.ServerName = "MyApi";
    o.ResourceUriScheme = "myapi";
});

// Use with McpServer.Create for a custom HTTP handler...
var server = McpServer.Create(httpTransport, mcpOptions);

// ...or pass the collections to ASP.NET Core's MapMcp.
```

### Multi-session

Each HTTP request creates an isolated MCP session. This uses the same mechanism as Repl's hosted sessions:

- `ReplSessionIO.SetSession()` creates an `AsyncLocal` scope per request
- Each session has its own output writer, input reader, and session ID
- Tool invocations are fully isolated — concurrent requests don't interfere

This is identical to how the framework handles concurrent tool calls in stdio mode (via `McpToolAdapter.ExecuteThroughPipelineAsync`).

### When to use

- You're building a web API that also exposes MCP endpoints
- You need multiple concurrent MCP sessions (agents connecting via HTTP)
- You want to integrate with the ASP.NET Core pipeline (auth, middleware, etc.)

## Configuration reference

| Option | Default | Description |
|--------|---------|-------------|
| `TransportFactory` | `null` (stdio) | Custom transport factory for Scenario A |
| `ResourceUriScheme` | `"repl"` | URI scheme for MCP resources (`{scheme}://path`) |
| `ServerName` | Assembly product name | Server name in MCP `initialize` response |
| `ServerVersion` | `"1.0.0"` | Server version in MCP `initialize` response |

See [mcp-server.md](mcp-server.md) for the full configuration reference.
