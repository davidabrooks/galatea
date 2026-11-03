import os, sys
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import look

def test_support_line_forwarded_with_prefix():
    l = "GT: furniture support: sitting; bearing hips,thighs; gap +0.035 m; parts hips +0.030"
    assert look.client_line(l) == l

def test_gap_lines_bare_and_others_dropped():
    assert look.client_line("GT: seat contact gap +0.01") == "seat contact gap +0.01"
    assert look.client_line("GT: render seconds 3") is None
