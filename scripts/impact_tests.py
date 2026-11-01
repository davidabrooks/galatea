#!/usr/bin/env python3
"""impact_tests.py: run only the tests a change can affect (local speed-up; CI still runs the full suite).

  python3 scripts/impact_tests.py                 # diff vs origin/main (+ uncommitted + untracked), run the impacted tests
  python3 scripts/impact_tests.py --dry-run       # only print what it would run, and why
  python3 scripts/impact_tests.py --full          # everything CI runs (C# xUnit + pytest)
  python3 scripts/impact_tests.py --commit SHA --dry-run   # analyse a past commit / merge (SHA^..SHA)
  python3 scripts/impact_tests.py --range A..B --dry-run

How the C# mapping works (static, name based, no compiler):
  1. Every .cs under textclient/ (client, MCP, SlTextureVision, tests) is tokenised and split into members
     (methods, properties, fields, ctors, enums). Each member records the identifiers and string literals in it.
  2. Changed line ranges from the diff pick the changed members (blank/comment-only lines are ignored; a member
     that disappeared counts as changed; a line outside any member, e.g. `using`, changes the whole file).
  3. Impact = reverse closure: any member whose body mentions the name of an impacted member is impacted too,
     repeated until nothing new. Same-name overloads/collisions only ever ADD tests (over-approximation).
  4. Impacted [Fact]/[Theory] methods are the selection. `--x-selftest` flags in Program.Main are nodes too, so the
     SelftestCliTests theory is narrowed to the flags whose code changed. A changed client/MCP source is always
     built by build-all.sh when a selected test uses the binary or no test covers it, so it is never left uncompiled.
  5. Data/script files (routes/*.json, *.py, *.sh ...) map to the tests/code whose string literals or text name them.
Whenever the mapping is unsure it runs the FULL suite: project/build/CI files, unparsable C#, a changed file nothing
references, or when the selection is > FULL_RATIO of all tests. Docs-only changes run nothing.
Limits: reflection / string-built names / config read at runtime are invisible to it; a changed C# string that a
test only checks by value is covered only through the member that holds it. CI is the safety net.
"""
import argparse, fnmatch, os, re, subprocess, sys
from collections import defaultdict, deque

FULL_RATIO = 0.6
SRC_EXCLUDE = ("textclient/src-libremetaverse/", "/bin/", "/obj/", "textclient/app-staging/", "textclient/mcp-app-staging/",
               "textclient/mcp-app-next/", "textclient/sl-texture-vision/", "textclient/linden/")
TEST_DIR = "textclient/GalatayText.Tests/"
FULL_PATTERNS = [".github/*", "*.csproj", "*.sln", "*.props", "*.targets", "global.json", "nuget.config",
                 "textclient/build-all.sh", "scripts/ci-setup-libremetaverse.sh", "textclient/*.patch", "pytest.ini",
                 "requirements-ci.txt", "*conftest.py", "textclient/GalatayText.Tests/*.json"]
DOC_ONLY = ["docs/*", "*.md", "LICENSE", ".gitignore"]  # md under routes/ etc. still checked for references first
KEYWORD_TYPES = {"class", "struct", "interface", "record", "enum"}
TEST_ATTRS = {"Fact", "Theory", "SkippableFact"}
KEYWORDS = set("""abstract as base bool break byte case catch char checked class const continue decimal default delegate do double
else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long
namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof
stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void
volatile while var async await get set value record init required partial where when with yield nameof global dynamic""".split())
TYPE_KW = {"string", "int", "float", "double", "bool", "char", "byte", "long", "uint", "ulong", "short", "object", "decimal", "var"}
MODIFIERS = {"public", "private", "protected", "internal", "static", "readonly", "async", "override", "virtual", "abstract",
             "sealed", "unsafe", "new", "extern", "partial", "const", "volatile", "ref", "out", "in"}
FLAG_RE = re.compile(r"^--[a-z0-9-]+-selftest$")

# ---------------------------------------------------------------- C# tokenizer
_TOK = re.compile(r"""
 (?P<nl>\n) | (?P<ws>[ \t\r\f]+) | (?P<lc>//[^\n]*) | (?P<bc>/\*.*?\*/) | (?P<pp>(?<![^\n])[ \t]*\#[^\n]*)
|(?P<verb>@"(?:[^"]|"")*") | (?P<str>"(?:[^"\\\n]|\\.)*") | (?P<chr>'(?:[^'\\\n]|\\.)+')
|(?P<id>@?[A-Za-z_]\w*) | (?P<num>\d\w*(?:\.\d+)?) | (?P<op>=>|==|!=|<=|>=|\?\?=?|&&|\|\||.)
""", re.X | re.S)


def _scan_interp(s, i, verbatim):
    """s[i] is just after the opening quote of an interpolated string. Return (end index, literal text, hole texts)."""
    out, holes, n = [], [], len(s)
    while i < n:
        c = s[i]
        if c == '"':
            if verbatim and i + 1 < n and s[i + 1] == '"': out.append('"'); i += 2; continue
            return i + 1, "".join(out), holes
        if c == "\\" and not verbatim: out.append(s[i:i + 2]); i += 2; continue
        if c == "{":
            if i + 1 < n and s[i + 1] == "{": out.append("{"); i += 2; continue
            depth, j = 1, i + 1
            while j < n and depth:
                d = s[j]
                if d == '"':  # nested string in the hole
                    j += 1
                    while j < n and s[j] != '"': j += 2 if s[j] == "\\" else 1
                elif d == "'":
                    j += 1
                    while j < n and s[j] != "'": j += 2 if s[j] == "\\" else 1
                elif d == "{": depth += 1
                elif d == "}": depth -= 1
                j += 1
            holes.append(s[i + 1:j - 1]); out.append("{" + s[i + 1:j - 1] + "}"); i = j; continue
        out.append(c); i += 1
    return n, "".join(out), holes


