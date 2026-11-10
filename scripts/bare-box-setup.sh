#!/usr/bin/env bash
# bare-box-setup.sh — rebuild Galatea's Second Life programs on a new box from this git repo
# plus public downloads. Runtime state (settings, notes, routes, shapes, Firestorm settings,
# backup scripts) comes from the newest Drive nightly latest-state.tar.gz, not from the
# 2026-10-29 full baseline. See RESTORE.md section 6.
#
# Usage:
#   scripts/bare-box-setup.sh --dry-run              # print the plan, change nothing
#   scripts/bare-box-setup.sh --latest-state FILE    # after programs, extract FILE over /
#   scripts/bare-box-setup.sh --force                # allow running on a box that already has an install
#
# This script is idempotent for downloads and installs. It will refuse to run (without --force)
# if a live text-client build or a deliberate-stop marker is already present, so it is safe to
# syntax-check and --dry-run on a live box. Do not run it destructively against a live avatar host.
#
# Pins (2026-11-10):
#   .NET SDK 10.0.403
#   LibreMetaverse via scripts/ci-setup-libremetaverse.sh (PIN in that script)
#   Firestorm 7.2.5.81788  sha256 c9a0dbb1c4076f177ac2ff5e1b62d8fdc58254f04deb854baeb3520c53b5b3da
#   Blender 4.5.15 linux-x64  sha256 0d2b8bb2efe97e6c8eb57de6b7322f8800e1984e3125b60decca5f6771e4773e
# Photos/screenshots/renders are not part of this restore.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
DOTNET_DIR="${DOTNET_ROOT:-/home/box/.dotnet}"
SDK_VERSION="10.0.403"
VIEWERS="${GALATEA_VIEWERS:-/home/box/viewers}"
TC="$VIEWERS/textclient"
TOOLS="${GALATEA_TOOLS:-/home/box/tools}"
SL="${GALATEA_SL:-/workspace/secondlife}"

FS_NAME="Phoenix-Firestorm-Releasex64_AVX2-7-2-5-81788"
FS_TAR="${FS_NAME}.tar.xz"
FS_SHA256="c9a0dbb1c4076f177ac2ff5e1b62d8fdc58254f04deb854baeb3520c53b5b3da"
FS_URL="https://downloads.firestormviewer.org/release/linux/${FS_TAR}"

BLENDER_VER="4.5.15"
BLENDER_TAR="blender-${BLENDER_VER}-linux-x64.tar.xz"
BLENDER_SHA256="0d2b8bb2efe97e6c8eb57de6b7322f8800e1984e3125b60decca5f6771e4773e"
BLENDER_URL="https://download.blender.org/release/Blender4.5/${BLENDER_TAR}"

FS_APT=(libasound2-plugins libfftw3-single3 libglu1-mesa libopengl0 libpulsedsp
        libspeexdsp1 libtdb1 libwebrtc-audio-processing-1-3 pulseaudio pulseaudio-utils)

DRY=0
FORCE=0
LATEST_STATE=""
FS_TAR_PATH=""
BLENDER_TAR_PATH=""

usage() {
  sed -n '2,24p' "$0" | sed 's/^# \?//'
  echo "Flags: --dry-run  --force  --latest-state FILE  --firestorm-tar FILE  --blender-tar FILE"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run) DRY=1; shift ;;
    --force) FORCE=1; shift ;;
    --latest-state) LATEST_STATE="${2:-}"; shift 2 ;;
    --firestorm-tar) FS_TAR_PATH="${2:-}"; shift 2 ;;
    --blender-tar) BLENDER_TAR_PATH="${2:-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "unknown argument: $1" >&2; usage; exit 2 ;;
  esac
done

log() { echo "[bare-box $(date '+%F %T %Z')] $*"; }
run() {
  if (( DRY )); then
    printf 'DRY:'; printf ' %q' "$@"; printf '\n'
    return 0
  fi
  "$@"
}

need_force_reason=""
if [[ -e "$TC/app/galatay-text.dll" || -e "$TC/run/deliberate-stop" ]]; then
  need_force_reason="live text-client install or deliberate-stop marker already present at $TC"
fi
if [[ -n "$need_force_reason" && $FORCE -eq 0 && $DRY -eq 0 ]]; then
  echo "REFUSING: $need_force_reason" >&2
  echo "This script is for a bare box. On a live host use --dry-run, or --force only if you mean to overlay the programs." >&2
  exit 3
