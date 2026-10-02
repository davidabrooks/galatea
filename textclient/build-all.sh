#!/usr/bin/env bash
# build-all.sh (2026-09-27): the ONE build command for Galatay's code. Builds BOTH projects so a shared source file
# missing from either project fails right away:
#   1. source-list check: every galatay-text/*.cs must be a <Compile Include> in galatay-mcp/GalatayMcp.csproj
#      (the MCP connector compiles the text client's core from there), every galatay-mcp/*.cs except McpProgram.cs must
#      be in galatay-text/GalatayText.csproj, and every Include must point to an existing file. Mismatch -> exit 3.
#   2. dotnet build galatay-text -> textclient/app-staging      (text client; deploy = cp+mv galatay-text.dll/.pdb into app/)
#   3. dotnet build galatay-mcp  -> textclient/mcp-app-staging  (MCP connector; never writes into the live mcp-app/)
# Options: --check-only   only the source-list check
#          --stage-mcp    after a good build, copy mcp-app-staging to mcp-app-next (+READY) for galatay-mcp.sh to swap in
#                         at the connector's next launch (the running connector is never touched)
# Exit: 0 ok, 3 source-list mismatch, 4 text client build failed, 5 MCP build failed.
set -uo pipefail
TC=/home/box/viewers/textclient
export PATH="/home/box/.dotnet:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-/home/box/.dotnet}" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
CHECK_ONLY=0; STAGE=0
for a in "$@"; do case "$a" in --check-only) CHECK_ONLY=1;; --stage-mcp) STAGE=1;; *) echo "usage: $0 [--check-only] [--stage-mcp]"; exit 2;; esac; done

python3 - "$TC" <<'PY' || exit 3
import sys, os, re, glob
tc = sys.argv[1]; bad = []
def includes(proj):
    d = os.path.dirname(proj); txt = open(proj).read()
    return {os.path.normpath(os.path.join(d, m.replace('\\', '/'))) for m in re.findall(r'<Compile\s+Include="([^"]+)"', txt)}
mcp_proj = f'{tc}/galatay-mcp/GalatayMcp.csproj'; txt_proj = f'{tc}/galatay-text/GalatayText.csproj'
mi, ti = includes(mcp_proj), includes(txt_proj)
text_cs = sorted(os.path.normpath(p) for p in glob.glob(f'{tc}/galatay-text/*.cs'))
mcp_cs = sorted(os.path.normpath(p) for p in glob.glob(f'{tc}/galatay-mcp/*.cs') if not p.endswith('/McpProgram.cs'))
for f in text_cs:
    if f not in mi: bad.append(f'GalatayMcp.csproj is missing <Compile Include="../galatay-text/{os.path.basename(f)}" Link="Core/{os.path.basename(f)}" />')
for f in mcp_cs:
    if f not in ti: bad.append(f'GalatayText.csproj is missing <Compile Include="../galatay-mcp/{os.path.basename(f)}" Link="{os.path.basename(f)}" />')
for proj, inc in ((mcp_proj, mi), (txt_proj, ti)):
    for f in sorted(inc):
        if not os.path.exists(f): bad.append(f'{os.path.basename(proj)} includes a file that does not exist: {f}')
if bad:
    print('SOURCE-LIST CHECK FAILED:'); [print('  ' + b) for b in bad]; sys.exit(1)
print(f'source-list check OK: {len(text_cs)} shared galatay-text/*.cs all in GalatayMcp.csproj; {len(mcp_cs)} galatay-mcp/*.cs (excl. McpProgram.cs) in GalatayText.csproj')
PY
(( CHECK_ONLY )) && exit 0

build() {  # dir outdir label
  local out; out=$(cd "$TC/$1" && dotnet build -c Release -o "$TC/$2" 2>&1); local rc=$?
  local errs warns; errs=$(grep -cE ': error ' <<<"$out"); warns=$(grep -E ': warning ' <<<"$out" | sort -u | wc -l)
  if (( rc != 0 || errs > 0 )); then echo "$3 build FAILED (rc $rc):"; grep -E ': error ' <<<"$out" | sort -u | head -30; return 1; fi
  echo "$3 build OK -> $TC/$2 (0 errors, $warns distinct warnings)"
}
build galatay-text app-staging "text client" || exit 4
build galatay-mcp mcp-app-staging "MCP connector" || exit 5
if (( STAGE )); then
  rm -rf "$TC/mcp-app-next.tmp" && cp -a "$TC/mcp-app-staging" "$TC/mcp-app-next.tmp" \
    && (cd "$TC/mcp-app-next.tmp" && sha256sum galatay-mcp.dll > READY && echo "staged $(date '+%F %T %Z') by build-all.sh" >> READY) \
    && rm -rf "$TC/mcp-app-next" && mv "$TC/mcp-app-next.tmp" "$TC/mcp-app-next" \
    && echo "MCP build staged in $TC/mcp-app-next (installed by galatay-mcp.sh at the connector's next launch)"
fi
echo "next: deploy the text client with cp+mv of app-staging/galatay-text.dll/.pdb into app/ (restart needed to load it)"
