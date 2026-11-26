namespace SpinnyDisc.Core.Engine;

public sealed class IoStats
{
    public long OpsCompleted { get; set; }
    public long BytesRead { get; set; }
    public long BytesWritten { get; set; }
    public int Hangs { get; set; }
    public int Stalls { get; set; }
    public int IoErrors { get; set; }
    public int Resets { get; set; }
    public int Mismatches { get; set; }
    public int RetriedOk { get; set; }
    public long SkippedBytes { get; set; }

    private const int MaxSamples = 131_072;
    private readonly List<double> _lat = new(MaxSamples);
    private long _seen;

    public void SampleLatency(double ms)
    {
        _seen++;
        if (_lat.Count < MaxSamples) { _lat.Add(ms); return; }
        // reservoir sampling
        int j = Random.Shared.Next((int)Math.Min(_seen, int.MaxValue));
        if (j < MaxSamples) _lat[j] = ms;
    }

    public (double p50, double p95, double p99, double max) Percentiles()
    {
        if (_lat.Count == 0) return (0, 0, 0, 0);
        var s = _lat.ToArray();
        Array.Sort(s);
        double Q(double p) => s[Math.Min(s.Length - 1, (int)(p * s.Length))];
        return (Q(0.50), Q(0.95), Q(0.99), s[^1]);
    }
}
