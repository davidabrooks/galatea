"""2026-10-13 20:05 photo hd: David (15/16 attachments) rendered as a grey stand-in block. HD photos omit stand-ins."""
import os, sys, types
HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.modules.setdefault("bpy", types.ModuleType("bpy"))
SRC = open(os.path.join(HERE, "cloud-render", "render_mesh.py")).read()
ns = {}
exec(SRC[SRC.index("def omit_stand_in"):SRC.index("def terrain_layers")], ns)


def test_hd_omits_stand_ins_looks_keep_them():
    assert ns["omit_stand_in"]({"GT_HD": "1"}) and not ns["omit_stand_in"]({}) and not ns["omit_stand_in"]({"GT_HD": "1", "GT_STANDIN": "1"})


def test_stand_in_is_logged_never_silent():
    assert 'GT: avatar omitted' in SRC and 'GT: avatar stand-in' in SRC
