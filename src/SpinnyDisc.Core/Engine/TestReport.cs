using SpinnyDisc.Core.Devices;

namespace SpinnyDisc.Core.Engine;

public enum Verdict
{
    Pass,          // clean run
    Degraded,      // stalls or transient I/O errors observed — "OS hang" candidate
    Failed,        // hangs, resets exhausted, or silent corruption
    Aborted,       // cancelled or aborted by policy before verdict was possible
    Inconclusive,  // couldn't even establish baseline
}

public sealed class TestReport
{
    public required DiskDescriptor Disk { get; init; }
    public required TestPlan Plan { get; init; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public Verdict Verdict { get; set; } = Verdict.Inconclusive;
    public string VerdictReason { get; set; } = "";
    public List<TestEvent> Events { get; } = [];
    public BadLbaMap BadMap { get; } = new();
    public SmartSnapshot? SmartBefore { get; set; }
    public SmartSnapshot? SmartAfter { get; set; }
    public IoStats Stats { get; } = new();
}
