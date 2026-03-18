# MCP Server Integration

Expose your Repl command graph as an [MCP](https://modelcontextprotocol.io) (Model Context Protocol) server so AI agents can discover and invoke your commands as typed tools.

See also: [sample 08-mcp-server](../samples/08-mcp-server/) for a working demo.

## Quick start

```bash
dotnet add package Repl.Mcp
```

```csharp
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

> **Note:** `UseMcpServer()` registers a hidden `mcp serve` context. The tool list is built lazily when an agent connects, so it sees all commands regardless of registration order.

## How it works

`UseMcpServer()` registers a hidden `mcp serve` command that starts an MCP stdio server. When an agent connects, the server reads the command graph via `CreateDocumentationModel()`, generates typed JSON Schema for each command, and dispatches tool calls through the standard Repl pipeline (routing, binding, middleware, rendering).

Commands map to MCP primitives:

| Repl concept | MCP primitive | How |
|---|---|---|
| `Map()` | Tool | Automatic — every non-hidden command becomes a tool |
| `Map().AsResource()` | Resource | Explicit — marks data-to-consult |
| `.ReadOnly()` | Resource (auto-promoted) | ReadOnly tools are also exposed as resources |
| `Map().AsPrompt()` | Prompt | Explicit — handler return becomes prompt template |
| `options.Prompt()` | Prompt | Explicit — registered in `ReplMcpServerOptions` |

## Annotations

Annotations tell agents how to use your tools safely. They map directly to [MCP tool annotations](https://modelcontextprotocol.io/specification/2025-03-26/server/tools#annotations):

```csharp
app.Map("contacts", handler).ReadOnly();                          // safe to call autonomously
app.Map("contact add", handler).OpenWorld().Idempotent();         // calls external systems, retriable
app.Map("contact delete {id}", handler).Destructive();            // agent asks for confirmation
app.Map("deploy", handler).Destructive().LongRunning().OpenWorld();
app.Map("wizard", handler).AutomationHidden();                    // excluded from MCP entirely
```

| Annotation | MCP hint | Agent behavior |
|---|---|---|
| `.ReadOnly()` | `readOnlyHint: true` | Call autonomously, no confirmation needed |
| `.Destructive()` | `destructiveHint: true` | Ask user for confirmation before calling |
| `.Idempotent()` | `idempotentHint: true` | Safe to retry on transient failure |
| `.OpenWorld()` | `openWorldHint: true` | Expects latency, may fail (external systems) |
| `.LongRunning()` | _(task support — future)_ | Use task-based execution |
| `.AutomationHidden()` | _(excluded from list)_ | Not visible to agents at all |

Without annotations, agents must assume worst-case (destructive, non-idempotent), leading to excessive confirmation prompts. **Annotate every command exposed to agents.**

## Rich descriptions

`WithDetails()` provides extended markdown content for agent tool descriptions and documentation export:

```csharp
app.Map("deploy {env}", handler)
    .WithDescription("Deploy the application")          // short summary (shown everywhere)
    .WithDetails("""
        Deploys to the specified environment.

        Prerequisites:
        - Valid credentials in ~/.config/deploy
        - Target environment must be provisioned

        Examples:
        - deploy staging
        - deploy production (requires approval)
        """);
```

`Description` is the short summary visible in help and tool listings. `Details` is extended content consumed by agents and documentation tools — it is not displayed in terminal `--help`.

## Tool vs Resource vs Prompt

| Aspect | Tool | Resource | Prompt |
|---|---|---|---|
| Intent | Perform an action | Consult data | Guide a conversation |
| MCP primitive | `tools/call` | `resources/read` | `prompts/get` |
| Invoked by | Model (automatic) | Model or user | User only |
| Side effects | Yes (unless ReadOnly) | No | No |
| Repl API | `Map()` | `Map().AsResource()` | `Map().AsPrompt()` or `options.Prompt()` |
| Auto-promotion | — | ReadOnly → resource | — |
| Parameters | Any types | Any types | All optional (MCP constraint) |

**When to use each:**
- **Tool**: operations that change state (`add`, `delete`, `deploy`, `import`)
- **Resource**: data sources an agent should consult before acting (`contacts`, `config`, `status`)
- **Prompt**: reusable instruction templates that shape agent behavior (`summarize-data`, `review-changes`, `troubleshoot`)

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

Route arguments become **required** properties. Options with defaults become **optional** properties. `[Description]` attributes on handler parameters populate the schema `description` field.

## Tool naming

MCP tools are flat (no hierarchy). Context segments are flattened into tool names:

| Route | Tool name |
|---|---|
| `greet` | `greet` |
| `contact add` | `contact_add` |
| `contact {id} notes` | `contact_notes` (`id` → required property) |
| `project {pid} task {tid}` | `project_task` (both → required properties) |

Dynamic segments are removed from the name and become input properties. The separator is configurable:

```csharp
app.UseMcpServer(o => o.ToolNamingSeparator = ToolNamingSeparator.Slash);
// contact add → contact/add
```

If two routes produce the same flattened name, startup throws with an actionable error suggesting a different separator.

## Interaction in MCP mode

Commands that use runtime prompts (`AskChoiceAsync`, `AskConfirmationAsync`, etc.) degrade gracefully:

| Tier | Mechanism | When |
|---|---|---|
| 1. Prefill | Values from tool arguments (`answer:confirm=yes`) | Always tried first |
| 2. Elicitation | Structured form request to user through agent client | `PrefillThenElicitation` mode + client supports it |
| 3. Sampling | LLM answers on behalf of user | `PrefillThenElicitation` or `PrefillThenSampling` + client supports it |
| 4. Default/Fail | Use default value or fail with descriptive error | Fallback |

`AskSecretAsync` is **always prefill-only** — secrets never go through elicitation or sampling.

```csharp
app.UseMcpServer(o => o.InteractivityMode = InteractivityMode.PrefillThenElicitation);
```

| Mode | Behavior |
|---|---|
| `PrefillThenFail` (default) | Prefill or fail with descriptive error |
| `PrefillThenDefaults` | Prefill, then use prompt defaults |
| `PrefillThenElicitation` | Prefill → elicitation → sampling → fail |
| `PrefillThenSampling` | Prefill → sampling → fail |

## Progress and status

`WriteProgressAsync` maps to MCP progress notifications — agents see real-time progress. `WriteStatusAsync` maps to MCP log messages (`level: info`). No changes needed in command handlers:

```csharp
app.Map("import", async (IReplInteractionChannel interaction, CancellationToken ct) =>
{
    await interaction.WriteProgressAsync("Importing...", 0, ct);
    // ... work ...
    await interaction.WriteProgressAsync("Done", 100, ct);
    return Results.Success("Imported.");
});
```

## Dynamic tool discovery

When `InvalidateRouting()` is called (module presence changes, session state changes), the MCP server automatically refreshes its tool list and emits `list_changed` notifications. Agents that support discovery refresh their available tools.

```csharp
app.MapModule(new AdminModule(),
    ctx => ctx.Channel != ReplRuntimeChannel.Programmatic);
```

## Controlling which commands are exposed

Multiple strategies at different granularities:

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
    o.InteractivityMode = InteractivityMode.PrefillThenFail;    // interaction degradation
    o.ResourceFallbackToTools = true;                           // expose resources as tools when client doesn't support resources
    o.CommandFilter = cmd => true;                              // filter which commands become tools
    o.Prompt("summarize", (string topic) => ...);               // explicit prompt registration
});
```

## MCP client compatibility

Feature support varies across agent clients. The table below reflects the state as of **March 2025** — check [mcp-availability.com](https://mcp-availability.com/) for current data.

| Feature | Claude Desktop | Claude Code | VS Code Copilot | Cursor | Continue |
|---|---|---|---|---|---|
| Tools | Yes | Yes | Yes | Yes | Yes |
| Resources | Yes | — | Yes | Yes | — |
| Prompts | Yes | — | Yes | — | Yes |
| Discovery (`list_changed`) | — | Yes | — | — | — |
| Sampling | — | — | Yes | — | — |
| Elicitation | — | — | Yes | — | — |

**Implications:**
- `PrefillThenFail` is the safest default (works with all clients)
- `PrefillThenElicitation` provides the best UX but requires elicitation support, degrading gracefully through sampling then failure
- Resources should be annotated `.ReadOnly()` as well, so they're always accessible as tools even when the client doesn't support resources

## Agent configuration

### Claude Desktop

**File:** `~/.config/Claude/claude_desktop_config.json` (macOS) or `%APPDATA%\Claude\claude_desktop_config.json` (Windows)

```json
{
  "mcpServers": {
    "myapp": {
      "command": "myapp",
      "args": ["mcp", "serve"],
      "env": {
        "MYAPP_API_KEY": "..."
      }
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
      "packageArguments": ["mcp", "serve"],
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
```

NuGet.org discovery: `nuget.org/packages?packagetype=mcpserver`

## Full example

```csharp
var app = ReplApp.Create(services =>
{
    services.AddReplMcpServer(mcp =>
    {
        mcp.ServerName = "ContactManager";
        mcp.InteractivityMode = InteractivityMode.PrefillThenElicitation;
    });
});

app.UseMcpServer();

// ── Resources ──────────────────────────────────────────────────────

app.Map("contacts", (IContactDb db) => db.GetAll())
    .WithDescription("List all contacts")
    .ReadOnly()
    .AsResource();

// ── Contact operations (grouped context) ───────────────────────────

app.Context("contact", contact =>
{
    contact.Map("{id:guid}", (Guid id, IContactDb db) => db.Get(id))
        .WithDescription("Get contact by ID")
        .ReadOnly();

    contact.Map("add", (string name, string email, IContactDb db) => db.Add(name, email))
        .WithDescription("Add a new contact")
        .WithDetails("""
            Creates a new contact record.
            The email must be unique across all contacts.
            """)
        .OpenWorld();

    contact.Map("delete {id:guid}",
        async (Guid id, IContactDb db, IReplInteractionChannel interaction, CancellationToken ct) =>
        {
            var contact = db.Get(id);
            if (!await interaction.AskConfirmationAsync("confirm", $"Delete {contact.Name}?", options: new(ct)))
                return Results.Cancelled("Aborted.");
            return db.Delete(id);
        })
        .WithDescription("Delete a contact")
        .Destructive();
});

// ── Long-running operations ────────────────────────────────────────

app.Map("import", async (string file, IContactDb db, IReplInteractionChannel interaction, CancellationToken ct) =>
    {
        await interaction.WriteProgressAsync("Importing...", 0, ct);
        var count = await db.ImportCsvAsync(file, ct);
        return Results.Success($"Imported {count} contacts.");
    })
    .WithDescription("Import contacts from CSV")
    .LongRunning()
    .OpenWorld();

// ── Prompts ────────────────────────────────────────────────────────
// Prompts are reusable instruction templates that shape how an agent
// approaches a task. The handler returns the text the agent should
// use as its starting instructions.

app.Map("troubleshoot {symptom}", (string symptom) =>
        $"The user reports: '{symptom}'. " +
        "Investigate using the available contact tools. " +
        "Check if any contacts are missing or malformed. " +
        "Summarize your findings and suggest a fix.")
    .WithDescription("Guide the agent through diagnosing a contact issue")
    .AsPrompt();

// ── Interactive-only ───────────────────────────────────────────────

app.Map("clear", async (IReplInteractionChannel ch, CancellationToken ct) =>
    {
        await ch.ClearScreenAsync(ct);
        return Results.Ok("Screen cleared.");
    })
    .AutomationHidden();

await app.RunAsync(args);
```

MCP exposure for this example:
- **Tool** `contacts` + **Resource** `repl://contacts` (explicit `AsResource()`)
- **Tool** `contact` + **Resource** `repl://contact` (auto-promoted via `ReadOnly`)
- **Tool** `contact_add` (open world)
- **Tool** `contact_delete` (destructive, confirmation via elicitation/sampling)
- **Tool** `import` (long-running, progress notifications)
- **Prompt** `troubleshoot` — guides the agent through diagnosing an issue using the contact tools
- `clear` is **not exposed** (`AutomationHidden`)
