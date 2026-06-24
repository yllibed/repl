# Repl.Mcp

MCP server integration for [Repl Toolkit](https://github.com/yllibed/repl) — expose your command graph as AI agent tools, resources, prompts, and MCP Apps UI via the [Model Context Protocol](https://modelcontextprotocol.io).

Use `Repl.Mcp` when you already have, or want to build, a Repl command graph and make the same operations available to AI agents without writing a separate MCP server by hand.

## Install

```bash
dotnet add package Repl.Mcp
```

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

`IReplInteractionChannel` user feedback maps to MCP-native transports:

- progress -> progress notifications
- notice / warning / problem feedback -> MCP message notifications

Keep operator logging on `ILogger`; do not rely on user-facing interaction as a logging sink.

## Agent configuration

Most MCP clients use the same shape:

```json
{
  "mcpServers": {
    "myapp": {
      "command": "myapp",
      "args": ["mcp", "serve"]
    }
  }
}
```

Use the executable or `dotnet run --project ... -- mcp serve` command that matches your app packaging.

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
| `.Idempotent()` | retry-safe hint |
| `.OpenWorld()` | external-system hint |
| `.LongRunning()` | long-running-operation hint |
| `.AsResource()` | MCP resource with `repl://` URI |
| `.AsMcpAppResource()` | MCP Apps HTML resource with `ui://` URI |
| `.WithMcpAppBorder()` | MCP Apps border/background preference |
| `.WithMcpAppDisplayMode(McpAppDisplayModes.Fullscreen)` | MCP Apps display preference |
| `.AsPrompt()` | MCP prompt template |
| `.AutomationHidden()` | Not visible to agents |
| `{id:guid}` | `{ "type": "string", "format": "uuid" }` |
| `[Description("...")]` | Schema `description` field |

## Safety guidelines

Annotate every command that is visible to agents:

```csharp
app.Map("contacts list", handler).ReadOnly();

app.Map("contacts import {file}", handler)
    .OpenWorld()
    .LongRunning();

app.Map("contacts delete {id:int}", handler)
    .Destructive();

app.Map("debug reset", handler)
    .AutomationHidden();
```

Unannotated tools force agents to assume the worst. Use `.ReadOnly()` for safe queries, `.Destructive()` for important mutations, `.OpenWorld()` for external systems, `.LongRunning()` for operations that should use call-now / poll-later patterns, and `.AutomationHidden()` for commands that should stay available to humans but invisible to MCP automation.

Prefer returning JSON-friendly objects instead of writing prose-only output. Structured results are easier for agents to inspect, retry, test, and summarize.

## Works with

Claude Desktop, Claude Code, VS Code Copilot, Cursor, and any MCP-compatible agent.

MCP Apps host support varies. VS Code currently renders MCP Apps inline; hosts that support display mode requests can honor `preferredDisplayMode`.

## Docs

- [MCP Mode](https://repl.yllibed.org/getting-started/mcp-mode/) — quick start, annotations, mental model
- [MCP In Depth](https://repl.yllibed.org/reference/mcp-concepts/) — interaction degradation, client compatibility, agent configuration
- [Agent-Native Development](https://repl.yllibed.org/reference/agent-native/) — designing commands for AI consumption
- [For coding agents](https://github.com/yllibed/repl/blob/main/docs/for-coding-agents.md) — decision rules and copyable instructions for coding agents
- [Cookbook: MCP Server](https://repl.yllibed.org/cookbook/mcp-server/) — resources, tools, prompts, annotations, and MCP Apps UI in action
