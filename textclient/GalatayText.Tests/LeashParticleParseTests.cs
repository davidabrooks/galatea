using System;
using System.IO;
using LibreMetaverse;
using Xunit;

// 2026-10-21 regression: compressed ObjectUpdates passed the WHOLE remaining buffer to ParticleSystem, so the
// size check took the "extended" path and every leash chain parsed as empty (leash-diag never logged).
public class LeashParticleParseTests
{
    static byte[] Legacy(UUID target)
    {
        var ps = new Primitive.ParticleSystem { CRC = 42, Target = target, PartMaxAge = 1f, MaxAge = 0f, PartStartScaleX = 0.1f, PartStartScaleY = 0.1f };
        var b = ps.GetBytes();   // extended: [u32 sysSize][68 sys][u32 dataSize][18 data][glow/blend]
        var l = new byte[86]; Buffer.BlockCopy(b, 4, l, 0, 68); Buffer.BlockCopy(b, 76, l, 68, 18); return l;
    }
    [Fact] public void ExactLegacyBlockKeepsTarget()
    {
        var t = UUID.Random(); var buf = new byte[200]; Buffer.BlockCopy(Legacy(t), 0, buf, 10, 86);
        var exact = new byte[86]; Buffer.BlockCopy(buf, 10, exact, 0, 86);
        var ps = new Primitive.ParticleSystem(exact, 0);
        Assert.Equal(t, ps.Target); Assert.Equal(42u, ps.CRC);
    }
    // LibreMetaverse 3.3.1 (upstream #205, 19537bc0/adbd82af) decodes the compressed update's block with an explicit
    // 86-byte length; this replaces libremetaverse-compressed-particles.patch, so guard the library behaviour itself.
    [Fact] public void LibraryDecodesLegacyBlockInsideLargerBuffer()
    {
        var t = UUID.Random(); var buf = new byte[200]; Buffer.BlockCopy(Legacy(t), 0, buf, 10, 86);
        var ps = new Primitive.ParticleSystem(buf, 10, 86);
        Assert.Equal(t, ps.Target); Assert.Equal(42u, ps.CRC);
    }
}

public class LeashPullTests
{
    static readonly UUID David = UUID.Random(), Other = UUID.Random();
    static readonly (UUID, Vector3)[] Avs = { (David, new Vector3(110, 100, 20)), (Other, new Vector3(100, 110, 20)) };
    [Fact] public void PulledTowardHolder() => Assert.Equal(David, GalatayText.Program.LeashPullToward(new Vector3(100, 100, 20), new Vector3(1, 0.05f, 0), Avs, false));
    [Fact] public void OwnWalkIsNoPull() => Assert.Null(GalatayText.Program.LeashPullToward(new Vector3(100, 100, 20), new Vector3(1, 0, 0), Avs, true));
    [Fact] public void StandingStillIsNoPull() => Assert.Null(GalatayText.Program.LeashPullToward(new Vector3(100, 100, 20), new Vector3(0.1f, 0, 0), Avs, false));
    [Fact] public void InvisibleLeashKeepsLeashWhilePulling()
    {
        var now = DateTime.Now;
        Assert.False(GalatayText.Program.LeashChainLossEnds(now, now.AddSeconds(-5)));   // invisible chain, still pulling
        Assert.True(GalatayText.Program.LeashChainLossEnds(now, now.AddMinutes(-5)));    // visible chain gone, no pull = unleashed
    }
}
