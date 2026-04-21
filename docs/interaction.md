# Interaction Channel

The interaction channel is a bidirectional contract between command handlers and the host.
Handlers emit **semantic requests** (prompts, status, progress); the host decides **how to render** them.

Use `Interaction` for **user-facing feedback**. Keep `ILogger` for operator diagnostics and centralized logging.

See also: [sample 04-interactive-ops](../samples/04-interactive-ops/) for a working demo.

## Core primitives

These methods are defined on `IReplInteractionChannel` and implemented by every host (console, WebSocket, test harness).

### `AskTextAsync`

Free-form text input with optional default.

```csharp
var name = await channel.AskTextAsync("name", "Contact name?");
var name = await channel.AskTextAsync("name", "Name?", defaultValue: "Alice");
```

### `AskChoiceAsync`

N-way choice prompt with default index and prefix matching.

```csharp
var index = await channel.AskChoiceAsync(
    "action", "How to handle duplicates?",
    ["Skip", "Overwrite", "Cancel"],
    defaultIndex: 0,
    new AskOptions(Timeout: TimeSpan.FromSeconds(10)));
```

### `AskConfirmationAsync`

Yes/no confirmation with a safe default.

```csharp
var confirmed = await channel.AskConfirmationAsync(
    "confirm", "Delete all contacts?", defaultValue: false);
```

### `AskSecretAsync`

Masked input for passwords and tokens. Characters are echoed as the mask character (default `*`), or hidden entirely with `Mask: null`.

```csharp
var password = await channel.AskSecretAsync("password", "Password?");
var token = await channel.AskSecretAsync("token", "API Token?",
    new AskSecretOptions(Mask: null, AllowEmpty: true));
```

### `AskMultiChoiceAsync`

Multi-selection prompt. Users enter comma-separated indices (1-based) or names.

```csharp
var selected = await channel.AskMultiChoiceAsync(
    "features", "Enable features:",
    ["Auth", "Logging", "Caching", "Metrics"],
    defaultIndices: [0, 1],
    new AskMultiChoiceOptions(MinSelections: 1, MaxSelections: 3));
```

### `ClearScreenAsync`

Clears the terminal screen.

```csharp
await channel.ClearScreenAsync(cancellationToken);
```

### `WriteStatusAsync`

Neutral inline feedback (validation errors, transient status).

```csharp
await channel.WriteStatusAsync("Import started", cancellationToken);
```

### User-facing feedback helpers

These extension methods live in `Repl.Interaction` and are intended for messages the current user should actually see.

```csharp
await channel.WriteNoticeAsync("Connection established", cancellationToken);
await channel.WriteWarningAsync("Token expires soon", cancellationToken);
await channel.WriteProblemAsync(
    "Sync failed",
    details: "Check connectivity and retry.",
    code: "sync_failed",
    cancellationToken: cancellationToken);
```

---

## Extension methods

These compose on top of the core primitives and are available via `using Repl.Interaction;`.

### `AskEnumAsync<TEnum>`

Single choice from an enum type. Uses `[Description]` or `[Display(Name)]` attributes when present, otherwise humanizes PascalCase names.

```csharp
var theme = await channel.AskEnumAsync<AppTheme>("theme", "Choose a theme:", AppTheme.System);
```

### `AskFlagsEnumAsync<TEnum>`

Multi-selection from a `[Flags]` enum. Selected values are combined with bitwise OR.

```csharp
var perms = await channel.AskFlagsEnumAsync<ContactPermissions>(
    "permissions", "Select permissions:",
    ContactPermissions.Read | ContactPermissions.Write);
```

### `AskNumberAsync<T>`

Typed numeric input with optional min/max bounds. Re-prompts until a valid value is entered.

```csharp
var limit = await channel.AskNumberAsync<int>(
    "limit", "Max contacts?",
    defaultValue: 100,
    new AskNumberOptions<int>(Min: 1, Max: 10000));
```

### `AskValidatedTextAsync`

Text input with a validation predicate. Re-prompts until the validator returns `null` (valid).

```csharp
var email = await channel.AskValidatedTextAsync(
    "email", "Email?",
    input => MailAddress.TryCreate(input, out _) ? null : "Invalid email.");
```

