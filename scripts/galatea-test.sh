#!/usr/bin/env bash
# galatea-test.sh (2026-11-18): the one local test command for a Galatea working tree. Prints one line per suite.
#
#   galatea-test.sh [--full | --impact | --csharp | --python] [--no-build] [--tree DIR]
#
#   --full     (default) build-all.sh (text client + MCP -> app-staging), xUnit suite, pytest -q: what CI runs
#   --impact   scripts/impact_tests.py: only the tests the diff vs origin/main touches (falls back to full when unsure)
#   --csharp   build + xUnit only        --python   pytest -q only        --no-build   skip build-all.sh (full/csharp)
#   --tree     working tree (default: the git toplevel of the current directory)
# Sets PATH/DOTNET_ROOT to /home/box/.dotnet. Full logs go to /tmp/galatea-test-<tree>-<time>/ (path printed).
# Exit: 0 all passed, 1 a suite failed, 2 usage.
set -uo pipefail
MODE=full; BUILD=1; TREE=""
while (( $# )); do case "$1" in
  --full|--impact|--csharp|--python) MODE=${1#--};; --no-build) BUILD=0;;
  --tree) TREE=${2:?--tree needs a dir}; shift;;
  -h|--help) sed -n '2,12p' "$0"; exit 0;;
  *) echo "usage: galatea-test.sh [--full|--impact|--csharp|--python] [--no-build] [--tree DIR]"; exit 2;; esac; shift; done
TREE=$(cd "${TREE:-.}" && git rev-parse --show-toplevel 2>/dev/null) || { echo "not inside a git working tree"; exit 2; }
[[ -f $TREE/textclient/GalatayText.Tests/GalatayText.Tests.csproj ]] || { echo "$TREE is not a Galatea tree"; exit 2; }
export PATH="/home/box/.dotnet:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-/home/box/.dotnet}" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
PY=${GALATEA_TEST_PYTHON:-python3}
"$PY" -c 'import pytest' 2>/dev/null || { [[ -x /workspace/ptvenv/bin/python ]] && PY=/workspace/ptvenv/bin/python; }
LOGS=/tmp/galatea-test-$(basename "$TREE")-$(date +%Y%m%d-%H%M%S); mkdir -p "$LOGS"
cd "$TREE"; rc=0
echo "tree $TREE @ $(git rev-parse --short HEAD) ($(git rev-parse --abbrev-ref HEAD)); logs $LOGS"

if [[ $MODE == impact ]]; then
  IMPACT_PYTHON=$PY "$PY" scripts/impact_tests.py >"$LOGS/impact.log" 2>&1; rc=$?
  grep -E 'Passed!|Failed!|passed|failed|error|selected|full suite|nothing to run' "$LOGS/impact.log" | tail -8
  echo "impact tests: $([[ $rc == 0 ]] && echo PASS || echo "FAIL (rc $rc)")"; exit $(( rc ? 1 : 0 ))
fi
if [[ $MODE == full || $MODE == csharp ]]; then
  if (( BUILD )); then
    if bash textclient/build-all.sh >"$LOGS/build.log" 2>&1; then grep -E 'build OK|check OK' "$LOGS/build.log" | sed 's/^/  /'; echo "build: OK"
    else echo "build: FAIL"; tail -25 "$LOGS/build.log"; exit 1; fi
  fi
  export GALATAY_TEXT_EXE="$TREE/textclient/app-staging/galatay-text"
  [[ -x $GALATAY_TEXT_EXE ]] || { echo "xUnit: no $GALATAY_TEXT_EXE (drop --no-build)"; exit 1; }
  dotnet test textclient/GalatayText.Tests/GalatayText.Tests.csproj -c Release --nologo >"$LOGS/xunit.log" 2>&1; x=$?
  sum=$(grep -E '(Passed|Failed)!' "$LOGS/xunit.log" | tail -1 | sed -E 's/^ +//')
  if (( x )); then rc=1; echo "xUnit: FAIL (rc $x) ${sum:-no summary}"; grep -E '^\s+Failed ' "$LOGS/xunit.log" | head -20; else echo "xUnit: PASS $sum"; fi
fi
if [[ $MODE == full || $MODE == python ]]; then
  "$PY" -m pytest -q -o addopts= >"$LOGS/pytest.log" 2>&1; p=$?
  sum=$(grep -E '[0-9]+ (passed|failed)' "$LOGS/pytest.log" | tail -1)
  if (( p )); then rc=1; echo "pytest: FAIL (rc $p) ${sum:-no summary}"; grep -E '^(FAILED|ERROR)' "$LOGS/pytest.log" | head -20; else echo "pytest: PASS $sum"; fi
fi
exit $rc
