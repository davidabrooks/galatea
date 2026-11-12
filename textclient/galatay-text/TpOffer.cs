// TpOffer.cs (2026-11-11, David): send a teleport offer (lure) so the other avatar can teleport to me.
// Client: tp_offer / offer_teleport [--confirm] <name|username|uuid|"First Last"> [message]
// MCP: tp_offer. Default allow-list is David Nightingale only (uuid or name). Anyone else needs confirm=true
// (David approved that exact offer in chat). Never a teleport request (asking to be teleported to them). Default
// message "Join me here!". Duplicate guard: skip if the same target was offered in the last 10 s.
using System.Globalization;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal const string TpOfferDefaultMessage = "Join me here!";
    internal static readonly TimeSpan TpOfferWindow = TimeSpan.FromSeconds(10);
    internal static readonly UUID TpOfferDavidId = new("44ce5a36-c1c7-4a68-ac9a-635ddfff6233");
    const string TpOfferUsage = "usage: tp_offer [--confirm] <name|username|uuid|\"First Last\"> [message]   (alias: offer_teleport). Default allow-list is David Nightingale only; anyone else needs --confirm (David's OK for that exact offer). Default message: 'Join me here!'. This sends an offer, never a teleport request.";

    static readonly Dictionary<string, DateTimeOffset> tpOfferLast = new(StringComparer.OrdinalIgnoreCase);

    internal sealed record TpOfferParse(bool Confirm, string Target, string Message, bool Quoted, string AltTarget, string AltMessage);

    // names / usernames / uuid that mean David Nightingale (not "me", which is only a follow shorthand)
    internal static bool TpOfferTargetIsDavid(UUID id, string name)
    {
        if (id != UUID.Zero && id == TpOfferDavidId) return true;
        var n = (name ?? "").Trim().Trim('"');
        if (n.Length == 0) return false;
        if (UUID.TryParse(n, out var u) && u == TpOfferDavidId) return true;
        var norm = n.Contains('.') && !n.Contains(' ') ? n.Replace('.', ' ') : n;
        if (norm.EndsWith(" Resident", StringComparison.OrdinalIgnoreCase))
            norm = norm[..^" Resident".Length].TrimEnd();
        return norm.Equals("david", StringComparison.OrdinalIgnoreCase)
            || norm.Equals("david nightingale", StringComparison.OrdinalIgnoreCase);
    }

    // null = allowed. After resolution the uuid is authoritative; the input name also counts so
    // "David Nightingale" is allowed before a directory lookup, and Sophie is never implicit.
    // once a uuid is known it is authoritative: a name that looks like David but resolves to someone else is refused
    internal static string TpOfferRefusal(UUID id, string name, bool confirm)
    {
        bool allowed = id != UUID.Zero ? id == TpOfferDavidId : TpOfferTargetIsDavid(UUID.Zero, name);
        if (allowed || confirm) return null;
        var who = string.IsNullOrWhiteSpace(name) ? (id == UUID.Zero ? "(unknown)" : id.ToString()) : name.Trim();
        return $"refused: teleport offers to anyone other than David Nightingale need confirm=true (David's OK for this exact offer); '{who}' is not on the allow-list; nothing sent.";
    }

    internal static string TpOfferDuplicateSkip(string name, DateTimeOffset now, DateTimeOffset? last, TimeSpan window)
    {
        if (last == null || now - last.Value >= window) return null;
        var ago = (now - last.Value).TotalSeconds;
        var at = last.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " PT";
        return $"skipped: already sent {name} a teleport offer at {at} ({ago:F1} s ago, {window.TotalSeconds:0} s duplicate window); not sent again.";
    }

    internal static TpOfferParse ParseTpOfferArgs(string rest, Func<string, bool> known)
    {
        rest = (rest ?? "").Trim();
        bool confirm = false;
        while (rest.StartsWith("--confirm", StringComparison.OrdinalIgnoreCase)
               && (rest.Length == 9 || char.IsWhiteSpace(rest[9])))
        {
            confirm = true;
            rest = rest.Length == 9 ? "" : rest[9..].TrimStart();
        }
        var raw = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (raw.Length > 0 && raw[^1].Equals("confirm", StringComparison.OrdinalIgnoreCase))
        {
            confirm = true;
            rest = string.Join(' ', raw[..^1]);
        }

        string Msg(string t) => string.IsNullOrWhiteSpace(t) ? TpOfferDefaultMessage : t.Trim();

        if (rest.Length == 0) return new(confirm, "", TpOfferDefaultMessage, false, null, null);

        if (rest[0] == '"' || rest[0] == '\'')
        {
            var q = rest[0]; var end = rest.IndexOf(q, 1);
            if (end > 0) return new(confirm, rest[1..end].Trim(), Msg(rest[(end + 1)..]), true, null, null);
        }

        var toks = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string After(int n)
        {
            var t = rest;
            for (int i = 0; i < n; i++)
            {
                t = t.TrimStart();
                var k = t.IndexOf(' ');
                t = k < 0 ? "" : t[(k + 1)..];
            }
            return t.Trim();
        }

        bool Known(string s) => (known != null && known(s)) || TpOfferTargetIsDavid(UUID.Zero, s);

        if (UUID.TryParse(toks[0], out _) || toks[0].Contains('.'))
            return new(confirm, toks[0], Msg(After(1)), false, null, null);
        if (toks.Length == 1)
            return new(confirm, toks[0], TpOfferDefaultMessage, false, null, null);
        if (toks[1].Equals("Resident", StringComparison.OrdinalIgnoreCase))
            return new(confirm, toks[0] + " " + toks[1], Msg(After(2)), false, null, null);

        var twoName = toks[0] + " " + toks[1];
        if (toks.Length == 2)
        {
            // two tokens, no further words: a known one-word username (or "Name Resident") + a one-word message, otherwise First Last
            if (!Known(twoName) && (Known(toks[0]) || Known(toks[0] + " Resident")))
                return new(confirm, toks[0], toks[1], false, null, null);
            return new(confirm, twoName, TpOfferDefaultMessage, false, null, null);
        }

        // 3+ tokens: same rule as im (known full name, else known username, else First Last with username fallback)
        if (Known(twoName)) return new(confirm, twoName, Msg(After(2)), false, null, null);
        if (Known(toks[0]) || Known(toks[0] + " Resident"))
            return new(confirm, toks[0], Msg(After(1)), false, null, null);
        return new(confirm, twoName, Msg(After(2)), false, toks[0], Msg(After(1)));
    }

    static async Task<string> TpOfferCmd(string rest)
    {
        var p = ParseTpOfferArgs(rest, n => ImNameKnown(n) || TpOfferTargetIsDavid(UUID.Zero, n));
        if (p.Target.Length == 0) return TpOfferUsage;

        string Check(string target, bool quoted)
        {
            if (TpOfferTargetIsDavid(UUID.Zero, target)) return null;
            return ImTargetCheck(target, quoted, ImNameKnown(target));
        }

        var bad = Check(p.Target, p.Quoted);
        if (bad != null) { Log("tp-offer", bad); return bad; }

        var id = await ResolveAvatar(p.Target);
        var target = p.Target; var message = p.Message;
        if (id == UUID.Zero && p.AltTarget != null)
        {
            var bad1 = Check(p.AltTarget, false);
            var id1 = bad1 == null ? await ResolveAvatar(p.AltTarget) : UUID.Zero;
            if (id1 != UUID.Zero)
            {
                Log("tp-offer", $"'{target}' not found; offering to username '{p.AltTarget}'");
                id = id1; target = p.AltTarget; message = p.AltMessage;
            }
        }
        if (id == UUID.Zero)
            return $"could not resolve avatar '{target}'{(p.AltTarget != null ? $" (nor '{p.AltTarget}')" : "")}; nothing sent. Quote the name (\"First Last\") or use the uuid.";

        if (client.Self.AgentID != UUID.Zero && id == client.Self.AgentID)
        {
            var self = $"refused: will not send a teleport offer to myself; nothing sent.";
            Log("tp-offer", self); return self;
        }

        var display = NameOf(id); if (display == id.ToString()) display = target;
        // once resolved, the uuid is authoritative (a name that looks like David but resolves to someone else is refused)
        var refusal = TpOfferRefusal(id, display, p.Confirm);
        if (refusal != null) { Log("tp-offer", refusal); return refusal; }

        var key = id.ToString();
        lock (tpOfferLast)
        {
            DateTimeOffset? last = tpOfferLast.TryGetValue(key, out var prev) ? prev : null;
            var skip = TpOfferDuplicateSkip(display, DateTimeOffset.Now, last, TpOfferWindow);
            if (skip != null) { Log("tp-offer", skip); return skip; }
            try { client.Self.SendTeleportLure(id, message); }
            catch (Exception ex)
            {
                var err = $"error: SendTeleportLure failed: {ex.GetType().Name}: {ex.Message}";
                Log("tp-offer", err); return err;
            }
            tpOfferLast[key] = DateTimeOffset.Now;
        }
        var ok = $"sent teleport offer to {display} ({id}): '{message}'";
        Log("tp-offer", ok);
        return ok;
    }

    internal static string TpOfferSelfTest()
    {
        var sb = new System.Text.StringBuilder(); int pass = 0, fail = 0;
        void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }

        var david = TpOfferDavidId;
        var sophie = new UUID("d695b17a-6504-4697-a945-0b71c53e4771");
        var stranger = new UUID("aebe8a43-0170-487f-aad8-3df2dc0137b3");

        C(TpOfferTargetIsDavid(david, null), "David uuid is allow-listed");
        C(TpOfferTargetIsDavid(UUID.Zero, "David Nightingale"), "legacy name is allow-listed");
        C(TpOfferTargetIsDavid(UUID.Zero, "david.nightingale"), "username is allow-listed");
        C(TpOfferTargetIsDavid(UUID.Zero, "David"), "short name 'David' is allow-listed");
        C(TpOfferTargetIsDavid(UUID.Zero, "44ce5a36-c1c7-4a68-ac9a-635ddfff6233"), "uuid string is allow-listed");
        C(!TpOfferTargetIsDavid(UUID.Zero, "me"), "'me' is not an offer target");
        C(!TpOfferTargetIsDavid(sophie, "SophieJeanneLaDouce Resident"), "Sophie is not on the outgoing allow-list");
        C(!TpOfferTargetIsDavid(stranger, "Some Stranger"), "a stranger is not allow-listed");

        C(TpOfferRefusal(david, "David Nightingale", false) == null, "David needs no confirm");
        C(TpOfferRefusal(UUID.Zero, "david.nightingale", false) == null, "David by username needs no confirm");
        var rSophie = TpOfferRefusal(sophie, "SophieJeanneLaDouce Resident", false);
        C(rSophie != null && rSophie.StartsWith("refused:") && rSophie.Contains("confirm=true"), "Sophie without confirm is refused");
        C(TpOfferRefusal(sophie, "SophieJeanneLaDouce Resident", true) == null, "Sophie with confirm=true is allowed");
        C(TpOfferRefusal(stranger, "Some Stranger", false) != null, "stranger without confirm is refused");
        C(TpOfferRefusal(stranger, "Some Stranger", true) == null, "stranger with confirm=true is allowed");
        C(TpOfferRefusal(stranger, "David Nightingale", false) != null, "a non-David uuid is refused even if the typed name is David Nightingale");
        C(TpOfferRefusal(david, "Some Stranger", false) == null, "David's uuid is allowed even if the directory name is unexpected");


        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ThomasNejutto Resident", "David Nightingale", "Thomas Nejutto" };
        bool K(string s) => known.Contains(s);

        var a = ParseTpOfferArgs("David Nightingale", K);
        C(a.Target == "David Nightingale" && a.Message == TpOfferDefaultMessage && !a.Confirm, $"David with default message ({a.Target} | {a.Message})");
        var b = ParseTpOfferArgs("David Nightingale Come join me", K);
        C(b.Target == "David Nightingale" && b.Message == "Come join me" && !b.Confirm, "David with a custom message");
        var c = ParseTpOfferArgs("--confirm Some Stranger", _ => false);
        C(c.Target == "Some Stranger" && c.Confirm && c.Message == TpOfferDefaultMessage, "--confirm + First Last, default message");
        var d = ParseTpOfferArgs("\"Jane Doe\" Meet me here", _ => false);
        C(d.Target == "Jane Doe" && d.Message == "Meet me here" && d.Quoted, "quoted name + message");
        var e = ParseTpOfferArgs("44ce5a36-c1c7-4a68-ac9a-635ddfff6233", _ => false);
        C(e.Target == "44ce5a36-c1c7-4a68-ac9a-635ddfff6233" && e.Message == TpOfferDefaultMessage, "uuid, default message");
        var f = ParseTpOfferArgs("david.nightingale Join me here!", _ => false);
        C(f.Target == "david.nightingale" && f.Message == "Join me here!", "username + message");
        var g = ParseTpOfferArgs("ThomasNejutto hi", s => s.Equals("ThomasNejutto Resident", StringComparison.OrdinalIgnoreCase));
        C(g.Target == "ThomasNejutto" && g.Message == "hi", "known one-word username + one-word message");
        var h = ParseTpOfferArgs("Some Stranger hello there", _ => false);
        C(h.Target == "Some Stranger" && h.Message == "hello there" && h.AltTarget == "Some" && h.AltMessage == "Stranger hello there", "unknown: First Last first, username fallback kept");
        var i = ParseTpOfferArgs("Some Stranger confirm", _ => false);
        C(i.Target == "Some Stranger" && i.Confirm && i.Message == TpOfferDefaultMessage, "trailing confirm on a two-word name");
        var j = ParseTpOfferArgs("", _ => false);
        C(j.Target == "" && !j.Confirm, "empty rest has no target");

        var t0 = DateTimeOffset.Parse("2026-11-11T15:40:00-08:00");
        C(TpOfferDuplicateSkip("David Nightingale", t0, null, TpOfferWindow) == null, "first offer is not skipped");
        var skip = TpOfferDuplicateSkip("David Nightingale", t0.AddSeconds(3), t0, TpOfferWindow);
        C(skip != null && skip.StartsWith("skipped:") && skip.Contains("10 s"), "same target within 10 s is skipped");
        C(TpOfferDuplicateSkip("David Nightingale", t0.AddSeconds(10), t0, TpOfferWindow) == null, "same target at 10 s is allowed");
        C(TpOfferDuplicateSkip("David Nightingale", t0.AddSeconds(11), t0, TpOfferWindow) == null, "same target after 10 s is allowed");

        return $"tp offer selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
    }
}
