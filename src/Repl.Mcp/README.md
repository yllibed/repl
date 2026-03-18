# Repl.Mcp

MCP server integration for [Repl Toolkit](https://github.com/yllibed/repl): expose your command graph as AI agent tools, resources, and prompts via the [Model Context Protocol](https://modelcontextprotocol.io).

## Quick start

```csharp
using Repl;

var app = ReplApp.Create();
app.Map("greet {name}", (string name) => $"Hello, {name}!");
app.UseMcpServer();
await app.RunAsync(args);
```

```bash
myapp mcp serve   # starts MCP stdio server
```

## Features

- Auto-derive MCP tools from the command graph with typed JSON Schema
- Behavioral annotations (`.ReadOnly()`, `.Destructive()`, `.Idempotent()`, `.OpenWorld()`) map to MCP tool hints
- `.AsResource()` marks data commands as MCP resources
- `.AsPrompt()` marks commands as MCP prompt templates
- Progressive interaction degradation (prefill, elicitation, sampling)
- Real-time progress notifications
- Dynamic tool discovery via `list_changed`

See the [sample](https://github.com/yllibed/repl/tree/main/samples/08-mcp-server) and the [main README](https://github.com/yllibed/repl) for full documentation.
