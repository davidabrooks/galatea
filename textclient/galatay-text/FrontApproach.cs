// FrontApproach.cs (2026-10-05): know which way other avatars face (Avatar.Rotation from ObjectUpdate/terse),
// show heading in avatars/nearby, and 'front <avatar> [m]' — walk a tight arc (~1 m radius, never through them)
// to ~1.5 m in front, then face them.
using System.Globalization;
using System.Text;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    // Yaw (radians) from an avatar/body quaternion (LibreMetaverse Avatar.Rotation / Primitive.Rotation).
    internal static float AvatarYawRad(Quaternion q) =>
        MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.Y * q.Y + q.Z * q.Z));

    internal static float AvatarYawDeg(Quaternion q)
    {
        var d = AvatarYawRad(q) * 180f / MathF.PI;
        d %= 360f; if (d < 0) d += 360f;
        return d;
    }

    // Compass-ish label for listings.
    internal static string HeadingLabel(float yawDeg)
    {
        string[] names = { "E", "NE", "N", "NW", "W", "SW", "S", "SE" };
        int i = (int)MathF.Round(yawDeg / 45f) & 7;
        return names[i];
    }

    internal static string FmtAvatarHeading(Quaternion rot) =>
        string.Format(CultureInfo.InvariantCulture, "facing {0:F0}° {1}", AvatarYawDeg(rot), HeadingLabel(AvatarYawDeg(rot)));

    // Shortest signed angle delta in (-pi, pi].
    internal static float AngleDelta(float from, float to)
    {
        var d = to - from;
        while (d > MathF.PI) d -= 2 * MathF.PI;
        while (d <= -MathF.PI) d += 2 * MathF.PI;
        return d;
    }

    /// <summary>
    /// Arc waypoints around <paramref name="leader"/> at <paramref name="orbitR"/> (~1 m), ending at
    /// <paramref name="frontDist"/> (~1.5 m) along their facing. Never aims through the leader (orbit clears them).
    /// </summary>
    internal static List<Vector2> FrontArcWaypoints(Vector2 me, Vector2 leader, float facingYawRad,
        float frontDist = 1.5f, float orbitR = 1.0f)
    {
        float a0 = MathF.Atan2(me.Y - leader.Y, me.X - leader.X);
        float a1 = facingYawRad;
        float da = AngleDelta(a0, a1);
        // ~30° steps; at least one intermediate if we must swing more than ~20°
        int n = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(da) / (MathF.PI / 6f)));
        if (MathF.Abs(da) < 20f * MathF.PI / 180f) n = 1; // already nearly in front: go straight to front point
        var pts = new List<Vector2>();
        for (int i = 1; i < n; i++)
        {
            float a = a0 + da * (i / (float)n);
            pts.Add(leader + new Vector2(MathF.Cos(a), MathF.Sin(a)) * orbitR);
        }
        pts.Add(leader + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * frontDist);
        return pts;
    }

    // Min horizontal distance from leader to any segment me→…→front (pure check for tests).
    internal static float FrontArcMinClearance(Vector2 me, Vector2 leader, IReadOnlyList<Vector2> pts)
    {
        float MinSeg(Vector2 a, Vector2 b)
        {
            var ab = b - a; float L2 = ab.X * ab.X + ab.Y * ab.Y;
            if (L2 < 1e-8f) return Vector2.Distance(leader, a);
            float t = Math.Clamp(((leader.X - a.X) * ab.X + (leader.Y - a.Y) * ab.Y) / L2, 0, 1);
            return Vector2.Distance(leader, a + ab * t);
        }
        float m = MinSeg(me, pts[0]);
        for (int i = 0; i < pts.Count - 1; i++) m = MathF.Min(m, MinSeg(pts[i], pts[i + 1]));
        return m;
    }

    static async Task<string> FrontCmd(string rest)
    {
        if (WanderBlocksManualWalk) return "wander is running: 'wander pause' (or 'wander stop') first";
        var a = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length == 0) return "usage: front <avatar> [metres in front, default 1.5]";
        float frontDist = 1.5f;
        string name = rest;
        if (a.Length >= 2 && F(a[^1], out var m) && m > 0.5f && m <= 4f)
        { frontDist = m; name = string.Join(' ', a[..^1]); }
        var av = Avatars().FirstOrDefault(t => t.av.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || t.av.Name.Equals(name + " Resident", StringComparison.OrdinalIgnoreCase)
            || t.av.Name.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase));
        if (av.av == null) return $"'{name}' is not in view";
        if (av.dist < 0) return $"{av.av.Name} is seated on an object not loaded yet (no position)";
        if (InPeronaut && UnderHouse(av.pos)) return $"refused: {av.av.Name} is at {V(av.pos)}, below the house floor";
        // home (21:18): far away or on another level -> walk over the path graph first, then the short front arc
        bool homeGraphFirst = InPeronaut && (Math.Abs(av.pos.Z - client.Self.SimPosition.Z) > 1.5f || HDist(av.pos, client.Self.SimPosition) > 6f);
        if (homeGraphFirst)
        {
            var avPos = av.pos; var avName = av.av.Name; var avRot = av.av.Rotation;
            return StartWalk($"front {avName} (path graph first)", async ct =>
            {
                await EnsureStandingForWalk(ct);
                var hr = await HomeGraphWalkTo(avPos, 2.5f, $"front {avName}", ct);
                if (hr != null) return $"could not reach {avName} at home: {hr}";
                var me2 = V2(client.Self.SimPosition); var z2 = client.Self.SimPosition.Z;
                var arc = FrontArcWaypoints(me2, new Vector2(avPos.X, avPos.Y), AvatarYawRad(avRot), frontDist, orbitR: 1.0f).Select(p => new Vector3(p.X, p.Y, z2)).ToList();
                if (!await WalkPath(arc, ct, lastTol: 0.6f)) return $"stopped at {V(client.Self.SimPosition)} before reaching the front of {avName}";
                try { client.Self.Movement.TurnToward(new Vector3(avPos.X, avPos.Y, client.Self.SimPosition.Z)); } catch { }
                return $"in front of {avName} at {V(client.Self.SimPosition)} (path graph, then the front arc)";
            });
        }
        float yaw = AvatarYawRad(av.av.Rotation);
        var me = V2(client.Self.SimPosition);
        var leader = new Vector2(av.pos.X, av.pos.Y);
        var pts2 = FrontArcWaypoints(me, leader, yaw, frontDist, orbitR: 1.0f);
        var z = client.Self.SimPosition.Z;
        var pts = pts2.Select(p => new Vector3(p.X, p.Y, z)).ToList();
        Log("front", $"{av.av.Name} {FmtAvatarHeading(av.av.Rotation)}: arc {pts2.Count} pts, front {frontDist:F1} m, clearance {FrontArcMinClearance(me, leader, pts2):F2} m");
        return StartWalk($"front {av.av.Name}", async ct =>
        {
            await EnsureStandingForWalk(ct);
            if (!await WalkPath(pts, ct, lastTol: 0.6f))
                return $"stopped at {V(client.Self.SimPosition)} before reaching the front of {av.av.Name}";
            try { client.Self.Movement.TurnToward(new Vector3(av.pos.X, av.pos.Y, client.Self.SimPosition.Z)); } catch { }
            return $"in front of {av.av.Name} at {V(client.Self.SimPosition)} ({FmtAvatarHeading(av.av.Rotation)})";
        });
    }

    internal static string FrontSelfTest()
    {
        var sb = new StringBuilder(); int pass = 0, fail = 0;
        void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        // Facing +X (east, yaw 0): me behind at (-2,0), arc should end at (+1.5, 0)
        var me = new Vector2(-2f, 0f); var L = new Vector2(0, 0);
        var pts = FrontArcWaypoints(me, L, facingYawRad: 0f, frontDist: 1.5f, orbitR: 1.0f);
        C(pts.Count >= 1, $"arc has waypoints ({pts.Count})");
        var end = pts[^1];
        C(MathF.Abs(end.X - 1.5f) < 0.05f && MathF.Abs(end.Y) < 0.05f, $"ends 1.5 m in front ({end.X:F2},{end.Y:F2})");
        C(FrontArcMinClearance(me, L, pts) >= 0.85f, $"clearance >= 0.85 m (got {FrontArcMinClearance(me, L, pts):F2}) — tight orbit, not through him");
        // Intermediate points near 1 m radius
        bool orbitOk = pts.Count == 1 || pts.Take(pts.Count - 1).All(p => MathF.Abs(Vector2.Distance(p, L) - 1.0f) < 0.08f);
        C(orbitOk, "intermediate waypoints stay on ~1 m orbit");
        // Shorter way: me at north, facing east → arc clockwise or CCW with |da|<=pi
        var pts2 = FrontArcWaypoints(new Vector2(0, 2), L, 0f, 1.5f, 1.0f);
        C(FrontArcMinClearance(new Vector2(0, 2), L, pts2) >= 0.85f, "from the side: still clears the avatar");
        C(HeadingLabel(0) == "E" && HeadingLabel(90) == "N" && HeadingLabel(180) == "W", "heading labels E/N/W");
        C(DoorThroughDelayMs == 300, "door through delay is 300 ms");
        return $"front selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
    }
}
