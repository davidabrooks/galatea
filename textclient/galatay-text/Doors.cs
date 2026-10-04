// Doors.cs (2026-10-03, David): 'door [name filter | object uuid]' - find the nearest door-like prim (name or description says
// door / gate / entrance / entry; root objects AND child prims of linksets such as houses) within 10 m and touch it once,
// then report what was touched and whether it visibly moved/rotated ~2.5 s later. Also used by AutoFollow.cs when she is stuck
// (radius 4 m, at most once per 30 s). Touch only: never buys, sits, or answers a dialog.
using System.Globalization;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static readonly Regex DoorRx = new(@"\b(doors?|gates?|entrance|entry|doorway)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex DoorBad = new(@"door ?(mat|bell|stop|knocker|frame|sign|rug|hanger|wreath)|\b(frame|sign|vendor|rezz|mat)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    internal static bool LooksLikeDoor(string name, string desc) =>
        (DoorRx.IsMatch(name ?? "") && !DoorBad.IsMatch(name ?? "")) || (DoorRx.IsMatch(desc ?? "") && !DoorBad.IsMatch(desc ?? "") && !DoorBad.IsMatch(name ?? ""));

    static Vector3 WorldPos(Simulator sim, Primitive p, out Primitive root)
    {
        root = p;
        if (p.ParentID == 0) return p.Position;
        if (!sim.ObjectsPrimitives.TryGetValue(p.ParentID, out var parent) || parent == null) { root = null; return Vector3.Zero; } // on an avatar, or parent unknown
        root = parent;
        return parent.Position + p.Position * parent.Rotation;
    }

    // returns the report; quiet=true returns null when nothing door-like is near (stuck helper)
    static async Task<string> DoorTouch(string arg, float radius = 10f, bool quiet = false)
    {
        if (!LoggedIn) return quiet ? null : "not logged in";
        var sim = client.Network.CurrentSim; if (sim == null) return quiet ? null : "no region";
        var me = client.Self.SimPosition;
        arg = (arg ?? "").Trim().Trim('"');
        var cands = new List<(Primitive p, Primitive root, Vector3 pos, float d)>();
        bool byId = UUID.TryParse(arg, out var wantId);
        foreach (var p in sim.ObjectsPrimitives.Values.ToList())
        {
            if (p == null || p.PrimData.PCode != PCode.Prim) continue;
            if (byId && p.ID != wantId) continue;
            var pos = WorldPos(sim, p, out var root); if (root == null) continue;
            var d = Vector3.Distance(pos, me);
            if (d <= (byId ? 64f : radius)) cands.Add((p, root, pos, d));
        }
        if (byId && cands.Count == 0) return $"object {wantId} is not within 64 m (or is worn by an avatar)";
        cands = cands.OrderBy(c => c.d).Take(400).ToList();
        await EnsureProperties(sim, cands.Select(c => c.p).Concat(cands.Select(c => c.root)).Distinct().ToList());
        var navDoors = NavDoorIds();   // NavPlan.cs: door panels found on a nav grid (house links are often just 'Object')
        var doors = byId ? cands : cands.Where(c => (LooksLikeDoor(c.p.Properties?.Name, c.p.Properties?.Description) || navDoors.Contains(c.p.ID))
                                   && (arg.Length == 0 || (c.p.Properties?.Name ?? "").Contains(arg, StringComparison.OrdinalIgnoreCase)
                                       || (c.p.Properties?.Description ?? "").Contains(arg, StringComparison.OrdinalIgnoreCase)
                                       || (c.root.Properties?.Name ?? "").Contains(arg, StringComparison.OrdinalIgnoreCase))).ToList();
        if (doors.Count == 0)
            return quiet ? null : $"no door-like prim (name/description: door, gate, entrance, entry){(arg.Length > 0 ? $" matching '{arg}'" : "")} within {radius:F0} m ({cands.Count} prims checked)";
        var t = doors[0];
        var rot0 = t.p.Rotation; var pos0 = t.pos;
        client.Self.Touch(t.p.LocalID);
        string what = $"'{t.p.Properties?.Name ?? "?"}'{(string.IsNullOrEmpty(t.p.Properties?.Description) ? "" : $" desc '{t.p.Properties.Description}'")} {t.p.ID}" +
                      (t.root != t.p ? $" (link of '{t.root.Properties?.Name ?? "?"}' {t.root.ID})" : "") + string.Format(CultureInfo.InvariantCulture, " at {0:F1} m", t.d);
        Log("door", "touched " + what);
        await Task.Delay(2500);
        var pos1 = WorldPos(sim, t.p, out _); var rot1 = t.p.Rotation;
        double ang = Math.Acos(Math.Min(1.0, Math.Abs(Quaternion.Dot(rot0, rot1)))) * 2 * 180 / Math.PI;
        float mv = Vector3.Distance(pos0, pos1);
        var after = ang > 5 || mv > 0.2f ? string.Format(CultureInfo.InvariantCulture, "it moved {0:F1} m / turned {1:F0}° (likely opened or closed)", mv, ang) : "no visible movement yet (may be phantom-on-touch, locked, or opens a menu)";
        var others = doors.Count > 1 ? $"; other door-like prims: {string.Join(", ", doors.Skip(1).Take(4).Select(c => $"'{c.p.Properties?.Name}' {c.d:F1} m"))}" : "";
        return $"touched {what}; {after}{others}";
    }
}
