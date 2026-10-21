# 05 Hosting Remote

Remote-hosted REPL sample running over four transports:

- raw WebSocket
- Telnet-over-WebSocket
- SignalR
- local named pipe, with a native console client and structured one-shot execution

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

## Native console and one-shot commands

Keep the server running, then open another terminal:

```powershell
dotnet run --no-launch-profile --project samples/05-hosting-remote -- --remote-console repl-hosting-sample
dotnet run --no-launch-profile --project samples/05-hosting-remote -- --remote-exec repl-hosting-sample settings set greeting hello
dotnet run --no-launch-profile --project samples/05-hosting-remote -- --remote-exec repl-hosting-sample settings show greeting
dotnet run --no-launch-profile --project samples/05-hosting-remote -- --remote-exec repl-hosting-sample health
```

Console mode supports server-side editing, history, completion, resize and Ctrl+C interruption. Ctrl+D detaches. One-shot mode prints returned JSON and exits with the command's code, which makes `health` usable by a container probe.

The executable selects these client modes before constructing the ASP.NET host. They connect to the running process; they do not start a second web server. Connections must use the same OS user and compatible Windows elevation. Server configuration `Repl:PipeName` (environment variable `Repl__PipeName`) changes the endpoint; pass that name to clients too.

These are advanced embedding features. See [the remote CLI guide](../../docs/remote-cli.md) for package boundaries, stream ownership, startup/callback order and the experimental JSON-RPC profile. Browser transports and the native pipe share the same settings and message bus. The `who`/`sessions` tracker currently lists browser sessions only; the native terminal's `status` command still exposes its own session metadata.

## Container

From the repository root:

```powershell
docker build -f samples/05-hosting-remote/Dockerfile -t repl-hosting-remote .
docker run --rm --name repl-hosting-remote -p 127.0.0.1:8080:8080 repl-hosting-remote
```

Open `http://localhost:8080`, or attach from another terminal:

```powershell
docker exec -it repl-hosting-remote dotnet /app/HostingRemoteSample.dll --remote-console repl-hosting-sample
docker exec repl-hosting-remote dotnet /app/HostingRemoteSample.dll --remote-exec repl-hosting-sample health
```

Use that same executable command in a container administration tool's console, with an interactive TTY. It connects to the process already serving the browser. The image's exec-form `HEALTHCHECK` uses this pipe too; no HTTP probe tool is required. Its pipe name is the default shown above; update the health-check command if you customize the server's endpoint.

The optional build target runs protocol, adapter and independent Python interoperability tests under Linux:

```powershell
docker build --target tests -f samples/05-hosting-remote/Dockerfile .
```

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
