# 06 — Testing
**Deterministic, typed tests for your command surface (Repl.Testing)**

This demo is about one thing: **making REPL/CLI apps testable without hacks**.

No `Console.SetIn/SetOut`.  
No brittle “assert on a giant string blob” unless you *want* to.  
No special-casing “interactive mode” vs “CLI mode”.

`Repl.Testing` gives you a **first-party test harness** to run commands and sessions deterministically, then assert on:

- **exit codes**
- **rendered output** (human or machine)
- **typed results** (`GetResult<T>()` / JSON deserialization)
- optional **interaction events** (status/progress/prompts)
- multi-session scenarios with **session descriptors + metadata snapshots**

It’s test-framework agnostic: **xUnit / NUnit / MSTest** all work.

---

## ⚡ 30-second tour

A multi-step, multi-session scenario can stay readable and semantic:

```csharp
await using var host = ReplTestHost.Create(() => SampleReplApp.Create(shared));
await using var admin = await host.OpenSessionAsync(new SessionDescriptor { TransportName = "signalr" });
await using var op = await host.OpenSessionAsync(new SessionDescriptor { TransportName = "websocket" });

var set = await admin.RunCommandAsync("settings set maintenance on --no-logo");
var show = await op.RunCommandAsync("settings show maintenance --no-logo");
var widget = await op.RunCommandAsync("widget show 2 --json --no-logo");

set.ExitCode.Should().Be(0);
show.OutputText.Should().Contain("on");
widget.GetResult<SampleReplApp.Widget>().Name.Should().Be("Beta");
```

That’s the promise: **AAA-style tests** for a command surface that behaves like a real operator tool.

---

## What you are testing (mental model)

You’re not “testing a console app”. You’re testing a **command surface**:

- a command graph
- a runtime that executes commands in different modes
- sessions with metadata and per-session state
- optional interaction events (status/progress/prompts)
- deterministic output modes (JSON/Markdown/etc.)

`Repl.Testing` sits above that runtime and gives you handles that look like what you reason about:

- “run this command once”
- “open a session and drive it step by step”
- “open two sessions and verify shared behavior”
- “assert on typed results, not parsing strings”

---

## What this sample contains

This sample is a **test project** where:

- the REPL app under test lives in the same project (`SampleReplApp.cs`)
- tests demonstrate common patterns (`Given_TestingSample.cs`)

Files:

- `samples/06-testing/SampleReplApp.cs` (this usually lives in a separate project: that's your app)
- `samples/06-testing/Given_TestingSample.cs`
- `samples/06-testing/TestingSample.csproj`

---

## What it shows

| Area                   | Test                                                               | What to look for                              |
|------------------------|--------------------------------------------------------------------|-----------------------------------------------|
| Command execution      | `When_RunningSimpleCommand_Then_OutputIsAvailable`                 | `ExitCode` + `OutputText` assertions          |
| Typed result           | `When_CommandReturnsObject_Then_TypedAssertionsArePossible`        | `GetResult<T>()` (preferred) and JSON parsing |
| Semantic events        | `When_CommandEmitsStatus_Then_InteractionEventsCanBeAsserted`      | `InteractionEvents` + `TimelineEvents`        |
| Multi-session metadata | `When_MultipleSessionsAreOpen_Then_SessionSnapshotsExposeMetadata` | `SessionDescriptor`, `QuerySessionsAsync`     |
| Timeouts               | `When_CommandIsTooSlow_Then_TimeoutIsRaised`                       | `CommandTimeout` contract                     |

The point isn’t the exact test names — it’s the **assertion shapes**.

---

## Testing styles supported

### 1) One-shot commands (CLI-style)

Use this when you want “run once, capture output, assert”:

- great for command behavior
- great for JSON output contracts
- very fast
- very stable

Typical assertions:

- `ExitCode == 0`
- `OutputText` contains something
- `ReadJson<T>()` or `GetResult<T>()` for typed checks

### 2) Interactive sessions (REPL-style)

Use this for guided operations and long-running flows:

- simulate typing lines in a session
- assert incremental output
- optionally assert on interaction events (status/progress) instead of rendered text

Typical assertions:

- output contains a prompt/progress label
- event stream contains a `ReplProgressEvent` with a certain label
- cancellation behaves as expected

### 3) Multi-session scenarios

Use this when behavior depends on sessions:

- shared app state (settings/message bus)
- session metadata (transport name, remote, terminal, screen)
- concurrency patterns (watch/send)

The key feature is that your tests can remain **deterministic**, even when the app scenario involves “many sessions” in real life.

---

## Capture modes: rendered output vs raw result

`Repl.Testing` can capture results at two levels:

- **Rendered output** (default): what the user or agent sees (`OutputText`)
- **Raw result object** (optional): what the handler returned *before* rendering

Why this matters:

- Rendered output is the contract you ship to humans/agents.
- Raw result capture is excellent for **unit-level behavior** (strong typing, no formatting noise).
- You can choose which contract you care about per test.

---

## Interaction events (optional, but powerful)

If your commands use `IReplInteractionChannel` (demo 04), you can assert on **semantic events**:

- status events (`ReplStatusEvent`)
- progress events (`ReplProgressEvent`)
- prompt events (`ReplPromptEvent`)

This gives you tests that don’t break because of:

- ANSI vs non-ANSI rendering differences
- minor text formatting changes
- table width differences

Event assertions are usually for “contract checks”.  
Rendered output assertions remain great for “what does the user see?” tests.

---

## Timeouts and failure diagnostics

The harness supports timeouts for:

- commands that never complete
- session steps that never produce expected output
- interactive flows that stall

The important bit: failure should tell you **what happened**, not just “timed out”.
(Transcript snapshots and event timelines are the usual debugging tools here.)

---

## Run

```powershell
dotnet test --project samples/06-testing/TestingSample.csproj -v minimal
```

---

## Why this matters (the real punch)

A serious command surface is an **operational API**.

If it’s not testable:

- it rots
- automation breaks silently
- “interactive flows” become un-maintainable
- your output contracts drift
- agents end up screen-scraping like it’s 1999

`Repl.Testing` is the missing building block that lets you treat a REPL app like any other production surface:
- deterministic execution
- typed results
- semantic events
- multi-session scenarios
- stable automation contracts

---

## What’s next?

At this point you’ve seen:

- shared routes across CLI/REPL (01)
- dynamic scopes + DI + navigation (02)
- module composition (03)
- guided interaction (04)
- stream-based remote hosting (05)
- deterministic tests (06)
