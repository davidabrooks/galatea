#!/usr/bin/env bash
# install.sh: create/refresh the sidecar venv and copy the scripts next to the text client.
# Usage: textclient/galatay-voice/install.sh   (idempotent; never touches the running client)
set -euo pipefail
TC=/home/box/viewers/textclient
HERE="$(cd "$(dirname "$0")" && pwd)"
[[ -x "$TC/voice-venv/bin/python" ]] || python3 -m venv "$TC/voice-venv"
# exact versions from the audited lock (2026-11-09); requirements.txt keeps only the direct pins
REQ="$HERE/requirements.lock.txt"; [[ -f "$REQ" ]] || REQ="$HERE/requirements.txt"
"$TC/voice-venv/bin/pip" install -q -r "$REQ"
mkdir -p "$TC/voice/testdata" "$TC/voice-models"
cp "$HERE/voice_sidecar.py" "$HERE/fake_voice_server.py" "$HERE/requirements.txt" "$HERE/requirements.lock.txt" "$TC/voice/"
cp "$HERE/testdata/"* "$TC/voice/testdata/"
echo "installed: $TC/voice (sidecar), $TC/voice-venv (python), models download to $TC/voice-models on first use"