def tokenize(src, line0=1):
    """-> list of (kind, text, line); kind in id/str/op. Raises nothing; odd input just yields odd tokens."""
    toks, i, n, line = [], 0, len(src), line0
    while i < n:
        c = src[i]
        # interpolated / raw strings need a hand scan
        if c in '$@"':
            pre = re.match(r'[$@]*', src[i:]).group(0)
            k = i + len(pre)
            if k < n and src[k] == '"' and ("$" in pre or src.startswith('"""', k)):
                if src.startswith('"""', k):  # raw string """ ... """
                    q = re.match(r'"{3,}', src[k:]).group(0)
                    end = src.find(q, k + len(q))
                    end = n if end < 0 else end + len(q)
                    body = src[i:end]
                    toks.append(("str", body, line))
                    if "$" in pre:
                        for h in re.findall(r"\{([^{}]*)\}", body): toks += tokenize(h, line)
                    line += body.count("\n"); i = end; continue
                j, lit, holes = _scan_interp(src, k + 1, "@" in pre)
                toks.append(("str", lit, line))
                for h in holes: toks += tokenize(h, line)
                line += src[i:j].count("\n"); i = j; continue
        m = _TOK.match(src, i)
        if not m: i += 1; continue
        k, t = m.lastgroup, m.group()
        if k == "nl": line += 1
        elif k in ("bc", "pp"): line += t.count("\n")
        elif k in ("id", "op"): toks.append((k, t.lstrip("@") if k == "id" else t, line))
        elif k == "num": toks.append(("num", t, line))
        elif k == "str": toks.append(("str", t[1:-1], line))
        elif k == "verb": toks.append(("str", t[2:-1], line)); line += t.count("\n")
        elif k == "chr": toks.append(("op", "'c'", line))
        i = m.end()
    return toks


# ---------------------------------------------------------------- C# member parser
class Member:
    __slots__ = ("file", "name", "cls", "ns", "start", "end", "ids", "strs", "is_test", "kind", "flags", "uid",
                 "unq", "pairs", "dots", "ext", "using_static", "private", "decl", "arity")

    def __init__(s, **kw):
        for k, v in kw.items(): setattr(s, k, v)

    @property
    def fqn(s): return f"{s.ns}.{s.cls}.{s.name}" if s.ns else f"{s.cls}.{s.name}"


class Parsed:
    def __init__(s):
        s.members, s.types, s.directive_lines, s.token_lines, s.ok, s.ns = [], [], set(), set(), True, ""
        s.using_static = set()


def _nargs(hdr, i):
    """hdr[i] is '(' : number of top-level arguments (0 for '()'), None when unsure (generic commas, unbalanced)."""
    d, commas, any_tok, n = 0, 0, False, len(hdr)
    j = i
    while j < n:
        k, t, _ = hdr[j]
        if k == "op" and t in "([{": d += 1
        elif k == "op" and t in ")]}":
            d -= 1
            if d == 0: return (commas + 1) if any_tok else 0
        elif d == 1:
            if k == "op" and t == ",": commas += 1
            elif k == "op" and t == "<": return None
            else: any_tok = True
        else: any_tok = True
        j += 1
    return None


def _arity(hdr, i, ext):
    """(min, max) parameter count of the parameter list opening at hdr[i]; max None = params[]."""
    d, n, cnt, opt, cur, big = 0, len(hdr), 0, 0, [], False
    j, params = i, []
    while j < n:
        k, t, _ = hdr[j]
        if k == "op" and t in "([{": d += 1
        elif k == "op" and t in ")]}":
            d -= 1
            if d == 0:
                if cur: params.append(cur)
                break
        if d == 1 and k == "op" and t == ",": params.append(cur); cur = []
        elif not (d == 1 and j == i): cur.append((k, t))
        j += 1
    if ext and params: params = params[1:]
    lo = sum(1 for p_ in params if not any(x == ("op", "=") for x in p_) and not any(x == ("id", "params") for x in p_))
    hi = None if any(("id", "params") in p_ for p_ in params) else len(params)
    return (lo, hi)


