using System.Collections.Generic;
using GalatayText;
using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Xunit;

namespace GalatayText.Tests;

/// <summary>2026-10-12 'material set' parsing + ModifyMaterialParams payload (pure).</summary>
public class MaterialSetTests
{
    static readonly UUID Chrome = new("adfa8a26-bc47-f5e6-1cd1-586eb1fead26");

    [Fact]
    public void Parses_faces_local_prefix_and_none()
    {
        var (a, err) = Program.ParseMaterialSetArgs("local:179968050 3,1,1 adfa8a26-bc47-f5e6-1cd1-586eb1fead26");
        Assert.Null(err); Assert.Equal(179968050u, a!.LocalId); Assert.Equal(new List<int> { 1, 3 }, a.Faces); Assert.Equal(Chrome, a.Asset);
        var (b, _) = Program.ParseMaterialSetArgs("42 all none");
        Assert.Null(b!.Faces); Assert.Equal(UUID.Zero, b.Asset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("42 0")]
    [InlineData("x 0 none")]
    [InlineData("42 8 none")]
    [InlineData("42 -1 none")]
    [InlineData("42 0 notauuid")]
    [InlineData("42 0 00000000-0000-0000-0000-000000000000")]
    public void Rejects_bad_input(string s) => Assert.NotNull(Program.ParseMaterialSetArgs(s).err);

    [Fact]
    public void Payload_has_one_entry_per_face_with_viewer_keys()
    {
        var obj = UUID.Random();
        var arr = Program.MaterialSetPayload(obj, new[] { 0, 2 }, Chrome);
        Assert.Equal(2, arr.Count);
        var m = (OSDMap)arr[1];
        Assert.Equal(obj, m["object_id"].AsUUID()); Assert.Equal(2, m["side"].AsInteger());
        Assert.Equal(Chrome, m["asset_id"].AsUUID()); Assert.Equal("", m["gltf_json"].AsString());
    }

    [Fact]
    public void Formats_per_face_ids()
    {
        var rm = new Dictionary<int, UUID> { [1] = Chrome };
        Assert.Equal($"0:none 1:{Chrome} 2:none", Program.FormatRenderMaterials(rm, 3));
        Assert.Equal("0:none", Program.FormatRenderMaterials(null, 1));
    }
}

public class MaterialUndoTests
{
    [Fact]
    public void Restore_payload_puts_each_face_back_to_its_own_id()
    {
        var obj = UUID.Random(); var a = UUID.Random();
        var arr = Program.MaterialRestorePayload(obj, new Dictionary<int, UUID> { [3] = UUID.Zero, [1] = a });
        Assert.Equal(2, arr.Count);
        Assert.Equal(1, ((OSDMap)arr[0])["side"].AsInteger()); Assert.Equal(a, ((OSDMap)arr[0])["asset_id"].AsUUID());
        Assert.Equal(UUID.Zero, ((OSDMap)arr[1])["asset_id"].AsUUID());
    }
}

public class MaterialBaseTexTests
{
    [Fact]
    public void Basetex_option_puts_a_base_colour_override_in_gltf_json()
    {
        var tex = new UUID("07a908ef-ec37-d0d3-e061-8510eefab89b");
        var (a, err) = Program.ParseMaterialSetArgs("42 0 adfa8a26-bc47-f5e6-1cd1-586eb1fead26 basetex=07a908ef-ec37-d0d3-e061-8510eefab89b");
        Assert.Null(err); Assert.Equal(tex, a!.BaseTex);
        var j = ((OSDMap)Program.MaterialSetPayload(UUID.Random(), new[] { 0 }, a.Asset, a.BaseTex)[0])["gltf_json"].AsString();
        var doc = (OSDMap)OSDParser.DeserializeJson(j);
        Assert.Equal(tex.ToString(), ((OSDMap)((OSDArray)doc["images"])[0])["uri"].AsString());
        Assert.NotNull(Program.ParseMaterialSetArgs("42 0 none basetex=07a908ef-ec37-d0d3-e061-8510eefab89b").err);
    }
}
