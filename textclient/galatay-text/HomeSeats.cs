// Peronaut home seats beyond the upper floor (David's tour 2026-10-05): catalogued seats in routes/_seats-peronaut-home.json
// are reached over the path graph (porch, beach, GOOSE 3-step stair + Mooring deck, Burgundy pier). Per-seat rules:
//  - group: several chairs at one spot -> pick one at random
//  - special "shower": Bikini on, menu Single* > random F1/F2, touch the valve child prim to start the water, touch it again before standing
//  - special "undress": undress before sitting (clawfoot tub), dress again after standing
using System.Globalization;
using System.Text;
using System.Text.Json;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    internal sealed record HomeSeatInfo(UUID Id, string Name, Vector3 Pos, string Level, string Group, string Special,
                                        List<string> MenuFixed, List<string> MenuChoice, UUID TouchChild, bool Wander, Vector3? ChangeSpot = null);

    internal static List<HomeSeatInfo> ParseHomeSeats(string json)
    {
        var res = new List<HomeSeatInfo>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("seats", out var seats)) return res;
        foreach (var s in seats.EnumerateArray())
        {
            if (!s.TryGetProperty("uuid", out var u) || !UUID.TryParse(u.GetString(), out var id)) continue;
            string Str(string k) => s.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var pos = Vector3.Zero;
            if (s.TryGetProperty("pos", out var pv) && pv.ValueKind == JsonValueKind.Array && pv.GetArrayLength() >= 3)
                pos = new Vector3((float)pv[0].GetDouble(), (float)pv[1].GetDouble(), (float)pv[2].GetDouble());
            var fixedSteps = new List<string>(); var choice = new List<string>();
            if (s.TryGetProperty("menu", out var mv) && mv.ValueKind == JsonValueKind.Array)
                foreach (var step in mv.EnumerateArray())
                {
                    if (step.ValueKind == JsonValueKind.String) fixedSteps.Add(step.GetString());
                    else if (step.ValueKind == JsonValueKind.Array) { choice = step.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).ToList(); break; }
                }
            var touch = UUID.Zero; var tc = Str("touch_child"); if (tc != null) UUID.TryParse(tc, out touch);
            bool wander = !s.TryGetProperty("wander", out var wv) || wv.ValueKind != JsonValueKind.False;
            Vector3? cs = null;
            if (s.TryGetProperty("change_spot", out var cv) && cv.ValueKind == JsonValueKind.Array && cv.GetArrayLength() >= 3)
                cs = new Vector3((float)cv[0].GetDouble(), (float)cv[1].GetDouble(), (float)cv[2].GetDouble());
            res.Add(new HomeSeatInfo(id, Str("name") ?? "?", pos, Str("level") ?? "", Str("group"), Str("special"), fixedSteps, choice, touch, wander, cs));
        }
        return res;
    }

    static List<HomeSeatInfo> LoadHomeSeats()
    {
        try { return File.Exists(HomeSeatsFile) ? ParseHomeSeats(File.ReadAllText(HomeSeatsFile)) : new(); }
        catch (Exception ex) { WLog("home seats file: " + ex.Message); return new(); }
    }

    // Graph distance with a level penalty: the upper patio sits right above the beach, so a plain horizontal
    // projection would put the outdoor shower (z20.9) on the patio edge (z29). 2.5 m of slack keeps sloped paths as before.
    internal static float GDist(Vector3 q, Vector3 x) => HDist(q, x) + 3f * Math.Max(0f, Math.Abs(q.Z - x.Z) - 2.5f);

    // menu for this sit: fixed steps + one random choice (shower: Single* > F1|F2)
    internal static List<string> HomeSeatMenu(HomeSeatInfo i, Random r)
    {
        if (i == null || (i.MenuFixed.Count == 0 && i.MenuChoice.Count == 0)) return null;
        var m = i.MenuFixed.ToList();
        if (i.MenuChoice.Count > 0) m.Add(i.MenuChoice[r.Next(i.MenuChoice.Count)]);
        return m;
    }

    // seat spot key: grouped chairs count as one spot (pick the spot, then a random free chair in it)
    internal static string HomeSeatSpot(UUID id, IReadOnlyDictionary<UUID, HomeSeatInfo> infos) =>
        infos.TryGetValue(id, out var i) && !string.IsNullOrEmpty(i.Group) ? "group:" + i.Group : id.ToString();

    // path to a seat: graph route to the nearest graph point + a final approach 1.2 m short of the seat
    static (List<Vector3> pts, Vector3 pathPt, float fromPath, string err) HomeSeatRoute(Graph g, Vector3 from, Vector3 seat)
    {
        var (q, d) = NearestOnGraph(g, seat);
        var (pts, err) = GraphRouteTo(g, from, q);
        if (err != null) return (null, q, d, err);
        if (HDist(q, seat) > 2.2f)
        {
            var dir = Vector3.Normalize(new Vector3(q.X - seat.X, q.Y - seat.Y, 0));
            pts.Add(new Vector3(seat.X + dir.X * 1.2f, seat.Y + dir.Y * 1.2f, Math.Min(q.Z, seat.Z + 0.6f)));
        }
        return (pts, q, d, null);
    }

    // ---- per-seat specials --------------------------------------------------------------------------------
    static string pendingDressOutfit; // set while undressed for the tub; dressed again once she stands

    static readonly HashSet<WearableType> UndressLayerTypes = new() { WearableType.Shirt, WearableType.Pants, WearableType.Underpants, WearableType.Undershirt,
        WearableType.Jacket, WearableType.Skirt, WearableType.Socks, WearableType.Shoes, WearableType.Gloves, WearableType.Alpha };

    static async Task<string> UndressForSeat(CancellationToken ct)
    {
        var sb = new StringBuilder();
        outfitChangeUntil = DateTime.Now.AddSeconds(60);
        try
        {
            var prot = OutfitProtectedIds();
            var roots = WornPrims(); await EnsureProperties(Sim, roots);
            foreach (var r in roots)
            {
                if (IsHudAttachPoint(r.PrimData.AttachmentPoint)) continue;
                var id = AttachItemId(r); var nm = r.Properties?.Name ?? "";
                if (id == UUID.Zero || prot.Contains(id) || OutfitGroup(nm) != null || !ClothingNameRx.IsMatch(nm)) continue;
                await RemoveCofLinksForItem(id, "undress (tub)", ct);
                await DetachItemAsync(id, "undress (tub)");
                sb.Append($"off '{nm}'; ");
            }
            List<AppearanceManager.WearableData> cur; try { cur = client.Appearance.GetWearables().ToList(); } catch { cur = new(); }
            var layers = new List<InventoryItem>();
            foreach (var w in cur.Where(w => UndressLayerTypes.Contains(w.WearableType)).GroupBy(w => w.ItemID).Select(x => x.First()))
            { var it = await FetchItemRO(w.ItemID, ct); if (it != null) layers.Add(it); }
            foreach (var it in layers) await RemoveCofLinksForItem(it.UUID, "undress (tub)", ct);
            if (layers.Count > 0) { client.Appearance.RemoveFromOutfit(layers); sb.Append($"layers off: {string.Join(", ", layers.Select(l => "'" + l.Name + "'"))}"); }
            await Task.Delay(2000, ct);
        }
        finally { outfitChangeUntil = DateTime.Now.AddSeconds(15); }
        var res = sb.Length == 0 ? "nothing to take off" : sb.ToString().TrimEnd(' ', ';');
        WLog("UNDRESS before the tub: " + res);
        try { res += "; " + await ToplessExtrasOn(ct); } catch (Exception ex) { WLog("topless extras on failed: " + ex.GetBaseException().Message); }
        return res;
    }

    // undress / dress only standing on the floor beside the tub (David 09:06): within 0.8 m of the seat's change_spot and
    // at its floor height (not up on the tub rim)
    internal static bool AtChangeSpot(Vector3 here, Vector3 spot) => HDist(here, spot) <= 0.8f && Math.Abs(here.Z - spot.Z) <= 0.5f;

    static async Task<bool> GoToChangeSpot(Vector3? spot, string why, CancellationToken ct)
    {
        if (spot == null) return true;
        for (int k = 0; k < 3; k++)
        {
            if (AtChangeSpot(client.Self.SimPosition, spot.Value)) return true;
            try { await WalkLeg(spot.Value, 0.5f, "change spot (" + why + ")", ct); } catch (OperationCanceledException) { throw; } catch (Exception ex) { WLog("change spot walk: " + ex.GetBaseException().Message); }
            await Task.Delay(800, ct);
        }
        var ok = AtChangeSpot(client.Self.SimPosition, spot.Value);
        WLog($"change spot for {why}: {(ok ? "reached" : "NOT reached")} (at {V(client.Self.SimPosition)}, spot {V(spot.Value)})");
        return ok;
    }

    static Vector3? pendingDressSpot;

    static async Task DressAfterSeatIfPending()
    {
        var name = pendingDressOutfit; if (name == null || client.Self.SittingOn != 0) return;
        try { await EnsureStandingForWalk(CancellationToken.None); using var cts = new CancellationTokenSource(45000); await GoToChangeSpot(pendingDressSpot, "dressing", cts.Token); } catch (Exception ex) { WLog("dress spot: " + ex.GetBaseException().Message); }
        pendingDressOutfit = null;
        try
        {
            var r = BikiniNameRx.IsMatch(name) ? await BikiniOn() : await WearOutfitWithHuds(name, keepColor: true);
            WLog($"DRESSED again after the tub ('{name}'): " + r.Replace("\n", " | ")[..Math.Min(400, r.Length)]);
            try { WLog("topless extras off: " + await ToplessExtrasOff()); } catch (Exception ex) { WLog("topless extras off failed: " + ex.GetBaseException().Message); }
        }
        catch (Exception ex) { WLog("dress after the tub failed: " + ex.GetBaseException().Message); pendingDressOutfit = name; }
    }

    // routes/_topless-extras.txt: attachments worn only while topless (nipple rings); "<uuid> <exact name>" per line
    internal static List<(UUID id, string name)> ParseToplessExtras(string text)
    {
        var res = new List<(UUID, string)>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var l = raw.Trim(); if (l.Length == 0 || l.StartsWith("#")) continue;
            var sp = l.IndexOf(' '); if (sp <= 0) continue;
            if (!UUID.TryParse(l[..sp], out var id) || id == UUID.Zero) continue;
            var nm = l[(sp + 1)..].Trim(); if (nm.Length == 0) continue;
            if (!res.Any(r => r.Item1 == id)) res.Add((id, nm));
        }
        return res;
    }

    static List<(UUID id, string name)> ToplessExtras()
    {
        try { var f = Path.Combine(RouteDir, "_topless-extras.txt"); return File.Exists(f) ? ParseToplessExtras(File.ReadAllText(f)) : new(); }
        catch { return new(); }
    }

    static async Task<string> ToplessExtrasOn(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var worn = WornPrims().Select(AttachItemId).ToHashSet();
        foreach (var (id, nm) in ToplessExtras())
        {
            if (worn.Contains(id)) { sb.Append($"'{nm}' already worn; "); continue; }
            var inv = await FetchItemRO(id, ct);
            if (inv == null || !string.Equals(inv.Name?.Trim(), nm, StringComparison.OrdinalIgnoreCase)) { sb.Append($"'{nm}' not found / name mismatch; "); continue; }
            client.Appearance.Attach(inv, AttachmentPoint.Default, false); // ADD, never replace
            sb.Append($"on '{nm}'; ");
        }
        var r = sb.Length == 0 ? "no topless extras configured" : sb.ToString().TrimEnd(' ', ';');
        WLog("TOPLESS extras: " + r);
        return r;
    }

    static async Task<string> ToplessExtrasOff()
    {
        var sb = new StringBuilder();
        foreach (var (id, nm) in ToplessExtras())
            sb.Append($"'{nm}': {await DetachItemAsync(id, "dressed again (topless extras off)")}; "); // detach also removes the COF link
        return sb.Length == 0 ? "none configured" : sb.ToString().TrimEnd(' ', ';');
    }

    static bool TouchChildPrim(UUID child, string why)
    {
        var p = Sim?.ObjectsPrimitives.Values.FirstOrDefault(x => x != null && x.ID == child);
        if (p == null) { WLog($"{why}: prim {child} not in view"); return false; }
        client.Self.Touch(p.LocalID); WLog($"{why}: touched {child} (local {p.LocalID})");
        return true;
    }

    // ---- planner check (no walking): which catalogued seats the graph planner can reach ----------------------
    static async Task<string> HomeSeatsCheck(Graph g)
    {
        var sb = new StringBuilder(); int ok = 0, bad = 0;
        var sim = Sim; var me = client.Self.SimPosition;
        var starts = new List<(string name, Vector3 p)> { ("here", me) };
        foreach (var pl in new[] { "front", "beach" }) if (g.Places.TryGetValue(pl, out var pn)) starts.Add((pl, g.N[pn.node]));
        foreach (var i in LoadHomeSeats().Where(i => i.Wander))
        {
            var prim = sim.ObjectsPrimitives.Values.FirstOrDefault(x => x != null && x.ID == i.Id);
            var pos = prim?.Position ?? i.Pos;
            var (q, d) = NearestOnGraph(g, pos);
            var why = new List<string>();
            if (prim == null) why.Add("not in view right now (catalogued position used)");
            if (d > 6f) why.Add($"{d:F1} m from the path graph");
            string routeNote = "";
            foreach (var (sn, sp) in starts)
            {
                var (pts, _, _, err) = HomeSeatRoute(g, sp, pos);
                if (err != null) { why.Add($"no route from {sn}: {err}"); continue; }
                var poly = new Poly(pts);
                var bnd = await CheckBounds(poly, new RouteOpts { Label = "seat check", Idle = true });
                if (bnd != null) why.Add($"from {sn}: {bnd}");
                else if (sn == "front") routeNote = $"{poly.Len:F0} m from front";
            }
            bool reach = d <= 6f && !why.Any(w => w.StartsWith("no route") || w.StartsWith("from "));
            if (reach) ok++; else bad++;
            sb.AppendLine($"{(reach ? "OK  " : "FAIL")} '{i.Name}' {i.Id} {P3(pos)} [{i.Level}{(i.Group != null ? ", group " + i.Group : "")}{(i.Special != null ? ", " + i.Special : "")}] graph {d:F1} m {routeNote}{(why.Count > 0 ? " — " + string.Join("; ", why) : "")}");
        }
        return $"home seat planner check: {ok} reachable, {bad} not\n" + sb.ToString().TrimEnd();
    }

    // ---- offline selftest over the repo's graph + seat catalogue ------------------------------------------
    static string FindRoutesDirForTest()
    {
        var env = Environment.GetEnvironmentVariable("GT_TEST_ROUTE_DIR");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "_graph-Peronaut.json"))) return env;
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            {
                foreach (var cand in new[] { Path.Combine(d.FullName, "textclient", "routes"), Path.Combine(d.FullName, "routes") })
                    if (File.Exists(Path.Combine(cand, "_graph-Peronaut.json")) && File.Exists(Path.Combine(cand, "_seats-peronaut-home.json"))) return cand;
            }
        return RouteDir;
    }

    internal static string HomeSeatsSelfTest()
    {
        var sb = new StringBuilder(); int pass = 0, fail = 0;
        void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        var dir = FindRoutesDirForTest();
        var g = LoadGraphFile(Path.Combine(dir, "_graph-Peronaut.json"));
        var seats = ParseHomeSeats(File.ReadAllText(Path.Combine(dir, "_seats-peronaut-home.json")));
        C(g != null && seats.Count > 0, $"graph + seats loaded from {dir}");
        var infos = seats.ToDictionary(s => s.Id);
        UUID U(string s) => UUID.Parse(s);
        var front = g.N[g.Places["front"].node]; var beach = g.N[g.Places["beach"].node];
        foreach (var s in seats.Where(s => s.Wander))
        {
            var (q, d) = NearestOnGraph(g, s.Pos);
            bool level = Math.Abs(q.Z - s.Pos.Z) < 2.6f;
            var r1 = HomeSeatRoute(g, front, s.Pos); var r2 = HomeSeatRoute(g, beach, s.Pos);
            C(d <= 6f && level && r1.err == null && r2.err == null, $"'{s.Name}' {s.Id.ToString()[..8]} reachable from front + beach (graph {d:F1} m, path z {q.Z:F1} vs seat {s.Pos.Z:F1})");
        }
        // new tour seats are there, with their spot groups and specials
        string[] tour = { "e386ff1e-825c-14ec-715d-3dfc2df5e54b", "c78d42b6-c75c-df84-aa8c-8f78cbd2cb6c", "1f6e8ba6-3ea2-02c3-6e3b-2618f4c1b6aa", "81f12788-a433-0222-8c30-9eeae62cf80e",
                          "889748ee-b571-128e-7868-64aafad8f7ab", "4f86d9a9-97de-3e9e-d1c4-d6ba9a843f1d", "a3f619bd-52c2-0e1f-e8c3-7f95375f2ae9", "e352f3a5-1c88-1747-b5fb-b9bc703eb2db",
                          "9970956b-d6dc-8328-224a-6f527268eb3f", "6ddb08d2-f9bd-4118-d602-af6f0386bd28", "753e6d7c-5cc0-5ab2-d015-f80b6ef5abf6", "90a518f7-e954-b6f1-3d4f-eb0c7ed1449f" };
        C(tour.All(t => infos.TryGetValue(U(t), out var i) && i.Wander), "all 12 tour seats catalogued for the wander");
        C(HomeSeatSpot(U(tour[0]), infos) == HomeSeatSpot(U(tour[1]), infos) && HomeSeatSpot(U(tour[3]), infos) == HomeSeatSpot(U(tour[4]), infos)
          && HomeSeatSpot(U(tour[7]), infos) == HomeSeatSpot(U(tour[8]), infos), "porch rockers / Nerenzo chairs / deck poolside chairs are one random-pick spot each");
        C(new[] { 9, 10, 11 }.Select(k => HomeSeatSpot(U(tour[k]), infos)).Distinct().Count() == 3, "the 3 pier benches are separate spots");
        var sh = infos[U(tour[5])];
        C(sh.Special == "shower" && sh.TouchChild == U("fd4c7e59-f20c-c393-1d40-54a4c1f08ecd"), "shower: special + valve child prim");
        var picks = Enumerable.Range(0, 40).Select(k => HomeSeatMenu(sh, new Random(k))).ToList();
        C(picks.All(m => m.Count == 2 && m[0] == "Single*" && (m[1] == "F1" || m[1] == "F2")) && picks.Select(m => m[1]).Distinct().Count() == 2, "shower menu: Single* then a random F1/F2");
        C(infos.TryGetValue(U("53f8929b-0abc-6e12-fac9-f9d6f9bfe6bc"), out var tub) && tub.Special == "undress", "clawfoot tub: undress before, dress after");
        var tx = ParseToplessExtras("# c\n\nf2a379d0-2952-3cc5-9b88-ca554cbfdec2 [BB] Nipple Rings - X (Orig.)\nbad line\n00000000-0000-0000-0000-000000000000 zero\nf2a379d0-2952-3cc5-9b88-ca554cbfdec2 dup\n");
        C(tx.Count == 1 && tx[0].name == "[BB] Nipple Rings - X (Orig.)", "topless extras: uuid + name with spaces, comments/bad/zero/dup skipped");
        var txf = Path.Combine(dir, "_topless-extras.txt");
        C(File.Exists(txf) && ParseToplessExtras(File.ReadAllText(txf)).Any(e => e.name.Contains("Nipple Rings")), "topless extras data file lists the nipple rings");
        C(tub?.ChangeSpot is Vector3 cs && HDist(cs, tub.Pos) is > 1f and < 2.5f && NearestOnGraph(g, cs).Item2 < 0.5f && cs.Z <= tub.Pos.Z + 0.5f, "tub change spot: beside the tub, on the floor path");
        C(tub?.ChangeSpot != null && AtChangeSpot(tub.ChangeSpot.Value + new Vector3(0.3f, 0, 0.2f), tub.ChangeSpot.Value) && !AtChangeSpot(tub.ChangeSpot.Value + new Vector3(0, 0, 0.9f), tub.ChangeSpot.Value)
          && !AtChangeSpot(tub.Pos, tub.ChangeSpot.Value), "change spot check: beside on the floor yes, up on the tub no");
        C(seats.Any(s => s.Id.ToString().StartsWith("fee00d83") && !s.Wander), "Nerenzo parasol is not a seat");
        C(!seats.Any(s => HDist(s.Pos, new Vector3(217f, 24f, 21f)) < 1.5f && s.Pos.Z < 23f), "multi-seat boat under the pier is not catalogued");
        // level-aware projection: the shower is under the patio edge but must land on the beach
        var (qs, _) = NearestOnGraph(g, sh.Pos);
        C(qs.Z < 23f, $"shower projects onto the beach (z {qs.Z:F1}), not the patio above");
        // nav links: GOOSE 3-step stair beach<->deck, Burgundy pier path south
        int N(string p) => g.Places[p].node;
        bool Edge(string a, string b) => g.E.Any(e => (e.a == N(a) && e.b == N(b)) || (e.a == N(b) && e.b == N(a)));
        C(Edge("deck-stair", "deck-top") && Edge("rowboat", "deck-stair") && Edge("deck-top", "mooring-deck"), "graph: beach (rowboat) <-> GOOSE 3-step stair <-> Mooring deck");
        C(Edge("pier-start", "pier") && Edge("pier", "pier-south") && Edge("pier-south", "pier-end"), "graph: Burgundy pier path south from the beach");
        var (pp, perr) = GraphRoute(g, g.N[N("porch")], N("pier-end"));
        C(perr == null && pp.Any(p => HDist(p, g.N[N("front-steps")]) < 0.5f) && pp.Any(p => HDist(p, g.N[N("beach-east")]) < 0.5f), "porch -> pier end goes via the front steps, east stairs and beach");
        // her own position on the beach under the patio starts on the beach graph, not on the patio
        var (bp, berr) = GraphRoute(g, new Vector3(223.3f, 54.6f, 21.5f), N("mooring-deck"));
        C(berr == null && bp.All(p => p.Z < 24f), "from the beach under the patio: route stays on the lower level");
        // 2026-10-06: Mirage bed / east-deck only through side-room doorways (no wall-cutting 8-12 / 7-11)
        var places = g.Places.ToDictionary(kv => kv.Key, kv => kv.Value.node, StringComparer.OrdinalIgnoreCase);
        var (doorOk, doorDetail) = PeronautDoorwayGraphOk(places, g.E);
        C(doorOk, "doorway routing: " + doorDetail);
        var (frBed, frErr) = GraphRoute(g, g.N[N("front")], N("bed"));
        C(frErr == null && frBed.Any(p => Math.Abs(p.Y - 76f) < 0.6f && p.X > 221f && p.X < 227f),
          "front->bed passes side-room-1 doorway band (y~76, x~222-226)");
        var (bedEast, beErr) = GraphRoute(g, g.N[N("bed")], N("east-deck"));
        C(beErr == null && bedEast.Any(p => Math.Abs(p.Y - 76f) < 0.6f && p.X < 223.5f)
          && bedEast.Any(p => Math.Abs(p.Y - 76f) < 0.6f && p.X > 233f),
          "bed->east-deck passes west then east side-room doorway bands");
        return $"home seats selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
    }
}