def parse_cs(path, src):
    toks = tokenize(src)
    P = Parsed(); P.token_lines = {t[2] for t in toks}
    nt = len(toks)

    def balanced(j):  # toks[j] is '{'; return index after matching '}'
        d = 0
        while j < nt:
            t = toks[j][1] if toks[j][0] == "op" else None
            if t == "{": d += 1
            elif t == "}":
                d -= 1
                if d == 0: return j + 1
            j += 1
        P.ok = False
        return nt

    def finish(hdr, start, end, cls, kind="member", tkind="class"):
        ids, strs, is_test, br = set(), [], False, 0
        name, angle, pr, prev, ext, done, first_paren, mods, param_open = None, 0, 0, None, False, False, None, set(), None
        for idx, (k, t, ln) in enumerate(hdr):
            if k == "id" and t not in KEYWORDS: ids.add(t)
            elif k == "str": strs.append(t)
            if k == "op" and t == "[": br += 1
            elif k == "op" and t == "]": br -= 1
            elif br:
                if k == "id" and t in TEST_ATTRS: is_test = True
            elif k == "op":
                if t == "(" and first_paren is None:
                    first_paren = idx
                if done: pass
                elif t == "<" and prev and prev[0] == "id": angle += 1
                elif t == ">" and angle: angle -= 1
                elif angle: pass
                elif t == "(":
                    if pr == 0 and prev and prev[0] == "id" and prev[1] not in MODIFIERS: done = True; param_open = idx  # parameter list
                    else: pr += 1                                                                      # tuple type
                elif t == ")": pr -= 1
                elif pr == 0 and t in ("=", "=>", "{"): done = True
            elif k == "id" and not done and not angle and pr == 0:
                name = t
                if t in MODIFIERS: mods.add(t)
            prev = (k, t)
        # references: unqualified, Type.member pairs, member access on something else (receiver variable name or None)
        unq, pairs, dots = set(), set(), set()
        for idx, (k, t, ln) in enumerate(hdr):
            if k != "id" or t in KEYWORDS: continue
            pv = hdr[idx - 1] if idx else None
            nx = hdr[idx + 1] if idx + 1 < len(hdr) else None
            if pv and pv[0] == "op" and pv[1] == ".":
                pp = hdr[idx - 2] if idx >= 2 else None
                na = _nargs(hdr, idx + 1) if nx and nx[0] == "op" and nx[1] == "(" else None
                if pp and pp[0] == "id" and pp[1] not in KEYWORDS - {"this", "base"}: pairs.add((pp[1], t, na))
                else: dots.add((None, t, na))
            else:
                unq.add(t)
                if pv and pv[0] == "op" and pv[1] in ("{", ",") and nx and nx[0] == "op" and nx[1] == "=": dots.add((None, t, None))  # object initializer
        # declared types of locals / parameters: `Type name` followed by = ; , ) : in  (None = unknown or conflicting)
        decl, n_ = {}, len(hdr)
        for idx in range(n_):
            k, t, ln = hdr[idx]
            if k != "id" or (t in KEYWORDS and t not in TYPE_KW): continue
            j = idx + 1
            if j < n_ and hdr[j][1] == "<":
                d = 0
                while j < n_ and hdr[j][0] in ("id", "op") and (hdr[j][0] == "id" or hdr[j][1] in "<>,.?[]"):
                    d += (hdr[j][1] == "<") - (hdr[j][1] == ">")
                    j += 1
                    if d == 0: break
                if d: continue
            while j < n_ and hdr[j][1] in ("?", "[", "]"): j += 1
            if j + 1 >= n_ or hdr[j][0] != "id" or hdr[j][1] in KEYWORDS: continue
            nxt = hdr[j + 1]
            if not ((nxt[0] == "op" and nxt[1] in ("=", ";", ",", ")", ":")) or nxt[1] == "in"): continue
            ty = t
            if t == "var":
                ty = None
                if nxt[1] == "=" and j + 2 < n_:
                    if hdr[j + 2][1] == "new" and j + 3 < n_ and hdr[j + 3][0] == "id": ty = hdr[j + 3][1]
                    elif hdr[j + 2][0] == "str": ty = "string"
            v = hdr[j][1]
            decl[v] = ty if v not in decl or decl[v] == ty else None
        ext = param_open is not None and param_open + 1 < len(hdr) and hdr[param_open + 1][1] == "this"
        arity = _arity(hdr, param_open, ext) if param_open is not None else None
        priv = not (mods & {"public", "internal", "protected"}) and tkind != "interface"
        P.members.append(Member(file=path, name=name or "<anon>", cls=".".join(cls), ns=P.ns, start=start, end=end, ids=ids,
                                strs=strs, is_test=is_test, kind=kind, flags=[], uid=None, unq=unq, pairs=pairs, dots=dots,
                                ext=ext, using_static=P.using_static, private=priv, decl=decl, arity=arity))

    def scan(i, cls, tkind="class"):
        """parse members at one nesting level (namespace or type body). Returns the index after the closing '}'."""
        while i < nt:
            hdr, br, pr, has_expr, j, start = [], 0, 0, False, i, toks[i][2]
            ended = False
            while j < nt and not ended:
                k, t, ln = toks[j]
                if k == "op":
                    if t == "[": br += 1
                    elif t == "]": br -= 1
                    elif t == "(": pr += 1
                    elif t == ")": pr -= 1
                    elif br == 0 and pr == 0:
                        if t in ("=", "=>"): has_expr = True
                        elif t == "}":  # end of this level (an unfinished member here is an enum's last item etc.)
                            if hdr: finish(hdr, start, ln, cls, "member", tkind)
                            return j + 1
                        elif t == ";":
                            ids = [x[1] for x in hdr if x[0] == "id"]
                            if ids and ids[0] in ("using", "extern", "namespace") or (ids[:1] == ["global"] and "using" in ids[:2]):
                                if ids[0] == "namespace": P.ns = ".".join(ids[1:])
                                if "using" in ids[:2] and "static" in ids[:3] and ids: P.using_static.add(ids[-1])
                                P.directive_lines.update(range(start, ln + 1))
                            elif hdr:
                                head = []
                                for kk, tt, _ in hdr:
                                    if kk == "op" and tt == "(": break
                                    if kk == "id": head.append(tt)
                                finish(hdr, start, ln, cls, "type" if KEYWORD_TYPES & set(head) or "delegate" in head else "member", tkind)
                            j += 1; ended = True; continue
                        elif t == "{":
                            pre = [x[1] for x in hdr if x[0] == "id"]
                            # keyword before any '(' / '=' means a type or namespace block
                            head = []
                            b2 = 0
                            for kk, tt, _ in hdr:
                                if kk == "op" and tt == "[": b2 += 1
                                elif kk == "op" and tt == "]": b2 -= 1
                                elif b2 == 0:
                                    if kk == "op" and tt in ("(", "=", "=>"): break
                                    if kk == "id": head.append(tt)
                            kw = [h for h in head if h in KEYWORD_TYPES or h == "namespace"]
                            if kw and not has_expr:
                                if kw[0] == "namespace":
                                    P.ns = ".".join(h for h in head[head.index("namespace") + 1:]); name = None
                                    P.directive_lines.add(ln)
                                    j = scan(j + 1, cls, tkind); ended = True; hdr = []; continue
                                name = head[head.index(kw[0]) + 1] if head.index(kw[0]) + 1 < len(head) else "<anon>"
                                if kw[0] == "enum":
                                    e = balanced(j)
                                    finish(hdr + toks[j:e], start, toks[e - 1][2], cls, "enum", tkind); P.types.append((name, start, toks[e - 1][2], "enum"))
                                    j = e; ended = True; continue
                                depth_start = start
                                j2 = scan(j + 1, cls + [name], kw[0])
                                P.types.append((name, depth_start, toks[j2 - 1][2] if j2 <= nt else toks[-1][2], kw[0]))
                                j = j2; ended = True; hdr = []; continue
                            e = balanced(j)
                            hdr += toks[j:e]; j = e
                            if has_expr: continue  # initializer / lambda: member runs to the ';'
                            nxt = toks[j] if j < nt else None
                            if nxt and nxt[0] == "op" and nxt[1] == "=": has_expr = True; continue  # `{ get; } = 5;`
                            finish(hdr, start, toks[e - 1][2], cls, "member", tkind); ended = True; continue
                hdr.append(toks[j]); j += 1
            i = j
            if not ended and j >= nt: break
        return nt

    scan(0, [])
    if sum(t == ("op", "{") for t in [(k, x) for k, x, _ in toks]) != sum(t == ("op", "}") for t in [(k, x) for k, x, _ in toks]): P.ok = False
    if not P.members and toks: P.ok = False
    return P