### `PressAnyKeyAsync`

Pauses execution until the user presses a key.

```csharp
await channel.PressAnyKeyAsync("Press any key to continue...", cancellationToken);
```

---

## Progress reporting

Handlers inject `IProgress<T>` to report progress. The framework creates the appropriate adapter automatically.

### Simple percentage: `IProgress<double>`

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

### Structured progress: `IProgress<ReplProgressEvent>`

```csharp
app.Map("import", async (IProgress<ReplProgressEvent> progress, CancellationToken ct) =>
{
    for (var i = 1; i <= total; i++)
    {
        progress.Report(new ReplProgressEvent("Importing", Current: i, Total: total));
    }
    return "done";
});
```

### Progress states and helpers

When you need richer user feedback, use the `IReplInteractionChannel` progress helpers instead of treating progress like logs.

```csharp
await channel.WriteProgressAsync("Preparing import", 10, cancellationToken);
await channel.WriteIndeterminateProgressAsync(
    "Waiting for agent review",
    "Sampling is still running.",
    cancellationToken);
await channel.WriteWarningProgressAsync(
    "Retrying duplicate check",
    55,
    "The remote worker timed out once.",
    cancellationToken);
await channel.WriteErrorProgressAsync(
    "Import failed",
    80,
    "The final retry window was exhausted.",
    cancellationToken);
await channel.ClearProgressAsync(cancellationToken);
```

`ReplProgressEvent` now carries a `State` value:

| State | Meaning |
|---|---|
| `Normal` | Regular progress update |
| `Warning` | Work is continuing, but the user should pay attention |
| `Error` | The current workflow has entered an error state |
| `Indeterminate` | Work is active but there is no meaningful percentage yet |
| `Clear` | Clear any visible progress indicator |

Notes:

- `WriteProgressAsync(string, double?)` remains the simple, backward-compatible API.
- `percent: null` does **not** imply indeterminate mode. Use `WriteIndeterminateProgressAsync(...)` or `State = Indeterminate` explicitly.
- Hosts can render these states differently. The built-in console presenter keeps the text fallback and, when enabled, also emits advanced terminal progress sequences.
- The framework clears visible progress automatically when a command completes, fails, or is cancelled.

### Advanced terminal progress

`InteractionOptions.AdvancedProgressMode` controls whether hosts should emit advanced progress sequences in addition to the normal text feedback:

| Value | Behavior |
|---|---|
| `Auto` | Emit advanced progress only for a conservative allowlist of known-compatible terminals; stay text-only for multiplexers such as `tmux`/`screen` and for unknown terminals |
| `Always` | Always emit advanced progress when the host can write terminal control sequences |
| `Never` | Disable advanced progress and keep the text-only fallback |

The built-in console presenter maps progress states to `OSC 9;4` when advanced terminal progress is enabled. This is intended for user-facing execution feedback such as taskbar progress bars or mirrored hosted-session UI, not for application logging.

In practice, `Always` is usually safe on modern terminals because unknown `OSC` sequences are typically ignored silently. The main caveat is very old or non-conformant terminals, which may render unsupported control sequences literally instead of ignoring them.

---

## Prefill with `--answer:*`

Every prompt method supports deterministic prefill for non-interactive automation:

| Prompt type         | Prefill syntax                                                  |
|---------------------|-----------------------------------------------------------------|
| `AskTextAsync`      | `--answer:name=value`                                           |
| `AskChoiceAsync`    | `--answer:name=label` (case-insensitive label or prefix match)  |
| `AskConfirmationAsync` | `--answer:name=y` or `--answer:name=no` (`y/yes/true/1` or `n/no/false/0`) |
| `AskSecretAsync`    | `--answer:name=value`                                           |
| `AskMultiChoiceAsync` | `--answer:name=1,3` (1-based indices) or `--answer:name=Auth,Cache` (names) |
| `AskEnumAsync`      | `--answer:name=Dark` (enum member name or description)          |
| `AskFlagsEnumAsync` | `--answer:name=Read,Write` (description names, comma-separated) |
| `AskNumberAsync`    | `--answer:name=42`                                              |
| `AskValidatedTextAsync` | `--answer:name=value` (must pass validation)                |

