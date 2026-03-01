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

These public types live under `Repl.Parameters`.
Typical app code starts with:

```csharp
using Repl.Parameters;
```

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

## Notes

- this document is intentionally focused on parameter-system behavior and tradeoffs
- API-level and phased implementation details remain tracked in active engineering tasks
