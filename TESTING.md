# Testing Galatea

Automated tests run on every pull request and every push to `main` (GitHub Actions workflow `.github/workflows/ci.yml`).
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

## PR backfill map

| PR(s) | Covered by |
|------|------------|
| #33–#37 look / mesher / alpha | `vision/tests`, optional crowd placeholder |
| #38 wander hold + webhook retry | `--bugfix-selftest` |
| #41 detach COF | `--detach-cof-selftest` |
| #43 pose keeper | `--pose-keeper-selftest` |
| #44 follow + doors | `--follow-door-selftest` |
| #45–#46 experiences / TEMP | `--exp-selftest` |
| #48 Peronaut nav | `--follow-door-selftest` (includes `nav selftest`) |
| #49–#50 voice wake | `--voice-wake-selftest` |
| #51–#53 pose / couples / path | `--pose-selftest` |
| #54 say --re | `--chat-guard-selftest` |
| #32 im --re | `--imguard-selftest` |

## What must never run in CI

- Second Life login / any use of `box-secrets.json` or account passwords  
- Downloading the faster-whisper speech model (mark those tests `Skip`)  
- Live webhook POSTs to production URLs  

## On the box (Galatea live client)

Deploy is unchanged: build with `textclient/build-all.sh` (or the viewers copy), then cp+mv dlls into `app/` and `text-galatay.sh` stop/start. Tests do not touch the live session.
