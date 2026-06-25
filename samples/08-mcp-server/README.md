# 08 — Build an MCP Server with Repl.Mcp

This sample shows how to build a real MCP server from a Repl command graph.

> **Important:** Repl.Mcp is not itself the MCP server you install in an agent host.
> Repl.Mcp is the Repl Toolkit component that lets your app expose its own command graph as an MCP server.
> In this sample, the sample app is the MCP server.

## What this sample shows

- `app.UseMcpServer()` — one line to enable an MCP stdio server for this app
- One command graph exposed as CLI, interactive REPL, and MCP tools/resources/prompts
- `contacts paged` — paged structured output for large result sets
- `IReplInteractionChannel` in MCP mode — portable notices, warnings, problems, and progress updates
- `feedback demo` / `feedback fail` — deterministic progress sequences that are easy to inspect in MCP Inspector
- `.ReadOnly()` / `.Destructive()` / `.OpenWorld()` — behavioral annotations
- `.AsResource()` — mark data-to-consult commands as MCP resources
- `.AsPrompt()` — mark commands as MCP prompt sources
- `.AsMcpAppResource()` — mark a command as a generated HTML MCP App resource
- `.WithMcpAppBorder()` / `.WithMcpAppDisplayMode(...)` — add MCP Apps presentation preferences
- `.AutomationHidden()` — hide interactive-only commands from agents
- `.WithDetails()` — rich descriptions consumed by agents and documentation tools, not displayed in `--help`
- `import {file}` — a realistic workflow that combines progress reporting, sampling, elicitation, and duplicate review

## Run from the repository root

Use these commands from the root of the `yllibed/repl` repository.

### CLI mode

```bash
dotnet run --project samples/08-mcp-server/McpServerSample.csproj -- contacts --json
```

Expected shape:

```json
[
  {
    "name": "Alice",
    "email": "alice@example.com"
  },
  {
    "name": "Bob",
    "email": "bob@example.com"
  }
]
```

Try another CLI command:

```bash
dotnet run --project samples/08-mcp-server/McpServerSample.csproj -- contact 1 --json
```

### Interactive REPL mode

```bash
dotnet run --project samples/08-mcp-server/McpServerSample.csproj
```

Then try:

```text
contacts
contacts paged --result:page-size=5
contact 1
feedback demo
exit
```

### MCP server mode

```bash
dotnet run --project samples/08-mcp-server/McpServerSample.csproj -- mcp serve
```

This starts a stdio MCP server for the sample app. It waits for an MCP client to connect over stdin/stdout.

### Test with MCP Inspector

```bash
npx @modelcontextprotocol/inspector dotnet run --project samples/08-mcp-server/McpServerSample.csproj -- mcp serve
```

In MCP Inspector:

1. Start the sample in MCP mode.
2. Call `contacts_paged` with `_replPageSize` set to `5`.
3. Call `contacts_paged` again with `_replPageSize` set to `5` and `_replCursor` set to the returned `pageInfo.nextCursor`.
4. Call `feedback_demo`.
5. Watch the tool emit `notifications/progress` during the run.
6. Call `feedback_fail`.
7. Watch the warning/error feedback arrive before the final tool error result.
8. Call `import` with any file name to see the longer workflow:
   the tool reports progress while reading, column-mapping, duplicate review, and commit.

The deterministic `feedback_*` tools make it easy to verify the host's notification rendering without depending on a real CSV file.

Clients with MCP Apps support render the `contacts dashboard` tool's generated `ui://contacts/dashboard` resource. Other clients still receive the normal launcher text instead of raw HTML.

In the current Repl.Mcp version, MCP Apps are experimental and the UI handler returns generated HTML as a string. Future versions may add richer return types and asset helpers.

## Agent host configuration

Use an absolute project path in agent-host config. Most hosts launch MCP servers outside your shell's current working directory, so relative paths are fragile.

Replace `/absolute/path/to/repl` with your local clone path.

### Generic MCP client / Claude Desktop style

```json
{
  "mcpServers": {
    "repl-contacts-sample": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/absolute/path/to/repl/samples/08-mcp-server/McpServerSample.csproj",
        "--",
        "mcp",
        "serve"
      ]
    }
  }
}
```

A copyable file lives at [`configs/generic-mcp-client.json`](configs/generic-mcp-client.json).

### Cursor

Project config path: `.cursor/mcp.json`.

```json
{
  "mcpServers": {
    "repl-contacts-sample": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/absolute/path/to/repl/samples/08-mcp-server/McpServerSample.csproj",
        "--",
        "mcp",
        "serve"
      ]
    }
  }
}
```

A copyable file lives at [`configs/cursor.mcp.json`](configs/cursor.mcp.json).

### VS Code / GitHub Copilot

Workspace config path: `.vscode/mcp.json`.

```json
{
  "servers": {
    "repl-contacts-sample": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/absolute/path/to/repl/samples/08-mcp-server/McpServerSample.csproj",
        "--",
        "mcp",
        "serve"
      ]
    }
  }
}
```

A copyable file lives at [`configs/vscode.mcp.json`](configs/vscode.mcp.json).

VS Code also supports adding MCP servers from the command line:

```bash
code --add-mcp '{"name":"repl-contacts-sample","command":"dotnet","args":["run","--project","/absolute/path/to/repl/samples/08-mcp-server/McpServerSample.csproj","--","mcp","serve"]}'
```

### Claude Code

If your Claude Code version supports command-line MCP registration, add the sample like this:

```bash
claude mcp add repl-contacts-sample -- dotnet run --project /absolute/path/to/repl/samples/08-mcp-server/McpServerSample.csproj -- mcp serve
```

Otherwise, use the generic `mcpServers` JSON shape above in the relevant Claude Code project or user settings.

### Cline

Use Cline's MCP settings or marketplace flow to add a local stdio server with:

- command: `dotnet`
- args: `run`, `--project`, `/absolute/path/to/repl/samples/08-mcp-server/McpServerSample.csproj`, `--`, `mcp`, `serve`

If your Cline version asks for JSON, start from the generic `mcpServers` block above.

## Adapting this sample to your app

1. Keep your command handlers small, typed, and dependency-injected.
2. Return JSON-friendly objects instead of prose-only console output.
3. Add `app.UseMcpServer()` to register the hidden `mcp serve` command.
4. Annotate commands that agents can call:
   - `.ReadOnly()` for safe queries;
   - `.Destructive()` for mutations that need confirmation;
   - `.Idempotent()` for operations safe to retry;
   - `.OpenWorld()` for external systems;
   - `.LongRunning()` for slow work;
   - `.AutomationHidden()` for interactive-only or unsafe commands.
5. Document the exact MCP command and args your users should paste into their agent host.

## Safety note

Local MCP servers run commands on the user's machine. Only configure MCP servers from trusted repositories, review the command and arguments before starting them, and avoid hardcoding secrets in MCP config files. Prefer environment variables or host-supported secret inputs for API keys.

## References

- [MCP Server Integration](../../docs/mcp-overview.md)
- [MCP Server Reference](../../docs/mcp-reference.md)
- [MCP agent capabilities](../../docs/mcp-agent-capabilities.md)
- [MCP transports](../../docs/mcp-transports.md)
