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

### Command-backed resource MIME types

Standard `.AsResource()` and auto-promoted `.ReadOnly()` resources are rendered through the MCP output converter. The default MCP converter is JSON, so command-backed resources advertise `application/json` and `resources/read` returns serialized JSON text for normal command results. This is a wire-observable change from earlier versions that advertised `text/plain` for the same JSON content.

Per-resource non-JSON output is not declared on `.AsResource()` today: the media type must come from the output path that actually produced the bytes. Use MCP Apps resources for HTML (`text/html;profile=mcp-app`) or wait for resource-specific converter/blob support before exposing Markdown, YAML, or binary resource bodies.

Resource reads bypass the tool-call text fallback, so a handler with no value keeps the serialized JSON payload (for example `null`) instead of the tool placeholder text (`OK`).

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
| **Return value** | `CallToolResult.Content` and, for paged results, `StructuredContent` | **Yes.** Preferred for all data. |
| **`IReplInteractionChannel`** | MCP primitives (progress, prompts, user-facing notices/problems) | **Yes.** Portable feedback that also works outside MCP. |
| **`IMcpFeedback`** | MCP progress and logging/message notifications | **Yes.** MCP-specific feedback when you need direct control. |
| **`ReplSessionIO.Output`** | Session output | Advanced cases only. |
| **`Console.WriteLine`** | Bypasses Repl abstraction | **No.** Anti-pattern in MCP. |
| **`Console.OpenStandardOutput()`** | MCP stdio transport directly | **Never.** Corrupts JSON-RPC. |

> **Why this matters:** Console-style writes blur the boundary between result data, progress, logs, and protocol traffic. In MCP, this ranges from confusing agent behavior to protocol corruption.

### Paged tool results

Paged MCP tool schemas include two reserved Repl result-flow inputs:

- `_replCursor`: opaque continuation cursor returned by a previous paged result.
- `_replPageSize`: requested page size.

These inputs are emitted only for commands that accept `IReplPagingContext` or
return a paged result. Other tools reject them like any undeclared MCP argument.

Handlers receive these values through `IReplPagingContext`, not as business parameters. A handler can return `ReplPage<T>`:

```csharp
app.Map("contacts", (IReplPagingContext paging, ContactStore store) =>
{
    var page = store.Query(paging.Cursor, paging.SuggestedPageSize);
    return paging.Page(page.Items, page.NextCursor, page.TotalCount);
}).ReadOnly();
```

MCP responses for `ReplPage<T>` include:

- `StructuredContent`: `{ "$type": "page", items, pageInfo }`
- `Content`: configurable text fallback for clients that do not use structured content

By default, `Content` contains compact serialized JSON so agents that ignore
`StructuredContent` can still see the first page and cursor. Applications can
choose a cheaper text fallback:

```csharp
app.UseMcpServer(o =>
{
    o.PagedResultTextMode = McpPagedResultTextMode.SummaryOnly;
});
```

| `PagedResultTextMode` | `Content` behavior | Trade-off |
|---|---|---|
| `SerializedJson` (default) | Compact JSON page envelope | Best compatibility; higher token cost |
| `SummaryOnly` | Short summary; cursor value stays only in structured content | Lowest token cost; clients must read structured content to continue |
| `SummaryAndSerializedJson` | Summary plus compact JSON page envelope | Most verbose; useful for diagnostics and weak clients |

Repl accepts compact cursor tokens only: non-empty, at most 512 characters, no
whitespace, no control characters, and not starting with `-`. Page-size tokens
must be numeric and at most 10 characters before normal result-flow clamping is
applied. Tool-call arguments are validated against the generated MCP input
schema before Repl reconstructs CLI tokens. Undeclared keys are rejected instead
of being converted to `--{key}` options, and business argument values that start
with `--` are rejected because the downstream CLI parser treats those as option
tokens.

MCP paging continuation works best when the client preserves structured tool
content. When `PagedResultTextMode` includes serialized JSON, the text fallback
also contains the page envelope; when it is `SummaryOnly`, the raw cursor stays
only in `StructuredContent.pageInfo.nextCursor`.

| Agent/client behavior | Paging support | Repl fallback |
|---|---|---|
| Reads `StructuredContent` and can call the same tool again | Full continuation with `_replCursor` and `_replPageSize` | Not needed |
| Reads only `Content` text | Sees first-page JSON by default; can continue only if it can reuse the cursor text safely | Configure `SerializedJson` or `SummaryAndSerializedJson` for compatibility, `SummaryOnly` for token savings |
| Ignores custom/reserved input properties | First page still works | Tool returns bounded first page |
| Does not support structured content | First page still works through text fallback; automatic continuation depends on `PagedResultTextMode` | Keep `SerializedJson` for weak clients or expose a command-specific cursor option |

Applications should not rely on all agents supporting continuation equally.
For important workflows, include enough data in the first page summary for the
agent to decide whether it needs a follow-up call, and keep handlers safe when
only the first page is consumed.

