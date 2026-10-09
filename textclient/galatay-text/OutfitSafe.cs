// OutfitSafe.cs (2026-10-05 16:16, David: the 16:13 relog stacked my old brown hair on top of my current hair).
// Own outfit swap instead of LibreMetaverse ReplaceOutfitAsync:
//  - attachments: detach worn non-HUD attachments that are not in the outfit, attach the missing ones (ADD);
//    hair / head / mesh-body groups: if the outfit has none, keep the current one (never bald / headless); if it has one,
//    every other worn item of that group comes off first -> never two hairs. Never touches the AO, the Firestorm bridge,
//    the retired AWP controller, or other HUDs.
//  - wearables: body parts replace by type; clothing layers not in the outfit come off, missing ones are added.
//  - COF: explicit removals drop their links; afterwards every worn item without a COF link gets one (add-only sync),
//    so the next relog keeps exactly this look. Self-detach COF cleanup is paused while a swap runs.
//  - clothing HUDs: for each clothing piece of the outfit, a HUD in the same inventory folder ('<HUD> ...', '[HUD ...')
//    is attached, a random color/pattern button pressed, the change verified on the worn piece, then the HUD detached.
using System.Text;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace GalatayText;

public static partial class Program
{
    static DateTime outfitChangeUntil = DateTime.MinValue;
    internal static bool OutfitChangeActive => DateTime.Now < outfitChangeUntil;

