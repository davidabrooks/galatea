// SeatStandOut.cs (2026-10-08 18:38 PT): standing from the bathroom sink (Wash Hands) put her at 240.6,75.7, inside the
// vanity cabinet footprint (y 75.03..76.2, wall 76.16); the next two legs failed STUCK s=0 after 4 lane recoveries each
// until a same-region teleport to 240.6,73.8 freed her. After standing from a home seat with a stand_spot / change_spot
// (sink, toilet, tub) she now steps to that known clear floor point first. Standing inside a furniture footprint, or a
// step that makes no progress, uses a short local hop (same region, home parcel only) to that point instead of walking.
using System.Text.Json;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal sealed record FurnitureBox(string Name, string Id, float X0, float Y0, float X1, float Y1);
    internal const float StandOutNearTol = 0.5f;

    internal static List<FurnitureBox> ParseFurnitureBoxes(string graphJson)
    {
        var res = new List<FurnitureBox>();
        using var doc = JsonDocument.Parse(graphJson);
        if (!doc.RootElement.TryGetProperty("furniture_footprints", out var fa) || fa.ValueKind != JsonValueKind.Array) return res;
        foreach (var f in fa.EnumerateArray())
        {
            if (!f.TryGetProperty("min", out var mn) || !f.TryGetProperty("max", out var mx) || mn.GetArrayLength() < 2 || mx.GetArrayLength() < 2) continue;
            res.Add(new FurnitureBox(f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", f.TryGetProperty("uuid", out var u) ? u.GetString() ?? "" : "",
                (float)mn[0].GetDouble(), (float)mn[1].GetDouble(), (float)mx[0].GetDouble(), (float)mx[1].GetDouble()));
        }
        return res;
    }

    // pure: the furniture box she is standing in (null = clear floor)
    internal static FurnitureBox InFurniture(Vector3 p, IEnumerable<FurnitureBox> boxes) =>
        boxes?.FirstOrDefault(b => p.X >= b.X0 && p.X <= b.X1 && p.Y >= b.Y0 && p.Y <= b.Y1);

    internal enum StandOutMove { None, Walk, Hop }

    // pure: after standing from this seat: where to go first and how (inside a footprint -> hop, never 4 failing lane tries)
    internal static (StandOutMove how, Vector3 to) StandOutPlan(Vector3 me, HomeSeatInfo seat, IEnumerable<FurnitureBox> boxes)
    {
        var spot = seat?.StandSpot ?? seat?.ChangeSpot;
        if (spot == null) return (StandOutMove.None, me);
        if (HDist(me, spot.Value) <= StandOutNearTol) return (StandOutMove.None, spot.Value);
        return (InFurniture(me, boxes) != null ? StandOutMove.Hop : StandOutMove.Walk, spot.Value);
    }

    // pure: a walk that ends still away from the spot and moved less than 0.3 m made no progress -> hop
    internal static bool StandOutNoProgress(Vector3 start, Vector3 end, Vector3 spot) =>
        HDist(end, spot) > StandOutNearTol + 0.2f && HDist(start, end) < 0.3f;

    static async Task StandOutAfterSeat(HomeSeatInfo seat, CancellationToken ct)
    {
        if (seat == null || client.Self.SittingOn != 0 || !InPeronaut) return;
        List<FurnitureBox> boxes;
        try { var f = Path.Combine(RouteDir, "_graph-Peronaut.json"); boxes = File.Exists(f) ? ParseFurnitureBoxes(File.ReadAllText(f)) : new(); }
        catch (Exception ex) { WLog("stand out: footprints: " + ex.Message); boxes = new(); }
        var me = client.Self.SimPosition;
        var (how, to) = StandOutPlan(me, seat, boxes);
        if (how == StandOutMove.None) return;
        var inBox = InFurniture(me, boxes);
        if (how == StandOutMove.Walk)
        {
            WLog($"stand out of '{seat.Name}': at {P3(me)}, stepping to the clear spot {P3(to)} first");
            try { await WalkLeg(to, 0.4f, $"stand out of '{seat.Name}'", ct, navMode: true); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { WLog("stand out walk: " + ex.GetBaseException().Message); }
            var end = client.Self.SimPosition;
            if (!StandOutNoProgress(me, end, to)) return;
            WLog($"stand out of '{seat.Name}': no progress from {P3(me)} (now {P3(end)}) -> local hop");
        }
        else WLog($"stand out of '{seat.Name}': stood inside '{inBox?.Name}' at {P3(me)} -> local hop to {P3(to)}");
        await StandOutHop(to, ct);
    }

    static async Task StandOutHop(Vector3 to, CancellationToken ct)
    {
        var sim = client.Network.CurrentSim; if (sim == null) return;
        try
        {
            var allowed = await AllowedParcel(sim);
            var pid = await ParcelAt(sim, to);
            if (!ParcelOk(pid, allowed)) { WLog($"stand out hop: {P3(to)} is not on the home parcel; not hopping"); return; }
            bool ok = await client.Self.TeleportAsync(sim.Name, new Vector3(to.X, to.Y, to.Z + 0.2f));
            WLog($"stand out hop to {P3(to)}: {(ok ? "ok" : "teleport failed")}; now at {P3(client.Self.SimPosition)}");
        }
        catch (Exception ex) { WLog("stand out hop: " + ex.GetBaseException().Message); }
    }
}
