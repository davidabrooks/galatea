using System.Collections.Generic;
using System.IO;
using System.Linq;
using GalatayText;
using LibreMetaverse;
using Xunit;
// 2026-10-26 13:20 live test after #316: `idle_pose now groundsit` right after login. The collar (worn at the chin) started 947dd903 / dccb493a / 201d84aa / c9574fd9 during the
// pose entry; they were learned as "AO sit animations", so a STAND counted as "AO sit playing": the hover check ran as `sit VAMOSITG12`, both renders showed her standing, and it
// POSTed hover -0.229. Now: those animations are never sits, and a hover fix is only computed from a render that shows the pose the client believes.
public class PoseRenderGuardTests
{
    static string Root() { var d = new DirectoryInfo(System.AppContext.BaseDirectory); while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent; return d!.FullName; }
    static string Src(string f) => File.ReadAllText(Path.Combine(Root(), "textclient/galatay-text", f));
    static UUID U(string s) => UUID.Parse(s);
    // the real ids of the 13:20 export (me.anims) and of the ground sit that rendered correctly at 13:08
    const string BadExportAnims = "2408fe9e-df1d-1d7d-f4ff-1384fa7b350f@15.2,947dd903-cd66-158b-13ae-79a72ee7c947@15.2,dccb493a-93f0-0903-041e-6a7469846d11@15.2,201d84aa-63e9-5cb0-ac55-6c6c4ddd334e@15.1,be2dba76-6004-28cb-29e4-68f73d54f13b@15.1,c5a6a338-d83a-5ec7-c32f-6a992ebfb4ef@15.1,c9574fd9-e482-d213-ac65-9d5c9b4e33cb@11.3";
    const string GoodSitAnims = "947dd903-cd66-158b-13ae-79a72ee7c947@1163.2,dccb493a-93f0-0903-041e-6a7469846d11@1163.2,201d84aa-63e9-5cb0-ac55-6c6c4ddd334e@1163.2,c9574fd9-e482-d213-ac65-9d5c9b4e33cb@245.2,1a2bd58e-87ff-0df8-0b4c-53e047b0bb6e@22.8,af45c937-269b-15d7-582f-2d5813394311@5.4";
    static readonly UUID[] TrueSits = { U("2941ee1d-94c1-a56f-0d90-fe0e2e0918dc"), U("7c8e8863-ab9a-efea-4d64-47a0fa85cdc1"), U("af45c937-269b-15d7-582f-2d5813394311"), U("e00411f6-4103-7a8b-1ffd-762a5468d3d5"), U("e27ed639-0d3f-3bdc-08eb-17313b30a543"), U("f07f8e1e-ddd1-cbcd-e85f-b344ca5496c2") };
    static readonly UUID[] Contaminated = TrueSits.Concat(Program.AoNotSitSeed).ToArray();   // the sit list the old build had saved after 13:20:17
    const double Size = 1.566;

    // ---- the cause: the collar's animations are never AO sits ----
    [Fact] public void CollarAnimations_AreSeededAsNeverSit()
    {
        Assert.Equal(4, Program.AoNotSitSeed.Length);
        foreach (var id in new[] { "947dd903-cd66-158b-13ae-79a72ee7c947", "dccb493a-93f0-0903-041e-6a7469846d11", "c9574fd9-e482-d213-ac65-9d5c9b4e33cb", "201d84aa-63e9-5cb0-ac55-6c6c4ddd334e" }) Assert.Contains(U(id), Program.AoNotSitSeed);
    }
    [Fact] public void AoSitLearnable_NeverLearnsAnOtherAttachmentsAnimation()
    {
        var added = new[] { "947dd903-cd66-158b-13ae-79a72ee7c947", "4943960c-f6e4-2c24-1637-cd850cb49199", "af45c937-269b-15d7-582f-2d5813394311" };
        var got = Program.AoSitLearnable(added, new UUID[0], new[] { U("4943960c-f6e4-2c24-1637-cd850cb49199") }, Program.AoNotSitSeed);
        Assert.Equal(new[] { U("af45c937-269b-15d7-582f-2d5813394311") }, got);                  // the collar one and the known AO stand are out, the real sit is in
        Assert.Equal(3, Program.AoSitLearnable(added, new UUID[0], new UUID[0]).Count);          // without the lists nothing is excluded (the old behaviour, AoSitTests)
    }
    [Fact] public void ClassLoad_DropsCollarIdsFromTheSavedSitList_AndEveryCallerChecksTheSource()
    {
        var ao = Src("AoSit.cs");
        Assert.Contains("aoSitIds.ExceptWith(aoOtherIds);", ao);                                 // an older build's saved list is cleaned on load
        Assert.Contains("foreach (var u in AoNotSitSeed) aoOtherIds.Add(u);", ao);
        Assert.Contains("IsOwnNonAoAttachment(e.src)", ao);                                       // AoLearnSits: the animation's source is checked
        Assert.Contains("IsOwnNonAoAttachment(src)) { AoNoteOther(id); return; }", ao);          // AoNoteStarted: an animation started by the collar is recorded
        Assert.Contains("[\"other\"] =", ao);                                                     // persisted
        Assert.Contains("AoSitLearnable(added, before, aoStandIds, aoOtherIds)", ao);
        var aw = Src("AttachWatch.cs"); Assert.Contains("p.ParentID == client.Self.LocalID && !IsAoHudSource(src)", aw);
    }
    [Fact] public void StandingWithCollarAnimations_IsNotAnAoSit_OnceTheListIsClean()
    {
        var playing = Program.ExportAnimIds(BadExportAnims);
        Assert.False(Program.AoSitPlaying(playing, false, TrueSits));                             // the 13:20 stand
        Assert.True(Program.AoSitPlaying(playing, false, Contaminated));                          // what the old build believed
        Assert.True(Program.AoSitPlaying(Program.ExportAnimIds(GoodSitAnims), false, TrueSits));  // the 13:08 ground sit is still a sit (af45c937)
    }

