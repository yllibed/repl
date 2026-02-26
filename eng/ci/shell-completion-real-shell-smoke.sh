#!/usr/bin/env bash
set -euo pipefail
set -E
trap 'echo "Shell completion smoke failed at line ${LINENO}" >&2' ERR

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <test-host-path>" >&2
  exit 2
fi

host_path="$1"
if [[ ! -x "$host_path" ]]; then
  echo "test host executable not found or not executable: $host_path" >&2
  exit 2
fi

command_name="$(basename "$host_path")"
host_dir="$(cd "$(dirname "$host_path")" && pwd)"
workspace="$(mktemp -d)"
trap 'rm -rf "$workspace"' EXIT

export PATH="$host_dir:$PATH"
export REPL_CMD_NAME="$command_name"
export REPL_TEST_SCENARIO="setup"
export REPL_TEST_SHELL_COMPLETION_STATE_FILE_PATH="$workspace/shell-completion-state.txt"

run_bash_smoke() {
  echo "[smoke] bash"
  local profile_path="$workspace/bash/.bashrc"
  mkdir -p "$(dirname "$profile_path")"
  export REPL_TEST_SHELL_COMPLETION_BASH_PROFILE_PATH="$profile_path"

  "$command_name" completion install --shell bash --force --no-logo

  REPL_BASH_PROFILE="$profile_path" bash --noprofile --norc -c '
set -euo pipefail
source "$REPL_BASH_PROFILE"
fn="$(complete -p "$REPL_CMD_NAME" | sed -E "s/.*-F ([^ ]+).*/\1/")"
[[ -n "$fn" ]]
COMP_LINE="$REPL_CMD_NAME c"
COMP_POINT=${#COMP_LINE}
COMPREPLY=()
"$fn"
printf "%s\n" "${COMPREPLY[@]}" | grep -Fx "contact" >/dev/null
'
}

run_zsh_smoke() {
  echo "[smoke] zsh"
  local profile_path="$workspace/zsh/.zshrc"
  mkdir -p "$(dirname "$profile_path")"
  export REPL_TEST_SHELL_COMPLETION_ZSH_PROFILE_PATH="$profile_path"

  "$command_name" completion install --shell zsh --force --no-logo

  REPL_ZSH_PROFILE="$profile_path" REPL_ZSH_CAPTURE="$workspace/zsh/candidates.txt" zsh -f -c '
set -eu
set -o pipefail
compdef() { :; }
compadd() {
  shift
  printf "%s\n" "$@" > "$REPL_ZSH_CAPTURE"
}
source "$REPL_ZSH_PROFILE"
fn="$(sed -n -E "s/^([_[:alnum:]_]+_complete)\(\) \{/\1/p" "$REPL_ZSH_PROFILE" | head -n 1)"
[[ -n "$fn" ]]
BUFFER="$REPL_CMD_NAME c"
CURSOR=${#BUFFER}
"$fn"
grep -Fx "contact" "$REPL_ZSH_CAPTURE" >/dev/null
'
}

run_fish_smoke() {
  echo "[smoke] fish"
  local profile_path="$workspace/fish/config.fish"
  mkdir -p "$(dirname "$profile_path")"
  export REPL_TEST_SHELL_COMPLETION_FISH_PROFILE_PATH="$profile_path"

  "$command_name" completion install --shell fish --force --no-logo

  REPL_FISH_PROFILE="$profile_path" fish -c '
function commandline
  if test (count $argv) -eq 1
    switch $argv[1]
      case -p
        printf "%s" "$REPL_CMD_NAME c"
        return 0
      case -C
        string length -- "$REPL_CMD_NAME c"
        return 0
    end
  end

  builtin commandline $argv
end
source $REPL_FISH_PROFILE
set -l matches (complete --do-complete "$REPL_CMD_NAME c")
printf "%s\n" $matches | string match -r "^contact" >/dev/null
'
}

run_powershell_smoke() {
  echo "[smoke] powershell"
  local profile_path="$workspace/powershell/Microsoft.PowerShell_profile.ps1"
  mkdir -p "$(dirname "$profile_path")"
  export REPL_TEST_SHELL_COMPLETION_POWERSHELL_PROFILE_PATH="$profile_path"

  "$command_name" completion install --shell powershell --force --no-logo

  REPL_PWSH_PROFILE="$profile_path" pwsh -NoLogo -NoProfile -File - <<'PWSH'
. $env:REPL_PWSH_PROFILE
$line = "$env:REPL_CMD_NAME c"
$result = TabExpansion2 -InputScript $line -CursorColumn $line.Length
if (-not ($result.CompletionMatches | Where-Object { $_.CompletionText -eq 'contact' })) {
  throw 'PowerShell completion did not return expected candidate.'
}
PWSH
}

run_nu_smoke() {
  echo "[smoke] nushell"
  local profile_path="$workspace/nu/config.nu"
  mkdir -p "$(dirname "$profile_path")"
  export REPL_TEST_SHELL_COMPLETION_NU_PROFILE_PATH="$profile_path"

  "$command_name" completion install --shell nushell --force --no-logo

  REPL_NU_PROFILE="$profile_path" nu -c '
source $env.REPL_NU_PROFILE
let completions = (do $env.config.completions.external.completer [$env.REPL_CMD_NAME "c"])
if (($completions | where value == "contact" | length) == 0) {
  exit 1
}
'
}

run_bash_smoke
run_zsh_smoke
run_fish_smoke
run_powershell_smoke
run_nu_smoke

echo "Shell completion smoke checks passed for bash, zsh, fish, powershell, and nushell."
