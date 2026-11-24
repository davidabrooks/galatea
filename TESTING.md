# Testing Galatea

Automated tests run on every pull request and every push to `main` via GitHub Actions:
[`.github/workflows/ci.yml`](.github/workflows/ci.yml) (two jobs: C# and Python). Locally, `scripts/galatea-test.sh` runs
the same steps (see below and `docs/box-scripts.md`).

Size (2026-11-23): about 2,060 xUnit tests and 420 pytest tests. Full local run on the box: about 45 s
(build 13 s, mesher selftests, xUnit 10 s, pytest 7 s in parallel); it was 104 s before the build and parallel changes.

**Nothing in CI logs in to Second Life or needs secrets.**

## Quick start (local)

### 1. LibreMetaverse (once)

```bash
bash scripts/ci-setup-libremetaverse.sh
```

Clones [cinderblocks/libremetaverse](https://github.com/cinderblocks/libremetaverse) into `textclient/src-libremetaverse` (gitignored) and applies the galatea patches (`libremetaverse-*.patch`).

### 2. Build the text client

```bash
bash textclient/build-all.sh --tests
```

Produces `textclient/app-staging/galatay-text` (and `mcp-app-staging/`); `--tests` also builds the xUnit project. Each
project builds into its normal `bin/Release` output, which is then copied into `*-staging`: an `-o` output override made
every later build recompile LibreMetaverse and the client again (about 20 s each).

### 3. C# tests (xUnit wrapping offline selftests)

```bash
export GALATAY_TEXT_EXE="$PWD/textclient/app-staging/galatay-text"
dotnet test textclient/GalatayText.Tests/GalatayText.Tests.csproj -c Release --no-build   # drop --no-build without step 2's --tests
```

xUnit runs test classes in parallel. Classes that change process-wide state (environment variables, `Program.*Override`
hooks, static fields such as the running roam) carry `[Collection(ProcessStateCollection.Name)]`
(`GalatayText.Tests/ProcessStateCollection.cs`): that collection never runs alongside anything else. Add new such classes to it.

These invoke the same `--*-selftest` entry points as the binary (pose, pose-keeper, chat-guard, imguard, attach-move, detach-cof, follow-door, exp, worn, voice-wake, bugfix). No login.

You can also run one selftest directly:

```bash
./textclient/app-staging/galatay-text --pose-selftest
./textclient/app-staging/galatay-text --chat-guard-selftest
./textclient/app-staging/galatay-text --imguard-selftest
# …
```

### 4. scene-mesher selftests

```bash
dotnet build vision/scene-mesher/SceneMesher.csproj -c Release -p:LMV="$PWD/textclient/src-libremetaverse"
for t in --lod-selftest --skin-selftest --mem-selftest --prim-selftest; do dotnet vision/scene-mesher/bin/Release/net10.0/SceneMesher.dll $t; done
```

### 5. Python tests (pytest)

```bash
pip install -r requirements-ci.txt     # includes pytest-xdist
pytest -q -n auto                      # parallel; plain `pytest -q` runs serially
```

Covers `vision/tests` (look mesher args, crowd placeholders on a small synthetic export in
`vision/tests/fixtures/crowd-placeholders/`, which runs wherever SceneMesher is built) and `textclient/galatay-voice/tests`
(SDP munge / fake server smoke). Tests must not depend on order or shared files, since xdist runs them in several processes.

### Slow tier (opt-in)

Tests that need the real voice venv and speech model (minutes each) are marked `@pytest.mark.slow` (pytest, `conftest.py`)
or `[SlowFact]` (xUnit, `GalatayText.Tests/SlowFactAttribute.cs`). They are skipped unless `GALATEA_SLOW_TESTS=1`
(`pytest --run-slow` also works). Neither CI nor the default `galatea-test.sh` run them:

```bash
GALATEA_SLOW_TESTS=1 scripts/galatea-test.sh
```

One command for all of the above on the box: `scripts/galatea-test.sh` (see `docs/box-scripts.md`).

## Run only the impacted tests (local)

```bash
python3 scripts/impact_tests.py --dry-run     # what would run, and why
python3 scripts/impact_tests.py               # run it (diff vs origin/main + uncommitted + untracked)
python3 scripts/impact_tests.py --full        # everything CI runs
python3 scripts/impact_tests.py --commit <sha> --dry-run   # analyse a past commit or merge
```

The full xUnit suite is ~2,000 tests and a full local run takes about 45 s; most changes touch a handful of them. The script tokenises every C# file under `textclient/`, splits it into members, picks the members whose lines the diff
changed, follows who uses them (names, narrowed by `Type.member`, private access, declared receiver type and call arity)
and runs only those `[Fact]`/`[Theory]` methods with `dotnet test --filter`. `--*-selftest` flags in `Program.Main` are
tracked too, so the `SelftestCliTests` theory runs only the flags whose code changed. Routes JSON, scripts and Python files map to the code and tests that
name them; Python changes run the pytest files that mention them (`IMPACT_PYTHON=/path/to/python` if pytest lives in a venv);
docs-only changes run nothing. `dotnet test` compiles the client and tests; `build-all.sh` (client + MCP + fresh binary) also runs when a selected test uses the built binary (selftests) or when no test covers a changed source, so changed code is never left uncompiled (the MCP-only build is otherwise left to CI).

It runs the **full suite** whenever it cannot be sure: project/build/CI files (`*.csproj`, `build-all.sh`, patches,
`.github/`, `pytest.ini`), C# it cannot parse, a changed file that nothing references, or a selection above 60% of all tests.
**CI is unchanged and still runs everything** on every PR and push to `main`; this is only a local shortcut. Blind spots:
reflection, names built at runtime, behaviour that depends on a base class or interface only (a `Type.member` call through
a base type is not followed). Tests for the mapper: `textclient/tests/test_impact_tests.py` (part of `pytest -q`).

## PR backfill map

| PR(s) | Covered by |
|------|------------|
| #33–#37 look / mesher / alpha | `vision/tests`, optional crowd placeholder |
| #38 wander hold + webhook retry | `--bugfix-selftest`, `PrBackfillPureTests` (Webhook.Due/Retriable/CapExempt) |
| #41 detach COF | `--detach-cof-selftest` |
| #43 pose keeper | `--pose-keeper-selftest` |
| #44 follow + doors | `--follow-door-selftest` |
| #45–#46 experiences / TEMP | `--exp-selftest`, `PrBackfillPureTests` (DecideScriptQuestion / IsLikelyTempAttach) |
| #48 Peronaut nav | `--follow-door-selftest` (includes `nav selftest`) |
| #49–#50 voice wake | `--voice-wake-selftest` |
| #51–#55 pose / couples / path | `--pose-selftest`, `PrBackfillPureTests` (root/leaf/Magnetize couples) |
| #54 say --re | `--chat-guard-selftest` |
| #32 im --re | `--imguard-selftest` |

`PrBackfillPureTests` links the test project to `galatay-text` (`InternalsVisibleTo`) for pure helpers without spawning the binary.

## What must never run in CI

- Second Life login / any use of `box-secrets.json` or account passwords  
- Downloading the faster-whisper speech model (mark those tests slow: `@pytest.mark.slow` / `[SlowFact]`)  
- Live webhook POSTs to production URLs  

## On the box (Galatea live client)

Deploy is unchanged: build with `textclient/build-all.sh` (or the viewers copy), then cp+mv dlls into `app/` and `text-galatay.sh` stop/start. Tests do not touch the live session.

## Writing tests that survive tuning

Assert behaviour, not tuning numbers. Durations, odds, weights, radii, margins and tolerances come from the `Program`
constants (e.g. `Program.RoamEdgeMarginM`, `Program.RoamArriveTolM`, `Program.RoamLegBook.MaxReplans`), so retuning them
does not break a test. `RoamLoopTests` is the pattern: it pins the roam loop's invariants (eject re-plans once, stops on
logout/sit/follow/leash/teleport, gives up after the re-plan limit, never keeps an adult pose) and reads every number.
Wait for conditions by polling (or a fake clock), not fixed sleeps.

## Connector proxy (restart without remove + re-add)

`textclient/tests/test_mcp_proxy.py` drives `galatay-mcp-proxy.py` over stdio against a stub MCP child and, when
`textclient/mcp-app-staging` is built, against the real connector (isolated run dir, TMP and log paths; no login):
`cd textclient && python3 -m pytest tests/test_mcp_proxy.py`.
