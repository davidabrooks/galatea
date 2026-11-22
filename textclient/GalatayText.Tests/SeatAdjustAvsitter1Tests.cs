using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GalatayText;
using Xunit;

// 2026-11-22 (fixes list item 7, Nov 20 11:15:22 PT): on the Nammos Beach boat (AVsitter 1.29.u2) the Personal Adjust menu did open
// (X-/Y-/Z-/X+/Y+/Z+/POS/0.05m/[SAVE]/[DEFAULT]), but its mode button says "POS" (AVsitter 2: "Position"). The check read it as
// rotation mode, pressed a "Rotation" button that is not there, and logged "no adjust controls I can find".
public class SeatAdjustAvsitter1Tests
{
    static readonly List<string> Nammos = new() { "X-", "Y-", "Z-", "X+", "Y+", "Z+", "POS", " - ", "0.05m", "[BACK]", "[SAVE]", "[DEFAULT]" };

    [Fact] public void Nammos_PersonalAdjust_IsReadAsPositionModeWithAStep()
    {
        Assert.Equal(SeatAdjust.Menu.Personal, SeatAdjust.Classify("Personal Adjust:", Nammos));
        Assert.Equal((true, (double?)0.05), SeatAdjust.PersonalState(Nammos));
        Assert.Null(SeatAdjust.ModeToggle(Nammos));   // already in position mode: nothing to switch
    }

    [Fact] public void RotationMode_IsSwitchedWithTheButtonThatMenuHas()
    {
        var rot1 = Nammos.Select(x => x == "POS" ? "ROT" : x).ToList();
        Assert.False(SeatAdjust.PersonalState(rot1).position);
        Assert.Equal("ROT", SeatAdjust.ModeToggle(rot1));
        var rot2 = new List<string> { "[BACK]", "Rotation", "5°", "[SAVE]", "X+", "Y+", "Z+", "X-", "Y-", "Z-" };
        Assert.Equal("Rotation", SeatAdjust.ModeToggle(rot2));
        Assert.Null(SeatAdjust.ModeToggle(new List<string> { "X+", "Z+" }));
    }

    // simulate a menu whose step button cycles in a given order; drive it the way SeatAdjustDriveZ does
    static double Drive(double dz, double start, double[] cycle, out int presses)
    {
        double st = start, moved = 0; presses = 0;
        for (string p; presses < SeatAdjust.MaxZPresses && (p = SeatAdjust.NextZPress(dz - moved, st)) != null; presses++)
        {
            if (p is "Z+" or "Z-") moved += p == "Z+" ? st : -st;
            else st = cycle[(Array.IndexOf(cycle, st) + 1) % cycle.Length];   // what the furniture shows next, not what the plan assumed
        }
        return moved;
    }

    [Theory]
    [InlineData(0.23)] [InlineData(-0.08)] [InlineData(0.31)] [InlineData(-0.24)] [InlineData(0.01)]
    public void ZMoves_AreExact_WhateverOrderTheStepButtonCycles(double dz)
    {
        foreach (var cycle in new[] { new[] { 0.05, 0.25, 0.01 }, new[] { 0.05, 0.01, 0.25 }, new[] { 0.01, 0.05, 0.25 } })
        foreach (var start in cycle)
        {
            var moved = Drive(dz, start, cycle, out var n);
            Assert.True(Math.Abs(moved - dz) < 1e-6, $"dz {dz} cycle {string.Join(">", cycle)} start {start}: moved {moved}");
            Assert.True(n < SeatAdjust.MaxZPresses);
        }
    }

    [Fact] public void TheDriverReReadsTheStep_AndBothCallersUseIt()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "textclient/galatay-text"))) d = d.Parent;
        var run = File.ReadAllText(Path.Combine(d!.FullName, "textclient/galatay-text/SeatAdjustRun.cs"));
        Assert.Contains("SeatAdjust.ModeToggle(d.ButtonLabels) is { } tg ? await SeatAdjustPress(seat, d, tg, ct) : null", run);
        Assert.DoesNotContain("SeatAdjustPress(seat, d, \"Rotation\", ct)", run);
        Assert.Contains("=> (await SeatAdjustDriveZ(seat, d, dz, ct)).d;", run);
        Assert.Contains("else if (SeatAdjust.PersonalState(d.ButtonLabels).step is double ns) st = ns;", run);
    }
}
