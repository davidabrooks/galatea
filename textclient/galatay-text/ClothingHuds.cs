// ClothingHuds.cs (2026-10-06, David: every non-beach outfit change does what the Bikini already did with
// '<HUD> Spicy Bikini': attach the top's color HUD, press a random color/pattern, wait until it shows, detach the HUD).
// One spec per HUD in routes/_clothing-huds.json (the Bikini included). Pure parts (spec parsing, button options,
// random pick that avoids the previous pick) are covered by --clothing-huds-selftest; the clicks themselves are
// verified live (texture change on the worn piece).
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    // a swatch grid drawn on one face of one prim: press by touch ST (s left->right, t bottom->top)
    internal sealed record HudGridGroup(string Name, List<float> T, List<string> Names);
    internal sealed record HudGrid(int Link, int Face, List<float> S, List<HudGridGroup> Groups);
    internal sealed record ClothingHudSpec(UUID Hud, string HudName, List<UUID> Clothing, string LabelRx, HudGrid Grid, Dictionary<string, string> LabelNames);
    // one pressable color/pattern: a named button prim (Face < 0, no ST) or a spot on a face (ST)
    internal sealed record HudOption(int Link, uint Local, string Label, int Face = -1, float S = -1, float T = -1)
    {
        public bool UsesSt => S >= 0 && T >= 0;
    }

    internal static List<ClothingHudSpec> ParseClothingHudSpecs(string json)
    {
        var res = new List<ClothingHudSpec>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("tops", out var tops) || tops.ValueKind != JsonValueKind.Array) return res;
        foreach (var t in tops.EnumerateArray())
        {
            if (!t.TryGetProperty("hud", out var h) || !UUID.TryParse(h.GetString(), out var hud) || hud == UUID.Zero) continue;
            if (hud == AoItem || hud == RetiredAwpAo) continue; // never the AO
            var cloth = new List<UUID>();
            if (t.TryGetProperty("clothing", out var c) && UUID.TryParse(c.GetString(), out var ci)) cloth.Add(ci);
            if (t.TryGetProperty("also", out var al) && al.ValueKind == JsonValueKind.Array)
                foreach (var x in al.EnumerateArray()) if (UUID.TryParse(x.GetString(), out var xi)) cloth.Add(xi);
            cloth = cloth.Where(u => u != UUID.Zero).Distinct().ToList();
            if (cloth.Count == 0) continue;
            string rx = t.TryGetProperty("labels", out var l) ? l.GetString() : null;
            if (rx != null) { try { _ = new Regex(rx); } catch { rx = null; } }
            HudGrid grid = null;
            if (t.TryGetProperty("grid", out var g) && g.ValueKind == JsonValueKind.Object)
            {
                var s = g.TryGetProperty("s", out var sa) ? sa.EnumerateArray().Select(v => (float)v.GetDouble()).ToList() : new();
                var groups = new List<HudGridGroup>();
                if (g.TryGetProperty("groups", out var ga))
                    foreach (var gr in ga.EnumerateArray())
                        groups.Add(new HudGridGroup(gr.TryGetProperty("name", out var gn) ? gn.GetString() ?? "" : "",
                            gr.TryGetProperty("t", out var ta) ? ta.EnumerateArray().Select(v => (float)v.GetDouble()).ToList() : new(),
                            gr.TryGetProperty("names", out var na) ? na.EnumerateArray().Select(v => v.GetString() ?? "").ToList() : new()));
                grid = new HudGrid(g.TryGetProperty("link", out var lk) ? lk.GetInt32() : 1, g.TryGetProperty("face", out var fc) ? fc.GetInt32() : 0, s, groups);
            }
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (t.TryGetProperty("label_names", out var ln) && ln.ValueKind == JsonValueKind.Object)
                foreach (var p in ln.EnumerateObject()) names[p.Name] = p.Value.GetString() ?? "";
            res.Add(new ClothingHudSpec(hud, t.TryGetProperty("hud_name", out var hn) ? hn.GetString() ?? "" : "", cloth, rx, grid, names));
        }
        // one spec per HUD (a later duplicate adds its clothing to the first)
        return res.GroupBy(r => r.Hud).Select(gp => gp.First() with { Clothing = gp.SelectMany(x => x.Clothing).Distinct().ToList() }).ToList();
    }

    // clothing item -> HUD (every listed piece, Bikini top and panties both -> the Spicy Bikini HUD)
    internal static Dictionary<UUID, UUID> ParseClothingHudMap(string json)
    {
        var d = new Dictionary<UUID, UUID>();
        foreach (var s in ParseClothingHudSpecs(json)) foreach (var c in s.Clothing) d[c] = s.Hud;
        return d;
    }

    // Pure: every option on a grid HUD (row-major per group, 1-based numbering per group, optional names)
    internal static List<HudOption> HudGridOptions(HudGrid g, uint rootLocal)
    {
        var res = new List<HudOption>();
        if (g == null || g.S.Count == 0) return res;
        foreach (var gr in g.Groups)
        {
            int n = 0;
            foreach (var t in gr.T)
                foreach (var s in g.S)
                {
                    var nm = n < gr.Names.Count && gr.Names[n].Length > 0 ? " " + gr.Names[n] : "";
                    n++;
                    if (s is < 0f or > 1f || t is < 0f or > 1f) continue;
                    res.Add(new HudOption(g.Link, rootLocal, $"{gr.Name} {n}{nm}", g.Face, s, t));
                }
        }
        return res;
    }

    // Pure: the options a spec allows on this HUD's prims (grid spec, else named buttons filtered by the label regex)
    internal static List<HudOption> HudOptionsFor(IReadOnlyList<(int link, uint local, string name, string desc)> prims, ClothingHudSpec spec)
    {
        if (spec?.Grid != null)
        {
            var root = prims.FirstOrDefault(p => p.link == spec.Grid.Link);
            return root == default ? new() : HudGridOptions(spec.Grid, root.local);
        }
        var opts = HudTextureOptions(prims).Select(o => new HudOption(o.link, o.local, o.label)).ToList();
        if (!string.IsNullOrEmpty(spec?.LabelRx)) opts = opts.Where(o => Regex.IsMatch(o.Label, spec.LabelRx, RegexOptions.IgnoreCase)).ToList();
        if (spec?.LabelNames is { Count: > 0 } ln) opts = opts.Select(o => ln.TryGetValue(o.Label, out var nm) && nm.Length > 0 ? o with { Label = o.Label + " " + nm } : o).ToList();
        return opts;
    }

    // Pure: random option, never the previous pick when there is any other (so two wears in a row look different);
    // 'exclude' = labels already tried in this round
    internal static HudOption PickHudOption(IReadOnlyList<HudOption> opts, string lastLabel, Random rng, ICollection<string> exclude = null)
    {
        var pool = opts.Where(o => exclude == null || !exclude.Contains(o.Label)).ToList();
        if (pool.Count > 1 && !string.IsNullOrEmpty(lastLabel)) pool = pool.Where(o => !o.Label.Equals(lastLabel, StringComparison.OrdinalIgnoreCase)).ToList();
        return pool.Count == 0 ? null : pool[rng.Next(pool.Count)];
    }

    // Pure: "default;f0;f1..." texture UUIDs (no tint/glow/material, so only a real texture swap counts)
    internal static string FaceTexSig(UUID def, IEnumerable<UUID> faces) => def + ";" + string.Join(";", faces);
    // Pure: prims whose face textures really changed; a prim with no data before or after (not rezzed yet) never counts
    internal static List<uint> TexIdsChanged(IReadOnlyDictionary<uint, string> before, IReadOnlyDictionary<uint, string> after) =>
        after.Where(kv => !string.IsNullOrEmpty(kv.Value) && before.TryGetValue(kv.Key, out var b) && !string.IsNullOrEmpty(b) && b != kv.Value)
             .Select(kv => kv.Key).OrderBy(k => k).ToList();

    // ---- last pick per HUD (run/hud-last-pick.json) ----------------------------------------------------------------
    static string HudLastPickFile => Env("GT_HUD_LAST_PICK", Path.Combine(Path.GetDirectoryName(LastNamedOutfitFile) ?? "/tmp", "hud-last-pick.json"));
    internal static Dictionary<string, string> ParseHudLastPicks(string json)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String) d[p.Name] = p.Value.GetString();
        }
        catch { }
        return d;
    }
    static string HudLastPick(UUID hud)
    {
        try { return File.Exists(HudLastPickFile) && ParseHudLastPicks(File.ReadAllText(HudLastPickFile)).TryGetValue(hud.ToString(), out var v) ? v : null; }
        catch { return null; }
    }
    static void SaveHudLastPick(UUID hud, string label)
    {
        try
        {
            var d = File.Exists(HudLastPickFile) ? ParseHudLastPicks(File.ReadAllText(HudLastPickFile)) : new(StringComparer.OrdinalIgnoreCase);
            d[hud.ToString()] = label;
            Directory.CreateDirectory(Path.GetDirectoryName(HudLastPickFile)!);
            File.WriteAllText(HudLastPickFile, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log("hud", "last-pick save: " + ex.Message); }
    }

    static List<ClothingHudSpec> LoadClothingHudSpecs()
    {
        try { return File.Exists(ClothingHudMapFile) ? ParseClothingHudSpecs(File.ReadAllText(ClothingHudMapFile)) : new(); }
        catch (Exception ex) { Log("hud", "clothing HUD map: " + ex.Message); return new(); }
    }

    // 'outfit hudmap': read-only, what each mapped HUD can pick
    static async Task<string> ClothingHudMapReport()
    {
        using var cts = new CancellationTokenSource(30000);
        var specs = LoadClothingHudSpecs();
        if (specs.Count == 0) return $"no clothing HUD specs in {ClothingHudMapFile}";
        var sb = new StringBuilder($"{ClothingHudMapFile}: {specs.Count} HUD(s)\n");
        foreach (var s in specs)
        {
            var hud = await FetchItemRO(s.Hud, cts.Token);
            var kind = s.Grid != null ? $"grid face {s.Grid.Face}: {HudGridOptions(s.Grid, 0).Count} swatches ({string.Join(", ", s.Grid.Groups.Select(g => $"{g.Name} {g.T.Count * s.Grid.S.Count}"))})" : $"named buttons, labels {s.LabelRx ?? "(any color/pattern)"}";
            sb.AppendLine($"  '{hud?.Name ?? s.HudName}' {s.Hud} {(hud == null ? "NOT IN INVENTORY" : "in inventory")} -> {s.Clothing.Count} piece(s); {kind}; last pick '{HudLastPick(s.Hud) ?? "-"}'");
        }
        return sb.ToString().TrimEnd();
    }

    internal static string ClothingHudsSelfTest()
    {
        var sb = new StringBuilder(); int pass = 0, fail = 0;
        void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        UUID U(int n) => new UUID($"00000000-0000-0000-0000-{n:D12}");
        // spec parsing
        var js = "{\"tops\":[{\"clothing\":\"" + U(1) + "\",\"also\":[\"" + U(2) + "\"],\"hud\":\"" + U(10) + "\",\"labels\":\"^[DWT]\\\\d+$\"}," +
                 "{\"clothing\":\"" + U(3) + "\",\"hud\":\"" + U(11) + "\",\"grid\":{\"link\":1,\"face\":0,\"s\":[0.25,0.75],\"groups\":[{\"name\":\"cotton\",\"t\":[0.8,0.6],\"names\":[\"red\",\"blue\",\"\",\"navy\"]},{\"name\":\"bad\",\"t\":[1.5]}]}}," +
                 "{\"clothing\":\"" + U(4) + "\",\"hud\":\"" + AoItem + "\"},{\"clothing\":\"x\",\"hud\":\"" + U(12) + "\"},{\"clothing\":\"" + U(5) + "\",\"hud\":\"" + U(10) + "\"}]}";
        var specs = ParseClothingHudSpecs(js);
        C(specs.Count == 2, $"two valid specs (AO HUD and bad clothing uuid dropped; got {specs.Count})");
        var bik = specs.FirstOrDefault(s => s.Hud == U(10));
        C(bik != null && bik.Clothing.SequenceEqual(new[] { U(1), U(2), U(5) }), "one spec per HUD: top + panties ('also') + a duplicate HUD entry merged");
        var map = ParseClothingHudMap(js);
        C(map.Count == 4 && map[U(2)] == U(10) && map[U(3)] == U(11) && !map.ContainsValue(AoItem), "clothing -> HUD map covers every listed piece");
        C(ParseClothingHudSpecs("{\"note\":1}").Count == 0 && ParseClothingHudSpecs("{\"tops\":[{\"clothing\":\"" + U(1) + "\",\"hud\":\"" + U(10) + "\",\"labels\":\"([\"}]}").Single().LabelRx == null, "no tops -> empty; a broken label regex is ignored");
        // grid options
        var grid = specs.First(s => s.Hud == U(11)).Grid;
        var go = HudGridOptions(grid, 777);
        C(go.Count == 4, $"grid: 2x2 swatches, the off-texture row (t 1.5) skipped ({go.Count})");
        C(go[0].Label == "cotton 1 red" && go[2].Label == "cotton 3" && go[3].Label == "cotton 4 navy", "grid labels: row-major numbering + names");
        C(go[3].UsesSt && go[3].S == 0.75f && go[3].T == 0.6f && go[3].Face == 0 && go[3].Local == 777, "grid option carries face + ST + root prim");
        // named-button options through a spec
        var spicy = new List<(int, uint, string, string)> { (1, 1, "<HUD> Spicy Bikini", ""), (2, 2, "[DETACH]", ""), (3, 3, "[TEXTURE]", "D6"), (4, 4, "[TEXTURE]", "W1"), (5, 5, "[TEXTURE]", "T9"), (6, 6, "Instagram", ""), (7, 7, "[TEXTURE]", "C38") };
        var so = HudOptionsFor(spicy, bik);
        C(so.Select(o => o.Label).OrderBy(x => x).SequenceEqual(new[] { "D6", "T9", "W1" }) && so.All(o => !o.UsesSt), "Spicy Bikini: only D/W/T [TEXTURE] buttons (label regex), pressed as buttons");
        C(HudOptionsFor(spicy, null).Count == 4, "no spec: every color/pattern button");
        var beth = new ClothingHudSpec(U(20), "", new() { U(21) }, @"^tx \d+m$", null, new(StringComparer.OrdinalIgnoreCase) { ["tx 3m"] = "gold heart" });
        var bo = HudOptionsFor(new List<(int, uint, string, string)> { (1, 1, "[HUD - Essential] Beth Tube Top", ""), (2, 2, "tx", "1m"), (3, 3, "tx", "3m"), (4, 4, "website", "https://x") }, beth);
        C(bo.Count == 2 && bo.Any(o => o.Label == "tx 3m gold heart"), "Beth: tx buttons only, with names");
        C(HudOptionsFor(new List<(int, uint, string, string)> { (2, 9, "x", "") }, specs.First(s => s.Hud == U(11))).Count == 0, "grid spec but its root link missing -> no options");
        // pick: random, never the last one
        var seen = new HashSet<string>();
        for (int i = 0; i < 60; i++) { var p = PickHudOption(go, "cotton 1 red", new Random(i)); seen.Add(p.Label); }
        C(!seen.Contains("cotton 1 red") && seen.Count == 3, $"never repeats the last pick, covers the rest ({string.Join(",", seen.OrderBy(x => x))})");
        C(PickHudOption(go.Take(1).ToList(), "cotton 1 red", new Random(1))?.Label == "cotton 1 red", "single option: picked even if it was last");
        C(PickHudOption(go, null, new Random(3), go.Select(o => o.Label).ToList()) == null && PickHudOption(new List<HudOption>(), null, new Random(1)) == null, "everything tried / nothing -> null");
        var tried = new HashSet<string> { "cotton 3", "cotton 4 navy" };
        C(PickHudOption(go, "cotton 1 red", new Random(5), tried)?.Label == "cotton 2 blue", "retry skips tried + last");
        // texture-change check (face UUIDs only)
        var t1 = FaceTexSig(U(30), new[] { U(31), U(32) });
        C(t1 == FaceTexSig(U(30), new[] { U(31), U(32) }) && t1 != FaceTexSig(U(30), new[] { U(31), U(33) }), "face sig: same UUIDs equal, one face swapped differs");
        var tb = new Dictionary<uint, string> { [1] = t1, [2] = "", [3] = t1 };
        var ta = new Dictionary<uint, string> { [1] = t1, [2] = t1, [3] = FaceTexSig(U(30), new[] { U(31), U(34) }), [4] = t1 };
        C(TexIdsChanged(tb, ta).SequenceEqual(new uint[] { 3 }), "changed: only a real face swap; not-rezzed-before and new prims don't count");
        C(TexIdsChanged(tb, tb).Count == 0 && TexIdsChanged(new Dictionary<uint, string> { [1] = t1 }, new Dictionary<uint, string> { [1] = "" }).Count == 0, "no swap / data lost -> nothing applied");
        // last-pick store
        var lp = ParseHudLastPicks("{\"" + U(10) + "\":\"D6\",\"n\":3}");
        C(lp.Count == 1 && lp[U(10).ToString()] == "D6" && ParseHudLastPicks("not json").Count == 0, "last-pick file: strings only, bad file -> empty");
        // the shipped map: Bikini + the three daily tops, each its own HUD, grid for ARTi'S
        var rd = FindRoutesDirForTest();
        var f = rd == null ? null : Path.Combine(rd, "_clothing-huds.json");
        if (f != null && File.Exists(f))
        {
            var real = ParseClothingHudSpecs(File.ReadAllText(f));
            C(real.Count == 4 && real.Select(r => r.Hud).Distinct().Count() == 4, $"shipped map: 4 HUDs ({real.Count})");
            var sp = real.FirstOrDefault(r => r.Hud == BikiniHudItem);
            C(sp != null && sp.Clothing.Contains(BikiniTopItem) && sp.Clothing.Contains(BikiniPantiesItem) && sp.LabelRx != null && Regex.IsMatch("D12", sp.LabelRx) && !Regex.IsMatch("C3", sp.LabelRx), "Spicy Bikini HUD: top + panties, D/W/T only");
            var art = real.FirstOrDefault(r => r.Hud == new UUID("cb0dc6c4-545c-3be6-8351-e049094315a7"));
            var ao = art?.Grid == null ? new() : HudGridOptions(art.Grid, 1);
            C(art != null && art.Clothing.Contains(StraplessTopItem) && ao.Count == 84 && ao.All(o => o.S is > 0 and < 1 && o.T is > 0 and < 1), $"ARTi'S HUD: 84 swatches on one face, all ST inside the texture ({ao.Count})");
            C(ao.Any(o => o.Label == "cotton 28 dark navy" && Math.Abs(o.S - 291f / 512) < 0.01 && Math.Abs(o.T - (1 - 468f / 1024)) < 0.01), "ARTi'S dark navy at the measured swatch centre");
            C(ao.Select(o => (o.S, o.T)).Distinct().Count() == 84, "ARTi'S swatches never overlap");
            var bt = real.FirstOrDefault(r => r.Clothing.Contains(new UUID("5c8487b7-0db2-34fd-a81b-fe2709c98021")));
            C(bt != null && bt.Hud == new UUID("6899891e-79d1-3a31-93ab-fac14a3bd75e") && bt.LabelRx != null && Regex.IsMatch("tx 5m", bt.LabelRx) && bt.LabelNames.Count == 5, "Beth tube top -> Beth Essential HUD, tx 1m..5m");
            var tee = real.FirstOrDefault(r => r.Clothing.Contains(new UUID("90d3e432-c8d1-3f2c-b800-6e88ed678c27")));
            C(tee != null && tee.Hud == new UUID("a2591928-d005-3af2-9b02-a04c9e5f93e7") && Regex.IsMatch("C33", tee.LabelRx) && Regex.IsMatch("P5", tee.LabelRx), "TETRA Chill T-Shirt -> Chill T-Shirt HUD, C colors + P patterns");
            C(!real.Any(r => r.Hud == AoItem), "no AO in the map");
        }
        else C(false, "routes/_clothing-huds.json not found");
        return $"clothing-huds selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
    }
}