# ---------------------------------------------------------------- repository access
class Tree:
    """Read-only view of a repo state: .files() lists tracked paths, .read(path) returns text or None."""

    def files(self): raise NotImplementedError

    def read(self, path): raise NotImplementedError


class MemTree(Tree):
    def __init__(self, d): self.d = d
    def files(self): return sorted(self.d)
    def read(self, p): return self.d.get(p)


def _git(args, cwd, check=True):
    r = subprocess.run(["git"] + args, cwd=cwd, capture_output=True, text=True, errors="replace")
    if check and r.returncode: raise RuntimeError(f"git {' '.join(args)}: {r.stderr.strip()}")
    return r.stdout


class WorkTree(Tree):
    def __init__(self, root):
        self.root = root
        out = _git(["ls-files", "-z", "--cached", "--others", "--exclude-standard"], root)
        self._f = sorted({p for p in out.split("\0") if p and os.path.isfile(os.path.join(root, p))})

    def files(self): return self._f

    def read(self, p):
        try:
            with open(os.path.join(self.root, p), encoding="utf-8", errors="replace") as f: return f.read()
        except OSError: return None


class GitTree(Tree):
    def __init__(self, root, rev):
        self.root, self.rev = root, rev
        self._f = [p for p in _git(["ls-tree", "-r", "-z", "--name-only", rev], root).split("\0") if p]

    def files(self): return self._f

    def read(self, p):
        r = subprocess.run(["git", "show", f"{self.rev}:{p}"], cwd=self.root, capture_output=True)
        return r.stdout.decode("utf-8", "replace") if r.returncode == 0 else None


class Change:
    def __init__(self, path, lines=None, status="M"):
        self.path, self.lines, self.status = path, lines, status  # lines: set of new-file line numbers, None = whole file


def parse_diff(text):
    """unified diff (-U0) -> {path: Change}"""
    ch, cur, old = {}, None, None
    for ln in text.splitlines():
        if ln.startswith("--- "): old = ln[4:].strip(); continue
        if ln.startswith("+++ "):
            new = ln[4:].strip()
            path = old[2:] if new == "/dev/null" else new[2:]
            cur = ch.setdefault(path, Change(path, set(), "D" if new == "/dev/null" else ("A" if old == "/dev/null" else "M")))
            if cur.status in ("A", "D"): cur.lines = None
            continue
        m = re.match(r"@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", ln)
        if m and cur is not None and cur.lines is not None:
            s, c = int(m.group(1)), int(m.group(2) if m.group(2) is not None else 1)
            cur.lines.update(range(s, s + c) if c else (s, s + 1))
    return ch


