// LoginVeilCreate.cs (2026-10-30): 'veil create' makes the body-only system alpha the login veil (LoginVeil.cs) needs, the way a viewer's
// "New Alpha" + save does: upload the wearable asset over UDP (AssetUploadRequest, free for wearables) and CreateInventoryItem with that
// transaction. Upper + lower body masked with IMG_INVISIBLE, head/eyes/hair left at the default (unmasked). Not worn. Writes the new item id to
// routes/_login-veil.txt. Refuses if that file already names an item (use 'veil create force').
using LibreMetaverse;
using LibreMetaverse.Assets;

namespace GalatayText;

internal static class LoginVeilAsset
{
    internal static readonly UUID Invisible = new("3a367d1c-bef1-6d43-7595-e88c1e3aadb3");
    internal static readonly UUID DefaultAlpha = new("5748decc-f629-461c-9a36-a35a221fe21f");
    internal const string Name = "Galatea login veil (body alpha)";

    /// <summary>Pure (tested): the veil's alpha wearable asset.</summary>
    internal static AssetClothing Build(UUID creator)
    {
        var a = new AssetClothing { Name = Name, Description = "Body-only alpha worn at logout so login starts with the body hidden", WearableType = WearableType.Alpha, Creator = creator, Owner = creator };
        a.Permissions = new Permissions((uint)PermissionMask.All, 0, 0, (uint)PermissionMask.All, (uint)PermissionMask.All);
        a.Textures[AvatarTextureIndex.UpperAlpha] = Invisible;
        a.Textures[AvatarTextureIndex.LowerAlpha] = Invisible;
        a.Textures[AvatarTextureIndex.HeadAlpha] = DefaultAlpha;
        a.Textures[AvatarTextureIndex.EyesAlpha] = DefaultAlpha;
        a.Textures[AvatarTextureIndex.HairAlpha] = DefaultAlpha;
        a.Encode();
        return a;
    }
}

public static partial class Program
{
    static async Task<string> LoginVeilCreateCmd(string[] a)
    {
        if (!LoggedIn) return "not logged in";
        bool force = a.Contains("force");
        var have = LoginVeilItem();
        if (have != UUID.Zero && !force) return $"veil already set: {have} (routes/_login-veil.txt); 'veil create force' to make another";
        var folder = client.Inventory.FindFolderForType(FolderType.Clothing);
        if (folder == UUID.Zero) return "Clothing folder not found";
        var asset = LoginVeilAsset.Build(client.Self.AgentID);
        var tx = UUID.Random();
        try
        {
            using var cts = new CancellationTokenSource(30000);
            await client.Assets.RequestUploadAsync(AssetType.Clothing, asset.AssetData, false, tx, cts.Token);
            var item = await client.Inventory.CreateItemAsync(folder, LoginVeilAsset.Name, asset.Description, AssetType.Clothing, tx,
                InventoryType.Wearable, WearableType.Alpha, PermissionMask.All, cts.Token);
            if (item == null) return "veil create: CreateInventoryItem failed";
            File.WriteAllText(Path.Combine(RouteDir, "_login-veil.txt"), item.UUID + "\n# " + LoginVeilAsset.Name + " (veil create " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ")\n");
            Log("veil", $"created {item.UUID} '{item.Name}' asset {item.AssetUUID} in Clothing; written to _login-veil.txt (not worn)");
            return $"veil created: item {item.UUID} asset {item.AssetUUID} in Clothing; written to routes/_login-veil.txt (not worn)";
        }
        catch (Exception ex) { return "veil create failed: " + ex.GetBaseException().Message; }
    }
}
