# Repl.Spectre

Spectre.Console integration for Repl Toolkit. Provides rich interactive prompts, injectable `IAnsiConsole`, a lightweight Spectre output format, and an interaction presenter that can capture feedback during screen-owned flows.

## Features

- **Rich prompts** — `SelectionPrompt`, `MultiSelectionPrompt`, `ConfirmationPrompt`, `TextPrompt`, and secret input via Spectre.Console
- **IAnsiConsole injection** — use `IAnsiConsole` as a command parameter to render tables, trees, panels, and other Spectre renderables
- **Lightweight output** — the `"spectre"` output format renders objects, results, help, and collections with less chrome than the default Spectre widgets
- **Banner support** — inject `IAnsiConsole` into `WithBanner()` callbacks for rich startup banners (FigletText, Markup, etc.)
- **Capture support** — `SpectreInteractionPresenter.BeginCapture(...)` redirects REPL feedback away from a screen-owned Spectre surface
- **Configurable capabilities** — `SpectreConsoleOptions` to control Unicode rendering for different terminal environments

## Setup

```csharp
var app = ReplApp.Create(services =>
{
    services.AddSpectreConsole(); // DI: IAnsiConsole + SpectreInteractionHandler + SpectreInteractionPresenter
})
.UseSpectreConsole(); // Output transformer + banner format + UTF-8 encoding
```

Two calls, two concerns:

| Method | Scope | What it does |
|--------|-------|-------------|
| `AddSpectreConsole()` | `IServiceCollection` | Registers `IAnsiConsole`, `SpectreInteractionHandler`, and `SpectreInteractionPresenter` in DI |
| `UseSpectreConsole()` | `ReplApp` | Registers `"spectre"` output transformer, sets it as default, adds `--spectre`, enables banners, configures UTF-8 |

## Usage

### Auto-rendered tables

Return a collection from a command — the output transformer renders it as a lightweight Spectre table:

```csharp
app.Map("list", (IContactStore store) => store.All());
```

Column headers are derived from `[Display]` attributes. No rendering code needed.

### Direct `IAnsiConsole` injection

Inject `IAnsiConsole` to use any Spectre renderable:

```csharp
app.Map("report", (IAnsiConsole console) =>
{
    var table = new Table().AddColumn("Name").AddColumn("Value");
    table.AddRow("Item", "42");
    console.Write(table);
});
```

Works with all Spectre renderables: `Table`, `Tree`, `Panel`, `BarChart`, `Calendar`, `FigletText`, `Progress`, `Status`, and more.

### Format switching

`UseSpectreConsole()` makes `spectre` the default output format. You can still switch per-command:

- `--spectre` selects the Spectre renderer
- `--human` switches back to the standard text renderer
- `--output:<format>` remains the canonical selector

`--help` respects the selected format as well, so `--spectre --help` uses Spectre help while `--human --help` returns the classic text help.

### Transparent prompt upgrade

`IReplInteractionChannel` calls are automatically rendered as Spectre prompts:

| Channel method | Spectre prompt |
|----------------|---------------|
| `AskTextAsync` | `TextPrompt<string>` |
| `AskChoiceAsync` | `SelectionPrompt<string>` |
| `AskMultiChoiceAsync` | `MultiSelectionPrompt<string>` |
| `AskConfirmationAsync` | `ConfirmationPrompt` |
| `AskSecretAsync` | `TextPrompt<string>.Secret()` |

No Spectre-specific code in handlers — the same handler works with or without the Spectre package.

### Capture feedback during screen-owned flows

If your command temporarily owns the terminal surface, do not mix that full-screen/live Spectre rendering with regular REPL status/progress output on the same writer. Instead, capture interaction feedback explicitly:

```csharp
app.Map("dashboard", static async (
    SpectreInteractionPresenter presenter,
    CancellationToken ct) =>
{
    using var capture = presenter.BeginCapture(Console.Error);
    await RunDashboardAsync(ct);
});
```

The `TextWriter` overload emits plain text only. Use it when a future TUI or live display manages the main screen and REPL feedback should go elsewhere.

You can also capture to a custom presenter:

```csharp
using var capture = presenter.BeginCapture(myPresenter);
```

This is the intended integration point for future TUI tooling.

### Banner with `IAnsiConsole`

Use `IAnsiConsole` in banner callbacks for rich startup output:

```csharp
app.WithBanner((IAnsiConsole console) =>
{
    console.Write(new FigletText("My App").Color(Color.Blue));
    console.MarkupLine("[grey]Type 'help' to get started[/]");
});
```

## Configuration

`UseSpectreConsole()` accepts an optional callback to configure capabilities:

```csharp
// Default: Unicode enabled
.UseSpectreConsole()

// Disable Unicode for limited terminals
.UseSpectreConsole(o => o.Unicode = false)
```

### `SpectreConsoleOptions`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Unicode` | `bool` | `true` | Enable Unicode box-drawing characters and symbols. When `false`, Spectre falls back to ASCII. |

When `Unicode` is enabled, `UseSpectreConsole()` sets `Console.OutputEncoding` to UTF-8 to ensure
Unicode characters (progress bars, spinners, box-drawing) render correctly on Windows.

## Sample

See [**sample 07-spectre**](../../samples/07-spectre/) for a comprehensive demo covering
21 Spectre features across 14 commands.
