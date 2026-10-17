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

| Try | Observe |
| --- | --- |
| Type `/find` and pause | An incomplete-input hint asks for a search term. |
| Type `/find "hello world" 3`, then Tab | The line becomes `query "hello world" --limit 3`. |
| Submit that line | The normal query handler returns three matching entries. |
| Submit `/find "hello world" 9` | A validator rejects the limit using an injected `SearchPolicy`. |
| Submit `status`, type `/refresh`, and pause | Preview leaves the host refresh counter unchanged. |
| Submit `/refresh`, then `status` | The counter increases once and the session stays open. |

`/refresh` simulates the surrounding application's action with an in-memory
counter; it does not modify files or contact a service. Its adapter checks both
`Mode` and `Purpose` before performing the action. Preview and completion can run
repeatedly, so applications must apply those guards themselves.

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
- The second adapter handles `/refresh` outside the command graph.
- The validator receives parsed values, `SearchPolicy` and `CancellationToken`
  directly through its signature. It runs during analysis and submission.
- `query` and `status` use the same `SearchIndex` owned by the host application.

See [advanced input adaptation and validation](../../docs/advanced-input.md) for
source ranges, cancellation, shell completion and the normalization opt-out.
