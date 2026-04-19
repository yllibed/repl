# MCP Server Reference

> **This page is for you if** you already have an MCP server working and need to look up specific features, options, or behaviors.
>
> **Purpose:** Complete reference for MCP server features. Consult, don't read end-to-end.
> **Prerequisite:** [MCP overview](mcp-overview.md)
> **Related:** [Advanced patterns](mcp-advanced.md) · [Sampling & elicitation](mcp-agent-capabilities.md) · [Transports](mcp-transports.md)

## Rich descriptions

`WithDetails()` provides extended markdown content for agent tool descriptions:

```csharp
app.Map("deploy {env}", handler)
    .WithDescription("Deploy the application")          // short summary (shown everywhere)
    .WithDetails("""
        Deploys to the specified environment.

        Prerequisites:
        - Valid credentials in ~/.config/deploy
        - Target environment must be provisioned
        """);
```

`Description` is visible in help and tool listings. `Details` is consumed by agents and documentation tools — **not** displayed in terminal `--help`.

## How commands map to MCP primitives

| Command markers | Tool? | Resource? | Prompt? |
|---|---|---|---|
| _(none)_ | Yes | No | No |
| `.ReadOnly()` | Yes | Yes (auto-promoted) | No |
| `.ReadOnly()` + `AutoPromoteReadOnlyToResources = false` | Yes | No | No |
| `.AsResource()` | No | Yes | No |
| `.AsResource()` + `ResourceFallbackToTools = true` | Yes | Yes | No |
| `.ReadOnly().AsResource()` | Yes | Yes | No |
| `.AsPrompt()` | No | No | Yes |
| `.AsPrompt()` + `PromptFallbackToTools = true` | Yes | No | Yes |
| `.AsMcpAppResource()` | Yes (launcher text) | Yes (`ui://` HTML resource) | No |
| `.AutomationHidden()` | No | No | No |

> **Compatibility fallback:** Only ~39% of clients support resources and ~38% support prompts. Enable `ResourceFallbackToTools` and/or `PromptFallbackToTools` to also expose them as tools:
>
> ```csharp
> app.UseMcpServer(o =>
> {
>     o.ResourceFallbackToTools = true;
>     o.PromptFallbackToTools = true;
>     o.AutoPromoteReadOnlyToResources = false;  // opt out of ReadOnly → resource
> });
> ```

## MCP Apps

`AsMcpAppResource()` maps a command as a `ui://` HTML resource with metadata for capable hosts:

```csharp
app.Map("contacts dashboard", (IContactDb contacts) => BuildHtml(contacts))
    .WithDescription("Open the contacts dashboard")
    .AsMcpAppResource()
    .WithMcpAppBorder();
```

| Behavior | Detail |
|---|---|
| Tool calls return | Launcher text (not HTML) |
| `resources/read` returns | `text/html;profile=mcp-app` |
| CSP, permissions, borders | Emitted as `_meta.ui` on the UI resource content |
| Clients without MCP Apps | Receive the tool's normal text result |

> **Experimental:** `AsMcpAppResource()` handlers should return `string`, `Task<string>`, or `ValueTask<string>`. Richer return types and asset helpers may be added later.

### CSP metadata

For HTML that loads external assets:

```csharp
app.Map("contacts dashboard", (IContactDb contacts) => BuildHtml(contacts))
    .AsMcpAppResource()
    .WithMcpAppCsp(new McpAppCsp
    {
        ResourceDomains = ["https://cdn.example.com"],
        ConnectDomains = ["https://api.example.com"],
    });
```

### Explicit URIs

Pass an explicit URI for a stable custom value:

```csharp
.AsMcpAppResource("ui://contacts/summary");
```

When omitted, Repl generates a `ui://` template from the route path (e.g., `viewer session {id:int} attach` → `ui://viewer/session/{id}/attach`). Route constraints are validated at dispatch time, not in the URI template.

### Raw UI resources

For UI not backed by a Repl command:

```csharp
app.UseMcpServer(o =>
{
    o.UiResource("ui://custom/app", () => "<html>...</html>");
});
```

## JSON Schema generation

Route constraints and handler parameter types produce typed JSON Schema:

| Repl type | JSON Schema | Format |
|---|---|---|
| `string` | `string` | — |
| `int` | `integer` | — |
| `long` | `integer` | — |
| `bool` | `boolean` | — |
| `double`, `decimal` | `number` | — |
| `enum` | `string` + `enum: [...]` | — |
| `List<T>` | `array` + `items` | — |
| `{x:email}` | `string` | `email` |
| `{x:guid}` | `string` | `uuid` |
| `{x:date}` | `string` | `date` |
| `{x:datetime}` | `string` | `date-time` |
| `{x:uri}` | `string` | `uri` |
| `{x:time}` | `string` | `time` |
| `{x:timespan}` | `string` | `duration` |

Route arguments → **required** properties. Options with defaults → **optional** properties. `[Description]` attributes → schema `description` field.

## Tool naming

MCP tools are flat. Context segments are flattened:

