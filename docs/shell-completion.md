# Shell Completion (Bash + PowerShell + Zsh + Fish + Nushell)

This page describes shell completion support in Repl Toolkit, including setup modes and install commands.

## Overview

Completion resolution is done by a bridge command:

```text
completion __complete --shell <bash|powershell|zsh|fish|nu> --line <input> --cursor <position>
```

The shell passes current line + cursor, and Repl returns candidates on `stdout` (one per line).
`completion __complete` is mapped in the regular command graph through the shell-completion module (CLI channel only).
The bridge route is marked as protocol passthrough, so repl suppresses banners and routes framework diagnostics to `stderr`.
The bridge handler writes candidates through `IReplIoContext.Output`, which remains bound to the protocol stream (`stdout`) in local CLI passthrough.

`IReplIoContext` is optional in general protocol commands:

- optional for local CLI commands that already use `Console.*` directly
- recommended when handlers need explicit stream injection, deterministic tests, or hosted-session compatibility

The module exposes a real `completion` context scope:

- `completion install`
- `completion uninstall`
- `completion status`
- `completion detect-shell`
- `completion __complete` (protocol bridge; hidden)

## Runtime setup modes

Shell completion behavior is configured through `ReplOptions.ShellCompletion`:

- `Enabled` (default: `true`)
- `SetupMode` (default: `Manual`)
- `PreferredShell` (optional override)
- `PromptOnce` (default: `true`)
- `StateFilePath` (optional)
- `BashProfilePath` / `PowerShellProfilePath` / `ZshProfilePath` / `FishProfilePath` / `NuProfilePath` (optional overrides)

`SetupMode` values:

- `Manual`: no automatic profile mutation. User runs install/uninstall commands.
- `Prompt`: interactive startup can propose installation once.
- `Auto`: interactive startup installs automatically when a supported shell is confidently detected.

`Prompt` and `Auto` apply only when entering interactive mode. Terminal one-shot commands never auto-install.

## User commands

The management surface is:

```text
completion install [--shell bash|powershell|zsh|fish|nu] [--force] [--silent]
completion uninstall [--shell bash|powershell|zsh|fish|nu] [--silent]
completion status
completion detect-shell
```

These commands are CLI-only (they are not available in interactive mode or hosted session mode).
They also require invoking the app through its own executable command head (the running process must match the app binary).

Structured output is supported via global output flags:

- `completion status --json`
- `completion detect-shell --output:json`
- `completion install --json`
- `completion uninstall --json`

Notes:

- `completion install` writes a managed block in the shell profile.
- `completion uninstall` removes only the managed block.
- `completion status` prints mode, detection, profile paths, profile existence, and install status.
- `completion detect-shell` prints detected shell and detection reason.
- for Nushell, a shared global dispatcher block is managed in addition to per-app blocks. If another app already manages it, use `--force` to merge.

## Status output (anonymized example)

Human output:

```text
Enabled                : True
Setup mode             : Manual
Detected shell         : powershell (env suggests PowerShell; parent process chain: <process-a> -> <process-b>)
Bash profile           : <home>/.bashrc
Bash profile exists    : True
Bash installed         : False
PowerShell profile     : <documents>/PowerShell/Microsoft.PowerShell_profile.ps1
PowerShell profile exists: True
PowerShell installed   : False
Zsh profile            : <home>/.zshrc
Zsh profile exists     : False
Zsh installed          : False
Fish profile           : <config>/fish/config.fish
Fish profile exists    : False
Fish installed         : False
Nushell profile        : <config>/nushell/config.nu
Nushell profile exists : False
Nushell installed      : False
```

JSON output includes these per-shell fields:

- `bashProfilePath`, `bashProfileExists`, `bashInstalled`
- `powerShellProfilePath`, `powerShellProfileExists`, `powerShellInstalled`
- `zshProfilePath`, `zshProfileExists`, `zshInstalled`
- `fishProfilePath`, `fishProfileExists`, `fishInstalled`
- `nuProfilePath`, `nuProfileExists`, `nuInstalled`

## Detection strategy

Shell detection is best-effort and uses weighted signals:

1. `PreferredShell` override (highest priority).
2. Environment variables (for example `BASH_VERSION`, `SHELL`, `PSModulePath`).
3. Parent/grand-parent process names as validation (`bash`, `zsh`, `fish`, `nu`, `nushell`, `pwsh`, `powershell`).

