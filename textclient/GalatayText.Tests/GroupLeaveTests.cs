using System.Linq;
using GalatayText;
using LibreMetaverse;
using Xunit;
// 2026-10-29 (David): leave ONE group (the Buddha Center) by exact name or uuid; never a fuzzy or ambiguous match
public class GroupLeaveTests
{
    static readonly UUID A = UUID.Random(), B = UUID.Random(), C = UUID.Random();
    static readonly (UUID, string)[] G = { (A, "The Buddha Center"), (B, "Peronaut Residents"), (C, "Twin"), (UUID.Random(), "twin") };
    [Fact] public void ExactNameOrUuidOnly()
    {
        Assert.Equal(A, Program.GroupLeaveTarget("the buddha center", G).id);
        Assert.Equal(B, Program.GroupLeaveTarget(B.ToString(), G).id);
        Assert.Null(Program.GroupLeaveTarget("Buddha", G).id);          // no partial match
        Assert.Null(Program.GroupLeaveTarget("Twin", G).id);            // ambiguous
        Assert.Null(Program.GroupLeaveTarget(UUID.Random().ToString(), G).id);
        Assert.Null(Program.GroupLeaveTarget("", G).id);
    }
    [Fact] public void McpExposesGroupsListAndLeave()
    {
        var src = System.IO.File.ReadAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, "../../../../", "galatay-mcp/McpProgram.cs"));
        Assert.Contains("Name = \"groups_list\"", src); Assert.Contains("R(\"group list\")", src);
        Assert.Contains("Name = \"group_leave\"", src); Assert.Contains("R(\"group leave \" + Clean(group))", src);
    }
}
