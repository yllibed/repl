# Output System

The output system controls how command results are serialized and rendered to the user. It supports multiple built-in formats, custom transformers, ANSI detection, and banner rendering.

Large result flow and paging are documented separately in [Result Flow And Paging](result-flow.md).

## Format Selection Precedence

The active output format is resolved in this order:

1. Explicit `--output:format` flag on the command line.
2. `ReplOptions.Output.DefaultFormat` configured at startup.
3. `"human"` (the built-in default).

## Built-in Formats

| Format     | Description                          |
|------------|--------------------------------------|
| `human`    | Plain text, intended for terminals.  |
| `spectre`  | Lightweight Spectre.Console rendering for terminals. |
| `json`     | JSON serialization.                  |
| `xml`      | XML serialization.                   |
| `yaml`     | YAML serialization.                  |
| `markdown` | Markdown table/document rendering.   |

### JSON results

A handler can return `System.Text.Json.Nodes.JsonNode` (`JsonObject`, `JsonArray`, `JsonValue`) or a
`JsonElement`, including as rows of an `IReplPageSource<T>`. `human` and `spectre` then render the JSON
data rather than the CLR members of those types:

- An object becomes one `key: value` line per field.
- In `spectre` as in `human`, a table's header labels stay on one line: a label wider than its column is
  truncated rather than wrapped, and a line break in a label reads as a space.
- Rows that are all objects with at least one key become a table. Its columns are the union of the rows'
  keys in first-seen order, matched ordinally, and a key missing from a row leaves that cell empty. The rows'
  keys, not a type, define the columns, so each page the pager fetches can name other columns than the page
  before it. See [Result Flow And Paging](result-flow.md) for how the pager shows their headers.
- An array of scalars becomes one value per line. So does a page of rows among which one is a JSON null or
  an empty object: each row reads as its literal, since a blank table row would read as no row at all.
- A page declared with a JSON item type, such as `IReplPageSource<JsonNode?>`, renders as JSON even when
  every item on it is a JSON null.
- Values are compact JSON literals. So a string shows as `"x"`, an explicit JSON null shows as `null`, and
  a nested object or array shows as itself. A `JsonElement` object that repeats a property name shows the
  last value given for it, as JavaScript reads it.
- A JSON value held by a property of an ordinary result object shows as a compact literal too, and a
  property declared as JSON (`JsonNode?`, `JsonElement?`) that holds `null` reads `null`, unless its
  `DisplayFormat` sets a `NullDisplayText`. A JSON value passed as a result's details renders like a JSON
  result.

In JSON strings and keys, control characters and Unicode format characters (such as bidirectional
overrides) are escaped. So a payload can neither drive the terminal nor make it display something other
than the data. Non-ASCII text stays readable. This covers JSON values only: a plain CLR `string` in a
result is still written as-is. The `json` format and MCP output are unchanged, and `markdown` does not
special-case JSON yet.

### Format aliases

The built-in aliases are:

- `--human` -> `human`
- `--json` -> `json`
- `--xml` -> `xml`
- `--yaml` / `--yml` -> `yaml`
- `--markdown` -> `markdown`

When `Repl.Spectre` is enabled, it also registers:

- `--spectre` -> `spectre`

Additional aliases can be registered.

## Custom Transformers

Implement `IOutputTransformer` and register it to add a new format:

```csharp
app.Configure<OutputOptions>(o =>
{
    o.AddTransformer("csv", new CsvTransformer());
});
```

The transformer receives the command result object and writes formatted output to the provided stream.

### Spectre views for application types

Keep handlers returning data and register their console presentation with `Repl.Spectre`:

```csharp
app.UseSpectreConsole(spectre =>
{
    spectre.AddView<IndexStatus>(status =>
        new Panel(new Text(status.Ready ? "Ready" : "Waiting")));
});
```

