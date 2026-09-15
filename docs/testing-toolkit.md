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

Runs execute as **standalone invocations**, not hosted sessions — the same classification a
process-owning `Main` gets. Commands gated to the CLI channel are therefore present, and handlers see
`IReplIoContext.IsHostedSession == false`.

`RunTimeout` is measured against the clock rather than against the run agreeing to stop, so a command
that never observes its cancellation token still fails the test instead of hanging the suite. Such a
run cannot be killed: the harness abandons it, and disposal says so — naming the command lines,
because such a run keeps its place in the process-wide signal epoch and every later harness is refused
until it ends.

**`StartRunAsync` guarantees the signal will reach the run, not that the command is running** — and
only for as long as the run lasts. The signal scope is installed around the whole run, before its
arguments are parsed, so the command body has usually not started when the call returns. A command
short enough to finish first takes its scope with it, and a delivery after that is `NotHandled`,
exactly as a signal arriving after a real process has done its work would be: signal a run that stays
put. If you are asserting on what the command did — that its
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
var run = await harness.StartRunAsync("work");

// Ctrl+Break is a signal on Windows and nothing anywhere else — this passes on Linux too.
harness.SendSignal(ReplProcessSignal.Break).Should().Be(ReplSignalDelivery.CancellationRequested);
```

Profiles: `Current`, `Windows`, `Unix` (Linux and macOS decide identically here, so they share one),
`Android`, `Browser`, `IOS`, `TvOS`. The last four have no signal bridge, so a run under them degrades
to caller-owned handling and says so once.

A declared platform changes **decisions only**: the harness never installs an operating-system signal
registration on that platform's behalf. That matters more than it sounds — .NET accepts a `SIGTERM`
registration on Windows too, so nothing but this rule would stop a declared-Unix test from installing
a live handler in your test process.

One consequence to know: a profile with no signal bridge (`Browser`, `Android`, `IOS`, `TvOS`)
registers no console cancel-key handler either, so a real Ctrl+C aimed at your runner during such a run
takes its normal course rather than being claimed by it.

Profiles are the only way to build one — the flags are readable so you can assert on them, not
settable, so combinations no device has cannot be constructed by mistake.

### One harness at a time

Signal handling is process-global, and a harness owns it for its lifetime — really owns it, not by
convention. While it holds ownership, a second harness is refused, and so is any other run that would
install its own signal handling: such a run joins the harness's isolated epoch, gets cancelled by its
synthetic signals, and releases its readiness wait. Failing that run loudly is the point. Configure
your framework so it does not happen:

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
when it fails; and disposal kills the process tree, so a failed assertion does not leave the probed
application running. That last one reaches only as far as the tree's root: a child that spawns
something long-lived and then exits on its own leaves that descendant behind, because there is no
parent left to walk down from.

`ReplProcessProbeOptions.Timeout` bounds every one of those waits — for output, for a signal, for the
exit — and defaults to 30 seconds. `Timeout.InfiniteTimeSpan` waits without a deadline; zero and
negatives are refused, because they would fail each wait the moment it started. The exit wait watches
the process rather than its output streams: a descendant that inherited the child's redirected handles
holds them open after the child is gone, and waiting on end-of-stream would report a process that
exited milliseconds ago as still running. Output settles separately, under its own short grace.

One last caveat about addressing a process by id: a signal and the tree kill both resolve the child by
its process id, and .NET's Unix implementation matches descendants without a start-time check. An id
reused between the probe reading it and the operation running belongs to whoever inherited it. In
practice this needs a process table churning hard enough to wrap within a test, but it is the reason
the probe kills as early as it can rather than at the end of a suite.

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