    static readonly Regex HairRx = new(@"\bhair(style)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex HeadRx = new(@"/\s*HEAD\s*/|\bEvoX?\s+head\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex MeshBodyRx = new(@"\bMesh\s*Body\b|\bMeshbody\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ClothingNameRx = new(@"\b(top|shirt|t-shirt|tee|bikini|dress|skirt|jeans|pants|shorts|capris?|panties|bra|jacket|sweater|hoodie|cutoffs|boots|shoes|heels|sandals)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex ClothingHudNameRx = new(@"^\s*(<HUD>|\[HUD)|[-:]\s*HUD\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static string OutfitGroup(string name)
    {
        name ??= "";
        if (HairRx.IsMatch(name)) return "hair";
        if (HeadRx.IsMatch(name)) return "head";
        if (MeshBodyRx.IsMatch(name) && !name.Contains("Add-on", StringComparison.OrdinalIgnoreCase)) return "body";
        return null;
    }

    internal sealed record WornAtt(UUID Item, string Name, bool Hud);
    internal sealed record TargetObj(UUID Item, string Name);

    // Pure (selftest): which worn attachments come off, which outfit objects go on.
    internal static (List<UUID> detach, List<UUID> attach, List<string> notes) PlanAttachmentSwap(
        IReadOnlyList<WornAtt> worn, IReadOnlyList<TargetObj> target, ISet<UUID> protectedIds)
    {
        var notes = new List<string>();
        var tIds = target.Select(t => t.Item).ToHashSet();
        var tGroups = target.Select(t => OutfitGroup(t.Name)).Where(g => g != null).ToHashSet();
        var detach = new List<UUID>();
        foreach (var w in worn)
        {
            if (w.Hud || protectedIds.Contains(w.Item) || tIds.Contains(w.Item)) continue;
            var g = OutfitGroup(w.Name);
            if (g != null && !tGroups.Contains(g)) { notes.Add($"keep current {g} '{w.Name}' (outfit has no {g})"); continue; }
            detach.Add(w.Item);
        }
        // outfit has no hair but more than one hair is worn: keep only the first
        foreach (var g in new[] { "hair", "head" })
        {
            if (tGroups.Contains(g)) continue;
            var kept = worn.Where(w => !w.Hud && OutfitGroup(w.Name) == g && !detach.Contains(w.Item)).ToList();
            foreach (var extra in kept.Skip(1)) { detach.Add(extra.Item); notes.Add($"extra {g} '{extra.Name}' comes off (one {g} only)"); }
        }
        // the outfit itself lists two hairs: wear only the first
        var attach = new List<UUID>();
        var seenGroup = new HashSet<string>();
        var wornIds = worn.Select(w => w.Item).ToHashSet();
        foreach (var t in target)
        {
            if (protectedIds.Contains(t.Item)) continue;
            var g = OutfitGroup(t.Name);
            if (g is "hair" or "head") { if (!seenGroup.Add(g)) { notes.Add($"outfit lists a second {g} '{t.Name}': skipped"); continue; } }
            if (!wornIds.Contains(t.Item)) attach.Add(t.Item);
        }
        // and if a target hair is attached, any worn hair that is in the target but not the chosen one also comes off
        return (detach, attach, notes);
    }

    static HashSet<UUID> OutfitProtectedIds()
    {
        var s = new HashSet<UUID> { RetiredAwpAo };
        try { var ao = AoItem; if (ao != UUID.Zero) s.Add(ao); } catch { }
        foreach (var p in WornPrims())
        {
            var n = p.Properties?.Name ?? "";
            if (n.Contains(AoNameMatch, StringComparison.OrdinalIgnoreCase) || n.StartsWith("#Firestorm LSL Bridge", StringComparison.OrdinalIgnoreCase))
            { var it = AttachItemId(p); if (it != UUID.Zero) s.Add(it); }
        }
        return s;
    }

    static async Task<List<InventoryItem>> ResolveOutfitItems(InventoryFolder folder, CancellationToken ct)
    {
        var kids = await ReadFolderRO(folder.UUID, ct);
        var items = new List<InventoryItem>();
        foreach (var b in kids.Where(k => k.ParentUUID == folder.UUID))
        {
            if (b is InventoryItem link && link.IsLink()) { var t = await FetchItemRO(link.AssetUUID, ct); if (t != null && !t.IsLink()) items.Add(t); }
            else if (b is InventoryItem it && !it.IsLink()) items.Add(it);
        }
        return items.GroupBy(i => i.UUID).Select(g => g.First()).ToList();
    }

    // Wear a My Outfits folder without stacking (see header). Returns a report.
    static async Task<string> OutfitWearSafe(InventoryFolder folder, CancellationToken ct)
    {
        var sb = new StringBuilder();
        outfitChangeUntil = DateTime.Now.AddSeconds(60);
        try
        {
            var items = await ResolveOutfitItems(folder, ct);
            if (items.Count == 0) return $"outfit '{folder.Name}' has no wearable links";
            var prot = OutfitProtectedIds();
            var roots = WornPrims(); await EnsureProperties(Sim, roots);
            var worn = roots.Select(p => new WornAtt(AttachItemId(p), p.Properties?.Name ?? "?", IsHudAttachPoint(p.PrimData.AttachmentPoint)))
                            .Where(w => w.Item != UUID.Zero).GroupBy(w => w.Item).Select(g => g.First()).ToList();
            var objs = items.Where(i => i is InventoryObject || i is InventoryAttachment).ToList();
            var target = objs.Select(o => new TargetObj(o.UUID, o.Name)).ToList();
            var (detach, attach, notes) = PlanAttachmentSwap(worn, target, prot);
            foreach (var hid in ExtraHudsOff(worn, target.Select(t => t.Item).ToHashSet(), ToplessExtras().Select(e => e.id))) if (!detach.Contains(hid)) detach.Add(hid);
            foreach (var n in notes) sb.AppendLine("  " + n);

            // plan the wearables first (body parts replace by type, clothing layers swapped), then run everything in one
            // batch (RedressFast.OutfitSwapOrder): old attachments off together, layer + COF changes in one go each, the bake
            // right away (2026-10-08: the bikini change waited LibreMetaverse's 5 s debounce after each layer's COF link)
            var wears = items.OfType<InventoryWearable>().ToList();
            var tBody = wears.Where(w => w.AssetType == AssetType.Bodypart).ToList();
            var tCloth = wears.Where(w => w.AssetType != AssetType.Bodypart).ToList();
            List<AppearanceManager.WearableData> cur; try { cur = client.Appearance.GetWearables().ToList(); } catch { cur = new(); }
            var curCloth = cur.Where(w => !IsBodyPartType(w.WearableType)).GroupBy(w => w.ItemID).Select(g => g.First()).ToList();
            // layers linked only in the COF count as worn too: the server bakes from the COF, and the library's own list lags
            // after an ad-hoc swap (2026-10-08: Bimbette leg alphas 'COF only' stayed on under the new shorts)
            var cofF = await CofFolder(ct);
            if (cofF != null)
                foreach (var l in (await ReadFolderRO(cofF.UUID, ct)).OfType<InventoryItem>().Where(i => i.ParentUUID == cofF.UUID && i.IsLink() && i.InventoryType == InventoryType.Wearable))
                    if (!curCloth.Any(c => c.ItemID == l.AssetUUID) && await FetchItemRO(l.AssetUUID, ct) is InventoryWearable tw && tw.AssetType != AssetType.Bodypart)
                        curCloth.Add(new AppearanceManager.WearableData { ItemID = tw.UUID, AssetID = tw.AssetUUID, WearableType = tw.WearableType, AssetType = tw.AssetType });
            // clothed outfits always carry the clothed alphas (routes/_clothed-alphas.txt); the Bikini sheds them
            var clothedAlphaItems = new List<InventoryItem>();
            if (IsClothedOutfit(folder.Name))
                foreach (var (aid, anm) in ClothedAlphas())
                { var ai = await FetchItemRO(aid, ct); if (ai is InventoryWearable && string.Equals(ai.Name?.Trim(), anm, StringComparison.OrdinalIgnoreCase)) clothedAlphaItems.Add(ai); }
            var tClothIds = EffectiveOutfitLayers(tCloth.Select(c => c.UUID), clothedAlphaItems.Select(c => c.UUID), IsClothedOutfit(folder.Name));
            foreach (var ai in clothedAlphaItems) if (!tCloth.Any(c => c.UUID == ai.UUID)) tCloth.Add((InventoryWearable)ai);
            var removeCloth = new List<InventoryItem>();
            foreach (var cid in LayersNotInOutfit(curCloth.Select(c => c.ItemID), tClothIds)) // incl. system Alpha layers
            { var it = await FetchItemRO(cid, ct); if (it != null) removeCloth.Add(it); }
            var replacedBody = new List<UUID>();
            foreach (var b in tBody)
            {
                var old = cur.Where(w => w.WearableType == b.WearableType && w.ItemID != b.UUID).Select(w => w.ItemID).ToList();
                replacedBody.AddRange(old);
            }
            var curIds = cur.Select(w => w.ItemID).ToHashSet();
            var addCloth = tCloth.Where(c => !curIds.Contains(c.UUID)).Cast<InventoryItem>().ToList();
            var removeIds = removeCloth.Select(r => r.UUID).ToHashSet();
            var wornLayersAfterRemoval = cur.Where(w => !removeIds.Contains(w.ItemID)).Select(w => (w.ItemID, w.WearableType)).ToList();
            var t0 = DateTime.Now;

            foreach (var step in OutfitSwapOrder(detach.Count, removeCloth.Count, tBody.Count, addCloth.Count, attach.Count))
                switch (step)
                {
                    case SwapStep.DetachAll: // attachments off first (so a new hair never lands on top of the old one)
                        foreach (var id in detach)
                        {
                            var nm = worn.FirstOrDefault(w => w.Item == id)?.Name ?? id.ToString();
                            var r = DetachItem(id, "outfit swap");
                            sb.AppendLine($"  off: '{nm}'");
                            Log("outfit", $"swap '{folder.Name}': detach '{nm}' {id}: {r}");
                        }
                        break;
                    case SwapStep.LayersOff:
                        client.Appearance.RemoveFromOutfit(removeCloth);
                        foreach (var it in removeCloth) sb.AppendLine($"  layer off: '{it.Name}'");
                        break;
                    case SwapStep.BodyOn: client.Appearance.AddToOutfit(tBody.Cast<InventoryItem>().ToList(), true); break;
                    case SwapStep.LayersOn:
                        client.Appearance.AddToOutfit(addCloth, false);
                        foreach (var c in addCloth) sb.AppendLine($"  layer on: '{c.Name}'");
                        break;
                    // COF links: the library rebuilds its wearables from the COF after a bake, so a layer whose COF link
                    // survives the removal comes straight back (16:59 'Jiyoo tubetop')
                    case SwapStep.CofLinksOff: sb.AppendLine("  " + await CofLinksOffBatch(detach.Concat(removeIds), "outfit swap", ct)); break;
                    case SwapStep.CofLinksOn:
                        var descs = LayerLinkDescs(addCloth.OfType<InventoryWearable>().Select(w => (w.UUID, w.WearableType)), wornLayersAfterRemoval);
                        sb.AppendLine("  " + await CofLinksOnBatch(addCloth, descs, "outfit swap", ct));
                        break;
                    case SwapStep.RebakeNow: sb.AppendLine($"  {await RebakeNow($"outfit '{folder.Name}'")} at +{(DateTime.Now - t0).TotalSeconds:F1} s"); break;
                    case SwapStep.AttachAll: // ADD, never replace; a short pause after the detaches first
                        if (detach.Count > 0) { var left = 1500 - (int)(DateTime.Now - t0).TotalMilliseconds; if (left > 0) await Task.Delay(left, ct); }
                        foreach (var id in attach)
                        {
                            var it = objs.First(o => o.UUID == id);
                            client.Appearance.Attach(it, AttachmentPoint.Default, false);
                            sb.AppendLine($"  on: '{it.Name}'");
                        }
                        break;
                }
            foreach (var old in replacedBody.Distinct()) await RemoveCofLinksForItem(old, "outfit swap (body part replaced)", ct);

            // wait for them to show up (max 20 s)
            for (int i = 0; i < 40 && attach.Count > 0; i++)
            {
                await Task.Delay(500, ct);
                var have = WornPrims().Select(AttachItemId).ToHashSet();
                if (attach.All(have.Contains)) break;
            }
            await Task.Delay(1500, ct);
            // verify the layers really came off; retry once (COF link + wearable), then report
            var removedIds = removeCloth.Select(r => r.UUID).ToHashSet();
            for (int k = 0; k < 2 && removedIds.Count > 0; k++)
            {
                List<UUID> back; try { back = client.Appearance.GetWearables().Select(w => w.ItemID).Where(removedIds.Contains).Distinct().ToList(); } catch { back = new(); }
                if (back.Count == 0) break;
                var again = removeCloth.Where(r => back.Contains(r.UUID)).ToList();
                if (k == 1) { sb.AppendLine($"  WARNING layers still worn after retry: {string.Join(", ", again.Select(a => a.Name))}"); break; }
                foreach (var it in again) await RemoveCofLinksForItem(it.UUID, "outfit swap (retry)", ct);
                client.Appearance.RemoveFromOutfit(again);
                sb.AppendLine($"  layer retry off: {string.Join(", ", again.Select(a => "'" + a.Name + "'"))}");
                await Task.Delay(3000, ct);
            }
            // verify the new layers really went on; add once more (2026-10-08: the removal retry re-sent a stale wearables
            // list and dropped the just-added '<Alpha mask> Chill Shorts - Maitreya')
            for (int k = 0; k < 2 && addCloth.Count > 0; k++)
            {
                HashSet<UUID> have; try { have = client.Appearance.GetWearables().Select(w => w.ItemID).ToHashSet(); } catch { have = new(); }
                var miss = LayersNotInOutfit(addCloth.Select(c => c.UUID), have).ToHashSet();
                if (miss.Count == 0) break;
                var again = addCloth.Where(c => miss.Contains(c.UUID)).ToList();
                if (k == 1) { sb.AppendLine($"  WARNING layers still not worn after retry: {string.Join(", ", again.Select(a => a.Name))}"); break; }
                client.Appearance.AddToOutfit(again, false);
                sb.AppendLine($"  layer retry on: {string.Join(", ", again.Select(a => "'" + a.Name + "'"))}");
                await Task.Delay(3000, ct);
            }
            var cofSync = await CofSyncAddMissing(ct, removedIds, addCloth.Select(c => c.UUID));
            sb.AppendLine("  " + cofSync);
            // a layer linked only now missed the bake already requested: ask for one more so the sim (and the library) wear it
            if (addCloth.Count > 0 && cofSync.Contains("added links"))
                try { await client.Appearance.RequestSetAppearance(true); sb.AppendLine("  rebake requested (new COF links)"); } catch (Exception ex) { Log("outfit", "rebake: " + ex.Message); }
            RememberNamedOutfit(folder.Name);
            Log("outfit", $"wore outfit '{folder.Name}' safely: {detach.Count} off, {attach.Count} on, {removeCloth.Count} layers off, {addCloth.Count} layers on, {tBody.Count} body parts");
            return $"wearing outfit '{folder.Name}' ({detach.Count} off, {attach.Count} on, {removeCloth.Count}/{addCloth.Count} layers off/on)\n" + sb.ToString().TrimEnd();
        }
        finally { outfitChangeUntil = DateTime.Now.AddSeconds(15); }
    }

    // ---- clothed alphas (routes/_clothed-alphas.txt, David 2026-10-07) ---------------------------------------------
    internal static bool IsClothedOutfit(string outfitName) => !BikiniNameRx.IsMatch((outfitName ?? "").Trim()) && !NakedNameRx.IsMatch((outfitName ?? "").Trim());
    static readonly Regex NakedNameRx = new(@"^(naked|nude)$", RegexOptions.IgnoreCase);
    // Pure (selftest): undress-extra HUDs (The V Play HUD) worn but not in the new outfit come off. PlanAttachmentSwap never
    // touches HUDs; non-HUD extras (rings, The V) already come off there as "not in the outfit".
    internal static List<UUID> ExtraHudsOff(IEnumerable<WornAtt> worn, ISet<UUID> outfitItems, IEnumerable<UUID> extras)
    {
        var ex = extras.ToHashSet();
        return worn.Where(w => w.Hud && ex.Contains(w.Item) && !outfitItems.Contains(w.Item)).Select(w => w.Item).Distinct().ToList();
    }
    // Pure (selftest): the layers an outfit wear keeps/adds = its own layers, plus the clothed alphas when clothed.
    internal static HashSet<UUID> EffectiveOutfitLayers(IEnumerable<UUID> outfitLayers, IEnumerable<UUID> clothedAlphas, bool clothed)
    {
        var s = outfitLayers.Where(x => x != UUID.Zero).ToHashSet();
        if (clothed) foreach (var a in clothedAlphas) if (a != UUID.Zero) s.Add(a);
        return s;
    }
    static List<(UUID id, string name)> ClothedAlphas()
    {
        try { var f = Path.Combine(RouteDir, "_clothed-alphas.txt"); return File.Exists(f) ? ParseToplessExtras(File.ReadAllText(f)) : new(); }
        catch { return new(); }
    }
    // undress paths that do not go through OutfitWearSafe (Bikini fallback swap): show the whole body again
    static async Task<string> ClothedAlphasOff(string why, CancellationToken ct)
    {
        List<UUID> worn; try { worn = client.Appearance.GetWearables().Select(w => w.ItemID).ToList(); } catch { return "clothed alphas: wearables unknown"; }
        var off = new List<InventoryItem>();
        foreach (var (id, _) in ClothedAlphas()) if (worn.Contains(id)) { var it = await FetchItemRO(id, ct); if (it != null) off.Add(it); }
        foreach (var it in off) await RemoveCofLinksForItem(it.UUID, why, ct);
        if (off.Count > 0) client.Appearance.RemoveFromOutfit(off);
        return off.Count == 0 ? "clothed alphas: none worn" : $"clothed alphas off ({why}): {string.Join(", ", off.Select(o => "'" + o.Name + "'"))}";
    }

    // Pure (selftest): worn clothing layers (system Alpha layers included) that are not in the new outfit's definition.
    internal static List<UUID> LayersNotInOutfit(IEnumerable<UUID> wornClothingLayers, ISet<UUID> outfitLayers) =>
        wornClothingLayers.Where(id => id != UUID.Zero && !outfitLayers.Contains(id)).Distinct().ToList();

    // Pure (selftest, 2026-10-07 Valentine Dress): after an ad-hoc 'wear remove' of a clothing attachment, the system Alpha
    // layers that came with it (same My Outfits folder) come off once none of that outfit's clothing attachments is still
    // worn. Tee off with the jeans still on -> alphas stay; jeans off too -> alphas off.
    internal static List<UUID> OrphanAlphas(IReadOnlyList<(UUID id, WearableType type)> wornLayers, ISet<UUID> outfitItems,
        ISet<UUID> outfitClothingAtts, ISet<UUID> wornAttsAfter)
    {
        if (outfitClothingAtts.Overlaps(wornAttsAfter)) return new();
        return wornLayers.Where(l => l.type == WearableType.Alpha && outfitItems.Contains(l.id)).Select(l => l.id).Distinct().ToList();
    }

    // Ad-hoc clothing change ('wear remove' of a clothing attachment, e.g. the chat routine swapping into a dress by hand):
    // drop that outfit's orphaned Alpha layers and their COF links. Remembered outfit first, then other My Outfits folders.
    static async Task<string> DropOrphanAlphasAfterRemove(InventoryItem removed, CancellationToken ct)
    {
        if (removed == null || !ClothingNameRx.IsMatch(removed.Name ?? "") || ClothingHudNameRx.IsMatch(removed.Name ?? "")) return null;
        List<(UUID id, WearableType type)> layers;
        try { layers = client.Appearance.GetWearables().Where(w => w.WearableType == WearableType.Alpha).Select(w => (w.ItemID, w.WearableType)).Distinct().ToList(); }
        catch { return null; }
        if (layers.Count == 0) return null; // nothing to orphan: no inventory reads
        bool leaving(UUID id) => id == removed.UUID || (detachIntent.TryGetValue(id, out var di) && (DateTime.Now - di.t).TotalSeconds < 60);
        var wornAfter = WornPrims().Where(p => !IsHudAttachPoint(p.PrimData.AttachmentPoint)).Select(AttachItemId)
                                   .Where(id => id != UUID.Zero && !leaving(id)).ToHashSet();
        var mo = await FindMyOutfits(ct); if (mo == null) return null;
        var folders = (await ReadFolderRO(mo.UUID, ct)).OfType<InventoryFolder>().Where(f => f.ParentUUID == mo.UUID)
                        .OrderBy(f => string.Equals(f.Name, lastNamedOutfit, StringComparison.OrdinalIgnoreCase) ? 0 : 1).Take(40).ToList();
        var drop = new HashSet<UUID>();
        foreach (var f in folders)
        {
            var items = await ResolveOutfitItems(f, ct);
            if (!items.Any(i => i.UUID == removed.UUID)) continue;
            var atts = items.Where(i => (i is InventoryObject || i is InventoryAttachment) && ClothingNameRx.IsMatch(i.Name ?? "") && !ClothingHudNameRx.IsMatch(i.Name ?? ""))
                            .Select(i => i.UUID).ToHashSet();
            var keepClothed = ClothedAlphas().Select(c => c.id).ToHashSet();
            foreach (var id in OrphanAlphas(layers, items.Select(i => i.UUID).ToHashSet(), atts, wornAfter)) if (!keepClothed.Contains(id)) drop.Add(id);
            if (string.Equals(f.Name, lastNamedOutfit, StringComparison.OrdinalIgnoreCase)) break; // the outfit she was in decides
        }
        if (drop.Count == 0) return null;
        var gone = new List<InventoryItem>();
        foreach (var id in drop) { var it = await FetchItemRO(id, ct); if (it == null) continue; await RemoveCofLinksForItem(id, "orphan alpha", ct); gone.Add(it); }
        if (gone.Count > 0) client.Appearance.RemoveFromOutfit(gone);
        var msg = $"alpha layer(s) that came with '{removed.Name}' off: {string.Join(", ", gone.Select(g => "'" + g.Name + "'"))} (COF links removed)";
        Log("wear", msg);
        return msg;
    }

    // Add a COF link for every worn attachment / wearable that has none (never removes anything; Firestorm bridge excluded).
    // 'also': items that must be linked even when the library's wearables list does not show them yet (layers an outfit
    // wear just added: a removal retry re-sent a stale list and they never got a COF link, so the next bake dropped them)
    static async Task<string> CofSyncAddMissing(CancellationToken ct, ICollection<UUID> skip = null, IEnumerable<UUID> also = null)
    {
        var cof = await CofFolder(ct); if (cof == null) return "COF sync: Current Outfit folder not found";
        var links = (await ReadFolderRO(cof.UUID, ct)).OfType<InventoryItem>().Where(l => l.ParentUUID == cof.UUID && l.IsLink()).ToList();
        var linked = links.Select(l => l.AssetUUID).ToHashSet();
        var want = new List<UUID>();
        foreach (var p in WornPrims())
        {
            if (IsTempAttachItem(AttachItemId(p))) continue;
            var it = AttachItemId(p); if (it != UUID.Zero && !linked.Contains(it)) want.Add(it);
        }
        try { foreach (var w in client.Appearance.GetWearables()) if (!linked.Contains(w.ItemID)) want.Add(w.ItemID); } catch { }
        foreach (var a in also ?? Enumerable.Empty<UUID>()) if (!linked.Contains(a)) want.Add(a);
        var added = new List<string>();
        foreach (var id in want.Distinct())
        {
            if (skip != null && skip.Contains(id)) continue; // just taken off: never re-link it
            var it = await FetchItemRO(id, ct); if (it == null || it.IsLink()) continue;
            if (it.Name.StartsWith("#Firestorm LSL Bridge", StringComparison.OrdinalIgnoreCase)) continue;
            if (ClothingHudNameRx.IsMatch(it.Name)) continue; // clothing HUDs are transient
            try
            {
                using var lt = CancellationTokenSource.CreateLinkedTokenSource(ct); lt.CancelAfter(20000);
                var made = await client.Inventory.CreateLinkAsync(cof.UUID, it.UUID, it.Name, "", it.InventoryType, UUID.Random(), lt.Token);
                if (made != null) added.Add($"'{it.Name}'");
            }
            catch (Exception ex) { Log("outfit", $"COF link for '{it.Name}' failed: {ex.GetBaseException().Message}"); }
        }
        if (added.Count > 0) Log("outfit", $"COF sync added links: {string.Join(", ", added)}");
        return added.Count == 0 ? "COF sync: every worn item already linked" : $"COF sync: added links for {string.Join(", ", added)}";
    }

    // ---- clothing HUDs -------------------------------------------------------------------------------------------
    // Pure (selftest): color/pattern buttons of a clothing HUD. TETRA: [TEXTURE] desc C12/P3/D38/W1/T9; Pink Cream Pie: 'tx' desc 1m..5m.
    internal static List<(int link, uint local, string label)> HudTextureOptions(IReadOnlyList<(int link, uint local, string name, string desc)> prims)
    {
        var res = new List<(int, uint, string)>();
        foreach (var p in prims)
        {
            var n = (p.name ?? "").Trim(); var d = (p.desc ?? "").Trim();
            if (n.Equals("[TEXTURE]", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(d, @"^[A-Z]{1,2}\d+$", RegexOptions.IgnoreCase)) res.Add((p.link, p.local, d));
            else if (n.Equals("tx", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(d, @"^\d+m?$", RegexOptions.IgnoreCase)) res.Add((p.link, p.local, "tx " + d));
            else if (Regex.IsMatch(n, @"^(colou?r|swatch|texture)\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(n, @"detach|url|lm|group|website", RegexOptions.IgnoreCase)) res.Add((p.link, p.local, n + (d.Length > 0 ? " " + d : "")));
        }
        return res;
    }

    // Face texture UUIDs only (2026-10-07: the old whole-TextureEntry hash counted any tint/glow/material/full-update
    // difference, and a not-yet-rezzed prim ("" before) as "changed", so 'leather 6 grey' was reported applied while the
    // tube top kept its old texture). Comparison: TexIdsChanged (ClothingHuds.cs, pure).
    static string TexSig(Primitive p)
    {
        try
        {
            var t = p.Textures; if (t == null) return "";
            var faces = new List<UUID>();
            for (int i = 0; i < HudFaceCount(p); i++) faces.Add(t.GetFace((uint)i)?.TextureID ?? UUID.Zero);
            return FaceTexSig(t.DefaultTexture?.TextureID ?? UUID.Zero, faces);
        }
        catch { return ""; }
    }

    static Dictionary<uint, string> SnapshotTextures(IEnumerable<UUID> clothingItems)
    {
        var ids = clothingItems.ToHashSet(); var d = new Dictionary<uint, string>();
        foreach (var r in WornPrims().Where(p => ids.Contains(AttachItemId(p))))
            foreach (var p in LinkPrims(r)) d[p.LocalID] = TexSig(p);
        return d;
    }

    static async Task<Primitive> WaitWornItem(UUID item, int ms, CancellationToken ct)
    {
        for (int i = 0; i < ms / 250; i++)
        {
            var p = WornPrims().FirstOrDefault(x => AttachItemId(x) == item);
            if (p != null) return p;
            await Task.Delay(250, ct);
        }
        return null;
    }

    // Attach a clothing HUD, press a random color/pattern (spec: ClothingHuds.cs; never the previous pick), wait until the
    // change shows on the worn clothing, detach (with a re-check: the library's after-bake outfit send once re-attached a
    // HUD seconds after it came off).
    static async Task<string> HudRandomize(InventoryItem hud, List<UUID> clothingItems, CancellationToken ct, ClothingHudSpec spec = null, bool keepColor = false,
                                           IReadOnlyList<(UUID hud, string label)> matchTo = null)
    {
        if (hud == null) return "no HUD";
        if (hud.UUID == AoItem || hud.UUID == RetiredAwpAo || (hud.Name ?? "").Contains(AoNameMatch, StringComparison.OrdinalIgnoreCase)) return $"refused: '{hud.Name}' is the AO";
        var sb = new StringBuilder();
        bool wasOn = WornPrims().Any(p => AttachItemId(p) == hud.UUID);
        if (!wasOn) client.Appearance.Attach(hud, AttachmentPoint.Default, false);
        var root = await WaitWornItem(hud.UUID, 15000, ct);
        if (root == null) return $"'{hud.Name}' did not attach within 15 s";
        sb.Append($"'{hud.Name}': attached; ");
        await Task.Delay(3000, ct); // HUD scripts: 'HUD ready'
        // the clothing must be on and rezzed, or a change can never be seen
        for (int i = 0; i < 20 && SnapshotTextures(clothingItems).Count == 0; i++) await Task.Delay(500, ct);
        var prims = LinkPrims(root); await EnsureProperties(Sim, prims);
        var list = prims.Select((p, i) => (i + 1, p.LocalID, p.Properties?.Name ?? "?", p.Properties?.Description ?? "")).ToList();
        var opts = HudOptionsFor(list, spec);
        if (opts.Count == 0)
        {
            // no mapped / named buttons: log the layout, then try touchable prims / faces, verified on the clothing
            Log("hud", $"'{hud.Name}' has no mapped color buttons; prims: " + string.Join(" | ", prims.Select((p, i) => $"#{i + 1} '{p.Properties?.Name}' desc '{p.Properties?.Description}' faces {HudFaceCount(p)}{((p.Flags & PrimFlags.Touch) != 0 ? " touch" : "")}")));
            var (ok2, how) = await HudFallbackPress(hud, prims, clothingItems, ct);
            sb.Append($"{how}; ");
        }
        else
        {
            var last = HudLastPick(hud.UUID);
            var keep = keepColor ? HudKeepOption(opts, last) : null;
            if (keep != null)
            {
                // same outfit re-dressed (tub / beach / relog): re-apply the remembered color, never a new one
                var target = prims.FirstOrDefault(p => p.LocalID == keep.Local);
                if (target != null)
                {
                    var before = SnapshotTextures(clothingItems);
                    if (keep.UsesSt) GrabAt(target, keep.Face, keep.S, keep.T);
                    else { client.Self.Grab(target.LocalID, Vector3.Zero, Vector3.Zero, Vector3.Zero, 0, Vector3.Zero, Vector3.Zero, Vector3.Zero); client.Self.DeGrab(target.LocalID); }
                    int changed = 0;
                    for (int i = 0; i < 10 && changed == 0; i++) { await Task.Delay(500, ct); changed = TexIdsChanged(before, SnapshotTextures(clothingItems)).Count; }
                    if (changed > 0) await Task.Delay(1000, ct);
                    SaveHudLastItems(hud.UUID, clothingItems, newPick: false); // these items now show it: next re-dress skips the HUD
                    sb.Append($"kept remembered '{last}' ({(changed > 0 ? $"re-applied, {changed} prim(s) changed" : "already showing")}); ");
                    Log("hud", $"'{hud.Name}' keep remembered '{last}': {(changed > 0 ? $"re-applied, {changed} prims changed" : "already showing")}");
                }
                else sb.Append($"remembered button '{last}' prim gone; ");
            }
            else if (keepColor) { sb.Append($"no remembered color ('{last ?? "-"}'): left as is; "); Log("hud", $"'{hud.Name}' keep mode, no remembered button '{last ?? "-"}': not recoloring"); }
            var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase); bool applied = false;
            string matchWhy = null;
            var pickFrom = keepColor ? opts : HudMatchOptions(opts, spec, matchTo, out matchWhy);
            if (!keepColor && matchWhy != null) { sb.Append($"color match ({matchWhy}): {pickFrom.Count} of {opts.Count}; "); Log("hud", $"'{hud.Name}' color match {matchWhy}: [{string.Join(", ", pickFrom.Select(o => o.Label))}]"); }
            for (int attempt = 0; attempt < 3 && !applied && !keepColor; attempt++)
            {
                var pick = PickHudOption(pickFrom, last, Random.Shared, tried) ?? PickHudOption(opts, last, Random.Shared, tried); if (pick == null) break;
                tried.Add(pick.Label);
                var target = prims.FirstOrDefault(p => p.LocalID == pick.Local);
                if (target == null) { sb.Append($"button '{pick.Label}' prim gone; "); continue; }
                var before = SnapshotTextures(clothingItems);
                if (pick.UsesSt) GrabAt(target, pick.Face, pick.S, pick.T);
                else
                {
                    client.Self.Grab(target.LocalID, Vector3.Zero, Vector3.Zero, Vector3.Zero, 0, Vector3.Zero, Vector3.Zero, Vector3.Zero);
                    client.Self.DeGrab(target.LocalID);
                }
                int changed = 0;
                for (int i = 0; i < 20 && changed == 0; i++) // wait for it to apply (max 10 s)
                {
                    await Task.Delay(500, ct);
                    var after = SnapshotTextures(clothingItems);
                    changed = TexIdsChanged(before, after).Count;
                }
                applied = changed > 0;
                { var aft = SnapshotTextures(clothingItems); foreach (var lid in TexIdsChanged(before, aft)) Log("hud", $"'{hud.Name}' prim {lid} faces {before[lid]} -> {aft[lid]}"); }
                if (applied) { await Task.Delay(1000, ct); SaveHudLastPick(hud.UUID, pick.Label); SaveHudLastItems(hud.UUID, clothingItems, newPick: true); } // let multi-prim pieces finish
                var how = pick.UsesSt ? $"face {pick.Face} st {pick.S:F3},{pick.T:F3}" : $"link {pick.Link} local {pick.Local}";
                sb.Append($"picked '{pick.Label}' (of {opts.Count}{(last != null ? $", last was '{last}'" : "")}) -> {(applied ? $"applied ({changed} prim(s) changed texture)" : "no visible change")}; ");
                Log("hud", $"'{hud.Name}' pick '{pick.Label}' {how}: {(applied ? $"applied, {changed} prims changed" : "no change seen")}");
            }
        }
        // detach + re-check
        for (int k = 0; k < 3; k++)
        {
            if (!WornPrims().Any(p => AttachItemId(p) == hud.UUID)) { if (k == 0) await Task.Delay(500, ct); else break; }
            if (WornPrims().Any(p => AttachItemId(p) == hud.UUID)) await DetachItemAsync(hud.UUID, "wear remove");
            await Task.Delay(6000, ct);
        }
        bool still = WornPrims().Any(p => AttachItemId(p) == hud.UUID);
        sb.Append(still ? "HUD STILL ATTACHED" : "HUD detached (verified)");
        return sb.ToString();
    }

    // Explicit clothing -> color HUD specs (routes/_clothing-huds.json, David 17:00; parsing in ClothingHuds.cs): checked before the same-folder lookup.
    static string ClothingHudMapFile => Path.Combine(RouteDir, "_clothing-huds.json"); // property: static init order across partial files

    static readonly System.Text.RegularExpressions.Regex HudControlRx = new(@"detach|close|minimi|maximi|lock|url|\blm\b|landmark|group|website|help|logo|reset|hide|show|redeliver|update|info|\bon\b|\boff\b|tab|page|next|prev|back|alpha|shine|gloss|mat(erial)?s?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    static int HudFaceCount(Primitive p) { try { var t = p.Textures; if (t?.FaceTextures == null) return 1; int n = 0; for (int i = 0; i < t.FaceTextures.Length; i++) if (t.FaceTextures[i] != null) n = i + 1; return Math.Max(1, n); } catch { return 1; } }

    // touch one face of a HUD prim at a texture (ST) coordinate: for HUDs whose swatches are faces or areas of one face
    static void GrabAt(Primitive p, int face, float s, float t)
    {
        var st = new Vector3(s, t, 0);
        client.Self.Grab(p.LocalID, Vector3.Zero, st, st, face, Vector3.Zero, Vector3.Zero, Vector3.Zero);
        client.Self.DeGrab(p.LocalID, st, st, face, Vector3.Zero, Vector3.Zero, Vector3.Zero);
    }

    static async Task<(bool ok, string how)> HudFallbackPress(InventoryItem hud, List<Primitive> prims, List<UUID> clothingItems, CancellationToken ct)
    {
        async Task<int> Changed(Dictionary<uint, string> before)
        {
            for (int i = 0; i < 12; i++) { await Task.Delay(500, ct); var after = SnapshotTextures(clothingItems); int n = TexIdsChanged(before, after).Count; if (n > 0) return n; }
            return 0;
        }
        var notes = new List<string>();
        // 1) touchable child prims with non-control names
        var cands = prims.Skip(prims.Count > 1 ? 1 : 0).Where(p => !HudControlRx.IsMatch((p.Properties?.Name ?? "") + " " + (p.Properties?.Description ?? ""))).ToList();
        foreach (var p in cands.OrderBy(_ => Random.Shared.Next()).Take(4))
        {
            var before = SnapshotTextures(clothingItems);
            int face = Random.Shared.Next(HudFaceCount(p));
            GrabAt(p, face, 0.5f, 0.5f);
            int n = await Changed(before);
            var lbl = $"prim '{p.Properties?.Name}' face {face}";
            Log("hud", $"'{hud.Name}' fallback press {lbl}: {(n > 0 ? $"applied, {n} prims changed" : "no change")}");
            if (n > 0) return (true, $"picked {lbl} -> applied ({n} prim(s) changed texture)");
            notes.Add(lbl + " no change");
        }
        // 2) random face + spot on the biggest prims (swatch grids drawn on one face)
        var big = prims.OrderByDescending(p => p.Scale.Y * p.Scale.Z).Take(2).ToList();
        for (int k = 0; k < 6 && big.Count > 0; k++)
        {
            var p = big[k % big.Count]; int face = Random.Shared.Next(HudFaceCount(p));
            float s = 0.1f + (float)Random.Shared.NextDouble() * 0.8f, t = 0.1f + (float)Random.Shared.NextDouble() * 0.8f;
            var before = SnapshotTextures(clothingItems);
            GrabAt(p, face, s, t);
            int n = await Changed(before);
            var lbl = $"prim '{p.Properties?.Name}' face {face} at st {s:F2},{t:F2}";
            Log("hud", $"'{hud.Name}' fallback press {lbl}: {(n > 0 ? $"applied, {n} prims changed" : "no change")}");
            if (n > 0) return (true, $"picked {lbl} -> applied ({n} prim(s) changed texture)");
            notes.Add(lbl + " no change");
        }
        return (false, "no named buttons; fallback touches gave no visible change (" + string.Join(", ", notes.Take(4)) + (notes.Count > 4 ? ", ..." : "") + ")");
    }

    static async Task<List<(InventoryItem hud, List<UUID> clothing, ClothingHudSpec spec)>> FindClothingHuds(IEnumerable<InventoryItem> outfitItems, CancellationToken ct)
    {
        var res = new List<(InventoryItem, List<UUID>, ClothingHudSpec)>();
        var items = outfitItems.ToList();
        var ids = items.Select(i => i.UUID).ToHashSet(); var mapped = new HashSet<UUID>();
        var (press, skip) = PartitionHudSpecs(LoadClothingHudSpecs(), ids);
        foreach (var sp in skip) foreach (var c in sp.Clothing.Where(ids.Contains)) mapped.Add(c); // single color: no HUD step at all
        foreach (var spec in press) // one HUD once, with every outfit piece it colors (Bikini top + panties)
        {
            var cloth = spec.Clothing.Where(ids.Contains).ToList();
            if (cloth.Count == 0) continue;
            var hud = await FetchItemRO(spec.Hud, ct);
            if (hud == null || hud.IsLink()) { Log("hud", $"mapped HUD {spec.Hud} '{spec.HudName}' not found in inventory"); continue; }
            foreach (var c in cloth) mapped.Add(c);
            res.Add((hud, cloth, spec));
        }
        var byFolder = items.Where(i => !mapped.Contains(i.UUID) && (i is InventoryObject || i is InventoryAttachment) && ClothingNameRx.IsMatch(i.Name ?? "")
                                         && OutfitGroup(i.Name) == null && !ClothingHudNameRx.IsMatch(i.Name ?? ""))
                                  .GroupBy(i => i.ParentUUID);
        foreach (var g in byFolder)
        {
            var kids = (await ReadFolderRO(g.Key, ct)).OfType<InventoryItem>().Where(k => k.ParentUUID == g.Key && !k.IsLink()).ToList();
            var huds = kids.Where(k => (k is InventoryObject || k is InventoryAttachment) && ClothingHudNameRx.IsMatch(k.Name ?? "")
                                      && !(k.Name ?? "").Contains(AoNameMatch, StringComparison.OrdinalIgnoreCase)
                                      && !res.Any(r => r.Item1.UUID == k.UUID)).ToList();
            foreach (var h in huds.Take(1)) res.Add((h, g.Select(i => i.UUID).ToList(), null));
        }
        return res;
    }

    static async Task<string> OutfitClothingHuds(InventoryFolder folder, CancellationToken ct, bool keepColor = false)
    {
        var items = await ResolveOutfitItems(folder, ct);
        var huds = await FindClothingHuds(items, ct);
        var sb = new StringBuilder();
        // HUDs that match another piece's color (the shorts) go last, after the tops have picked
        var done = new List<(UUID hud, string label)>();
        // single-color pieces (Beth tube top): HUD skipped; their remembered finish still steers the shorts color match
        foreach (var sp in PartitionHudSpecs(LoadClothingHudSpecs(), items.Select(i => i.UUID).ToHashSet()).skip)
        { done.Add((sp.Hud, HudLastPick(sp.Hud))); sb.AppendLine($"  '{sp.HudName}': single color, HUD step skipped"); }
        if (huds.Count == 0) return sb.Length > 0 ? sb.ToString().TrimEnd() : $"no clothing HUDs found for '{folder.Name}'";
        var wornNow = keepColor ? WornPrims().Select(AttachItemId).Where(u => u != UUID.Zero).ToHashSet() : null;
        foreach (var (hud, clothing, spec) in huds.OrderBy(h => h.spec?.Matches == true ? 1 : 0))
        {
            // same outfit re-dressed: the very same items still carry the remembered colour, so no HUD at all (RedressFast.cs)
            var skipWhy = RedressHudSkipReason(keepColor, HudLastPick(hud.UUID), HudLastItems(hud.UUID), clothing, wornNow);
            if (skipWhy != null)
            {
                sb.AppendLine($"  '{hud.Name}': HUD step skipped ({skipWhy})");
                Log("hud", $"'{hud.Name}' re-dress: HUD step skipped ({skipWhy})");
                done.Add((hud.UUID, HudLastPick(hud.UUID)));
                continue;
            }
            sb.AppendLine("  " + await HudRandomize(hud, clothing, ct, spec, keepColor, done));
            done.Add((hud.UUID, HudLastPick(hud.UUID)));
        }
        return sb.ToString().TrimEnd();
    }

    // wear + clothing HUD randomize (non-beach outfits and the Bikini alike)
    // keepColor: re-dressing the SAME outfit (after the tub / beach, relog restore) re-applies the remembered HUD color;
    // only a real change into a different outfit (daily pick, 'outfit wear') randomizes.
    static async Task<string> WearOutfitWithHuds(string name, bool keepColor = false)
    {
        if (!LoggedIn) return "not logged in";
        using var cts = new CancellationTokenSource(180000); var ct = cts.Token;
        var folder = await FindOutfitFolder(name, ct);
        if (folder == null) return $"no outfit '{name}' under My Outfits";
        var r = await OutfitWearSafe(folder, ct);
        await Task.Delay(2000, ct);
        var h = await OutfitClothingHuds(folder, ct, keepColor);
        return r + "\n" + h;
    }

    // outfit link-remove <outfit> <name part>  (link -> Trash; the item itself untouched)  | outfit link-add <outfit> <original item uuid>
    static async Task<string> OutfitLinkEdit(string[] a)
    {
        using var cts = new CancellationTokenSource(60000); var ct = cts.Token;
        if (a.Length < 3) return "usage: outfit link-remove <outfit> <name part> | outfit link-add <outfit> <original item uuid>";
        // multi-word outfit name: outfit link-remove <outfit words> | <name part>
        var bar = Array.IndexOf(a, "|");
        if (bar > 1 && bar < a.Length - 1) a = new[] { a[0], string.Join(' ', a[1..bar]) }.Concat(a[(bar + 1)..]).ToArray();
        var folder = await FindOutfitFolder(a[1], ct); if (folder == null) return $"no outfit '{a[1]}'";
        var kids = (await ReadFolderRO(folder.UUID, ct)).OfType<InventoryItem>().Where(k => k.ParentUUID == folder.UUID).ToList();
        if (a[0] == "link-remove")
        {
            var part = string.Join(' ', a[2..]);
            var hits = kids.Where(k => k.IsLink() && (k.Name ?? "").Contains(part, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count != 1) return $"'{part}' matches {hits.Count} links in '{folder.Name}': {string.Join(", ", hits.Select(h => h.Name))}";
            var trash = client.Inventory.FindFolderForType(FolderType.Trash);
            client.Inventory.MoveItem(hits[0].UUID, trash, hits[0].Name);
            Log("outfit", $"outfit '{folder.Name}': link '{hits[0].Name}' {hits[0].UUID} -> Trash");
            return $"moved link '{hits[0].Name}' ({hits[0].UUID}) from '{folder.Name}' to Trash (item {hits[0].AssetUUID} untouched)";
        }
        if (a[0] == "link-add")
        {
            if (!UUID.TryParse(a[2], out var id)) return "need an item uuid";
            var it = await FetchItemRO(id, ct); if (it == null || it.IsLink()) return "item not found (or a link)";
            if (kids.Any(k => k.AssetUUID == id)) return $"'{folder.Name}' already links '{it.Name}'";
            var made = await client.Inventory.CreateLinkAsync(folder.UUID, it.UUID, it.Name, "", it.InventoryType, UUID.Random(), ct);
            Log("outfit", $"outfit '{folder.Name}': link added '{it.Name}' {it.UUID}");
            return made != null ? $"linked '{it.Name}' into '{folder.Name}'" : "link failed";
        }
        return "usage: outfit link-remove|link-add ...";
    }

    internal static string OutfitSafeSelfTest()
    {
        var sb = new StringBuilder(); int pass = 0, fail = 0;
        void C(bool ok, string w) { if (ok) pass++; else fail++; sb.AppendLine($"{(ok ? "PASS" : "FAIL")} {w}"); }
        UUID U(int n) => new UUID($"00000000-0000-0000-0000-{n:D12}");
        var ao = U(99);
        var worn = new List<WornAtt>
        {
            new(U(1), "DOUX - Oasis Hairstyle [M/Long]", false), new(U(2), "/ HEAD / lel evox / AVALON 4.0", false),
            new(U(3), "TETRA - Spicy Bikini Top - LaraX Petite", false), new(U(4), "Maitreya Mesh Body - LaraX V1.1", false),
            new(U(5), ".Heol Star Earrings Gold", false), new(ao, "VISTA ANIMATIONS *HUD 6.3*MARTHA STS BENTO AO-V1.7", true),
        };
        // tube top outfit with its own (brown) hair: old hair must come off, new goes on, AO untouched
        var t1 = new List<TargetObj> { new(U(10), "DOUX - Yadira Hairstyle [M/BRUNETTE]"), new(U(2), "/ HEAD / lel evox / AVALON 4.0"),
                                       new(U(11), "Beth Top :: PetiteX - White :: Pink Cream Pie"), new(U(4), "Maitreya Mesh Body - LaraX V1.1") };
        var p1 = PlanAttachmentSwap(worn, t1, new HashSet<UUID> { ao });
        C(p1.detach.Contains(U(1)), "old hair off when outfit has a hair");
        C(p1.attach.Contains(U(10)), "outfit hair on");
        C(p1.detach.Contains(U(3)) && p1.attach.Contains(U(11)), "bikini top off, tube top on");
        C(!p1.detach.Contains(ao), "AO never detached");
        C(!p1.detach.Contains(U(2)) && !p1.attach.Contains(U(2)), "same head kept as is");
        // outfit without hair: keep current hair
        var hm = ParseClothingHudMap("{\"tops\":[{\"clothing\":\"" + U(11) + "\",\"hud\":\"" + U(12) + "\"},{\"clothing\":\"" + U(13) + "\",\"hud\":\"" + AoItem + "\"}]}");
        C(hm.Count == 1 && hm[U(11)] == U(12), "clothing HUD map parsed; an AO HUD entry is refused");
        C(HudControlRx.IsMatch("DETACH") && HudControlRx.IsMatch("Shine OFF") && !HudControlRx.IsMatch("Pink") && !HudControlRx.IsMatch("color 3"), "HUD fallback skips control buttons, keeps swatches");
        C(ClothingHudNameRx.IsMatch("[ARTi'S] Strapless Top - HUD") && ClothingHudNameRx.IsMatch("<HUD> Chill T-Shirt") && !ClothingHudNameRx.IsMatch("VISTA ANIMATIONS *HUD 6.3*MARTHA STS BENTO AO-V1.7"), "clothing HUD names (ARTi'S '- HUD' suffix); AO is not one");
        var t2 = new List<TargetObj> { new(U(11), "Beth Top :: PetiteX"), new(U(2), "/ HEAD / lel evox / AVALON 4.0") };
        var p2 = PlanAttachmentSwap(worn, t2, new HashSet<UUID> { ao });
        C(!p2.detach.Contains(U(1)), "no hair in outfit -> current hair stays");
        C(!p2.detach.Contains(U(4)), "no body in outfit -> current body stays");
        // two hairs worn already, outfit has none -> one comes off
        var worn3 = worn.Append(new WornAtt(U(12), "DOUX - Yadira Hairstyle [M/BRUNETTE]", false)).ToList();
        var p3 = PlanAttachmentSwap(worn3, t2, new HashSet<UUID> { ao });
        C(p3.detach.Count(d => d == U(1) || d == U(12)) == 1, "two hairs worn -> exactly one comes off");
        // outfit lists two hairs -> only the first attaches
        var t4 = new List<TargetObj> { new(U(1), "DOUX - Oasis Hairstyle"), new(U(10), "DOUX - Yadira Hairstyle") };
        var p4 = PlanAttachmentSwap(worn, t4, new HashSet<UUID> { ao });
        C(!p4.attach.Contains(U(10)), "second hair in outfit skipped");
        C(PlanAttachmentSwap(worn, new List<TargetObj> { new(RetiredAwpAo, "AWP female animation controller v2") }, new HashSet<UUID> { ao, RetiredAwpAo }).attach.Count == 0, "retired AWP never attached");
        // HUD options
        var hudA = new List<(int, uint, string, string)> { (1, 1, "<HUD> Chill T-Shirt", ""), (2, 2, "[ALPHA]", "CT,1,2"), (3, 3, "[TEXTURE]", "C3"), (4, 4, "[TEXTURE]", "P12"), (5, 5, "[DETACH]", ""), (6, 6, "[GOTOFACE]", "1") };
        var oA = HudTextureOptions(hudA);
        C(oA.Count == 2 && oA.Any(o => o.label == "C3") && oA.Any(o => o.label == "P12"), "TETRA colors + patterns only");
        var hudB = new List<(int, uint, string, string)> { (1, 1, "[HUD - Essential] Beth Tube Top", ""), (2, 2, "tx", "1m"), (3, 3, "tx", "5m"), (4, 4, "website", "https://x") };
        C(HudTextureOptions(hudB).Count == 2, "Pink Cream Pie tx swatches");
        C(OutfitGroup("DOUX - Yadira Hairstyle [M/BRUNETTE]") == "hair" && OutfitGroup("/ HEAD / lel evox / CEYLON 4.0") == "head" && OutfitGroup("Maitreya Mesh Body - LaraX Petite Add-on V1.1") == null, "groups");
        C(ClothingHudNameRx.IsMatch("<HUD> Spicy Bikini") && ClothingHudNameRx.IsMatch("[HUD - Essential] Beth Tube Top :: Pink Cream Pie") && !ClothingHudNameRx.IsMatch("VISTA ANIMATIONS *HUD 6.3*MARTHA STS BENTO AO-V1.7"), "clothing HUD names, AO excluded");
        // PR #63 follow-up: the shipped top -> HUD map (routes/_clothing-huds.json) maps each of the 3 tops to its own HUD
        var rd = FindRoutesDirForTest();
        if (rd != null && File.Exists(Path.Combine(rd, "_clothing-huds.json")))
        {
            var real = ParseClothingHudMap(File.ReadAllText(Path.Combine(rd, "_clothing-huds.json")));
            C(real.Count == 6 && real.Values.Distinct().Count() == 5 && !real.Values.Contains(AoItem), $"_clothing-huds.json: 3 tops + bikini top/panties + Chill Shorts -> 5 distinct HUDs, no AO ({real.Count})");
            C(!real.ContainsKey(new UUID("9992295d-5f4b-33fc-addf-b774396caa50")) && !real.Values.Contains(new UUID("a612303a-f115-3f56-8cc9-c5936180f1ff")), "Valentine Dress trashed (2026-10-07): no Valentine HUD in the map");
            C(real.TryGetValue(new UUID("5c8487b7-0db2-34fd-a81b-fe2709c98021"), out var beth) && beth == new UUID("6899891e-79d1-3a31-93ab-fac14a3bd75e"), "Beth tube top -> Beth Tube Top HUD");
            C(real.TryGetValue(new UUID("aa265665-7a17-34f6-b423-66d336262ed2"), out var arts) && arts == new UUID("cb0dc6c4-545c-3be6-8351-e049094315a7"), "ARTi'S strapless top -> ARTi'S HUD");
            C(real.TryGetValue(new UUID("90d3e432-c8d1-3f2c-b800-6e88ed678c27"), out var tee) && tee == new UUID("a2591928-d005-3af2-9b02-a04c9e5f93e7"), "TETRA Chill T-Shirt -> Chill T-Shirt HUD");
        }
        else C(false, "routes/_clothing-huds.json not found for the map test");
        C(ParseClothingHudMap("{\"note\":\"x\"}").Count == 0 && ParseClothingHudMap("{\"tops\":[{\"clothing\":\"not-a-uuid\",\"hud\":\"" + U(12) + "\"}]}").Count == 0, "map without tops / with a bad uuid -> empty, no crash");
        // stray system Alpha layers (2026-10-07: Bimbette alphas left on under the Valentine Dress)
        var a1 = U(31); var a2 = U(32); var a3 = U(33); var teeX = U(40); var jeans = U(41); var dress = U(42); var skin = U(43);
        C(LayersNotInOutfit(new[] { a1, a2, a3, U(50) }, new HashSet<UUID> { U(50) }).Count == 3, "outfit wear: alphas not in the new outfit come off");
        C(LayersNotInOutfit(new[] { a1, U(50) }, new HashSet<UUID> { a1, U(50) }).Count == 0, "outfit wear: alphas in the new outfit stay");
        var wl = new List<(UUID, WearableType)> { (a1, WearableType.Alpha), (a2, WearableType.Alpha), (a3, WearableType.Alpha), (skin, WearableType.Tattoo), (U(34), WearableType.Alpha) };
        var chill = new HashSet<UUID> { a1, a2, a3, teeX, jeans, skin };
        var chillAtts = new HashSet<UUID> { teeX, jeans };
        C(OrphanAlphas(wl, chill, chillAtts, new HashSet<UUID> { jeans }).Count == 0, "ad hoc: tee off, jeans still on -> alphas stay");
        var oa = OrphanAlphas(wl, chill, chillAtts, new HashSet<UUID> { dress });
        C(oa.Count == 3 && !oa.Contains(skin) && !oa.Contains(U(34)), "ad hoc: jeans off too -> only that outfit's 3 alphas off (tattoo + foreign alpha kept)");
        // clothed alphas: kept/added for clothed outfits, shed for the Bikini (= show all)
        var ca = new[] { U(60), U(61) };
        var eff = EffectiveOutfitLayers(new[] { U(50) }, ca, IsClothedOutfit("Valentine Dress"));
        C(eff.SetEquals(new[] { U(50), U(60), U(61) }), "clothed outfit: clothed alphas added to the outfit's layers");
        C(LayersNotInOutfit(new[] { U(60), U(61), a1 }, eff).SequenceEqual(new[] { a1 }), "clothed outfit: clothed alphas never removed, stray alpha is");
        var effB = EffectiveOutfitLayers(new[] { U(50) }, ca, IsClothedOutfit("Bikini"));
        C(!IsClothedOutfit("Bikini") && !IsClothedOutfit("spicy") && LayersNotInOutfit(new[] { U(60), U(61) }, effB).Count == 2, "Bikini: clothed alphas come off (whole body shows)");
        C(!IsClothedOutfit("Naked") && !IsClothedOutfit(" nude ") && IsClothedOutfit("Naked Ambition Dress"), "Naked outfit is not clothed (no clothed alphas)");
        var vHud = U(70); var vBody = U(71);
        var wornV = new List<WornAtt> { new(vHud, "The V - Bento Play HUD", true), new(vBody, "The V - Bento", false), new(AoItem, "AO", true) };
        C(ExtraHudsOff(wornV, new HashSet<UUID> { U(50) }, new[] { vHud, vBody }).SequenceEqual(new[] { vHud }), "dressing: the undress-extra HUD comes off, other HUDs stay");
        C(ExtraHudsOff(wornV, new HashSet<UUID> { vHud, vBody }, new[] { vHud, vBody }).Count == 0, "Naked outfit lists the HUD: it stays");
        if (rd != null && File.Exists(Path.Combine(rd, "_clothed-alphas.txt")))
        {
            var cl = ParseToplessExtras(File.ReadAllText(Path.Combine(rd, "_clothed-alphas.txt")));
            C(cl.Count == 0, $"_clothed-alphas.txt disabled (2026-10-07 body alphas reset): no entries ({cl.Count})");
        }
        else C(false, "routes/_clothed-alphas.txt not found");
        // 'outfit create "Valentine Dress"' made a folder named '"Valentine' (10:26): quotes + multi-word names
        var pc1 = ParseOutfitCreateArgs(new[] { "create", "\"Valentine", "Dress\"" });
        C(pc1.name == "Valentine Dress" && pc1.extra.Length == 0, $"outfit create: quoted multi-word name ('{pc1.name}')");
        var pc2 = ParseOutfitCreateArgs(new[] { "create", "Beach", "Day", U(7) + "," + U(8) });
        C(pc2.name == "Beach Day" && pc2.extra.Length == 2, "outfit create: trailing comma-separated uuid list = extra ids");
        C(ParseOutfitCreateArgs(new[] { "plan", "Chill" }).name == "Chill", "outfit plan: single word unchanged");
        return $"outfit-safe selftest: {pass} PASS, {fail} FAIL\n" + sb.ToString().TrimEnd();
    }
}