---

## Timeout and cancellation

### Prompt timeout

Pass a `Timeout` via options to auto-select the default after a countdown:

```csharp
var choice = await channel.AskChoiceAsync(
    "action", "Continue?", ["Yes", "No"],
    defaultIndex: 0,
    new AskOptions(Timeout: TimeSpan.FromSeconds(10)));
```

The host displays a countdown and selects the default when time expires.

### Cancellation

- **Esc** during a prompt cancels the prompt
- **Ctrl+C** during a command cancels the per-command `CancellationToken`
- A second **Ctrl+C** exits the session

---

## Custom presenters

The interaction channel delegates all rendering to an `IReplInteractionPresenter`. By default, the built-in console presenter is used, but you can replace it via DI:

```csharp
var app = ReplApp.Create(services =>
{
    services.AddSingleton<IReplInteractionPresenter, MyCustomPresenter>();
});
```

This enables third-party packages (e.g. Spectre.Console, Terminal.Gui, or GUI frameworks) to provide their own rendering without replacing the channel logic (validation, retry, prefill, timeout).

If you use `Repl.Spectre`, the package now registers `SpectreInteractionPresenter` as the default presenter when no custom presenter already exists. That implementation wraps the built-in console behavior and also supports temporary capture for screen-owned flows.

The presenter receives strongly-typed semantic events:

| Event type              | When emitted                     |
|-------------------------|----------------------------------|
| `ReplPromptEvent`       | Before each prompt               |
| `ReplStatusEvent`       | Status and validation messages   |
| `ReplProgressEvent`     | Progress updates                 |
| `ReplClearScreenEvent`  | Clear screen requests            |

All events inherit from `ReplInteractionEvent(DateTimeOffset Timestamp)`.

---

## Custom interaction handlers

For richer control over the interaction experience (e.g. Spectre.Console autocomplete, Terminal.Gui dialogs, or GUI pop-ups), register an `IReplInteractionHandler` via DI. Handlers form a chain-of-responsibility pipeline: each handler pattern-matches on the request type and either returns a result or delegates to the next handler. The built-in console handler is always the final fallback.

```csharp
var app = ReplApp.Create(services =>
{
    services.AddSingleton<IReplInteractionHandler, MyInteractionHandler>();
});
```

### How the pipeline works

1. Prefill (`--answer:*`) is checked first — it always takes precedence.
2. The handler pipeline is walked in registration order.
3. Each handler receives an `InteractionRequest` and returns either `InteractionResult.Success(value)` or `InteractionResult.Unhandled`.
4. The first handler that returns `Success` wins — subsequent handlers are skipped.
5. If no handler handles the request, the built-in console presenter renders it.

### Request types

Each core primitive has a corresponding request record:

| Request type              | Result type            | Corresponding method       |
|---------------------------|------------------------|----------------------------|
| `AskTextRequest`          | `string`               | `AskTextAsync`             |
| `AskChoiceRequest`        | `int`                  | `AskChoiceAsync`           |
| `AskConfirmationRequest`  | `bool`                 | `AskConfirmationAsync`     |
| `AskSecretRequest`        | `string`               | `AskSecretAsync`           |
| `AskMultiChoiceRequest`   | `IReadOnlyList<int>`   | `AskMultiChoiceAsync`      |
| `ClearScreenRequest`      | —                      | `ClearScreenAsync`         |
| `WriteStatusRequest`      | `bool`                 | `WriteStatusAsync`         |
| `WriteProgressRequest`    | `bool`                 | `WriteProgressAsync`       |
| `WriteNoticeRequest`      | `bool`                 | `WriteNoticeAsync`         |
| `WriteWarningRequest`     | `bool`                 | `WriteWarningAsync`        |
| `WriteProblemRequest`     | `bool`                 | `WriteProblemAsync`        |

All request types derive from `InteractionRequest<TResult>` (or `InteractionRequest` for void operations) and carry the same parameters as the corresponding channel method.

### Example handler

