# Repl.Mcp.AspNetCore

ASP.NET Core Streamable HTTP hosting integration for Repl MCP servers.

## Hosted in ASP.NET Core

```csharp
builder.Services.AddReplMcpHttp(replApp);

var app = builder.Build();
app.MapReplMcp("/mcp");
```

`MapReplMcp()` maps the default `/mcp` endpoint.

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

For per-installation transport customization, configure the hosted transport:

```csharp
builder.Services.AddReplMcpHttp(replApp, http =>
{
	http.IdleTimeout = TimeSpan.FromMinutes(15);
	http.MaxIdleSessionCount = 50;
});
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
`repl` on a phone keypad. The self-hosted command is intended for local agents
and trusted development networks. Non-loopback bindings require an explicit
opt-in:

```bash
myapp mcp httpserve --host 0.0.0.0 --allow-remote
```

Loopback self-hosting rejects browser requests with unexpected `Origin` headers
and restricts the HTTP `Host` header to loopback names. Remote opt-in relaxes
the Host restriction for trusted networks, but does not add authentication or
TLS. Use hosted ASP.NET Core when the endpoint needs production-grade auth,
certificates, reverse proxy integration, or custom network policy.

Self-hosted apps can expose limited advanced hooks:

```csharp
var replApp = ReplApp.Create()
	.UseMcpHttpServer(options =>
	{
		options.ConfigureBuilder = builder =>
			builder.Configuration.AddEnvironmentVariables("MYAPP_");

		options.ConfigureApp = app =>
			app.UseForwardedHeaders();
	});
```

The `http` and `http-serve` aliases also resolve to `httpserve`.
