# Repl.Mcp

**Website:** [repl.yllibed.org](https://repl.yllibed.org/)

MCP server integration for [Repl Toolkit](https://github.com/yllibed/repl) — expose your command graph as AI agent tools, resources, prompts, and MCP Apps UI via the [Model Context Protocol](https://modelcontextprotocol.io).

Use `Repl.Mcp` when you already have, or want to build, a Repl command graph and make the same operations available to AI agents without writing a separate MCP server by hand.

## Upgrading from a 1.x SDK build

This version builds on `ModelContextProtocol` **2.x**. Five things change for an application already
using `Repl.Mcp`; the repository's
[MCP reference](https://github.com/yllibed/repl/blob/main/docs/mcp-reference.md#upgrading-from-the-1x-sdk)
carries the full list.

- **The SDK moves to 2.x.** It is a transitively public dependency, so a consumer referencing it
  directly moves with this package. The 1.x and 2.x assemblies cannot coexist.
- **`IMcpFeedback.SendMessageAsync` takes `McpMessageLevel`** instead of the SDK's deprecated
  `LoggingLevel`. Same members, same values — the swap is mechanical.
- **Tool results can carry extra content blocks.** A message the client could not receive as a
  notification is appended after the command's payload. The payload stays the first block and
  `StructuredContent` is untouched, but a test asserting exactly one block will fail.
- **`.LongRunning()` no longer advertises task support on the protocol surface**, because SDK 2.x
  removed the per-tool execution augmentation. The annotation still reaches help and documentation.
- **Module presence no longer varies with the client on `2026-07-28`**, which requires the advertised
  set not to vary per connection. Discovery there runs every presence predicate against fixed
  answers — `IsSupported`, `IsLoggingSupported` and `IsProgressSupported` are true, `HasSoftRoots` is
  false, `Current` and `GetAsync()` are empty — and whatever the predicate returns is what every
  client is offered. Read it off the result, not off the member: one that comes out true is
  advertised **to every client** and stays callable by every client, so that command must now return
  a clear error instead of relying on being absent; one that comes out false, including a negated capability gate such as
  `!roots.IsSupported`, is advertised **to none** and disappears with no error, so map it
  unconditionally. Earlier revisions are unchanged.

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
- notice / warning / problem feedback -> MCP message notifications, or the result itself

On `2026-07-28` a request that declared no log level must receive no message notifications, so that
feedback is appended to the tool or prompt result instead — and to the surfaced error when the call
fails. A resource read is the exception: its body has to match the advertised MIME type, so a read
that succeeds keeps only that body and the feedback it reported is dropped on purpose, while a read
that fails carries it in the surfaced error. Everywhere else it survives.

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

Use the executable that matches your app packaging. For local project samples, build once and use `dotnet run --no-build --project ... -- mcp serve` so host startup does not rebuild or write build output to stdout.

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
| `.LongRunning()` | help and documentation hint only — nothing on the protocol surface |
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

Unannotated tools force agents to assume the worst. Use `.ReadOnly()` for safe queries, `.Destructive()` for important mutations, `.OpenWorld()` for external systems, `.LongRunning()` for slow operations (a documentation hint today — protocol-level MCP task advertisement returns once Repl integrates the SDK Tasks extension), and `.AutomationHidden()` for commands that should stay available to humans but invisible to MCP automation.

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
