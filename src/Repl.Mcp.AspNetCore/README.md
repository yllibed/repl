# Repl.Mcp.AspNetCore

ASP.NET Core Streamable HTTP hosting integration for Repl MCP servers.

```csharp
builder.Services.AddReplMcpHttp(replApp);

var app = builder.Build();
app.MapReplMcp("/mcp");
```

Use ASP.NET Core middleware and endpoint conventions for production concerns
such as authentication, authorization, CORS, HTTPS, and reverse proxy hosting.
