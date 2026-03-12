# Interaction Channel

The interaction channel is a bidirectional contract between command handlers and the host.
Handlers emit **semantic requests** (prompts, status, progress); the host decides **how to render** them.

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

Inline feedback (validation errors, status messages).

```csharp
await channel.WriteStatusAsync("Import started", cancellationToken);
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
| `WriteStatusRequest`      | —                      | `WriteStatusAsync`         |
| `WriteProgressRequest`    | —                      | `WriteProgressAsync`       |

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
