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

complete_line() {
  COMP_LINE="$1"
  COMP_POINT=${#COMP_LINE}
  COMPREPLY=()
  "$fn"
}

complete_line "$REPL_CMD_NAME c"
printf "%s\n" "${COMPREPLY[@]}" | grep -Fx "contact" >/dev/null

# Shell-scoped value provider: the candidates must be safe SHELL SYNTAX.
complete_line "$REPL_CMD_NAME deploy "

# 1) A command-substitution value is offered single-quoted, and parsing the
#    accepted candidate the way the shell would binds it LITERALLY — the
#    substitution must not run (regression for the quoting-injection report).
inj="$(printf "%s\n" "${COMPREPLY[@]}" | grep -F "PWNED")"
[[ "$inj" == "'"'"'\$(printf PWNED)'"'"'" ]]
eval "set -- $inj"
[[ $# -eq 1 && "$1" == "\$(printf PWNED)" ]]

# 2) A value with whitespace round-trips as ONE argument.
ny="$(printf "%s\n" "${COMPREPLY[@]}" | grep -F "New York")"
eval "set -- $ny"
[[ $# -eq 1 && "$1" == "New York" ]]

# 3) A completion requested from inside an OPEN double quote yields no
#    provider value (the bridge cannot safely reshape the open quote).
complete_line "$REPL_CMD_NAME deploy \"Ne"
if printf "%s\n" "${COMPREPLY[@]}" | grep -F "PWNED" >/dev/null; then
  echo "provider value leaked into an open-quoted context" >&2
  exit 1
fi

# 4) An ESCAPED quote delimiter keeps the shell inside the open quote; a
#    naive delimiter count would think it closed and re-offer values.
complete_line "$REPL_CMD_NAME deploy \"a\\"
if printf "%s\n" "${COMPREPLY[@]}" | grep -F "PWNED" >/dev/null; then
  echo "provider value leaked past an escaped quote delimiter" >&2
  exit 1
fi
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
  local check_script_path="$workspace/powershell/smoke-check.ps1"
  mkdir -p "$(dirname "$profile_path")"
  export REPL_TEST_SHELL_COMPLETION_POWERSHELL_PROFILE_PATH="$profile_path"

  "$command_name" completion install --shell powershell --force --no-logo

  cat > "$check_script_path" <<'PWSH'
. $env:REPL_PWSH_PROFILE
$cmd = $env:REPL_CMD_NAME

$line = "$cmd c"
$result = TabExpansion2 -InputScript $line -CursorColumn $line.Length
if (-not ($result.CompletionMatches | Where-Object { $_.CompletionText -eq 'contact' })) {
  throw 'PowerShell completion did not return expected candidate.'
}

# Shell-scoped value provider: candidates must be literal PowerShell data.
$line = "$cmd deploy "
$result = TabExpansion2 -InputScript $line -CursorColumn $line.Length
$texts = $result.CompletionMatches | ForEach-Object { $_.CompletionText }

# A subexpression value is single-quoted (literal in PowerShell) and, when the
# accepted candidate is parsed and run, binds verbatim — it must not evaluate.
$inj = $texts | Where-Object { $_ -like '*PWNED*' }
if ($inj -ne "'`$(printf PWNED)'") {
  throw "PowerShell candidate was not single-quoted literal data: $inj"
}
$out = Invoke-Expression "$cmd deploy $inj --no-logo"
if ($out -ne '$(printf PWNED)') {
  throw "PowerShell accept-to-argv did not bind the literal value: $out"
}

# A value with whitespace round-trips as one argument.
$ny = $texts | Where-Object { $_ -like '*New York*' }
$out = Invoke-Expression "$cmd deploy $ny --no-logo"
if ($out -ne 'New York') {
  throw "PowerShell accept-to-argv split a spaced value: $out"
}
PWSH
  REPL_PWSH_PROFILE="$profile_path" pwsh -NoLogo -NoProfile -File "$check_script_path"
}

run_nu_smoke() {
  echo "[smoke] nushell"
  local profile_path="$workspace/nu/config.nu"
  mkdir -p "$(dirname "$profile_path")"
  export REPL_TEST_SHELL_COMPLETION_NU_PROFILE_PATH="$profile_path"

  "$command_name" completion install --shell nushell --force --no-logo

  nu -c "source \"$profile_path\"; let completions = (do \$env.config.completions.external.completer [\$env.REPL_CMD_NAME \"c\"]); if ((\$completions | where value == \"contact\" | length) == 0) { exit 1 }"
}

run_bash_smoke
run_zsh_smoke
run_fish_smoke
run_powershell_smoke
run_nu_smoke

echo "Shell completion smoke checks passed for bash, zsh, fish, powershell, and nushell."
