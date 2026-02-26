# Repl.WebSocket

[![Ask DeepWiki](https://deepwiki.com/badge.svg)](https://deepwiki.com/yllibed/repl)

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

- Terminal metadata model and precedence: [docs/terminal-metadata.md](https://github.com/yllibed/repl/blob/main/docs/terminal-metadata.md)
- Remote hosting sample: [samples/05-hosting-remote](https://github.com/yllibed/repl/blob/main/samples/05-hosting-remote)
- Community DeepWiki (unofficial): [deepwiki.com/yllibed/repl](https://deepwiki.com/yllibed/repl)
