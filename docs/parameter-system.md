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
