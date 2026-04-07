# 08 — MCP Server

Expose a Repl command graph as an MCP server for AI agents, including a minimal MCP Apps UI.

## What this sample shows

- `app.UseMcpServer()` — one line to enable MCP stdio server
- `.ReadOnly()` / `.Destructive()` / `.OpenWorld()` — behavioral annotations
- `.AsResource()` — mark data-to-consult commands as MCP resources
- `.AsPrompt()` — mark commands as MCP prompt sources
- `.WithMcpApp("ui://...")` — attach a model-visible launcher tool to an MCP App
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

Clients with MCP Apps support render the `contacts dashboard` tool's `ui://contacts/dashboard` resource. Other clients still receive the normal text fallback from the launcher tool result.

The sample intentionally uses two commands for the dashboard:

- `contacts dashboard` is visible to the model and returns a short text fallback.
- `contacts dashboard app` generates the HTML and is marked `visibility: McpAppVisibility.App`, so capable hosts can call it without exposing raw HTML as model-facing text.

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
