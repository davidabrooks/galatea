"""Tests for scripts/impact_tests.py (the impact-based local test selector). Pure python, no dotnet needed."""
import os, subprocess, sys
import pytest

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "scripts"))
import impact_tests as it  # noqa: E402

SRC = "textclient/galatay-text/"
TST = "textclient/GalatayText.Tests/"


def plan_for(files, changes, old=None, **kw):
    """files: {path: text} (new state); changes: {path: set(lines) | None | 'D'}; old: {path: text} for modified/deleted files."""
    ch = {}
    for p, v in changes.items():
        ch[p] = it.Change(p, None, "D") if v == "D" else it.Change(p, v, "A" if v is None else "M")
    return it.make_plan(it.MemTree(files), it.MemTree(old if old is not None else files), ch, **kw)


def line_of(text, needle):
    for i, l in enumerate(text.splitlines(), 1):
        if needle in l: return i
    raise AssertionError(needle)


# ------------------------------------------------------------------ tokenizer / parser
def toks(src): return [t[1] for t in it.tokenize(src) if t[0] == "op"]


def test_braces_inside_strings_comments_and_interpolation_are_not_structure():
    t = toks('var a = "{ not a block"; // } also not\n/* { */ var b = $"x {Foo("}")} y {{z}}"; var c = @"a ""{"" b"; var d = \'{\';')
    assert t.count("{") == 0 and t.count("}") == 0
    assert "Foo" in [x[1] for x in it.tokenize('var b = $"x {Foo(1)} y";')]   # interpolation holes still count as references


def test_raw_string_literal_is_one_token():
    t = it.tokenize('var s = """\n  { "quoted" }\n  """; int n;')
    assert [x for x in t if x[0] == "str"] and "n" in [x[1] for x in t]


SAMPLE = '''using System;
using static GalatayText.Program;
namespace GalatayText;
public static partial class Program
{
    const int Limit = 5;
    static readonly Func<int, int> Twice = x => { return x * 2; };
    internal static Task<(int a, int b)> Pair(int x, int y = 2) { return null; }
    static (float s, float d) Proj(this string t, float q) => (q, q);
    public int Prop { get; set; } = 3;
    int Expr => Limit + 1;
    internal sealed class Grid
    {
        public Grid(int n) { }
        public bool Contains(float x, float y) => true;
        int hidden;
    }
    enum Mode { A, B }
    internal sealed record Spot(string Name, int N);
    [Fact] public void T1() { Pair(1); }
    [Theory, InlineData("--x-selftest")] public void T2(string f) { }
}
'''


def members(src, path=SRC + "A.cs"):
    P = it.parse_cs(path, src)
    assert P.ok
    return {m.name: m for m in P.members}


def test_member_names_kinds_and_flags():
    m = members(SAMPLE)
    for n in ("Limit", "Twice", "Pair", "Proj", "Prop", "Expr", "Grid", "Contains", "hidden", "Mode", "Spot", "T1", "T2"):
        assert n in m, n
    assert m["Pair"].arity == (1, 2) and m["Contains"].arity == (2, 2) and m["Limit"].arity is None
    assert m["Proj"].ext and m["Proj"].arity == (1, 1)          # `this` parameter is not an argument
    assert m["Mode"].kind == "enum" and m["Spot"].kind == "type"
    assert m["T1"].is_test and m["T2"].is_test and not m["Pair"].is_test
    assert m["hidden"].private and not m["Contains"].private
    assert m["Contains"].cls == "Program.Grid" and m["Contains"].ns == "GalatayText"
    assert m["Twice"].end >= m["Twice"].start


def test_unbalanced_braces_make_the_file_unparsable():
    assert not it.parse_cs(SRC + "B.cs", "class A { void F() { }").ok


def test_diff_parser_maps_hunks_to_new_lines():
    d = ("diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -3,2 +3,3 @@\n@@ -10 +11,0 @@\n"
         "diff --git a/n b/n\n--- /dev/null\n+++ b/n\n@@ -0,0 +1,2 @@\n")
    ch = it.parse_diff(d)
    assert ch["x.cs"].lines == {3, 4, 5, 11, 12} and ch["n"].status == "A" and ch["n"].lines is None


