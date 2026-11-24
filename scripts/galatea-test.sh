#!/usr/bin/env bash
# galatea-test.sh (2026-11-18): the one local test command for a Galatea working tree. Prints one line per suite.
#
#   galatea-test.sh [--full | --impact | --csharp | --python] [--no-build] [--tree DIR]
#
#   --full     (default) build-all.sh --tests (text client + MCP -> app-staging, xUnit project), scene-mesher build +
#              --lod/--skin/--mem/--prim-selftest, xUnit suite (dotnet test --no-build), pytest: what CI runs
#   --impact   scripts/impact_tests.py: only the tests the diff vs origin/main touches (falls back to full when unsure)
#   --csharp   build + mesher selftests + xUnit   --python   pytest only   --no-build   skip build-all.sh (full/csharp)
#   --tree     working tree (default: the git toplevel of the current directory)
# pytest runs in parallel (pytest-xdist, -n ${GALATEA_TEST_WORKERS:-auto}) when xdist is installed, else serially.
# GALATEA_SLOW_TESTS=1 also runs the opt-in slow tier (real voice model; minutes). Neither CI nor the default run does.
# Sets PATH/DOTNET_ROOT to /home/box/.dotnet. Full logs go to /tmp/galatea-test-<tree>-<time>/ (path printed).
# Exit: 0 all passed, 1 a suite failed, 2 usage.
set -uo pipefail
MODE=full; BUILD=1; TREE=""
while (( $# )); do case "$1" in
  --full|--impact|--csharp|--python) MODE=${1#--};; --no-build) BUILD=0;;
  --tree) TREE=${2:?--tree needs a dir}; shift;;
  -h|--help) sed -n '2,14p' "$0"; exit 0;;
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
  NOBUILD=()
  if (( BUILD )); then   # one build of everything; dotnet test then runs with --no-build (no second compile)
    if bash textclient/build-all.sh --tests >"$LOGS/build.log" 2>&1; then grep -E 'build OK|check OK' "$LOGS/build.log" | sed 's/^/  /'; echo "build: OK"; NOBUILD=(--no-build)
    else echo "build: FAIL"; tail -25 "$LOGS/build.log"; exit 1; fi
  fi
  # scene-mesher pure selftests, as in CI (built against this tree's LibreMetaverse checkout, never the box's copy)
  if [[ -d textclient/src-libremetaverse ]]; then
    M=vision/scene-mesher/SceneMesher.csproj; LMVP=-p:LMV="$TREE/textclient/src-libremetaverse"
    if dotnet build "$M" -c Release "$LMVP" --nologo -v q >"$LOGS/mesher.log" 2>&1; then
      MD=$(dotnet msbuild "$M" -nologo -getProperty:TargetDir -p:Configuration=Release "$LMVP" 2>/dev/null | tail -1); m=0
      for st in --lod-selftest --skin-selftest --mem-selftest --prim-selftest; do
        dotnet "${MD}SceneMesher.dll" "$st" >>"$LOGS/mesher.log" 2>&1 || { m=1; echo "  mesher $st failed"; }
      done
      if (( m )); then rc=1; echo "mesher selftests: FAIL (log $LOGS/mesher.log)"; else echo "mesher selftests: PASS (lod, skin, mem, prim)"; fi
    else rc=1; echo "mesher selftests: FAIL (build)"; grep -E ': error ' "$LOGS/mesher.log" | sort -u | head -10; fi
  else echo "mesher selftests: SKIP (no textclient/src-libremetaverse; run build-all.sh first)"; fi
  export GALATAY_TEXT_EXE="$TREE/textclient/app-staging/galatay-text"
  [[ -x $GALATAY_TEXT_EXE ]] || { echo "xUnit: no $GALATAY_TEXT_EXE (drop --no-build)"; exit 1; }
  dotnet test textclient/GalatayText.Tests/GalatayText.Tests.csproj -c Release --nologo "${NOBUILD[@]}" >"$LOGS/xunit.log" 2>&1; x=$?
  sum=$(grep -E '(Passed|Failed)!' "$LOGS/xunit.log" | tail -1 | sed -E 's/^ +//')
  if (( x )); then rc=1; echo "xUnit: FAIL (rc $x) ${sum:-no summary}"; grep -E '^\s+Failed ' "$LOGS/xunit.log" | head -20; else echo "xUnit: PASS $sum"; fi
fi
if [[ $MODE == full || $MODE == python ]]; then
  XD=(); "$PY" -c 'import xdist' 2>/dev/null && XD=(-n "${GALATEA_TEST_WORKERS:-auto}")
  (( ${#XD[@]} )) || echo "  (pytest-xdist not installed for $PY: running serially; pip install -r requirements-ci.txt)"
  "$PY" -m pytest -q -o addopts= "${XD[@]}" >"$LOGS/pytest.log" 2>&1; p=$?
  sum=$(grep -E '[0-9]+ (passed|failed)' "$LOGS/pytest.log" | tail -1)
  if (( p )); then rc=1; echo "pytest: FAIL (rc $p) ${sum:-no summary}"; grep -E '^(FAILED|ERROR)' "$LOGS/pytest.log" | head -20; else echo "pytest: PASS $sum"; fi
fi
exit $rc
