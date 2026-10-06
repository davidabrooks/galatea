// ImTarget.cs (2026-10-05, David): 'im [--re ids] <to> <text>' recipient parsing. The old split took the first TWO words as
// the name whenever there were 3+ words, so 'im --re 7 ThomasNejutto hi there' went to "ThomasNejutto hi" with text "there".
// Now: "quoted" or 'single-quoted' names, uuids and first.last are taken as is; otherwise a known contact decides
// (friends, names seen this session, nearby avatars, people I've IMed); 'X Resident' is always two words; if neither
// reading is known, the two-word name is tried in the directory first and the one-word username is the fallback.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal sealed record ImTargetSplit(string Target, string Text, bool Quoted, string AltTarget, string AltText);

    // pure (selftest): AltTarget/AltText = the one-word reading to try when the two-word name doesn't resolve
    internal static ImTargetSplit SplitImTarget(string rest, Func<string, bool> known)
    {
        rest = (rest ?? "").Trim();
        if (rest.Length > 0 && (rest[0] == '"' || rest[0] == '\''))
        {
            var q = rest[0]; var end = rest.IndexOf(q, 1);
            if (end > 0) return new(rest[1..end].Trim(), rest[(end + 1)..].Trim(), true, null, null);
        }
        var toks = rest.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (toks.Length == 0) return new("", "", false, null, null);
        string After(int n) { var t = rest; for (int i = 0; i < n; i++) { t = t.TrimStart(); t = t[(t.IndexOf(' ') is var k && k < 0 ? t.Length : k)..]; } return t.Trim(); }
        var one = new ImTargetSplit(toks[0], After(1), false, null, null);
        if (UUID.TryParse(toks[0], out _) || toks[0].Contains('.')) return one;
        if (toks.Length < 2) return one;
        var twoName = toks[0] + " " + toks[1];
        var two = new ImTargetSplit(twoName, After(2), false, null, null);
        if (toks[1].Equals("Resident", StringComparison.OrdinalIgnoreCase)) return two;
        if (toks.Length < 3) return one; // '<name> <one-word text>'
        bool k2 = known(twoName), k1 = known(toks[0]) || known(toks[0] + " Resident");
        if (k2) return two;          // a known full name wins (also when the first word alone is known too)
        if (k1) return one;
        return two with { AltTarget = one.Target, AltText = one.Text }; // unknown: try 'First Last', then the username
    }

    static bool ImNameKnown(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Trim(); var full = n.Contains(' ') ? n : n + " Resident";
        lock (nameToId) if (nameToId.ContainsKey(full)) return true;
        try { if (client.Friends.FriendList.Values.Any(f => string.Equals(f.Name, full, StringComparison.OrdinalIgnoreCase))) return true; } catch { }
        try { if (Sim.ObjectsAvatars.Values.Any(av => av != null && string.Equals(av.Name, full, StringComparison.OrdinalIgnoreCase))) return true; } catch { }
        return false;
    }

    internal static string ImTargetSelfTest()
    {
        var sb = new System.Text.StringBuilder(); int pass = 0, fail = 0;
        void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ThomasNejutto Resident", "David Nightingale", "Sophie Resident", "Thomas Nejutto" };
        bool K(string s) => known.Contains(s);
        bool KnownNoThomas2(string s) => s.Equals("ThomasNejutto Resident", StringComparison.OrdinalIgnoreCase);
        var a = SplitImTarget("ThomasNejutto hi there, how are you?", KnownNoThomas2);
        C(a.Target == "ThomasNejutto" && a.Text == "hi there, how are you?", $"known one-word username keeps the whole message ({a.Target} | {a.Text})");
        var b = SplitImTarget("David Nightingale on my way", K);
        C(b.Target == "David Nightingale" && b.Text == "on my way", "known 'First Last' contact");
        var c = SplitImTarget("\"Jane Doe\" hello you", _ => false);
        C(c.Target == "Jane Doe" && c.Text == "hello you" && c.Quoted, "double-quoted name");
        var d = SplitImTarget("'ThomasNejutto' Good morning", _ => false);
        C(d.Target == "ThomasNejutto" && d.Text == "Good morning" && d.Quoted, "single-quoted name");
        var e = SplitImTarget("aebe8a43-0170-487f-aad8-3df2dc0137b3 Hello there friend", _ => false);
        C(e.Target == "aebe8a43-0170-487f-aad8-3df2dc0137b3" && e.Text == "Hello there friend", "uuid recipient");
        var f = SplitImTarget("thomas.nejutto see you soon", _ => false);
        C(f.Target == "thomas.nejutto" && f.Text == "see you soon", "first.last username");
        var g = SplitImTarget("ThomasNejutto Resident hi there", _ => false);
        C(g.Target == "ThomasNejutto Resident" && g.Text == "hi there", "'X Resident' is always the full name");
        var h = SplitImTarget("Some Stranger hello there", _ => false);
        C(h.Target == "Some Stranger" && h.Text == "hello there" && h.AltTarget == "Some" && h.AltText == "Stranger hello there", "unknown: 'First Last' first, one-word fallback kept");
        var i = SplitImTarget("ThomasNejutto hi", _ => false);
        C(i.Target == "ThomasNejutto" && i.Text == "hi", "two words = name + one-word message");
        var j = SplitImTarget("Thomas Nejutto hi", s => s == "Thomas Nejutto" || s == "Thomas Resident");
        C(j.Target == "Thomas Nejutto" && j.Text == "hi", "both readings known with 3+ words: full name wins");
        var j2 = SplitImTarget("Thomas Nejutto how are you", s => s == "Thomas Nejutto" || s == "Thomas Resident");
        C(j2.Target == "Thomas Nejutto" && j2.Text == "how are you", "both readings known: the full contact name wins");
        var k = SplitImTarget("ThomasNejutto   spaced   text", KnownNoThomas2);
        C(k.Target == "ThomasNejutto" && k.Text == "spaced   text", "inner spacing of the message kept");
        var l = SplitImTarget("\"ThomasNejutto Resident\" ok", _ => false);
        C(l.Target == "ThomasNejutto Resident" && l.Text == "ok", "quoted full legacy name");
        return $"im target selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
    }
}
