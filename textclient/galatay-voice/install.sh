#!/usr/bin/env bash
# install.sh: create/refresh the sidecar venv and copy the scripts next to the text client.
# Usage: textclient/galatay-voice/install.sh   (idempotent; never touches the running client)
set -euo pipefail
TC=/home/box/viewers/textclient
HERE="$(cd "$(dirname "$0")" && pwd)"
[[ -x "$TC/voice-venv/bin/python" ]] || python3 -m venv "$TC/voice-venv"
"$TC/voice-venv/bin/pip" install -q -r "$HERE/requirements.txt"
mkdir -p "$TC/voice/testdata" "$TC/voice-models"
cp "$HERE/voice_sidecar.py" "$HERE/fake_voice_server.py" "$TC/voice/"
cp "$HERE/testdata/"* "$TC/voice/testdata/"
echo "installed: $TC/voice (sidecar), $TC/voice-venv (python), models download to $TC/voice-models on first use"
