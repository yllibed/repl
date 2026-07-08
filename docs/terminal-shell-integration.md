# Terminal Shell Integration

Modern terminals understand semantic marks that delimit the prompt, the user input, and the command output. When those marks are present, the terminal can offer command navigation (jump between commands), command-aware selection and copy, success/failure decorations in the gutter, and sticky command headers.

Repl owns the prompt and the command lifecycle in interactive mode, so it can emit those marks itself — no shell script hooks required. The feature is opt-in:

```csharp
var app = ReplApp.Create()
    .UseTerminalIntegration();          // ShellIntegration = Auto by default

// or explicitly:
app.UseTerminalIntegration(options =>
{
    options.ShellIntegration = ShellIntegrationMode.Always;
});
```

Raw escape sequences are never exposed to command handlers; Repl chooses the protocol and emits the marks around its own prompt loop.

## What gets emitted

In interactive REPL mode, each prompt cycle is delimited with the FinalTerm semantic sequence (OSC 133), or the VS Code shell-integration sequence (OSC 633) when the VS Code integrated terminal is detected:

| Moment | Mark |
|---|---|
| Before the prompt text | `A` (prompt start) |
| After the prompt text, before input | `B` (input start) |
| After a committed line (VS Code only) | `E;<command line>` (command-line report) |
| Right before command execution | `C` (output start) |
| After the command completes | `D;<exit code>` (command end) |

Exit codes follow shell conventions: `0` for success, `1` for errors (failed results, unknown commands, validation failures), and `130` (128+SIGINT) when a command is cancelled with Ctrl+C. The interruption decoration is intentionally broad: a handler that throws `OperationCanceledException` for any other reason (its own timeout, a linked token) also reports `130`, because the loop cannot tell who requested the cancellation. An abandoned cycle — Escape at the prompt, an empty line, or end of input — reports `D` without an exit code, the FinalTerm "command aborted" form.

The VS Code `E` mark reports the exact committed command line (with protocol escaping), which makes VS Code's command detection independent of what is visible on screen. Note the privacy implication: whatever was typed at the prompt — including secrets passed as command arguments — is transmitted verbatim to the terminal, which may persist it for command detection and history. This mirrors what VS Code's own shell integration does for regular shells; if commands take secrets, prefer prompting for them interactively instead of passing them as arguments.

CLI one-shot mode emits no marks: Repl does not own the surrounding shell prompt there, and fake prompt markers would corrupt the host shell's own command navigation. Nested interaction prompts (`IReplInteractionChannel` questions asked *during* a command) emit no marks either — they are not shell prompts.

## Modes

`ShellIntegrationMode` mirrors the existing `AdvancedProgressMode` semantics:

- `Auto` (default) — emit when the terminal is known to render marks. For a hosted session, only what the remote client advertised counts: `TerminalCapabilities.ShellIntegrationMarks` (usually inferred from its reported terminal identity). For the local console, the environment identifies Windows Terminal (`WT_SESSION`), VS Code (`TERM_PROGRAM=vscode`), or WezTerm (`TERM_PROGRAM=WezTerm`); multiplexers (tmux, GNU screen) stay off because mark positioning is unreliable through panes. The server's own environment never enables marks for remote clients.
- `Always` — emit whenever the structural gates allow it (see below). Useful for terminals that render marks but are not auto-detected, such as iTerm2 reached over SSH.
- `Never` — never emit.

Regardless of mode, marks are never written when:

- output is redirected and no hosted session is active;
- ANSI output is disabled (`NO_COLOR`, `TERM=dumb`, explicit `AnsiMode.Never`, ...);
- a command is streaming raw protocol bytes (protocol passthrough, including MCP stdio).

## Protocol-passthrough commands

A command marked `.AsProtocolPassthrough()` turns the output stream into a raw protocol channel (MCP stdio, a completion payload, a file transfer). No mark may sit inside that stream. Because the prompt marks `A` and `B` are written *around the prompt* — before the committed line is known — they still precede such a command; but once the input resolves to a passthrough route, no `E`, `C`, or `D` is emitted, and the cycle is abandoned. The next prompt's `A` implicitly closes the abandoned segment on the terminal side. This holds whatever the command's exit code: a passthrough handler may emit bytes and then fail, so an exit code can never prove the payload never started.

An input that only *looks* like it targets a passthrough route but does not actually stream a payload keeps the normal lifecycle: an ambient command sharing the route's token (for example `help`, `history`, or a custom ambient), or `<route> --help` (which only renders help), all get `C` and `D` as usual.

## Disabling marks

