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
using Repl.Interaction;
using Repl.Terminal;

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

var exitCode = execution.ExitCode;       // process-style status, follows ReplOptions.ExitCodes
var text = execution.OutputText;         // rendered output text
var duration = execution.Duration;       // elapsed command time
```

`RunCommandAsync` throws `TimeoutException` when a command exceeds
`ReplScenarioOptions.CommandTimeout`, whether the application under test lets the cancellation
propagate or maps it to an exit code through `ReplOptions.ExitCodes.Cancelled`.

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
`ReadJson<T>()` is available when the rendered output is JSON and the test should validate the serialized representation.

`Repl.Testing` intentionally validates the Repl command pipeline rather than MCP protocol metadata.
For MCP wire contracts such as resource `mimeType` values, use the MCP test fixture or the opt-in MCP Inspector CLI smoke test (`REPL_RUN_MCP_INSPECTOR_TESTS=1` + `TestCategory=ExternalToolchain`).

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

## Process Signals

Signal handling is not a session concern, so it has its own entry points rather than living on
`ReplTestHost`. There are two, and they prove different things:

- `ReplProcessSignalHarness` drives the whole lifecycle **in memory**. Deterministic, fast, runs
  everywhere, and covers every decision the framework makes.
- `ReplProcessProbe` **spawns your application** and sends it real signals. Slower and platform-bound,
  and the only way to show that a process actually terminates.

Reach for the harness first. Use the probe for the handful of guarantees the harness cannot honestly
make.

### Deterministic, in memory

```csharp
await using var harness = ReplProcessSignalHarness.Create(CreateApp);
var run = await harness.StartRunAsync("work");

var delivery = harness.SendSignal(ReplProcessSignal.Interrupt);
var result = await run.Completion;

delivery.Should().Be(ReplSignalDelivery.CancellationRequested);
result.OutcomeKind.Should().Be(ReplExecutionOutcomeKind.Interrupted);
result.ExitCode.Should().Be(130);
```

`SendSignal` is synchronous on purpose: a signal callback owes the operating system a suppression
decision before it returns, so the framework decides synchronously and so does this. Await
`run.Completion` to see what the decision did.

**`StartRunAsync` guarantees the signal will reach the run, not that the command is running.** The
signal scope is installed around the whole run, before its arguments are parsed, so the command body
has usually not started when the call returns. If you are asserting on what the command did — that its
cleanup ran, say — have the command say when it is running:

```csharp
var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
app.Map("work", async (CancellationToken ct) =>
{
    started.TrySetResult();
    try { await Task.Delay(Timeout.Infinite, ct); return "unreachable"; }
    finally { /* cleanup you want to assert on */ }
});
// ...
var run = await harness.StartRunAsync("work");
await started.Task;
harness.SendSignal(ReplProcessSignal.Interrupt);
```

Start a second run to test a **late joiner** — a run that begins while a signal is already claimed and
inherits that epoch. Starting is serialised; the runs are not. Note that an epoch resets once its last
run drains, so the earlier run has to still hold its scope for the window to exist.

### Declaring a platform

By default the harness applies the decisions of the platform you are on. Declare another one and they
become assertable from anywhere:

```csharp
await using var harness = ReplProcessSignalHarness.Create(
    CreateApp,
    options => options.Platform = ReplPlatformProfile.Windows);
// Ctrl+Break is a signal on Windows and nothing anywhere else — this passes on Linux too.
harness.SendSignal(ReplProcessSignal.Break).Should().Be(ReplSignalDelivery.CancellationRequested);
```

Profiles: `Current`, `Windows`, `Unix` (Linux and macOS decide identically here, so they share one),
`Android`, `Browser`, `IOS`, `TvOS`. The last four have no signal bridge, so a run under them degrades
to caller-owned handling and says so once.

A declared platform changes **decisions only**. It never installs an operating-system registration on
that platform's behalf, and a real signal aimed at your test runner is still judged against the real
host. That matters more than it sounds: .NET accepts a `SIGTERM` registration on Windows too, so
nothing but this rule would stop a declared-Unix test from installing a live handler in your test
process.

### One harness at a time

Signal handling is process-global. A harness owns it for its lifetime, and creating a second one while
the first is alive throws — two would corrupt each other's isolation, not merely race on the
application. Configure your framework accordingly:

| Framework | Parallel by default? | What to add |
| --- | --- | --- |
| MSTest | yes | `[DoNotParallelize]` on the test class |
| xUnit | yes, across classes | a shared `[Collection("...")]`, or `[assembly: CollectionBehavior(DisableTestParallelization = true)]` |
| NUnit | no | `[NonParallelizable]`, only if you opted into parallelism |

Sharding across separate processes needs none of this: the state is per-process. And the package
references no test framework, so it cannot apply any of these for you.

### What only a real process can prove

```csharp
await using var probe = ReplProcessProbe.Start("./my-app", ["wait", markerPath]);
await probe.WaitForOutputAsync("READY");
await probe.SendSignalAsync(ReplProcessSignal.Terminate);

(await probe.WaitForExitAsync()).Should().Be(143);
```

Output is drained continuously, so a chatty child never blocks; every wait reports what was captured
when it fails; and disposal kills the process tree so a failed assertion cannot leak a running
process.

**Signals are delivered on Unix only.** Sending one to another process on Windows needs a console
control event and console attachment rather than a signal, which is deliberately out of scope until
the Windows lifecycle work lands. `SendSignalAsync` throws `PlatformNotSupportedException` there;
spawning, waiting on output and exit codes all still work, so a cross-platform suite shares everything
but the delivery. `ReplProcessSignal.Break` is refused on every platform — it is a Windows console
event, and the nearest Unix signal is one the framework deliberately leaves unclaimed.

One trap worth knowing: `probe.Output` holds what the child flushed. To observe what happened during a
shutdown the process might not survive, have the application append to a file and read that.

### What each half proves

| | Harness | Probe |
| --- | --- | --- |
| The decision about each signal | yes | — |
| Diagnostics the framework wrote | yes, on `harness.DiagnosticText` | in the child's output |
| The exit code the run resolves to | yes | yes, as the shell sees it |
| Cleanup was given time to run | yes | yes |
| Every platform's wiring decisions | yes, from any host | only the host's own |
| **The process actually terminated** | **no** | **yes** |

A second signal makes the harness report `WouldTerminateProcess`. That is the framework's decision and
nothing more: nothing dies in-process, so the run keeps unwinding and your code after the call keeps
executing. Read it as "Repl chose not to intervene again", never as "it stopped".

Signal diagnostics are written from whichever context delivers the signal — an operating-system
callback thread with no session, in production — so they belong to the delivery rather than to a run.
`harness.DiagnosticText` holds them; `ReplSignalRunResult.DiagnosticText` holds what the run itself
wrote.

## Notes

- The harness is in-memory and transport-agnostic (no loopback network stack required).
- Session state persists per `ReplSessionHandle` across multiple commands.
- The toolkit itself does not require MSTest/xUnit/NUnit or any assertion package.
