# Testing Galatea

Automated tests are meant to run on every pull request and every push to `main` via GitHub Actions.

The workflow file is checked in as [`docs/ci/github-actions-ci.yml`](docs/ci/github-actions-ci.yml). Copy it to `.github/workflows/ci.yml` once the GitHub token has the `workflow` scope (OAuth apps need that scope to create workflow files). Until then, run the suite locally as below.

**Nothing in CI logs in to Second Life or needs secrets.**

## Quick start (local)

### 1. LibreMetaverse (once)

```bash
bash scripts/ci-setup-libremetaverse.sh
```

Clones [cinderblocks/libremetaverse](https://github.com/cinderblocks/libremetaverse) into `textclient/src-libremetaverse` (gitignored) and applies the galatea patches (`libremetaverse-*.patch`).

### 2. Build the text client

```bash
bash textclient/build-all.sh
```

Produces `textclient/app-staging/galatay-text`.

### 3. C# tests (xUnit wrapping offline selftests)

```bash
export GALATAY_TEXT_EXE="$PWD/textclient/app-staging/galatay-text"
dotnet test textclient/GalatayText.Tests/GalatayText.Tests.csproj -c Release
```

These invoke the same `--*-selftest` entry points as the binary (pose, pose-keeper, chat-guard, imguard, attach-move, detach-cof, follow-door, exp, worn, voice-wake, bugfix). No login.

You can also run one selftest directly:

```bash
./textclient/app-staging/galatay-text --pose-selftest
./textclient/app-staging/galatay-text --chat-guard-selftest
./textclient/app-staging/galatay-text --imguard-selftest
# …
```

### 4. Python tests (pytest)

```bash
pip install -r requirements-ci.txt
pytest -q
```

Covers `vision/tests` (look mesher args, optional crowd placeholders) and `textclient/galatay-voice/tests` (SDP munge / fake server smoke).  
**Skipped in CI:** full voice WebRTC+whisper (`--voice-selftest` / `fake_voice_server.py --drive`) and crowd-placeholder mesher runs that need a large export fixture + SceneMesher.

## Run only the impacted tests (local)

```bash
python3 scripts/impact_tests.py --dry-run     # what would run, and why
python3 scripts/impact_tests.py               # run it (diff vs origin/main + uncommitted + untracked)
python3 scripts/impact_tests.py --full        # everything CI runs
python3 scripts/impact_tests.py --commit <sha> --dry-run   # analyse a past commit or merge
```

The full xUnit suite is ~600 tests and a full local run (build + tests) takes over a minute; most changes touch a handful of
them. The script tokenises every C# file under `textclient/`, splits it into members, picks the members whose lines the diff
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
- Downloading the faster-whisper speech model (mark those tests `Skip`)  
- Live webhook POSTs to production URLs  

## On the box (Galatea live client)

Deploy is unchanged: build with `textclient/build-all.sh` (or the viewers copy), then cp+mv dlls into `app/` and `text-galatay.sh` stop/start. Tests do not touch the live session.

## Connector proxy (restart without remove + re-add)

`textclient/tests/test_mcp_proxy.py` drives `galatay-mcp-proxy.py` over stdio against a stub MCP child and, when
`textclient/mcp-app-staging` is built, against the real connector (isolated run dir, TMP and log paths; no login):
`cd textclient && python3 -m pytest tests/test_mcp_proxy.py`.
