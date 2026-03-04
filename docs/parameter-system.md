# Parameter System

This document describes Repl Toolkit's parameter/option model and key design decisions.

## Goals

- keep app-side declaration simple (handler-first, low ceremony)
- keep runtime parsing strict and predictable by default
- share one option model across parsing, help, completion, and docs

## Current behavior highlights

- unknown command options are validation errors by default
- option value syntaxes: `--name value`, `--name=value`, `--name:value`
- option parsing is case-sensitive by default, configurable via `ParsingOptions.OptionCaseSensitivity`
- response files are supported with `@file.rsp` (non-recursive)
- custom global options can be registered via `ParsingOptions.AddGlobalOption<T>(...)`
- signed numeric literals (`-1`, `-0.5`, `-1e3`) are treated as positional values, not options

## Public declaration API

Application-facing parameter DSL:

- `ReplOptionAttribute`
  - canonical `Name`
  - explicit `Aliases` (full tokens, for example `-m`, `--mode`)
  - explicit `ReverseAliases` (for example `--no-verbose`)
  - `Mode` (`OptionOnly`, `ArgumentOnly`, `OptionAndPositional`)
  - optional per-parameter `CaseSensitivity`
  - optional `Arity`
- `ReplArgumentAttribute`
  - optional positional `Position`
  - `Mode`
- `ReplValueAliasAttribute`
  - maps a token to an injected parameter value (for example `--json` -> `output=json`)
- `ReplEnumFlagAttribute`
  - maps enum members to explicit alias tokens

Supporting enums:

- `ReplCaseSensitivity`
- `ReplParameterMode`
- `ReplArity`

### Options groups

- `ReplOptionsGroupAttribute` (on a class) marks it as a reusable parameter group
- the group's public writable properties become command options
- standard `ReplOptionAttribute`, `ReplArgumentAttribute`, `ReplValueAliasAttribute` apply on properties
- PascalCase property names are automatically lowered to camelCase for canonical tokens (`Format` → `--format`)
- group properties are `OptionOnly` by default; positional binding is opt-in via explicit property attributes
- properties with initializer values serve as defaults (no `HasDefaultValue` on `PropertyInfo`, so arity defaults to `ZeroOrOne`)
- named + positional values for the same group property in one invocation are rejected as validation errors
- abstract/interface group types and nested groups are rejected at registration time
- parameter name collisions between group properties and regular handler parameters are detected at registration time
- positional group properties cannot be mixed with positional non-group handler parameters in the same command

### Temporal range types

Three public record types represent temporal intervals:

- `ReplDateRange(DateOnly From, DateOnly To)`
- `ReplDateTimeRange(DateTime From, DateTime To)`
- `ReplDateTimeOffsetRange(DateTimeOffset From, DateTimeOffset To)`

These types live under `Repl` namespace and support two parsing syntaxes:

- range: `start..end` (double-dot separator)
- duration: `start@duration` (at sign with `TimeSpanLiteralParser` duration)

Reversed ranges (`To < From`) are validation errors.
For `ReplDateRange` (`DateOnly`), `start@duration` accepts whole-day durations only.

These public types live under `Repl.Parameters`.
Typical app code starts with:

```csharp
using Repl.Parameters;
```

## Public namespace map

The public API is grouped by concern:

- `Repl.Parameters` for option/argument declaration attributes
- `Repl.Documentation` for documentation export contracts
- `Repl.ShellCompletion` for shell completion setup/runtime options
- `Repl.Terminal` for terminal metadata/control contracts
- `Repl.Interaction` for prompt/progress/status interaction contracts
- `Repl.Autocomplete` for interactive autocomplete options
- `Repl.Rendering` for ANSI rendering/palette contracts

## Internal architecture boundary

The option engine internals are intentionally not public:

- schema model and runtime parser internals live under `src/Repl.Core/Internal/Options`
- these internals are consumed by command parsing, help rendering, shell completion, and documentation export
- only the declaration DSL above is public for application code

Documentation-export contracts are also separated from the root namespace:

- `DocumentationExportOptions`
- `ReplDocumentationModel`
- `ReplDoc*` records

These types now live under `Repl.Documentation`.

## Help/completion/doc consistency

Command option metadata is generated from one internal schema per route.
This same schema drives:

- runtime parsing and diagnostics
- command help option sections
- shell option completion candidates
- exported documentation option metadata

## System.CommandLine comparison

### Similarities

- modern long-option syntaxes and `--` sentinel semantics
- structured parsing errors and typo suggestions
- explicit aliases and discoverable command surfaces

### Differences

- Repl Toolkit is handler-first and REPL/session-aware by design
- global options are consumed before command routing and can be app-extended
- response-file expansion is disabled by default in interactive sessions
- short-option bundling (`-abc` -> `-a -b -c`) is not enabled implicitly
- reusable options groups and temporal range literals are first-class in Repl Toolkit, while System.CommandLine typically requires custom composition/parsing for equivalent behavior

## Notes

- this document is intentionally focused on parameter-system behavior and tradeoffs
- API-level and phased implementation details remain tracked in active engineering tasks
