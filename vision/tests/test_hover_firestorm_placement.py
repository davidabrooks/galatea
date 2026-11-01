"""2026-10-31 David's sit photo (19:08, VAMOSITG11): her legs/hips drew ~5 cm higher than in his Firestorm. The render placed her
with agent z - size/2 + hover - a per-pose bias (sit 0.099, stand 0.1285); Firestorm has no bias: root = agent z + agent_off
+ hover (llvoavatar.cpp:5646/5649/5887). With the sim's appearance hover (-0.08) taken out of her own agent z, Firestorm's
formula lands the approved stand 1.9 cm under the old stand bias and the sit 4.8 cm lower (hips on the floor, as in his photo)."""
from test_hover_floor_missing import _rm

AGENT_Z, FLOOR, AGENT_OFF = 28.873, 28.033, -0.9333   # 19:03:52 export; agent_off from scene-mesher (Firestorm body size 1.5709)

def _z0(hover, **extra):
    env = {"GT_HOVER_CHECK": "1", "GT_AGENT_SIZE": "1.572", "GT_HOVER": str(hover), "GT_SIM_HOVER": "-0.080", **extra}
    return _rm()["hover_check_z0"](env, AGENT_Z, 0.0, FLOOR, AGENT_OFF)[0]

def test_no_pose_bias_is_used():
    assert _z0(0.028) == _z0(0.028, GT_HOVER_BIAS="0.099") == _z0(0.028, GT_HOVER_BIAS="0.1285")

def test_firestorm_root_formula():
    assert abs(_z0(0.028) - (AGENT_Z + 0.08 + AGENT_OFF + 0.028)) < 1e-9

def test_approved_stand_unchanged_sit_lower():
    old = lambda hov, bias: AGENT_Z + 0.08 - 1.572 / 2 + hov - bias    # the biased model
    assert 0.015 < old(0.012, 0.1285) - _z0(0.012) < 0.025         # stand David approved: 1.9 cm lower (check with him)
    assert 0.04 < old(0.028, 0.099) - _z0(0.028) < 0.055              # the sit drew ~5 cm high

def test_hover_clamped_like_firestorm():
    assert _z0(5) == _z0(3)    # MAX_HOVER_Z, llvoavatar.cpp:147
