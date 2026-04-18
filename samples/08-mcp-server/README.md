# 08 — MCP Server

Expose a Repl command graph as an MCP server for AI agents, including a minimal MCP Apps UI.

## What this sample shows

- `app.UseMcpServer()` — one line to enable MCP stdio server
- `IReplInteractionChannel` in MCP mode — portable notices, warnings, problems, and progress updates
- `feedback demo` / `feedback fail` — deterministic progress sequences that are easy to inspect in MCP Inspector
- `.ReadOnly()` / `.Destructive()` / `.OpenWorld()` — behavioral annotations
- `.AsResource()` — mark data-to-consult commands as MCP resources
- `.AsPrompt()` — mark commands as MCP prompt sources
- `.AsMcpAppResource()` — mark a command as a generated HTML MCP App resource
- `.WithMcpAppBorder()` / `.WithMcpAppDisplayMode(...)` — add MCP Apps presentation preferences
- `.AutomationHidden()` — hide interactive-only commands from agents
- `.WithDetails()` — rich descriptions consumed by agents and documentation tools (not displayed in `--help`)
- `import {file}` — a realistic workflow that combines progress reporting, sampling, elicitation, and duplicate review

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

Clients with MCP Apps support render the `contacts dashboard` tool's generated `ui://contacts/dashboard` resource. Other clients still receive the normal launcher text instead of raw HTML.

In the current Repl.Mcp version, MCP Apps are experimental and the UI handler returns generated HTML as a string. Future versions may add richer return types and asset helpers.

## Demo workflow

In the interactive REPL, try:

- `feedback demo` to emit a successful sequence with normal, indeterminate, and warning progress states
- `feedback fail` to emit warning and error progress, then finish with a problem result
- `import contacts.csv` to see the realistic workflow that uses sampling and elicitation when the connected client supports them

In MCP Inspector:

1. Start the sample in MCP mode.
2. Call `feedback_demo`.
3. Watch the tool emit `notifications/progress` during the run.
4. Call `feedback_fail`.
5. Watch the warning/error feedback arrive before the final tool error result.
6. Call `import` with any file name to see the longer workflow:
   the tool reports progress while reading, column-mapping, duplicate review, and commit.

The deterministic `feedback_*` tools make it easy to verify the host's notification rendering without depending on a real CSV file.

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
