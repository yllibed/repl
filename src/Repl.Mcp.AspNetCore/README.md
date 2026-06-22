# Repl.Mcp.AspNetCore

ASP.NET Core Streamable HTTP hosting integration for Repl MCP servers.

## Hosted in ASP.NET Core

```csharp
builder.Services.AddReplMcpHttp(replApp);

var app = builder.Build();
app.MapReplMcp("/mcp");
```

Use ASP.NET Core middleware and endpoint conventions for production concerns
such as authentication, authorization, CORS, HTTPS, and reverse proxy hosting.

```csharp
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddReplMcpHttp(replApp);

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapReplMcp("/mcp").RequireAuthorization();
```

## Self-Hosted CLI

Register the local HTTP command on a Repl app:

```csharp
var replApp = ReplApp.Create()
	.UseMcpHttpServer();
```

Then run:

```bash
myapp mcp httpserve
```

The default endpoint is `http://127.0.0.1:7375/mcp`. The port digits map to
`repl` on a phone keypad. Non-loopback bindings require an explicit opt-in:

```bash
myapp mcp httpserve --host 0.0.0.0 --allow-remote
```

The `http` and `http-serve` aliases also resolve to `httpserve`.
