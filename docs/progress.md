# Progress

Progress in Repl is semantic feedback from a command handler to the active host. A handler reports that work is moving forward; the host decides whether that becomes a console line, terminal taskbar progress, MCP progress notifications, hosted-session feedback, or a captured side stream.

Use progress for user-facing execution feedback. Do not use it for logs, command results, or long-lived TUI rendering primitives.

## Quick Choice

| Need | Use |
|---|---|
| Simple percent complete | `IProgress<double>` |
| Label, current/total, state, or details | `IProgress<ReplProgressEvent>` |
| Direct async control from a handler | `IReplInteractionChannel` progress helpers |
| MCP-only progress/message control | `IMcpFeedback` |
| Spectre live/TUI progress bars | `IAnsiConsole.Progress(...)`, with Repl feedback captured away from the main surface |

## Simple Progress

Handlers can request `IProgress<double>`. The framework creates the adapter automatically.

```csharp
app.Map("sync", async (IProgress<double> progress, CancellationToken ct) =>
{
    for (var i = 1; i <= 10; i++)
    {
        progress.Report(i * 10.0);
        await Task.Delay(100, ct);
    }

    return "done";
});
```

`IProgress<double>` is connected to `IReplInteractionChannel.WriteProgressAsync(...)`. Reporting `42` is equivalent to sending a normal progress event with the configured default label and `Percent = 42`.

## Structured Progress

Use `IProgress<ReplProgressEvent>` when the progress update needs a label, computed percentage, state, unit, or details.

```csharp
app.Map("import", async (IProgress<ReplProgressEvent> progress, CancellationToken ct) =>
{
    var total = 100;
    for (var i = 1; i <= total; i++)
    {
        progress.Report(new ReplProgressEvent(
            "Importing contacts",
            Current: i,
            Total: total,
            Unit: "contacts"));

        await Task.Delay(25, ct);
    }

    return "done";
});
```

`ReplProgressEvent.ResolvePercent()` uses `Percent` when set. Otherwise, it computes a percentage from `Current` and `Total` when both are available.

## Channel Helpers

Use `IReplInteractionChannel` when the handler is already async and you want explicit control.

```csharp
app.Map("import", async (IReplInteractionChannel channel, CancellationToken ct) =>
{
    await channel.WriteProgressAsync("Preparing import", 10, ct);

    await channel.WriteIndeterminateProgressAsync(
        "Waiting for remote review",
        "The agent is still processing.",
        ct);

    await channel.WriteWarningProgressAsync(
        "Retrying duplicate check",
        percent: 55,
        details: "The remote worker timed out once.",
        ct);

    await channel.WriteErrorProgressAsync(
        "Import failed",
        percent: 80,
        details: "The final retry window was exhausted.",
        ct);

    await channel.ClearProgressAsync(ct);
});
```

Available states:

| State | Meaning |
|---|---|
| `Normal` | Regular progress update |
| `Warning` | Work is continuing, but the user should pay attention |
| `Error` | The current workflow has entered an error state |
| `Indeterminate` | Work is active but there is no meaningful percentage yet |
| `Clear` | Clear any visible progress indicator |

`percent: null` does not mean indeterminate. Use `WriteIndeterminateProgressAsync(...)` or `State = ReplProgressState.Indeterminate` explicitly.

## Rendering Pipeline

All portable progress APIs converge on the same semantic pipeline:

```text
IProgress<double>
IProgress<ReplProgressEvent>
IReplInteractionChannel progress helpers
        |
        v
WriteProgressRequest / ReplProgressEvent
        |
        v
Host presenter or transport
```

The built-in hosts render that semantic event differently:

| Host | Behavior |
|---|---|
| Console/default host | Text fallback, optional in-place rewriting, optional advanced terminal progress |
| Spectre presenter | Same semantic event, with explicit capture support for TUI/live surfaces |
| MCP | `notifications/progress`, plus warning/error message notifications for structured states |
| Hosted sessions | Session-aware output and terminal capabilities drive rendering |