    // ---- the guard: a fix is only computed from a render that shows the believed pose ----
    [Fact] public void TheRealThirteenTwentyCase_IsRefusedEvenWithTheContaminatedList()
    {
        var ex = Program.ExportAnimIds(BadExportAnims);
        Assert.Contains("no AO sit animation", Program.PoseRenderMismatch("sit", Size, 1.0917479, ex, TrueSits));        // clean list: the anims say stand
        Assert.Contains("standing, not sitting", Program.PoseRenderMismatch("sit", Size, 1.0917479, ex, Contaminated)); // contaminated list: the pelvis (1.09 m) still says stand
        Assert.Contains("no AO sit animation", Program.PoseRenderMismatch("sit", Size, null, ex, TrueSits));            // no mesh.json: the animations alone refuse
    }
    [Fact] public void ARealGroundSit_AndARealStand_PassTheGuard()
    {
        Assert.Null(Program.PoseRenderMismatch("sit", Size, 0.10209572, Program.ExportAnimIds(GoodSitAnims), TrueSits));      // 13:08 VAMOSITG26, pelvis 0.10 m
        Assert.Null(Program.PoseRenderMismatch("stand", Size, 1.0803777, Program.ExportAnimIds(BadExportAnims), TrueSits));   // a stand (12:19 export pelvis 1.08 m)
        Assert.Null(Program.PoseRenderMismatch("sit", Size, null, Program.ExportAnimIds(GoodSitAnims), TrueSits));            // unknown pelvis: the animations decide
    }
    [Fact] public void AStandCheck_IsRefusedOnASittingRender_AndOnASitAnimation()
    {
        Assert.Contains("sitting, not standing", Program.PoseRenderMismatch("stand", Size, 0.10, Program.ExportAnimIds(BadExportAnims), TrueSits));
        Assert.Contains("AO sit animation", Program.PoseRenderMismatch("stand", Size, 1.08, Program.ExportAnimIds(GoodSitAnims), TrueSits));
    }
    [Theory] [InlineData("sit", 0.70, true)] [InlineData("sit", 0.71, false)] [InlineData("stand", 0.87, true)] [InlineData("stand", 0.85, false)]
    public void PelvisBounds_AreFractionsOfHerHeight(string kind, double pelvis, bool ok)
    {
        var anims = kind == "sit" ? Program.ExportAnimIds(GoodSitAnims) : Program.ExportAnimIds(BadExportAnims);
        Assert.Equal(ok, Program.PoseRenderMismatch(kind, Size, pelvis, anims, TrueSits) == null);
    }
    [Fact] public void MeshPelvisZ_ReadsTheExportsMeshJson()
    {
        Assert.Equal(0.10209572, Program.MeshPelvisZ("{\"pelvis\":[-0.001,0.006,0.10209572],\"head\":[0,0,1]}")!.Value, 6);
        Assert.Null(Program.MeshPelvisZ("{\"pelvis\":null}")); Assert.Null(Program.MeshPelvisZ("{}")); Assert.Null(Program.MeshPelvisZ("not json")); Assert.Null(Program.MeshPelvisZ("{\"pelvis\":[1,2]}"));
        Assert.Empty(Program.ExportAnimIds(null)); Assert.Equal(2, Program.ExportAnimIds("a@1,947dd903-cd66-158b-13ae-79a72ee7c947@2,201d84aa-63e9-5cb0-ac55-6c6c4ddd334e@3").Count);
    }

    // ---- a large fix needs a second consistent render ----
    [Fact] public void LargeFixes_NeedConfirmation_SmallOnesDoNot()
    {
        Assert.True(Program.HoverFixNeedsConfirm(0.229)); Assert.True(Program.HoverFixNeedsConfirm(-0.13)); Assert.False(Program.HoverFixNeedsConfirm(0.12)); Assert.False(Program.HoverFixNeedsConfirm(0.098)); Assert.False(Program.HoverFixNeedsConfirm(-0.069));
        Assert.True(Program.HoverFixConfirmed(0.229, 0.2315)); Assert.False(Program.HoverFixConfirmed(0.229, 0.0));
        Assert.Equal(3, Program.HoverCheckRenderCount(true, true)); Assert.Equal(2, Program.HoverCheckRenderCount(false, true)); Assert.Equal(2, Program.HoverCheckRenderCount(true)); Assert.Equal(1, Program.HoverCheckRenderCount(false));
    }
    [Fact] public void TheCheck_RefusesBeforeAnyPost_AndAsksForARetry()
    {
        var ph = Src("PoseHover.cs"); var chk = ph[ph.IndexOf("static async Task<string> PoseHoverCheck(")..];
        int guard = chk.IndexOf("PoseRenderMismatch(kind, size, pz, exAnims, AoSitIdsNow())"), post = chk.IndexOf("await PostHover(");
        Assert.True(guard > 0 && guard < post);                                                   // the guard runs on render 1, before anything can be POSTed
        var g = chk[guard..post]; Assert.Contains("not verified: pose not rendered", g); Assert.Contains("nothing changed", g); Assert.Contains("return \"pose not rendered: \" + why;", g);   // a non-null return = the request retries
        Assert.Contains("HoverFixNeedsConfirm(eg)", chk[guard..post]);
        Assert.True(chk.IndexOf("HoverFixNeedsConfirm(eg)") < post);
    }
}
