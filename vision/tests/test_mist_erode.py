"""2026-10-18 speckled hair: the haze (compositor, from the Mist pass) of a pixel that mixes near hashed-alpha hair with far sky / backdrop was
the MEAN of the two mists (~2000 m of haze on a surface 3 m away) and un-denoised: pale lavender speckles over the hair, a pale fringe round
the stage truss. The fog now uses a minimum-filtered (eroded) mist, i.e. the nearest surface within a couple of pixels."""
import os

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = open(os.path.join(HERE, "cloud-render", "render_mesh.py")).read()


def test_mist_is_eroded_before_the_fog_in_non_batch_passes():
    assert 'MIST_ERODE = int(os.environ.get("GT_MIST_ERODE", "-1"))' in SRC
    assert 'CompositorNodeDilateErode' in SRC and 'er.distance = -er_px' in SRC and 'er.mode = "DISTANCE"' in SRC
    assert 'if LAYER != "batch" and er_px > 0:' in SRC          # batch layers keep the Mist-to-file path (avatar-only, no haze in the pixels)
    i = SRC.index('er.distance = -er_px'); j = SRC.index('ex = m("EXPONENT"')
    assert i < j and 'm("MULTIPLY", mist, -k * D)' in SRC and 'm("LESS_THAN", mist, 0.9999)' in SRC   # the fog is computed from the eroded mist
    assert 'L(rl.outputs["Mist"], fo.inputs[0])' in SRC          # the batch's solid-pixel mist file is the raw pass


def test_auto_width_scales_with_the_frame():
    assert 'max(1, round(min(sc.render.resolution_x, sc.render.resolution_y) / 1080))' in SRC   # 1 px at 960x540, 2 px at 4K
