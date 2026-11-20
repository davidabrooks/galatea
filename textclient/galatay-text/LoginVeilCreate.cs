// LoginVeilCreate.cs (2026-10-30): 'veil create' makes the body-only system alpha the login veil (LoginVeil.cs) needs, the way a viewer's
// "New Alpha" + save does: upload the wearable asset over UDP (AssetUploadRequest, free for wearables) and CreateInventoryItem with that
// transaction. Upper, lower, head, eyes and hair all masked with IMG_INVISIBLE (11:51 David: the body-only veil left a floating head). Not worn. Writes the new item id to
// routes/_login-veil.txt. Refuses if that file already names an item (use 'veil create force').
using LibreMetaverse;
using LibreMetaverse.Assets;

namespace GalatayText;

internal static class LoginVeilAsset
{
    internal static readonly UUID Invisible = new("3a367d1c-bef1-6d43-7595-e88c1e3aadb3");
    internal static readonly UUID DefaultAlpha = new("5748decc-f629-461c-9a36-a35a221fe21f");
    internal const string Name = "Galatea login veil (full avatar alpha)";
    /// <summary>2026-11-20 (SwapVeil.cs): the body-only veil for android -> human outfit changes; head, eyes and hair stay visible.</summary>
    internal const string SwapName = "Galatea swap veil (body alpha)";

    /// <summary>Pure (tested): an existing veil item with another name is an older (body-only) build and gets recreated.</summary>
    internal static bool NeedsMigration(string existingName) => existingName != null && existingName != Name;

    /// <summary>Pure (tested): the veil's alpha wearable asset.</summary>
    internal static AssetClothing Build(UUID creator)
    {
        var a = new AssetClothing { Name = Name, Description = "Full-avatar alpha (body, head, eyes, hair) worn at logout so login starts with the whole avatar hidden", WearableType = WearableType.Alpha, Creator = creator, Owner = creator };
        a.Permissions = new Permissions((uint)PermissionMask.All, 0, 0, (uint)PermissionMask.All, (uint)PermissionMask.All);
        a.Textures[AvatarTextureIndex.UpperAlpha] = Invisible;
        a.Textures[AvatarTextureIndex.LowerAlpha] = Invisible;
        a.Textures[AvatarTextureIndex.HeadAlpha] = Invisible;
        a.Textures[AvatarTextureIndex.EyesAlpha] = Invisible;
        a.Textures[AvatarTextureIndex.HairAlpha] = Invisible;
        a.Encode();
        return a;
    }

    /// <summary>Pure (tested): the swap veil's alpha wearable: upper and lower body only (mid-session nothing is stashed, so a masked head
    /// would leave eyes, teeth, hair and lashes floating).</summary>
    internal static AssetClothing BuildBody(UUID creator)
    {
        var a = new AssetClothing { Name = SwapName, Description = "Body-only alpha (upper + lower body) worn during an android -> human outfit change", WearableType = WearableType.Alpha, Creator = creator, Owner = creator };
        a.Permissions = new Permissions((uint)PermissionMask.All, 0, 0, (uint)PermissionMask.All, (uint)PermissionMask.All);
        a.Textures[AvatarTextureIndex.UpperAlpha] = Invisible;
        a.Textures[AvatarTextureIndex.LowerAlpha] = Invisible;
        a.Encode();
        return a;
    }
}

public static partial class Program
{
    static async Task<string> LoginVeilCreateCmd(string[] a)
    {
        if (!LoggedIn) return "not logged in";
        if (a.Contains("swap")) return await SwapVeilCreateCmd(a.Contains("force"));
        bool force = a.Contains("force");
        var have = LoginVeilItem();
        // Migration: a veil from the old body-only build (any other name) is replaced without 'force'.
        if (have != UUID.Zero && !force)
        {
            InventoryItem old = null;
            try { using var c0 = new CancellationTokenSource(15000); old = await FetchItemRO(have, c0.Token); } catch { }
            if (old != null && LoginVeilAsset.NeedsMigration(old.Name)) { force = true; Log("veil", $"migrating old veil {have} '{old.Name}' to the full-avatar veil"); }
        }
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

    /// <summary>'veil create swap [force]': the body-only swap veil (SwapVeil.cs), written to routes/_swap-veil.txt. Not worn.</summary>
    static async Task<string> SwapVeilCreateCmd(bool force)
    {
        var file = Path.Combine(RouteDir, "_swap-veil.txt");
        if (File.Exists(file) && !force) return $"swap veil already set: {SwapVeilItem()} (routes/_swap-veil.txt); 'veil create swap force' to make another";
        var folder = client.Inventory.FindFolderForType(FolderType.Clothing);
        if (folder == UUID.Zero) return "Clothing folder not found";
        var asset = LoginVeilAsset.BuildBody(client.Self.AgentID);
        try
        {
            using var cts = new CancellationTokenSource(30000);
            var tx = UUID.Random();
            await client.Assets.RequestUploadAsync(AssetType.Clothing, asset.AssetData, false, tx, cts.Token);
            var item = await client.Inventory.CreateItemAsync(folder, LoginVeilAsset.SwapName, asset.Description, AssetType.Clothing, tx,
                InventoryType.Wearable, WearableType.Alpha, PermissionMask.All, cts.Token);
            if (item == null) return "swap veil create: CreateInventoryItem failed";
            File.WriteAllText(file, item.UUID + "\n# " + LoginVeilAsset.SwapName + " (veil create swap " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ")\n");
            Log("veil", $"created swap veil {item.UUID} '{item.Name}' asset {item.AssetUUID} in Clothing; written to _swap-veil.txt (not worn)");
            return $"swap veil created: item {item.UUID} asset {item.AssetUUID} in Clothing; written to routes/_swap-veil.txt (not worn)";
        }
        catch (Exception ex) { return "swap veil create failed: " + ex.GetBaseException().Message; }
    }
}