# ------------------------------------------------------------------ closure
LIB = """namespace GalatayText;
public static partial class Program
{
    internal static float Core(float v) { return v * 2; }
    internal static float Mid(float v) { return Core(v) + 1; }
    internal static float Other(float v) { return v; }
    internal static string Name() { return "x"; }
}
internal sealed class Grid
{
    public bool Contains(float x, float y) { return true; }
    private int secret;
}
"""
TESTS = """using Xunit;
namespace GalatayText.Tests;
public class MidTests
{
    [Fact] public void Uses_mid() { Assert.Equal(3, Program.Mid(1)); }
}
public class OtherTests
{
    [Fact] public void Uses_other() { Assert.Equal(1, Program.Other(1)); }
    [Fact] public void String_contains() { string s = "abc"; Assert.True(s.Contains("a", System.StringComparison.Ordinal)); }
    [Fact] public void Grid_contains() { var g = new Grid(); Assert.True(g.Contains(1, 2)); }
    [Fact] public void Local_name_only() { var Core = 1; Assert.Equal(1, Core); }
}
"""
FILES = {SRC + "Lib.cs": LIB, TST + "T.cs": TESTS}


def keys(plan): return sorted(m for _, m in plan.tests)


def test_changed_member_selects_direct_and_transitive_callers_only():
    p = plan_for(FILES, {SRC + "Lib.cs": {line_of(LIB, "float Core")}})
    assert keys(p) == ["Uses_mid"] and not p.full


def test_unrelated_member_change_selects_only_its_users():
    p = plan_for(FILES, {SRC + "Lib.cs": {line_of(LIB, "float Other")}})
    assert keys(p) == ["Uses_other"]


def test_member_access_receiver_type_and_arity_narrow_the_selection():
    p = plan_for(FILES, {SRC + "Lib.cs": {line_of(LIB, "bool Contains")}})
    assert keys(p) == ["Grid_contains"]            # string.Contains(a, b) has a BCL-typed receiver: not Grid


def test_private_member_is_not_reached_from_other_classes():
    p = plan_for(FILES, {SRC + "Lib.cs": {line_of(LIB, "private int secret")}})
    assert keys(p) == []


def test_comment_or_blank_only_change_runs_nothing():
    src = LIB.replace("    internal static float Other", "// note\n\n    internal static float Other")
    p = plan_for({**FILES, SRC + "Lib.cs": src}, {SRC + "Lib.cs": {6, 7}}, old=FILES)
    assert not p.full and not p.tests


def test_removed_member_counts_as_changed():
    new = LIB.replace("    static float Other(float v) { return v; }\n", "")
    p = plan_for({**FILES, SRC + "Lib.cs": new}, {SRC + "Lib.cs": {5, 6}}, old=FILES)
    assert "Uses_other" in keys(p)


def test_file_level_change_marks_every_member_of_the_file():
    p = plan_for(FILES, {SRC + "Lib.cs": {1}})     # the `namespace` line
    assert {"Uses_mid", "Uses_other", "Grid_contains"} <= set(keys(p))


def test_changed_test_method_selects_just_itself():
    p = plan_for(FILES, {TST + "T.cs": {line_of(TESTS, "Uses_mid")}})
    assert keys(p) == ["Uses_mid"]


def test_new_file_selects_its_own_tests():
    t2 = "using Xunit;\nnamespace GalatayText.Tests;\npublic class NewTests { [Fact] public void Brand_new() { } }\n"
    p = plan_for({**FILES, TST + "N.cs": t2}, {TST + "N.cs": None})
    assert keys(p) == ["Brand_new"]


def test_deleted_source_selects_its_former_users():
    files = {k: v for k, v in FILES.items() if k != SRC + "Lib.cs"}
    p = plan_for(files, {SRC + "Lib.cs": "D"}, old=FILES)
    assert {"Uses_mid", "Uses_other"} <= set(keys(p))


def test_selection_over_the_ratio_falls_back_to_full():
    p = plan_for(FILES, {SRC + "Lib.cs": {line_of(LIB, "float Core")}}, full_ratio=0.1)
    assert p.full and "not worth the risk" in p.full_reasons[0]


# ------------------------------------------------------------------ selftest flags
MAIN = """namespace GalatayText;
public static partial class Program
{
    static string PoseSelfTest() { return "ok"; }
    static string WornSelfTest() { return "ok"; }
    public static int Main(string[] args)
    {
        if (args.Contains("--worn-selftest")) { var r = WornSelfTest(); return 0; }
        if (args.Contains("--pose-selftest"))
        {
            var r = PoseSelfTest(); return 0;
        }
        return 2;
    }
}
"""
CLI = """using Xunit;
namespace GalatayText.Tests;
public class SelftestCliTests
{
    [Theory]
    [InlineData("--pose-selftest")]
    [InlineData("--worn-selftest")]
    public void Offline_selftest_passes(string flag) => SelftestRunner.AssertPass(flag);
}
"""


def test_selftest_theory_is_narrowed_to_the_flags_whose_code_changed():
    files = {SRC + "P.cs": MAIN, TST + "C.cs": CLI}
    p = plan_for(files, {SRC + "P.cs": {line_of(MAIN, "string PoseSelfTest")}})
    k = (("GalatayText.Tests.SelftestCliTests", "Offline_selftest_passes"))
    assert list(p.tests) == [k] and p.flags[k] == {"--pose-selftest"}
    f = it.csharp_filter(p)
    assert "DisplayName~--pose-selftest" in f and "--worn" not in f