fi

plan() {
  cat <<EOF
Plan (dry-run=$DRY force=$FORCE):
  1. Install .NET SDK $SDK_VERSION into $DOTNET_DIR
  2. Sync this repo's textclient/ into $TC (sources + launchers)
  3. $ROOT/scripts/ci-setup-libremetaverse.sh  (LibreMetaverse pin + galatea patches)
  4. $ROOT/textclient/build-all.sh --stage-mcp  and install app/ + mcp-app/ from staging
  5. Copy launchers to $VIEWERS/ (text-galatay.sh, galatay-mcp.sh, galatay-mcp-child.sh, galatay-autostart.sh)
     and Firestorm helpers from scripts/firestorm/ (login-galatay.sh, run-firestorm.sh, fs-lowperf-settings.py, ~/.asoundrc)
  6. Firestorm 7.2.5.81788 into $VIEWERS/$FS_NAME, symlink firestorm-current; sha256 $FS_SHA256
  7. apt packages: ${FS_APT[*]}
  8. Blender $BLENDER_VER into $TOOLS/blender-${BLENDER_VER}-linux-x64; sha256 $BLENDER_SHA256
  9. scene-mesher: dotnet build vision/scene-mesher -c Release -o $TOOLS/scene-mesher (LMV=$TC/src-libremetaverse)
 10. textclient/galatay-voice/install.sh
 11. Extract latest-state${LATEST_STATE:+ ($LATEST_STATE)} over / if given (settings/notes/routes/shapes; no photos)
 12. scripts/ensure-venv.sh  (needs $SL/scripts/requirements.txt, from the extract or a copy of repo scripts)
Photos, screenshots and renders are not installed or restored.
EOF
}
plan
if [[ -n "$need_force_reason" && $DRY -eq 1 ]]; then
  log "note: would refuse a live run without --force ($need_force_reason)"
fi

# ---------- 1. .NET ----------
dotnet_ok() {
  [[ -x "$DOTNET_DIR/dotnet" ]] && "$DOTNET_DIR/dotnet" --list-sdks 2>/dev/null | grep -q "^${SDK_VERSION} "
}
if dotnet_ok; then
  log ".NET SDK $SDK_VERSION already installed"
else
  log "installing .NET SDK $SDK_VERSION"
  inst="$ROOT/textclient/dotnet-install.sh"
  if [[ ! -f "$inst" ]]; then
    run curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    inst=/tmp/dotnet-install.sh
    (( DRY )) || chmod +x "$inst"
  fi
  run bash "$inst" --version "$SDK_VERSION" --install-dir "$DOTNET_DIR"
fi
export DOTNET_ROOT="$DOTNET_DIR" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export PATH="$DOTNET_DIR:$PATH"

# ---------- 2. sync textclient sources ----------
log "sync textclient sources -> $TC"
run mkdir -p "$TC" "$VIEWERS" "$TOOLS" "$SL"
# Copy the repo tree's textclient sources, but never clobber a live app/ or run/ without --force.
# On a bare box those dirs are empty. rsync if available, else cp -a of source dirs.
sync_src() {
  local src="$ROOT/textclient" dest="$TC"
  if command -v rsync >/dev/null 2>&1; then
    rsync -a --delete --exclude 'app/' --exclude 'app-staging/' --exclude 'mcp-app/' \
      --exclude 'mcp-app-staging/' --exclude 'mcp-app-next/' --exclude 'src-libremetaverse/' \
      --exclude 'run/' --exclude 'shape-backups/' --exclude '.git/' \
      "$src/" "$dest/"
  else
    mkdir -p "$dest"
    cp -a "$src/." "$dest/"
  fi
}
if (( DRY )); then
  run sync_src
else
  sync_src
fi

# ---------- 3. LibreMetaverse ----------
log "ci-setup-libremetaverse.sh"
# The setup script expects textclient next to the repo scripts. If $TC is not $ROOT/textclient,
# point it at the install tree by running from a copy or setting paths inside that script (it
# uses ROOT/textclient). For a bare box we prefer to build in the repo clone, then copy builds out.
if [[ "$(cd "$ROOT/textclient" && pwd)" == "$(cd "$TC" 2>/dev/null && pwd || true)" ]]; then
  run bash "$HERE/ci-setup-libremetaverse.sh"