- **Per app**: `options.ShellIntegration = ShellIntegrationMode.Never` (or simply never calling `UseTerminalIntegration`).
- **Per run, by the end user**: `NO_COLOR=1` or `TERM=dumb` disables marks — but as collateral of disabling all ANSI styling, since marks ride the same ANSI gate (`CLICOLOR_FORCE=1` overrides `TERM=dumb`, matching styled output). There is no mark-only runtime switch; on a **local console**, if a terminal is misdetected and shows raw `]133;…`, `NO_COLOR` is the escape hatch. For **hosted sessions** these variables live in the *server* process environment: a remote user cannot set them, and setting them server-side disables ANSI for every connected session — a misdetected hosted client is better fixed by correcting the identity/capabilities it advertises (or `ShellIntegrationMode.Never` app-side).

## Backend selection

The generic backend is OSC 133, understood by Windows Terminal, WezTerm, iTerm2, Ghostty, and others. When the VS Code integrated terminal is detected — `TERM_PROGRAM=vscode` for the local console, or a `vscode` terminal identity reported by the hosted session's client — Repl switches to the OSC 633 dialect and additionally reports the command line with `E`. Backend selection follows the same session boundary as `Auto`: the server's environment never picks the dialect for a remote client.

The 633 backend also declares two properties before the first prompt, like VS Code's own shell scripts do: `633;P;Prompt=<text>` (the prompt text, re-declared when scope navigation changes it) and, on a local Windows console only, `633;P;IsWindows=True`. The latter switches VS Code's command detection to its ConPTY-compensating heuristics — without it, the gutter decoration of the first command can be misplaced because ConPTY rewrites the byte stream at process start. Hosted transports deliver bytes verbatim, so they never declare `IsWindows`.

Because a Repl app is usually *launched from* an integrated shell, that shell's own command (the app process) is still open from the terminal's point of view when the first prompt renders, and VS Code would anchor the first prompt at that command's stale end position — the first gutter decoration then lands on the app banner. The 633 backend therefore opens the session with a lone `D` (no exit code, the "aborted" form: the outer command's exit code is unknowable from inside it) before the very first `A`, closing the outer command at the true cursor position. This is a nested-shell handshake regular shells don't need; it is harmless when nothing was open.

ConEmu is deliberately excluded from `Auto`: it renders OSC 9;4 progress but not FinalTerm marks.

## Hosted sessions

Hosted sessions (WebSocket, Telnet) receive marks when their reported terminal identity infers `TerminalCapabilities.ShellIntegrationMarks` (for example `Windows Terminal`, `wezterm`, `vscode`) or when the host sets the flag explicitly through `TerminalSessionOverrides`. See [Terminal Metadata](terminal-metadata.md).

## Troubleshooting

Ask the running app first: `IReplSessionInfo.ShellIntegrationStatus` reports the detection outcome for the current prompt cycle — the active dialect (`"OSC 133"`, `"OSC 633 (VS Code)"`) or `"off (<gate>)"` naming the gate that disabled emission. A three-line debug command surfaces it:

```csharp
app.Map("terminal", (IReplSessionInfo session) =>
    session.ShellIntegrationStatus ?? "no prompt cycle yet");
```

The decision is also deterministic, so it can be walked by hand: gates are evaluated in a fixed order and the first failing gate decides. In order: integration configured → not in protocol passthrough → ANSI capable → output not redirected (local only) → mode (`Always`/`Never`) → capability advertised (hosted) or terminal recognized (local). When marks misbehave, walk that chain — the first gate that fails explains the symptom:

| Symptom | Gate to check |
|---|---|
| Marks emitted but nothing visible in Windows Terminal | Terminal-side setup: unlike VS Code, Windows Terminal (≥ 1.21) exposes mark features only through settings — `"showMarksOnScrollbar": true` on the profile for scrollbar pips, and `scrollToMark` actions bound to keys (e.g. Ctrl+Up/Down) for command navigation; neither is on by default. |
| Raw `]133;…` / `]633;…` text on screen | The terminal does not render marks. Set `ShellIntegrationMode.Never`, or `NO_COLOR=1` as a local end-user escape hatch; for a hosted client, fix the identity/capabilities it advertises (server-side `NO_COLOR` affects every session — see Disabling marks). |
| No marks at all (expected some) | `ShellIntegration` mode (`Never`?); then `UseTerminalIntegration` actually called?; then the ANSI gate (`NO_COLOR`, `TERM=dumb`, `AnsiMode.Never`, redirected output with no hosted session). |
| No marks under Auto specifically | Detection: locally `WT_SESSION`/`TERM_PROGRAM`; under tmux/screen Auto stays off; for a hosted client, the advertised `ShellIntegrationMarks` capability. |
| Command navigation works but wrong dialect | Backend selection: OSC 633 only when VS Code is detected (`TERM_PROGRAM=vscode` locally or a `vscode` hosted identity), else OSC 133. |
| Marks missing only around one command | That command is protocol passthrough (see above) — this is intended. |

## See Also

- [Interactive Loop](interactive-loop.md) — where the marks sit in the prompt cycle
- [Progress](progress.md#advanced-terminal-progress) — the OSC 9;4 progress integration
- [Terminal Metadata](terminal-metadata.md) — capability flags and how sessions advertise them
- [Configuration Reference](configuration-reference.md) — all options
