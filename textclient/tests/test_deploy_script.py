"""deploy.sh against scratch dirs only (never the real live dir): dry run writes nothing, offline/marker refusals,
rollback swaps and hash-verifies, a tampered backup is refused, and a rollback of the rollback restores the start."""
import os, shutil, subprocess, time
from pathlib import Path

DEPLOY = Path(__file__).resolve().parents[1] / "deploy.sh"


def tree(root: Path, tag: str):
    for d, dll in (("app", "galatay-text.dll"), ("mcp-app", "galatay-mcp.dll")):
        p = root / d; (p / "sub").mkdir(parents=True)
        (p / dll).write_text(f"{d} {tag}")
        (p / "LibreMetaverse.dll").write_bytes(b"MZ....#Strings\0ScheduleSeedRetry\0Caps\0ObjectDiskCache\0")
        (p / "sub" / "x.json").write_text(tag)
    return root


def live(tmp: Path):
    lv = tree(tmp / "live", "old")
    (lv / "run").mkdir(); (lv / "run" / "deliberate-stop").write_text("test")
    (lv / "routes").mkdir(); (lv / "routes" / "r.json").write_text("{}")
    return lv


def snap(p: Path):
    return sorted((str(f.relative_to(p)), f.read_bytes() if f.is_file() else b"") for f in p.rglob("*"))


def run(*a):
    return subprocess.run(["bash", str(DEPLOY), *map(str, a)], capture_output=True, text=True)


def test_dry_run_deploy_writes_nothing(tmp_path):
    lv = live(tmp_path)
    st = tmp_path / "stage"; st.mkdir()
    new = tree(tmp_path / "new", "new")
    shutil.copytree(new / "app", st / "app-staging"); shutil.copytree(new / "mcp-app", st / "mcp-app-staging")
    before = snap(lv)
    r = run("--dry-run", "--tag", "pr1", "--live", lv, "--viewers", tmp_path, "--no-restart", st)
    assert r.returncode == 0, r.stdout + r.stderr
    assert "DRY RUN: nothing written" in r.stdout and "app: 2 changed" in r.stdout
    assert snap(lv) == before


def test_refusals(tmp_path):
    lv = live(tmp_path); bk = tree(tmp_path / "bk", "bk")
    assert run("--rollback", bk, "--live", lv).returncode == 2          # scratch live without --no-restart
    (lv / "run" / "deliberate-stop").unlink()
    assert run("--rollback", bk, "--live", lv, "--no-restart").returncode == 3
    (lv / "run" / "deliberate-stop").write_text("t")
    fake = tmp_path / "galatay-fake"; shutil.copy("/bin/sleep", fake)
    p = subprocess.Popen([str(fake), "30"])
    try:
        (lv / "run" / "sl-session.pid").write_text(str(p.pid))
        r = run("--rollback", bk, "--live", lv, "--no-restart")
        assert r.returncode == 3 and "logged in" in r.stdout
    finally:
        p.kill()
    assert snap(lv / "app") == snap(tmp_path / "live" / "app")


def test_rollback_roundtrip_and_tamper(tmp_path):
    lv = live(tmp_path); bk = tree(tmp_path / "bk", "bk")
    start = snap(lv / "app"), snap(lv / "mcp-app")
    r = run("--rollback", bk, "--live", lv, "--no-restart")
    assert r.returncode == 0, r.stdout + r.stderr
    assert snap(lv / "app") == snap(bk / "app") and snap(lv / "mcp-app") == snap(bk / "mcp-app")
    assert "swap OK" in r.stdout and "----- notes entry -----" in r.stdout
    pre = next((lv / "app-backups").glob("rollback-pre-*"))
    assert (pre / "backup-sha256.json").is_file() and (pre / "notes-entry.md").is_file()
    (pre / "app" / "galatay-text.dll").write_text("tampered")
    assert run("--rollback", pre, "--live", lv, "--no-restart").returncode == 2
    (pre / "app" / "galatay-text.dll").write_text("app old")
    time.sleep(1.1)  # backup dir names are per second
    assert run("--rollback", pre, "--live", lv, "--no-restart").returncode == 0
    assert (snap(lv / "app"), snap(lv / "mcp-app")) == start
    assert not list(lv.glob(".deploy-tmp-*"))
