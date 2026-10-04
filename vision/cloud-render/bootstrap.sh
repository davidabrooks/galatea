#!/usr/bin/env bash
# Runpod Serverless worker bootstrap (container start command): installs a minimal runtime on the stock
# nvidia/cuda base image, downloads Blender, then starts the Runpod handler. Kept in the repo so the
# endpoint can use a public base image without a custom registry push (first test only).
set -euo pipefail
T0=$(date +%s)
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends python3 python3-pip curl xz-utils ca-certificates \
  libx11-6 libxi6 libxxf86vm1 libxfixes3 libxrender1 libxkbcommon0 libsm6 libice6 libgl1 libegl1 libglvnd0 \
  libvulkan1 libxext6 >/dev/null
pip3 install -q --no-cache-dir runpod==1.7.* pillow >/dev/null
BL=blender-4.2.3-linux-x64
if [ ! -x /opt/$BL/blender ]; then
  curl -sL https://download.blender.org/release/Blender4.2/$BL.tar.xz | tar -xJ -C /opt
fi
REF=${GT_REPO_REF:-vision/runpod-test-render}
BASE=https://raw.githubusercontent.com/davidabrooks/galatea/$REF/vision/cloud-render
mkdir -p /app && cd /app
curl -sfL $BASE/handler.py -o handler.py
curl -sfL $BASE/render_scene.py -o render_scene.py
export BLENDER=/opt/$BL/blender
export GT_BOOTSTRAP_SECONDS=$(( $(date +%s) - T0 ))
exec python3 -u handler.py
