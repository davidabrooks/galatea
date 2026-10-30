using LibreMetaverse;
using LibreMetaverse.Assets;
using GalatayText;
using Xunit;

public class LoginVeilCreateTests
{
    [Fact]
    public void VeilAssetMasksBodyOnly()
    {
        var a = LoginVeilAsset.Build(UUID.Random());
        var d = new AssetClothing(UUID.Random(), a.AssetData);
        Assert.True(d.Decode());
        Assert.Equal(WearableType.Alpha, d.WearableType);
        Assert.Equal(LoginVeilAsset.Invisible, d.Textures[AvatarTextureIndex.UpperAlpha]);
        Assert.Equal(LoginVeilAsset.Invisible, d.Textures[AvatarTextureIndex.LowerAlpha]);
        foreach (var i in new[] { AvatarTextureIndex.HeadAlpha, AvatarTextureIndex.EyesAlpha, AvatarTextureIndex.HairAlpha })
            Assert.NotEqual(LoginVeilAsset.Invisible, d.Textures[i]);
        Assert.Equal(5, d.Textures.Count); // alpha layers only, no tattoo
    }
}
