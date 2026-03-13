# Repl.Spectre

Spectre.Console integration for Repl Toolkit. Provides rich interactive prompts, injectable `IAnsiConsole`, and beautiful table rendering.

## Features

- **Rich prompts** — `SelectionPrompt`, `MultiSelectionPrompt`, `ConfirmationPrompt`, `TextPrompt`, and secret input via Spectre.Console
- **IAnsiConsole injection** — use `IAnsiConsole` as a command parameter to render tables, trees, panels, and other Spectre renderables
- **Table output** — the "spectre" output format renders collections as bordered Spectre tables

## Usage

```csharp
var app = ReplApp.Create(services =>
{
    services.AddSpectreConsole();
});
app.UseSpectreConsole();

app.Map("list", () => new[]
{
    new { Name = "Alpha", Status = "Active" },
    new { Name = "Beta",  Status = "Paused" },
});

app.Map("report", (IAnsiConsole console) =>
{
    var table = new Table();
    table.AddColumn("Name");
    table.AddColumn("Value");
    table.AddRow("Item", "42");
    console.Write(table);
});
```
