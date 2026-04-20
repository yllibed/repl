# MCP Server Integration

> **This page is for you if** you want to expose your Repl commands as MCP tools for AI agents.
>
> **Purpose:** Get a working MCP server in 5 minutes, understand the mental model.
> **Prerequisite:** A working Repl app ([quick start](../README.md#quick-start))
> **Related:** [Reference](mcp-reference.md) · [Advanced patterns](mcp-advanced.md) · [Sampling & elicitation](mcp-agent-capabilities.md) · [Transports](mcp-transports.md)

## Quick start

```bash
dotnet add package Repl.Mcp
```

```csharp
using Repl.Mcp;

var app = ReplApp.Create().UseDefaultInteractive();
app.UseMcpServer();

app.Map("greet {name}", (string name) => $"Hello, {name}!");

return app.Run(args);
```

```bash
myapp mcp serve    # starts MCP stdio server
myapp              # still works as CLI / interactive REPL
```

One command graph. CLI, REPL, remote sessions, and AI agents — all from the same code.

> `UseMcpServer()` registers a hidden `mcp serve` context. The tool list is built lazily when an agent connects, so it sees all commands regardless of registration order.

## What it does

Commands map to MCP primitives automatically:

| You write | Agent sees | How |
|---|---|---|
| `Map()` | Tool | Automatic — every non-hidden command becomes a tool |
| `.ReadOnly()` | Tool + Resource | Auto-promoted to resource |
| `.AsResource()` | Resource only | Explicit data source |
| `.AsPrompt()` | Prompt | Reusable instruction template |
| `.AsMcpAppResource()` | Tool + `ui://` HTML resource | Interactive UI for capable hosts |
| `.AutomationHidden()` | _(nothing)_ | Excluded from MCP entirely |

## Annotations

Annotations tell agents how to use your tools safely:

```csharp
app.Map("contacts", handler).ReadOnly();                // safe to call autonomously
app.Map("contact add", handler).OpenWorld().Idempotent(); // calls external systems, retriable
app.Map("contact delete {id}", handler).Destructive();  // agent asks for confirmation
app.Map("deploy", handler).Destructive().LongRunning().OpenWorld();
```

| Annotation | Agent behavior |
|---|---|
| `.ReadOnly()` | Call autonomously, no confirmation, can parallelize |
| `.Destructive()` | Ask user for confirmation, sequential |
| `.Idempotent()` | Safe to retry, can parallelize |
| `.OpenWorld()` | Reaches external systems — expect latency and transient failures |
| `.LongRunning()` | Enables call-now/poll-later pattern |
| `.AutomationHidden()` | Not visible to agents |

**Annotate every command exposed to agents.** Unannotated tools force agents to assume the worst: confirm everything, no parallelism, no retries.

## Tool vs Resource vs Prompt

| | Tool | Resource | Prompt |
|---|---|---|---|
| **Intent** | Perform an action | Consult data | Guide a conversation |
| **Side effects** | Yes (unless ReadOnly) | No | No |
| **When to use** | `add`, `delete`, `deploy` | `contacts`, `config`, `status` | `summarize-data`, `troubleshoot` |

See [mcp-reference.md](mcp-reference.md#how-commands-map-to-mcp-primitives) for the exhaustive mapping table with all marker combinations and fallback options.

## MCP Apps

Render interactive HTML UI in agents that support MCP Apps:

```csharp
app.Map("contacts dashboard", (IContactDb contacts) => BuildHtml(contacts))
    .WithDescription("Open the contacts dashboard")
    .AsMcpAppResource()
    .WithMcpAppBorder();
```

Capable hosts render the HTML. Others receive normal tool text. See [mcp-reference.md](mcp-reference.md#mcp-apps) for CSP, URIs, and display modes. See [mcp-advanced.md](mcp-advanced.md#mcp-apps-advanced-patterns) for WebAssembly and complex patterns.

## Agent configuration

Most MCP clients use the same format:

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

See [mcp-reference.md](mcp-reference.md#agent-configuration) for all client-specific paths and formats (Claude Desktop, Claude Code, VS Code Copilot, Cursor, MCP Inspector).

## What most apps need vs what few apps need

| Most apps need | Few apps need |
|---|---|
| `UseMcpServer()` | Custom transports ([mcp-transports.md](mcp-transports.md)) |
| Annotations (`.ReadOnly()`, `.Destructive()`, etc.) | Dynamic tools / compatibility shim ([mcp-advanced.md](mcp-advanced.md)) |
| `.AsResource()` / `.AsPrompt()` | Client roots / soft roots ([mcp-advanced.md](mcp-advanced.md)) |
| Return values + `IReplInteractionChannel` | Direct sampling / elicitation ([mcp-agent-capabilities.md](mcp-agent-capabilities.md)) |
| Agent config (above) | MCP Apps UI ([mcp-reference.md](mcp-reference.md#mcp-apps)) |
| | Publishing as NuGet tool ([mcp-reference.md](mcp-reference.md#publishing-as-a-dotnet-tool-mcp-server)) |

## Next steps

- **Building your first MCP server?** You have everything you need above. Start coding. See [sample 08-mcp-server](../samples/08-mcp-server/) for a complete working example.
- **Need the full reference** — JSON Schema, tool naming, interaction modes, output rules, configuration, compatibility, publishing? See [mcp-reference.md](mcp-reference.md).
- **Dynamic tools, roots, or client compatibility workarounds?** See [mcp-advanced.md](mcp-advanced.md).
- **Want to use sampling or elicitation directly?** See [mcp-agent-capabilities.md](mcp-agent-capabilities.md).
- **Custom transports or HTTP hosting?** See [mcp-transports.md](mcp-transports.md).
