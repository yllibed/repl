# Repl.Mcp

MCP server integration for [Repl Toolkit](https://github.com/yllibed/repl) — expose your command graph as AI agent tools via the [Model Context Protocol](https://modelcontextprotocol.io).

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

## What agents see

| You write | Agents get |
|---|---|
| `.ReadOnly()` | `readOnlyHint` — call autonomously |
| `.Destructive()` | `destructiveHint` — ask for confirmation |
| `.AsResource()` | MCP resource with `repl://` URI |
| `.AsPrompt()` | MCP prompt template |
| `.AutomationHidden()` | Not visible to agents |
| `{id:guid}` | `{ "type": "string", "format": "uuid" }` |
| `[Description("...")]` | Schema `description` field |

## Works with

Claude Desktop, Claude Code, VS Code Copilot, Cursor, and any MCP-compatible agent.

## Learn more

- [Full documentation](https://github.com/yllibed/repl/blob/main/docs/mcp-server.md) — annotations, interaction degradation, client compatibility matrix, agent configuration, NuGet publishing
- [Sample app](https://github.com/yllibed/repl/tree/main/samples/08-mcp-server) — resources, tools, prompts, and annotations in action
