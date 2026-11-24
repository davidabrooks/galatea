// OutfitJob.cs (2026-11-23, David via dr eggbot, 5:05 PM PT): the MCP outfit_wear call blocked ~55 s for 'Android' (bedroom walk
// + ~30-item swap) and the host timed it out (-32001) although the swap finished fine at 17:04:16 (collar Stealth / ears until
// 17:04:24). outfit_wear now STARTS a job and returns at once ('outfit start <name> [force]'); the job runs the very same
// 'outfit wear' command (bedroom walk, safe swap, clothing HUDs), then waits for the post-swap steps (EvoX ears, Android collar
// Stealth: CollarStealth.cs outfitPostSteps, capped). Progress and the result are in 'status' (outfit_change) and in one
// [outfit-job] log line per state (poll_events). A second start while a job runs is refused and names the running job; a
// plain 'outfit wear' (CLI, routines) while a job runs is refused too, so two swaps never overlap.
namespace GalatayText;

public static partial class Program
{
    internal sealed record OutfitJobState(int Id, string Name, string State, DateTime Started, DateTime? Finished, string Summary)
    {
        public bool Running => State is "starting" or "walking" or "swapping" or "post-steps";
        public object ToStatus() => new
        {
            id = Id, name = Name, state = State, started = Started.ToString("yyyy-MM-dd HH:mm:ss"),
            finished = Finished?.ToString("yyyy-MM-dd HH:mm:ss"), seconds = Math.Round(((Finished ?? DateTime.Now) - Started).TotalSeconds, 1), summary = Summary
        };
    }
    static volatile OutfitJobState outfitJob;
    static int outfitJobSeq;
    static readonly object outfitJobLock = new();
    static readonly AsyncLocal<bool> outfitJobSelf = new();
    internal const int OutfitPostStepsCapS = 90;

    /// <summary>Pure (tested): did the 'outfit wear' report fail? Same words the MCP layer's ok flag uses.</summary>
    internal static bool OutfitJobFailed(string r) =>
        string.IsNullOrWhiteSpace(r) || r.StartsWith("not logged in") || r.StartsWith("usage") || r.StartsWith("error") || r.StartsWith("could not")
        || r.StartsWith("unknown") || r.StartsWith("no outfit") || r.StartsWith("refused", StringComparison.OrdinalIgnoreCase) || r.StartsWith("FAILED")
        || r.Contains("ABORT") || r.Contains("failed");

    /// <summary>Pure (tested): the one-line summary of a long report (first line, at most 300 chars).</summary>
    internal static string OutfitJobSummary(string r)
    {
        var lines = (r ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var s = string.Join(" | ", lines.Where(l => l.StartsWith("wore ") || l.StartsWith("no outfit") || l.Contains("failed") || l.Contains("ABORT") || l.StartsWith("bedroom")).Take(3));
        if (s.Length == 0) s = lines.FirstOrDefault() ?? "";
        return s.Length > 300 ? s[..300] + "…" : s;
    }

    /// <summary>Pure (tested): may a new job start? null = yes, else the refusal naming the running job.</summary>
    internal static string OutfitJobStartBlock(OutfitJobState running, bool otherChangeActive) =>
        running is { Running: true } j ? $"refused: outfit change '{j.Name}' (job {j.Id}) is still running ({j.State}, started {j.Started:HH:mm:ss}); see status outfit_change"
        : otherChangeActive ? "refused: another outfit change is running right now; try again in a minute"
        : null;

    static void OutfitJobSet(OutfitJobState s, bool log = true)
    {
        outfitJob = s;
        if (log) Log("outfit-job", $"job {s.Id} '{s.Name}': {s.State}{(s.Summary != null ? " - " + s.Summary : "")}");
    }

    /// <summary>'outfit start &lt;name&gt; [force]': start the change and return at once.</summary>
    static string OutfitJobStart(string[] nameArgs)
    {
        var args = nameArgs.Where(x => x.Length > 0).ToArray();
        bool force = args.Any(x => x.Equals("force", StringComparison.OrdinalIgnoreCase));
        var name = string.Join(' ', args.Where(x => !x.Equals("force", StringComparison.OrdinalIgnoreCase)));
        if (name.Length == 0) return "usage: outfit start <name> [force]";
        if (!LoggedIn) return "not logged in";
        OutfitJobState job;
        lock (outfitJobLock)
        {
            var block = OutfitJobStartBlock(outfitJob, OutfitChangeActive);
            if (block != null) return block;
            job = new OutfitJobState(++outfitJobSeq, name, "starting", DateTime.Now, null, null);
            OutfitJobSet(job);
        }
        _ = Task.Run(async () =>
        {
            outfitJobSelf.Value = true;
            var j = job;
            try
            {
                if (NeedsBedroomWalk(client.Network.CurrentSim?.Name, client.Self.SimPosition, force)) OutfitJobSet(j = j with { State = "walking" });
                var pre = await ChangeInBedroomFirst("outfit " + name, force);
                OutfitJobSet(j = j with { State = "swapping" });
                NoteManualOutfitDuringLogin(name);
                var r = await OutfitCmd(new[] { "wear" }.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray());
                var report = (pre == null ? "" : pre + "\n") + r;
                if (OutfitJobFailed(r)) { OutfitJobSet(j with { State = "failed", Finished = DateTime.Now, Summary = OutfitJobSummary(report) }); return; }
                OutfitJobSet(j = j with { State = "post-steps", Summary = OutfitJobSummary(report) });
                var post = outfitPostSteps;
                bool settled = await Task.WhenAny(post, Task.Delay(OutfitPostStepsCapS * 1000)) == post;
                OutfitJobSet(j with { State = "done", Finished = DateTime.Now,
                    Summary = OutfitJobSummary(report) + (settled ? "; ears/collar steps finished" : $"; ears/collar steps still running after {OutfitPostStepsCapS} s (see [collar] / [login-veil] lines)") });
            }
            catch (Exception ex) { OutfitJobSet(j with { State = "failed", Finished = DateTime.Now, Summary = "error: " + ex.GetBaseException().Message }); }
        });
        return $"started outfit change '{name}' (job {job.Id}, {job.Started:HH:mm:ss}); progress and result in status outfit_change and [outfit-job] events";
    }

    /// <summary>A plain 'outfit wear' while a job runs (not from the job itself): the refusal, else null.</summary>
    static string OutfitWearBlockedByJob() => outfitJobSelf.Value ? null : (outfitJob is { Running: true } j ? OutfitJobStartBlock(j, false) : null);

    internal static object OutfitJobStatus() => outfitJob?.ToStatus();
}
