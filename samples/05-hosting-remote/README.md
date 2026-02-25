# 05 Hosting Remote

Remote-hosted REPL sample running over three transports:
- raw WebSocket
- Telnet-over-WebSocket
- SignalR

It demonstrates shared state, session visibility, and transport-aware terminal metadata in one app.

## Architecture snapshot

```text
Browser terminal (xterm.js / VT-compatible, raw mode)
  <-> Transport (WebSocket / Telnet-over-WebSocket / SignalR)
       -> StreamedReplHost (transport-agnostic)
          -> server-side line editor + VT probe
          -> ReplApp command graph (same handlers as local)
```

## What It Shows

| Area | Command(s) | Notes |
|---|---|---|
| Shared settings | `settings show {key}`, `settings set {key} {value}` | Values are shared across all connected sessions |
| Message bus | `watch`, `send {message}` | One session watches, another publishes |
| Session names | `who` | Quick list of connected session identifiers |
| Session details | `sessions` | Transport, remote peer, terminal, screen, connected/idle durations |
| Runtime diagnostics | `status` | Screen, terminal identity/capabilities, transport, runtime |

## Run

```powershell
dotnet run --project samples/05-hosting-remote/HostingRemoteSample.csproj
```

Open:

```text
http://localhost:5000
```

Optional quick-connect query:

```text
http://localhost:5000/?autoconnect=telnet
```

Supported values: `ws`, `websocket`, `telnet`, `signalr`, `sr`.

If Kestrel binds to another port, use the URL printed in the console.

## Browser UI

The page lets you choose a transport and connect the terminal.  
Try these commands directly in the REPL:

```text
status
sessions
who
settings show maintenance
settings set maintenance on
watch
send hello
```

## Multi-session Scenario

1. Open two browser tabs on the sample page.
2. In tab A, connect and run: `watch`
3. In tab B, connect and run: `send hello from tab-b`
4. In tab B (or a third tab), run: `sessions`
5. Run: `status` in each tab and compare transport/terminal/screen values.

## Notes

- `sessions` focuses on active server-side connections tracked by the sample.
- `status` reflects metadata of the current REPL session.
- Telnet mode performs NAWS/terminal-type negotiation automatically in the browser client script.
- This sample intentionally mixes in-band and out-of-band metadata paths for demonstration.
- Canonical support matrix and precedence order live in [`docs/terminal-metadata.md`](../../docs/terminal-metadata.md).
