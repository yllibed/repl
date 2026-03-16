# 07 — Spectre
**Rich Spectre.Console integration: renderables, visualizations, and interactive prompts**

This sample showcases the `Repl.Spectre` package with **21 Spectre.Console features**
across **14 commands**. It demonstrates both direct `IAnsiConsole` usage for custom
renderables and the transparent prompt upgrade where `IReplInteractionChannel` calls
are automatically rendered as Spectre prompts.

---

## Quick start

```bash
dotnet run --project samples/07-spectre/SpectreOpsSample.csproj
```

---

## Commands

### `tour` — Guided walkthrough (the star command)

A multi-step flow chaining 10 Spectre features sequentially:

1. **FigletText** — ASCII art welcome banner
2. **TextPrompt** — ask user's name (via `AskTextAsync`)
3. **Panel** — greeting panel with the name
4. **SelectionPrompt** — choose a data category (via `AskChoiceAsync`)
5. **Table** — display contacts for the chosen category
6. **BarChart** — domain distribution visualization
7. **Tree** — hierarchical contact breakdown
8. **ConfirmationPrompt** — "Continue to summary?" (via `AskConfirmationAsync`)
9. **Calendar** — current month with today highlighted
10. **Rule** + **Panel** — divider and summary

### `list` — Auto-rendered Spectre table

Returns a collection; the `"spectre"` output transformer renders it as a bordered
table automatically. Zero rendering code in the handler.

### `detail {name}` — Panel + Grid

Uses `IAnsiConsole` to render a `Panel` containing a `Grid` of contact details.

### `chart` — BarChart + BreakdownChart

Renders a `BarChart` of contact counts per domain and a `BreakdownChart`
of email provider distribution.

### `tree` — Tree view

Renders a `Tree` of contacts grouped by email domain.

### `json {name}` — JsonText

Renders syntax-highlighted JSON for a contact using `Spectre.Console.Json.JsonText`.

### `path` — TextPath + Columns

Renders file paths with `TextPath` (syntax-highlighted path components) in a `Columns` layout.

### `calendar` — Calendar

Renders a `Calendar` with today and event dates highlighted.

### `figlet {text}` — FigletText

Renders large ASCII art from user input.

### `status` — Status spinner

Uses `IAnsiConsole` with `Status().StartAsync()` to show a spinner progressing
through named stages with changing colors.

### `progress` — Progress bars

Uses `IAnsiConsole` with `Progress().StartAsync()` to show multi-task progress
bars with description, percentage, and spinner columns.

### `add` — Interactive prompts

Uses `IReplInteractionChannel` for `AskTextAsync` + `AskChoiceAsync`. With
Spectre registered, these are automatically rendered as rich Spectre prompts —
no Spectre-specific code in the handler.

### `configure` — MultiSelectionPrompt

Uses `AskMultiChoiceAsync` which renders as a Spectre `MultiSelectionPrompt`
with checkbox-style selection.

### `login` — Secret input

Uses `AskSecretAsync` which renders as a Spectre `TextPrompt` with masked input.

---

## Spectre features coverage

| Feature | Class | Used in |
|---------|-------|---------|
| FigletText | `FigletText` | `tour`, `figlet`, banner |
| Table | `Table` | `tour` |
| Table (auto) | via output transformer | `list` |
| Tree | `Tree` | `tour`, `tree` |
| Panel | `Panel` | `tour`, `detail`, `json`, `calendar`, `chart` |
| Rule | `Rule` | `tour` |
| BarChart | `BarChart` | `tour`, `chart` |
| BreakdownChart | `BreakdownChart` | `chart` |
| Calendar | `Calendar` | `tour`, `calendar` |
| JsonText | `JsonText` | `json` |
| TextPath | `TextPath` | `path` |
| Columns | `Columns` | `path` |
| Grid | `Grid` | `detail` |
| Markup | `Markup` | throughout |
| Status | `Status` | `status` |
| Progress | `Progress` | `progress` |
| SelectionPrompt | via `AskChoiceAsync` | `tour`, `add` |
| MultiSelectionPrompt | via `AskMultiChoiceAsync` | `configure` |
| ConfirmationPrompt | via `AskConfirmationAsync` | `tour` |
| TextPrompt | via `AskTextAsync` | `tour`, `add` |
| TextPrompt.Secret | via `AskSecretAsync` | `login` |

---

## Two integration paths

### 1. Direct `IAnsiConsole` injection

Inject `IAnsiConsole` into any command handler to use any Spectre renderable:

```csharp
app.Map("chart", (IAnsiConsole console, IContactStore store) =>
{
    var chart = new BarChart().Label("Contacts per Domain");
    // ... add items ...
    console.Write(chart);
});
```

### 2. Transparent prompt upgrade

Call `IReplInteractionChannel` methods as usual — with Spectre registered,
they render as rich Spectre prompts automatically:

```csharp
app.Map("add", async (IReplInteractionChannel channel) =>
{
    var name = await channel.AskTextAsync("name", "Contact name?");
    var dept = await channel.AskChoiceAsync("dept", "Department?",
        ["Engineering", "Sales", "Marketing"]);
});
```

No Spectre-specific code in the handler — the same handler works with or without Spectre.

---

## Banner with `IAnsiConsole`

The sample uses `IAnsiConsole` directly in the banner callback to render
a FigletText header at startup:

```csharp
.WithBanner((IAnsiConsole console) =>
{
    console.Write(new FigletText("Spectre").Color(Color.Blue));
    console.MarkupLine("  [grey]Commands:[/] tour, list, detail, ...");
})
```

---

## Configuration

`UseSpectreConsole()` accepts an optional configuration callback:

```csharp
// Default: Unicode enabled, UTF-8 output encoding
.UseSpectreConsole()

// Disable Unicode for terminals that don't support it
.UseSpectreConsole(o => o.Unicode = false)
```

When `Unicode` is `true` (the default), `UseSpectreConsole` sets
`Console.OutputEncoding` to UTF-8 so that box-drawing characters,
progress bars, and spinners render correctly on Windows.

---

## What's next?

You now have the full progressive path:

- Routing and parsing (01)
- Scopes and DI (02)
- Composable modules (03)
- Interaction channel (04)
- Remote hosting (05)
- Testing toolkit (06)
- **Spectre.Console integration (07)**

See also: [`Repl.Spectre` package README](../../src/Repl.Spectre/README.md)
for the integration API reference.
