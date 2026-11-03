# look.py imports the render stack (imagecodecs, ...), absent in light CI: load only the pure client_line() from its source
import ast, os
SRC = os.path.join(os.path.dirname(__file__), "..", "look.py")
_tree = ast.parse(open(SRC).read())
_fn = next(n for n in _tree.body if isinstance(n, ast.FunctionDef) and n.name == "client_line")
_ns = {}; exec(compile(ast.Module([_fn], []), SRC, "exec"), _ns); client_line = _ns["client_line"]

def test_support_line_forwarded_with_prefix():
    l = "GT: furniture support: sitting; bearing hips,thighs; gap +0.035 m; parts hips +0.030"
    assert client_line(l) == l

def test_gap_lines_bare_and_others_dropped():
    assert client_line("GT: seat contact gap +0.01") == "seat contact gap +0.01"
    assert client_line("GT: render seconds 3") is None
