namespace AiDownloadManager.Core;

/// <summary>
/// Pure functions for byte-range planning. No I/O, no state; every
/// function is covered by unit tests.
/// </summary>
public static class SegmentPlan
{
    /// <summary>
    /// Splits [0, totalBytes) into at most <paramref name="maxConnections"/>
    /// contiguous ranges, each at least <paramref name="minSegmentBytes"/>
    /// long (the last range may be shorter). Ranges are contiguous,
    /// non-overlapping, and cover the whole file exactly.
    /// </summary>
    public static IReadOnlyList<ByteRange> CreateInitialRanges(
        long totalBytes, int maxConnections, long minSegmentBytes)
    {
        if (totalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(totalBytes));
        if (maxConnections <= 0) throw new ArgumentOutOfRangeException(nameof(maxConnections));
        if (minSegmentBytes <= 0) throw new ArgumentOutOfRangeException(nameof(minSegmentBytes));

        var count = (int)Math.Min(maxConnections, Math.Max(1, totalBytes / minSegmentBytes));
        var ranges = new List<ByteRange>(count);
        var baseSize = totalBytes / count;
        var remainder = totalBytes % count;
        var start = 0L;
        for (var i = 0; i < count; i++)
        {
            var size = baseSize + (i < remainder ? 1 : 0);
            ranges.Add(new ByteRange(start, start + size - 1));
            start += size;
        }
        return ranges;
    }

    /// <summary>
    /// Returns the parts of <paramref name="whole"/> not covered by any
    /// range in <paramref name="completed"/>. Completed ranges may overlap
    /// each other and may extend past <paramref name="whole"/>; the result
    /// is sorted, non-overlapping, and clipped to <paramref name="whole"/>.
    /// </summary>
    public static IReadOnlyList<ByteRange> SubtractRanges(
        ByteRange whole, IEnumerable<ByteRange> completed)
    {
        var clipped = completed
            .Select(r => new ByteRange(Math.Max(r.Start, whole.Start), Math.Min(r.End, whole.End)))
            .Where(r => r.Start <= r.End)
            .OrderBy(r => r.Start)
            .ToList();

        var remaining = new List<ByteRange>();
        var cursor = whole.Start;
        foreach (var range in clipped)
        {
            if (range.Start > cursor)
                remaining.Add(new ByteRange(cursor, range.Start - 1));
            cursor = Math.Max(cursor, range.End + 1);
            if (cursor > whole.End)
                break;
        }
        if (cursor <= whole.End)
            remaining.Add(new ByteRange(cursor, whole.End));
        return remaining;
    }

    /// <summary>
    /// Splits the largest range in <paramref name="ranges"/> in half and
    /// returns both halves plus the other ranges. Used for dynamic
    /// rebalancing: an idle worker takes the second half of the largest
    /// remaining range. Returns null when no range is large enough to split.
    /// </summary>
    public static (ByteRange First, ByteRange Second)? SplitLargest(
        IReadOnlyList<ByteRange> ranges, long minSplitBytes)
    {
        var largest = ranges
            .Where(r => r.Length >= 2 * minSplitBytes)
            .OrderByDescending(r => r.Length)
            .FirstOrDefault();
        if (largest is null)
            return null;

        var mid = largest.Start + (largest.Length / 2);
        return (new ByteRange(largest.Start, mid - 1), new ByteRange(mid, largest.End));
    }

    /// <summary>Merges overlapping or adjacent ranges into a minimal sorted set.</summary>
    public static IReadOnlyList<ByteRange> MergeRanges(IEnumerable<ByteRange> ranges)
    {
        var sorted = ranges
            .Where(r => r.Start <= r.End)
            .OrderBy(r => r.Start)
            .ToList();
        var merged = new List<ByteRange>();
        foreach (var range in sorted)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End + 1)
            {
                var last = merged[^1];
                merged[^1] = new ByteRange(last.Start, Math.Max(last.End, range.End));
            }
            else
            {
                merged.Add(range);
            }
        }
        return merged;
    }
}
