namespace SpinnyDisc.Core.Engine;

public enum EventKind
{
    TestStart, TestEnd,
    PhaseStart, PhaseEnd,
    IoError,          // command completed with a Win32/storage error
    Stall,            // completed but over the stall threshold
    Hang,             // never completed inside the timeout
    ResetAttempt, ResetOk, ResetFailed,
    VerifyMismatch,   // read-back data != written data (silent corruption)
    SmartSnapshot,
    IdentifyInfo,
    Note,
    Aborted,
}

public sealed record TestEvent
{
    public DateTimeOffset Ts { get; init; } = DateTimeOffset.UtcNow;
    public required EventKind Kind { get; init; }
    public string Phase { get; init; } = "";
    public long Offset { get; init; } = -1;
    public long Length { get; init; }
    public double LatencyMs { get; init; }
    public int Win32Error { get; init; }
    public string Detail { get; init; } = "";
    public Dictionary<string, object>? Extra { get; init; }

    public long Lba(uint sectorSize) => Offset >= 0 && sectorSize > 0 ? Offset / sectorSize : -1;
}

/// <summary>Progress ping for UI — emitted separately from the event stream.</summary>
public sealed record PhaseProgress(
    TestPhaseKind Phase,
    double Fraction,        // 0..1 within the phase
    long BytesDone,
    long BytesTotal,
    string Status);

public interface ITestSink
{
    void Emit(TestEvent e);
    void Progress(PhaseProgress p);
}

/// <summary>Sink that does nothing — useful for tests.</summary>
public sealed class NullSink : ITestSink
{
    public void Emit(TestEvent e) { }
    public void Progress(PhaseProgress p) { }
}
