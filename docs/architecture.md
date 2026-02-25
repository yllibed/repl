# REPL Toolkit Architecture Blueprint

## Project boundaries

- `Repl.Core`
	- Dependency-free authoring/runtime core (`CoreReplApp`, `IReplMap`, `ReplOptions`, `Results.*`).
	- Keeps host/runtime-neutral abstractions.
- `Repl.Defaults`
	- DI-enabled app facade (`ReplApp`), lifecycle orchestration, and default composition profiles (`UseDefaultInteractive`, `UseCliProfile`, `UseEmbeddedConsoleProfile`).
- `Repl`
	- Meta-package entrypoint for typical apps (depends on `Repl.Core`, `Repl.Defaults`, `Repl.Protocol`).
- `Repl.Protocol`
	- Machine-readable contracts for protocol/tooling use cases:
		- Help and error contracts.
		- Minimal MCP-oriented manifest/tool/resource contracts (extension-ready, not runtime MCP mode).
- `Repl.WebSocket`
	- WebSocket session host integration (`ReplWebSocketSession`) over `StreamedReplHost`.
- `Repl.Telnet`
	- Telnet framing/session integration (`ReplTelnetSession`) with NAWS window-size negotiation.
- `Repl.Testing`
	- In-memory multi-session testing toolkit (`ReplTestHost`, `ReplSessionHandle`, typed execution results/events).
- `Repl.Tests`
	- Unit tests for pure logic and contracts.
- `Repl.IntegrationTests`
	- End-to-end runtime behavior at process/app boundary.
- `Repl.ProtocolTests`
	- Contract tests for machine-readable help/error payloads.
- `Repl.Benchmarks`
	- Performance harness for hot paths and allocation baselines.

## Quality gates

- Strict build rules from `src/Directory.Build.props`:
	- warnings as errors
	- deterministic build
	- nullable enabled
	- analyzers enabled
- Central package versions in `src/Directory.Packages.props`.
- CI restore/build/test/coverage/pack pipeline in `.github/workflows/ci.yml`.
	- Build/test validation runs on Windows, Linux, and macOS.
- CI restore audit guard in `src/Directory.Solution.targets`.

## Test conventions

- Framework:
	- `MSTest.Sdk` with Microsoft Testing Platform (`global.json`).
	- `AwesomeAssertions` for fluent assertions.
- Naming:
	- Test classes: `Given_<Subject>`
	- Test methods: `When_<Condition>_Then_<Outcome>`
- Style:
	- AAA structure (arrange/act/assert) kept explicit in each test.
	- Every test uses `[Description("...")]` to capture behavioral intent (regression guard), not method-name restatement.

## Runtime highlights implemented

- Output transformers:
	- Built-in `human`, `json`, `xml`, `yaml`.
	- Built-in `markdown` (with `--markdown` alias via output alias map).
	- Global format selectors: `--json`, `--xml`, `--yaml`, `--yml`, `--output:<format>`.
	- Unknown format returns explicit error text and non-zero exit code.
- Numeric parsing:
	- Numeric culture is configurable via `ParsingOptions.NumericCulture` (`Invariant` default, `Current` optional).
	- Integer literals support C-like forms: hexadecimal (`0xFF`), binary (`0b1010` or `1010b`), and `_` separators (`1_000_000`).
- Help/discovery:
	- Human help (`--help` or interactive `help`) remains text-first.
	- Non-human help output (`--help --json|--xml|--yaml`) now emits structured machine-readable payloads.
- Terminal/session metadata:
	- Supported metadata channels and field-level resolution order are documented in `docs/terminal-metadata.md`.
	- Transport-native signaling (DTTERM push, Telnet NAWS/TERMINAL-TYPE) is preferred.
	- `@@repl:*` control messages and out-of-band metadata are supported extension patterns.

## Branching and versioning

- NBGV (`version.json`) drives package/release versioning.
- `main` publishes prerelease.
- `release/*` publishes stable.
