"""galatea-wt-report.sh verdicts: named branches match a PR by head only, never by SHA;
the newest deploy/* worktree is KEEP (live rollback); a superseded one may be SAFE if clean."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "galatea-wt-report.sh"


def _helpers():
    text = SCRIPT.read_text()
    start = text.index("# --- classify helpers (tested) ---")
    end = text.index("# --- end classify helpers ---")
    ns = {}
    exec(compile(text[start:end], str(SCRIPT), "exec"), ns)
    return ns


def _row(**kw):
    r = {
        "path": "/workspace/wt/example",
        "branch": "fix/example",
        "pr_state": "",
        "unpushed": "none",
        "dirty": 0,
        "in_use": False,
    }
    r.update(kw)
    return r


def test_helpers_are_present_and_sha_match_is_detached_only():
    h = _helpers()
    assert "classify" in h and "pr_for" in h
    src = SCRIPT.read_text()
    # named-branch SHA fallback must be gone; only detached HEADs may match by sha
    assert "elif head in by_sha:" not in src
    assert "if detached and head in by_sha:" in src
    assert "pr=pr_for(branch, detached, head, by_branch, by_sha)" in src


def test_pr_for_named_branch_does_not_use_sha():
    h = _helpers()
    pr = {"number": 484, "state": "MERGED", "headRefName": "deps/lmv-master-3ee812f"}
    by_branch = {pr["headRefName"]: pr}
    by_sha = {"83d0b8c6c8f11578b4f30037e9d6b895c53c5357": pr}
    # deploy/* (or any named non-PR branch) sitting on that merge commit
    assert h["pr_for"]("deploy/20261123", False, "83d0b8c6c8f11578b4f30037e9d6b895c53c5357", by_branch, by_sha) is None
    assert h["pr_for"]("fix/from-main", False, "83d0b8c6c8f11578b4f30037e9d6b895c53c5357", by_branch, by_sha) is None
    # the actual PR branch still matches by name
    assert h["pr_for"]("deps/lmv-master-3ee812f", False, "unused", by_branch, by_sha) is pr
    # detached HEAD may match by SHA
    assert h["pr_for"]("(detached)", True, "83d0b8c6c8f11578b4f30037e9d6b895c53c5357", by_branch, by_sha) is pr


def test_newest_deploy_is_live_rollback():
    h = _helpers()
    newest = h["newest_deploy_branch"](["deploy/20261101", "fix/foo", "deploy/20261123"])
    assert newest == "deploy/20261123"
    r = _row(path="/workspace/wt/deploy-20261123", branch="deploy/20261123", pr_state="MERGED")
    assert h["classify"](r, newest) == "KEEP (live rollback)"
    # even if dirty or in use, the newest deploy tree is the live rollback
    assert h["classify"](_row(path="/workspace/wt/deploy-20261123", branch="deploy/20261123", dirty=2, in_use=True), newest) == "KEEP (live rollback)"


def test_superseded_deploy_safe_only_if_clean_and_pushed():
    h = _helpers()
    newest = "deploy/20261123"
    old = _row(path="/workspace/wt/deploy-20261101", branch="deploy/20261101")
    assert h["classify"](old, newest) == "SAFE (mechanical)"
    assert h["classify"](_row(**{**old, "dirty": 1}), newest) == "KEEP (uncommitted)"
    assert h["classify"](_row(**{**old, "unpushed": "2 unique"}), newest) == "KEEP (unpushed)"
    assert h["classify"](_row(**{**old, "in_use": True}), newest) == "KEEP (in use)"


def test_named_non_pr_branch_on_merge_sha_is_not_safe():
    h = _helpers()
    # after pr_for ignores SHA, pr_state stays empty
    r = _row(path="/workspace/wt/from-main", branch="fix/from-main", pr_state="")
    assert h["classify"](r, None) == "LOOK (no PR match)"


def test_merged_pr_branch_and_detached_head_still_safe():
    h = _helpers()
    assert h["classify"](_row(branch="deps/lmv-master-3ee812f", pr_state="MERGED"), None) == "SAFE (mechanical)"
    assert h["classify"](_row(branch="(detached)", pr_state="MERGED"), None) == "SAFE (mechanical)"
    assert h["classify"](_row(branch="feat/open", pr_state="OPEN"), None) == "KEEP (PR OPEN)"
    assert h["classify"](_row(path="/workspace/galatea", branch="main"), "deploy/20261123") == "KEEP (host clone)"
