// SceneExport.cs (2026-10-03, David: real shapes + her real face/body for the agent-vision test renders)
//   scene export [radius=48]   READ-ONLY: writes /workspace/secondlife/vision/export-<time>/scene.json with every prim
//                              (roots + children) whose root is within radius, her own attachments, nearby avatar
//                              positions, and her server-side bakes as raw .j2c (fetched from the appearance service).
// Prims are LibreMetaverse's Primitive.GetOSD() plus world_pos/world_rot. Geometry is built offline by
// vision/scene-mesher (keeps meshing out of this process). Never touches, sends or edits anything.
using System.Globalization;
using LibreMetaverse;
using LibreMetaverse.StructuredData;

namespace GalatayText;

public static partial class Program
{
    // bake name in the appearance-service URL, by AvatarTextureIndex (names as in the LL viewer's appearance dictionary)
    static readonly (int te, string name)[] Bakes = { (8, "head"), (9, "upper"), (10, "lower"), (11, "eyes"), (19, "skirt"), (20, "hair"),
                                                       (40, "leftarm"), (41, "leftleg"), (42, "aux1"), (43, "aux2"), (44, "aux3") };

    static async Task<string> SceneExport(string[] a)
    {
        float r = 48;
        if (a.Length > 1 && (!float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out r) || r <= 0 || r > 128))
            return "usage: scene export [radius 1-128]";
        var sim = Sim; var me = client.Self;
        var myPos = me.SimPosition;
        var prims = sim.ObjectsPrimitives.Values.Where(p => p != null).ToList();
        var byLocal = prims.ToDictionary(p => p.LocalID);
        Primitive Root(Primitive p) { var q = p; for (int i = 0; i < 8 && q.ParentID != 0 && byLocal.TryGetValue(q.ParentID, out var up); i++) q = up; return q; }

        var outPrims = new OSDArray(); int mine = 0;
        foreach (var p in prims)
        {
            var root = Root(p); bool attachedToMe = root.ParentID == me.LocalID;
            if (root.ParentID != 0 && !attachedToMe) continue;            // other avatars' attachments: skipped (privacy, no skeleton yet)
            if (!attachedToMe && Vector3.Distance(root.Position, myPos) > r) continue;
            if (p.PrimData.PCode != PCode.Prim) continue;                 // trees/grass (PCode Tree/Grass) have no volume data
            var o = (OSDMap)p.GetOSD();
            if (attachedToMe) { o["attached_to_me"] = true; o["attach_point"] = (int)root.PrimData.AttachmentPoint; mine++; }
            else
            {
                var wp = p == root ? p.Position : root.Position + p.Position * root.Rotation;
                var wr = p == root ? p.Rotation : p.Rotation * root.Rotation;
                o["world_pos"] = OSD.FromVector3(wp); o["world_rot"] = OSD.FromQuaternion(wr);
            }
            outPrims.Add(o);
        }

        var dir = $"/workspace/secondlife/vision/export-{DateTime.Now:yyyyMMdd-HHmmss}";
        Directory.CreateDirectory(dir);
        var bakes = new OSDMap(); var bakeErr = new List<string>();
        sim.ObjectsAvatars.TryGetValue(me.LocalID, out var self);
        foreach (var (te, name) in Bakes)
        {
            var id = self?.Textures?.GetFace((uint)te)?.TextureID ?? UUID.Zero;
            if (id == UUID.Zero || id == Primitive.TextureEntry.WHITE_TEXTURE || id.ToString() == "3a367d1c-bef1-6d43-7595-e88c1e3aadb3") continue; // unset / default
            var tex = await client.Assets.RequestServerBakedImageAsync(me.AgentID, id, name);
            if (tex?.AssetData == null) { bakeErr.Add(name); continue; }
            File.WriteAllBytes(Path.Combine(dir, $"bake-{name}.j2c"), tex.AssetData);
            bakes[name] = OSD.FromUUID(id);
        }
        var avatars = new OSDArray();
        foreach (var av in sim.ObjectsAvatars.Values.Where(x => x != null && x.LocalID != me.LocalID))
        {
            var ap = PositionHelper.GetAvatarPosition(sim, av);
            if (Vector3.Distance(ap, myPos) <= r) avatars.Add(new OSDMap { ["pos"] = OSD.FromVector3(ap), ["rot"] = OSD.FromQuaternion(av.Rotation) });
        }
        var doc = new OSDMap
        {
            ["region"] = sim.Name, ["agent_id"] = OSD.FromUUID(me.AgentID), ["me"] = new OSDMap { ["pos"] = OSD.FromVector3(myPos), ["rot"] = OSD.FromQuaternion(me.SimRotation), ["sitting_on"] = (int)me.SittingOn },
            ["radius"] = r, ["prims"] = outPrims, ["bakes"] = bakes, ["avatars"] = avatars, ["exported_at"] = DateTime.Now.ToString("o"),
        };
        File.WriteAllText(Path.Combine(dir, "scene.json"), OSDParser.SerializeJsonString(doc));
        return $"exported {outPrims.Count} prims ({mine} on my attachments), {avatars.Count} avatar(s), bakes {string.Join(",", bakes.Keys)}" +
               (bakeErr.Count > 0 ? $" (failed: {string.Join(",", bakeErr)})" : "") + $"\n{dir}/scene.json";
    }
}
