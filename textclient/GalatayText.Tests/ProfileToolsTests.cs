using GalatayText;
using Xunit;
// 2026-10-31 (David): profile photo upload needs confirm + a fee cap; picks deleted only by id after a backup
public class ProfileToolsTests
{
    [Fact] public void UploadNeedsConfirmCapAndJ2c()
    {
        Assert.NotNull(Program.UploadTextureRefusal(false, 40, "/x/a.j2c"));
        Assert.NotNull(Program.UploadTextureRefusal(true, 0, "/x/a.j2c"));
        Assert.NotNull(Program.UploadTextureRefusal(true, 51, "/x/a.j2c"));
        Assert.NotNull(Program.UploadTextureRefusal(true, 40, "/x/a.png"));
        Assert.Null(Program.UploadTextureRefusal(true, 40, "/x/a.j2c"));
    }
    [Fact] public void NewApprovalMarker() => Assert.EndsWith("paid-upload-profile-20261031.done", Program.PaidMarker);
    [Fact] public void McpExposesTools()
    {
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, "../../../../", "galatay-mcp/McpProgram.cs"));
        foreach (var t in new[] { "upload_texture", "set_profile_image", "list_picks", "delete_pick" }) Assert.Contains($"Name = \"{t}\"", src);
        Assert.Contains("Core.UploadTextureRefusal(confirm", src);
    }
}
