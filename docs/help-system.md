# Help System

## Help Resolution

When `--help` is requested, `HelpTextBuilder` determines what to display:

1. **Command help** — when input matches a specific command (exact route match or unambiguous prefix)
   - Shows: usage, description, arguments (from dynamic segments), options (from handler parameters), answer slots

2. **Scope help** — when input matches a context or is ambiguous
   - Shows: available sub-commands and nested contexts at the current scope
   - Groups commands hierarchically

The decision flow:

- Try exact match on registered routes (`IsExactMatch()`)
- Try prefix match (`MatchesPrefix()`)
- Check for dynamic continuations at next segment level
- If a single command matches → command help; otherwise → scope help

## Help Entry Points

- CLI: `myapp --help`, `myapp users --help`
- Interactive: `help`, `?`, or `help <command>`
- Programmatic: `app.CreateDocumentationModel()` for structured export

## Typo Suggestions

When a command or option is not found, the framework suggests the closest match using Levenshtein distance:

- **Commands**: suggests if distance ≤ `max(2, input.Length / 3)`. Prefers exact prefix matches.
- **Options**: suggests if distance ≤ 2 (fixed threshold).

Example: `myapp deply` → "Did you mean 'deploy'?"

## Documentation Model Export

`CreateDocumentationModel()` returns a structured `ReplDocumentationModel`:

```csharp
var model = app.CreateDocumentationModel();
// model.App       — app metadata (name, version, description)
// model.Commands  — all commands with arguments, options, answers
// model.Contexts  — all contexts with descriptions
// model.Resources — resource-annotated commands
```

Each `ReplDocCommand` includes:

- Route template, description, details
- Arguments (from dynamic segments) with types and constraints
- Options (from handler parameters) with aliases, defaults, arity
- Answer slots (from `[Answer]` / `.WithAnswer()`)
- Behavioral flags (read-only, destructive, idempotent, etc.)

This model powers:

- `--help` text rendering
- MCP tool/resource/prompt schema generation and MCP Apps metadata
- Shell completion candidate generation

See also: [Commands](commands.md) | [MCP Server](mcp-server.md) | [Parameter System](parameter-system.md)
