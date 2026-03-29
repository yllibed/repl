# 08 — MCP Server

Expose a Repl command graph as an MCP server for AI agents.

## What this sample shows

- `app.UseMcpServer()` — one line to enable MCP stdio server
- `.ReadOnly()` / `.Destructive()` / `.OpenWorld()` — behavioral annotations
- `.AsResource()` — mark data-to-consult commands as MCP resources
- `.AsPrompt()` — mark commands as MCP prompt sources
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
