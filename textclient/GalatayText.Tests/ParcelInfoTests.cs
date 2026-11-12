using GalatayText;
using LibreMetaverse;
using Xunit;

// 2026-11-11 David: parcel_info flag decoding from ParcelProperties flags.
public class ParcelInfoTests
{
    [Fact]
    public void No_script_flags_decode_as_no_scripts()
    {
        var f = Program.DecodeParcelFlags(ParcelFlags.None);
        Assert.Equal("no-scripts", f.Scripts);
        Assert.False(f.Build);
        Assert.False(f.Fly);
        Assert.False(f.PushRestrict);
        Assert.False(f.Damage);
        Assert.False(f.Voice);
        Assert.False(f.AccessGroup);
        Assert.False(f.AccessList);
        Assert.False(f.BanList);
        Assert.False(f.AccessRestricted);
        Assert.False(f.MusicUrl);
        Assert.False(f.MediaUrl);
    }

    [Fact]
    public void Open_public_parcel_with_other_and_group_scripts()
    {
        var flags = ParcelFlags.AllowOtherScripts | ParcelFlags.AllowGroupScripts | ParcelFlags.CreateObjects
                    | ParcelFlags.AllowFly | ParcelFlags.AllowVoiceChat;
        var f = Program.DecodeParcelFlags(flags, "http://music.example/stream", null);
        Assert.Equal("other+group", f.Scripts);
        Assert.True(f.Build);
        Assert.True(f.Fly);
        Assert.False(f.PushRestrict);
        Assert.False(f.Damage);
        Assert.True(f.Voice);
        Assert.False(f.AccessRestricted);
        Assert.True(f.MusicUrl);
        Assert.False(f.MediaUrl);
    }

    [Fact]
    public void Group_scripts_only_and_access_list_is_restricted()
    {
        var flags = ParcelFlags.AllowGroupScripts | ParcelFlags.UseAccessList | ParcelFlags.RestrictPushObject | ParcelFlags.AllowDamage;
        var f = Program.DecodeParcelFlags(flags, null, "https://media.example/clip");
        Assert.Equal("group", f.Scripts);
        Assert.True(f.PushRestrict);
        Assert.True(f.Damage);
        Assert.True(f.AccessList);
        Assert.False(f.AccessGroup);
        Assert.True(f.AccessRestricted);
        Assert.False(f.MusicUrl);
        Assert.True(f.MediaUrl);
    }

    [Fact]
    public void Other_scripts_only_with_access_group_and_ban_list()
    {
        var flags = ParcelFlags.AllowOtherScripts | ParcelFlags.UseAccessGroup | ParcelFlags.UseBanList;
        var f = Program.DecodeParcelFlags(flags);
        Assert.Equal("other", f.Scripts);
        Assert.True(f.AccessGroup);
        Assert.True(f.BanList);
        Assert.True(f.AccessRestricted);
        var line = Program.ParcelInfoLine("Welcome Area", "3d6181b0-6a4b-97ef-18d8-7227138161bb", 65536, 1, f);
        Assert.Contains("Welcome Area", line);
        Assert.Contains("scripts=other", line);
        Assert.Contains("access=group", line);
        Assert.Contains("ban-list", line);
    }
}