The framework clears visible progress automatically when a command completes, fails, or is cancelled.

## Advanced Terminal Progress

The console presenter can emit `OSC 9;4` progress sequences in addition to normal text feedback. These sequences are useful for terminal taskbar progress or hosted terminal integrations.

`InteractionOptions.AdvancedProgressMode` controls this behavior:

| Value | Behavior |
|---|---|
| `Auto` | Emit advanced progress only for known-compatible terminals or sessions advertising progress support |
| `Always` | Emit advanced progress whenever the host can write terminal control sequences |
| `Never` | Disable advanced progress and keep text-only rendering |

Advanced progress is emitted only when the console presenter can safely write terminal control sequences. It is disabled for protocol passthrough and for terminal multiplexer sessions such as `tmux` and `screen` in automatic mode.

`OSC 9;4` is additional feedback. It does not replace the text progress line.

## Text Labels And TUI Surfaces

Console progress has a text fallback. That fallback includes a label, so it can break a full-screen TUI or a Spectre live display if both write to the same terminal surface.

When a command temporarily owns the screen, capture regular Repl interaction feedback away from the main TUI surface:

```csharp
app.Map("dashboard", static async (
    SpectreInteractionPresenter presenter,
    IReplIoContext io,
    CancellationToken ct) =>
{
    using var capture = presenter.BeginCapture(io.Error);
    await RunDashboardAsync(ct);
});
```

The `TextWriter` capture overload emits plain text only. It does not emit ANSI styling, line rewriting, or `OSC 9;4`.

For a custom TUI, another option is to capture to an `IReplInteractionPresenter` that ignores `ReplProgressEvent` or renders it inside an app-owned panel.

## Repl Progress Vs Spectre Progress

`IProgress<double>`, `IProgress<ReplProgressEvent>`, and `IReplInteractionChannel` are portable Repl feedback APIs. They work across console, hosted sessions, and MCP.

`Spectre.Console.Progress` is a Spectre rendering primitive. Use it when your command owns a Spectre surface and you want Spectre's progress bar UI. It is not automatically connected to Repl progress events.

If you use Spectre live displays or progress bars, avoid sending normal Repl progress to the same writer unless you capture or redirect it.

## Configuration

Progress display is configured through `ReplOptions.Interaction`:

```csharp
app.Options(o =>
{
    o.Interaction.DefaultProgressLabel = "Sync";
    o.Interaction.ProgressTemplate = "[{label}] {percent:0.0}%";
    o.Interaction.AdvancedProgressMode = AdvancedProgressMode.Auto;
});
```

`ProgressTemplate` supports:

| Placeholder | Example |
|---|---|
| `{label}` | `Sync` |
| `{percent}` | `12.5` |
| `{percent:0}` | `13` |
| `{percent:0.0}` | `12.5` |

## MCP

Portable progress should normally use `IReplInteractionChannel`. In MCP mode, Repl maps those calls to MCP feedback:

| Repl API | MCP behavior |
|---|---|
| `WriteProgressAsync("Label", 40)` | `notifications/progress` with `progress = 40`, `total = 100` |
| `WriteIndeterminateProgressAsync(...)` | `notifications/progress` with a message and no `total` |
| `WriteWarningProgressAsync(...)` | `notifications/progress` plus a warning-level message notification |
| `WriteErrorProgressAsync(...)` | `notifications/progress` plus an error-level message notification |

Use `IMcpFeedback` only when the command is intentionally MCP-specific and needs direct control over MCP notifications.

## Rules Of Thumb

- Return command results as handler return values, not as progress.
- Use progress for transient execution feedback the current user should see.
- Use `ILogger` for operator diagnostics and centralized logs.
- Use `IProgress<double>` for simple percent updates.
- Use structured progress or channel helpers for warning/error/indeterminate states.
- In TUI or Spectre live surfaces, capture or redirect Repl progress so text labels do not fight the app-owned screen.
