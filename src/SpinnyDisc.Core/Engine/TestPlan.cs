namespace SpinnyDisc.Core.Engine;

public enum TestPhaseKind
{
    Identify,
    SmartBaseline,
    ReadSweep,
    RandomRead,
    IdleBurst,
    WriteVerify,       // destructive — gated on AllowWrites
    HotSpot,           // dense probing around recorded hang locations
    SmartDelta,
}

public sealed record TestPlan
{
    /// <summary>Allow destructive writes (write+verify phase, buffer-fill).</summary>
    public bool AllowWrites { get; init; }

    /// <summary>
    /// Explicit opt-in required before destructive tests run on the disk hosting the
    /// system volume. On a Windows To Go bench the *test* drive often IS disk 0 while
    /// the boot drive is the To Go stick — that case needs no override since
    /// IsSystemDisk follows the boot volume, not the disk number. This flag is only
    /// for deliberately testing the very drive you booted from.
    /// </summary>
    public bool AllowSystemDisk { get; init; }

    /// <summary>Read/write block granularity for sweeps. Default 1 MiB.</summary>
    public int ChunkBytes { get; init; } = 1 << 20;

    /// <summary>Per-command timeout — beyond this the I/O counts as a hang and we reset.</summary>
    public int HangTimeoutMs { get; init; } = 30_000;

    /// <summary>Commands slower than this are logged as stalls (the "OS hiccup" symptom).</summary>
    public int StallThresholdMs { get; init; } = 5_000;

    /// <summary>Give up on the drive after this many resets.</summary>
    public int MaxResets { get; init; } = 10;

    /// <summary>Give up after this many hangs even if resets keep working.</summary>
    public int MaxHangs { get; init; } = 25;

    /// <summary>When a hang is confirmed at an LBA, skip this far around it during sweeps (bytes).</summary>
    public long SkipRadiusBytes { get; init; } = 16L << 20;

    // Phase parameters
    /// <summary>Sweep advances len*SweepStride each read — 4 = every 4th chunk (~25% surface, quick triage).</summary>
    public int SweepStride { get; init; } = 1;
    public int RandomReadOps { get; init; } = 4096;
    public int RandomBlockBytes { get; init; } = 128 * 1024;
    public int IdleSeconds { get; init; } = 30;
    public int IdleRounds { get; init; } = 3;
    public int IdleBurstOps { get; init; } = 64;
    public int HotSpotRadiusBlocks { get; init; } = 64;   // in ChunkBytes units around each hang
    public uint WriteSeed { get; init; } = 0x51D1EC01;

    /// <summary>How long to wait for the drive to come back after a reset.</summary>
    public int ResetWaitMs { get; init; } = 60_000;

    public IReadOnlyList<TestPhaseKind> Phases =>
        AllowWrites
            ? [TestPhaseKind.Identify, TestPhaseKind.SmartBaseline, TestPhaseKind.ReadSweep,
               TestPhaseKind.RandomRead, TestPhaseKind.IdleBurst, TestPhaseKind.WriteVerify,
               TestPhaseKind.HotSpot, TestPhaseKind.SmartDelta]
            : [TestPhaseKind.Identify, TestPhaseKind.SmartBaseline, TestPhaseKind.ReadSweep,
               TestPhaseKind.RandomRead, TestPhaseKind.IdleBurst,
               TestPhaseKind.HotSpot, TestPhaseKind.SmartDelta];
}