| Route | Tool name |
|---|---|
| `greet` | `greet` |
| `contact add` | `contact_add` |
| `contact {id} notes` | `contact_notes` (`id` → required property) |
| `project {pid} task {tid}` | `project_task` (both → required properties) |

Separator is configurable:

```csharp
app.UseMcpServer(o => o.ToolNamingSeparator = ToolNamingSeparator.Slash);
// contact add → contact/add
```

Duplicate flattened names cause a startup error suggesting a different separator.

## Interaction in MCP mode

Commands using runtime prompts (`AskChoiceAsync`, `AskConfirmationAsync`, etc.) degrade through tiers:

| Tier | Mechanism | When |
|---|---|---|
| 1. Prefill | Values from tool arguments (`answer.confirm=yes`) | Always tried first |
| 2. Elicitation | Structured form request to user through agent client | `PrefillThenElicitation` + client supports it |
| 3. Sampling | LLM answers on behalf of user | `PrefillThenElicitation` or `PrefillThenSampling` + client supports it |
| 4. Default/Fail | Use default value or fail with descriptive error | Fallback |

`AskSecretAsync` is **always prefill-only**.

| Mode | Behavior |
|---|---|
| `PrefillThenFail` (default) | Prefill or fail — safest, works with all clients |
| `PrefillThenDefaults` | Prefill, then use prompt defaults |
| `PrefillThenElicitation` | Prefill → elicitation → sampling → fail — best UX |
| `PrefillThenSampling` | Prefill → sampling → fail |

```csharp
app.UseMcpServer(o => o.InteractivityMode = InteractivityMode.PrefillThenElicitation);
```

> Commands can also use sampling and elicitation directly via `IMcpSampling` and `IMcpElicitation`. See [mcp-agent-capabilities.md](mcp-agent-capabilities.md).

## Output rules

| Method | Where it goes | Use? |
|---|---|---|
| **Return value** | `CallToolResult.Content` (JSON) | **Yes.** Preferred for all data. |
| **`IReplInteractionChannel`** | MCP primitives (progress, prompts, user-facing notices/problems) | **Yes.** Portable feedback that also works outside MCP. |
| **`IMcpFeedback`** | MCP progress and logging/message notifications | **Yes.** MCP-specific feedback when you need direct control. |
| **`ReplSessionIO.Output`** | Session output | Advanced cases only. |
| **`Console.WriteLine`** | Bypasses Repl abstraction | **No.** Anti-pattern in MCP. |
| **`Console.OpenStandardOutput()`** | MCP stdio transport directly | **Never.** Corrupts JSON-RPC. |

> **Why this matters:** Console-style writes blur the boundary between result data, progress, logs, and protocol traffic. In MCP, this ranges from confusing agent behavior to protocol corruption.

`WriteProgressAsync` maps to MCP progress notifications. `WriteStatusAsync` maps to log messages (`level: info`):

```csharp
app.Map("import", async (IReplInteractionChannel interaction, CancellationToken ct) =>
{
    await interaction.WriteProgressAsync("Importing...", 0, ct);
    // ... work ...
    await interaction.WriteProgressAsync("Done", 100, ct);
    return Results.Success("Imported.");
});
```

### Runtime feedback mapping

The interaction channel is the preferred API when the feedback should stay portable across console, hosted sessions, and MCP.

| Repl API | MCP behavior |
|---|---|
| `WriteProgressAsync("Label", 40)` | `notifications/progress` with `progress = 40`, `total = 100` |
| `WriteIndeterminateProgressAsync(...)` | `notifications/progress` with a message and no `total` |
| `WriteWarningProgressAsync(...)` | `notifications/progress` plus a warning-level message notification |
| `WriteErrorProgressAsync(...)` | `notifications/progress` plus an error-level message notification |
| `WriteNoticeAsync(...)` | info-level message notification |
| `WriteWarningAsync(...)` | warning-level message notification |
| `WriteProblemAsync(...)` | error-level message notification |

Notes:

- `ClearProgressAsync()` clears local host rendering. MCP clients typically just stop receiving progress updates and then see the final tool result.
- The final tool result still comes from the handler's return value (`Results.Success(...)`, `Results.Error(...)`, and so on). Progress and message notifications are intermediate feedback, not replacements for the result.
- Use `IMcpFeedback` only when the behavior is intentionally MCP-specific. Prefer `IReplInteractionChannel` when the same command should behave well in console and hosted sessions too.

## Controlling which commands are exposed

| Strategy | Granularity | Example |
|---|---|---|
| `.AutomationHidden()` | Per-command | Interactive-only commands |
| `.Hidden()` | Per-command | Hidden from all surfaces |
| `CommandFilter` | App-level | `o.CommandFilter = c => !c.Path.StartsWith("admin")` |
| Module presence + `Programmatic` | Per-module | Entire feature areas |

```csharp
app.UseMcpServer(o =>
{
    o.CommandFilter = cmd => !cmd.Path.StartsWith("debug", StringComparison.OrdinalIgnoreCase);
});
```

## Configuration options

