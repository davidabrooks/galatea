using Xunit;

/// <summary>2026-11-23: test classes that change process-wide state (environment variables, Program.*Override hooks,
/// static fields such as Program.LoginFailedReason, the running roam) run one at a time in this collection and never
/// alongside another class, so a parallel test can never read a half-set value.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessStateCollection
{
    public const string Name = "Process-wide state (not parallel)";
}
