using LibreMetaverse;
using LibreMetaverse.Assets;
using GalatayText;
using Xunit;

public class LoginVeilCreateTests
{
    [Fact]
    public void VeilAssetMasksTheWholeAvatar()
    {
        var a = LoginVeilAsset.Build(UUID.Random());
        var d = new AssetClothing(UUID.Random(), a.AssetData);
        Assert.True(d.Decode());
        Assert.Equal(WearableType.Alpha, d.WearableType);
        Assert.Equal(LoginVeilAsset.Invisible, d.Textures[AvatarTextureIndex.UpperAlpha]);
        Assert.Equal(LoginVeilAsset.Invisible, d.Textures[AvatarTextureIndex.LowerAlpha]);
        foreach (var i in new[] { AvatarTextureIndex.HeadAlpha, AvatarTextureIndex.EyesAlpha, AvatarTextureIndex.HairAlpha })
            Assert.Equal(LoginVeilAsset.Invisible, d.Textures[i]); // 11:51: body-only veil left a floating head
        Assert.Equal(5, d.Textures.Count); // alpha layers only, no tattoo
    }

    [Fact]
    public void OldBodyOnlyVeilIsMigrated()
    {
        Assert.True(LoginVeilAsset.NeedsMigration("Galatea login veil (body alpha)"));
        Assert.False(LoginVeilAsset.NeedsMigration(LoginVeilAsset.Name));
        Assert.False(LoginVeilAsset.NeedsMigration(null));
    }
}