```csharp
public class SpectreInteractionHandler : IReplInteractionHandler
{
    public ValueTask<InteractionResult> TryHandleAsync(
        InteractionRequest request, CancellationToken ct) => request switch
    {
        AskChoiceRequest r => HandleChoice(r, ct),
        AskSecretRequest r => HandleSecret(r, ct),
        _ => new ValueTask<InteractionResult>(InteractionResult.Unhandled),
    };

    private async ValueTask<InteractionResult> HandleChoice(
        AskChoiceRequest r, CancellationToken ct)
    {
        // Spectre.Console rendering...
        var index = 0; // resolved from Spectre prompt
        return InteractionResult.Success(index);
    }

    private async ValueTask<InteractionResult> HandleSecret(
        AskSecretRequest r, CancellationToken ct)
    {
        // Spectre.Console secret prompt...
        var secret = ""; // resolved from Spectre prompt
        return InteractionResult.Success(secret);
    }
}
```

### Handlers vs presenters

| Concern               | `IReplInteractionPresenter`         | `IReplInteractionHandler`                |
|-----------------------|-------------------------------------|------------------------------------------|
| **What it controls**  | Visual rendering of events          | Full interaction flow (input + output)   |
| **Granularity**       | Display only — no input             | Reads user input and returns results     |
| **Pipeline position** | After the built-in logic            | Before the built-in logic                |
| **Use case**          | Custom progress bars, styled text   | Spectre prompts, GUI dialogs, TUI        |

Use a **presenter** when you only want to change how things look. Use a **handler** when you want to replace the entire interaction for a given request type.

## Spectre and screen ownership

`IAnsiConsole.Write(...)` is great for one-shot renderables, banners, and prompt-driven flows. It is not a good fit for a full-screen or continuously refreshed TUI if normal REPL feedback is still writing to the same terminal surface.

In particular:

- Do not mix a Spectre live display / full-screen surface with normal REPL status or progress output on the same writer.
- `OSC 9;4` progress is terminal feedback for CLI-style execution, not a rendering primitive for TUIs.
- If your app temporarily owns the screen, capture interaction output away from the main surface.

With `Repl.Spectre`, use `SpectreInteractionPresenter.BeginCapture(...)`:

```csharp
var presenter = services.GetRequiredService<SpectreInteractionPresenter>();
var io = services.GetRequiredService<IReplIoContext>();

using var capture = presenter.BeginCapture(io.Error);
await RunFullScreenDashboardAsync(cancellationToken);
```

You can capture to any `IReplInteractionPresenter` or to a plain `TextWriter`. For application handlers, prefer a session-aware sink such as `IReplIoContext.Error` or a custom presenter registered by your host. The `TextWriter` overload emits plain text only — no ANSI styling, no line rewriting, and no `OSC 9;4`.

### Custom request types

Apps can define their own `InteractionRequest<TResult>` subtypes for app-specific controls:

```csharp
public sealed record AskColorPickerRequest(string Name, string Prompt)
    : InteractionRequest<Color>(Name, Prompt);
```

Dispatch them through the pipeline via `DispatchAsync`:

```csharp
var color = await channel.DispatchAsync(
    new AskColorPickerRequest("color", "Pick a color:"),
    cancellationToken);
```

If no registered handler handles the request, a `NotSupportedException` is thrown with a clear message identifying the unhandled request type. This ensures app authors are immediately aware when a required handler is missing.

---

## Rich interactive prompts

When the terminal supports ANSI escape sequences and individual key reads, `AskChoiceAsync` and `AskMultiChoiceAsync` automatically upgrade to rich interactive menus:

- **Single-choice**: arrow-key menu (`Up`/`Down` to navigate, `Enter` to confirm, `Esc` to cancel). Mnemonic shortcut keys select items directly.
- **Multi-choice**: checkbox-style menu (`Up`/`Down` to navigate, `Space` to toggle, `Enter` to confirm with min/max validation, `Esc` to cancel).

The upgrade is transparent — command handlers call the same `AskChoiceAsync` / `AskMultiChoiceAsync` API; the framework selects the best rendering mode automatically.

### Fallback chain

The interaction pipeline evaluates handlers in this order:

1. **Prefill** (`--answer:*`) — always checked first.
2. **User handlers** — `IReplInteractionHandler` implementations registered via DI.
3. **Built-in rich handler** (`RichPromptInteractionHandler`) — renders arrow-key menus when ANSI + key reader are available.
4. **Text fallback** — numbered list with typed input; works in all environments (redirected stdin, hosted sessions, no ANSI).

