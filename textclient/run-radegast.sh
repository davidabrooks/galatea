#!/usr/bin/env bash
# Radegast Veles 3.0.0 launcher (GUI; needs DISPLAY). Source: /home/box/viewers/textclient/radegast-src
# (cinderblocks/radegast master; rebuilt 2026-10-02 PT at d1398eb).
# Local fix: the stock linux-x64 libSkiaSharp.so (NoDependencies build) crashes with
# "Default font family name can't be null"; the fontconfig-enabled 4.151.1 build in
# radegast-src/local-fixes/ is copied over it. A rebuild overwrites it, so this launcher re-applies it.
# Known gotchas (tested 2026-10-02 PT, see client-options.md):
#  - Never close the MAIN window (title-bar X, Alt+F4, or File > Hide Window): it only hides to a tray
#    icon, and this box has no tray host, so the window can't be brought back. Use File > Logout, then Exit.
#  - The password goes into the GUI; leave "Remember credentials" unchecked.
#  - Scene Viewer / HUD Viewer need Vulkan GPU interop and don't work on llvmpipe. Scene Viewer
#    also pushes RAM to ~6 GB, so don't open it.
set -euo pipefail
: "${DISPLAY:?set DISPLAY to your own X display}"
export DOTNET_ROOT="${DOTNET_ROOT:-/home/box/.dotnet}"
SRC=/home/box/viewers/textclient/radegast-src
BIN="$SRC/bin/Veles/Release"
FIX="$SRC/local-fixes/libSkiaSharp-4.151.1-fc.so"
SO="$BIN/runtimes/linux-x64/native/libSkiaSharp.so"
if [ -f "$FIX" ] && ! cmp -s "$FIX" "$SO"; then cp "$FIX" "$SO"; echo "re-applied SkiaSharp fontconfig fix" >&2; fi
if pgrep -x do-not-directly >/dev/null; then echo "Firestorm is running; log it out before logging in with Radegast." >&2; fi
if pgrep -f '/app/galatay-text' >/dev/null || pgrep -x RadegastVeles >/dev/null; then
  echo "Another Galatay client (text client or Radegast) seems to be running; only one SL session per account." >&2
fi
cd "$BIN" && exec ./RadegastVeles "$@"