def collect_changes(root, base=None, commit=None, rng=None):
    """-> (changes, old_tree, new_tree)"""
    if commit or rng:
        a, b = (f"{commit}^", commit) if commit else rng.split("..", 1)
        ch = parse_diff(_git(["diff", "-U0", "--no-color", "--no-ext-diff", "--no-renames", a, b], root))
        return ch, GitTree(root, a), GitTree(root, b)
    base = base or "origin/main"
    mb = _git(["merge-base", base, "HEAD"], root).strip()
    ch = parse_diff(_git(["diff", "-U0", "--no-color", "--no-ext-diff", "--no-renames", mb], root))
    for p in _git(["ls-files", "-z", "--others", "--exclude-standard"], root).split("\0"):
        if p and os.path.isfile(os.path.join(root, p)): ch[p] = Change(p, None, "A")
    return ch, GitTree(root, mb), WorkTree(root)


# ---------------------------------------------------------------- analysis
def is_cs_source(p):
    return p.endswith(".cs") and p.startswith("textclient/") and not any(x in "/" + p if x.startswith("/") else p.startswith(x) for x in SRC_EXCLUDE)


def _globs(p, pats): return any(fnmatch.fnmatch(p, g) for g in pats)


def _doc_only(p): return p.startswith("docs/") or "/" not in p and p.endswith(".md") or os.path.basename(p) in ("README.md", "LICENSE", ".gitignore")


class Plan:
    def __init__(self):
        self.full, self.full_reasons = False, []
        self.notes = []                       # one line per changed file: how it was classified
        self.tests = {}                       # (fqn class, method) -> reason chain string
        self.flags = defaultdict(set)         # (class, method) -> {selftest flags} when only reached through flags
        self.py_tests, self.extra_cmds = {}, []
        self.needs_build = self.uses_exe = False
        self.total_tests = 0

    def force_full(self, why):
        self.full = True
        if why not in self.full_reasons: self.full_reasons.append(why)


def _mentions(text, path):
    base = os.path.basename(path)
    if path.endswith(".py"): return re.search(r"\b" + re.escape(base[:-3]) + r"\b", text) is not None
    return base in text


def _last(cls): return cls.rsplit(".", 1)[-1]


def _test_key(m): return (f"{m.ns}.{m.cls}" if m.ns else m.cls, m.name)


