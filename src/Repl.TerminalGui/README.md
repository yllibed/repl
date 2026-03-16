# Repl.TerminalGui

Terminal.Gui TUI hosting for Repl Toolkit. Host your entire REPL inside a native terminal UI with composable views, modal dialogs, ANSI output rendering, and multi-session support.

## Features

- **Composable views** — `ReplOutputView` and `ReplInputView` are standard Terminal.Gui views you place anywhere in your layout
- **ANSI terminal emulation** — output view interprets ANSI escape sequences via XTerm.NET (colors, styles, cursor movement)
- **Modal dialog prompts** — `AskTextAsync`, `AskChoiceAsync`, `AskConfirmationAsync`, `AskSecretAsync`, `AskMultiChoiceAsync` render as native Terminal.Gui dialogs
- **Spectre.Console compatible** — Spectre's output transformer writes ANSI to the output view; only prompts use Terminal.Gui dialogs
- **Multi-session support** — run multiple REPL sessions side by side in the same Terminal.Gui application
- **Command history** — Up/Down arrow navigation in the input field

## Setup

```csharp
var app = ReplApp.Create(services =>
{
    services.AddTerminalGui();  // DI: TerminalGuiInteractionHandler
});

app.Map("hello", () => "world");

using var guiApp = Application.Create().Init();

var output = new ReplOutputView() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(1) };
var input = new ReplInputView() { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };

var window = new Window() { Title = "My App" };
window.Add(output, input);

var session = new ReplSession(app, output, input);
return await session.RunAsync(window);
```

## With Spectre.Console

Terminal.Gui hosts the session while Spectre renders rich output (tables, progress, figlet):

```csharp
var app = ReplApp.Create(services =>
{
    services.AddTerminalGui();       // Terminal.Gui dialogs for prompts
    services.AddSpectreConsole();    // Spectre for rich output
})
.UseSpectreConsole();  // Spectre tables, formatting in the TUI output view
```

Spectre's prompt handler automatically steps aside (hosted session), so Terminal.Gui handles all interactive prompts.

## Multi-session

```csharp
var left = new ReplOutputView() { Width = Dim.Percent(50), Height = Dim.Fill(1) };
var right = new ReplOutputView() { X = Pos.Percent(50), Width = Dim.Fill(), Height = Dim.Fill(1) };
// ... add input views, wire sessions
var session1 = new ReplSession(app, left, leftInput);
var session2 = new ReplSession(app, right, rightInput);
```

Each session has isolated state via `AsyncLocal` — independent context navigation, history, and DI scope.

## Sample

See [**sample 08-terminal-gui**](../../samples/08-terminal-gui/) for a working demo.
