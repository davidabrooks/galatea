// LeashRelease.cs (2026-10-26, David 10:17: "you still followed me for a long time until I typed this").
// Android form, Magic Leash -> Invisible: there is no particle chain to watch go away, and my own leash-walk kept the "collar is
// pulling me" clock fresh, so the old "the chain is gone" rule (3 scans AND 30 s with no pull) took ~30-50 s to notice the unleash.
// The collar's own RLV object is the authority: at leash time it sends @tplure:<holder>=n, @tplm=n, @tploc=n, @fly=n, @sittp:6=n ...
// and on unleash the same list with =y. A lift that removes the holder's @tplure exception, or the LAST leash restriction the
// collar was holding, starts a short confirm window (ConfirmSec); if the restrictions are still gone at its end the leash ends
// at once (walk stops, wander/auto-follow resume, Magic Leash RESET). A lift followed by a re-apply inside the window (collar menu
// re-send) leaves the leash alone.
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    // the restrictions a collar leash sets and lifts
    internal static bool LeashRlvKey(string behav) => behav is "tplm" or "tploc" or "tplure" or "fly" or "sittp" or "tplocal";
    internal static bool LeashRlvStoreKey(string key) => LeashRlvKey(key.Split(':')[0]);

    // pure (tested): decides a prompt unleash from the collar's RLV lifts
    internal sealed class LeashRelease
    {
        public const double ConfirmSec = 1.5;
        readonly object l = new();
        UUID src, holder; DateTime? since; bool holderTrigger;
        public bool Pending { get { lock (l) return since != null; } }
        // apply one y/n/add/rem leash restriction from the collar and watch for a release; returns "restricted"/"lifted"/null (not a restriction)
        public string Apply(RlvStore store, DateTime now, UUID source, string name, RlvCmd c, UUID leashHolder, bool leashed)
        {
            var p = c.Param;
            if (p is "n" or "add") { store.Add(source, name, c.Behav, c.Option); lock (l) if (since != null && source == src && LeashRlvKey(c.Behav)) since = null; return "restricted"; }
            if (p is "y" or "rem")
            {
                bool removed = store.Remove(source, c.Behav, c.Option);
                if (removed && leashed && LeashRlvKey(c.Behav))
                {
                    bool holderKey = c.Behav == "tplure" && c.Option.Length > 0 && UUID.TryParse(c.Option, out var k) && k == leashHolder;
                    bool lastLeft = !store.HasAnyFrom(source, LeashRlvStoreKey);
                    if (holderKey || lastLeft) lock (l) { since ??= now; src = source; holder = leashHolder; holderTrigger = holderKey || holderTrigger; }
                }
                return "lifted";
            }
            return null;
        }
        // true once, when the confirm window has run out and the collar still holds none of its leash restrictions
        public bool Confirm(RlvStore store, DateTime now, UUID currentHolder)
        {
            lock (l)
            {
                if (since is not DateTime s) return false;
                if (holder != currentHolder) { since = null; return false; }
                if ((now - s).TotalSeconds < ConfirmSec) return false;
                // the holder's own @tplure exception is leash-specific: its lift is enough even if the collar keeps other locks for itself
                bool gone = holderTrigger ? !store.HasFrom(src, "tplure:" + holder) : !store.HasAnyFrom(src, LeashRlvStoreKey);
                since = null; holderTrigger = false;
                return gone;
            }
        }
        public void Cancel() { lock (l) { since = null; holderTrigger = false; } }
    }

    // pure (tested): fallback when no RLV lift was seen: the chain is missing for a few scans and the collar's strict restrictions
    // (which this leash had) are gone from the store: the leash is over, whatever the pull clock says
    internal static bool LeashChainLossEndsFast(int missScans, bool hadStrict, bool strictNow) => missScans >= 3 && hadStrict && !strictNow;

    internal static readonly LeashRelease leashRelease = new();
    static bool leashHadStrict;                       // this leash has been strict at some point (the collar set its restrictions)
    static DateTime leashNoPullStartUntil = DateTime.MinValue;   // after a release: the leftover momentum must not look like a new pull
    internal static bool LeashPullStartAllowed(DateTime now, DateTime until) => now >= until;

    // RlvOne's y/n handling for a collar restriction; the confirm runs ConfirmSec later and ends the leash
    static string RlvApplyRestriction(UUID src, string srcName, RlvCmd c)
    {
        var l = leash;
        var r = leashRelease.Apply(rlv, DateTime.Now, src, srcName, c, l?.holder ?? UUID.Zero, l != null && l.mode == "leash");
        if (r == "lifted" && leashRelease.Pending)
        {
            RlvLog("leash: the collar lifted its leash restrictions (RLV release): confirming in 1.5 s");
            _ = Task.Run(async () => { await Task.Delay((int)(LeashRelease.ConfirmSec * 1000) + 100); try { LeashReleaseCheck(); } catch { } });
        }
        return r;
    }
    static void LeashReleaseCheck()
    {
        var l = leash; if (l == null || l.mode != "leash") { leashRelease.Cancel(); return; }
        if (!leashRelease.Confirm(rlv, DateTime.Now, l.holder)) return;
        leashNoPullStartUntil = DateTime.Now.AddSeconds(20);
        LeashEnd("the collar released the leash (RLV restrictions lifted)");
    }
}