If the terminal cannot support rich prompts (e.g. ANSI disabled, stdin redirected, or hosted session), the framework falls back to the text-based prompt automatically.

---

## Mnemonic shortcuts

Choice labels support an underscore convention to define keyboard shortcuts:

| Label | Display | Shortcut |
|---|---|---|
| `"_Abort"` | `Abort` | `A` |
| `"No_thing"` | `Nothing` | `t` |
| `"__real"` | `_real` | (none — escaped underscore) |
| `"Plain"` | `Plain` | (auto-assigned) |

### Rendering

- **ANSI mode**: the shortcut letter is rendered with an underline (`ESC[4m` / `ESC[24m`).
- **Text mode**: the shortcut letter is wrapped in brackets: `[A]bort / [R]etry / [F]ail`.

### Auto-assignment

When a label has no explicit `_` marker, the framework auto-assigns a shortcut:

1. First unique letter of the display text.
2. If taken, scan remaining letters.
3. If all letters are taken, assign digits `1`–`9`.

### Example

```csharp
var index = await channel.AskChoiceAsync(
    "action", "How to proceed?",
    ["_Abort", "_Retry", "_Fail"],
    defaultIndex: 0);
```

---

## `ITerminalInfo`

The `ITerminalInfo` service exposes terminal capabilities for custom `IReplInteractionHandler` implementations. It is registered automatically by the framework and available via DI.

```csharp
public interface ITerminalInfo
{
    bool IsAnsiSupported { get; }
    bool CanReadKeys { get; }
    (int Width, int Height)? WindowSize { get; }
    AnsiPalette? Palette { get; }
}
```

### Usage in a custom handler

```csharp
public class MyHandler(ITerminalInfo terminal) : IReplInteractionHandler
{
    public ValueTask<InteractionResult> TryHandleAsync(
        InteractionRequest request, CancellationToken ct)
    {
        if (!terminal.IsAnsiSupported || !terminal.CanReadKeys)
            return new(InteractionResult.Unhandled);

        // Rich rendering using terminal.WindowSize, terminal.Palette, etc.
        ...
    }
}
```

Register via DI as usual:

```csharp
var app = ReplApp.Create(services =>
{
    services.AddSingleton<IReplInteractionHandler, MyHandler>();
});
```

The framework injects `ITerminalInfo` automatically — no manual registration required.

---

## Spectre.Console integration

The `Repl.Spectre` package provides a production-ready `IReplInteractionHandler` that renders
all prompts as rich Spectre.Console widgets, plus injectable `IAnsiConsole` for custom renderables.

```csharp
var app = ReplApp.Create(services =>
{
    services.AddSpectreConsole();
})
.UseSpectreConsole();
```

With this setup:

- `AskChoiceAsync` renders as a Spectre `SelectionPrompt` (arrow-key navigation)
- `AskMultiChoiceAsync` renders as a `MultiSelectionPrompt` (checkbox-style)
- `AskConfirmationAsync` renders as a `ConfirmationPrompt`
- `AskTextAsync` renders as a `TextPrompt<string>`
- `AskSecretAsync` renders as a `TextPrompt<string>.Secret()`
- Collections returned from handlers render as lightweight Spectre tables

### Output formats

`UseSpectreConsole()` registers the `spectre` output format and makes it the default. You can still switch per-command:

- `--spectre` selects the Spectre renderer
- `--human` switches back to the standard text renderer
- `--output:<format>` remains the canonical format selector

`--help` respects the selected format:

- `human` renders the classic text help
- `spectre` renders dedicated Spectre help
- `json/xml/yaml/markdown` keep the structured help pipeline

### Presenter capture for future TUIs

When a command temporarily owns the terminal surface, capture interaction events explicitly:

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

This is the intended integration point for future TUI tooling.

Command handlers remain unchanged — the upgrade from built-in prompts to Spectre prompts is transparent.

See also: [`Repl.Spectre` README](../src/Repl.Spectre/README.md) | [sample 07-spectre](../samples/07-spectre/)