```csharp
app.UseMcpServer(o =>
{
    o.ServerName = "MyApp";                                     // MCP initialize response
    o.ServerVersion = "1.0.0";                                  // MCP initialize response
    o.ContextName = "mcp";                                      // myapp {ContextName} serve
    o.ToolNamingSeparator = ToolNamingSeparator.Underscore;     // contact_add
    o.ResourceUriScheme = "repl";                               // resource URIs: repl://path
    o.InteractivityMode = InteractivityMode.PrefillThenFail;    // interaction degradation
    o.ResourceFallbackToTools = false;                          // also expose resources as tools
    o.PromptFallbackToTools = false;                            // also expose prompts as tools
    o.DynamicToolCompatibility = DynamicToolCompatibilityMode.Disabled; // shim for weak clients
    o.EnableApps = false;                                       // usually auto-enabled by MCP App mappings
    o.CommandFilter = cmd => true;                              // filter which commands become tools
    o.Prompt("summarize", (string topic) => ...);               // explicit prompt registration
    o.UiResource("ui://custom/app", () => "...");               // raw MCP App HTML resource
});
```

## Agent configuration

### Claude Desktop

**File:** `~/.config/Claude/claude_desktop_config.json` (macOS) or `%APPDATA%\Claude\claude_desktop_config.json` (Windows)

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

### Claude Code

**File:** `~/.claude.json` or project `.claude/settings.json`

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

### VS Code (GitHub Copilot)

**File:** `.vscode/mcp.json` (workspace) or `~/.mcp.json` (global)

```json
{
  "servers": {
    "myapp": {
      "type": "stdio",
      "command": "myapp",
      "args": ["mcp", "serve"]
    }
  }
}
```

### Cursor

**File:** `.cursor/mcp.json` (project) or `~/.cursor/mcp.json` (global)

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

### Debugging with MCP Inspector

```bash
npx @modelcontextprotocol/inspector myapp mcp serve
```

## Client compatibility

Feature support varies across agents. Check [mcp-availability.com](https://mcp-availability.com/) for current data.

| Feature | Claude Desktop | Claude Code | Codex | VS Code Copilot | Cursor | Continue |
|---|---|---|---|---|---|---|
| Tools | Yes | Yes | Yes | Yes | Yes | Yes |
| Resources | Yes | — | — | Yes | Yes | — |
| Prompts | Yes | — | — | Yes | — | Yes |
| Discovery (`list_changed`) | — | Yes | — | — | — | — |
| Sampling | — | — | — | Yes | — | — |
| Elicitation | — | — | — | Yes | — | — |

### MCP Apps host compatibility

| Host | MCP Apps UI | `fullscreen` | `pip` | Notes |
|---|---|---|---|---|
| VS Code Copilot | Yes | No | No | Inline in chat only; see [VS Code MCP developer guide](https://code.visualstudio.com/api/extension-guides/ai/mcp) |
| Microsoft 365 Copilot | Yes | Yes | No | Supports fullscreen widgets; see [M365 Copilot UI widgets](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/declarative-agent-ui-widgets) |
| Other hosts | Varies | Varies | Varies | Check `availableDisplayModes` or fall back to inline |

## Known limitations

- **Collection parameters** (`List<T>`, `int[]`): MCP passes JSON arrays as a single element. The CLI binding layer expects repeated values (`--tag vip --tag priority`), so collection parameters are not correctly bound from MCP tool calls yet. Use string parameters with custom parsing as a workaround.
- **Parameterized resources**: Commands with route parameters (e.g. `config {env}`) marked `.AsResource()` are exposed as MCP resource templates with URI variables (e.g. `repl://config/{env}`). Agents read them via `resources/read` with the concrete URI.

## Publishing as a dotnet tool MCP server

Repl apps can be published as NuGet tools for zero-config agent discovery.

### Project configuration

```xml
<PropertyGroup>
  <PackAsTool>true</PackAsTool>
  <ToolCommandName>myapp</ToolCommandName>
  <PackageType>McpServer</PackageType>
</PropertyGroup>
```

### `.mcp/server.json`

Include this file in the package to enable registry-based discovery:

```json
{
  "$schema": "https://static.modelcontextprotocol.io/schemas/2025-10-17/server.schema.json",
  "name": "io.github.myorg/myapp",
  "description": "My application MCP server",
  "version": "1.0.0",
  "packages": [
    {
      "registryType": "nuget",
      "registryBaseUrl": "https://api.nuget.org",
      "identifier": "MyApp",
      "version": "1.0.0",
      "transport": { "type": "stdio" },
      "packageArguments": [
        { "type": "positional", "value": "mcp" },
        { "type": "positional", "value": "serve" }
      ],
      "environmentVariables": []
    }
  ]
}
```

### Publishing and installation

```bash
# Publish
dotnet pack -c Release
dotnet nuget push bin/Release/MyApp.1.0.0.nupkg --source https://api.nuget.org/v3/index.json

# Install and run
dotnet tool install -g MyApp
myapp mcp serve

# Run without install
dnx -y MyApp -- mcp serve
```

NuGet.org discovery: `nuget.org/packages?packagetype=mcpserver`
