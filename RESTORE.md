# RESTORE: Galatea's Second Life setup (avatar Galatay Resident)

Backup system written 2026-09-26. This file is kept in `/home/box/backups/`, `/home/box/backups/archives/`,
`/workspace/secondlife-backups/`, the git repo, and inside every archive.

## Where the backups are (newest first, in order of preference)

| copy | path | survives |
|---|---|---|
| primary archives (newest 6 runs + hourly 6 h + daily 3 d, no weekly) | `/home/box/backups/archives/sl-full-<UTC>-s<seq>.tar.gz` + `.sha256` | reboot, deleting /workspace |
| git history (one commit per run that saw a change) | `/home/box/backups/sl-repo` | reboot |
| git mirror (bare) | `/workspace/secondlife-backups/sl-repo.git` | reboot, deleting /home/box/backups |
| off-box upload copy (newest verified) | `/workspace/secondlife-backups/latest-offbox.tar.gz` + `.sha256` + `.json` | copied to Google Drive by Galatea |
| screenshots/images (historical only; no longer produced as of 2026-11-10, David's decision — do not include photos in backups) | existing copies may still be under `/home/box/backups/media/` and `/workspace/secondlife-backups/latest-offbox-media.tar.gz`; they are not part of a restore | left in place, not updated |
| off-box | Google Drive "Grok Bots/Galatea/Galatea SL Backups" (folder id 1DEZTJgQpJsZwfha0wLb2pho-GzVCMKkF), one dated folder per night (see section 6) | box reset or loss |

Archive names use UTC time plus a sequence number: the highest `s<seq>` is the newest, even if the clock was wrong.
Health: `cat /home/box/backups/STATUS`; full check: `/home/box/backups/bin/verify-backups`.

## Archive layout

```
sl-full-<stamp>-s<seq>/
  files/home/box/...        original absolute paths (files/workspace/secondlife/... too)
  meta/INVENTORY.md         what is in and what was left out, and why
  meta/libremetaverse.txt   base commit of the patched LibreMetaverse + meta/libremetaverse-local.patch
  meta/firestorm-viewer.txt viewer build name + sha256 (binaries not included)
  meta/fs-extra-debs.txt    apt package list for Firestorm; fs-extra-usr.sha256 lists the extracted libs
  meta/dotnet.txt           SDK/runtime versions; meta/hooks.txt the boot hook chain
  MANIFEST.sha256           sha256 of every file;  BACKUP-INFO.json;  RESTORE.md
```
`*.log.tail` files are only the last 1 MB of big logs (e.g. textclient.log). Do not copy them over live logs.

## 1. Pick and check an archive

```bash
A=$(ls /home/box/backups/archives/sl-full-*.tar.gz | sed 's/.*-s\([0-9]*\)\.tar\.gz$/\1 &/' | sort -n | tail -1 | cut -d' ' -f2)   # newest; or any copy above
cd "$(dirname "$A")" && sha256sum -c "$(basename "$A").sha256"                    # must print OK
# off-box copy: cd /workspace/secondlife-backups && sha256sum -c latest-offbox.tar.gz.sha256
mkdir -p /tmp/slr && tar -xzf "$A" -C /tmp/slr && cd /tmp/slr/sl-full-*/ && sha256sum -c --quiet MANIFEST.sha256 && echo MANIFEST OK
```
If the newest archive fails, take the next older one (or another copy). Never delete a copy while restoring.

## 2. Rebuild a bare new box

Run as user `box`. Only one Second Life login per account: never start the text client while Firestorm or
another client is logged in as Galatay.

```bash
cd /tmp/slr/sl-full-*/
# 2a. put the files back (on a bare box nothing is overwritten; on a live box restore single files instead, see 4.)
cp -a files/home/box/. /home/box/
cp -a files/workspace/. /workspace/
# Photos/screenshots/renders are not backed up (David 2026-11-10). Do not restore media archives as part of a
# normal restore. Historical copies, if you ever want one file from them, may still be under
# /home/box/backups/media/ or /workspace/secondlife-backups/latest-offbox-media.tar.gz.

# 2b. .NET SDK 10 (the client targets net10.0)
bash /home/box/viewers/textclient/dotnet-install.sh --channel 10.0 --install-dir /home/box/.dotnet
export DOTNET_ROOT=/home/box/.dotnet DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# 2c. the patched LibreMetaverse is restored at textclient/src-libremetaverse (tree + .git). If it is missing:
#   git clone https://github.com/cinderblocks/libremetaverse /home/box/viewers/textclient/src-libremetaverse
#   cd .../src-libremetaverse && git checkout <base commit from meta/libremetaverse.txt> && git apply /tmp/slr/sl-full-*/meta/libremetaverse-local.patch

# 2d. rebuild the text client and the MCP connector (the archive also holds the live builds app/ and mcp-app/,
#     which run as they are; rebuilding proves the source is complete)
cd /home/box/viewers/textclient/galatay-text && ~/.dotnet/dotnet build -c Release -o ../app-staging     # expect 0 errors
cp -a ../app-staging/. ../app/
cd /home/box/viewers/textclient/galatay-mcp && ~/.dotnet/dotnet build -c Release -o ../mcp-app-staging
cp -a ../mcp-app-staging/. ../mcp-app/
/home/box/viewers/text-galatay.sh check            # launcher syntax + client --check

# 2e. optional: Firestorm (GUI viewer). Download the build named in meta/firestorm-viewer.txt from
#     https://www.firestormviewer.org/linux/ into /home/box/viewers/ and extract it; then
sudo dpkg -i /home/box/viewers/fs-extra-debs/*.deb || sudo apt-get -f install -y     # or apt-get install the list in meta/fs-extra-debs.txt
#     Firestorm settings are restored in /home/box/.firestorm_x64/user_settings (saved-login store bin_conf.dat is NOT backed up).
# optional: cd /home/box/viewers/textclient/nm && npm ci   (only for nm-test.js)
# 2f. Python venv for the wander snapshot / `overhead` (scripts/overhead.py needs Pillow). The venv is NOT backed up
#     (.venv/venv/__pycache__ dirs are skipped); rebuild it from scripts/requirements.txt (the boot autostart also does this):
/workspace/secondlife/scripts/ensure-venv.sh
```

## 3. Secrets, connector, daemon, hooks

1. **Password**: no backup contains it. Re-add it only through Galatea's secure secret request (box secret store,
   key `card.SECONDLIFE_PASSWORD`; the webhook key is `card.GALATAY_WEBHOOK_KEY`). Never from a file, never on a
   command line, never in chat. The client reads it in-process from `/home/box/agent-data/box-secrets.json`.
   The backup secret scanner (slbackup / secret-check / make-slim-backup) loads values from the agent-injected
   environment (`CLOUD_AGENT_INJECTED_SECRET_NAMES` and each named variable) and, when readable, also from
   `box-secrets.json`; secret values are never printed or written by the scanner.
