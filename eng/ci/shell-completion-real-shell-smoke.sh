#!/usr/bin/env bash
set -euo pipefail

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
  local profile_path="$workspace/bash/.bashrc"
  mkdir -p "$(dirname "$profile_path")"
  export REPL_TEST_SHELL_COMPLETION_BASH_PROFILE_PATH="$profile_path"

  "$host_path" completion install --shell bash --force --silent --no-logo

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
  local profile_path="$workspace/zsh/.zshrc"
  mkdir -p "$(dirname "$profile_path")"
  export REPL_TEST_SHELL_COMPLETION_ZSH_PROFILE_PATH="$profile_path"

  "$host_path" completion install --shell zsh --force --silent --no-logo

  REPL_ZSH_PROFILE="$profile_path" REPL_ZSH_CAPTURE="$workspace/zsh/candidates.txt" zsh -f -c '
set -eu
set -o pipefail
autoload -Uz compinit
compinit
source "$REPL_ZSH_PROFILE"
fn="$(sed -n -E "s/^([_[:alnum:]_]+_complete)\(\) \{/\1/p" "$REPL_ZSH_PROFILE" | head -n 1)"
[[ -n "$fn" ]]
compadd() {
  shift
  printf "%s\n" "$@" > "$REPL_ZSH_CAPTURE"
}
BUFFER="$REPL_CMD_NAME c"
CURSOR=${#BUFFER}
"$fn"
grep -Fx "contact" "$REPL_ZSH_CAPTURE" >/dev/null
'
}

run_fish_smoke() {
  local profile_path="$workspace/fish/config.fish"
  mkdir -p "$(dirname "$profile_path")"
  export REPL_TEST_SHELL_COMPLETION_FISH_PROFILE_PATH="$profile_path"

  "$host_path" completion install --shell fish --force --silent --no-logo

  REPL_FISH_PROFILE="$profile_path" fish -c '
source $REPL_FISH_PROFILE
set -l matches (complete --do-complete "$REPL_CMD_NAME c")
printf "%s\n" $matches | string match -r "^contact(\t|$)" >/dev/null
'
}

run_powershell_smoke() {
  local profile_path="$workspace/powershell/Microsoft.PowerShell_profile.ps1"
  mkdir -p "$(dirname "$profile_path")"
  export REPL_TEST_SHELL_COMPLETION_POWERSHELL_PROFILE_PATH="$profile_path"

  "$host_path" completion install --shell powershell --force --silent --no-logo

  REPL_PWSH_PROFILE="$profile_path" pwsh -NoLogo -NoProfile -Command @'
. $env:REPL_PWSH_PROFILE
$line = "$env:REPL_CMD_NAME c"
$result = TabExpansion2 -InputScript $line -CursorColumn $line.Length
if (-not ($result.CompletionMatches | Where-Object { $_.CompletionText -eq 'contact' })) {
  throw 'PowerShell completion did not return expected candidate.'
}
'@
}

run_nu_smoke() {
  local profile_path="$workspace/nu/config.nu"
  mkdir -p "$(dirname "$profile_path")"
  export REPL_TEST_SHELL_COMPLETION_NU_PROFILE_PATH="$profile_path"

  "$host_path" completion install --shell nushell --force --silent --no-logo

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
