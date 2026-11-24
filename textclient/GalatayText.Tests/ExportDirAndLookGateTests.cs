using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalatayText;
using Xunit;

namespace GalatayText.Tests;

/// <summary>
/// 2026-11-23 7:34:47 PM PT: a PoseHover export and `look around far env` started in the same second,
/// shared export-yyyyMMdd-HHmmss, and the look hit IOException on scene.json.tmp (FileShare.None).
/// </summary>
public class ExportDirAndLookGateTests
{
    [Fact]
    public void AllocateExportDir_TwoConcurrentSameSecond_GetDistinctDirs()
    {
        var parent = Path.Combine(Path.GetTempPath(), "exportdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            var now = new DateTime(2026, 11, 23, 19, 34, 47);
            var start = new ManualResetEventSlim(false);
            string a = null!, b = null!;
            var t1 = Task.Run(() => { start.Wait(); a = Program.AllocateExportDir(parent, now); });
            var t2 = Task.Run(() => { start.Wait(); b = Program.AllocateExportDir(parent, now); });
            start.Set();
            Task.WaitAll(t1, t2);
            Assert.NotEqual(a, b);
            Assert.True(Directory.Exists(a));
            Assert.True(Directory.Exists(b));
            var names = new[] { Path.GetFileName(a)!, Path.GetFileName(b)! }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal("export-20261123-193447", names[0]);
            Assert.Equal("export-20261123-193447-2", names[1]);
        }
        finally
        {
            try { Directory.Delete(parent, true); } catch { }
        }
    }

    [Fact]
    public async Task RunWithLookGate_ReleasesGateAfterThrow()
    {
        var gate = new SemaphoreSlim(1, 1);
        var msg = await Program.RunWithLookGate(gate, _ => throw new IOException("The process cannot access the file 'scene.json.tmp' because it is being used by another process."));
        Assert.StartsWith("look failed: The process cannot access the file 'scene.json.tmp'", msg);
        Assert.Equal(1, gate.CurrentCount);
        Assert.True(await gate.WaitAsync(0));
        gate.Release();
    }

    [Fact]
    public async Task RunWithLookGate_StillRenderingPath_HoldsGateUntilReleased()
    {
        var gate = new SemaphoreSlim(1, 1);
        var msg = await Program.RunWithLookGate(gate, keepHeld =>
        {
            keepHeld();
            return Task.FromResult("look: still rendering after 75 s (pid 1); the image path(s) will be logged as [look] in the client log");
        });
        Assert.StartsWith("look: still rendering after 75 s", msg);
        Assert.Equal(0, gate.CurrentCount);
        Assert.False(await gate.WaitAsync(0));
        gate.Release();
        Assert.Equal(1, gate.CurrentCount);
    }
}