2. **Webhook URL** (`textclient/webhook-url.txt`) is excluded because it works like a token: re-create it with the
   connector's webhook tools (`webhook_status` / `webhook_test`).
3. **MCP connector**: register `/home/box/viewers/galatay-mcp.sh` as the stdio MCP server `secondlife-galatay2`
   (server id `user-secondlife-galatay2`) in the agent host (the host starts it after every boot).
   Re-adding a connector under a name that was used before keeps the app's old cached tool list, so if the
   tools look stale, register it under a fresh name (e.g. `secondlife-galatay3`) and update notes that name it.
   The proxy exposes `call_tool` / `list_tools`: use `list_tools` to see the live tools, `call_tool` to run one.
4. **Daemon**: the restored `textclient/run/deliberate-stop` (if present) means "she was stopped on purpose";
   `text-galatay.sh start` clears it. Start: `/home/box/viewers/text-galatay.sh start`, check with
   `/home/box/viewers/text-galatay.sh status`. The wander resumes by itself if `textclient/wander-state.json` says enabled.
5. **Hooks**: `galatay-mcp.sh` (restored) already detaches `galatay-autostart.sh` (text client, once per boot) and
   `/home/box/backups/bin/backup-loop.sh start` (hourly backups). To start backups by hand:
   `/home/box/backups/bin/backup-loop.sh start`, then `/home/box/backups/bin/backup-now` and `/home/box/backups/bin/verify-backups`.
   If `/home/box/backups/sl-repo` is gone: `git clone /workspace/secondlife-backups/sl-repo.git /home/box/backups/sl-repo`.

## 4. Roll back ONE file (bad edit) from git history

```bash
cd /home/box/backups/sl-repo
F=files/home/box/viewers/textclient/galatay-text/Wander.cs          # path = files/ + absolute path
git log --format='%h %cd %s' --date=iso -- "$F"                     # versions of this file
git diff <commit> -- "$F"                                           # what changed since then
C=$(git rev-list -1 --before="2026-09-26 13:00" main)               # or: the version as of a given time
git show "$C:$F" > /tmp/restored-file
cp -p "/${F#files/}" "/${F#files/}.bak-$(date +%Y%m%d%H%M)"         # keep the current (bad) one
cp /tmp/restored-file "/${F#files/}"
```
Then rebuild/redeploy if it was source (step 2d, app-staging, cp + mv of the changed dll/pdb) and restart only if needed.
From an archive instead: `tar -xzf ARCHIVE -O sl-full-<stamp>-s<seq>/files/home/box/<path> > /tmp/restored-file`.
Binaries, log tails and the LibreMetaverse tree are only in the archives (not in git).

## 5. When STATUS says ERROR

* `LOW DISK SPACE`: free space on / (the run skipped itself and deleted nothing), then `backup-now`.
* `archive failed verification, discarded`: the previous verified archive is untouched; run `backup-now` again;
  if it repeats, run `verify-backups` and check `logs/backup.log`.
* `previous run did not finish`: a run was killed or crashed; its temp files were removed by the next run. Harmless if the later run is OK.
* `STALE` in verify-backups / loop NOT running: `/home/box/backups/bin/backup-loop.sh start`.
* `SECRET FOUND`: that archive was never published; the offending file is named (value never shown). Remove the secret
  from that file (or add it to the deny list in `bin/slbackup.py`) and run `backup-now`.

