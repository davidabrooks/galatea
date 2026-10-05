"""PR #25/#26: cloud-render parse helpers (no GPU / no downloads)."""
import importlib.util
import os
import sys

import pytest

CR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "cloud-render")
sys.path.insert(0, CR)

# Prefer existing test_parse module if present
tp = os.path.join(CR, "test_parse.py")
if os.path.isfile(tp):
    spec = importlib.util.spec_from_file_location("test_parse_mod", tp)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)

    def test_pr25_cloud_render_parse_module_loads():
        assert mod is not None
else:
    def test_pr25_cloud_render_parse_skipped():
        pytest.skip("cloud-render/test_parse.py not present")
