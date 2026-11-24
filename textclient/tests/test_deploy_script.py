"""deploy.sh against scratch dirs only (never the real live dir): dry run writes nothing, offline/marker refusals,
rollback swaps and hash-verifies, a tampered backup is refused, and a rollback of the rollback restores the start."""
import os, shutil, subprocess, time
from pathlib import Path

DEPLOY = Path(__file__).resolve().parents[1] / "deploy.sh"


def tree(root: Path, tag: str):
    for d, dll in (("app", "galatay-text.dll"), ("mcp-app", "galatay-mcp.dll")):
        p = root / d; (p / "sub").mkdir(parents=True)
        (p / dll).write_text(f"{d} {tag}")
        (p / "LibreMetaverse.dll").write_bytes(b"MZ....#Strings\0ScheduleSeedRetry\0Caps\0")
        (p / "LibreMetaverse.Caching.dll").write_bytes(b"MZ....#Strings\0ObjectDiskCache\0")
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


# ---------- renderer step (2026-11-22): vision/ of the renderer checkout follows the staged commit ----------
def git(cwd, *a):
    return subprocess.run(["git", "-C", str(cwd), *a], capture_output=True, text=True, check=True).stdout.strip()


def renderer_setup(tmp: Path):
    """repo with commit A (live renderer) and B (staged): B edits render_mesh.py, removes old.py, adds new.py"""
    repo = tmp / "repo"; (repo / "vision" / "cloud-render").mkdir(parents=True); (repo / "textclient").mkdir()
    git(repo, "init", "-q", "-b", "main"); git(repo, "config", "user.email", "t@t"); git(repo, "config", "user.name", "t")
    (repo / "vision" / "look.py").write_text("look A\n"); (repo / "vision" / "cloud-render" / "render_mesh.py").write_text("mesh A\n")
    (repo / "vision" / "old.py").write_text("old\n"); (repo / "textclient" / "keep.txt").write_text("k")
    git(repo, "add", "-A"); git(repo, "commit", "-qm", "A"); a = git(repo, "rev-parse", "HEAD")
    (repo / "vision" / "cloud-render" / "render_mesh.py").write_text("mesh B\n"); (repo / "vision" / "old.py").unlink()
    (repo / "vision" / "new.py").write_text("new B\n"); git(repo, "add", "-A"); git(repo, "commit", "-qm", "B")
    vis = tmp / "sl-repo"; subprocess.run(["git", "clone", "-q", str(repo), str(vis)], check=True); git(vis, "checkout", "-q", "--detach", a)
    st = repo / "textclient"; new = tree(tmp / "new", "new")
    shutil.copytree(new / "app", st / "app-staging"); shutil.copytree(new / "mcp-app", st / "mcp-app-staging")
    (repo / ".gitignore").write_text("textclient/app-staging/\ntextclient/mcp-app-staging/\n"); git(repo, "add", "-A"); git(repo, "commit", "-qm", "ignore")
    return st, vis, live(tmp)


def vrun(st, vis, lv, tmp, *extra):
    return run("--tag", "prV", "--live", lv, "--viewers", tmp, "--no-restart", "--allow-unmerged", "--vision", vis, *extra, st)


def test_renderer_dry_run_plans_and_writes_nothing(tmp_path):
    st, vis, lv = renderer_setup(tmp_path); before = snap(vis / "vision")
    r = vrun(st, vis, lv, tmp_path, "--dry-run")
    assert r.returncode == 0, r.stdout + r.stderr
    assert "renderer: " in r.stdout and "2 to update, 1 to remove" in r.stdout and "renderer: sync 2 + remove 1" in r.stdout
    assert snap(vis / "vision") == before


def test_renderer_sync_backup_verify_and_rollback(tmp_path):
    st, vis, lv = renderer_setup(tmp_path)
    (vis / "vision" / "scratch-photographer.txt").write_text("not ours")          # untracked: never touched
    start = snap(vis / "vision")
    r = vrun(st, vis, lv, tmp_path)
    assert r.returncode == 0, r.stdout + r.stderr
    assert "renderer OK:" in r.stdout and "sha256-verified" in r.stdout
    v = vis / "vision"
    assert (v / "cloud-render" / "render_mesh.py").read_text() == "mesh B\n" and (v / "new.py").read_text() == "new B\n" and not (v / "old.py").exists()
    assert (v / "look.py").read_text() == "look A\n" and (v / "scratch-photographer.txt").read_text() == "not ours"
    b = next((lv / "app-backups").glob("deploy-*"))
    assert (b / "vision" / "old.py").read_text() == "old\n" and (b / "renderer-installed-sha256.json").is_file()
    assert "renderer OK:" in (b / "notes-entry.md").read_text()
    time.sleep(1.1)
    r = run("--rollback", b, "--live", lv, "--no-restart", "--vision", vis)
    assert r.returncode == 0, r.stdout + r.stderr
    assert snap(v) == start                                                        # mesh A, old.py back, new.py gone, scratch kept


def test_renderer_refuses_unknown_local_work_and_a_busy_renderer(tmp_path):
    st, vis, lv = renderer_setup(tmp_path)
    (vis / "vision" / "cloud-render" / "render_mesh.py").write_text("someone's uncommitted edit\n")
    before, app_before = snap(vis / "vision"), snap(lv / "app")
    r = vrun(st, vis, lv, tmp_path)
    assert r.returncode == 2 and "unaccounted" in r.stdout, r.stdout + r.stderr
    assert snap(vis / "vision") == before and snap(lv / "app") == app_before and not (lv / "app-backups").exists()
    git(vis, "checkout", "-q", "--", "vision")                                   # back to commit A content
    p = subprocess.Popen(["python3", "-c", "import time; time.sleep(30)", str(vis / "vision" / "look.py")]); time.sleep(0.3)     # a render from that tree is running
    try:
        r = vrun(st, vis, lv, tmp_path)
        assert r.returncode == 3 and "renderer is in use" in r.stdout, r.stdout + r.stderr
    finally:
        p.kill()
    assert snap(lv / "app") == app_before


def test_renderer_content_from_history_counts_as_accounted(tmp_path):
    st, vis, lv = renderer_setup(tmp_path)
    (vis / "vision" / "cloud-render" / "render_mesh.py").write_text("mesh B\n")   # e.g. a hand checkout of a merged file: known content
    (vis / "vision" / "look.py").write_text("look A\n")
    r = vrun(st, vis, lv, tmp_path)
    assert r.returncode == 0, r.stdout + r.stderr
    assert "1 to update, 1 to remove" in r.stdout
