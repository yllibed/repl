# Repl.WebSocket

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

- Supports in-band terminal control messages (`@@repl:*`) for terminal/session metadata.

## Docs

- Terminal metadata model and precedence: `https://github.com/yllibed/repl/blob/main/docs/terminal-metadata.md`
- Remote hosting sample: `https://github.com/yllibed/repl/blob/main/samples/05-hosting-remote`
