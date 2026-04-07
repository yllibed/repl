# Repl.Mcp

MCP server integration for [Repl Toolkit](https://github.com/yllibed/repl) — expose your command graph as AI agent tools, resources, prompts, and MCP Apps UI via the [Model Context Protocol](https://modelcontextprotocol.io).

## One line to add

```csharp
using Repl.Mcp;

app.UseMcpServer();
```

Your commands become MCP tools. Route constraints become JSON Schema. Annotations become safety hints.

```bash
myapp mcp serve   # AI agents connect here
myapp              # still a CLI / interactive REPL
```

## MCP Apps

Repl.Mcp can also expose MCP Apps UI resources:

This support is experimental in the current version. `AsMcpAppResource()` handlers should return generated HTML as `string`, `Task<string>`, or `ValueTask<string>`; richer return shapes and asset helpers may be added later.

```csharp
app.Map("contacts dashboard", (ContactStore contacts) =>
        $"<!doctype html><html><body>{contacts.All.Count} contacts</body></html>")
    .WithDescription("Open the contacts dashboard")
    .AsMcpAppResource()
    .WithMcpAppBorder()
    .WithMcpAppDisplayMode(McpAppDisplayModes.Fullscreen);
```

Clients with MCP Apps support render the generated `ui://` resource. Other MCP clients still receive the command's normal launcher text instead of raw HTML.

## What agents see

| You write | Agents get |
|---|---|
| `.ReadOnly()` | `readOnlyHint` — call autonomously |
| `.Destructive()` | `destructiveHint` — ask for confirmation |
| `.AsResource()` | MCP resource with `repl://` URI |
| `.AsMcpAppResource()` | MCP Apps HTML resource with `ui://` URI |
| `.WithMcpAppBorder()` | MCP Apps border/background preference |
| `.WithMcpAppDisplayMode(McpAppDisplayModes.Fullscreen)` | MCP Apps display preference |
| `.AsPrompt()` | MCP prompt template |
| `.AutomationHidden()` | Not visible to agents |
| `{id:guid}` | `{ "type": "string", "format": "uuid" }` |
| `[Description("...")]` | Schema `description` field |

## Works with

Claude Desktop, Claude Code, VS Code Copilot, Cursor, and any MCP-compatible agent.

MCP Apps host support varies. VS Code currently renders MCP Apps inline; hosts that support display mode requests can honor `preferredDisplayMode`.

## Learn more

- [Full documentation](https://github.com/yllibed/repl/blob/main/docs/mcp-server.md) — annotations, interaction degradation, client compatibility matrix, agent configuration, NuGet publishing
- [Sample app](https://github.com/yllibed/repl/tree/main/samples/08-mcp-server) — resources, tools, prompts, annotations, and a minimal MCP Apps UI in action
