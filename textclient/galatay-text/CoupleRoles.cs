using LibreMetaverse;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GalatayText;

// 2026-10-13 David (Reiley Net Chair, 19:59): he sat first and got the AVsitter role meant for the woman; sit_lap put me in
// the other role, so he was on MY lap until I pressed [SWAP]. Rule: before any couples pose (sit_lap, pose couples, and
// when we end up on the same furniture) check we each have the right role (me = female / on-lap / receiving, David =
// male) and swap first if reversed; verify after the swap.
// How roles are decided, strongest first:
//  1. the seat menu text names my role explicitly (AVsitter "Sitter 1 (Female)", "Her", "Male", "Him", "M"/"F" ...)
//  2. our seated heights (pelvis = agent positions, world z): on a lap / on top I'm clearly above him
//     (sit_lap: >= 0.10 m; any other couples furniture: >= 0.25 m so a taller standing partner never counts)
//  3. otherwise unknown: no swap, logged.
// AVsitter's bare "Sitter 0/1" number is NOT used alone: which number is the woman is up to each creator (on this
// chair Sitter 0 was hers).
public static partial class Program
{
    internal enum CoupleRole { Unknown, Female, Male }

    static readonly Regex RoleFemaleRx = new(@"(?<![a-z])(female|woman|women|her|hers|she|girl|lady|f|♀)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex RoleMaleRx = new(@"(?<![a-z])(male|man|men|him|his|he|boy|guy|m|♂)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // pure: my role from the seat menu's text (the line naming my sitter slot), Unknown when it doesn't say
    internal static (CoupleRole role, string why) RoleFromMenuText(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return (CoupleRole.Unknown, "menu text empty");
        foreach (var raw in message.Split('\n'))
        {
            var line = raw.Trim();
            var m = Regex.Match(line, @"(?i)\b(sitter|role|position|seat)\s*#?\s*(\d+)\b(.*)$");
            if (!m.Success) continue;
            var rest = m.Groups[3].Value;
            bool f = RoleFemaleRx.IsMatch(rest), ml = RoleMaleRx.IsMatch(rest);
            if (f && !ml) return (CoupleRole.Female, $"menu '{line}' names a female role");
            if (ml && !f) return (CoupleRole.Male, $"menu '{line}' names a male role");
            return (CoupleRole.Unknown, $"menu '{line}' gives only a sitter number (not used alone)");
        }
        return (CoupleRole.Unknown, "no sitter line in the menu text");
    }

    // pure: verdict "ok" | "reversed" | "unknown" from the menu role and our seated heights (world z, metres)
    internal static (string verdict, string why) CoupleRoleVerdict(CoupleRole menuRole, string menuWhy, float? myZ, float? hisZ, bool lapContext)
    {
        if (menuRole == CoupleRole.Female) return ("ok", menuWhy);
        if (menuRole == CoupleRole.Male) return ("reversed", menuWhy);
        if (myZ == null || hisZ == null) return ("unknown", menuWhy + "; positions unknown");
        float d = myZ.Value - hisZ.Value, thr = lapContext ? 0.10f : 0.25f;
        var pos = $"me {d:+0.00;-0.00} m vs David (threshold {thr:0.00})";
        if (d >= thr) return ("ok", menuWhy + "; " + pos + ": I'm above him");
        if (d <= -thr) return ("reversed", menuWhy + "; " + pos + ": he's above me");
        return ("unknown", menuWhy + "; " + pos + ": too close to tell");
    }

    // pure: the button that swaps sitter roles: AVsitter [SWAP] / Swap / ⇄; else a role-select button for the woman; -1 none
    internal static (int index, string kind) RoleSwapButton(IReadOnlyList<string> labels)
    {
        if (labels == null) return (-1, null);
        for (int i = 0; i < labels.Count; i++)
        {
            var b = (labels[i] ?? "").Trim().Trim('[', ']', '*', ' ').Trim();
            if (Regex.IsMatch(b, @"(?i)^(swap|swap\s*(sides|roles|places)?|switch\s*(sides|roles|places)|⇄|⇆)$")) return (i, "swap");
        }
        for (int i = 0; i < labels.Count; i++)
        {
            var b = (labels[i] ?? "").Trim().Trim('[', ']', ' ').Trim();
            if (Regex.IsMatch(b, @"(?i)^(f|female|her|she|woman|girl|♀)$")) return (i, "role-select");
        }
        return (-1, null);
    }

    static float? AvatarWorldZ(UUID id)
    {
        var sim = Sim; if (sim == null) return null;
        var av = sim.ObjectsAvatars.Values.FirstOrDefault(a => a != null && a.ID == id);
        if (av == null || (av.ParentID != 0 && !sim.ObjectsPrimitives.ContainsKey(av.ParentID))) return null;
        return PositionHelper.GetAvatarPosition(sim, av).Z;
    }

    // check (and fix) our roles on a seat we share; returns a one-line summary, also logged as [roles]
    static async Task<string> EnsureCoupleRoles(Primitive seat, string seatName, string ctx, bool lapContext, CancellationToken ct)
    {
        try
        {
            await Task.Delay(1500, ct);   // the seat's anims and sit offsets settle
            var t0 = DateTime.Now; client.Self.Touch(seat.LocalID);
            var d = await WaitSeatDialog(seat, t0, 5000, ct);
            var (role, mwhy) = RoleFromMenuText(d?.Message);
            var (v, why) = CoupleRoleVerdict(role, mwhy, AvatarWorldZ(client.Self.AgentID), AvatarWorldZ(DavidId), lapContext);
            string Note(string s) { Log("roles", $"{ctx} '{seatName}': {s}"); return s; }
            if (v != "reversed") return Note($"roles {v}: {why}");
            var (bi, kind) = RoleSwapButton(d?.ButtonLabels);
            if (d == null || bi < 0) return Note($"roles reversed ({why}) but no swap/role button in [{string.Join(" | ", d?.ButtonLabels ?? new List<string>())}]");
            var label = d.ButtonLabels[bi];
            Log("roles", $"{ctx} '{seatName}': reversed ({why}) -> pressing '{label}' ({kind})");
            client.Self.ReplyToScriptDialog(d.Channel, bi, label, d.ObjectID);
            await Task.Delay(2500, ct);
            var t1 = DateTime.Now; client.Self.Touch(seat.LocalID);
            var d2 = await WaitSeatDialog(seat, t1, 4000, ct);
            var (role2, mwhy2) = RoleFromMenuText(d2?.Message);
            var (v2, why2) = CoupleRoleVerdict(role2, mwhy2, AvatarWorldZ(client.Self.AgentID), AvatarWorldZ(DavidId), lapContext);
            return Note($"swapped with '{label}'; after: roles {v2} ({why2})");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log("roles", $"{ctx} '{seatName}': check failed: {ex.GetBaseException().Message}"); return "role check failed: " + ex.GetBaseException().Message; }
    }
}