def make_plan(new, old, changes, full_ratio=FULL_RATIO):
    plan = Plan()
    files = new.files()
    text = {}

    def read(p):
        if p not in text: text[p] = new.read(p) or ""
        return text[p]

    # ---- parse every C# file of the build (new state)
    parsed, members = {}, []
    for p in files:
        if is_cs_source(p):
            P = parse_cs(p, read(p)); parsed[p] = P
            for m in P.members: m.uid = len(members); members.append(m)
    old_parsed = {}
    for path, c in changes.items():
        if is_cs_source(path) and c.status != "A":
            src_ = old.read(path)
            if src_ is not None: old_parsed[path] = parse_cs(path, src_)
    typenames = {t[0] for P in list(parsed.values()) + list(old_parsed.values()) for t in P.types} \
        | {m.name for m in members if m.kind in ("type", "enum")}
    for m in members:   # Type.member pairs whose left side is no known type are member accesses on something else
        for a_, n_, na in list(m.pairs):
            if a_ in ("this", "base"): m.unq.add(n_); m.pairs.discard((a_, n_, na))
            elif a_ not in typenames: m.dots.add((a_, n_, na)); m.pairs.discard((a_, n_, na))
    by_any, by_unq, by_pair, by_dot = (defaultdict(set) for _ in range(4))
    for m in members:
        for x in m.ids: by_any[x].add(m.uid)
        for x in m.unq: by_unq[x].add(m.uid)
        for a_, n_, na in m.pairs: by_pair[(a_, n_)].add(m.uid)
        for r_, n_, na in m.dots: by_dot[n_].add((m.uid, r_, na))
        for st in m.strs:
            if FLAG_RE.match(st): by_any[st].add(m.uid)
    # --x-selftest flags in Main: a pseudo node per flag holding the identifiers of its `if (...) {...}` statement
    flag_nodes = {}
    for p, P in parsed.items():
        for m in P.members:
            if m.name != "Main" or not any(FLAG_RE.match(x) for x in m.strs): continue
            toks = [t for t in tokenize(read(p)) if m.start <= t[2] <= m.end]
            depth, dd = 0, []
            for k, t, ln in toks:
                if k == "op" and t == "{": depth += 1
                dd.append(depth)
                if k == "op" and t == "}": depth -= 1
            for idx, (k, t, ln) in enumerate(toks):
                if k == "str" and FLAG_RE.match(t) and t not in flag_nodes:
                    a_ = idx
                    while a_ > 0 and not (toks[a_][1] == "if" and dd[a_] == dd[idx]): a_ -= 1
                    b_ = idx + 1
                    while b_ < len(toks) and not (toks[b_][1] == "if" and dd[b_] == dd[idx]) and dd[b_] >= dd[idx]: b_ += 1
                    flag_nodes[t] = ({x[1] for x in toks[a_:b_] if x[0] == "id"}, m.cls)
    test_keys = {_test_key(m) for m in members if m.is_test}
    plan.total_tests = len(test_keys)

    # ---- seeds
    seeds = []          # Member-like objects whose consumers must be found
    direct = {}         # uid -> why: impacted without any reference step (changed test code, string references)
    py_changed, ref_files = [], []

    def virtual(cls, name, kind, why):
        v = Member(file="", name=name, cls=cls, ns="", start=0, end=0, ids=set(), strs=[], is_test=False, kind=kind, flags=[],
                   uid=None, unq=set(), pairs=set(), dots=set(), ext=False, using_static=set(), private=False, decl={}, arity=None)
        v.flags = why
        seeds.append(v)

    def seed_member(m, why):
        direct.setdefault(m.uid, why); m.flags = why; seeds.append(m)

    for path, c in sorted(changes.items()):
        if _globs(path, FULL_PATTERNS):
            plan.force_full(f"{path}: project/build/CI file"); plan.notes.append(f"{path}: build/CI file -> full suite"); continue
        if is_cs_source(path):
            newP = parsed.get(path)
            oldP = old_parsed.get(path)
            if c.status == "D":
                if oldP and oldP.ok:
                    for m in oldP.members: virtual(m.cls, m.name, m.kind, f"{path} deleted")
                    plan.notes.append(f"{path}: deleted -> its {len(oldP.members)} members count as changed")
                else: plan.force_full(f"{path}: deleted, could not parse the old version")
                continue
            if newP is None or not newP.ok or (oldP is not None and not oldP.ok):
                plan.force_full(f"{path}: C# file could not be parsed reliably"); plan.notes.append(f"{path}: unparsable -> full suite"); continue
            hit, whole = {}, False
            lines = range(1, max(newP.token_lines | {1}) + 1) if c.lines is None else sorted(c.lines)
            for ln in lines:
                if ln not in newP.token_lines: continue   # blank / comment-only line
                cand = [m for m in newP.members if m.start <= ln <= m.end]
                if cand:
                    m = min(cand, key=lambda x: x.end - x.start); hit[m.uid] = m
                elif ln in newP.directive_lines or not any(a_ <= ln <= b_ for _, a_, b_, _ in newP.types):
                    whole = True
                else:  # type-level line (header, attribute ...): the whole innermost type
                    n_, a_, b_, _ = min((t for t in newP.types if t[1] <= ln <= t[2]), key=lambda t: t[2] - t[1])
                    for m in newP.members:
                        if a_ <= m.start and m.end <= b_: hit[m.uid] = m
                    if n_ != "Program": virtual(n_, n_, "type", f"{path}: type {n_} changed")
            if whole: hit = {m.uid: m for m in newP.members}
            removed = set()
            if oldP:
                have = {(m.cls, m.name) for m in newP.members}
                for m in oldP.members:
                    if (m.cls, m.name) not in have: virtual(m.cls, m.name, m.kind, f"{path}: removed {m.name}"); removed.add(m.name)
            for m in hit.values(): seed_member(m, f"{os.path.basename(path)}:{m.name}" + (" (file-level change)" if whole else ""))
            names = sorted({m.name for m in hit.values()})
            plan.notes.append(f"{path}: {len(hit)} changed member(s)" + (": " + ", ".join(names[:5]) + (" ..." if len(names) > 5 else "") if names else " (comments/blank only)")
                              + (f"; removed: {', '.join(sorted(removed))}" if removed else ""))
        elif path.endswith(".py"):
            py_changed.append(path); ref_files.append(path)
        elif _doc_only(path):
            plan.notes.append(f"{path}: docs only -> nothing to run")
        else:
            ref_files.append(path)

    # ---- references to changed non-C# files (data, scripts, python) and python referrers of changed C#
    pytest_dirs = _pytest_testpaths(read("pytest.ini") if "pytest.ini" in files else "")
    is_pytest = lambda p: p.endswith(".py") and os.path.basename(p).startswith("test_") and any(p.startswith(d) for d in pytest_dirs)
    non_cs_files = [p for p in files if not p.endswith(".cs") and not p.endswith(".md") and not _doc_only(p)]
    py_hits, seen = {}, set()
    queue = deque((p, p) for p in ref_files + [p for p in changes if is_cs_source(p) and changes[p].status != "D"])
    while queue:
        p, why = queue.popleft()
        if p in seen: continue
        seen.add(p)
        if is_pytest(p) and p in changes: py_hits.setdefault(p, f"{p} changed")
        base = os.path.basename(p)
        pref = base.split("-")[0] + "-" if "-" in base else None
        referrers = 0
        for q in non_cs_files:
            if q == p or q.startswith(".github/"): continue
            if _mentions(read(q), p):
                referrers += 1
                if is_pytest(q): py_hits.setdefault(q, f"{q} mentions {base}")
                elif q.endswith(".py") and p.endswith(".py"): queue.append((q, f"{why} -> {q}"))   # python imports chain; nothing else does
        if not p.endswith(".cs"):
            for m in members:
                if any(base in x or (pref and len(pref) >= 4 and pref in x) for x in m.strs):
                    referrers += 1; seed_member(m, f"{os.path.basename(m.file)}:{m.name} names {base}")
            if p in changes and referrers == 0 and not is_pytest(p) and changes[p].status != "D":
                if p.endswith(".md"): plan.notes.append(f"{p}: doc, not referenced -> nothing to run")
                else:
                    plan.force_full(f"{p}: changed file that no test, source or script references")
                    plan.notes.append(f"{p}: unreferenced -> full suite")
            elif p in changes and not plan.full:
                plan.notes.append(f"{p}: found via references ({referrers} referrer(s))")
    plan.py_tests = dict(py_hits)
    if any(p.startswith("vision/scene-mesher/") and p.endswith(".cs") for p in changes): plan.extra_cmds.append("scene-mesher")

    # ---- reverse closure: who uses an impacted member
    def inside(m, x): return m.cls == x.cls or m.cls.startswith(x.cls + ".")

    def consumers(x):
        if x.kind in ("type", "enum") or x.name == _last(x.cls): return by_any.get(x.name, ())   # type or ctor: any mention
        out = set()
        for uid in by_unq.get(x.name, ()):
            m = members[uid]
            if inside(m, x) or _last(x.cls) in m.using_static: out.add(uid)
        for uid in by_pair.get((_last(x.cls), x.name), ()):
            if not x.private or inside(members[uid], x): out.add(uid)
        if x.cls != "Program" or x.ext:   # `anything.Name`: the receiver's type is unknown unless a local declaration tells
            for uid, recv, na in by_dot.get(x.name, ()):
                m = members[uid]
                if x.private and not inside(m, x): continue
                if na is not None and x.arity is not None and not (x.arity[0] <= na and (x.arity[1] is None or na <= x.arity[1])): continue
                if recv and not x.ext and recv in m.decl and m.decl[recv] is not None and m.decl[recv] not in typenames: continue  # BCL / external type
                out.add(uid)
        return out

    impacted, expanded = dict(direct), set()
    queue = deque(seeds)
    flags_hit = {}
    while queue:
        x = queue.popleft()
        key = (x.cls, x.name, x.kind, x.arity)
        if key in expanded: continue
        expanded.add(key)
        for uid in consumers(x):
            m = members[uid]
            if uid not in impacted:
                impacted[uid] = f"{os.path.basename(m.file)}:{m.name} uses {x.name} <- {x.flags if isinstance(x.flags, str) else '?'}"
                m.flags = impacted[uid]; queue.append(m)
        for fl, (ids, hostcls) in flag_nodes.items():
            if fl not in flags_hit and x.name in ids and (x.cls == hostcls or x.cls.startswith(hostcls + ".")):
                flags_hit[fl] = f"selftest {fl} runs {x.name} <- {x.flags if isinstance(x.flags, str) else '?'}"
    # tests: through a flag only when the test method reaches the code solely via that flag
    flag_tests = defaultdict(set)
    for uid, why in impacted.items():
        m = members[uid]
        if m.is_test: plan.tests.setdefault(_test_key(m), why)
    for fl, why in flags_hit.items():
        for uid in by_any.get(fl, ()):
            m = members[uid]
            if not m.is_test: continue
            k = _test_key(m)
            if uid in impacted: continue  # already whole-method
            flag_tests[k].add(fl); plan.tests.setdefault(k, why)
    for k, fl in flag_tests.items():
        plan.flags[k] = fl
    # only tests that start the built galatay-text binary (SelftestRunner) need build-all.sh; the rest build via dotnet test
    plan.uses_exe = any("SelftestRunner" in m.ids and _test_key(m) in plan.tests for m in members if m.is_test)
    # `dotnet test` compiles the client + tests itself. build-all.sh (client AND MCP, fresh app-staging binary) runs only when
    # a selected test starts the built binary (SelftestRunner), when no test was selected for a changed source file (so it
    # is still compiled), or when the MCP-only file changed. Otherwise CI's build-all is the MCP compile check.
    cs_src_changed = any(is_cs_source(p) and not p.startswith(TEST_DIR) for p in changes)
    plan.uses_exe = any("SelftestRunner" in m.ids and _test_key(m) in plan.tests for m in members if m.is_test)
    plan.needs_build = cs_src_changed and (plan.uses_exe or not plan.tests or "textclient/galatay-mcp/McpProgram.cs" in changes)
    if plan.tests and len(plan.tests) > full_ratio * max(1, len(test_keys)):
        plan.force_full(f"selection is {len(plan.tests)} of {len(test_keys)} test methods (> {int(full_ratio * 100)}%): not worth the risk")
    return plan


