# Configuration Reference

This is the complete configuration reference for the Repl Toolkit. All options are accessible through the `ReplOptions` object passed to `app.Options(...)`.

```csharp
var app = ReplApp.Create();
app.Options(o =>
{
    o.Interactive.Prompt = "$";
    o.Output.DefaultFormat = "json";
    o.Parsing.AllowResponseFiles = false;
});
```

See also: [Commands](commands.md) | [Shell Completion](shell-completion.md) | [Interaction](interaction.md) | [Progress](progress.md)

## ReplOptions

The root configuration object. Exposes the following section properties:

- `Parsing` — Command-line parsing behavior
- `Interactive` — REPL session and prompt settings
- `Output` — Formatting, theming, and rendering
- `Binding` — Parameter binding behavior
- `Capabilities` — Terminal capability declarations
- `AmbientCommands` — Built-in command toggles and custom ambient commands
- `Interaction` — Progress and prompt fallback settings
- `ShellCompletion` — Shell completion installation and behavior

## ParsingOptions

Accessed via `ReplOptions.Parsing`.

- `AllowUnknownOptions` (`bool`, default: `false`) — Allow options not explicitly registered.
- `OptionCaseSensitivity` (`ReplCaseSensitivity`, default: `CaseSensitive`) — Option name case sensitivity.
- `AllowResponseFiles` (`bool`, default: `true`) — Expand response files (`@args.rsp`).
- `NumericCulture` (`NumericParsingCulture`, default: `Invariant`) — Culture used for numeric conversions.

### Methods

- `AddRouteConstraint(name, predicate)` — Register a named route constraint.
- `AddGlobalOption<T>(name, aliases, defaultValue)` — Register a global option available to all commands.
- `AddGlobalOption(name, typeName, aliases, defaultValue)` — Register a global option using a type name string (`"int"`, `"bool"`, `"guid"`, etc.).

### IGlobalOptionsAccessor

Registered automatically in DI. Provides typed access to parsed global option values from middleware, DI factories, and handlers.

- `GetValue<T>(name, defaultValue)` — Get typed value, falling back to registration default then caller default.
- `GetRawValues(name)` — Get all raw string values (supports repeated options).
- `HasValue(name)` — Check if the option was explicitly provided.
- `GetOptionNames()` — Enumerate all option names with values.

Values are updated after each global option parsing pass (per-invocation in interactive mode).

### UseGlobalOptions&lt;T&gt;()

