using AiDownloadManager.Core;

namespace AiDownloadManager.Engine.Tests;

public class SegmentPlanTests
{
    [Fact]
    public void CreateInitialRanges_CoversFileExactlyWithoutOverlapOrGap()
    {
        var ranges = SegmentPlan.CreateInitialRanges(10 * 1024 * 1024, 8, 1024 * 1024);
        Assert.Equal(8, ranges.Count);
        Assert.Equal(0, ranges[0].Start);
        Assert.Equal(10 * 1024 * 1024 - 1, ranges[^1].End);
        Assert.Equal(10 * 1024 * 1024, ranges.Sum(r => r.Length));
        for (var i = 1; i < ranges.Count; i++)
            Assert.Equal(ranges[i - 1].End + 1, ranges[i].Start);
    }

    [Fact]
    public void CreateInitialRanges_SmallFileGetsOneSegment()
    {
        var ranges = SegmentPlan.CreateInitialRanges(512 * 1024, 8, 1024 * 1024);
        var single = Assert.Single(ranges);
        Assert.Equal(new ByteRange(0, 512 * 1024 - 1), single);
    }

    [Fact]
    public void CreateInitialRanges_LimitsSegmentsByMinimumSize()
    {
        // 5 MB file, 1 MB minimum: at most 5 segments even with 32 requested.
        var ranges = SegmentPlan.CreateInitialRanges(5 * 1024 * 1024, 32, 1024 * 1024);
        Assert.Equal(5, ranges.Count);
        Assert.Equal(5 * 1024 * 1024, ranges.Sum(r => r.Length));
    }

    [Fact]
    public void CreateInitialRanges_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SegmentPlan.CreateInitialRanges(0, 8, 1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => SegmentPlan.CreateInitialRanges(100, 0, 1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => SegmentPlan.CreateInitialRanges(100, 8, 0));
    }

    [Fact]
    public void SubtractRanges_RemovesCompletedParts()
    {
        var whole = new ByteRange(0, 999);
        var remaining = SegmentPlan.SubtractRanges(whole, new[]
        {
            new ByteRange(0, 249),
            new ByteRange(500, 749)
        });
        Assert.Equal(new[] { new ByteRange(250, 499), new ByteRange(750, 999) }, remaining);
    }

    [Fact]
    public void SubtractRanges_EmptyWhenEverythingIsComplete()
    {
        var remaining = SegmentPlan.SubtractRanges(new ByteRange(0, 999), new[] { new ByteRange(0, 999) });
        Assert.Empty(remaining);
    }

    [Fact]
    public void SubtractRanges_ToleratesOverlappingAndOutOfBoundsCompletedRanges()
    {
        var remaining = SegmentPlan.SubtractRanges(new ByteRange(0, 999), new[]
        {
            new ByteRange(100, 300),
            new ByteRange(200, 400), // overlaps the previous one
            new ByteRange(900, 5000) // extends past the file
        });
        Assert.Equal(new[] { new ByteRange(0, 99), new ByteRange(401, 899) }, remaining);
    }

    [Fact]
    public void SplitLargest_HalvesTheLargestRange()
    {
        var ranges = new[] { new ByteRange(0, 9_999_999), new ByteRange(10_000_000, 10_999_999) };
        var split = SegmentPlan.SplitLargest(ranges, 1024 * 1024);
        Assert.NotNull(split);
        Assert.Equal(new ByteRange(0, 4_999_999), split!.Value.First);
        Assert.Equal(new ByteRange(5_000_000, 9_999_999), split.Value.Second);
    }

    [Fact]
    public void SplitLargest_ReturnsNullWhenNothingIsLargeEnough()
    {
        var ranges = new[] { new ByteRange(0, 999) };
        Assert.Null(SegmentPlan.SplitLargest(ranges, 1024 * 1024));
    }

    [Fact]
    public void MergeRanges_CoalescesOverlappingAndAdjacentRanges()
    {
        var merged = SegmentPlan.MergeRanges(new[]
        {
            new ByteRange(500, 600),
            new ByteRange(0, 100),
            new ByteRange(90, 200),
            new ByteRange(601, 700) // adjacent to the first
        });
        Assert.Equal(new[] { new ByteRange(0, 200), new ByteRange(500, 700) }, merged);
    }
}
