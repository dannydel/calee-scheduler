using Calee.Scheduler.Internal;

namespace Calee.Scheduler.Tests;

public class VirtualRowHeightIndexTests
{
    [Fact]
    public void Finds_Rows_And_Viewport_Ranges_With_Estimated_Heights()
    {
        var index = new VirtualRowHeightIndex(80);
        index.Reset(1_000);

        Assert.Equal(80_000, index.TotalHeight);
        Assert.Equal(0, index.FindRow(0));
        Assert.Equal(1, index.FindRow(80));
        Assert.Equal(999, index.FindRow(80_000));
        Assert.Equal((8, 15), index.GetRange(800, 160, overscan: 2));
    }

    [Fact]
    public void Updates_Prefix_Sums_For_Variable_Height_Rows()
    {
        var index = new VirtualRowHeightIndex(80);
        index.Reset(4);

        Assert.True(index.Update(1, 160));
        Assert.False(index.Update(1, 160));

        Assert.Equal(320, index.PrefixSum(3));
        Assert.Equal(1, index.FindRow(159));
        Assert.Equal(2, index.FindRow(240));
        Assert.Equal((0, 4), index.GetRange(160, 100, overscan: 1));
    }
}
