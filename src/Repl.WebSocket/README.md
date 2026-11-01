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
- A terminal Ctrl+C interrupts the active command in that session; it does not stop the host.

## Docs

- [Cookbook: Hosting Remote Sessions](https://repl.yllibed.org/cookbook/hosting-remote/) — WebSocket/Telnet setup, DI scopes, auth
- [Terminal Integration](https://repl.yllibed.org/reference/terminal-integration/) — capability detection, window size, ANSI
