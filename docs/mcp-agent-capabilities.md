# Agent Capabilities: Sampling and Elicitation

Use MCP [sampling](https://modelcontextprotocol.io/specification/2025-11-05/client/sampling) and [elicitation](https://modelcontextprotocol.io/specification/2025-11-05/client/elicitation) directly from your command handlers to interact with the connected agent client.

See also: [sample 08-mcp-server](../samples/08-mcp-server/) for a working example that uses both in a CSV import workflow.

Related guides:

- [mcp-server.md](mcp-server.md) — interaction degradation (prefill/elicitation/sampling via `IReplInteractionChannel`)
- [mcp-advanced.md](mcp-advanced.md) — roots, dynamic tools, MCP Apps

## Overview

Repl provides two MCP capabilities as injectable interfaces:

| Interface | MCP capability | What it does |
|---|---|---|
| `IMcpSampling` | [Sampling](https://modelcontextprotocol.io/specification/2025-11-05/client/sampling) | Ask the connected LLM to generate a completion |
| `IMcpElicitation` | [Elicitation](https://modelcontextprotocol.io/specification/2025-11-05/client/elicitation) | Ask the user for structured input through the agent client |

Both work like `IMcpClientRoots` — inject them into any command handler, check `IsSupported`, and use them. They are automatically excluded from MCP tool schemas.

> **Note:** These interfaces give your commands _direct_ access to MCP capabilities. This is different from `IReplInteractionChannel`, which uses sampling and elicitation _internally_ as part of its [interaction degradation](mcp-server.md#interaction-in-mcp-mode) strategy. Use `IReplInteractionChannel` when you want portable prompts that work across CLI, REPL, and MCP. Use `IMcpSampling` / `IMcpElicitation` when you want MCP-specific behavior that only makes sense when an agent is connected.

## When to use these

Sampling and elicitation are most useful as **steps inside a larger workflow** — not as standalone commands. An agent can already summarize or classify on its own; the value is when your command orchestrates something the agent cannot:

| Pattern | Sampling | Elicitation |
|---|---|---|
| Column mapping for a CSV import | LLM identifies which headers map to which fields | — |
| Ticket triage pipeline | LLM classifies the ticket | User confirms or adjusts the classification |
| Data import with duplicates | LLM fuzzy-matches incoming vs existing records | User decides how to handle conflicts |
| Dynamic deployment wizard | — | User picks environment, flags, and options at runtime |
| Content generation pipeline | LLM drafts a reply or summary | User reviews and approves before sending |

## Sampling

Sampling lets your command ask the connected LLM to generate text. The request goes from your MCP server to the agent client, which runs it through its language model and returns the result.

### API

```csharp
public interface IMcpSampling
{
    bool IsSupported { get; }
    ValueTask<string?> SampleAsync(string prompt, int maxTokens = 1024, CancellationToken cancellationToken = default);
}
```

- `IsSupported` — `true` when the connected client declares sampling capability
- `SampleAsync` — returns the model's text response, or `null` if sampling is not supported
- `maxTokens` — caps the response length (default 1024)

### Example: column mapping in a CSV import

The app reads a CSV but doesn't know what the columns mean. The LLM identifies the mapping — something the app can't do alone:

```csharp
app.Map("import {file}",
    async (string file, ContactStore contacts, IMcpSampling sampling,
        IReplInteractionChannel interaction, CancellationToken ct) =>
{
    // Phase 1: read the file
    await interaction.WriteProgressAsync("Reading CSV...", 0, ct);
    var (headers, rawRows) = CsvParser.ReadRaw(file);

    // Phase 2: ask the LLM to map columns (sampling)
    int nameCol = 0, emailCol = 1;

    if (sampling.IsSupported)
    {
        await interaction.WriteProgressAsync("Identifying columns...", 15, ct);

        var mapping = await sampling.SampleAsync(
            "A CSV file has these column headers:\n" +
            string.Join(", ", headers.Select((h, i) => $"[{i}] \"{h}\"")) + "\n\n" +
            "Which column index contains the person's name and which contains " +
            "their email address? Reply as: name=0 email=2",
            maxTokens: 50, cancellationToken: ct);

        (nameCol, emailCol) = CsvParser.ParseColumnMapping(mapping, nameCol, emailCol);
    }

    // Phase 3: import with resolved columns
    await interaction.WriteProgressAsync("Importing...", 50, ct);
    var rows = CsvParser.MapRows(rawRows, nameCol, emailCol);
    var imported = contacts.Import(rows);

    return Results.Success($"Imported {imported} contacts.");
})
.LongRunning().OpenWorld();
```

The sampling step is **optional** — without it, the command falls back to positional columns. With it, the command handles arbitrary CSV formats automatically.

### Other sampling patterns

**Classify or triage** — ask the LLM to categorize data as a step before acting on it:

```csharp
var category = await sampling.SampleAsync(
    $"Classify as 'bug', 'feature', or 'question' (reply with the word only):\n\n{ticket.Body}",
    maxTokens: 20, cancellationToken: ct);
```

**Extract structured data** — parse unstructured text into fields:

```csharp
var extracted = await sampling.SampleAsync(
    "Extract names and emails as JSON: [{\"name\": \"...\", \"email\": \"...\"}]\n\n" + emailBody,
    maxTokens: 512, cancellationToken: ct);
```

**Draft content** — generate a starting point for human review:

```csharp
var draft = await sampling.SampleAsync(
    $"Draft a reply to this support ticket:\n\n{ticket.Body}",
    maxTokens: 512, cancellationToken: ct);
```

In all cases, sampling is a **step** in a pipeline — the command does real work before and after.

## Elicitation

Elicitation asks the user for structured input through the agent's UI. The client renders a form with typed fields (text, boolean, enum) and returns the user's response. This is different from sampling — the _user_ answers, not the LLM.

### API

```csharp
public interface IMcpElicitation
{
    bool IsSupported { get; }
    ValueTask<string?> ElicitTextAsync(string message, CancellationToken ct = default);
    ValueTask<bool?> ElicitBooleanAsync(string message, CancellationToken ct = default);
    ValueTask<string?> ElicitChoiceAsync(string message, IReadOnlyList<string> choices, CancellationToken ct = default);
    ValueTask<double?> ElicitNumberAsync(string message, CancellationToken ct = default);
}
```

- `IsSupported` — `true` when the connected client declares elicitation capability
- Each method returns `null` when the client does not support elicitation or the user cancels
- The interface is dependency-agnostic — no MCP SDK types are exposed

| Method | Renders as | Example |
|---|---|---|
| `ElicitTextAsync` | Text input | Free-text name, description |
| `ElicitBooleanAsync` | Checkbox / toggle | Confirm, enable/disable |
| `ElicitChoiceAsync` | Dropdown / radio group | Choose environment, priority |
| `ElicitNumberAsync` | Number input | Port, count, threshold |

> **Multi-field elicitation:** The current API handles one field at a time, which covers most use cases. If you need to gather multiple values in a single form (e.g., environment + dry-run toggle + tag), please [open a feature request](https://github.com/yllibed/repl/issues) — this is a planned extension.

### Example: conflict resolution during import

After detecting duplicate contacts, the command asks the user how to handle them:

```csharp
// ... earlier in the import command, after duplicate detection ...

var duplicates = FindDuplicates(rows, existingContacts);

if (duplicates.Count > 0 && elicitation.IsSupported)
{
    string[] strategies = ["skip", "overwrite", "keep-both"];

    var choice = await elicitation.ElicitChoiceAsync(
        $"{duplicates.Count} contact(s) may already exist. How should they be handled?",
        strategies,
        ct);

    if (choice is null)
        return Results.Cancelled("Import cancelled during conflict resolution.");

    rows = ApplyStrategy(rows, duplicates, strategies[choice.Value]);
}

// ... continue with the import ...
```

### Other elicitation patterns

**Dynamic configuration** — ask the user for settings that depend on runtime context:

```csharp
var envIndex = await elicitation.ElicitChoiceAsync(
    $"Which environment for {service}?",
    availableEnvironments,  // built from runtime data
    ct);
var env = envIndex is not null ? availableEnvironments[envIndex.Value] : "dev";

var dryRun = await elicitation.ElicitBooleanAsync("Dry run?", ct);
```

**Guided selection** — let the user choose when the code discovers multiple options:

```csharp
var instances = registry.GetInstances(service);
if (instances.Count > 1 && elicitation.IsSupported)
{
    var hosts = instances.Select(i => i.Host).ToList();
    var selected = await elicitation.ElicitChoiceAsync(
        $"Multiple {service} instances found. Which one?",
        hosts,
        ct);

    // ... connect to the chosen instance ...
}
```

## Combining both in a workflow

The most powerful pattern uses sampling and elicitation as successive steps: the LLM analyzes data, the user confirms or adjusts, then the command acts. See [sample 08-mcp-server](../samples/08-mcp-server/) for a complete example that:

1. Reads a CSV file
2. Uses **sampling** to identify which columns map to name and email
3. Detects duplicate contacts against the existing store
4. Uses **elicitation** to ask the user how to handle conflicts
5. Imports the resolved contacts with progress reporting

Both steps are optional — the command works without them but produces better results when the agent supports them.

## Graceful degradation

Both interfaces return `null` when the client does not support the capability. Design commands so the capability is an **enhancement**, not a requirement:

```csharp
// Best: optional enhancement — command works either way
int nameCol = 0, emailCol = 1;  // sensible defaults
if (sampling.IsSupported)
{
    // ... LLM improves the result ...
}

// Acceptable: error when the capability is essential to the command's purpose
if (!elicitation.IsSupported)
    return Results.Error("needs-elicitation", "This command requires elicitation support.");
```

## Client compatibility

Not all MCP clients support sampling and elicitation. The table below lists agents with **confirmed support** — agents not listed either do not support these capabilities or have not been validated.

| Agent | Sampling | Elicitation | Source | Validated |
|---|---|---|---|---|
| VS Code Copilot (Chat) | Yes | Yes | [MCP spec support](https://code.visualstudio.com/docs/copilot/chat/mcp-servers) | 2026-04 |

Check [mcp-availability.com](https://mcp-availability.com/) for the latest data. Support is expanding rapidly — design your commands to degrade gracefully so they work everywhere even when a capability is missing.

## IMcpSampling vs IReplInteractionChannel

| | `IMcpSampling` / `IMcpElicitation` | `IReplInteractionChannel` |
|---|---|---|
| **Purpose** | Direct MCP capability access for data processing and user input | Portable user interaction (prompts, confirmations) |
| **Works in CLI/REPL** | No (`IsSupported` = false) | Yes (renders console prompts) |
| **Works in MCP** | When client supports the capability | Always (with [degradation tiers](mcp-server.md#interaction-in-mcp-mode)) |
| **Who answers** | Sampling: the LLM. Elicitation: the user. | The user (or LLM as fallback in MCP) |
| **Use when** | The command needs AI processing or rich structured input as part of a workflow | You need a simple confirmation, choice, or text prompt that works everywhere |