else
  # Run the official script against the repo checkout, then we'll build there and install the output.
  run bash "$HERE/ci-setup-libremetaverse.sh"
fi

# ---------- 4. build ----------
log "build-all.sh --stage-mcp"
if (( DRY )); then
  run bash "$ROOT/textclient/build-all.sh" --stage-mcp
else
  bash "$ROOT/textclient/build-all.sh" --stage-mcp
  mkdir -p "$TC/app" "$TC/mcp-app"
  if [[ -d "$ROOT/textclient/app-staging" ]]; then
    cp -a "$ROOT/textclient/app-staging/." "$TC/app/"
  fi
  # Prefer the staged next build; fall back to mcp-app-staging.
  if [[ -d "$ROOT/textclient/mcp-app-next" ]]; then
    cp -a "$ROOT/textclient/mcp-app-next/." "$TC/mcp-app/"
  elif [[ -d "$ROOT/textclient/mcp-app-staging" ]]; then
    cp -a "$ROOT/textclient/mcp-app-staging/." "$TC/mcp-app/"
  fi
fi

# ---------- 5. launchers + Firestorm helpers ----------
log "copy launchers and Firestorm helpers"
for s in text-galatay.sh galatay-mcp.sh galatay-mcp-child.sh galatay-autostart.sh; do
  if [[ -f "$ROOT/textclient/$s" ]]; then
    run install -m 0755 "$ROOT/textclient/$s" "$VIEWERS/$s"
  fi
done
for s in login-galatay.sh run-firestorm.sh fs-lowperf-settings.py; do
  if [[ -f "$HERE/firestorm/$s" ]]; then
    run install -m 0755 "$HERE/firestorm/$s" "$VIEWERS/$s"
  fi
done
if [[ -f "$HERE/firestorm/asoundrc" ]]; then
  run install -m 0644 "$HERE/firestorm/asoundrc" "${HOME}/.asoundrc"
fi

# ---------- 6. Firestorm ----------
fetch_and_check() {
  # fetch_and_check url dest sha256 [preexisting-path]
  local url="$1" dest="$2" sha="$3" pre="${4:-}"
  if [[ -n "$pre" && -f "$pre" ]]; then
    run cp -p "$pre" "$dest"
  elif [[ -f "$dest" ]]; then
    log "already have $dest"
  else
    run curl -fL --retry 3 -o "$dest" "$url"
  fi
  if (( DRY )); then
    log "DRY: would sha256sum-check $dest against $sha"
    return 0
  fi
  local got
  got=$(sha256sum "$dest" | awk '{print $1}')
  if [[ "$got" != "$sha" ]]; then
    echo "SHA256 mismatch for $dest" >&2
    echo "expected $sha" >&2
    echo "got      $got" >&2
    rm -f "$dest"
    exit 4
  fi
  log "sha256 OK $dest"
}

log "Firestorm $FS_NAME"
run mkdir -p "$VIEWERS"
FS_DEST="$VIEWERS/$FS_TAR"
if [[ ! -d "$VIEWERS/$FS_NAME" ]]; then
  fetch_and_check "$FS_URL" "$FS_DEST" "$FS_SHA256" "$FS_TAR_PATH"
  run tar -xJf "$FS_DEST" -C "$VIEWERS"
else
  log "Firestorm already extracted at $VIEWERS/$FS_NAME"
fi
run ln -sfn "$FS_NAME" "$VIEWERS/firestorm-current"

# ---------- 7. apt fs-extra ----------
log "apt packages for Firestorm"
if (( DRY )); then
  log "DRY: sudo apt-get install -y ${FS_APT[*]}"
else
  if command -v sudo >/dev/null 2>&1; then
    sudo apt-get update -qq
    sudo apt-get install -y "${FS_APT[@]}"
  else
    echo "sudo not available; install these packages by hand: ${FS_APT[*]}" >&2
  fi
fi