def test_filter_string_lists_methods_with_fully_qualified_names():
    p = plan_for(FILES, {SRC + "Lib.cs": {line_of(LIB, "float Core")}})
    assert it.csharp_filter(p) == "FullyQualifiedName~GalatayText.Tests.MidTests.Uses_mid"


# ------------------------------------------------------------------ fallbacks and non-C# files
def test_project_build_and_ci_files_run_the_full_suite():
    for path in ("textclient/galatay-text/GalatayText.csproj", "textclient/build-all.sh", ".github/workflows/ci.yml",
                 "textclient/libremetaverse-render.patch", "pytest.ini", "scripts/ci-setup-libremetaverse.sh"):
        p = plan_for({**FILES, path: "x"}, {path: {1}})
        assert p.full, path


def test_docs_only_changes_run_nothing():
    files = {**FILES, "README.md": "x", "docs/capabilities.md": "x"}
    p = plan_for(files, {"README.md": {1}, "docs/capabilities.md": {2}})
    assert not p.full and not p.tests and not p.py_tests and not p.extra_cmds


def test_unparsable_csharp_runs_the_full_suite():
    p = plan_for({**FILES, SRC + "Lib.cs": LIB + "{"}, {SRC + "Lib.cs": {4}}, old=FILES)
    assert p.full


def test_unreferenced_non_doc_file_runs_the_full_suite():
    p = plan_for({**FILES, "textclient/mystery.bin": "x"}, {"textclient/mystery.bin": {1}})
    assert p.full and "references" in p.full_reasons[0]


def test_data_file_selects_the_code_and_tests_that_name_it():
    lib = LIB.replace('return "x";', 'return File.ReadAllText("routes/_thing.json");')
    tst = TESTS.replace("Assert.Equal(1, Program.Other(1));", 'Assert.NotNull(System.IO.File.ReadAllText("textclient/routes/_thing.json"));')
    files = {SRC + "Lib.cs": lib, TST + "T.cs": tst, "textclient/routes/_thing.json": "{}"}
    p = plan_for(files, {"textclient/routes/_thing.json": {1}})
    assert not p.full and "Uses_other" in keys(p) and "Uses_mid" not in keys(p)


def test_python_change_selects_pytests_that_mention_it():
    files = {**FILES, "pytest.ini": "[pytest]\ntestpaths =\n    vision/tests\n", "vision/look.py": "x = 1\n",
             "vision/tests/test_look.py": "import look\n", "vision/tests/test_other.py": "import other\n"}
    p = plan_for(files, {"vision/look.py": {1}})
    assert list(p.py_tests) == ["vision/tests/test_look.py"] and not p.tests and not p.full


# ------------------------------------------------------------------ the real repo
def real_tree():
    return it.WorkTree(ROOT)


@pytest.mark.skipif(not os.path.isdir(os.path.join(ROOT, "textclient", "galatay-text")), reason="needs the repo checkout")
def test_every_real_csharp_file_parses():
    t = real_tree()
    bad = [p for p in t.files() if it.is_cs_source(p) and not it.parse_cs(p, t.read(p)).ok]
    assert bad == []


@pytest.mark.skipif(not os.path.isdir(os.path.join(ROOT, "textclient", "galatay-text")), reason="needs the repo checkout")
def test_changing_one_real_test_method_selects_exactly_that_method():
    t = real_tree()
    path = TST + "DoorPreOpenTests.cs"
    P = it.parse_cs(path, t.read(path))
    m = next(x for x in P.members if x.is_test)
    plan = it.make_plan(t, t, {path: it.Change(path, {m.start + 1} if m.end > m.start else {m.start}, "M")})
    assert not plan.full and [k[1] for k in plan.tests] == [m.name]


# ------------------------------------------------------------------ build step
def test_build_all_runs_only_when_needed():
    ch = {SRC + "Lib.cs": {line_of(LIB, "float Core")}}
    assert not plan_for(FILES, ch).needs_build                                   # dotnet test compiles the client itself
    uncovered = plan_for(FILES, {SRC + "Lib.cs": {line_of(LIB, "string Name")}})
    assert not uncovered.tests and uncovered.needs_build                         # nothing else would compile it
    files = {SRC + "P.cs": MAIN, TST + "C.cs": CLI}
    assert plan_for(files, {SRC + "P.cs": {line_of(MAIN, "string PoseSelfTest")}}).needs_build   # selftests need the fresh binary
    assert not plan_for(files, {TST + "C.cs": {line_of(CLI, "Offline_selftest_passes")}}).needs_build   # test-only change