def _pytest_testpaths(ini):
    m = re.search(r"testpaths\s*=\s*((?:\n[ \t]+\S+)+)", ini)
    return [x.strip().rstrip("/") + "/" for x in m.group(1).split()] if m else ["vision/tests/", "textclient/galatay-voice/tests/", "textclient/tests/"]


def csharp_filter(plan):
    """vstest --filter string for the selected methods (class-wide when every selected method of a class is listed is not
    needed: methods are cheap to list)"""
    parts = []
    for (cls, meth) in sorted(plan.tests):
        fl = sorted(plan.flags.get((cls, meth), ()))
        base = f"FullyQualifiedName~{cls}.{meth}"
        if fl: parts.append("(" + base + "&(" + "|".join("DisplayName~" + f for f in fl) + "))")
        else: parts.append(base)
    return "|".join(parts)


# ---------------------------------------------------------------- reporting / running
def report(plan, out=print):
    out("Changed files:")
    for n in plan.notes: out("  " + n)
    if plan.full:
        out("=> FULL SUITE: " + "; ".join(plan.full_reasons)); return
    if plan.needs_build:
        out("=> build-all.sh: builds the text client + MCP (fresh binary for selftests / compile check of a source no test covers)")
    if not (plan.tests or plan.py_tests or plan.extra_cmds):
        out("=> no test covers this change" + ("" if plan.needs_build else ": nothing to run")); return
    if plan.tests:
        out(f"=> C# xUnit: {len(plan.tests)} of {plan.total_tests} test methods")
        by = defaultdict(list)
        for (cls, meth) in sorted(plan.tests): by[cls].append(meth)
        for cls, ms in sorted(by.items()):
            ex = plan.tests[(cls, ms[0])]
            fl = sorted({f for m in ms for f in plan.flags.get((cls, m), ())})
            out(f"   {cls.split('.')[-1]}: {len(ms)} method(s)" + (f", selftest flags {', '.join(fl)}" if fl else "") + f"   [{ex}]")
    if plan.py_tests:
        out(f"=> pytest: {len(plan.py_tests)} file(s)")
        for p, why in sorted(plan.py_tests.items()): out(f"   {p}   [{why}]")
    for c in plan.extra_cmds: out(f"=> extra: {c} build + selftests")


