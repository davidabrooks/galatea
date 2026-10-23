import os, sys
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
from pose_contact import contact_lo

def test_contact_ignores_one_stray_vertex():
    zs = [0.10] * 1000 + [-0.5]          # a chain end dangling below the floor
    assert abs(contact_lo(zs, "sit") - 0.10) < 1e-6

def test_lower_pose_reads_lower():
    assert contact_lo([0.05] * 500 + [0.3] * 500) < contact_lo([0.09] * 500 + [0.3] * 500)

def test_empty():
    assert contact_lo([]) is None