`WriteProgressAsync` maps to MCP progress notifications. `WriteStatusAsync` maps to log messages (`level: info`). See [Progress](progress.md#mcp) for the centralized progress model across console, hosted sessions, Spectre, and MCP:

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
    o.PagedResultTextMode = McpPagedResultTextMode.SerializedJson;      // paged result text fallback
    o.EnableApps = false;                                       // usually auto-enabled by MCP App mappings
    o.CommandFilter = cmd => true;                              // filter which commands become tools
    o.Prompt("summarize", (string topic) => ...);               // explicit prompt registration
    o.UiResource("ui://custom/app", () => "...");               // raw MCP App HTML resource
});
```

## Agent configuration

Agent hosts configure the app or tool you built with Repl.Mcp. They do not
install Repl.Mcp directly.

Prefer a stable executable command, such as a published `dotnet tool`, for
shared team configs. When documenting a local sample, build it once and use
`dotnet run --no-build`, or point the host at a published executable. Plain
`dotnet run --project ...` is fragile in host configs because cold builds can
exceed startup timeouts and build/restore output can reach stdout before the MCP
JSON-RPC stream starts.

### Generic MCP client / Claude Desktop

**File:** `~/Library/Application Support/Claude/claude_desktop_config.json`
(macOS) or `%APPDATA%\Claude\claude_desktop_config.json` (Windows)

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

For a local sample project, split the launch into `command` and `args`:

```json
{
  "mcpServers": {
    "repl-contacts-sample": {
      "command": "dotnet",
      "args": [
        "run",
        "--no-build",
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

On Windows JSON, write paths with doubled backslashes, for example
`C:\\Users\\you\\src\\repl\\samples\\08-mcp-server\\McpServerSample.csproj`.

### Claude Code

Claude Code uses command registration as the standard flow:

```bash
claude mcp add myapp -- myapp mcp serve
```

For a local Repl sample after an initial build:

```bash
claude mcp add repl-contacts-sample -- dotnet run --no-build --project /absolute/path/to/repl/samples/08-mcp-server/McpServerSample.csproj -- mcp serve
```

For file-based provisioning, use `.mcp.json` at the project root for project
settings or `~/.claude.json` for user/local settings. Both use the generic
`mcpServers` JSON shape.

### VS Code / GitHub Copilot

**File:** `.vscode/mcp.json` (workspace)

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

VS Code also supports command-line registration from macOS/Linux shells:

```bash
code --add-mcp '{"name":"myapp","command":"myapp","args":["mcp","serve"]}'
```

On Windows, prefer the `.vscode/mcp.json` file. The `code.cmd --add-mcp`
argument path can mangle inline JSON in common shells; if you write JSON by
hand, remember to escape backslashes in Windows paths.

### Cursor

**File:** `.cursor/mcp.json` (project) or `~/.cursor/mcp.json` (global)

Cursor uses the generic `mcpServers` JSON shape.

### Cline

Use **Configure MCP Servers** to edit `cline_mcp_settings.json`, then add the
local stdio server with the generic `mcpServers` JSON shape. The Cline
marketplace is for published/curated servers and will not install an unpublished
local sample.

### Complete copy/paste sample

See [sample 08 — Build an MCP Server with Repl.Mcp](../samples/08-mcp-server/)
for Cursor, VS Code, Claude Code, Cline, generic JSON, and MCP Inspector
examples.

### Debugging with MCP Inspector

Use the UI for interactive exploration:

```bash
npx @modelcontextprotocol/inspector myapp mcp serve
```

For a local sample project, build first and pass the same no-build command used
by agent hosts:

```bash
npx @modelcontextprotocol/inspector dotnet run --no-build --project /absolute/path/to/repl/samples/08-mcp-server/McpServerSample.csproj -- mcp serve
```

Use CLI mode for repeatable smoke checks. `resources/list` exposes the
advertised resource MIME type, and `resources/read` exposes the MIME type and
body returned on the wire:

```bash
# Build or publish the server first; this example uses a built sample DLL.
dotnet build samples/08-mcp-server/McpServerSample.csproj -c Release

npx -y @modelcontextprotocol/inspector@0.22.0 --cli \
  dotnet samples/08-mcp-server/bin/Release/net10.0/McpServerSample.dll mcp serve \
  --method resources/list \
  | jq '.resources[] | { uri, mimeType }'

npx -y @modelcontextprotocol/inspector@0.22.0 --cli \
  dotnet samples/08-mcp-server/bin/Release/net10.0/McpServerSample.dll mcp serve \
  --method resources/read \
  --uri repl://contacts \
  | jq '.contents[] | { uri, mimeType, text }'
```

The repository also includes an opt-in `dotnet test` smoke guard for this
external toolchain:

```bash
REPL_RUN_MCP_INSPECTOR_TESTS=1 \
  dotnet test src/Repl.McpTests/Repl.McpTests.csproj -c Release \
  --filter 'TestCategory=ExternalToolchain'
```

It is skipped by default so the normal .NET test suite stays hermetic and does
not require Node/npm or npm registry access.

For command-level tests, use `Repl.Testing`: `CommandExecution.GetResult<T>()`
validates the handler return value before rendering, while `OutputText` /
`ReadJson<T>()` validate rendered output. For MCP wire contracts such as
`Resource.MimeType` and `TextResourceContents.MimeType`, use the MCP test
fixture or the opt-in Inspector CLI smoke check because those values are
protocol metadata, not `Repl.Testing` command results.

Command-backed resources expose the rendered handler return value as the
resource body. Low-level writes to `IReplIoContext.Output` are treated as
side-channel command output and are not included in `resources/read` bodies.

## Client compatibility

Feature support varies across agents. Check [mcp-availability.com](https://mcp-availability.com/) for current data.

| Feature | Claude Desktop | Claude Code | Codex | VS Code Copilot | Cursor | Continue |
|---|---|---|---|---|---|---|
| Tools | Yes | Yes | Yes | Yes | Yes | Yes |
| Paged tools, first page | Yes | Yes | Yes | Yes | Yes | Yes |
| Paged tools, automatic continuation | Depends on structured tool-result handling | Depends on structured tool-result handling | Depends on structured tool-result handling | Depends on structured tool-result handling | Depends on structured tool-result handling | Depends on structured tool-result handling |
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