Extension method on `ReplApp`. Registers a typed class whose public settable properties become global options. The class is available via DI, populated from parsed values. Property names are converted to kebab-case (`MaxRetries` → `--max-retries`). See [Commands — Accessing global options](commands.md#accessing-global-options-outside-handlers).

## InteractiveOptions

Accessed via `ReplOptions.Interactive`.

- `Prompt` (`string`, default: `">"`) — REPL prompt text.
- `InteractivePolicy` (`InteractivePolicy`, default: `Auto`) — Controls interactive mode activation: `Auto`, `Always`, or `Never`.
- `HistoryProvider` (`IHistoryProvider?`, default: `null`) — Custom history provider.
- `Autocomplete` (`AutocompleteOptions`) — Nested autocomplete options (see below).

### AutocompleteOptions

Accessed via `ReplOptions.Interactive.Autocomplete`.

- `Mode` (`AutocompleteMode`, default: `Auto`) — Autocomplete activation mode.
- `Presentation` (`AutocompletePresentation`, default: `Hybrid`) — How suggestions are displayed.
- `MaxVisibleSuggestions` (`int`, default: `8`) — Maximum number of visible suggestions.
- `CaseSensitive` (`bool`, default: `false`) — Whether matching is case-sensitive.
- `EnableFuzzyMatching` (`bool`, default: `false`) — Enable fuzzy matching for suggestions.
- `LiveHintEnabled` (`bool`, default: `true`) — Show inline hint while typing.
- `LiveHintMaxAlternatives` (`int`, default: `5`) — Maximum alternatives shown in live hint.
- `ShowContextAlternatives` (`bool`, default: `true`) — Show context-aware alternatives.
- `ShowInvalidAlternatives` (`bool`, default: `true`) — Show invalid alternatives in suggestions.
- `ColorizeInputLine` (`bool`, default: `true`) — Colorize the input line.
- `ColorizeHintAndMenu` (`bool`, default: `true`) — Colorize hints and the suggestion menu.

## OutputOptions

Accessed via `ReplOptions.Output`.

- `DefaultFormat` (`string`, default: `"human"`) — Default output format.
- `AnsiMode` (`AnsiMode`, default: `Auto`) — ANSI color support mode.
- `ThemeMode` (`ThemeMode`, default: `Auto`) — Theme mode: `Auto`, `Light`, or `Dark`.
- `PaletteProvider` (`IAnsiPaletteProvider`, default: `DefaultAnsiPaletteProvider`) — Custom color palette provider.
- `BannerEnabled` (`bool`, default: `true`) — Enable banner output.
- `BannerFormats` (`ISet<string>`, default: `{"human"}`) — Output formats that display banners.
- `ColorizeStructuredInteractive` (`bool`, default: `true`) — Colorize JSON/XML in interactive mode.
- `PreferredWidth` (`int?`, default: `null`) — Preferred render width. `null` uses automatic detection.
- `FallbackWidth` (`int`, default: `120`) — Fallback width when the terminal is unavailable.
- `JsonSerializerOptions` (`JsonSerializerOptions`, default: Web defaults + indented) — JSON serializer options.

Built-in transformers: `human`, `json`, `xml`, `yaml`, `markdown`.

### OutputOptions Methods

- `AddTransformer(name, transformer)` — Register a custom output transformer.
- `AddAlias(alias, format)` — Register a format alias.

## BindingOptions

Accessed via `ReplOptions.Binding`.

- `AggregateConversionErrors` (`bool`, default: `true`) — Aggregate all conversion errors instead of failing on the first.

## CapabilityOptions

Accessed via `ReplOptions.Capabilities`.

- `SupportsAnsi` (`bool`, default: `true`) — Declare whether the terminal supports ANSI escape sequences.

## AmbientCommandOptions

Accessed via `ReplOptions.AmbientCommands`.

- `ExitCommandEnabled` (`bool`, default: `true`) — Enable the built-in `exit` command.
- `ShowHistoryInHelp` (`bool`, default: `false`) — Show the `history` command in help output.
- `ShowCompleteInHelp` (`bool`, default: `false`) — Show the `complete` command in help output.

### AmbientCommandOptions Methods

- `MapAmbient(name, handler, description)` — Register a custom ambient command.

## InteractionOptions

Accessed via `ReplOptions.Interaction`.

These options are configured through `app.Options(...)`. Repl does not currently auto-bind them from `IConfiguration`.

- `DefaultProgressLabel` (`string`, default: `"Progress"`) — Default label for progress indicators.
- `ProgressTemplate` (`string`, default: `"{label}: {percent:0}%"`) — Progress display template. Supports placeholders: `{label}`, `{percent}`, `{percent:0}`, `{percent:0.0}`.
- `AdvancedProgressMode` (`AdvancedProgressMode`, default: `Auto`) — Controls whether compatible hosts emit advanced terminal progress sequences. See [Progress](progress.md#advanced-terminal-progress).
- `PromptFallback` (`PromptFallback`, default: `UseDefault`) — Behavior when interactive prompts are unavailable.

## ShellCompletionOptions

Accessed via `ReplOptions.ShellCompletion`. See [Shell Completion](shell-completion.md) for setup details.

- `Enabled` (`bool`, default: `true`) — Enable shell completion support.
- `SetupMode` (`ShellCompletionSetupMode`, default: `Manual`) — Completion setup mode.
- `PreferredShell` (`ShellKind?`, default: `null`) — Preferred shell for completion. `null` uses automatic detection.
- `PromptOnce` (`bool`, default: `true`) — Only prompt the user once for completion setup.
- `StateFilePath` (`string?`, default: `null`) — Path to the completion state file.
- `BashProfilePath` (`string?`, default: `null`) — Custom path for the Bash profile.
- `PowerShellProfilePath` (`string?`, default: `null`) — Custom path for the PowerShell profile.
- `ZshProfilePath` (`string?`, default: `null`) — Custom path for the Zsh profile.
- `FishProfilePath` (`string?`, default: `null`) — Custom path for the Fish profile.
- `NuProfilePath` (`string?`, default: `null`) — Custom path for the Nushell profile.

## ReplRunOptions

A record passed to `app.RunAsync(...)` to control runtime behavior. Separate from `ReplOptions`.

- `HostedServiceLifecycle` (`HostedServiceLifecycleMode`, default: `None`) — Hosted service lifecycle mode.
- `AnsiSupport` (`AnsiMode`, default: `Auto`) — ANSI support mode for this run.
- `TerminalOverrides` (`TerminalSessionOverrides?`, default: `null`) — Terminal session overrides.
