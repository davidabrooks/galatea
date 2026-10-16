"""2026-10-16: texture cache (raw JPEG2000 tier + decoded tier, caps with oldest-first eviction, negative cache, legacy
uuid.png migration). Pure: no network, no real decode."""
import importlib.util, os, sys, time, types
if "PIL" not in sys.modules:   # CI has no Pillow (see test_crowd_textures)
    try: import PIL.Image  # noqa: F401
    except ImportError: pil = types.ModuleType("PIL"); pil.Image = None; sys.modules["PIL"] = pil
HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(HERE, "cloud-render"))
_spec = importlib.util.spec_from_file_location("gt_real_handler_tc", os.path.join(HERE, "cloud-render", "handler.py"))
handler = importlib.util.module_from_spec(_spec); _spec.loader.exec_module(handler)   # other tests stub `handler`

U = ["%08x-0000-0000-0000-000000000000" % i for i in range(1, 9)]


def _file(path, size, age):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f: f.write(b"x" * size)
    t = time.time() - age; os.utime(path, (t, t))


def test_raw_key_is_per_uuid_in_first_digit_subdir(tmp_path):
    tex = str(tmp_path / "work" / "tex")
    p = handler._raw_path(tex, "a1b2c3d4-0000-0000-0000-000000000000")
    assert p == str(tmp_path / "work" / "tex-raw" / "a" / "a1b2c3d4-0000-0000-0000-000000000000.j2c")   # no size in the key


def test_prune_evicts_oldest_first_down_to_80_percent_and_spares_other_files(tmp_path):
    d = str(tmp_path)
    for i, u in enumerate(U): _file(f"{d}/{u}.512.png", 100, age=1000 - i * 100)   # U[0] oldest
    _file(f"{d}/bake-abc.png", 5000, age=9999); _file(f"{d}/.missing.json", 5000, age=9999)
    files, before, after, n = handler.prune_cache(d, 500, handler.DEC_NAME.match)
    assert (files, before) == (8, 800) and after <= 400 and n == 4
    left = sorted(os.listdir(d))
    assert "bake-abc.png" in left and ".missing.json" in left                      # never evicted
    assert [u for u in U if f"{u}.512.png" in left] == U[4:]                        # the four newest survive


def test_prune_under_cap_is_a_noop_and_a_hit_refreshes_the_file(tmp_path):
    d = str(tmp_path)
    for i, u in enumerate(U): _file(f"{d}/{u}.64.png", 100, age=1000 - i * 100)
    assert handler.prune_cache(d, 10_000, handler.DEC_NAME.match)[3] == 0
    handler._touch(f"{d}/{U[0]}.64.png")                                             # a cache hit: now the newest
    handler.prune_cache(d, 500, handler.DEC_NAME.match)
    assert os.path.exists(f"{d}/{U[0]}.64.png") and not os.path.exists(f"{d}/{U[1]}.64.png")


def test_raw_tier_prunes_recursively_and_only_j2c(tmp_path):
    raw = tmp_path / "tex-raw"
    for i, u in enumerate(U): _file(str(raw / u[0] / f"{u}.j2c"), 100, age=1000 - i * 100)
    _file(str(raw / "0" / "notes.txt"), 9999, age=5000)
    r = handler.prune_cache(str(raw), 400, lambda n: n.endswith(".j2c"), recurse=True)
    assert r[0] == 8 and r[2] <= 320 and (raw / "0" / "notes.txt").exists()


def test_legacy_uuid_png_migration_deletes_only_legacy_once(tmp_path):
    d = str(tmp_path)
    for name in (f"{U[0]}.png", f"{U[1]}.png", f"{U[0]}.512.png", "bake-abc.png", ".missing.json"): _file(f"{d}/{name}", 10, 0)
    assert handler.retire_legacy_pngs(d) == 2
    assert sorted(os.listdir(d)) == sorted([".legacy-retired", ".missing.json", f"{U[0]}.512.png", "bake-abc.png"])
    _file(f"{d}/{U[2]}.png", 10, 0)
    assert handler.retire_legacy_pngs(d) == 0 and os.path.exists(f"{d}/{U[2]}.png")   # one time only


def test_negative_cache_ttl(tmp_path, monkeypatch):
    d = str(tmp_path)
    assert not handler._recent_miss(U[0], d)
    handler._note_miss(U[0], d)
    assert handler._recent_miss(U[0], d) and not handler._recent_miss(U[1], d)
    real = time.time; monkeypatch.setattr(handler.time, "time", lambda: real() + 7 * 3600)
    assert not handler._recent_miss(U[0], d)                                          # 6 h TTL


def test_second_size_reuses_the_raw_bytes_and_a_dead_texture_is_asked_once(tmp_path, monkeypatch):
    tex = str(tmp_path / "work" / "tex"); os.makedirs(tex)
    gets = []
    def fake_get(u):
        gets.append(u)
        if u == U[7]: raise OSError("timed out")
        return b"j2c-" + u.encode()
    monkeypatch.setattr(handler, "_cdn_get", fake_get)
    monkeypatch.setattr(handler, "to_png", lambda data, path, cap: open(path, "wb").write(data + b"@%d" % cap))
    assert handler.fetch_tex(U[0], tex, 64)[1].endswith(f"{U[0]}.64.png")
    assert handler.fetch_tex(U[0], tex, 2048)[1].endswith(f"{U[0]}.2048.png")        # new size: decoded from the raw file
    assert handler.fetch_tex(U[0], tex, 2048)[1] and gets == [U[0]]                   # one download for two sizes
    assert handler.fetch_tex(U[7], tex, 64)[1] is None and handler.fetch_tex(U[7], tex, 64)[2].startswith("recent CDN miss")
    assert gets == [U[0], U[7]]                                                       # the dead one: asked once, then remembered
