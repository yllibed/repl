# 08 — MCP Server

Expose a Repl command graph as an MCP server for AI agents.

## What this sample shows

- `app.UseMcpServer()` — one line to enable MCP stdio server
- `.ReadOnly()` / `.Destructive()` / `.OpenWorld()` — behavioral annotations
- `.AsResource()` — mark data-to-consult commands as MCP resources
- `.AsPrompt()` — mark commands as MCP prompt sources
- `.AsMcpAppResource(..., visibility: McpAppVisibility.App, preferredDisplayMode: ...)` — expose generated HTML as an app-only MCP App with a display preference
- `.AutomationHidden()` — hide interactive-only commands from agents
- `.WithDetails()` — rich descriptions that serve both `--help` and agents

## Running

**Interactive REPL:**

```bash
dotnet run
```

**MCP server mode:**

```bash
dotnet run -- mcp serve
```

**Test with MCP Inspector:**

```bash
npx @modelcontextprotocol/inspector dotnet run --project . -- mcp serve
```

Clients with MCP Apps support render the `contacts dashboard` tool's `ui://contacts/dashboard` resource inline. Other clients still receive the normal text fallback from the tool result.

## Agent configuration

### Claude Desktop

```json
{
  "mcpServers": {
    "contacts": {
      "command": "dotnet",
      "args": ["run", "--project", "samples/08-mcp-server", "--", "mcp", "serve"]
    }
  }
}
```

### VS Code (GitHub Copilot)

```json
{
  "servers": {
    "contacts": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "samples/08-mcp-server", "--", "mcp", "serve"]
    }
  }
}
```