If signals are conflicting or weak, result is `unknown` (no auto-install in `Auto` mode).

## What completion returns (current scope)

- Command literals from the mapped graph.
- Static command options from handler parameters (resolved terminal routes).
- Static global options (`--help`, `--interactive`, `--no-interactive`, `--no-logo`, output aliases, `--output:<format>`).

Not included:

- Dynamic data values (contexts/arguments).
- `WithCompletion(...)` providers through shell completion.

## Managed profile blocks

Install/uninstall is idempotent through namespaced markers:

- `# >>> repl completion [appId=<app-id>;shell=<bash|powershell|zsh|fish|nu>] >>>`
- `# <<< repl completion [appId=<app-id>;shell=<bash|powershell|zsh|fish|nu>] <<<`

Update/remove targets only the block matching the current app and shell.

## Manual setup snippets

Bash:

```bash
_myapp_complete() {
  local line cursor
  local candidate
  line="$COMP_LINE"
  cursor="$COMP_POINT"
  COMPREPLY=()
  while IFS= read -r candidate; do
    COMPREPLY[${#COMPREPLY[@]}]="$candidate"
  done < <(myapp completion __complete --shell bash --line "$line" --cursor "$cursor" --no-interactive --no-logo)
}

complete -F _myapp_complete myapp
```

PowerShell:

```powershell
$__replCompletionCommandNames = @('myapp')
$__replCompleter = {
    param($wordToComplete, $commandAst, $cursorPosition)

    $invokedCommand = if ($commandAst.CommandElements.Count -gt 0 -and $commandAst.CommandElements[0] -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
        $commandAst.CommandElements[0].Value
    } else {
        'myapp'
    }

    & $invokedCommand completion __complete --shell powershell --line $commandAst.ToString() --cursor $cursorPosition --no-interactive --no-logo |
        ForEach-Object {
            [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
        }
}
if ((Get-Command Register-ArgumentCompleter).Parameters.ContainsKey('Native')) {
    Register-ArgumentCompleter -Native -CommandName $__replCompletionCommandNames -ScriptBlock $__replCompleter
} else {
    Register-ArgumentCompleter -CommandName $__replCompletionCommandNames -ScriptBlock $__replCompleter
}
```

Zsh:

```zsh
_myapp_complete() {
  local line cursor
  local candidate
  local -a reply
  line="$BUFFER"
  cursor=$((CURSOR > 0 ? CURSOR - 1 : 0))
  reply=()
  while IFS= read -r candidate; do
    reply+=("$candidate")
  done < <(myapp completion __complete --shell zsh --line "$line" --cursor "$cursor" --no-interactive --no-logo)
  if (( ${#reply[@]} > 0 )); then
    compadd -- "${reply[@]}"
  fi
}

compdef _myapp_complete myapp
```

Fish:

```fish
function _myapp_complete
  set -l line (commandline -p)
  set -l cursor (commandline -C)
  myapp completion __complete --shell fish --line "$line" --cursor "$cursor" --no-interactive --no-logo
end

complete -c myapp -f -a "(_myapp_complete)"
```

Nushell:

```nu
const __repl_completion_entries = [
  { appId: "myapp", command: "myapp" }
]
def _repl_nu_dispatch_completion [spans: list<string>] {
  if (($spans | length) == 0) {
    return []
  }

  let head = ($spans | get 0)
  let matches = ($__repl_completion_entries | where { |item| $item.command == $head })
  if (($matches | length) == 0) {
    return []
  }

  let entry = ($matches | get 0)
  let line = ($spans | str join ' ')
  let cursor = ($line | str length)
  (
    ^$entry.command completion __complete --shell nu --line $line --cursor $cursor --no-interactive --no-logo
    | lines
    | each { |line| { value: $line, description: "" } }
  )
}

$env.config = (
  $env.config
  | upsert completions.external.enable true
)
$env.config.completions.external.completer = { |spans| _repl_nu_dispatch_completion $spans }
```

## Compatibility notes

- PowerShell 7+ is recommended. In Windows PowerShell 5.1, native completion registration for external executables is limited and may not trigger reliably.
- Nushell uses one global `completions.external.completer`; Repl manages a shared dispatcher block to route completions per app command head.
