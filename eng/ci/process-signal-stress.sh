#!/usr/bin/env bash
set -euo pipefail
set -E
trap 'echo "Process-signal stress failed at line ${LINENO}" >&2' ERR

unit_iterations="${1:-50}"
integration_iterations="${2:-20}"
configuration="${CONFIGURATION:-Release}"

for value in "$unit_iterations" "$integration_iterations"; do
  if [[ ! "$value" =~ ^[1-9][0-9]*$ ]]; then
    echo "usage: $0 [unit-iterations] [integration-iterations]" >&2
    exit 2
  fi
done

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

log_file="$(mktemp)"
trap 'rm -f "$log_file"' EXIT

# Restore explicitly, then build without one. An incremental restore audits no projects, which trips
# the CI-only NuGet audit assertion in src/Directory.Solution.targets when the caller already restored.
dotnet restore src/Repl.slnx --force

dotnet build src/Repl.slnx \
  -c "$configuration" \
  -warnaserror \
  --no-restore \
  --nologo

run_stress() {
  local label="$1"
  local iterations="$2"
  local project="$3"
  local filter="$4"
  local minimum_tests="$5"

  for ((iteration = 1; iteration <= iterations; iteration++)); do
    if ! dotnet test --project "$project" \
      -c "$configuration" \
      --no-build \
      --no-restore \
      --no-ansi \
      --filter "$filter" \
      --minimum-expected-tests "$minimum_tests" >"$log_file" 2>&1; then
      echo "$label failed on iteration $iteration/$iterations" >&2
      cat "$log_file" >&2
      return 1
    fi
  done

  echo "$label: $iterations/$iterations iterations passed ($minimum_tests tests each)"
}

run_stress \
  "process-signal unit stress" \
  "$unit_iterations" \
  src/Repl.Tests/Repl.Tests.csproj \
  "FullyQualifiedName~Given_ProcessSignalCancellationScope" \
  25

run_stress \
  "process-signal integration stress" \
  "$integration_iterations" \
  src/Repl.IntegrationTests/Repl.IntegrationTests.csproj \
  "FullyQualifiedName~Given_ProcessSignals" \
  8
