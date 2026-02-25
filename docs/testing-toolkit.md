# Repl.Testing Toolkit

`Repl.Testing` provides an in-memory harness for multi-step and multi-session REPL tests.
It is test-framework-agnostic and assertion-library-agnostic.

## Core Flow

1. Create a host with `ReplTestHost.Create(...)`.
2. Open one or more sessions with `OpenSessionAsync(...)`.
3. Run commands with `RunCommandAsync(...)`.
4. Assert on `CommandExecution` and/or session snapshots.

```csharp
using Repl.Testing;

await using var host = ReplTestHost.Create(() =>
{
    var app = ReplApp.Create().UseDefaultInteractive();
    app.Map("hello", () => "world");
    return app;
});

await using var session = await host.OpenSessionAsync();
var execution = await session.RunCommandAsync("hello --no-logo");
```

## Run Commands And Read Output

`RunCommandAsync` accepts one command line (same tokenization behavior as the toolkit parser, including quoted values).

```csharp
var execution = await session.RunCommandAsync("contact show --json --no-logo");

var exitCode = execution.ExitCode;       // numeric process-style status
var text = execution.OutputText;         // rendered output text
var duration = execution.Duration;       // elapsed command time
```

## Assertion Surface (Complete)

### Exit code and text output

```csharp
execution.ExitCode.Should().Be(0);
execution.OutputText.Should().Contain("world");
```

### Typed result object

```csharp
var contact = execution.GetResult<Contact>();      // throws if wrong/missing type
var ok = execution.TryGetResult<Contact>(out var typed);
```

### Rendered output contract (optional)

Use `OutputText` when you intentionally validate the rendered contract (format/content as seen by users or external clients).
For application tests, prefer `GetResult<T>()` / `TryGetResult<T>(...)` as the default assertion path.

### Semantic interaction events

```csharp
execution.InteractionEvents
    .OfType<ReplStatusEvent>()
    .Should()
    .ContainSingle(e => string.Equals(e.Text, "Import started", StringComparison.Ordinal));
```

### Timeline events

`TimelineEvents` includes:
- `OutputWrittenEvent`
- `InteractionObservedEvent`
- `ResultProducedEvent`

```csharp
execution.TimelineEvents.OfType<ResultProducedEvent>().Should().ContainSingle();
```

## Multi-Session Tests

Open multiple sessions and run commands concurrently.

```csharp
await using var host = ReplTestHost.Create(CreateApp);
await using var ws = await host.OpenSessionAsync(new SessionDescriptor { TransportName = "websocket" });
await using var telnet = await host.OpenSessionAsync(new SessionDescriptor { TransportName = "telnet" });

var a = ws.RunCommandAsync("status --no-logo");
var b = telnet.RunCommandAsync("status --no-logo");
await Task.WhenAll(a.AsTask(), b.AsTask());
```

## Session Metadata And Snapshots

Use `SessionDescriptor` to simulate remote metadata, and `GetSnapshot` / `QuerySessionsAsync` to assert session state.

```csharp
var descriptor = new SessionDescriptor
{
    TransportName = "signalr",
    RemotePeer = "::1:41957",
    TerminalIdentity = "xterm-256color",
    WindowSize = (120, 40),
    TerminalCapabilities = TerminalCapabilities.Ansi | TerminalCapabilities.ResizeReporting,
};

await using var session = await host.OpenSessionAsync(descriptor);
var snapshot = session.GetSnapshot();
var all = await host.QuerySessionsAsync();
```

Fields available on `SessionSnapshot`:
- `SessionId`
- `Transport`
- `Remote`
- `Terminal`
- `Screen`
- `Capabilities`
- `AnsiSupported`
- `LastUpdatedUtc`

## Scenario Options

Configure defaults at host creation:

```csharp
await using var host = ReplTestHost.Create(
    CreateApp,
    options =>
    {
        options.CommandTimeout = TimeSpan.FromSeconds(2);
        options.NormalizeAnsi = true;
    });
```

- `CommandTimeout`: maximum duration per command.
- `NormalizeAnsi`: strips ANSI escape sequences from `OutputText` when `true`.
- `RunOptionsFactory`: provides base `ReplRunOptions` for each session.

## Notes

- The harness is in-memory and transport-agnostic (no loopback network stack required).
- Session state persists per `ReplSessionHandle` across multiple commands.
- The toolkit itself does not require MSTest/xUnit/NUnit or any assertion package.
