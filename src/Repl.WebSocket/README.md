# Repl.WebSocket

**Website:** [repl.yllibed.org](https://repl.yllibed.org/)

`Repl.WebSocket` runs a Repl Toolkit session over a **raw WebSocket** connection.

## Install

```bash
dotnet add package Repl.WebSocket
```

## Run a session (example)

```csharp
using System.Net.WebSockets;
using Repl;
using Repl.WebSocket;

var app = ReplApp.Create().UseDefaultInteractive();
app.Map("hello", () => "world");

WebSocket socket = /* connected socket */;
return await ReplWebSocketSession.RunAsync(app, socket);
```

## Notes

For an application embedded in an ASP.NET Core host, pass its services explicitly. The request owns the WebSocket session scope:

```csharp
await ReplWebSocketSession.RunWithServicesAsync(app, socket, context.RequestServices,
    new ReplRunOptions { SessionScope = SessionScopeBehavior.CallerOwned },
    cancellationToken: context.RequestAborted);
```

`RunAsync` uses the Repl application's own services. `RunWithServicesAsync` borrows the supplied provider; it neither starts host services nor disposes that provider. Its default scope behavior opens a scope per session; use `CallerOwned` when the provider already represents the connection's scope.

- Supports in-band terminal control messages (`@@repl:*`) for terminal/session metadata.
- Reassembles fragmented messages before decoding terminal controls and preserves UTF-8 characters split across reads.
- Limits each incoming message to 64 KiB across all its fragments. Oversized messages close the session with `MessageTooBig` (1009), interrupting its active command; a peer that ignores the close is aborted after the close deadline. Clients sending larger pastes must split them into separate messages.
- The one-second close deadline starts once and remains active during synchronous cancellation callbacks and asynchronous command cleanup. The connection can be aborted while cleanup is pending; the session still waits for that cleanup before releasing its host and borrowed services.
- A terminal Ctrl+C interrupts the active command in that session; it does not stop the host.
- A peer close or dropped connection cancels the session, including a command that is not reading terminal input. The session waits for command cleanup before releasing its host and services. Peer disconnection ends normally; cancellation requested by the caller still propagates to the caller.

## Docs

- [Cookbook: Hosting Remote Sessions](https://repl.yllibed.org/cookbook/hosting-remote/) — WebSocket/Telnet setup, DI scopes, auth
- [Terminal Integration](https://repl.yllibed.org/reference/terminal-integration/) — capability detection, window size, ANSI