## 6. Restore from Google Drive (box reset or loss)

Since 2026-11-10 a Drive restore does **not** use the 2026-10-29 full baseline (or any other dated
Drive folder except the newest night's `latest-state.tar.gz`). Programs come from GitHub
`davidabrooks/galatea` plus public downloads, via `scripts/bare-box-setup.sh` in the repo. Runtime
state (settings, notes, route and pose rules, shape backups, Firestorm settings, backup scripts)
comes from the newest nightly `latest-state.tar.gz`.

Drive folder "Grok Bots/Galatea/Galatea SL Backups" (id 1DEZTJgQpJsZwfha0wLb2pho-GzVCMKkF) has one
dated folder (`YYYY-MM-DD`) per night with:

| file | what it is |
|---|---|
| `latest-state.tar.gz` + `.sha256` | full current STATE, rebuilt from scratch every night (not a delta) |
| `RESTORE.md` | this file |

**Photos, screenshots and renders are not backed up** (David 2026-10-31 and 2026-11-10; HD photos
are handled by a separate Photographer bot). The slim archive and the local full backup
(`slbackup.py` / `backup-loop.sh`) both skip image and media files (jpg, png, exr, hdr, webp, j2c,
tga, and other IMG_EXT types), including under `textclient/routes` and `textclient/shape-backups`.
Older photo archives that may still exist on Drive or under `/home/box/backups/media/` are
historical only and are not required for a restore. Do not delete local photos on the box as part
of cleanup.

The `2026-10-29` folder (last full program backup) and the `2026-10-30` photos folder are no longer
needed for a restore and are no longer protected from the nightly trim (they go to Drive trash,
never a permanent delete). A monthly copy (for example `2026-11-01`) is still kept by retention.

**What latest-state contains** (archive paths are `state/<absolute path>`, plus `RESTORE.md` and
`INVENTORY.md`):
textclient `*.json`, `*.txt` (hover.txt, wander-state.json ...) and `run/`; `textclient/routes`
(skipping `routes/linden` cache and all images, including `_nav-peronaut-home.png`);
`textclient/shape-backups` (`.lnw` shape files); Firestorm helpers `login-galatay.sh`,
`run-firestorm.sh`, `fs-lowperf-settings.py`, `~/.asoundrc`, and `viewers/fs-extra/etc` (small
ALSA/Pulse/xdg config); `/home/box/backups/bin` (backup scripts); top-level
`.md/.txt/.json/.log/.jsonl/.bak-*` files of `/workspace/secondlife`; its
`nav, rlv, notecards, drafts, shapes, linden, the-v, voice, clienttests` dirs; `scripts/` files not
tracked in the galatea git repo; Firestorm `user_settings` and `galatay_resident`;
`~/.config/RadegastVeles`; `viewers/icloudwatch`.
Left out (listed in `INVENTORY.md`): deny-listed credential/token/webhook files, any file containing
a secret value, all images/photos/screenshots/renders and other media, binaries
(.dll/.so/.deb/...), build/source/cache dirs (app, mcp-app, src-libremetaverse, bin, obj, .git,
venv ...), non-log files over 4 MiB. Logs/text over 4 MiB keep only the last 1 MiB. So
latest-state does NOT rebuild the programs: those come from GitHub plus `scripts/bare-box-setup.sh`.

```bash
# 6a. download the NEWEST dated Drive folder's latest-state.tar.gz(.sha256) and RESTORE.md into /tmp/drv
cd /tmp/drv && sha256sum -c latest-state.tar.gz.sha256                     # must print OK

# 6b. rebuild programs from GitHub (do this on a bare box; the script is idempotent and has --dry-run):
#     install git and curl if needed, then:
git clone https://github.com/davidabrooks/galatea.git /tmp/galatea
bash /tmp/galatea/scripts/bare-box-setup.sh
#     That installs .NET 10.0.403, clones/sets up the repo and LibreMetaverse, builds the text client
#     and MCP connector (build-all.sh --stage-mcp), copies launchers, installs Firestorm 7.2.5
#     (sha256-checked), the apt fs-extra packages, Blender 4.5.15 (sha256-checked), scene-mesher,
#     the voice sidecar, and the Python venv (ensure-venv.sh).

# 6c. overlay last night's state (settings, notes, routes, shapes, Firestorm settings, backup scripts):
mkdir -p /tmp/sls && tar -xzf /tmp/drv/latest-state.tar.gz -C /tmp/sls && cp -a /tmp/sls/state/. /
```

If `scripts/bare-box-setup.sh` is not yet on the clone you have, follow its comments in the repo or
run the same steps by hand (dotnet 10.0.403, `scripts/ci-setup-libremetaverse.sh`,
`textclient/build-all.sh --stage-mcp`, launchers, Firestorm 7.2.5, apt packages in
`meta/fs-extra-debs.txt` / the script, Blender 4.5.15, scene-mesher, voice `install.sh`,
`scripts/ensure-venv.sh`) and then do 6c.

Then section 3 (password via secure request, webhook, connector, daemon, hooks).
