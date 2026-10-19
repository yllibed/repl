# 05 Hosting Remote

Remote-hosted REPL sample running over three transports:

- raw WebSocket
- Telnet-over-WebSocket
- SignalR

It demonstrates shared state, session visibility, transport-aware terminal metadata, and hosted user feedback in one app.

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
| Terminal capabilities | `debug` | Structured status rows showing ANSI support, progress reporting, window size, terminal, transport |
| Interactive configuration | `configure` | Multi-choice interactive menu (rich arrow-key menu or text fallback) |
| Maintenance actions | `maintenance` | Single-choice interactive menu with mnemonic shortcuts (`_Abort`, `_Retry`, `_Fail`) |
| Hosted feedback | `feedback demo`, `feedback fail` | Shows progress, warning, error, and indeterminate states over hosted transports |
| Browser feedback mirror | Panel above the terminal | Mirrors hosted `OSC 9;4` progress signals into a badge + progress bar |

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
feedback demo
feedback fail
settings show maintenance
settings set maintenance on
watch
send hello
```

## Multi-session Scenario

1. Open two browser tabs on the sample page.
2. In tab A, connect and run: `watch`
3. In tab B, connect and run: `send hello from tab-b`
4. In tab B, run: `feedback demo`
5. In tab C, connect with `Plain (no ANSI)` and run: `feedback demo`
6. In tab B (or a third tab), run: `sessions`
7. Run: `status` or `debug` in each tab and compare transport/terminal/screen/feedback values.

## Feedback Walkthrough

1. Connect with `WebSocket`, `Telnet`, or `SignalR`.
2. Run `feedback demo`.
3. Watch the terminal render normal progress, an indeterminate state, a warning state, then a successful completion.
4. Watch the browser's **Feedback Mirror** panel update from the same hosted `OSC 9;4` stream.
5. Run `feedback fail` to see an error-state progress update and a final problem result.
6. Switch to `Plain (no ANSI)` and repeat. The terminal still shows the text fallback, but the panel stays idle because that client does not advertise `ProgressReporting`.

## Notes

- `sessions` focuses on active server-side connections tracked by the sample.
- `status` reflects metadata of the current REPL session.
- `debug` makes `ProgressReporting` explicit so you can tell whether advanced progress is negotiated for this client.
- Telnet mode performs NAWS/terminal-type negotiation automatically in the browser client script.
- This sample intentionally mixes in-band and out-of-band metadata paths for demonstration.
- Use `--ansi:never` to force Plain mode (no ANSI), which demonstrates the text fallback for interactive prompts.
- The browser panel is a teaching aid: it parses the same hosted `OSC 9;4` progress signals that a richer terminal could consume for native UI integration.
- Canonical support matrix and precedence order live in [`docs/terminal-metadata.md`](../../docs/terminal-metadata.md).