def _env(root):
    env = dict(os.environ)
    dn = "/home/box/.dotnet"
    if os.path.isdir(dn):
        env["PATH"] = dn + os.pathsep + env.get("PATH", ""); env.setdefault("DOTNET_ROOT", dn)
    env.setdefault("DOTNET_CLI_TELEMETRY_OPTOUT", "1"); env.setdefault("DOTNET_NOLOGO", "1")
    exe = os.path.join(root, "textclient", "app-staging", "galatay-text")
    if os.path.exists(exe): env.setdefault("GALATAY_TEXT_EXE", exe)
    return env


def run_plan(plan, root, full=False):
    env = _env(root)
    rc = 0
    py = os.environ.get("IMPACT_PYTHON", sys.executable)   # interpreter that has pytest (pip install -r requirements-ci.txt)

    def sh(cmd, label):
        nonlocal rc
        print(f"$ {label}", flush=True)
        r = subprocess.run(cmd, cwd=root, env=env)
        if r.returncode: rc = rc or r.returncode; print(f"!! {label} exit {r.returncode}", flush=True)
        return r.returncode

    test_proj = "textclient/GalatayText.Tests/GalatayText.Tests.csproj"
    if full or plan.full:
        if sh(["bash", "textclient/build-all.sh"], "build-all.sh") == 0:
            env["GALATAY_TEXT_EXE"] = os.path.join(root, "textclient", "app-staging", "galatay-text")
            sh(["dotnet", "test", test_proj, "-c", "Release", "--nologo", "-v", "q", "-clp:ErrorsOnly"], "dotnet test (full)")
        sh([py, "-m", "pytest", "-q"], "pytest (full)")
        return rc
    if plan.needs_build or (plan.tests and plan.uses_exe and not os.path.exists(os.path.join(root, "textclient", "app-staging", "galatay-text"))):
        if sh(["bash", "textclient/build-all.sh"], "build-all.sh") != 0: return rc
        env["GALATAY_TEXT_EXE"] = os.path.join(root, "textclient", "app-staging", "galatay-text")
    if plan.tests:
        sh(["dotnet", "test", test_proj, "-c", "Release", "--nologo", "-v", "q", "-clp:ErrorsOnly", "--filter", csharp_filter(plan)], f"dotnet test ({len(plan.tests)} methods)")
    if plan.py_tests:
        sh([py, "-m", "pytest", "-q"] + sorted(plan.py_tests), f"pytest ({len(plan.py_tests)} files)")
    if "scene-mesher" in plan.extra_cmds:
        out = os.path.join(os.environ.get("TMPDIR", "/tmp"), "impact-mesher")
        if sh(["dotnet", "build", "vision/scene-mesher/SceneMesher.csproj", "-c", "Release", "-o", out,
               f"-p:LMV={root}/textclient/src-libremetaverse", "--nologo", "-v", "q"], "scene-mesher build") == 0:
            for f in ("--lod-selftest", "--skin-selftest", "--bodysize-selftest"): sh(["dotnet", os.path.join(out, "SceneMesher.dll"), f], f"scene-mesher {f}")
    return rc


def main(argv=None):
    ap = argparse.ArgumentParser(description="Run only the tests impacted by the current change (CI still runs everything).")
    ap.add_argument("--base", default="origin/main", help="diff base (default origin/main; includes uncommitted + untracked)")
    ap.add_argument("--commit", help="analyse one past commit/merge (SHA^..SHA); needs --dry-run")
    ap.add_argument("--range", help="analyse A..B; needs --dry-run")
    ap.add_argument("--full", action="store_true", help="run the full suite (what CI runs)")
    ap.add_argument("--dry-run", action="store_true", help="print the selection and why, run nothing")
    a = ap.parse_args(argv)
    root = _git(["rev-parse", "--show-toplevel"], os.getcwd()).strip()
    if (a.commit or a.range) and not a.dry_run: ap.error("--commit/--range analyse past diffs; add --dry-run")
    if a.full:
        plan = Plan(); plan.force_full("--full")
    else:
        changes, old, new = collect_changes(root, a.base, a.commit, a.range)
        if not changes: print("No changes against the base: nothing to run."); return 0
        plan = make_plan(new, old, changes)
    report(plan)
    if a.dry_run: return 0
    if not a.full and not (plan.full or plan.tests or plan.py_tests or plan.extra_cmds or plan.needs_build): return 0
    return run_plan(plan, root, a.full)


if __name__ == "__main__":
    sys.exit(main())