A view is a `Func<T, IRenderable>` using `Spectre.Console.Rendering.IRenderable`.
It applies to the exact runtime type of a non-null command result, before the default
object or collection rendering. Base-class and interface registrations do not match derived
runtime types. Registering a type again replaces its view; configure views before running the app.

Repl renders the returned view using the session's width, ANSI, color and Unicode settings,
including terminal fallbacks. The view must return a renderable and must not write to a console.
Help, result envelopes and pages retain their built-in rendering. Views do not apply to individual
page items or nested object properties, and Repl does not infer repeated table headers from a custom view.
JSON and MCP still receive the original result data.

See the [documentation index sample](../samples/10-docs-mcp/README.md) for status,
search comparison and document views.

### Custom aliases

Map an alias to any registered format name:

```csharp
app.Configure<OutputOptions>(o =>
{
    o.AddAlias("spreadsheet", "csv");
});
```

## ANSI Detection

Two questions are answered separately: may the output be **coloured**, and may it **drive the terminal**
(clear the screen, move the cursor for arrow-key menus, the full-screen pager or Spectre's live
displays, write shell-integration marks or advanced progress). This section is the reference for both;
other pages link here.

### Colour

Checked in this order; the first row that decides wins:

| # | Check | Result |
|---|---|---|
| 1 | A hosted session states its ANSI support (`ReplSessionIO.AnsiSupport`) | that answer |
| 2 | `OutputOptions.AnsiMode` is `Always` or `Never` | on or off |
| 3 | The host reports no ANSI support (`CapabilityOptions.SupportsAnsi = false`) | off |
| 4 | `FORCE_COLOR` has any non-empty value, including `0` (the rule `System.Console` applies on .NET 11) | on |
| 5 | `NO_COLOR` has a non-blank value ([no-color.org](https://no-color.org)) | off |
| 6 | `CLICOLOR_FORCE=1` | on |
| 7 | `TERM=dumb` | off |
| 8 | The stream is redirected to a file or a pipe | off, otherwise on |

### Terminal control

Colour must be on, and in addition one of these must hold:

- a hosted session stated its ANSI support;
- the application set `AnsiMode.Always`;
- the stream reaches a terminal, and neither `NO_COLOR` nor `TERM=dumb` (without `CLICOLOR_FORCE=1`) is set.

Independently of the colour table, a hosted client that advertises ANSI through capability flags may be
driven too, unless `AnsiMode.Never`, `NO_COLOR` or `TERM=dumb` says otherwise; see
[Hosted clients and Spectre](#hosted-clients-and-spectre).

So `FORCE_COLOR` or `CLICOLOR_FORCE=1` colours a file or a pipe without writing control sequences into it,
and `NO_COLOR` keeps them off even where `FORCE_COLOR` turned colour on. `ITerminalInfo.IsAnsiSupported`
reports this answer, and `ITerminalInfo.Palette` the colour one. Some features add their own conditions:
screen clearing and progress need the stream they write to reach a terminal (a session's writer always
does), and shell-integration marks are never written to redirected local output, even under `AnsiMode.Always`.

> **Changed in 0.12.1.** `FORCE_COLOR` is new, and it outranks `NO_COLOR` for colour: with both set, output
> used to be plain and is now coloured. Terminal control is now decided apart from colour:
>
> - `CLICOLOR_FORCE=1` on redirected output cleared the screen and enabled arrow-key menus there; it no longer does.
> - Under `AnsiMode.Always`, a redirected local output was cleared; the clear now needs a terminal, as progress did.
> - A hosted client that advertises ANSI only through capability flags got no screen clear and the line-by-line
>   pager; it now gets the clear and the full-screen pager, as it already got shell-integration marks.
> - A Spectre console coloured where the terminal may not be driven is no longer interactive: Spectre prompts
>   give way to Repl's text prompts, and a prompt on an injected `IAnsiConsole` fails there; ask through
>   `IReplInteractionChannel` to keep working ([Hosted clients and Spectre](#hosted-clients-and-spectre)).
>
> `ITerminalInfo.IsAnsiSupported` follows the same answer; see [`ITerminalInfo`](interaction.md#iterminalinfo).
> Upgrade Repl.Spectre together with Repl.Core: Repl.Spectre 0.12.0 takes forced colour from the new
> Repl.Core but not the rule that keeps its live displays out of a redirected output.

### Hosted clients and Spectre

A hosted client that advertises ANSI only through capability flags (terminal identity, control messages)
gets terminal control, Spectre styling and an `ITerminalInfo` palette even when the server console's own
state says no; `AnsiMode.Never`, `NO_COLOR` and `TERM=dumb` still win over that fallback. Spectre rendering
(the `spectre` format and injected `IAnsiConsole` instances) is otherwise styled by the colour answer:
colours degrade to plain text when it is off. Unicode box drawing falls back to safe borders when the
output sink's encoding cannot carry the glyphs.

Spectre animates `Progress` and `Status` only when its console is interactive and ANSI. A console that is
coloured but may not drive the terminal (colour forced onto redirected output, or onto a terminal under
`NO_COLOR` or `TERM=dumb`) is therefore not interactive: live displays print static lines, a prompt on an
injected `IAnsiConsole` fails as it does in any non-interactive console, and Repl's own text prompts answer
`IReplInteractionChannel` requests instead of Spectre's. An uncoloured console stays interactive: prompts on
an injected `IAnsiConsole` keep working under `app | tee log`, and `NO_COLOR` on a terminal keeps Spectre's
prompts.

See [Terminal Shell Integration](terminal-shell-integration.md) for the mark gates.

## Banner Rendering

The startup banner is controlled by:

- **`OutputOptions.BannerEnabled`** — master toggle (default `true`).
- **`BannerFormats`** — set of format names that allow the banner (typically `human` only).
- **`--no-logo` flag** — suppresses the banner for the current invocation.

The banner is only rendered when the active format is in the `BannerFormats` set and `BannerEnabled` is `true`.

## Help output

`--help` uses the active output format:

1. `human` renders the classic text help.
2. `spectre` renders dedicated Spectre help.
3. Structured formats (`json`, `xml`, `yaml`, `markdown`) use the machine-readable help pipeline.

## Render Width

The output width used for wrapping and table layout is resolved as:

1. `OutputOptions.PreferredWidth` if set explicitly.
2. Detected terminal width.
3. `OutputOptions.FallbackWidth` (default `120`).

## JSON Colorization

In interactive mode, when ANSI is supported, JSON output is syntax-highlighted automatically. This applies only to the `json` format, and follows the colour answer ([ANSI Detection](#ansi-detection)): redirected output stays plain unless colour is forced.

## Interaction Feedback And Machine Formats

A transformer declares whether its output is meant for a program through `IOutputTransformer.IsMachineReadable`
(`false` by default; `true` for `json`, `xml` and `yaml`). While a command renders such a format, the
console presenter writes its interaction feedback (progress, status, notices, warnings, problems and
prompt labels) to the error stream, so the payload on standard output stays parseable. A redirected
standard output has the same effect in a local console run.
`OutputOptions.FeedbackStream` overrides both rules; see [Progress](progress.md#which-stream-receives-progress).

## Paging

Human terminal formats (`human` and `spectre`) can use the integrated result pager when rendered output exceeds the visible row capacity or a result-flow page source has more data. The pager is never used for redirected stdout, protocol passthrough, MCP/programmatic execution, or machine formats.

Paged handler results should return `ReplPage<T>` through `IReplPagingContext`. JSON serializes these as `{ items, pageInfo }`; human and Spectre formats render the current page plus continuation metadata.

## See Also

- [Configuration Reference](configuration-reference.md) — `OutputOptions` properties.
- [Execution Pipeline](execution-pipeline.md) — output formatting occurs at stage 11.
- [Result Flow And Paging](result-flow.md) - paging contracts, CLI flags, and MCP behavior.
