"""2026-10-13 graphics audit: SL particle systems snapshot (the purple orb on the Peronaut porch is particles)."""
import os, sys
HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(HERE, "cloud-render"))
import particles  # noqa: E402

ORB = {"part_max_age": 2.0, "burst_part_count": 2, "burst_rate": 0.1, "pattern": 2, "burst_radius": 0.0,
       "burst_speed_min": 0.0, "burst_speed_max": 0.05, "part_data_flags": 0x103,
       "part_start_color": [0.6, 0.2, 1.0, 0.8], "part_end_color": [0.6, 0.2, 1.0, 0.0],
       "part_start_scale": [0.3, 0.3, 0], "part_end_scale": [0.1, 0.1, 0], "part_start_glow": 0.3, "part_end_glow": 0.0}


def test_orb_snapshot_counts_colours_and_stays_near_source():
    ps = particles.snapshot(ORB, [233.79, 59.24, 29.6])
    assert len(ps) == 40 and all(p["emissive"] for p in ps)
    assert all(abs(p["pos"][2] - 29.6) <= 0.11 for p in ps)
    assert all(0.0 <= p["color"][3] <= 0.8 for p in ps) and all(0.1 <= p["size"][0] <= 0.3 for p in ps)


def test_no_particles_when_off_and_capped():
    assert particles.snapshot({"part_max_age": 0}, [0, 0, 0]) == []
    assert len(particles.snapshot({**ORB, "burst_part_count": 250, "part_max_age": 30}, [0, 0, 0])) == particles.MAX_PER_SOURCE
    drop = particles.snapshot({**ORB, "pattern": 1, "part_acceleration": [0, 0, -1]}, [0, 0, 10])
    assert all(p["pos"][2] <= 10 for p in drop)
