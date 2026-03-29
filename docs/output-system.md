# Output System

The output system controls how command results are serialized and rendered to the user. It supports multiple built-in formats, custom transformers, ANSI detection, and banner rendering.

## Format Selection Precedence

The active output format is resolved in this order:

1. Explicit `--output:format` flag on the command line.
2. `ReplOptions.Output.DefaultFormat` configured at startup.
3. `"human"` (the built-in default).

## Built-in Formats

| Format     | Description                          |
|------------|--------------------------------------|
| `human`    | Plain text, intended for terminals.  |
| `json`     | JSON serialization.                  |
| `xml`      | XML serialization.                   |
| `yaml`     | YAML serialization.                  |
| `markdown` | Markdown table/document rendering.   |

### Format aliases

The alias `yml` resolves to `yaml`. Additional aliases can be registered.

## Custom Transformers

Implement `IOutputTransformer` and register it to add a new format:

```csharp
app.Configure<OutputOptions>(o =>
{
    o.AddTransformer("csv", new CsvTransformer());
});
```

The transformer receives the command result object and writes formatted output to the provided stream.

### Custom aliases

Map an alias to any registered format name:

```csharp
app.Configure<OutputOptions>(o =>
{
    o.AddAlias("spreadsheet", "csv");
});
```

## ANSI Detection

ANSI color and styling support is resolved through a chain of checks:

1. **Session override** — a hosted session can force ANSI on or off.
2. **Explicit `AnsiMode`** — set via `OutputOptions.AnsiMode`.
3. **Environment variables** — `NO_COLOR` (disables), `CLICOLOR_FORCE` (enables), `TERM` (checked for `dumb`).
4. **Redirection check** — if stdout is redirected to a file or pipe, ANSI is disabled.

The first check that produces a definitive answer wins.

## Banner Rendering

The startup banner is controlled by:

- **`OutputOptions.BannerEnabled`** — master toggle (default `true`).
- **`BannerFormats`** — set of format names that allow the banner (typically `human` only).
- **`--no-logo` flag** — suppresses the banner for the current invocation.

The banner is only rendered when the active format is in the `BannerFormats` set and `BannerEnabled` is `true`.

## Render Width

The output width used for wrapping and table layout is resolved as:

1. `OutputOptions.PreferredWidth` if set explicitly.
2. Detected terminal width.
3. `OutputOptions.FallbackWidth` (default `120`).

## JSON Colorization

In interactive mode, when ANSI is supported, JSON output is syntax-highlighted automatically. This applies only to the `json` format rendered to a terminal — redirected or non-ANSI output remains plain.

## See Also

- [Configuration Reference](configuration-reference.md) — `OutputOptions` properties.
- [Execution Pipeline](execution-pipeline.md) — output formatting occurs at stage 11.
