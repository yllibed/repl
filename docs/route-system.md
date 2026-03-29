# Route System

## Route Template Syntax

Route templates are strings passed to `app.Map()`. They contain static segments (literal text) and dynamic segments (parameters in braces).

```csharp
app.Map("user {id:int}", handler);      // explicit constraint
app.Map("search {query}", handler);     // defaults to string
app.Map("file {path:uri}", handler);    // URI constraint
app.Map("item {id?}", handler);         // optional trailing segment
```

Syntax: `{name}`, `{name:type}`, `{name?}`, `{name:type?}`

- Optional segments must be trailing (required cannot follow optional)
- One constraint per segment (no multiple `:type` per segment)

## Built-in Constraints

Complete table of built-in constraint types:

| Constraint | .NET Type | Description | Example |
|---|---|---|---|
| `string` | `string` | Any text (default) | `"hello"` |
| `alpha` | `string` | Letters only | `"abc"` |
| `bool` | `bool` | Boolean | `"true"`, `"false"` |
| `int` | `int` | Integer (supports `_` separators) | `"42"`, `"1_000"` |
| `long` | `long` | Long integer | `"1000000"` |
| `email` | `string` | RFC email via `MailAddress` | `"user@example.com"` |
| `uri` | `Uri` | Absolute URI | `"https://example.com"` |
| `url` | `Uri` | Absolute URI with http(s) + valid host | `"https://example.com"` |
| `urn` | `Uri` | URI with `urn:` scheme | `"urn:isbn:0451450523"` |
| `date` / `dateonly` / `date-only` | `DateOnly` | Date | `"2024-01-15"` |
| `time` / `timeonly` / `time-only` | `TimeOnly` | Time | `"10:30:00"` |
| `datetime` / `date-time` | `DateTime` | Date and time | `"2024-01-15T10:30"` |
| `datetimeoffset` / `date-time-offset` | `DateTimeOffset` | Date/time with offset | `"2024-01-15T10:30+01:00"` |
| `timespan` / `time-span` | `TimeSpan` | Duration | `"2h30m"`, `"30d"` |
| `guid` | `Guid` | GUID | `"123e4567-e89b-..."` |

Implicit types (inferred from handler parameter type, not usable as constraint names):

- `FileInfo` — file path
- `DirectoryInfo` — directory path
- `ReplDateRange`, `ReplDateTimeRange`, `ReplDateTimeOffsetRange` — range syntax with `..` and `@`

## Type Inference

When no explicit constraint is in the template, the framework infers from the handler parameter type:

```csharp
app.Map("user {id}", (int id) => ...);  // inferred as :int
app.Map("path {p}", (Uri p) => ...);    // inferred as :uri
```

## Custom Constraints

Register custom constraints via `ParsingOptions.AddRouteConstraint()`:

```csharp
app.Options(o => o.Parsing.AddRouteConstraint("ipv4", value =>
    System.Net.IPAddress.TryParse(value, out var addr) &&
    addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));

app.Map("server {address:ipv4}", (string address) => ...);
```

Reserved names cannot be overridden (all built-in constraint names are reserved).

## Route Resolution

When a command is invoked, `RouteResolver` matches input tokens against all registered routes:

- **Literal segments** are scored higher than dynamic segments
- The best-scoring match wins
- If no match: framework suggests similar commands (Levenshtein distance)
- If ambiguous: error reported with alternatives

## Parameter Binding Precedence

Handler parameters are resolved in this order:

1. `CancellationToken` — injected from execution context
2. Explicit attributes — `[FromServices]` or `[FromContext]`
3. Options group — `[ReplOptionsGroup]` for multi-property binding
4. Route values — captured from dynamic segments (`{name}`)
5. Named options — from `--option value` syntax
6. Context values / DI services — resolved from context stack or service provider
7. Positional arguments — remaining tokens consumed left-to-right
8. Parameter defaults — C# default values
9. Null — for nullable types without other source

Binding mode can be controlled per parameter:

- `OptionAndPositional` (default) — accept from both `--name` and positional
- `OptionOnly` — require `--name` form
- `ArgumentOnly` — require positional form

## Context Routes

`Context()` creates nested route hierarchies with optional validation:

```csharp
app.Context("project {id:int}", project =>
{
    project.Map("build", handler);     // matches: project 42 build
    project.Map("deploy", handler);    // matches: project 42 deploy
}, validation: (int id) => id > 0);
```

Context validation delegates receive bound route values as parameters.

Cross-reference `docs/commands.md` for command definition, `docs/parameter-system.md` for parameter attributes, `docs/execution-pipeline.md` for the full execution flow.
