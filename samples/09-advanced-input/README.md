# Advanced Input

This sample embeds Repl in an application that keeps its legacy search syntax
and owns an index-refresh action. These are advanced, opt-in integration features
for backward compatibility or applications where Repl is only a small component.
Start with [Core Basics](../01-core-basics/) for ordinary `Map` and `RunAsync` usage.

## Run the walkthrough

From the repository root:

```powershell
dotnet run --project samples/09-advanced-input -- demo
dotnet run --project samples/09-advanced-input
```

In the interactive session, type `demo` to display the steps again.

The same sample also demonstrates preparation and startup; no separate app is
needed. Preparation declares typed globals and normalizes legacy input before
`SearchPolicy` is registered. A startup hook then receives the composed index:

```powershell
dotnet run --project samples/09-advanced-input -- /find "hello world" 3 --maximum-limit 2
dotnet run --project samples/09-advanced-input -- status --refresh-on-start
dotnet run --project samples/09-advanced-input -- --refresh-on-start
```

The first invocation is refused by the policy selected before DI. The second
refreshes once before `status` executes. The last handles startup without needing
a matching command. Help and completion do not perform that action. Startup does
not repeat for later interactive commands; `/refresh` still handles individual
interactive input as shown below.

| Try | Observe |
| --- | --- |
| Type `/find` and pause | An incomplete-input hint asks for a search term. |
| Type `/find "hello world" 3`, then Tab | The line becomes `query "hello world" --limit 3`. |
| Submit that line | The normal query handler returns three matching entries. |
| Submit `/find "hello world" 9` | A validator rejects the limit using an injected `SearchPolicy`. |
| Submit `/find "hello world" 0` | A parsed invocation hook selects the policy maximum, then normal validation and search run. |
| Submit `status`, type `/refresh`, and pause | Preview leaves the host refresh counter unchanged. |
| Submit `/refresh`, then `status` | The counter increases once and the session stays open. |

`/refresh` simulates the surrounding application's action with an in-memory
counter; it does not modify files or contact a service. Its invocation hook gets
`SearchIndex` through injection and checks `Purpose` before performing the action.
The preview adapter only suppresses the unknown-command hint. Invocation hooks
run on submission and explicit completion, so help/completion guards still matter.

## One-shot CLI

```powershell
dotnet run --project samples/09-advanced-input -- /find "hello world" 2
dotnet run --project samples/09-advanced-input -- query "hello world" --limit 2
dotnet run --project samples/09-advanced-input -- /find "hello world" 9
dotnet run --project samples/09-advanced-input -- /refresh
```

The first two invocations return the same results. The third fails validation.
Each CLI process has its own counter; use an interactive session to observe it
across invocations.

## Read the implementation

- The first adapter translates `/find` into ordinary Repl arguments without
  rebuilding a shell command string.
- A parsed hook handles `/refresh` outside the command graph with an injected index.
- Another parsed hook rewrites a zero limit into a native structured request using
  the injected policy, preserving the parsed globals. Earlier callbacks are not replayed.
- The validator receives parsed values, `SearchPolicy` and `CancellationToken`
  directly through its signature. It runs during analysis and submission.
- `query` and `status` use the same `SearchIndex` owned by the host application.

See [advanced input adaptation and validation](../../docs/advanced-input.md) for
source ranges, cancellation, shell completion and the normalization opt-out.
The [startup guide](../../docs/advanced-startup.md) specifies callback order,
Core without DI, external providers and lifecycle ownership.