# ---------- 8. Blender ----------
log "Blender $BLENDER_VER"
run mkdir -p "$TOOLS"
BL_DEST="$TOOLS/$BLENDER_TAR"
BL_DIR="$TOOLS/blender-${BLENDER_VER}-linux-x64"
if [[ ! -d "$BL_DIR" ]]; then
  fetch_and_check "$BLENDER_URL" "$BL_DEST" "$BLENDER_SHA256" "$BLENDER_TAR_PATH"
  run tar -xJf "$BL_DEST" -C "$TOOLS"
else
  log "Blender already extracted at $BL_DIR"
fi

# ---------- 9. scene-mesher ----------
log "scene-mesher -> $TOOLS/scene-mesher"
if (( DRY )); then
  run dotnet build "$ROOT/vision/scene-mesher/SceneMesher.csproj" -c Release -o "$TOOLS/scene-mesher"
else
  LMV="$ROOT/textclient/src-libremetaverse"
  if [[ ! -d "$LMV" ]]; then
    LMV="$TC/src-libremetaverse"
  fi
  dotnet build "$ROOT/vision/scene-mesher/SceneMesher.csproj" -c Release -o "$TOOLS/scene-mesher" -p:LMV="$LMV"
fi

# ---------- 10. voice ----------
log "galatay-voice install.sh"
if (( DRY )); then
  run bash "$ROOT/textclient/galatay-voice/install.sh"
else
  # install.sh writes into /home/box/viewers/textclient; that is $TC on a standard box.
  bash "$ROOT/textclient/galatay-voice/install.sh"
fi

# ---------- 11. latest-state overlay ----------
if [[ -n "$LATEST_STATE" ]]; then
  log "extract latest-state $LATEST_STATE"
  if (( DRY )); then
    log "DRY: sha256sum -c ${LATEST_STATE}.sha256 (if present); tar -tzf; cp -a state/. /"
  else
    if [[ -f "${LATEST_STATE}.sha256" ]]; then
      (cd "$(dirname "$LATEST_STATE")" && sha256sum -c "$(basename "$LATEST_STATE").sha256")
    fi
    tmp=$(mktemp -d /tmp/galatea-latest-state.XXXXXX)
    tar -xzf "$LATEST_STATE" -C "$tmp"
    if [[ ! -d "$tmp/state" ]]; then
      echo "latest-state archive has no state/ directory" >&2
      exit 5
    fi
    # Never copy photos even if an old archive still has them.
    if command -v rsync >/dev/null 2>&1; then
      rsync -a --exclude '*.png' --exclude '*.jpg' --exclude '*.jpeg' --exclude '*.jpe' \
        --exclude '*.gif' --exclude '*.webp' --exclude '*.bmp' --exclude '*.tga' \
        --exclude '*.tif' --exclude '*.tiff' --exclude '*.j2c' --exclude '*.j2k' \
        --exclude '*.jp2' --exclude '*.exr' --exclude '*.hdr' --exclude '*.hdri' \
        --exclude '*.mp4' --exclude '*.webm' --exclude '*.mov' --exclude 'photos/' \
        --exclude 'screenshots/' --exclude 'images/' \
        "$tmp/state/" /
    else
      cp -a "$tmp/state/." /
    fi
    rm -rf "$tmp"
  fi
else
  log "no --latest-state given; overlay it later as in RESTORE.md section 6c"
fi

# ---------- 12. venv ----------
if [[ ! -f "$SL/scripts/requirements.txt" && -f "$ROOT/scripts/requirements.txt" ]]; then
  log "copy scripts/requirements.txt to $SL/scripts (needed by ensure-venv.sh)"
  run mkdir -p "$SL/scripts"
  run cp -p "$ROOT/scripts/requirements.txt" "$SL/scripts/requirements.txt"
  [[ -f "$ROOT/scripts/ensure-venv.sh" ]] && run cp -p "$ROOT/scripts/ensure-venv.sh" "$SL/scripts/ensure-venv.sh" || true
fi
log "ensure-venv.sh"
if [[ -x "$SL/scripts/ensure-venv.sh" ]]; then
  run bash "$SL/scripts/ensure-venv.sh"
elif [[ -x "$HERE/ensure-venv.sh" ]]; then
  run bash "$HERE/ensure-venv.sh"
else
  log "ensure-venv.sh not found; skip"
fi

log "done. Next: section 3 of RESTORE.md (password via secure request, webhook, connector, daemon, hooks)."
log "A restored deliberate-stop marker means she was stopped on purpose; do not auto-login."
