// InvDump.cs (2026-10-19, David: study the N9 collar / NX Remote packages; never wear them).
// invdump <folder uuid> [outdir]   READ-ONLY: every item in the folder (recursive) with owner perms (M/C/T) and next-owner perms.
//   Scripts and notecards she may read (owner Modify for scripts; notecards need Copy+Modify) are saved as text into outdir
//   (default /workspace/secondlife/scripts/thirdparty/<folder name>/). No-mod scripts: listed as "source not readable".
using System.Text;
using LibreMetaverse;
using LibreMetaverse.Assets;

namespace GalatayText;

public static partial class Program
{
    static string PermStr(PermissionMask m) => $"{((m & PermissionMask.Modify) != 0 ? "M" : "-")}{((m & PermissionMask.Copy) != 0 ? "C" : "-")}{((m & PermissionMask.Transfer) != 0 ? "T" : "-")}";
    static string SafeName(string n) => string.Concat((n ?? "x").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ' ? c : '_')).Trim();

    static async Task<string> InvDumpCmd(string[] a)
    {
        if (a.Length < 1 || !UUID.TryParse(a[0], out var fid)) return "usage: invdump <folder uuid> [outdir]";
        using var cts = new CancellationTokenSource(240000); var ct = cts.Token;
        var root = await ReadFolderRO(fid, ct);
        string outdir = a.Length > 1 ? a[1] : null;
        var sb = new StringBuilder(); int saved = 0;
        async Task Walk(UUID f, List<InventoryBase> kids, string path)
        {
            foreach (var k in kids)
            {
                if (k is InventoryFolder sub) { await Walk(sub.UUID, await ReadFolderRO(sub.UUID, ct), path + "/" + sub.Name); continue; }
                if (k is not InventoryItem it) continue;
                var own = it.Permissions.OwnerMask; var next = it.Permissions.NextOwnerMask;
                var line = $"{path}/{it.Name}  [{it.AssetType}] owner {PermStr(own)} next {PermStr(next)} item {it.UUID}";
                bool readable = it.AssetType == AssetType.LSLText ? (own & PermissionMask.Modify) != 0
                              : it.AssetType == AssetType.Notecard ? (own & PermissionMask.Copy) != 0 : false; // a no-mod notecard still opens read-only
                if (it.AssetType is AssetType.LSLText or AssetType.Notecard)
                {
                    if (!readable) line += it.AssetType == AssetType.LSLText ? "  -> source not readable (no-mod)" : "  -> text not readable (perms)";
                    else
                    {
                        try
                        {
                            using var t2 = new CancellationTokenSource(30000);
                            var asset = await client.Assets.RequestInventoryAssetAsync(it, true, UUID.Random(), t2.Token);
                            string text = null;
                            if (asset is AssetScriptText st) { st.Decode(); text = st.Source; }
                            else if (asset is AssetNotecard nc) { nc.Decode(); text = nc.BodyText; }
                            else if (asset?.AssetData != null) text = Encoding.UTF8.GetString(asset.AssetData);
                            if (text == null) line += "  -> download returned nothing";
                            else
                            {
                                var dir = outdir ?? Path.Combine("/workspace/secondlife/scripts/thirdparty", SafeName(path.TrimStart('/').Split('/')[0]));
                                Directory.CreateDirectory(dir);
                                var file = Path.Combine(dir, SafeName(path.Replace('/', '_').Trim('_') + "_" + it.Name) + (it.AssetType == AssetType.LSLText ? ".lsl" : ".txt"));
                                File.WriteAllText(file, text); saved++;
                                line += $"  -> saved {text.Length} chars to {file}";
                            }
                        }
                        catch (Exception ex) { line += "  -> download failed: " + ex.GetBaseException().Message; }
                    }
                }
                sb.AppendLine(line);
            }
        }
        string name = client.Inventory.Store.Contains(fid) ? client.Inventory.Store[fid].Name : fid.ToString();
        await Walk(fid, root, "/" + name);
        try   // every URL in the saved notecards -> urls.txt next to them
        {
            var dir0 = outdir ?? Path.Combine("/workspace/secondlife/scripts/thirdparty", SafeName(name));
            if (Directory.Exists(dir0))
            {
                var urls = Directory.GetFiles(dir0, "*.txt").Where(f => !f.EndsWith("urls.txt")).SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"(?:https?|secondlife)://[^\s""'<>)\]]+").Select(m => m.Value)).Distinct().OrderBy(x => x).ToList();
                File.WriteAllLines(Path.Combine(dir0, "urls.txt"), urls); sb.AppendLine($"urls.txt: {urls.Count} URL(s)");
            }
        }
        catch { }
        var res = $"invdump '{name}': {saved} text file(s) saved (read-only; nothing worn or changed)\n" + sb.ToString().TrimEnd();
        Log("inv", res.Split('\n')[0]); return res;
    }
}
