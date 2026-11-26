namespace SpinnyDisc.Core.Engine;

public enum BadRegionKind { Hang, VerifyMismatch }

/// <summary>One region of the drive's address space that provoked a failure.</summary>
public sealed record BadRegion(long StartByte, long EndByte, BadRegionKind Kind, int EventCount);

/// <summary>
/// Sorted, coalescing map of byte ranges that caused hangs/mismatches.
/// Clustered regions = dying NAND; scattered = controller/firmware. This map
/// is the single most diagnostic output of the tool.
/// </summary>
public sealed class BadLbaMap
{
    private readonly List<(long start, long end, BadRegionKind kind, int count)> _regions = [];

    public IReadOnlyList<BadRegion> Regions =>
        _regions.Select(r => new BadRegion(r.start, r.end, r.kind, r.count)).ToList();

    /// <summary>Add a region; merges with existing regions that overlap within <paramref name="gap"/> bytes.</summary>
    public void Add(long start, long end, BadRegionKind kind, long gap = 0)
    {
        long s = start, e = end; int count = 1;
        for (int i = _regions.Count - 1; i >= 0; i--)
        {
            var r = _regions[i];
            if (r.end + gap >= s && r.start - gap <= e)
            {
                s = Math.Min(s, r.start);
                e = Math.Max(e, r.end);
                count += r.count;
                _regions.RemoveAt(i);
            }
        }
        int idx = _regions.FindIndex(r => r.start > s);
        if (idx < 0) idx = _regions.Count;
        _regions.Insert(idx, (s, e, kind, count));
    }

    /// <summary>True if [offset, offset+length) intersects a known-bad region inflated by margin.</summary>
    public bool Touches(long offset, long length, long margin = 0)
    {
        long end = offset + length;
        foreach (var r in _regions)
            if (offset <= r.end + margin && end >= r.start - margin)
                return true;
        return false;
    }

    /// <summary>Byte ranges to skip during a sweep: each bad region inflated by skipRadius.</summary>
    public IEnumerable<(long start, long end)> SkipRanges(long skipRadius)
    {
        foreach (var r in _regions)
            yield return (Math.Max(0, r.start - skipRadius), r.end + skipRadius);
    }
}
